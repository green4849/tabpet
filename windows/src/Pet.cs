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
                if (!s.frames.TryGetValue("idle", out var idle) || idle.Count == 0 || !s.frames.TryGetValue("walk", out var walk) || walk.Count == 0) return null;
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
                g.DrawImage(frames[row][i], new Rectangle(0, 0, w, h));
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

        public void Draw(IntPtr hbitmap, int x, int y, int w, int h)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr old = Native.SelectObject(mem, hbitmap);
            try
            {
                var dst = new Native.POINT(x, y); var size = new Native.SIZE(w, h); var src = new Native.POINT(0, 0);
                var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
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

    sealed class Pet : IDisposable
    {
        enum Mood { Quiet, Music, Away }

        public readonly string Name;
        readonly Sprite sprite;
        readonly Settings settings;
        readonly Voice voice;
        readonly Senses senses;
        readonly HashSet<IntPtr> petWindows;
        readonly int slot;
        readonly Random rng = new Random();
        PetWindow window;
        Timer timer;

        double x, footY, vy, dir = 1;      // x: left edge; footY: the line its feet are on (pixels, y down)
        bool dragging, falling, placed, shown;
        double landing;
        Screen home;
        Floor floor;                        // what it stands on now (null: the bottom of its screen)
        List<Floor> floors = new List<Floor>();
        double lastScan = -1;
        IntPtr target = IntPtr.Zero, declined = IntPtr.Zero;
        double targetSince;
        Screen targetScreen;
        sealed class Hop { public double FromX, FromY, ToX, ToY, T, Dur; public Floor To; }
        Hop hop;
        double restUntil, nextRest;
        string row = "idle"; int frame; double phase;
        double last;
        Point? dragLast; bool moved;

        Pet(string name, Sprite s, Settings st, Voice v, Senses se, HashSet<IntPtr> windows, int slot)
        {
            Name = name; sprite = s; settings = st; voice = v; senses = se; petWindows = windows; this.slot = slot;
        }

        public static Pet Create(string name, Settings st, Voice v, Senses se, HashSet<IntPtr> windows, int slot)
        {
            var s = Sprite.Load(name);
            return s == null ? null : new Pet(name, s, st, v, se, windows, slot);
        }

        public void Start()
        {
            if (window != null) return;
            window = new PetWindow();
            window.MouseDown += (o, e) => { if (e.Button == MouseButtons.Left) { dragLast = Cursor.Position; moved = false; } };
            window.MouseMove += (o, e) => OnDrag(e);
            window.MouseUp += (o, e) => { if (e.Button == MouseButtons.Left) OnRelease(); };
            window.Show();
            petWindows.Add(window.Handle);
            if (!placed)
            {
                home = Screen.PrimaryScreen;
                var wa = home.WorkingArea;
                x = wa.Left + wa.Width * (0.3 + 0.13 * (slot % 5));
                footY = wa.Bottom;
                dir = slot % 2 == 0 ? 1 : -1;
                nextRest = Clock.Now + 12;
                placed = true;
            }
            shown = true;
            last = Clock.Now;
            timer = new Timer { Interval = 33 };
            timer.Tick += (o, e) => Tick();
            timer.Start();
        }

        public void Stop()
        {
            timer?.Stop(); timer?.Dispose(); timer = null;
            if (window != null) { petWindows.Remove(window.Handle); window.Close(); window.Dispose(); window = null; }
        }

        public void Dispose() { Stop(); sprite.Dispose(); }

        double K => Native.ScaleAt((int)(x + 10), (int)footY - 10);     // pixels per 96-dpi unit here
        (int w, int h) Size(double k) => ((int)Math.Round(sprite.Width * settings.Scale * k), (int)Math.Round(sprite.Height * settings.Scale * k));

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
                var (w, h) = Size(K);
                home = Screen.FromPoint(new Point((int)(x + w / 2.0), (int)(footY - h / 2.0)));
                dragging = false; falling = true; vy = 0; floor = null;
                voice.SayNow(Voice.Kind.Drop, Clock.Now);
                // put somewhere by hand: don't hop back onto the front Chrome window until another one comes to the front
                declined = target;
            }
            else voice.Want(Voice.Kind.Poke);
            dragLast = null; moved = false;
        }

        static double Dist(Floor f, double cx) => cx < f.MinX ? f.MinX - cx : (cx > f.MaxX ? cx - f.MaxX : 0);
        static double ClampX(double x, Floor f, int w) => Math.Min(Math.Max(x, f.MinX), Math.Max(f.MinX, f.MaxX - w));
        static Floor ScreenFloor(Screen s) => new Floor { Window = IntPtr.Zero, Y = s.WorkingArea.Bottom, MinX = s.WorkingArea.Left, MaxX = s.WorkingArea.Right, OriginX = s.WorkingArea.Left };

        Mood CurrentMood => senses.Music ? Mood.Music : senses.Away ? Mood.Away : Mood.Quiet;

        void Tick()
        {
            if (window == null) return;
            double now = Clock.Now;
            double dt = Math.Min(0.1, now - last); last = now;
            senses.Update(now);

            // A full-screen app is in front: stay out of sight and quiet.
            if (senses.FullScreen) { if (shown) { window.Hide(); shown = false; } return; }
            if (!shown) { window.Show(); shown = true; }

            if (home == null || !Screen.AllScreens.Any(s => s.DeviceName == home.DeviceName)) home = Screen.PrimaryScreen;
            double k = K;
            var (w, h) = Size(k);
            int feet = (int)Math.Round(4 * settings.Scale * k);   // the sheet leaves this much below the feet
            double Mid() => x + w / 2.0;

            // Look more often while standing on a window, so it keeps up when the window is dragged.
            if (now - lastScan >= (floor != null && floor.Window != IntPtr.Zero ? 0.1 : 0.3))
            {
                lastScan = now;
                var scan = ChromeScan.Get(now, settings, petWindows);
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
            }
            // Go to the frontmost Chrome window once it has been in front for a moment.
            if (!dragging && !falling && hop == null && landing <= 0 && target != IntPtr.Zero && target != declined
                && (floor == null || floor.Window != target) && now - targetSince >= 1.0)
            {
                var mine = floors.Where(f => f.Window == target).ToList();
                if (mine.Count > 0 && targetScreen != null)
                {
                    var g = mine.OrderBy(f => Dist(f, Mid())).First();
                    if (targetScreen.DeviceName == home.DeviceName) StartHop(g, w, k);
                    else DropIn(g, targetScreen, w, h);
                }
                else if (targetScreen != null && targetScreen.DeviceName != home.DeviceName)
                    DropIn(ScreenFloor(targetScreen), targetScreen, w, h);   // no free strip there: wait at that screen's bottom
            }

            var ground = ScreenFloor(home);
            var here = floor ?? hop?.To ?? ground;
            double minX = here.MinX, maxX = here.MaxX - w;
            string want = "idle"; double speed = 0; int? fixedFrame = null;

            if (dragging) { want = "fall"; fixedFrame = 0; }
            else if (hop != null)
            {
                // a small jump in an arc up (or down) onto a window's tab strip
                hop.T += dt;
                double p = Math.Min(1, hop.T / hop.Dur);
                double rise = Math.Min(hop.ToY, hop.FromY) - 40 * k;
                x = hop.FromX + (hop.ToX - hop.FromX) * p;
                double a = hop.FromY, c = hop.ToY, b = 2 * rise - (a + c) / 2;
                footY = a * (1 - p) * (1 - p) + 2 * b * p * (1 - p) + c * p * p;
                want = "fall"; fixedFrame = p < 0.5 ? 0 : 1;
                dir = hop.ToX >= hop.FromX ? 1 : -1;
                if (p >= 1) { floor = hop.To; footY = hop.ToY; hop = null; landing = 0.3; }
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
            else
            {
                switch (CurrentMood)
                {
                    case Mood.Music: want = "dance"; break;
                    case Mood.Away: want = "sleep"; break;
                    default:
                        if (now < restUntil) want = "idle";
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
                        break;
                }
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
            if (fixedFrame.HasValue) frame = Math.Min(fixedFrame.Value, count - 1);
            else
            {
                phase += dt * sprite.Fps(row);
                while (phase >= 1) { phase -= 1; frame++; if (frame >= count) frame = 0; }
            }
            if (frame >= count) frame = 0;
            var hb = sprite.Frame(row, frame, row == "walk" && dir < 0, w, h);
            window.Draw(hb, (int)Math.Round(x), (int)Math.Round(footY) - h + feet, w, h);
            voice.Flush(now, senses.Music, senses.Away, true);
        }

        // Jump to a floor on the same screen, landing as close to where it is as the floor allows.
        void StartHop(Floor f, int w, double k)
        {
            double tx = ClampX(x, f, w), ty = f.Y;
            double d = Math.Sqrt((tx - x) * (tx - x) + (ty - footY) * (ty - footY)) / k;
            hop = new Hop { FromX = x, FromY = footY, ToX = tx, ToY = ty, T = 0, Dur = Math.Min(0.9, 0.45 + d / 2000), To = f };
            floor = null;
            voice.Want(Voice.Kind.Hop);
        }

        // On another screen: appear just above the floor there and drop onto it,
        // at the same place across the width (as a fraction of the screen).
        void DropIn(Floor f, Screen to, int w, int h)
        {
            var from = home ?? Screen.PrimaryScreen;
            double frac = (x - from.Bounds.Left) / Math.Max(1, from.Bounds.Width);
            home = to;
            x = ClampX(to.Bounds.Left + frac * to.Bounds.Width, f, w);
            footY = f.Y - Math.Min(h * 0.8, f.Y - to.Bounds.Top);
            floor = null; hop = null; falling = true; vy = 0;
        }
    }
}
