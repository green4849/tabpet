// A small checklist that opens over a character when you click it. Same as the Mac
// version: one list in %USERPROFILE%\.tabpet\todo.json, written as you type (0.3 s),
// nothing is ever removed unless you remove it, and edits to the file from outside show
// up in the open list.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TabPet
{
    sealed class TodoItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("done")] public bool Done { get; set; }
    }

    sealed class TodoFile
    {
        [JsonPropertyName("items")] public List<TodoItem> Items { get; set; } = new List<TodoItem>();
        [JsonPropertyName("draft")] public string Draft { get; set; } = "";
    }

    // The file. Changes made here win over changes from outside that arrive before they're
    // written; a half-written or broken file from outside is skipped.
    sealed class TodoStore
    {
        public TodoFile Data { get; private set; } = new TodoFile();
        public event Action ExternalChange;
        public event Action<bool> SaveError;          // true: the last write failed
        public static string FilePath => Path.Combine(Characters.UserDir, "todo.json");

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer { Interval = 300 };
        readonly System.Windows.Forms.Timer rereadTimer = new System.Windows.Forms.Timer { Interval = 150 };
        readonly Control ui = new Control();   // made on the UI thread: the watcher's events come back through it
        FileSystemWatcher watcher;
        byte[] lastDisk;          // what we last read or wrote: our own writes aren't news
        bool dirty;               // changed here and not on disk yet (stays set if a write fails)
        bool blocked;             // an unreadable file we couldn't copy aside: never write over it

        public TodoStore()
        {
            ui.CreateControl();
            saveTimer.Tick += (o, e) => SaveNow();
            rereadTimer.Tick += (o, e) => { rereadTimer.Stop(); Reread(); };
            try
            {
                if (File.Exists(FilePath))
                {
                    var d = File.ReadAllBytes(FilePath);
                    var f = Parse(d);
                    if (f != null) { Data = f; lastDisk = d; }
                    else
                    {
                        // Can't read it: keep a copy before anything is written over it.
                        var aside = Path.Combine(Characters.UserDir, $"todo.unreadable-{DateTimeOffset.Now.ToUnixTimeSeconds()}.json");
                        try { File.Copy(FilePath, aside); }
                        catch (Exception e) { blocked = true; Settings.Log($"todo: {FilePath} is unreadable and couldn't be copied aside; not writing to it ({e.Message})"); }
                    }
                }
            }
            catch (Exception e) { blocked = true; Settings.Log($"todo: can't read {FilePath}; not writing to it ({e.Message})"); }
            Watch();
        }

        // Only a whole list counts: "items" (each with id, text, done) and "draft". Anything
        // else (an empty object, a file caught half-written) is not taken over what's here.
        static TodoFile Parse(byte[] d)
        {
            try
            {
                using (var doc = JsonDocument.Parse(d))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
                        || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.String) return null;
                    foreach (var i in items.EnumerateArray())
                        if (i.ValueKind != JsonValueKind.Object
                            || !i.TryGetProperty("id", out var a) || a.ValueKind != JsonValueKind.String
                            || !i.TryGetProperty("text", out var b) || b.ValueKind != JsonValueKind.String
                            || !i.TryGetProperty("done", out var c) || (c.ValueKind != JsonValueKind.True && c.ValueKind != JsonValueKind.False)) return null;
                }
                var f = JsonSerializer.Deserialize<TodoFile>(d, Opts);
                if (f == null) return null;
                f.Items = (f.Items ?? new List<TodoItem>()).Where(i => i != null).ToList();
                foreach (var i in f.Items) { i.Text ??= ""; if (string.IsNullOrEmpty(i.Id)) i.Id = Guid.NewGuid().ToString().ToUpperInvariant(); }
                f.Draft ??= "";
                return f;
            }
            catch { return null; }
        }

        byte[] Bytes(TodoFile f) => JsonSerializer.SerializeToUtf8Bytes(f, Opts);

        public void Change(Action<TodoFile> f)
        {
            f(Data);
            dirty = true;
            saveTimer.Stop(); saveTimer.Interval = 300; saveTimer.Start();
        }

        // Write anything still waiting (closing the list, quitting).
        public void Flush() { if (dirty) SaveNow(); }

        void SaveNow()
        {
            saveTimer.Stop();
            if (blocked) { SaveError?.Invoke(true); return; }
            try
            {
                var d = Bytes(Data);
                if (lastDisk != null && d.AsSpan().SequenceEqual(lastDisk)) { dirty = false; return; }
                Directory.CreateDirectory(Characters.UserDir);
                // Write a new file and swap it in, so a half-written file is never left behind.
                var tmp = FilePath + ".tmp";
                File.WriteAllBytes(tmp, d);
                File.Move(tmp, FilePath, true);
                lastDisk = d; dirty = false;
                saveTimer.Interval = 300;
                SaveError?.Invoke(false);
            }
            catch (Exception e)
            {
                Settings.Log($"todo: can't write {FilePath}: {e.Message}");
                SaveError?.Invoke(true);
                saveTimer.Interval = 3000; saveTimer.Start();   // try again in a moment
            }
        }

        void Reread()
        {
            if (dirty || blocked) return;
            byte[] d;
            try { if (!File.Exists(FilePath)) return; d = File.ReadAllBytes(FilePath); }
            catch { rereadTimer.Start(); return; }       // still being written: look again shortly
            if (lastDisk != null && d.AsSpan().SequenceEqual(lastDisk)) return;
            var f = Parse(d);
            if (f == null) return;
            lastDisk = d;
            if (!Bytes(f).AsSpan().SequenceEqual(Bytes(Data))) { Data = f; ExternalChange?.Invoke(); }
        }

        void Watch()
        {
            try
            {
                Directory.CreateDirectory(Characters.UserDir);
                watcher = new FileSystemWatcher(Characters.UserDir, "todo.json")
                { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                void Poke()
                {
                    try { if (ui.IsHandleCreated) ui.BeginInvoke(new Action(() => { rereadTimer.Stop(); rereadTimer.Start(); })); }
                    catch (InvalidOperationException) { }   // quitting
                }
                watcher.Changed += (o, e) => Poke();
                watcher.Created += (o, e) => Poke();
                watcher.Renamed += (o, e) => Poke();
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) { Settings.Log($"todo: can't watch {Characters.UserDir}: {e.Message}"); }
        }
    }

    // The round tick box: fills with the character's colour when done.
    sealed class CheckDot : Control
    {
        public bool Done;
        public Color Accent = SystemColors.Highlight;
        public Color Ring = Color.Gray;
        public CheckDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float d = Math.Min(Width, Height) * 0.54f, x = (Width - d) / 2f, y = (Height - d) / 2f;
            if (Done)
            {
                using var b = new SolidBrush(Accent); g.FillEllipse(b, x, y, d, d);
                // a dark tick on light colours (a pale yellow), white on the rest
                bool light = Accent.R * 0.299 + Accent.G * 0.587 + Accent.B * 0.114 > 0.7 * 255;
                using var p = new Pen(light ? Color.FromArgb(51, 51, 51) : Color.White, d * 0.12f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(p, new[] { new PointF(x + d * 0.27f, y + d * 0.49f), new PointF(x + d * 0.44f, y + d * 0.66f), new PointF(x + d * 0.74f, y + d * 0.33f) });
            }
            else
            {
                using var p = new Pen(Ring, Math.Max(1f, d * 0.09f)); g.DrawEllipse(p, x, y, d, d);
            }
        }
    }

    // The window: borderless, on top, rounded on Windows 11, in the light or dark theme.
    sealed class ChecklistForm : Form
    {
        public ChecklistForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
        }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;      // TOOLWINDOW: not in Alt+Tab
                cp.ClassStyle |= 0x00020000;   // DROPSHADOW
                return cp;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;   // DWMWCP_ROUND (Windows 11; ignored on Windows 10)
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }

    // One line: tick box, the words (click to change them), and × while the mouse is over it
    // or you're typing in it.
    sealed class TodoRow : Panel
    {
        public readonly string Id;
        public readonly CheckDot Check = new CheckDot();
        public readonly TextBox Field = new TextBox { BorderStyle = BorderStyle.None };
        public readonly Label Del = new Label { Text = "×", TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, Visible = false };
        public TodoRow(string id) { Id = id; Controls.Add(Check); Controls.Add(Field); Controls.Add(Del); }
    }

    static class Ime
    {
        [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
        [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
        [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern int ImmGetCompositionStringW(IntPtr hIMC, int index, IntPtr buf, int len);
        // Still putting a syllable together in this box?
        public static bool Composing(Control c)
        {
            IntPtr h = ImmGetContext(c.Handle);
            if (h == IntPtr.Zero) return false;
            try { return ImmGetCompositionStringW(h, 0x0008 /* GCS_COMPSTR */, IntPtr.Zero, 0) > 0; }
            finally { ImmReleaseContext(c.Handle, h); }
        }
    }

    sealed class Checklist
    {
        public readonly TodoStore Store = new TodoStore();
        const int MaxRows = 10;

        ChecklistForm form;
        Panel list;
        Panel line;
        Label plus;
        TextBox input;
        readonly List<TodoRow> rows = new List<TodoRow>();

        Pet pet;                  // the character it's over (null: opened from the tray menu)
        Rectangle target;         // what it's over, on screen
        Color accent = SystemColors.Highlight;
        bool loading;             // setting text from the file, not typing
        bool reloadAfterEdit;
        double closedAt = -1;

        // the theme's colours
        Color bg, fg, faded, ring, sep;
        float k = 1;              // pixels per 96-dpi unit where it opens
        int W => S(240); int Row => S(28); int Pad => S(6);
        int S(double v) => (int)Math.Round(v * k);

        public bool IsOpen => form != null && form.Visible;

        public Checklist()
        {
            Store.ExternalChange += FileChanged;
            Store.SaveError += failed =>
            {
                if (plus == null) return;
                plus.ForeColor = failed ? Color.IndianRed : faded;
                plus.Tag = failed ? "저장하지 못했어요: " + TodoStore.FilePath : null;
            };
        }

        // Click on a character: open over it, or close if it's already open there.
        public void Toggle(Pet p)
        {
            if (IsOpen && pet == p) { Close(); return; }
            if (!IsOpen && Clock.Now - closedAt < 0.25) return;   // the click that just closed it
            Open(p.Drawn, p);
        }

        public void Open(Rectangle r, Pet p)
        {
            if (pet != null && pet != p) pet.Held = false;
            pet = p; target = r;
            if (p != null) p.Held = true;
            accent = p?.Accent ?? SystemColors.Highlight;
            k = (float)Native.ScaleAt(r.Left + r.Width / 2, r.Top + r.Height / 2);
            Build();
            Theme();
            ReloadRows();
            Layout();
            if (!form.Visible) form.Show();
            form.Activate();
            input.Focus();
            input.SelectionStart = input.TextLength; input.SelectionLength = 0;
        }

        public void Close()
        {
            if (!IsOpen) return;
            form.Hide();
            Store.Flush();
            if (pet != null) pet.Held = false;
            pet = null;
            closedAt = Clock.Now;
        }

        // The character it's over moved (its Chrome window was dragged) or was put away.
        public void PetMoved(Pet p, Rectangle r) { if (IsOpen && pet == p) { target = r; Layout(); } }
        public void PetGone(Pet p) { if (pet == p) Close(); }

        void Build()
        {
            if (form != null) return;
            form = new ChecklistForm();
            form.Deactivate += (o, e) => Close();              // clicked somewhere else
            form.KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); } };
            list = new Panel { AutoScroll = true };
            line = new Panel();
            plus = new Label { Text = "+", TextAlign = ContentAlignment.MiddleCenter };
            input = new TextBox { BorderStyle = BorderStyle.None, PlaceholderText = "적기…" };
            input.TextChanged += (o, e) => { if (!loading) { var s = input.Text; Store.Change(f => f.Draft = s); } };
            input.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Enter && !Ime.Composing(input)) { e.SuppressKeyPress = true; Add(); }
            };
            form.Controls.Add(list); form.Controls.Add(line); form.Controls.Add(plus); form.Controls.Add(input);
            var tip = new ToolTip();
            plus.MouseEnter += (o, e) => tip.SetToolTip(plus, plus.Tag as string);
        }

        // Light or dark, as Windows is set for apps.
        void Theme()
        {
            bool dark = false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                dark = key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { }
            bg = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(251, 251, 251);
            fg = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(31, 31, 31);
            faded = dark ? Color.FromArgb(130, 130, 130) : Color.FromArgb(160, 160, 160);
            ring = dark ? Color.FromArgb(110, 110, 110) : Color.FromArgb(185, 185, 185);
            sep = dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(229, 229, 229);
            form.BackColor = bg; list.BackColor = bg; line.BackColor = sep;
            input.BackColor = bg; input.ForeColor = fg;
            input.Font = new Font("Segoe UI", 14f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            plus.ForeColor = plus.Tag == null ? faded : Color.IndianRed;
            plus.Font = new Font("Segoe UI", 16f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        // sizes in pixels at this screen's scale (k), so the list is the same size on every monitor
        Font TextFont(bool done) => new Font("Segoe UI", 14f * k, done ? FontStyle.Strikeout : FontStyle.Regular, GraphicsUnit.Pixel);

        void ReloadRows()
        {
            list.SuspendLayout();
            foreach (var r in rows) { list.Controls.Remove(r); r.Dispose(); }
            rows.Clear();
            foreach (var item in Store.Data.Items)
            {
                var r = new TodoRow(item.Id);
                r.BackColor = bg; r.Field.BackColor = bg;
                r.Check.BackColor = bg; r.Check.Accent = accent; r.Check.Ring = ring;
                r.Del.ForeColor = faded; r.Del.BackColor = bg;
                r.Del.Font = new Font("Segoe UI", 14f * k, FontStyle.Regular, GraphicsUnit.Pixel);
                Show(r, item);
                string id = item.Id;
                r.Check.Click += (o, e) => ToggleDone(id);
                r.Del.Click += (o, e) => Remove(id);
                r.Field.TextChanged += (o, e) =>
                {
                    if (loading) return;
                    var s = r.Field.Text;
                    Store.Change(f => { var it = f.Items.FirstOrDefault(i => i.Id == id); if (it != null) it.Text = s; });
                };
                r.Field.KeyDown += (o, e) =>
                {
                    if (e.KeyCode != Keys.Enter || Ime.Composing(r.Field)) return;
                    e.SuppressKeyPress = true;
                    // Enter on a line you emptied: that line goes. Otherwise back to the new-line box.
                    bool empty = string.IsNullOrWhiteSpace(r.Field.Text);
                    input.Focus();
                    if (empty) Remove(id);
                };
                r.Field.Enter += (o, e) => r.Del.Visible = true;
                r.Field.Leave += (o, e) =>
                {
                    r.Del.Visible = Hovering(r);
                    var it = Store.Data.Items.FirstOrDefault(i => i.Id == id);
                    if (it != null) Show(r, it);
                    if (reloadAfterEdit) { reloadAfterEdit = false; form.BeginInvoke(new Action(() => { ReloadRows(); Layout(); })); }
                };
                foreach (Control c in new Control[] { r, r.Check, r.Field, r.Del })
                {
                    c.MouseEnter += (o, e) => r.Del.Visible = true;
                    c.MouseLeave += (o, e) => r.Del.Visible = Hovering(r) || r.Field.Focused;
                }
                rows.Add(r);
                list.Controls.Add(r);
            }
            list.ResumeLayout();
            if (!input.Focused) { loading = true; input.Text = Store.Data.Draft; loading = false; }
        }

        static bool Hovering(Control c) => c.ClientRectangle.Contains(c.PointToClient(Cursor.Position));

        // Show an item. Leaves the words alone while you're typing in them.
        void Show(TodoRow r, TodoItem item)
        {
            r.Check.Done = item.Done; r.Check.Invalidate();
            if (r.Field.Focused) return;
            loading = true;
            r.Field.Text = item.Text;
            loading = false;
            r.Field.Font = TextFont(item.Done);
            r.Field.ForeColor = item.Done ? faded : fg;
        }

        void Layout()
        {
            if (form == null) return;
            int n = rows.Count, shown = Math.Min(n, MaxRows);
            int listH = shown * Row, gap = n > 0 ? S(5) : 0;
            int H = Pad + listH + gap + Row + Pad;
            var wa = Screen.FromRectangle(target).WorkingArea;
            // Above what it's over; below when there's no room (a character up in a tab strip).
            int y = target.Top - H - S(2);
            if (y < wa.Top) y = Math.Min(target.Bottom + S(2), wa.Bottom - H);
            int x = Math.Min(Math.Max(target.Left + target.Width / 2 - W / 2, wa.Left + S(6)), wa.Right - W - S(6));
            form.SetBounds(x, y, W, H);

            int scroll = n > MaxRows ? SystemInformation.VerticalScrollBarWidth : 0;
            list.SetBounds(0, Pad, W, listH);
            for (int i = 0; i < n; i++)
            {
                var r = rows[i];
                r.SetBounds(0, i * Row, W - scroll, Row);
                r.Check.SetBounds(S(4), 0, S(26), Row);
                int th = r.Field.PreferredHeight;
                r.Field.SetBounds(S(34), (Row - th) / 2, W - scroll - S(34) - S(30), th);
                r.Del.SetBounds(W - scroll - S(28), (Row - S(20)) / 2, S(20), S(20));
            }
            line.Visible = n > 0;
            line.SetBounds(S(12), Pad + listH + gap / 2, W - S(24), Math.Max(1, S(1)));
            int iy = Pad + listH + gap;
            plus.SetBounds(S(4), iy, S(26), Row);
            int ih = input.PreferredHeight;
            input.SetBounds(S(34), iy + (Row - ih) / 2, W - S(34) - S(12), ih);
        }

        void ToggleDone(string id)
        {
            var it = Store.Data.Items.FirstOrDefault(i => i.Id == id);
            if (it == null) return;
            Store.Change(f => it.Done = !it.Done);
            var r = rows.FirstOrDefault(x => x.Id == id);
            if (r != null) Show(r, it);
        }

        void Remove(string id)
        {
            Store.Change(f => f.Items.RemoveAll(i => i.Id == id));
            ReloadRows(); Layout();
            if (!rows.Any(r => r.Field.Focused)) input.Focus();
        }

        void Add()
        {
            var text = input.Text.Trim();
            if (text.Length == 0) return;
            Store.Change(f => { f.Items.Add(new TodoItem { Id = Guid.NewGuid().ToString().ToUpperInvariant(), Text = text }); f.Draft = ""; });
            loading = true; input.Text = ""; loading = false;
            ReloadRows(); Layout();
            if (rows.Count > 0) list.ScrollControlIntoView(rows[rows.Count - 1]);
        }

        void FileChanged()
        {
            if (!IsOpen) return;
            // Typing in a line: wait until you leave it, so the words under the cursor don't jump.
            if (rows.Any(r => r.Field.Focused)) { reloadAfterEdit = true; return; }
            if (input.Focused && input.Text != Store.Data.Draft)
            {
                // What's in the box right now is what you're typing: keep it.
                var typed = input.Text;
                Store.Change(f => f.Draft = typed);
            }
            ReloadRows(); Layout();
        }
    }
}
