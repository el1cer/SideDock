using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct THUMBPROPS
    {
        public int Flags; public RECT Dest; public RECT Source; public byte Opacity;
        public bool Visible; public bool ClientOnly;
    }
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMBPROPS p);

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
}

class Docked
{
    public IntPtr Hwnd;
    public Native.RECT Orig;
    public bool Right;
    public Rectangle Bounds;
    public Rectangle Work;
    public bool Shown;
    public DateTime LastInside;
    public bool Pinned;
    public IntPtr Prev;
    public ThumbForm Thumb;
    public IntPtr ThumbId;
}

class ThumbForm : Form
{
    public ThumbForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(60, 60, 60);
        Opacity = 0.85;
    }
    public bool PointLeft;
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int cx = ClientSize.Width / 2, cy = ClientSize.Height / 2;
        int dx = Math.Max(2, ClientSize.Width / 4), dy = dx * 2;
        int tip = PointLeft ? cx - dx : cx + dx, back = PointLeft ? cx + dx : cx - dx;
        using (Pen pen = new Pen(Color.White, 2f))
            g.DrawLines(pen, new Point[] { new Point(back, cy - dy), new Point(tip, cy), new Point(back, cy + dy) });
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008;
            return cp;
        }
    }
}

class SideDockApp : Form
{
    const int EdgePixels = 2;
    const int HoverDelayMs = 150;
    const int HideDelayMs = 400;
    const int SlideSteps = 8;
    const int SlideStepMs = 8;
    const int TabWidthDivisor = 140;
    const int TabHeightDivisor = 10;

    const int HK_RIGHT = 1, HK_LEFT = 2, HK_UNDOCK = 3;

    readonly List<Docked> docked = new List<Docked>();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    Docked hoverCand;
    DateTime hoverSince;

    static readonly Keys[] DefaultHotkeys = {
        Keys.Control | Keys.Alt | Keys.Right, Keys.Control | Keys.Alt | Keys.Left, Keys.Control | Keys.Alt | Keys.U };
    static readonly string[] HotkeyNames = { "Dock to right edge", "Dock to left edge", "Undock" };
    Keys[] hotkeys = (Keys[])DefaultHotkeys.Clone();

    string HotkeyText(int i) { return new KeysConverter().ConvertToString(hotkeys[i]); }

