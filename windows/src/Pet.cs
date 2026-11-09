// One character on screen: a small always-on-top window with per-pixel transparency that
// walks in the front Chrome window's tab strip (or above the taskbar), hops, falls, dances
// while music plays and dozes when you're away. Same behaviour as the Mac version.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace TabPet
{
    // A sprite sheet: rows idle, walk (facing right), sleep, dance, fall; cut into frames once,
    // then drawn at each monitor's scale on demand (and mirrored for walking left).
    sealed class Sprite : IDisposable
    {
        readonly Dictionary<string, List<Bitmap>> frames = new Dictionary<string, List<Bitmap>>();
        readonly Dictionary<string, double> fps = new Dictionary<string, double>();
        readonly Dictionary<string, IntPtr> cache = new Dictionary<string, IntPtr>();
        public readonly double Width, Height;   // in 96-dpi units, before the size setting
        // How it gets up to a tab strip far above (see Pet.StartHop), from the sheet's "climb":
        // jump, balloon, fly, steps or poof (the default).
        public string Climb { get; private set; } = "poof";
        static readonly HashSet<string> ClimbStyles = new HashSet<string> { "jump", "balloon", "fly", "steps", "poof" };

        Sprite(double w, double h) { Width = w; Height = h; }

        public static Sprite Load(string name)
        {
            try
            {
                var dir = Characters.DirOf(name); if (dir == null) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, name + "-sheet.json")));
                var root = doc.RootElement;
                int cw = root.GetProperty("cell")[0].GetInt32(), ch = root.GetProperty("cell")[1].GetInt32();
                var dp = root.GetProperty("displayPoints");
                var s = new Sprite(dp[0].GetDouble(), dp[1].GetDouble());
                if (root.TryGetProperty("climb", out var cl) && cl.ValueKind == JsonValueKind.String && ClimbStyles.Contains(cl.GetString())) s.Climb = cl.GetString();
                using var sheet = new Bitmap(Path.Combine(dir, name + "-sheet.png"));
                int r = 0;
                foreach (var row in root.GetProperty("rows").EnumerateArray())
                {
                    string rn = row.GetProperty("name").GetString();
                    int n = row.GetProperty("frames").GetInt32();
                    var list = new List<Bitmap>();
                    for (int i = 0; i < n; i++)
                    {
                        var cell = new Rectangle(i * cw, r * ch, cw, ch);
                        if (cell.Right > sheet.Width || cell.Bottom > sheet.Height) break;
                        list.Add(sheet.Clone(cell, PixelFormat.Format32bppArgb));
                    }
                    s.frames[rn] = list;
                    s.fps[rn] = row.GetProperty("fps").GetDouble();
                    r++;
                }
                if (!s.Has("idle") || !s.Has("walk")) { s.Dispose(); return null; }
                return s;
            }
            catch (Exception e) { Settings.Log($"sheet {name}: {e.Message}"); return null; }
        }

        public bool Has(string row) => frames.TryGetValue(row, out var l) && l.Count > 0;
        public int Count(string row) => frames[row].Count;
        public double Fps(string row) => fps.TryGetValue(row, out var f) ? f : 8;
        public Bitmap First(string row) => frames[row][0];

        // A GDI bitmap of one frame at w×h pixels, kept for reuse.
        public IntPtr Frame(string row, int i, bool mirrored, int w, int h)
        {
            string key = $"{row}/{i}/{(mirrored ? 1 : 0)}/{w}x{h}";
            if (cache.TryGetValue(key, out var hb)) return hb;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                if (mirrored) { g.TranslateTransform(w, 0); g.ScaleTransform(-1, 1); }
                var src = frames[row][i];
                using var edges = new ImageAttributes();
                edges.SetWrapMode(WrapMode.TileFlipXY);     // no faint fringe along the frame's edges
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, edges);
            }
            hb = bmp.GetHbitmap(Color.FromArgb(0));
            if (cache.Count > 400) ClearCache();
            cache[key] = hb;
            return hb;
        }

        void ClearCache()
        {
            foreach (var hb in cache.Values) Native.DeleteObject(hb);
            cache.Clear();
        }

        public void Dispose()
        {
            ClearCache();
            foreach (var l in frames.Values) foreach (var b in l) b.Dispose();
        }
    }

    // The pet's window: borderless, always on top, never takes focus, not in the taskbar,
    // and drawn with per-pixel alpha so only the character itself is visible and clickable.
    sealed class PetWindow : Form
    {
        public PetWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 /* LAYERED */ | 0x00000080 /* TOOLWINDOW */ | 0x00000008 /* TOPMOST */ | 0x08000000 /* NOACTIVATE */;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        // Crossing to a monitor with another scale: the pet redraws itself at the new size,
        // so don't let Windows resize the window in between.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0 /* WM_DPICHANGED */) return;
            base.WndProc(ref m);
        }

        public void Draw(IntPtr hbitmap, int x, int y, int w, int h, byte alpha = 255)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr old = Native.SelectObject(mem, hbitmap);
            try
            {
                var dst = new Native.POINT(x, y); var size = new Native.SIZE(w, h); var src = new Native.POINT(0, 0);
                var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = Native.AC_SRC_ALPHA };
                Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.SelectObject(mem, old);
                Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }

    // One character on screen. Several can be out at once; they share one Voice and one Senses,
    // which the tray app updates (Program.cs).
    sealed class Pet : IDisposable
    {
        readonly Sprite sprite;
        readonly Settings settings;
        readonly Voice voice;
        readonly Senses senses;
        readonly Random rng = new Random();
        readonly PetWindow window = new PetWindow();
        readonly Timer timer = new Timer { Interval = 33 };

        double x, footY, vy, dir = 1;      // x: left edge; footY: the line its feet are on (pixels, y down)
        bool dragging, falling, shown;
        double landing;
        Screen home;                        // whose bottom it falls to
        Floor floor;                        // what it stands on now (null: the bottom of its screen)
        List<Floor> floors = new List<Floor>();   // tab strips wide enough for it
        double lastScan = -1;
        IntPtr target = IntPtr.Zero, declined = IntPtr.Zero;   // front Chrome window; one it was dragged off
        double targetSince;
        Screen targetScreen;
        // A jump onto a floor. Up to a strip far above, it goes the character's own way (Style).
        sealed class Hop { public double FromX, FromY, ToX, ToY, T, Dur; public Floor To; public string Style; public int Steps; }
        Hop hop;
        double restUntil, nextRest;
        string row = "idle"; int frame; double phase;
        double last;
        Point? dragLast; bool moved;
        bool failed;                        // logged one error already

        Pet(Sprite s, Settings st, Voice v, Senses se, int slot)
        {
            sprite = s; settings = st; voice = v; senses = se;
            window.MouseDown += (o, e) => { if (e.Button == MouseButtons.Left) { dragLast = Cursor.Position; moved = false; } };
            window.MouseMove += (o, e) => OnDrag(e);
            window.MouseUp += (o, e) => { if (e.Button == MouseButtons.Left) OnRelease(); };
            home = Primary();
            if (home != null)
            {
                var wa = home.WorkingArea;
                x = wa.Left + wa.Width * (0.3 + 0.13 * (slot % 5));
                footY = wa.Bottom;
            }
            dir = slot % 2 == 0 ? 1 : -1;
            nextRest = Clock.Now + 12;
            last = Clock.Now;
            window.Show(); shown = true;
            timer.Tick += (o, e) => SafeTick();
            timer.Start();
        }

        public static Pet Create(string name, Settings st, Voice v, Senses se, int slot)
        {
            var s = Sprite.Load(name);
            return s == null ? null : new Pet(s, st, v, se, slot);
        }

        public void Dispose()
        {
            timer.Stop(); timer.Dispose();
            window.Close(); window.Dispose();
            sprite.Dispose();
        }

        static Screen Primary() => Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();

        double K => Native.ScaleAt((int)(x + 10), (int)footY - 10);     // pixels per 96-dpi unit here
        (int w, int h) SizeAt(double k) => ((int)Math.Round(sprite.Width * settings.Scale * k), (int)Math.Round(sprite.Height * settings.Scale * k));

        void OnDrag(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || dragLast == null) return;
            var p = Cursor.Position; var l = dragLast.Value;
            if (!moved && Math.Abs(p.X - l.X) + Math.Abs(p.Y - l.Y) < 4) return;
            moved = true; dragging = true; falling = false; hop = null;
            x += p.X - l.X; footY += p.Y - l.Y;
            dragLast = p;
        }

        void OnRelease()
        {
            if (moved)
            {
                var (w, h) = SizeAt(K);
                home = Screen.FromPoint(new Point((int)(x + w / 2.0), (int)(footY - h / 2.0)));
                LetGo();
                voice.SayNow(Voice.Kind.Drop, Clock.Now);
                // put somewhere by hand: don't hop back onto the front Chrome window until another one comes to the front
                declined = target;
            }
            else voice.Want(Voice.Kind.Poke);
            dragLast = null; moved = false;
        }

        void LetGo() { dragging = false; falling = true; vy = 0; floor = null; }

        static double Dist(Floor f, double cx) => cx < f.MinX ? f.MinX - cx : (cx > f.MaxX ? cx - f.MaxX : 0);
        static double ClampX(double x, Floor f, int w) => Math.Min(Math.Max(x, f.MinX), Math.Max(f.MinX, f.MaxX - w));
        static Floor ScreenFloor(Screen s) => new Floor { Window = IntPtr.Zero, Y = s.WorkingArea.Bottom, MinX = s.WorkingArea.Left, MaxX = s.WorkingArea.Right, OriginX = s.WorkingArea.Left };

        // A bad sheet or setting shouldn't throw 30 times a second: log once and keep going.
        void SafeTick()
        {
            try { Tick(); }
            catch (Exception e) { if (!failed) { failed = true; Settings.Log($"pet: {e}"); } }
        }

        void Tick()
        {
            double now = Clock.Now;
            double dt = Math.Min(0.1, now - last); last = now;

            // A full-screen app is in front: stay out of sight (a drag in progress ends there).
            if (senses.FullScreen)
            {
                if (shown) { window.Hide(); shown = false; }
                if (dragging) { LetGo(); dragLast = null; moved = false; }
                return;
            }
            if (!shown) { window.Show(); shown = true; }

            // Screens come and go, and their work areas change (a resized taskbar): look it up again.
            home = Screen.AllScreens.FirstOrDefault(s => home != null && s.DeviceName == home.DeviceName) ?? Primary();
            if (home == null) return;
            double k = K;
            var (w, h) = SizeAt(k);
            int feet = (int)Math.Round(5 * settings.Scale * k);   // the sheet's floor line is 5 units above the cell's bottom
            double Mid() => x + w / 2.0;

            // Look every frame while standing on a window, so it keeps up when the window is dragged.
            if (now - lastScan >= (floor != null && floor.Window != IntPtr.Zero ? 0 : 0.3))
            {
                lastScan = now;
                var scan = ChromeScan.Get(now, settings);
                floors = scan.Floors.Where(f => f.MaxX - f.MinX >= w).ToList();
                if (scan.Front != target) { target = scan.Front; targetSince = now; }
                targetScreen = scan.FrontScreen;
                if (target != declined && declined != IntPtr.Zero && target != IntPtr.Zero) declined = IntPtr.Zero;
                // Standing on a Chrome window: follow it, step over to another free part of it, fall when it's gone.
                if (floor != null && floor.Window != IntPtr.Zero && !dragging && !falling && hop == null)
                {
                    var mine = floors.Where(f => f.Window == floor.Window).ToList();
                    if (mine.Count > 0)
                    {
                        x += mine[0].OriginX - floor.OriginX;
                        var on = mine.FirstOrDefault(f => Mid() >= f.MinX && Mid() <= f.MaxX);
                        if (on != null) floor = on;
                        else
                        {
                            var g = mine.OrderBy(f => Dist(f, Mid())).First();
                            if (Dist(g, Mid()) <= w) { floor = g; x = ClampX(x, g, w); }
                            else StartHop(g, w, k);
                        }
                    }
                    else { floor = null; falling = true; vy = 0; }
                }
                // Carried to another screen with its window: that screen's bottom is now where it falls to.
                if (floor != null && floor.Window != IntPtr.Zero) home = Screen.FromPoint(new Point((int)Mid(), floor.Y - 1));
            }
            // Go to the frontmost Chrome window once it has been in front for a moment.
            if (!dragging && !falling && hop == null && landing <= 0 && target != IntPtr.Zero && target != declined
                && (floor == null || floor.Window != target) && now - targetSince >= 1.0 && targetScreen != null)
            {
                var g = floors.Where(f => f.Window == target).OrderBy(f => Dist(f, Mid())).FirstOrDefault();
                bool sameScreen = targetScreen.DeviceName == home.DeviceName;
                if (g != null) { if (sameScreen) StartHop(g, w, k); else DropIn(g, targetScreen, w, h); }
                else if (!sameScreen) DropIn(ScreenFloor(targetScreen), targetScreen, w, h);   // no free strip there: wait at that screen's bottom
            }

            var ground = ScreenFloor(home);
            var here = floor ?? hop?.To ?? ground;
            double minX = here.MinX, maxX = here.MaxX - w;
            string want = "idle"; double speed = 0; int? fixedFrame = null;
            double scale = 1, alpha = 1;

            if (dragging) { want = "fall"; fixedFrame = 0; }
            else if (hop != null)
            {
                hop.T += dt;
                double p = Math.Min(1, hop.T / hop.Dur);
                double S = settings.Scale * k, lerpX = hop.FromX + (hop.ToX - hop.FromX) * p;
                bool hasClimb = sprite.Has("climb");
                // a parabola from a (q=0) through rise (q=0.5) to c (q=1); y goes down, so "up" is smaller
                static double Arc(double a, double c, double rise, double q)
                {
                    double b = 2 * rise - (a + c) / 2;
                    return a * (1 - q) * (1 - q) + 2 * b * q * (1 - q) + c * q * q;
                }
                dir = hop.ToX >= hop.FromX ? 1 : -1;
                switch (hop.Style)
                {
                    case "jump":
                    {
                        // crouch, then one big leap (with a spin when the sheet has a "climb" row)
                        double crouch = 0.3 / hop.Dur;
                        if (p < crouch)
                        {
                            x = hop.FromX; footY = hop.FromY;
                            want = hasClimb ? "climb" : "fall"; fixedFrame = hasClimb ? 0 : 2;
                        }
                        else
                        {
                            double q = (p - crouch) / (1 - crouch);
                            x = hop.FromX + (hop.ToX - hop.FromX) * q;
                            // rises fast and slows near the strip, with a little extra lift that
                            // never takes it off the top of the screen
                            double lift = Math.Max(0, Math.Min(35 * S, hop.ToY - (home.Bounds.Top + h - feet)));
                            footY = hop.FromY + (hop.ToY - hop.FromY) * (1 - (1 - q) * (1 - q)) - lift * Math.Sin(Math.PI * q);
                            want = hasClimb ? "climb" : "fall"; fixedFrame = hasClimb ? 1 + Math.Min(4, (int)(q * 5)) : (q < 0.5 ? 0 : 1);
                        }
                        break;
                    }
                    case "balloon":
                    {
                        // floats up slowly, swaying
                        double e = p * p * (3 - 2 * p);
                        x = lerpX + Math.Sin(p * Math.PI * 3) * 8 * S;
                        footY = hop.FromY + (hop.ToY - hop.FromY) * e;
                        want = hasClimb ? "climb" : "idle";
                        break;
                    }
                    case "fly":
                    {
                        // flaps up quickly at first, then eases in, bobbing
                        double e = 1 - (1 - p) * (1 - p);
                        x = lerpX + Math.Sin(p * Math.PI * 3) * 12 * S;
                        footY = hop.FromY + (hop.ToY - hop.FromY) * e - Math.Sin(p * Math.PI * 6) * 5 * S * (1 - p);
                        want = hasClimb ? "climb" : "dance";
                        break;
                    }
                    case "steps":
                    {
                        // several little hops, one after another
                        double n = hop.Steps, kk = Math.Min(n - 1, Math.Floor(p * n)), q = p * n - kk;
                        double ay = hop.FromY + (hop.ToY - hop.FromY) * kk / n, cy = hop.FromY + (hop.ToY - hop.FromY) * (kk + 1) / n;
                        x = lerpX;
                        footY = Arc(ay, cy, Math.Min(ay, cy) - 22 * S, q);
                        want = "fall"; fixedFrame = q < 0.5 ? 0 : 1;
                        break;
                    }
                    case "poof":
                    {
                        // shrinks away where it is, and pops up again on the strip
                        if (p < 0.35) { x = hop.FromX; footY = hop.FromY; scale = 1 - p / 0.35 * 0.85; alpha = 1 - p / 0.35; }
                        else if (p < 0.55) { x = hop.ToX; footY = hop.ToY; scale = 0.15; alpha = 0; }
                        else { double q = (p - 0.55) / 0.45; x = hop.ToX; footY = hop.ToY; scale = 0.15 + 0.85 * q; alpha = q; }
                        want = "idle"; fixedFrame = 0;
                        break;
                    }
                    default:
                        // a small jump in an arc up (or down) to a strip close by
                        x = lerpX;
                        footY = Arc(hop.FromY, hop.ToY, Math.Min(hop.ToY, hop.FromY) - 40 * S, p);
                        want = "fall"; fixedFrame = p < 0.5 ? 0 : 1;
                        break;
                }
                if (p >= 1)
                {
                    floor = hop.To; x = hop.ToX; footY = hop.ToY;
                    landing = hop.Style == "poof" ? 0 : 0.3;
                    hop = null; scale = 1; alpha = 1;
                }
            }
            else if (falling)
            {
                double before = footY;
                vy += 2200 * k * dt; footY += vy * dt;
                want = "fall"; fixedFrame = vy < 200 * k ? 0 : 1;
                // land on the first tab strip it passes through, else on the bottom of its screen
                var hit = floors.Where(f => before <= f.Y + 1 && footY >= f.Y && Mid() >= f.MinX && Mid() <= f.MaxX).OrderBy(f => f.Y).FirstOrDefault();
                if (hit != null)
                {
                    floor = hit; footY = hit.Y; falling = false; landing = 0.3;
                    if (hit.Window == declined) declined = IntPtr.Zero;    // put back on it by hand: fine to stay
                }
                else if (footY >= ground.Y) { floor = null; footY = ground.Y; falling = false; landing = 0.3; }
            }
            else if (landing > 0)
            {
                landing -= dt;
                want = "fall"; fixedFrame = landing > 0.15 ? 2 : 3;
            }
            else if (senses.Music) want = "dance";
            else if (senses.Away) want = "sleep";
            else if (now < restUntil) want = "idle";
            else
            {
                want = "walk"; speed = 18 * Math.Max(0.7, settings.Scale);
                if (now >= nextRest)
                {
                    restUntil = now + 3 + rng.NextDouble() * 5;
                    nextRest = restUntil + 10 + rng.NextDouble() * 15;
                }
                if (rng.NextDouble() < dt * 0.05) dir = -dir;
            }

            x += dir * speed * k * dt;
            if (!dragging && hop == null)
            {
                if (x <= minX) { x = minX; if (speed > 0) dir = 1; }
                if (x >= maxX) { x = maxX; if (speed > 0) dir = -1; }
            }
            if (!dragging && !falling && hop == null) footY = (floor ?? ground).Y;

            if (!sprite.Has(want)) want = "idle";
            if (want != row) { row = want; frame = 0; phase = 0; }
            int count = sprite.Count(row);
            if (fixedFrame.HasValue) frame = fixedFrame.Value;
            else
            {
                phase += dt * sprite.Fps(row);
                while (phase >= 1) { phase -= 1; frame++; if (frame >= count) frame = 0; }
            }
            frame = Math.Min(frame, count - 1);
            // shrunk (while it pops away or back) toward its feet, the bottom middle
            int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
            var hb = sprite.Frame(row, frame, row == "walk" && dir < 0, sw, sh);
            window.Draw(hb, (int)Math.Round(x + (w - sw) / 2.0), (int)Math.Round(footY) - sh + (int)Math.Round(feet * scale), sw, sh,
                        (byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)));
        }

        // Jump to a floor on the same screen, landing as close to where it is as the floor allows.
        // A short way, or down, is a small hop. Far up (from the taskbar to a tab strip) each
        // character goes its own way, slower, so it doesn't just shoot up the screen.
        void StartHop(Floor f, int w, double k)
        {
            double tx = ClampX(x, f, w), ty = f.Y;
            double d = Math.Sqrt((tx - x) * (tx - x) + (ty - footY) * (ty - footY)) / k;
            double rise = (footY - ty) / (k * settings.Scale);       // in sheet units, up is positive
            string style = rise > 120 ? sprite.Climb : "hop";
            int steps = Math.Max(2, (int)Math.Ceiling(rise / 70));
            double dur = style switch
            {
                "jump" => 1.5,
                "balloon" => Math.Clamp(rise / 280, 1.8, 3.5),
                "fly" => Math.Clamp(rise / 380, 1.3, 2.6),
                "steps" => steps * 0.34,
                "poof" => 1.0,
                _ => Math.Min(0.9, 0.45 + d / 2000),
            };
            hop = new Hop { FromX = x, FromY = footY, ToX = tx, ToY = ty, T = 0, Dur = dur, To = f, Style = style, Steps = steps };
            floor = null;
            voice.Want(Voice.Kind.Hop);
        }

        // On another screen: appear just above the floor there and drop onto it,
        // at the same place across the width (as a fraction of the screen).
        void DropIn(Floor f, Screen to, int w, int h)
        {
            var from = home ?? to;
            double frac = (x - from.Bounds.Left) / Math.Max(1, from.Bounds.Width);
            home = to;
            x = ClampX(to.Bounds.Left + frac * to.Bounds.Width, f, w);
            footY = f.Y - h * 0.8;
            floor = null; hop = null; falling = true; vy = 0;
        }
    }
}
