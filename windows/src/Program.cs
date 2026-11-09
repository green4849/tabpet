// Entry point: an icon in the notification area (system tray) whose menu turns the
// characters and their voice on or off. Each character is a Pet (Pet.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TabPet
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // One copy at a time: a second one would put the characters on screen twice.
            using var mutex = new Mutex(true, "TabPet.SingleInstance", out bool first);
            if (!first) return;
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            // Log anything unexpected instead of showing an error box over the desktop.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (o, e) => Settings.Log($"error: {e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (o, e) => Settings.Log($"fatal: {e.ExceptionObject}");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        readonly Settings settings = Settings.Load();
        readonly Voice voice;
        readonly Senses senses;
        readonly Dictionary<string, Pet> pets = new Dictionary<string, Pet>();
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };

        public TrayApp()
        {
            voice = new Voice(settings);
            senses = new Senses(voice);
            tray = new NotifyIcon { Text = "TabPet", Icon = MakeIcon(), ContextMenuStrip = menu, Visible = true };
            menu.Items.Add("…");              // the menu is built each time it opens
            menu.Opening += (o, e) => BuildMenu();
            SyncPets();
            // What the computer is doing, and the sounds the pets want, for all of them at once.
            timer.Tick += (o, e) =>
            {
                double now = Clock.Now;
                senses.Update(now);
                // Hidden (a full-screen app in front) or none out: stay quiet too.
                voice.Flush(now, senses.Music, senses.Away, !senses.FullScreen && pets.Count > 0);
            };
            timer.Start();
        }

        // Start the chosen characters that aren't out yet, put away the rest.
        void SyncPets()
        {
            var want = settings.Shown ? settings.Characters.Where(c => Characters.DirOf(c) != null).ToList() : new List<string>();
            if (settings.Shown && want.Count == 0)
            {
                var all = Characters.All();
                if (all.Count > 0) { want.Add(all[0]); settings.Characters = new List<string> { all[0] }; settings.Save(); }
            }
            foreach (var name in pets.Keys.ToList().Where(n => !want.Contains(n))) { pets[name].Dispose(); pets.Remove(name); }
            for (int i = 0; i < want.Count; i++)
            {
                if (pets.ContainsKey(want[i])) continue;
                var pet = Pet.Create(want[i], settings, voice, senses, i);
                if (pet == null) { Settings.Log($"could not read the sheet for {want[i]}"); continue; }
                pets[want[i]] = pet;
            }
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            var all = Characters.All();
            var shown = new ToolStripMenuItem(all.Count == 0 ? $"화면 캐릭터 (그림 없음: {Characters.UserDir})" : "화면에 캐릭터 띄우기")
            { Checked = settings.Shown && all.Count > 0, Enabled = all.Count > 0 };
            shown.Click += (o, e) => { settings.Shown = !settings.Shown; settings.Save(); SyncPets(); };
            menu.Items.Add(shown);

            var sound = new ToolStripMenuItem("캐릭터 소리") { Checked = settings.Sound, Enabled = all.Count > 0 && settings.Shown };
            sound.Click += (o, e) => { settings.Sound = !settings.Sound; settings.Save(); };
            menu.Items.Add(sound);

            if (all.Count > 1)
            {
                // Tick as many as you like; at least one stays.
                var chars = new ToolStripMenuItem("캐릭터 (여러 개 고를 수 있음)");
                foreach (var name in all)
                {
                    bool on = settings.Characters.Contains(name);
                    var it = new ToolStripMenuItem(Characters.Label(name)) { Checked = on, Enabled = !(on && settings.Characters.Count == 1) };
                    it.Click += (o, e) =>
                    {
                        var c = settings.Characters.ToList();
                        if (c.Contains(name)) { if (c.Count > 1) c.Remove(name); } else c.Add(name);
                        settings.Characters = c; settings.Save(); SyncPets();
                    };
                    chars.DropDownItems.Add(it);
                }
                menu.Items.Add(chars);
            }

            var login = new ToolStripMenuItem("Windows 시작할 때 실행") { Checked = StartsAtLogin() };
            login.Click += (o, e) => SetStartAtLogin(!StartsAtLogin());
            menu.Items.Add(login);

            var folder = new ToolStripMenuItem("내 캐릭터 폴더 열기");
            folder.Click += (o, e) =>
            {
                System.IO.Directory.CreateDirectory(Characters.UserDir);
                System.Diagnostics.Process.Start("explorer.exe", Characters.UserDir);
            };
            menu.Items.Add(folder);

            menu.Items.Add(new ToolStripSeparator());
            var quit = new ToolStripMenuItem("종료");
            quit.Click += (o, e) => Quit();
            menu.Items.Add(quit);
        }

        static bool StartsAtLogin()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("TabPet") != null;
        }

        static void SetStartAtLogin(bool on)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue("TabPet", $"\"{Application.ExecutablePath}\"");
            else key.DeleteValue("TabPet", false);
        }

        // The tray icon: the first frame of the first character, or a plain one.
        Icon MakeIcon()
        {
            try
            {
                var name = settings.Characters.FirstOrDefault(c => Characters.DirOf(c) != null) ?? Characters.All().FirstOrDefault();
                using var s = name == null ? null : Sprite.Load(name);
                if (s != null)
                {
                    using var bmp = new Bitmap(32, 32);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        var src = s.First("idle");
                        // crop to the character: the middle of the cell, a little above the bottom
                        int side = (int)(src.Width * 0.75);
                        var from = new Rectangle((src.Width - side) / 2, src.Height - side - (int)(src.Height * 0.04), side, side);
                        g.DrawImage(src, new Rectangle(0, 0, 32, 32), from, GraphicsUnit.Pixel);
                    }
                    IntPtr hIcon = bmp.GetHicon();
                    var icon = (Icon)Icon.FromHandle(hIcon).Clone();
                    Native.DestroyIcon(hIcon);
                    return icon;
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        void Quit()
        {
            timer.Stop(); timer.Dispose();
            foreach (var p in pets.Values) p.Dispose();
            pets.Clear();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }
}
