// Everything the pets share: settings, where characters and sounds live, the voice,
// what the computer is doing (music, away, full screen) and where Chrome's tab strips are.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace TabPet
{
    static class Clock
    {
        static readonly Stopwatch sw = Stopwatch.StartNew();
        public static double Now => sw.Elapsed.TotalSeconds;
    }

    // %APPDATA%\TabPet\settings.json. Edit it while TabPet is closed to fine-tune sizes.
    sealed class Settings
    {
        public bool Shown { get; set; } = true;
        public bool Sound { get; set; } = true;
        public List<string> Characters { get; set; } = new List<string> { "sprout" };
        public double Scale { get; set; } = 0.75;           // size as a fraction of the sheet
        public double TabStripHeight { get; set; } = 40;    // in 96-dpi units
        public double TabStripLeft { get; set; } = 300;     // left part of the strip left free (tabs)
        public double TabStripRight { get; set; } = 260;    // right part left free (window buttons)

        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabPet");
        static string FilePath => Path.Combine(Dir, "settings.json");
        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public static Settings Load()
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Opts) ?? new Settings(); }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts)); } catch { }
        }

        public static void Log(string line)
        {
            try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\r\n"); } catch { }
        }
    }

    // Your own characters in %USERPROFILE%\.tabpet, the example in the characters folder next to TabPet.exe.
    static class Characters
    {
        public static string UserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tabpet");
        public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "characters");
        public static string[] Dirs => new[] { UserDir, BundledDir };

        public static List<string> All()
        {
            var seen = new List<string>();
            foreach (var dir in Dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*-sheet.json").OrderBy(f => f))
                {
                    var name = Path.GetFileName(f);
                    name = name.Substring(0, name.Length - "-sheet.json".Length);
                    if (!seen.Contains(name)) seen.Add(name);
                }
            }
            return seen;
        }

        public static string DirOf(string name) => Dirs.FirstOrDefault(d => File.Exists(Path.Combine(d, name + "-sheet.json")));

        public static string Label(string name)
        {
            try
            {
                var dir = DirOf(name); if (dir == null) return name;
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, name + "-sheet.json")));
                if (doc.RootElement.TryGetProperty("displayName", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString();
            }
            catch { }
            return name;
        }
    }

    // One voice for all the pets, so the spacing rules hold across them: 8 s between any two
    // sounds, 2 min between the same one, one at a time (the order below is the priority),
    // nothing while a full-screen app is in front or you're away (except dozing off, as it
    // happens), only dance while music plays. Drop is said every time, right away.
    // Clips: poke.wav or poke-1.wav, poke-2.wav … (wav or mp3) in .tabpet\sounds or the bundled sounds.
    sealed class Voice
    {
        public enum Kind { Welcome, Poke, Dance, Hop, Sleep, Drop }

        readonly Settings settings;
        readonly HashSet<Kind> pending = new HashSet<Kind>();
        readonly Dictionary<Kind, double> lastKind = new Dictionary<Kind, double>();
        readonly Random rng = new Random();
        double lastAny = -1e9;
        SoundPlayer player;
        bool mciOpen;

        public Voice(Settings s) { settings = s; }

        // Until when the output device being busy is (probably) our own clip.
        public double SelfUntil { get; private set; }

        public void Want(Kind k) => pending.Add(k);

        public void SayNow(Kind k, double now)
        {
            if (!settings.Sound) return;
            var path = Clip(k);
            if (path == null) { Settings.Log($"TabPet pet voice: {Name(k)} skipped (no clip)"); return; }
            Play(k, path, now);
            pending.Clear();
        }

        public void Flush(double now, bool music, bool away, bool visible)
        {
            try
            {
                if (!settings.Sound || !visible || now - lastAny < 8) return;
                foreach (Kind k in Enum.GetValues(typeof(Kind)))
                {
                    if (!pending.Contains(k)) continue;
                    if (away && k != Kind.Sleep) continue;
                    if (music && k != Kind.Dance) continue;
                    if (lastKind.TryGetValue(k, out double t) && now - t < 120) continue;
                    var path = Clip(k);
                    if (path == null) continue;
                    Play(k, path, now);
                    return;
                }
            }
            finally { pending.Clear(); }
        }

        static string Name(Kind k) => k.ToString().ToLowerInvariant();

        string Clip(Kind k)
        {
            string name = Name(k);
            foreach (var dir in Characters.Dirs.Select(d => Path.Combine(d, "sounds")))
            {
                if (!Directory.Exists(dir)) continue;
                var mine = Directory.GetFiles(dir).Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".wav" && ext != ".mp3") return false;
                    var b = Path.GetFileNameWithoutExtension(f);
                    if (b == name) return true;
                    return b.StartsWith(name + "-") && int.TryParse(b.Substring(name.Length + 1), out _);
                }).ToList();
                if (mine.Count > 0) return mine[rng.Next(mine.Count)];
            }
            return null;
        }

        void Play(Kind k, string path, double now)
        {
            double seconds = 2;
            bool ok = false;
            try
            {
                player?.Stop();
                if (mciOpen) { Native.mciSendString("close tabpet", null, 0, IntPtr.Zero); mciOpen = false; }
                if (Path.GetExtension(path).ToLowerInvariant() == ".wav")
                {
                    seconds = WavSeconds(path);
                    player = new SoundPlayer(path);
                    player.Play();
                    ok = true;
                }
                else
                {
                    ok = Native.mciSendString($"open \"{path}\" type mpegvideo alias tabpet", null, 0, IntPtr.Zero) == 0;
                    if (ok)
                    {
                        mciOpen = true;
                        var len = new StringBuilder(32);
                        Native.mciSendString("status tabpet length", len, len.Capacity, IntPtr.Zero);
                        if (double.TryParse(len.ToString(), out double ms)) seconds = ms / 1000.0;
                        ok = Native.mciSendString("play tabpet", null, 0, IntPtr.Zero) == 0;
                    }
                }
            }
            catch { ok = false; }
            Settings.Log($"TabPet pet voice: {Name(k)} {Path.GetFileName(path)} started={(ok ? 1 : 0)}");
            lastAny = now; lastKind[k] = now;
            SelfUntil = now + seconds + 3;
        }

        static double WavSeconds(string path)
        {
            try
            {
                using var f = File.OpenRead(path); using var r = new BinaryReader(f);
                f.Seek(28, SeekOrigin.Begin); int byteRate = r.ReadInt32();
                return byteRate > 0 ? (f.Length - 44) / (double)byteRate : 2;
            }
            catch { return 2; }
        }
    }

    // What the computer is doing, checked once a second for all the pets.
    sealed class Senses
    {
        readonly Voice voice;
        double lastCheck = -1, lastScreen = -1;
        double? soundSince, silentSince;

        public bool Music { get; private set; }
        public bool Away { get; private set; }
        public bool FullScreen { get; private set; }

        public Senses(Voice v) { voice = v; }

        public void Update(double now)
        {
            if (now - lastScreen >= 0.5) { lastScreen = now; FullScreen = Native.FullScreenAppInFront(); }
            if (now - lastCheck < 1) return;
            lastCheck = now;
            bool wasAway = Away;
            Away = Native.SecondsIdle() > 5 * 60;
            if (wasAway && !Away) voice.Want(Voice.Kind.Welcome);
            if (!wasAway && Away) voice.Want(Voice.Kind.Sleep);
            // Its own clip isn't music.
            if (now < voice.SelfUntil) { soundSince = null; return; }
            bool wasMusic = Music;
            if (AudioMeter.SoundPlaying())
            {
                silentSince = null;
                soundSince ??= now;
                if (now - soundSince.Value >= 3) Music = true;     // not for a notification blip
            }
            else
            {
                soundSince = null;
                silentSince ??= now;
                if (now - silentSince.Value >= 2) Music = false;
            }
            if (Music && !wasMusic) voice.Want(Voice.Kind.Dance);
        }
    }

    // Something to stand on, in physical pixels with y going down: the line its feet are on.
    sealed class Floor
    {
        public IntPtr Window;          // the Chrome window, or zero for the bottom of a screen
        public int Y, MinX, MaxX;
        public int OriginX;            // the window's left edge, to carry the pet along when it moves
    }

    // Where Chrome's tab strips are: the free part of each one that isn't covered by a window in
    // front, and the frontmost Chrome window. Only window positions and process names are read.
    static class ChromeScan
    {
        public sealed class Result
        {
            public List<Floor> Floors = new List<Floor>();
            public IntPtr Front = IntPtr.Zero;
            public Screen FrontScreen;
        }

        static Result cached = new Result();
        static double cachedAt = -1;
        static readonly Dictionary<uint, string> procNames = new Dictionary<uint, string>();
        static readonly HashSet<string> notOccluders = new HashSet<string> { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW" };

        static string ProcessName(uint pid)
        {
            if (procNames.TryGetValue(pid, out var n)) return n;
            try { n = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { n = ""; }
            if (procNames.Count > 500) procNames.Clear();
            procNames[pid] = n; return n;
        }

        public static Result Get(double now, Settings s, HashSet<IntPtr> mine)
        {
            if (now - cachedAt < 0.08) return cached;
            cachedAt = now;
            var res = new Result();
            var front = new List<Rectangle>();
            uint me = (uint)Environment.ProcessId;
            Native.EnumWindows((h, _) =>
            {
                if (mine.Contains(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.Cloaked(h)) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == me) return true;
                if (!Native.Bounds(h, out var r)) return true;
                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                if (rect.Width <= 0 || rect.Height <= 0) return true;
                string cls = Native.ClassName(h);
                if (notOccluders.Contains(cls)) return true;
                if (cls == "Chrome_WidgetWin_1" && ProcessName(pid) == "chrome" && rect.Height > 150 && rect.Width > 200)
                {
                    var top = new Point(rect.Left + rect.Width / 2, rect.Top + 1);
                    var scr = Screen.AllScreens.FirstOrDefault(sc => sc.Bounds.Contains(top));
                    if (scr != null)
                    {
                        if (res.Front == IntPtr.Zero) { res.Front = h; res.FrontScreen = scr; }
                        double k = Native.ScaleAt(top.X, top.Y);
                        int tabH = (int)Math.Round(s.TabStripHeight * k);
                        int floorY = rect.Top + tabH;
                        var band = new Rectangle(rect.Left, rect.Top, rect.Width, tabH);
                        // the free part of the strip, minus what windows in front cover
                        var segs = new List<int[]>();
                        int a0 = Math.Max(rect.Left + (int)(s.TabStripLeft * k), scr.Bounds.Left);
                        int b0 = Math.Min(rect.Right - (int)(s.TabStripRight * k), scr.Bounds.Right);
                        if (b0 > a0) segs.Add(new[] { a0, b0 });
                        foreach (var o in front)
                        {
                            if (!o.IntersectsWith(band)) continue;
                            var next = new List<int[]>();
                            foreach (var g in segs)
                            {
                                if (o.Right <= g[0] || o.Left >= g[1]) { next.Add(g); continue; }
                                if (o.Left > g[0]) next.Add(new[] { g[0], Math.Min(g[1], o.Left) });
                                if (o.Right < g[1]) next.Add(new[] { Math.Max(g[0], o.Right), g[1] });
                            }
                            segs = next;
                        }
                        foreach (var g in segs)
                            res.Floors.Add(new Floor { Window = h, Y = floorY, MinX = g[0], MaxX = g[1], OriginX = rect.Left });
                    }
                }
                front.Add(rect);
                return true;
            }, IntPtr.Zero);
            cached = res;
            return res;
        }
    }
}