    void LoadHotkeys()
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\SideDock"))
            for (int i = 0; i < hotkeys.Length; i++)
            {
                object v = k.GetValue("Hotkey" + (i + 1));
                if (v is int && (int)v != 0) hotkeys[i] = (Keys)(int)v;
            }
    }

    void SaveHotkeys()
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\SideDock"))
            for (int i = 0; i < hotkeys.Length; i++) k.SetValue("Hotkey" + (i + 1), (int)hotkeys[i]);
    }

    bool RegisterHotkeys()
    {
        bool ok = true;
        for (int i = 0; i < hotkeys.Length; i++)
        {
            Keys kd = hotkeys[i];
            uint mods = 0;
            if ((kd & Keys.Alt) != 0) mods |= 0x1;
            if ((kd & Keys.Control) != 0) mods |= 0x2;
            if ((kd & Keys.Shift) != 0) mods |= 0x4;
            ok &= Native.RegisterHotKey(Handle, i + 1, mods, (uint)(kd & Keys.KeyCode));
        }
        return ok;
    }

    void UnregisterHotkeys()
    {
        for (int i = 0; i < hotkeys.Length; i++) Native.UnregisterHotKey(Handle, i + 1);
    }

    bool dialogOpen;

    void ChangeHotkeys()
    {
        if (dialogOpen) return;
        dialogOpen = true;
        UnregisterHotkeys();
        Keys[] edit = (Keys[])hotkeys.Clone();
        Keys[] old = (Keys[])hotkeys.Clone();
        using (Form f = new Form())
        {
            f.Text = "SideDock shortcuts";
            f.Icon = AppIcon();
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false; f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.TopMost = true;
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.ClientSize = new Size(380, 190);

            Label hint = new Label();
            hint.Text = "Click a box and press the new shortcut (must include Ctrl, Alt or Shift).";
            hint.SetBounds(12, 10, 356, 32);
            f.Controls.Add(hint);

            KeysConverter conv = new KeysConverter();
            TextBox[] boxes = new TextBox[edit.Length];
            for (int i = 0; i < edit.Length; i++)
            {
                int idx = i;
                Label l = new Label();
                l.Text = HotkeyNames[i];
                l.SetBounds(12, 50 + i * 30, 140, 20);
                TextBox t = new TextBox();
                t.ReadOnly = true;
                t.Text = conv.ConvertToString(edit[i]);
                t.SetBounds(160, 47 + i * 30, 208, 22);
                t.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    e.SuppressKeyPress = true;
                    Keys code = e.KeyCode;
                    if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu ||
                        code == Keys.LWin || code == Keys.RWin || code == Keys.None) return;
                    if ((e.Modifiers & (Keys.Control | Keys.Alt | Keys.Shift)) == 0) return;
                    edit[idx] = e.KeyData;
                    t.Text = conv.ConvertToString(e.KeyData);
                };
                boxes[i] = t;
                f.Controls.Add(l);
                f.Controls.Add(t);
            }

            Button reset = new Button();
            reset.Text = "Defaults";
            reset.SetBounds(12, 150, 80, 28);
            reset.Click += delegate
            {
                for (int i = 0; i < edit.Length; i++) { edit[i] = DefaultHotkeys[i]; boxes[i].Text = conv.ConvertToString(edit[i]); }
            };
            Button okB = new Button();
            okB.Text = "OK"; okB.DialogResult = DialogResult.OK;
            okB.SetBounds(202, 150, 80, 28);
            Button cancel = new Button();
            cancel.Text = "Cancel"; cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(288, 150, 80, 28);
            f.Controls.Add(reset); f.Controls.Add(okB); f.Controls.Add(cancel);
            f.CancelButton = cancel;

            if (f.ShowDialog() == DialogResult.OK)
            {
                if (edit[0] == edit[1] || edit[0] == edit[2] || edit[1] == edit[2])
                {
                    MessageBox.Show("Each action needs a different shortcut. Nothing was changed.", "SideDock");
                }
                else
                {
                    hotkeys = edit;
                    if (RegisterHotkeys()) SaveHotkeys();
                    else
                    {
                        UnregisterHotkeys();
                        hotkeys = old;
                        MessageBox.Show("One of those shortcuts is already used by another program. Nothing was changed.", "SideDock");
                    }
                    dialogOpen = false;
                    if (hotkeys != old) return;
                }
            }
        }
        RegisterHotkeys();
        dialogOpen = false;
    }

    public SideDockApp()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        IntPtr h = Handle;

        LoadHotkeys();
        bool ok = RegisterHotkeys();

        tray.Icon = AppIcon();
        tray.Text = "SideDock";
        tray.Visible = true;
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Opening += delegate { BuildMenu(menu); };
        BuildMenu(menu);
        tray.ContextMenuStrip = menu;

        tray.ShowBalloonTip(4000, "SideDock is running",
            ok ? HotkeyText(0) + " / " + HotkeyText(1) + " docks the active window. " + HotkeyText(2) + " undocks."
               : "Some hotkeys are already used by another program. Docking may not work.",
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);

#if MESSAGES
        ShowGreeting();
#endif

        timer.Interval = 30;
        timer.Tick += delegate { Tick(); };
        timer.Start();

        Application.ApplicationExit += delegate { UndockAll(); tray.Visible = false; };
    }

    static Icon AppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return SystemIcons.Application; }
    }

    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            int id = m.WParam.ToInt32();
            if (id == HK_RIGHT) DockForeground(true);
            else if (id == HK_LEFT) DockForeground(false);
            else if (id == HK_UNDOCK) UndockCurrent();
        }
        base.WndProc(ref m);
    }

    void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        ToolStripItem info = menu.Items.Add("Dock: " + HotkeyText(0) + " / " + HotkeyText(1) + "   Undock: " + HotkeyText(2));
        info.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        foreach (Docked d in docked.ToArray())
        {
            Docked item = d;
            string label = "Undock: " + Title(item.Hwnd) + (item.Right ? "  (right)" : "  (left)");
            menu.Items.Add(label, null, delegate { Undock(item); });
        }
        if (docked.Count > 0)
        {
            menu.Items.Add("Undock all", null, delegate { UndockAll(); });
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add("Change shortcuts...", null, delegate { ChangeHotkeys(); });
        ToolStripMenuItem startup = new ToolStripMenuItem("Start with Windows");
        startup.Checked = StartupEnabled();
        startup.Click += delegate { SetStartup(!StartupEnabled()); };
        menu.Items.Add(startup);
        menu.Items.Add("Exit", null, delegate { Application.Exit(); });
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool StartupEnabled()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("SideDock") != null;
    }

    static void SetStartup(bool on)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("SideDock", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("SideDock", false);
        }
    }

