// RefreshLock - keeps a monitor locked at a chosen refresh rate.
// Build: csc /target:winexe /win32icon:app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll RefreshLock.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("RefreshLock")]
[assembly: System.Reflection.AssemblyProduct("RefreshLock")]
[assembly: System.Reflection.AssemblyDescription("Locks your monitor's refresh rate")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace RefreshLock
{
    // ---------------------------------------------------------------- Win32
    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
            public int dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettingsW(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsExW(string dev, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayDevicesW(string dev, int i, ref DISPLAY_DEVICE dd, int flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
        public const int CDS_UPDATEREGISTRY = 0x1;
        public const int ATTACHED = 0x1, PRIMARY = 0x4;

        public static DEVMODE NewMode() { var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return d; }
    }

    class DisplayInfo
    {
        public string Device;   // \\.\DISPLAY1
        public string Label;    // "1 · Generic PnP Monitor (primary)"
        public override string ToString() { return Label; }
    }

    static class Displays
    {
        public static List<DisplayInfo> List()
        {
            var list = new List<DisplayInfo>();
            for (int i = 0; ; i++)
            {
                var dd = new Native.DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf(dd);
                if (!Native.EnumDisplayDevicesW(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & Native.ATTACHED) == 0) continue;
                var mon = new Native.DISPLAY_DEVICE(); mon.cb = Marshal.SizeOf(mon);
                string monName = Native.EnumDisplayDevicesW(dd.DeviceName, 0, ref mon, 0) ? mon.DeviceString : dd.DeviceString;
                string num = dd.DeviceName.Replace(@"\\.\DISPLAY", "");
                bool primary = (dd.StateFlags & Native.PRIMARY) != 0;
                list.Add(new DisplayInfo { Device = dd.DeviceName, Label = num + "  ·  " + monName + (primary ? "  (primary)" : "") });
            }
            return list;
        }

        public static bool Current(string dev, out Native.DEVMODE dm)
        {
            dm = Native.NewMode();
            return Native.EnumDisplaySettingsW(dev, Native.ENUM_CURRENT_SETTINGS, ref dm);
        }

        public static List<int> Rates(string dev, int w, int h)
        {
            var set = new SortedSet<int>();
            for (int i = 0; ; i++)
            {
                var dm = Native.NewMode();
                if (!Native.EnumDisplaySettingsW(dev, i, ref dm)) break;
                if (dm.dmPelsWidth == w && dm.dmPelsHeight == h && dm.dmDisplayFrequency > 1) set.Add(dm.dmDisplayFrequency);
            }
            return set.Reverse().ToList();
        }

        public static int Apply(string dev, int hz)
        {
            Native.DEVMODE dm;
            if (!Current(dev, out dm)) return -99;
            dm.dmDisplayFrequency = hz;
            dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY;
            return Native.ChangeDisplaySettingsExW(dev, ref dm, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
        }
    }

    // ---------------------------------------------------------------- Settings
    class Settings
    {
        public string Device = "";
        public int TargetHz = 240;
        public int IntervalMs = 1000;
        public bool Enforce = true;
        public bool CloseToTray = true;
        public bool StartMinimized = false;
        public bool Notify = true;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RefreshLock"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('='); if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "Device": s.Device = v; break;
                        case "TargetHz": int.TryParse(v, out s.TargetHz); break;
                        case "IntervalMs": int.TryParse(v, out s.IntervalMs); break;
                        case "Enforce": s.Enforce = v == "1"; break;
                        case "CloseToTray": s.CloseToTray = v == "1"; break;
                        case "StartMinimized": s.StartMinimized = v == "1"; break;
                        case "Notify": s.Notify = v == "1"; break;
                    }
                }
            }
            catch { }
            if (s.IntervalMs < 250) s.IntervalMs = 1000;
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[] {
                    "Device=" + Device, "TargetHz=" + TargetHz, "IntervalMs=" + IntervalMs,
                    "Enforce=" + (Enforce ? 1 : 0), "CloseToTray=" + (CloseToTray ? 1 : 0),
                    "StartMinimized=" + (StartMinimized ? 1 : 0), "Notify=" + (Notify ? 1 : 0) });
            }
            catch { }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool StartupEnabled
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("RefreshLock") != null; }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue("RefreshLock", "\"" + Application.ExecutablePath + "\" --tray");
                        else k.DeleteValue("RefreshLock", false);
                    }
                }
                catch { }
            }
        }
    }

    // ---------------------------------------------------------------- Theme + controls
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(17, 19, 24);
        public static readonly Color Card = Color.FromArgb(26, 29, 36);
        public static readonly Color Field = Color.FromArgb(34, 38, 47);
        public static readonly Color FieldHover = Color.FromArgb(42, 47, 58);
        public static readonly Color Border = Color.FromArgb(44, 49, 60);
        public static readonly Color Text = Color.FromArgb(230, 232, 236);
        public static readonly Color Muted = Color.FromArgb(139, 147, 161);
        public static readonly Color Accent = Color.FromArgb(34, 211, 238);
        public static readonly Color Accent2 = Color.FromArgb(124, 92, 255);
        public static readonly Color Good = Color.FromArgb(61, 220, 151);
        public static readonly Color Warn = Color.FromArgb(245, 165, 36);
        public static readonly Color Bad = Color.FromArgb(244, 92, 92);

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    class Card : Panel
    {
        public Card() { DoubleBuffered = true; BackColor = Theme.Bg; ResizeRedraw = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpiScale(this);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10 * s))
            using (var b = new SolidBrush(Theme.Card)) using (var pen = new Pen(Theme.Border))
            { g.FillPath(b, p); g.DrawPath(pen, p); }
        }
        public static float DeviceDpiScale(Control c) { using (var g = c.CreateGraphics()) return g.DpiX / 96f; }
    }

    // Big current-rate readout
    class Readout : Control
    {
        public int Hz; public string Res = ""; public string Status = "Starting…"; public Color StatusColor = Theme.Muted;
        public Readout() { DoubleBuffered = true; SetStyle(ControlStyles.SupportsTransparentBackColor, true); BackColor = Theme.Card; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float s = Card.DeviceDpiScale(this);
            using (var small = new Font("Segoe UI Semibold", 8.5f))
            using (var big = new Font("Segoe UI Semibold", 34f))
            using (var unit = new Font("Segoe UI", 14f))
            using (var pill = new Font("Segoe UI Semibold", 9f))
            using (var muted = new SolidBrush(Theme.Muted))
            using (var text = new SolidBrush(Theme.Text))
            {
                g.DrawString("CURRENT REFRESH RATE", small, muted, 0, 0);
                string n = Hz > 0 ? Hz.ToString() : "--";
                var typo = StringFormat.GenericTypographic;
                var nSize = g.MeasureString(n, big, 1000, typo);
                float y = 20 * s;
                using (var grad = new LinearGradientBrush(new RectangleF(0, y, Math.Max(1, nSize.Width), nSize.Height), Theme.Accent, Theme.Accent2, 0f))
                    g.DrawString(n, big, grad, 0, y, typo);
                // baseline-align the unit with the number
                float baseN = y + big.FontFamily.GetCellAscent(big.Style) * big.Size / big.FontFamily.GetEmHeight(big.Style) * g.DpiY / 72f;
                float ascU = unit.FontFamily.GetCellAscent(unit.Style) * unit.Size / unit.FontFamily.GetEmHeight(unit.Style) * g.DpiY / 72f;
                g.DrawString("Hz", unit, muted, nSize.Width + 6 * s, baseN - ascU, typo);
                g.DrawString(Res, small, muted, 0, y + nSize.Height + 2 * s);

                // status pill, right side
                var ts = g.MeasureString(Status, pill);
                float pw = ts.Width + 26 * s, ph = 26 * s;
                var pr = new RectangleF(Width - pw - 1, y + 14 * s, pw, ph);
                using (var path = Theme.Round(pr, ph / 2))
                using (var fill = new SolidBrush(Color.FromArgb(38, StatusColor)))
                using (var pen = new Pen(Color.FromArgb(110, StatusColor)))
                { g.FillPath(fill, path); g.DrawPath(pen, path); }
                using (var dot = new SolidBrush(StatusColor))
                    g.FillEllipse(dot, pr.X + 10 * s, pr.Y + ph / 2 - 3.5f * s, 7 * s, 7 * s);
                using (var tb = new SolidBrush(StatusColor))
                    g.DrawString(Status, pill, tb, pr.X + 20 * s, pr.Y + (ph - ts.Height) / 2);
            }
        }
    }

    // Dark owner-drawn dropdown
    class DarkCombo : ComboBox
    {
        public DarkCombo()
        {
            DrawMode = DrawMode.OwnerDrawFixed; DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Field; ForeColor = Theme.Text; Font = new Font("Segoe UI", 10f); IntegralHeight = false;
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool sel = (e.State & DrawItemState.Selected) != 0 && !edit;
            using (var b = new SolidBrush(sel ? Theme.FieldHover : Theme.Field)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
                TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font,
                    new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
                    sel ? Theme.Accent : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    class Toggle : CheckBox
    {
        public Toggle(string text)
        {
            Text = text; AutoSize = false; ForeColor = Theme.Text; Font = new Font("Segoe UI", 9.5f); Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Card);
            float s = Card.DeviceDpiScale(this);
            float w = 34 * s, h = 18 * s, y = (Height - h) / 2;
            var r = new RectangleF(1, y, w, h);
            using (var p = Theme.Round(r, h / 2))
            using (var b = new SolidBrush(Checked ? Theme.Accent : Theme.Field))
            using (var pen = new Pen(Checked ? Theme.Accent : Theme.Border))
            { g.FillPath(b, p); g.DrawPath(pen, p); }
            float k = h - 6 * s, kx = Checked ? r.Right - k - 3 * s : r.X + 3 * s;
            using (var kb = new SolidBrush(Checked ? Theme.Bg : Theme.Muted)) g.FillEllipse(kb, kx, y + 3 * s, k, k);
            TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(w + 12 * s), 0, Width - (int)(w + 12 * s), Height),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    class BigButton : Button
    {
        public bool Active;
        public BigButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 11f);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        bool hover;
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Theme.Bg);
            float s = Card.DeviceDpiScale(this);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = Theme.Round(r, 10 * s))
            {
                if (Active)
                {
                    using (var b = new SolidBrush(hover ? Theme.FieldHover : Theme.Field)) g.FillPath(b, p);
                    using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
                }
                else
                {
                    using (var b = new LinearGradientBrush(r, Theme.Accent, Theme.Accent2, 0f))
                    { if (hover) b.GammaCorrection = true; g.FillPath(b, p); }
                }
            }
            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), Active ? Theme.Text : Theme.Bg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // ---------------------------------------------------------------- Main window
    class MainForm : Form
    {
        Settings cfg;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        Readout readout; DarkCombo cbDisplay, cbRate, cbInterval;
        BigButton btn; Toggle tTray, tMin, tStartup, tNotify; Label lblLog;
        ToolStripMenuItem miEnforce;
        bool allowShow, reallyExit, loading;
        int corrections; string lastCorrection = "";
        DateTime lastFailNotice = DateTime.MinValue;
        static readonly int[] Intervals = { 250, 500, 1000, 2000, 5000 };

        public MainForm(bool startHidden)
        {
            cfg = Settings.Load();
            allowShow = !(startHidden || cfg.StartMinimized);
            Icon appIcon = null;
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Text = "RefreshLock"; Icon = appIcon; BackColor = Theme.Bg; ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(440, 546);

            BuildUi();
            BuildTray(appIcon);
            LoadDisplays();

            timer.Tick += (s, e) => Check(false);
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            ApplyEnforceState();
        }

        // -------- layout
        Label MakeLabel(string t, int x, int y, Control parent, Color c, float size = 9f, bool bold = false)
        {
            var l = new Label { Text = t, Left = x, Top = y, AutoSize = true, ForeColor = c, BackColor = Color.Transparent,
                Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size) };
            parent.Controls.Add(l); return l;
        }

        void BuildUi()
        {
            // header
            var logo = new PictureBox { Left = 20, Top = 18, Width = 36, Height = 36, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Bg };
            try { logo.Image = new Icon(Icon, 48, 48).ToBitmap(); } catch { }
            Controls.Add(logo);
            MakeLabel("RefreshLock", 64, 14, this, Theme.Text, 15f, true);
            MakeLabel("Keeps your monitor at the rate you choose", 66, 40, this, Theme.Muted, 9f);

            // readout card
            var c1 = new Card { Left = 20, Top = 72, Width = 400, Height = 122 };
            Controls.Add(c1);
            readout = new Readout { Left = 18, Top = 14, Width = 364, Height = 100 };
            c1.Controls.Add(readout);

            // settings card
            var c2 = new Card { Left = 20, Top = 206, Width = 400, Height = 158 };
            Controls.Add(c2);
            MakeLabel("Monitor", 18, 16, c2, Theme.Muted, 9f);
            cbDisplay = new DarkCombo { Left = 18, Top = 36, Width = 364 };
            c2.Controls.Add(cbDisplay);
            MakeLabel("Lock at", 18, 80, c2, Theme.Muted, 9f);
            cbRate = new DarkCombo { Left = 18, Top = 100, Width = 172 };
            c2.Controls.Add(cbRate);
            MakeLabel("Check every", 210, 80, c2, Theme.Muted, 9f);
            cbInterval = new DarkCombo { Left = 210, Top = 100, Width = 172 };
            foreach (var ms in Intervals) cbInterval.Items.Add(ms < 1000 ? (ms / 1000.0).ToString("0.##") + " s" : (ms / 1000) + " s");
            c2.Controls.Add(cbInterval);

            cbDisplay.SelectedIndexChanged += (s, e) => { if (loading) return; var d = cbDisplay.SelectedItem as DisplayInfo; if (d != null) { cfg.Device = d.Device; cfg.Save(); } LoadRates(); Check(false); };
            cbRate.SelectedIndexChanged += (s, e) => { if (loading || cbRate.SelectedItem == null) return; cfg.TargetHz = ((RateItem)cbRate.SelectedItem).Hz; cfg.Save(); Check(false); };
            cbInterval.SelectedIndexChanged += (s, e) => { if (loading) return; cfg.IntervalMs = Intervals[Math.Max(0, cbInterval.SelectedIndex)]; timer.Interval = cfg.IntervalMs; cfg.Save(); };

            // button
            btn = new BigButton { Left = 20, Top = 376, Width = 400, Height = 46 };
            btn.Click += (s, e) => { cfg.Enforce = !cfg.Enforce; cfg.Save(); ApplyEnforceState(); };
            Controls.Add(btn);

            // options card
            var c3 = new Card { Left = 20, Top = 434, Width = 400, Height = 78 };
            Controls.Add(c3);
            tTray = new Toggle("Close to tray") { Left = 16, Top = 10, Width = 180, Height = 26, Checked = cfg.CloseToTray };
            tMin = new Toggle("Start minimized") { Left = 206, Top = 10, Width = 180, Height = 26, Checked = cfg.StartMinimized };
            tStartup = new Toggle("Launch at login") { Left = 16, Top = 42, Width = 180, Height = 26, Checked = Settings.StartupEnabled };
            tNotify = new Toggle("Notify on fix") { Left = 206, Top = 42, Width = 180, Height = 26, Checked = cfg.Notify };
            c3.Controls.AddRange(new Control[] { tTray, tMin, tStartup, tNotify });
            tTray.CheckedChanged += (s, e) => { cfg.CloseToTray = tTray.Checked; cfg.Save(); };
            tMin.CheckedChanged += (s, e) => { cfg.StartMinimized = tMin.Checked; cfg.Save(); };
            tStartup.CheckedChanged += (s, e) => { Settings.StartupEnabled = tStartup.Checked; };
            tNotify.CheckedChanged += (s, e) => { cfg.Notify = tNotify.Checked; cfg.Save(); };

            lblLog = MakeLabel("No corrections yet", 22, 520, this, Theme.Muted, 8.5f);
        }

        // rate combo items carry the Hz value
        class RateItem { public int Hz; public bool Offered; public override string ToString() { return Hz + " Hz" + (Offered ? "" : "  (not offered now)"); } }

        void BuildTray(Icon icon)
        {
            tray.Icon = icon ?? SystemIcons.Application;
            tray.Text = "RefreshLock";
            tray.Visible = true;
            var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
            menu.ForeColor = Theme.Text; menu.BackColor = Theme.Card;
            var miOpen = new ToolStripMenuItem("Open RefreshLock", null, (s, e) => ShowFromTray()) { Font = new Font("Segoe UI Semibold", 9f), ForeColor = Theme.Text };
            miEnforce = new ToolStripMenuItem("Enforcing", null, (s, e) => { cfg.Enforce = !cfg.Enforce; cfg.Save(); ApplyEnforceState(); }) { ForeColor = Theme.Text };
            var miFix = new ToolStripMenuItem("Force now", null, (s, e) => Check(true)) { ForeColor = Theme.Text };
            var miExit = new ToolStripMenuItem("Exit", null, (s, e) => { reallyExit = true; Close(); }) { ForeColor = Theme.Text };
            menu.Items.AddRange(new ToolStripItem[] { miOpen, miEnforce, miFix, new ToolStripSeparator(), miExit });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { if (Visible) Hide(); else ShowFromTray(); } };
        }

        class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
            public override Color MenuItemSelected { get { return Theme.FieldHover; } }
            public override Color MenuItemBorder { get { return Theme.FieldHover; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Card; } }
            public override Color CheckBackground { get { return Theme.Field; } }
            public override Color CheckSelectedBackground { get { return Theme.FieldHover; } }
        }

        // -------- data
        string Dev { get { var d = cbDisplay.SelectedItem as DisplayInfo; return d != null ? d.Device : null; } }

        void LoadDisplays()
        {
            loading = true;
            var list = Displays.List();
            cbDisplay.Items.Clear();
            foreach (var d in list) cbDisplay.Items.Add(d);
            int idx = list.FindIndex(d => d.Device == cfg.Device);
            if (idx < 0) idx = list.FindIndex(d => d.Label.EndsWith("(primary)"));
            if (idx < 0 && list.Count > 0) idx = 0;
            if (idx >= 0) { cbDisplay.SelectedIndex = idx; cfg.Device = list[idx].Device; }
            int iv = Array.IndexOf(Intervals, cfg.IntervalMs);
            cbInterval.SelectedIndex = iv >= 0 ? iv : 2;
            timer.Interval = Intervals[cbInterval.SelectedIndex];
            loading = false;
            LoadRates();
        }

        void LoadRates()
        {
            loading = true;
            cbRate.Items.Clear();
            Native.DEVMODE cur;
            if (Dev != null && Displays.Current(Dev, out cur))
            {
                var rates = Displays.Rates(Dev, cur.dmPelsWidth, cur.dmPelsHeight);
                if (!rates.Contains(cfg.TargetHz)) cbRate.Items.Add(new RateItem { Hz = cfg.TargetHz, Offered = false });
                foreach (var r in rates) cbRate.Items.Add(new RateItem { Hz = r, Offered = true });
                for (int i = 0; i < cbRate.Items.Count; i++)
                    if (((RateItem)cbRate.Items[i]).Hz == cfg.TargetHz) { cbRate.SelectedIndex = i; break; }
            }
            loading = false;
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnDisplayChanged(sender, e))); return; }
            // Monitors can come and go; refresh lists, then react instantly instead of waiting for the timer.
            var list = Displays.List();
            if (list.Count != cbDisplay.Items.Count) LoadDisplays(); else LoadRates();
            Check(false);
        }

        // -------- core
        void ApplyEnforceState()
        {
            btn.Active = cfg.Enforce;
            btn.Text = cfg.Enforce ? "Stop enforcing" : "Start enforcing";
            btn.Invalidate();
            miEnforce.Checked = cfg.Enforce;
            timer.Enabled = cfg.Enforce;
            Check(false);
        }

        void Check(bool force)
        {
            if (Dev == null) { SetStatus(0, "", "No monitor", Theme.Bad); return; }
            Native.DEVMODE cur;
            if (!Displays.Current(Dev, out cur)) { SetStatus(0, "", "Can't read monitor", Theme.Bad); return; }
            int hz = cur.dmDisplayFrequency;
            string res = cur.dmPelsWidth + " × " + cur.dmPelsHeight;

            if (!cfg.Enforce && !force) { SetStatus(hz, res, "Paused", Theme.Muted); return; }
            if (hz == cfg.TargetHz) { SetStatus(hz, res, "Locked", Theme.Good); return; }

            var rates = Displays.Rates(Dev, cur.dmPelsWidth, cur.dmPelsHeight);
            if (!rates.Contains(cfg.TargetHz))
            {
                SetStatus(hz, res, cfg.TargetHz + " Hz not offered", Theme.Warn);
                if (cfg.Notify && (DateTime.Now - lastFailNotice).TotalMinutes > 5)
                {
                    lastFailNotice = DateTime.Now;
                    tray.ShowBalloonTip(4000, "RefreshLock", "Windows isn't offering " + cfg.TargetHz + " Hz at " + res + " right now. Retrying…", ToolTipIcon.Warning);
                }
                return;
            }

            int r = Displays.Apply(Dev, cfg.TargetHz);
            Native.DEVMODE after; Displays.Current(Dev, out after);
            if (r == 0 && after.dmDisplayFrequency == cfg.TargetHz)
            {
                corrections++;
                lastCorrection = DateTime.Now.ToString("h:mm:ss tt") + "  (" + hz + " → " + cfg.TargetHz + " Hz)";
                lblLog.Text = "Corrected " + corrections + "×   ·   last " + lastCorrection;
                SetStatus(after.dmDisplayFrequency, res, "Locked", Theme.Good);
                if (cfg.Notify) tray.ShowBalloonTip(2500, "RefreshLock", "Caught " + hz + " Hz, forced back to " + cfg.TargetHz + " Hz.", ToolTipIcon.Info);
            }
            else
            {
                SetStatus(after.dmDisplayFrequency, res, "Change failed (" + r + ")", Theme.Bad);
            }
        }

        void SetStatus(int hz, string res, string status, Color c)
        {
            readout.Hz = hz; readout.Res = res; readout.Status = status; readout.StatusColor = c; readout.Invalidate();
            string t = "RefreshLock — " + (hz > 0 ? hz + " Hz" : "--") + " · " + status;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        // -------- window / tray behaviour
        protected override void SetVisibleCore(bool value)
        {
            if (!allowShow) { value = false; if (!IsHandleCreated) CreateHandle(); }
            base.SetVisibleCore(value);
        }

        void ShowFromTray()
        {
            allowShow = true; Show(); WindowState = FormWindowState.Normal; Activate(); BringToFront();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1; // dark title bar on Win10 2004+ / Win11
            try { if (Native.DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) Native.DwmSetWindowAttribute(Handle, 19, ref on, 4); } catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && cfg != null && cfg.CloseToTray) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && cfg.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; Hide();
                return;
            }
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            timer.Stop(); tray.Visible = false; tray.Dispose();
            base.OnFormClosing(e);
        }

        public void BringUp() { if (InvokeRequired) BeginInvoke(new Action(ShowFromTray)); else ShowFromTray(); }
    }

    // ---------------------------------------------------------------- Entry
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "RefreshLock_SingleInstance_8f2c", out created))
            using (var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "RefreshLock_Show_8f2c"))
            {
                if (!created) { showEvt.Set(); return; }   // already running: pop the existing window instead
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool tray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
                var form = new MainForm(tray);
                var listener = new Thread(() => { while (true) { showEvt.WaitOne(); form.BringUp(); } }) { IsBackground = true };
                listener.Start();
                Application.Run(form);
            }
        }
    }
}