#if MESSAGES
    void ShowGreeting()
    {
        string[] msgs = {
            "Te iubesc",
            "Bafta mai",
            "Spor",
            "Multumesc printul meu pe armasar alb",
            "GOGOOOO"
        };
        int last = -1, i;
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\SideDock"))
        {
            object v = k.GetValue("LastGreeting");
            if (v is int) last = (int)v;
            Random rnd = new Random();
            do { i = rnd.Next(msgs.Length); } while (i == last);
            k.SetValue("LastGreeting", i);
        }
        string msg = msgs[i];
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 5000;
        t.Tick += delegate { t.Stop(); t.Dispose(); tray.ShowBalloonTip(5000, "SideDock", msg, ToolTipIcon.None); };
        t.Start();
    }
#endif

    static string Title(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(256);
        Native.GetWindowText(h, sb, sb.Capacity);
        string s = sb.ToString();
        if (s.Length == 0) s = "(untitled)";
        if (s.Length > 40) s = s.Substring(0, 40) + "...";
        return s;
    }

    Docked Find(IntPtr h)
    {
        foreach (Docked d in docked) if (d.Hwnd == h) return d;
        return null;
    }

    void DockForeground(bool right)
    {
        IntPtr h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero || h == Handle) return;

        StringBuilder cls = new StringBuilder(64);
        Native.GetClassName(h, cls, cls.Capacity);
        string c = cls.ToString();
        if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return;

        Docked existing = Find(h);
        if (existing != null) Undock(existing);

        if (Native.IsZoomed(h) || Native.IsIconic(h)) Native.ShowWindow(h, 9);

        Docked d = new Docked();
        d.Hwnd = h;
        d.Right = right;
        Native.GetWindowRect(h, out d.Orig);
        Screen s = Screen.FromHandle(h);
        d.Bounds = s.Bounds;
        d.Work = s.WorkingArea;
        d.Shown = true;
        docked.Add(d);
        Hide(d);

        Native.RECT check;
        Native.GetWindowRect(h, out check);
        if (check.Left == d.Orig.Left && check.Top == d.Orig.Top)
        {
            docked.Remove(d);
            tray.ShowBalloonTip(4000, "SideDock", "Could not move that window. If it runs as administrator, run SideDock as administrator too.", ToolTipIcon.Warning);
            return;
        }
        d.Thumb = new ThumbForm();
        d.Thumb.PointLeft = right;
        LayoutThumbs();
    }

    void DropThumb(Docked d)
    {
        if (d.ThumbId != IntPtr.Zero) { Native.DwmUnregisterThumbnail(d.ThumbId); d.ThumbId = IntPtr.Zero; }
        if (d.Thumb != null) { d.Thumb.Dispose(); d.Thumb = null; }
    }

    void LayoutThumbs()
    {
        foreach (Docked d in docked)
        {
            if (d.Thumb == null) continue;
            List<Docked> g = Group(d);
            int zoneH = d.Bounds.Height / g.Count, idx = g.IndexOf(d);
            int fw = Math.Max(10, d.Bounds.Width / TabWidthDivisor);
            int fh = Math.Min(Math.Max(60, d.Bounds.Height / TabHeightDivisor), zoneH - 16);
            int x = d.Right ? d.Bounds.Right - fw : d.Bounds.Left;
            int y = d.Bounds.Top + zoneH * idx + zoneH / 2 - fh / 2;
            d.Thumb.Bounds = new Rectangle(x, y, fw, fh);
            d.Thumb.Visible = !d.Shown;
            d.Thumb.Invalidate();
        }
    }

    void UndockCurrent()
    {
        Docked d = Find(Native.GetForegroundWindow());
        if (d == null)
            foreach (Docked x in docked) if (x.Shown) { d = x; break; }
        if (d != null) Undock(d);
    }

    void Undock(Docked d)
    {
        docked.Remove(d);
        if (hoverCand == d) hoverCand = null;
        DropThumb(d);
        LayoutThumbs();
        if (!Native.IsWindow(d.Hwnd)) return;
        Native.SetWindowPos(d.Hwnd, Native.HWND_NOTOPMOST, d.Orig.Left, d.Orig.Top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void UndockAll()
    {
        foreach (Docked d in docked.ToArray()) Undock(d);
    }

    List<Docked> Group(Docked d)
    {
        List<Docked> g = new List<Docked>();
        foreach (Docked x in docked)
            if (x.Right == d.Right && x.Bounds == d.Bounds) g.Add(x);
        return g;
    }

    static void GetRects(IntPtr h, out Native.RECT r, out Native.RECT frame)
    {
        Native.GetWindowRect(h, out r);
        if (Native.DwmGetWindowAttribute(h, 9, out frame, 16) != 0) frame = r;
    }

    void Show(Docked d)
    {
        if (d.Thumb != null) d.Thumb.Visible = false;
        Native.RECT r, f;
        GetRects(d.Hwnd, out r, out f);
        int w = r.Right - r.Left, hgt = r.Bottom - r.Top;
        int insL = f.Left - r.Left, insR = r.Right - f.Right, insT = f.Top - r.Top, insB = r.Bottom - f.Bottom;

        List<Docked> g = Group(d);
        int n = g.Count, idx = g.IndexOf(d);
        int zoneH = d.Bounds.Height / n;
        int y = d.Bounds.Top + zoneH * idx + zoneH / 2 - hgt / 2;
        int maxY = d.Work.Bottom - hgt + insB, minY = d.Work.Top - insT;
        if (y > maxY) y = maxY;
        if (y < minY) y = minY;

        int from = d.Right ? d.Bounds.Right : d.Bounds.Left - w;
        int to = d.Right ? d.Bounds.Right - w + insR : d.Bounds.Left - insL;
        Slide(d.Hwnd, from, to, y, Native.HWND_TOPMOST);
        d.Shown = true;
        d.Pinned = false;
        d.LastInside = DateTime.Now;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg != d.Hwnd)
        {
            d.Prev = (fg != Handle && Find(fg) == null) ? fg : IntPtr.Zero;
            Focus(d.Hwnd);
        }
    }

    static void Focus(IntPtr h)
    {
        uint pid;
        IntPtr fg = Native.GetForegroundWindow();
        uint other = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out pid) : 0;
        uint me = Native.GetCurrentThreadId();
        bool attached = other != 0 && other != me && Native.AttachThreadInput(me, other, true);
        Native.SetForegroundWindow(h);
        Native.BringWindowToTop(h);
        if (attached) Native.AttachThreadInput(me, other, false);
    }

    bool OnTitle(Docked d, Native.POINT p)
    {
        IntPtr under = Native.WindowFromPoint(p);
        if (under == IntPtr.Zero || Native.GetAncestor(under, 2) != d.Hwnd) return false;
        IntPtr res;
        IntPtr lp = new IntPtr((p.Y << 16) | (p.X & 0xFFFF));
        if (Native.SendMessageTimeout(d.Hwnd, 0x0084, IntPtr.Zero, lp, 2, 100, out res) != IntPtr.Zero)
            return res.ToInt64() == 2;
        Native.RECT r, f;
        GetRects(d.Hwnd, out r, out f);
        return p.Y >= f.Top && p.Y < f.Top + 32;
    }

    void Hide(Docked d)
    {
        Native.RECT r;
        Native.GetWindowRect(d.Hwnd, out r);
        int w = r.Right - r.Left;
        int to = d.Right ? d.Bounds.Right : d.Bounds.Left - w;
        IntPtr fg = Native.GetForegroundWindow();
        bool hadFocus = fg == d.Hwnd || (fg != IntPtr.Zero && Native.GetAncestor(fg, 3) == d.Hwnd);
        Slide(d.Hwnd, r.Left, to, r.Top, Native.HWND_NOTOPMOST);
        d.Shown = false;
        d.Pinned = false;
        if (hadFocus && d.Prev != IntPtr.Zero && Native.IsWindow(d.Prev) && !Native.IsIconic(d.Prev)) Focus(d.Prev);
        d.Prev = IntPtr.Zero;
        if (d.Thumb != null) d.Thumb.Visible = true;
    }

    static void Slide(IntPtr h, int x0, int x1, int y, IntPtr after)
    {
        for (int i = 1; i <= SlideSteps; i++)
        {
            int x = x0 + (x1 - x0) * i / SlideSteps;
            Native.SetWindowPos(h, after, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (i < SlideSteps) System.Threading.Thread.Sleep(SlideStepMs);
        }
    }

    bool CursorOver(Docked d, Native.POINT p)
    {
        Native.RECT r;
        Native.GetWindowRect(d.Hwnd, out r);
        if (p.X >= r.Left - 10 && p.X < r.Right + 10 && p.Y >= r.Top - 10 && p.Y < r.Bottom + 10) return true;
        IntPtr under = Native.WindowFromPoint(p);
        return under != IntPtr.Zero && Native.GetAncestor(under, 3) == d.Hwnd;
    }

    Docked FindTarget(Native.POINT p)
    {
        foreach (Docked d in docked)
            if (d.Thumb != null && d.Thumb.Visible && d.Thumb.Bounds.Contains(p.X, p.Y)) return d;
        foreach (Docked d in docked)
        {
            Rectangle b = d.Bounds;
            if (p.Y < b.Top || p.Y >= b.Bottom) continue;
            bool atEdge = d.Right
                ? (p.X >= b.Right - EdgePixels && p.X < b.Right)
                : (p.X < b.Left + EdgePixels && p.X >= b.Left);
            if (!atEdge) continue;
            List<Docked> g = Group(d);
            int idx = (p.Y - b.Top) * g.Count / b.Height;
            if (idx >= g.Count) idx = g.Count - 1;
            return g[idx];
        }
        return null;
    }

    bool wasDown;

    void Tick()
    {
        foreach (Docked d in docked.ToArray())
            if (!Native.IsWindow(d.Hwnd)) { docked.Remove(d); if (hoverCand == d) hoverCand = null; DropThumb(d); LayoutThumbs(); }
        if (docked.Count == 0) return;

        Native.POINT p;
        if (!Native.GetCursorPos(out p)) return;
        DateTime now = DateTime.Now;
        bool mouseDown = (Native.GetAsyncKeyState(1) & 0x8000) != 0;

        bool overShown = false;
        foreach (Docked d in docked)
            if (d.Shown && CursorOver(d, p)) { overShown = true; d.LastInside = now; }

        if (mouseDown && !wasDown)
            foreach (Docked d in docked)
                if (d.Shown && OnTitle(d, p)) d.Pinned = !d.Pinned;
        wasDown = mouseDown;

        Docked target = (!overShown && !mouseDown) ? FindTarget(p) : null;
        if (target != hoverCand) { hoverCand = target; hoverSince = now; }

        if (target != null && !target.Shown && (now - hoverSince).TotalMilliseconds >= HoverDelayMs)
        {
            foreach (Docked o in Group(target)) if (o.Shown && !o.Pinned) Hide(o);
            Show(target);
        }
        if (target != null && target.Shown) target.LastInside = now;

        if (!mouseDown)
            foreach (Docked d in docked.ToArray())
                if (d.Shown && !d.Pinned && d != target && (now - d.LastInside).TotalMilliseconds > HideDelayMs) Hide(d);
    }

    [STAThread]
    static void Main()
    {
        bool isNew;
        using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "SideDock_SingleInstance", out isNew))
        {
            if (!isNew) return;
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.Run(new SideDockApp());
        }
    }
}
