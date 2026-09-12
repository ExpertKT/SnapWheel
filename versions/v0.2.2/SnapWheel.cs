using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnapWheel
{
    static class AppInfo
    {
#if NO_KEY
        public const string Version = "0.2.2";   // 变体：多 Wheel + 框选缩放/锁定（无万能键）
#else
        public const string Version = "0.3.2";   // 完整版：含万能键摇杆 + 旋转 + 缩放修正
#endif
        public const string Author = "exper7";
        public const string Name = "SnapWheel";
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;
        public const int HOTKEY_ID = 0x5A01;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x; public int y; public POINT(int X, int Y) { x = X; y = Y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx; public int cy; public SIZE(int X, int Y) { cx = X; cy = Y; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize; public int biWidth; public int biHeight;
            public short biPlanes; public short biBitCount; public int biCompression; public int biSizeImage;
            public int biXPelsPerMeter; public int biYPelsPerMeter; public int biClrUsed; public int biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }

        public const int ULW_ALPHA = 0x02;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string str);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);

        public static void PushLayered(Form f, Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBmp);
            SIZE size = new SIZE(bmp.Width, bmp.Height);
            POINT src = new POINT(0, 0);
            POINT dst = new POINT(f.Left, f.Top);
            BLENDFUNCTION bf = new BLENDFUNCTION();
            bf.BlendOp = AC_SRC_OVER; bf.BlendFlags = 0; bf.SourceConstantAlpha = 255; bf.AlphaFormat = AC_SRC_ALPHA;
            UpdateLayeredWindow(f.Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, ULW_ALPHA);
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    static class Gfx
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // fit the whole image inside dest, preserving aspect (letterbox)
        public static RectangleF FitContain(Size img, RectangleF dest)
        {
            float s = Math.Min(dest.Width / img.Width, dest.Height / img.Height);
            float w = img.Width * s, h = img.Height * s;
            return new RectangleF(dest.X + (dest.Width - w) / 2f, dest.Y + (dest.Height - h) / 2f, w, h);
        }
    }

    static class Brand
    {
        static System.Drawing.Icon _icon;
        public static System.Drawing.Icon Get()
        {
            if (_icon != null) return _icon;
            try
            {
                Bitmap b = new Bitmap(32, 32);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(0, 122, 204)))
                        g.FillEllipse(bg, 0, 0, 31, 31);
                    using (Pen p = new Pen(Color.White, 3f))
                    {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        g.DrawLines(p, new PointF[] { new PointF(10, 6), new PointF(6, 6), new PointF(6, 10) });
                        g.DrawLines(p, new PointF[] { new PointF(22, 6), new PointF(26, 6), new PointF(26, 10) });
                        g.DrawLines(p, new PointF[] { new PointF(10, 26), new PointF(6, 26), new PointF(6, 22) });
                        g.DrawLines(p, new PointF[] { new PointF(22, 26), new PointF(26, 26), new PointF(26, 22) });
                    }
                    using (SolidBrush d = new SolidBrush(Color.White))
                        g.FillEllipse(d, 13, 13, 6, 6);
                }
                _icon = System.Drawing.Icon.FromHandle(b.GetHicon());
            }
            catch { _icon = SystemIcons.Application; }
            return _icon;
        }
    }

    class StoreItem
    {
        public Bitmap Image;
        public string FilePath;
    }

    class Store
    {
        public readonly List<StoreItem> Items = new List<StoreItem>();
        public bool SaveToDisk = false;
        public string Dir = "";
        public int MaxCount = 50;

        public StoreItem Add(Bitmap bmp)
        {
            StoreItem it = new StoreItem();
            it.Image = bmp;
            if (SaveToDisk && Dir.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    string f = Path.Combine(Dir, "snap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                    bmp.Save(f, ImageFormat.Png);
                    it.FilePath = f;
                }
                catch { }
            }
            Items.Add(it);
            while (Items.Count > MaxCount && Items.Count > 0) Items.RemoveAt(0);
            return it;
        }

        public string EnsureFile(StoreItem it)
        {
            if (it == null || it.Image == null) return null;
            if (it.FilePath != null && File.Exists(it.FilePath)) return it.FilePath;
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_" + Guid.NewGuid().ToString("N") + ".png");
                it.Image.Save(tmp, ImageFormat.Png);
                it.FilePath = tmp;
                return tmp;
            }
            catch { return null; }
        }
    }

    static class Palette
    {
        public static readonly string[] Names = { "蓝", "红", "琥珀", "绿", "紫", "青", "橙", "灰" };
        public static readonly Color[] Colors = {
            Color.FromArgb(0, 122, 204),
            Color.FromArgb(232, 86, 110),
            Color.FromArgb(247, 166, 35),
            Color.FromArgb(46, 184, 114),
            Color.FromArgb(139, 108, 240),
            Color.FromArgb(0, 176, 185),
            Color.FromArgb(236, 120, 60),
            Color.FromArgb(120, 132, 150)
        };
        public static Color Get(int i)
        {
            int n = Colors.Length;
            int k = i % n; if (k < 0) k += n;
            return Colors[k];
        }
    }

    class Wheel
    {
        public string Id;
        public string Name;
        public int ColorIndex;
        public Store Store = new Store();
        public Wheel() { Id = Guid.NewGuid().ToString("N").Substring(0, 8); Name = "项目"; ColorIndex = 0; }
        public Color Accent { get { return Palette.Get(ColorIndex); } }
    }

    // holds all wheels + which one is active; persists names/colours/active index
    class WheelManager
    {
        public readonly List<Wheel> Wheels = new List<Wheel>();
        public int Active = 0;
        Settings _s;

        public WheelManager(Settings s)
        {
            _s = s;
            Load();
            if (Wheels.Count == 0) { New(); }
            if (Active < 0 || Active >= Wheels.Count) Active = 0;
            ApplySettings();
            Save();          // ensure wheels.ini exists (also on first run)
        }

        public Wheel ActiveWheel { get { return Wheels[Active]; } }
        public Store ActiveStore { get { return Wheels[Active].Store; } }
        public Color Accent { get { return Wheels[Active].Accent; } }

        static string MetaPath()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapWheel");
            try { Directory.CreateDirectory(d); } catch { }
            return Path.Combine(d, "wheels.ini");
        }

        public static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "项目";
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < bad.Length; i++) name = name.Replace(bad[i], '_');
            return name.Trim();
        }

        public void ApplySettings()
        {
            for (int i = 0; i < Wheels.Count; i++)
            {
                Store st = Wheels[i].Store;
                st.SaveToDisk = _s.SaveToDisk;
                st.MaxCount = _s.MaxCount;
                st.Dir = _s.SaveToDisk && !string.IsNullOrEmpty(_s.Dir)
                    ? Path.Combine(_s.Dir, SafeName(Wheels[i].Name))
                    : "";
            }
        }

        public Wheel New()
        {
            Wheel w = new Wheel();
            w.Name = "项目" + (Wheels.Count + 1);
            w.ColorIndex = Wheels.Count % Palette.Colors.Length;
            Wheels.Add(w);
            ApplySettings();
            return w;
        }

        public void Remove(int i)
        {
            if (Wheels.Count <= 1) { Wheels.Clear(); New(); Active = 0; return; }   // never run out of wheels
            Wheel w = Wheels[i];
            try
            {
                if (w.Store.SaveToDisk && !string.IsNullOrEmpty(w.Store.Dir) && Directory.Exists(w.Store.Dir))
                    Directory.Delete(w.Store.Dir, true);
            }
            catch { }
            Wheels.RemoveAt(i);
            if (Active >= Wheels.Count) Active = Wheels.Count - 1;
            if (Active < 0) Active = 0;
        }

        public void Next() { if (Wheels.Count > 0) Active = (Active + 1) % Wheels.Count; }
        public void Prev() { if (Wheels.Count > 0) Active = (Active - 1 + Wheels.Count) % Wheels.Count; }

        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("active=" + Active);
                for (int i = 0; i < Wheels.Count; i++)
                    lines.Add("wheel=" + Wheels[i].Id + "|" + Wheels[i].Name + "|" + Wheels[i].ColorIndex);
                File.WriteAllLines(MetaPath(), lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

        void Load()
        {
            try
            {
                string f = MetaPath();
                if (!File.Exists(f)) return;
                foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                {
                    if (line.StartsWith("active=")) { int a; if (int.TryParse(line.Substring(7), out a)) Active = a; }
                    else if (line.StartsWith("wheel="))
                    {
                        string[] p = line.Substring(6).Split('|');
                        if (p.Length < 3) continue;
                        Wheel w = new Wheel();
                        w.Id = p[0];
                        w.Name = p[1];
                        int ci; if (int.TryParse(p[2], out ci)) w.ColorIndex = ci;
                        Wheels.Add(w);
                    }
                }
            }
            catch { }
        }

        // load saved shots for every wheel when running in disk mode
        public void LoadImagesFromDisk()
        {
            if (!_s.SaveToDisk || string.IsNullOrEmpty(_s.Dir)) return;
            for (int i = 0; i < Wheels.Count; i++)
            {
                Store st = Wheels[i].Store;
                if (string.IsNullOrEmpty(st.Dir) || !Directory.Exists(st.Dir)) continue;
                string[] files = Directory.GetFiles(st.Dir, "snap_*.png");
                Array.Sort(files);
                for (int k = 0; k < files.Length; k++)
                {
                    try
                    {
                        Bitmap b = new Bitmap(files[k]);
                        StoreItem it = new StoreItem();
                        it.Image = b;
                        it.FilePath = files[k];
                        st.Items.Add(it);
                    }
                    catch { }
                }
                while (st.Items.Count > st.MaxCount) st.Items.RemoveAt(0);
            }
        }
    }

    static class HotkeyUtil
    {
        public static readonly string[] Names = { "Ctrl+Shift+S", "Ctrl+Shift+A", "Ctrl+Alt+A", "Alt+Shift+A", "Ctrl+Shift+X" };

        public static bool TryParse(string name, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrEmpty(name)) return false;
            string[] parts = name.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= Native.MOD_CONTROL;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= Native.MOD_SHIFT;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= Native.MOD_ALT;
                else if (p.Length == 1)
                {
                    char c = char.ToUpperInvariant(p[0]);
                    if (c >= 'A' && c <= 'Z') vk = (uint)c;
                }
            }
            return mods != 0 && vk != 0;
        }
    }

    class Settings
    {
        public bool SaveToDisk = false;
        public string Dir = "";
        public int MaxCount = 50;
        public bool AutoHide = false;
        public int AutoHideSeconds = 8;
        public bool ShowWheelOnStart = true;
        public bool AlwaysOnTop = true;
        public string Hotkey = "Ctrl+Shift+S";
        public int ThumbSize = 96;      // nominal thumbnail long side
        public int Radius = 300;        // ring radius from the screen corner
        public int Slots = 5;           // how many cards visible on the arc
        public int LabelSize = 16;      // index label font size (px)
        public string Corner = "BL";    // BL / BR / TL / TR - which screen corner the ring docks to
        public bool AutoStart = false;  // launch at logon (HKCU Run)
        public string DeleteMode = "double";  // "double" right-click to delete, or "single"
        public string SwitchMode = "radial";  // "radial" (万能键圆盘) or "swipe" (长按滑动切换)

        static string FilePath()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapWheel");
            try { Directory.CreateDirectory(d); } catch { }
            return Path.Combine(d, "settings.ini");
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            s.Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapWheel");
            try
            {
                string f = FilePath();
                if (File.Exists(f))
                {
                    foreach (string line in File.ReadAllLines(f))
                    {
                        string[] kv = line.Split(new char[] { '=' }, 2);
                        if (kv.Length != 2) continue;
                        string k = kv[0].Trim(), v = kv[1].Trim();
                        if (k == "SaveToDisk") s.SaveToDisk = (v == "1");
                        else if (k == "Dir" && v.Length > 0) s.Dir = v;
                        else if (k == "MaxCount") { int n; if (int.TryParse(v, out n)) s.MaxCount = n; }
                        else if (k == "AutoHide") s.AutoHide = (v == "1");
                        else if (k == "AutoHideSeconds") { int n; if (int.TryParse(v, out n)) s.AutoHideSeconds = n; }
                        else if (k == "ShowWheelOnStart") s.ShowWheelOnStart = (v == "1");
                        else if (k == "AlwaysOnTop") s.AlwaysOnTop = (v == "1");
                        else if (k == "Hotkey" && v.Length > 0) s.Hotkey = v;
                        else if (k == "ThumbSize") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 260) s.ThumbSize = n; }
                        else if (k == "Radius") { int n; if (int.TryParse(v, out n) && n >= 120 && n <= 700) s.Radius = n; }
                        else if (k == "Slots") { int n; if (int.TryParse(v, out n) && n >= 2 && n <= 12) s.Slots = n; }
                        else if (k == "LabelSize") { int n; if (int.TryParse(v, out n) && n >= 8 && n <= 40) s.LabelSize = n; }
                        else if (k == "Corner" && (v == "BL" || v == "BR" || v == "TL" || v == "TR")) s.Corner = v;
                        else if (k == "AutoStart") s.AutoStart = (v == "1");
                        else if (k == "DeleteMode" && (v == "single" || v == "double")) s.DeleteMode = v;
                        else if (k == "SwitchMode" && (v == "radial" || v == "swipe")) s.SwitchMode = v;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("SaveToDisk=" + (SaveToDisk ? "1" : "0"));
                lines.Add("Dir=" + Dir);
                lines.Add("MaxCount=" + MaxCount);
                lines.Add("AutoHide=" + (AutoHide ? "1" : "0"));
                lines.Add("AutoHideSeconds=" + AutoHideSeconds);
                lines.Add("ShowWheelOnStart=" + (ShowWheelOnStart ? "1" : "0"));
                lines.Add("AlwaysOnTop=" + (AlwaysOnTop ? "1" : "0"));
                lines.Add("Hotkey=" + Hotkey);
                lines.Add("ThumbSize=" + ThumbSize);
                lines.Add("Radius=" + Radius);
                lines.Add("Slots=" + Slots);
                lines.Add("LabelSize=" + LabelSize);
                lines.Add("Corner=" + Corner);
                lines.Add("AutoStart=" + (AutoStart ? "1" : "0"));
                lines.Add("DeleteMode=" + DeleteMode);
                lines.Add("SwitchMode=" + SwitchMode);
                File.WriteAllLines(FilePath(), lines.ToArray());
            }
            catch { }
        }
    }

    // flat rounded button with hover state (for a cleaner, more designed look)
    class RoundButton : Button
    {
        public Color Fill = Color.FromArgb(0, 122, 204);
        public Color FillHover = Color.FromArgb(0, 138, 228);
        public Color TextColor = Color.White;

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
            Color c = hot ? FillHover : Fill;
            using (GraphicsPath p = Gfx.Round(r, 9f))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
            TextRenderer.DrawText(g, Text, Font, r, TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    static class AutoRun
    {
        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string NAME = "SnapWheel";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                    return k != null && k.GetValue(NAME) != null;
            }
            catch { return false; }
        }

        public static void Apply(bool enable)
        {
            try
            {
                if (enable)
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                        k.SetValue(NAME, "\"" + Application.ExecutablePath + "\"");
                }
                else
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                        k.DeleteValue(NAME, false);
                }
            }
            catch { }
        }
    }

    class OverlayForm : Form
    {
        Bitmap _shot;
        Bitmap _dimmed;
        Rectangle _vs;

        // 选区模型：中心 + 尺寸 + 旋转角（弧度），支持旋转
        PointF _c;
        SizeF _sz;
        float _ang = 0f;
        bool _hasSel;

        bool _dragging;      // 新建选区
        Point _start;
        bool _moving;
        PointF _moveStartC;
        int _resizeCorner = -1;    // 0..3 左上/右上/右下/左下
        bool _rotating;
        float _rotGrab = 0f;

        // 比例
        float _ratio = 0f;          // 来自比例胶囊；0 = 自由
        bool _locked = false;       // 锁定键状态
        float _lockedRatio = 0f;

        // 比例胶囊
        struct Chip { public string Label; public float Ratio; public Rectangle Rect; }
        Chip[] _chips;
        Rectangle _toggleRect;
        bool _chipsOpen = false;
        float _chipsT = 0f;
        Timer _anim;
        int[] _chipW;
        int _toggleW = 92;
        Rectangle _panelBounds;

        public Bitmap Result;

        public OverlayForm(Rectangle virtualScreen, Bitmap shot)
        {
            _vs = virtualScreen;
            _shot = shot;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = _vs;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            _dimmed = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(_dimmed))
            {
                g.DrawImageUnscaled(shot, 0, 0);
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    g.FillRectangle(dim, 0, 0, shot.Width, shot.Height);
            }
            MeasureChips(); PlaceChips();
            BuildInfoPanel();
            _anim = new Timer();
            _anim.Interval = 15;
            _anim.Tick += new EventHandler(AnimTick);
            _anim.Start();
        }

        // 右上角信息面板：可输入宽高、角度归零
        TextBox _inW, _inH;
        Label _lblAngle;

        void BuildInfoPanel()
        {
            int px = _vs.Width - 420;
            Panel panel = new Panel();
            panel.Bounds = new Rectangle(px, 18, 400, 40);
            panel.BackColor = Color.FromArgb(210, 18, 20, 24);
            Controls.Add(panel);

            Label l1 = new Label(); l1.Text = "宽"; l1.ForeColor = Color.White;
            l1.Bounds = new Rectangle(10, 10, 20, 22); panel.Controls.Add(l1);
            _inW = new TextBox(); _inW.Bounds = new Rectangle(32, 8, 66, 24);
            _inW.BackColor = Color.FromArgb(38, 40, 46); _inW.ForeColor = Color.White;
            _inW.BorderStyle = BorderStyle.FixedSingle; _inW.TextAlign = HorizontalAlignment.Center;
            panel.Controls.Add(_inW);

            Label l2 = new Label(); l2.Text = "高"; l2.ForeColor = Color.White;
            l2.Bounds = new Rectangle(108, 10, 20, 22); panel.Controls.Add(l2);
            _inH = new TextBox(); _inH.Bounds = new Rectangle(130, 8, 66, 24);
            _inH.BackColor = Color.FromArgb(38, 40, 46); _inH.ForeColor = Color.White;
            _inH.BorderStyle = BorderStyle.FixedSingle; _inH.TextAlign = HorizontalAlignment.Center;
            panel.Controls.Add(_inH);

            RoundButton apply = new RoundButton();
            apply.Text = "应用"; apply.Size = new Size(58, 26); apply.Location = new Point(204, 7);
            apply.Fill = Color.FromArgb(0, 122, 204); apply.FillHover = Color.FromArgb(0, 140, 232);
            apply.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            apply.Click += new EventHandler(delegate(object o, EventArgs e2) { ApplySizeFromBoxes(); });
            panel.Controls.Add(apply);

            RoundButton reset = new RoundButton();
            reset.Text = "角度归零"; reset.Size = new Size(84, 26); reset.Location = new Point(268, 7);
            reset.Fill = Color.FromArgb(70, 74, 84); reset.FillHover = Color.FromArgb(92, 98, 110);
            reset.Font = new Font("Microsoft YaHei UI", 9f);
            reset.Click += new EventHandler(delegate(object o, EventArgs e2) { _ang = 0f; Invalidate(); SyncInfo(); });
            panel.Controls.Add(reset);

            _lblAngle = new Label();
            _lblAngle.ForeColor = Color.FromArgb(170, 176, 186);
            _lblAngle.Bounds = new Rectangle(10, 34, 380, 20);
            panel.Controls.Add(_lblAngle);
            panel.Height = 58;

            _inW.KeyDown += new KeyEventHandler(OnBoxKey);
            _inH.KeyDown += new KeyEventHandler(OnBoxKey);
        }

        void OnBoxKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { ApplySizeFromBoxes(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { Cancel(); }
        }

        void ApplySizeFromBoxes()
        {
            int w, h;
            if (!int.TryParse(_inW.Text.Trim(), out w)) w = (int)Math.Round(_sz.Width);
            if (!int.TryParse(_inH.Text.Trim(), out h)) h = (int)Math.Round(_sz.Height);
            w = Math.Max(2, Math.Min(_vs.Width, w));
            h = Math.Max(2, Math.Min(_vs.Height, h));
            float r = EffRatio();
            if (r > 0f) h = Math.Max(2, (int)Math.Round(w / r));
            if (!_hasSel) { _hasSel = true; _c = new PointF(_vs.Width / 2f, _vs.Height / 2f); }
            _sz = new SizeF(w, h);
            ClampCenter();
            SyncInfo();
            Invalidate();
        }

        void SyncInfo()
        {
            if (!_inW.Focused) _inW.Text = ((int)Math.Round(_sz.Width)).ToString();
            if (!_inH.Focused) _inH.Text = ((int)Math.Round(_sz.Height)).ToString();
            string a = ((int)Math.Round(_ang * 180f / (float)Math.PI)).ToString();
            _lblAngle.Text = "角度 " + a + "°" + (_locked ? "　·　比例已锁定" : "") + (_hasSel ? "" : "　·　拖拽以框选");
        }

        void AnimTick(object sender, EventArgs e)
        {
            float tgt = _chipsOpen ? 1f : 0f;
            if (Math.Abs(_chipsT - tgt) < 0.002f) { _chipsT = tgt; _anim.Stop(); Invalidate(); return; }
            _chipsT += (tgt - _chipsT) * 0.26f;
            Invalidate();
        }

        // ---------- 选区几何 ----------
        PointF AxisU() { return new PointF((float)Math.Cos(_ang), (float)Math.Sin(_ang)); }
        PointF AxisV() { return new PointF((float)-Math.Sin(_ang), (float)Math.Cos(_ang)); }

        PointF[] Corners()
        {
            PointF u = AxisU(), v = AxisV();
            float hw = _sz.Width / 2f, hh = _sz.Height / 2f;
            return new PointF[] {
                new PointF(_c.X - u.X*hw - v.X*hh, _c.Y - u.Y*hw - v.Y*hh),   // 左上
                new PointF(_c.X + u.X*hw - v.X*hh, _c.Y + u.Y*hw - v.Y*hh),   // 右上
                new PointF(_c.X + u.X*hw + v.X*hh, _c.Y + u.Y*hw + v.Y*hh),   // 右下
                new PointF(_c.X - u.X*hw + v.X*hh, _c.Y - u.Y*hw + v.Y*hh)    // 左下
            };
        }

        bool InsideSel(PointF p)
        {
            if (!_hasSel) return false;
            PointF u = AxisU(), v = AxisV();
            float dx = p.X - _c.X, dy = p.Y - _c.Y;
            float du = dx * u.X + dy * u.Y, dv = dx * v.X + dy * v.Y;
            return Math.Abs(du) <= _sz.Width / 2f + 2 && Math.Abs(dv) <= _sz.Height / 2f + 2;
        }

        RectangleF SelBounds()
        {
            PointF[] cs = Corners();
            float minx = cs[0].X, maxx = cs[0].X, miny = cs[0].Y, maxy = cs[0].Y;
            for (int i = 1; i < 4; i++)
            {
                if (cs[i].X < minx) minx = cs[i].X;
                if (cs[i].X > maxx) maxx = cs[i].X;
                if (cs[i].Y < miny) miny = cs[i].Y;
                if (cs[i].Y > maxy) maxy = cs[i].Y;
            }
            return new RectangleF(minx, miny, maxx - minx, maxy - miny);
        }

        // 旋转键：在“上边中点”外侧；锁定键：连在旋转键外侧
        PointF RotateHandlePos()
        {
            PointF v = AxisV();
            return new PointF(_c.X - v.X * (_sz.Height / 2f + 30f), _c.Y - v.Y * (_sz.Height / 2f + 30f));
        }
        PointF LockHandlePos()
        {
            PointF v = AxisV();
            return new PointF(_c.X - v.X * (_sz.Height / 2f + 62f), _c.Y - v.Y * (_sz.Height / 2f + 62f));
        }

        float EffRatio()
        {
            if (_locked && _lockedRatio > 0.01f) return _lockedRatio;
            return _ratio;
        }

        // 用“外接矩形”夹取（不是外接圆），保证选区很大时仍然能自由移动
        void ClampCenter()
        {
            RectangleF bb = SelBounds();
            float hw = bb.Width / 2f, hh = bb.Height / 2f;
            if (hw * 2f > _vs.Width) _c.X = _vs.Width / 2f;
            else { if (_c.X < hw) _c.X = hw; if (_c.X > _vs.Width - hw) _c.X = _vs.Width - hw; }
            if (hh * 2f > _vs.Height) _c.Y = _vs.Height / 2f;
            else { if (_c.Y < hh) _c.Y = hh; if (_c.Y > _vs.Height - hh) _c.Y = _vs.Height - hh; }
        }

        // ---------- 比例胶囊 ----------
        void MeasureChips()
        {
            string[] labels = { "自由", "1:1", "16:9", "9:16", "4:3", "3:4", "21:9" };
            _chipW = new int[labels.Length];
            using (Font f = new Font("Microsoft YaHei UI", 10f))
            using (Graphics g = CreateGraphics())
                for (int i = 0; i < labels.Length; i++)
                    _chipW[i] = (int)g.MeasureString(labels[i], f).Width + 22;
        }

        void PlaceChips()
        {
            string[] labels = { "自由", "1:1", "16:9", "9:16", "4:3", "3:4", "21:9" };
            float[] ratios = { 0f, 1f, 16f / 9f, 9f / 16f, 4f / 3f, 3f / 4f, 21f / 9f };
            if (_chipW == null) MeasureChips();
            int h = 32, gap = 8;
            int chipsW = 0;
            for (int i = 0; i < _chipW.Length; i++) chipsW += _chipW[i] + gap;
            chipsW -= gap;
            int totalW = _toggleW + gap + chipsW;

            int rowX, rowY;
            if (_hasSel)
            {
                RectangleF bb = SelBounds();
                rowX = (int)bb.Left;
                rowY = (int)bb.Bottom + 14;
                if (rowY + h > _vs.Height - 10) rowY = (int)bb.Top - h - 40;
            }
            else
            {
                rowX = (_vs.Width - totalW) / 2;
                rowY = _vs.Height - h - 44;
            }
            if (rowX < 10) rowX = 10;
            if (rowX + totalW > _vs.Width - 10) rowX = _vs.Width - 10 - totalW;
            if (rowY < 10) rowY = 10;
            if (rowY + h > _vs.Height - 10) rowY = _vs.Height - 10 - h;

            _toggleRect = new Rectangle(rowX, rowY, _toggleW, h);
            int x = rowX + _toggleW + gap;
            _chips = new Chip[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                _chips[i].Label = labels[i];
                _chips[i].Ratio = ratios[i];
                _chips[i].Rect = new Rectangle(x, rowY, _chipW[i], h);
                x += _chipW[i] + gap;
            }
            _panelBounds = new Rectangle(rowX, rowY, totalW, h);
        }

        void DrawChips(Graphics g)
        {
            PlaceChips();
            if (_chips == null) return;
            using (Font f = new Font("Microsoft YaHei UI", 10f))
            {
                int shift = (int)((1f - _chipsT) * 26f);
                int al = (int)(255 * _chipsT);
                if (_chipsT > 0.01f)
                {
                    foreach (Chip c in _chips)
                    {
                        bool act = (_ratio > 0f && Math.Abs(c.Ratio - _ratio) < 0.001f) || (c.Ratio == 0f && _ratio == 0f && !_locked);
                        Rectangle r = new Rectangle(c.Rect.X - shift, c.Rect.Y, c.Rect.Width, c.Rect.Height);
                        using (GraphicsPath p = Gfx.Round(r, 8f))
                        using (SolidBrush b = new SolidBrush(act
                            ? Color.FromArgb((int)(235 * _chipsT), 0, 122, 204)
                            : Color.FromArgb((int)(185 * _chipsT), 22, 24, 28)))
                            g.FillPath(b, p);
                        using (GraphicsPath p2 = Gfx.Round(r, 8f))
                        using (Pen pen = new Pen(Color.FromArgb((int)((act ? 255 : 120) * _chipsT), 255, 255, 255), 1.2f))
                            g.DrawPath(pen, p2);
                        TextRenderer.DrawText(g, c.Label, f, r, Color.FromArgb(al, 255, 255, 255),
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                Rectangle tr = _toggleRect;
                using (GraphicsPath p = Gfx.Round(tr, 9f))
                using (SolidBrush b = new SolidBrush(_chipsOpen ? Color.FromArgb(225, 0, 122, 204) : Color.FromArgb(185, 22, 24, 28)))
                    g.FillPath(b, p);
                using (GraphicsPath p2 = Gfx.Round(tr, 9f))
                using (Pen pen = new Pen(Color.FromArgb(130, 255, 255, 255), 1.2f))
                    g.DrawPath(pen, p2);
                TextRenderer.DrawText(g, _chipsOpen ? "比例 ▼" : "比例 ▶", f, tr, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // ---------- 绘制 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.CompositingMode = CompositingMode.SourceCopy;
            if (_dimmed != null) g.DrawImageUnscaled(_dimmed, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;

            if (_hasSel && _sz.Width > 1 && _sz.Height > 1)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                PointF[] cs = Corners();
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddPolygon(cs);
                    // 选区内显示原图（未变暗）
                    if (_shot != null)
                    {
                        g.SetClip(path);
                        g.DrawImageUnscaled(_shot, 0, 0);
                        g.ResetClip();
                    }
                    using (Pen p = new Pen(Color.FromArgb(0, 174, 255), 2f)) g.DrawPath(p, path);
                }

                // 四角缩放手柄
                foreach (PointF p in cs)
                {
                    using (SolidBrush b = new SolidBrush(Color.White))
                        g.FillRectangle(b, p.X - 4.5f, p.Y - 4.5f, 9, 9);
                    using (Pen bp = new Pen(Color.FromArgb(0, 174, 255), 1.6f))
                        g.DrawRectangle(bp, p.X - 4.5f, p.Y - 4.5f, 9, 9);
                }

                // 旋转键 + 连体锁定键
                PointF rh = RotateHandlePos(), lh = LockHandlePos();
                using (Pen line = new Pen(Color.FromArgb(160, 255, 255, 255), 1.2f))
                {
                    PointF topMid = new PointF((cs[0].X + cs[1].X) / 2f, (cs[0].Y + cs[1].Y) / 2f);
                    g.DrawLine(line, topMid, rh);
                    g.DrawLine(line, rh, lh);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 22, 24, 28)))
                    g.FillEllipse(b, rh.X - 13, rh.Y - 13, 26, 26);
                using (Pen p = new Pen(Color.White, 1.6f))
                {
                    // 旋转图标：圆弧 + 箭头
                    g.DrawArc(p, rh.X - 6.5f, rh.Y - 6.5f, 13, 13, 40, 250);
                    g.DrawLine(p, rh.X + 3.4f, rh.Y - 7.6f, rh.X + 7.2f, rh.Y - 4.4f);
                    g.DrawLine(p, rh.X + 7.2f, rh.Y - 4.4f, rh.X + 2.6f, rh.Y - 3.4f);
                }
                using (SolidBrush b = new SolidBrush(_locked ? Color.FromArgb(240, 0, 122, 204) : Color.FromArgb(225, 22, 24, 28)))
                    g.FillEllipse(b, lh.X - 13, lh.Y - 13, 26, 26);
                using (Pen p = new Pen(Color.White, 1.6f))
                {
                    // 锁图标
                    g.DrawRectangle(p, lh.X - 5f, lh.Y - 1f, 10f, 9f);
                    g.DrawArc(p, lh.X - 3.5f, lh.Y - 7f, 7f, 8f, 180, 180);
                }

                // 尺寸/角度标签
                RectangleF bb2 = SelBounds();
                string txt = ((int)Math.Round(_sz.Width)) + " x " + ((int)Math.Round(_sz.Height));
                if (Math.Abs(_ang) > 0.001f) txt += "   " + ((int)Math.Round(_ang * 180f / Math.PI)) + "°";
                if (_locked) txt += "   🔒";
                using (Font f = new Font("Segoe UI", 9.5f))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(205, 0, 0, 0)))
                using (SolidBrush fg = new SolidBrush(Color.White))
                {
                    SizeF szl = g.MeasureString(txt, f);
                    float tx = bb2.Left, ty = bb2.Top - szl.Height - 6;
                    if (ty < 4) ty = bb2.Bottom + 6;
                    g.FillRectangle(bg, tx, ty, szl.Width + 10, szl.Height + 3);
                    g.DrawString(txt, f, fg, tx + 5, ty + 1);
                }

                string hint = "双击保存　·　拖角缩放　·　拖圆点旋转　·　Esc 取消";
                using (Font f2 = new Font("Microsoft YaHei UI", 10f))
                using (SolidBrush fg2 = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                using (SolidBrush bg2 = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                {
                    SizeF sz2 = g.MeasureString(hint, f2);
                    float hx = bb2.Left;
                    float hy = bb2.Top - sz2.Height - 36;
                    if (hy < 4) hy = bb2.Bottom + 30;
                    g.FillRectangle(bg2, hx, hy, sz2.Width + 8, sz2.Height + 4);
                    g.DrawString(hint, f2, fg2, hx + 4, hy + 2);
                }
            }
            DrawChips(g);
        }

        // ---------- 交互 ----------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Cancel(); return; }
            if (e.Button != MouseButtons.Left) return;

            if (_toggleRect.Contains(e.Location)) { _chipsOpen = !_chipsOpen; _anim.Start(); Invalidate(); return; }
            if (_chipsOpen && _chipsT > 0.5f && _chips != null)
            {
                int shift = (int)((1f - _chipsT) * 26f);
                for (int i = 0; i < _chips.Length; i++)
                {
                    Rectangle r = new Rectangle(_chips[i].Rect.X - shift, _chips[i].Rect.Y, _chips[i].Rect.Width, _chips[i].Rect.Height);
                    if (!r.Contains(e.Location)) continue;
                    _ratio = _chips[i].Ratio;
                    _locked = (_ratio > 0f);
                    _lockedRatio = _ratio;
                    ApplyRatioToSel();
                    SyncInfo();
                    Invalidate();
                    return;
                }
            }

            if (_hasSel)
            {
                // 锁定键
                PointF lh = LockHandlePos();
                if (Dist(e.Location, lh) < 15f)
                {
                    _locked = !_locked;
                    if (_locked) { _lockedRatio = _sz.Height > 1 ? _sz.Width / _sz.Height : 1f; _ratio = 0f; }
                    else { _lockedRatio = 0f; _ratio = 0f; }
                    SyncInfo();
                    Invalidate();
                    return;
                }
                // 旋转键
                PointF rh = RotateHandlePos();
                if (Dist(e.Location, rh) < 15f)
                {
                    _rotating = true;
                    _rotGrab = (float)Math.Atan2(e.Y - _c.Y, e.X - _c.X) - _ang;
                    return;
                }
                // 四角
                PointF[] cs = Corners();
                for (int i = 0; i < 4; i++)
                    if (Dist(e.Location, cs[i]) < 11f) { _resizeCorner = i; return; }
                // 内部拖动
                if (InsideSel(e.Location)) { _moving = true; _moveStartC = _c; _start = e.Location; return; }
            }

            // 新建选区
            _dragging = true;
            _start = e.Location;
            _hasSel = false;
            Invalidate();
        }

        static float Dist(PointF a, PointF b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        void ApplyRatioToSel()
        {
            float r = EffRatio();
            if (r <= 0f || !_hasSel || _sz.Width < 4) return;
            float w = _sz.Width;
            _sz = new SizeF(w, w / r);
            ClampCenter();
        }

        void InvalidateForSelection(Rectangle a, Rectangle b)
        {
            Rectangle oldPanel = _panelBounds;
            Rectangle dirty = Rectangle.Union(a, b);
            PlaceChips();
            dirty = Rectangle.Union(dirty, Rectangle.Union(oldPanel, _panelBounds));
            dirty.Inflate(90, 90);
            Invalidate(dirty);
        }

        Rectangle DirtyRect()
        {
            RectangleF bb = _hasSel ? SelBounds() : RectangleF.Empty;
            Rectangle r = new Rectangle((int)bb.Left - 80, (int)bb.Top - 100, (int)bb.Width + 160, (int)bb.Height + 200);
            if (r.Width < 1) r = new Rectangle(0, 0, _vs.Width, _vs.Height);
            return r;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // 关键保护：如果左键其实没按住，立刻清掉所有拖拽状态，
            // 否则“在选区外松开鼠标”后，后续移动会继续缩放/旋转 -> 乱飞
            if ((Control.MouseButtons & MouseButtons.Left) == 0)
            {
                if (_rotating || _resizeCorner >= 0 || _moving || _dragging)
                {
                    _rotating = false; _resizeCorner = -1; _moving = false; _dragging = false;
                }
            }

            if (_rotating)
            {
                Rectangle old = DirtyRect();
                float want = (float)Math.Atan2(e.Y - _c.Y, e.X - _c.X) - _rotGrab;
                if (Math.Abs(want - _ang) > 0.0005f) { _ang = want; }
                InvalidateForSelection(old, DirtyRect());
                return;
            }
            if (_resizeCorner >= 0)
            {
                Rectangle old = DirtyRect();
                ResizeTo(e.Location);
                InvalidateForSelection(old, DirtyRect());
                return;
            }
            if (_moving)
            {
                Rectangle old = DirtyRect();
                _c = new PointF(_moveStartC.X + (e.X - _start.X), _moveStartC.Y + (e.Y - _start.Y));
                ClampCenter();
                InvalidateForSelection(old, DirtyRect());
                return;
            }
            if (_dragging)
            {
                Rectangle old = DirtyRect();
                float x1 = Math.Min(_start.X, e.X), y1 = Math.Min(_start.Y, e.Y);
                float x2 = Math.Max(_start.X, e.X), y2 = Math.Max(_start.Y, e.Y);
                float w = Math.Max(2f, x2 - x1), h = Math.Max(2f, y2 - y1);
                float r = EffRatio();
                if (r > 0f) { if (w / h > r) h = w / r; else w = h * r; }
                _c = new PointF(x1 + w / 2f, y1 + h / 2f);
                _sz = new SizeF(w, h);
                _ang = 0f;
                _hasSel = true;
                InvalidateForSelection(old, DirtyRect());
            }
        }

        // 按住某个角缩放：对角绝对不动；比例锁定按比例；只在屏内限制尺寸（绝不移动固定角）
        void ResizeTo(PointF mouse)
        {
            if (!_hasSel) return;
            PointF u = AxisU(), v = AxisV();
            PointF[] cs = Corners();
            PointF fx = cs[(_resizeCorner + 2) % 4];        // 对角固定
            // 先把鼠标点夹到屏幕内（到边即停），再据此算尺寸 -> 不再出现边缘回弹抽搐
            float mx = mouse.X, my = mouse.Y;
            if (mx < 0f) mx = 0f; if (mx > _vs.Width) mx = _vs.Width;
            if (my < 0f) my = 0f; if (my > _vs.Height) my = _vs.Height;
            float dx = mx - fx.X, dy = my - fx.Y;
            float du = dx * u.X + dy * u.Y;
            float dv = dx * v.X + dy * v.Y;
            if (du < 6f) du = 6f;
            if (dv < 6f) dv = 6f;
            float r = EffRatio();
            if (r > 0f) { if (du / dv > r) dv = du / r; else du = dv * r; }

            _sz = new SizeF(du, dv);
            _c = new PointF(fx.X + u.X * du / 2f + v.X * dv / 2f,
                            fx.Y + u.Y * du / 2f + v.Y * dv / 2f);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _moving = false; _resizeCorner = -1; _rotating = false;
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                if (!_hasSel || _sz.Width < 3 || _sz.Height < 3) { _hasSel = false; Invalidate(); }
            }
            SyncInfo();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (_hasSel && InsideSel(e.Location)) Confirm();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Cancel();
            else if (e.KeyCode == Keys.Enter && _hasSel) Confirm();
        }

        void Confirm()
        {
            if (_shot == null || !_hasSel) { Close(); return; }
            int w = Math.Max(1, (int)Math.Round(_sz.Width));
            int h = Math.Max(1, (int)Math.Round(_sz.Height));
            Bitmap crop = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(crop))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TranslateTransform(w / 2f, h / 2f);
                g.RotateTransform(-_ang * 180f / (float)Math.PI);
                g.TranslateTransform(-_c.X, -_c.Y);
                g.DrawImageUnscaled(_shot, 0, 0);
            }
            Result = crop;
            DialogResult = DialogResult.OK;
            Close();
        }

        void Cancel() { Result = null; DialogResult = DialogResult.Cancel; Close(); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_shot != null) { _shot.Dispose(); _shot = null; }
            if (_dimmed != null) { _dimmed.Dispose(); _dimmed = null; }
            base.OnFormClosed(e);
        }
    }

    // small preview that follows the cursor while dragging an item out
    class DragProxyForm : Form
    {
        Bitmap _bmp;
        const int BOX = 120;
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        public DragProxyForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Size = new Size(BOX + 24, BOX + 24);
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return cp; }
        }

        public void ShowFor(Bitmap img, Point at)
        {
            int w = Width, h = Height;
            if (_bmp == null || _bmp.Width != w || _bmp.Height != h)
            {
                if (_bmp != null) _bmp.Dispose();
                _bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            }
            using (Graphics g = Graphics.FromImage(_bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                RectangleF box = new RectangleF(12, 12, BOX, BOX);
                using (GraphicsPath bgp = Gfx.Round(box, 14f))
                {
                    using (SolidBrush bb = new SolidBrush(Color.FromArgb(244, 26, 28, 33))) g.FillPath(bb, bgp);
                    if (img != null)
                    {
                        g.SetClip(bgp);
                        RectangleF fit = Gfx.FitContain(img.Size, new RectangleF(box.X + 4, box.Y + 4, box.Width - 8, box.Height - 8));
                        g.DrawImage(img, fit);
                        g.ResetClip();
                    }
                    using (Pen bp = new Pen(Color.FromArgb(235, 255, 255, 255), 1.6f)) g.DrawPath(bp, bgp);
                }
            }
            Location = at;
            IntPtr hh = Handle;                       // create the window first
            if (!Visible) Show();
            Native.PushLayered(this, _bmp);           // content ready (no flash at a wrong spot)
        }

        public void MoveTo(Point at)
        {
            if (Location == at) return;
            Location = at;
            if (_bmp != null) Native.PushLayered(this, _bmp);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _bmp != null) { try { _bmp.Dispose(); } catch { } _bmp = null; }
            base.Dispose(disposing);
        }
    }

    // ---- minimal corner dock: a quarter arc hugging the bottom-left corner ----
    class WheelForm : Form
    {
        Store _store { get { return _mgr.ActiveStore; } }     // always the ACTIVE wheel's store
        WheelManager _mgr;
        Settings _settings;
        Timer _anim;
        float _R = 300f;          // ring radius, measured from the screen corner
        float _thumb = 96f;       // nominal thumbnail long side
        float _phiMin, _phiMax;   // angle span that keeps cards fully on screen
        int _slots = 5;
        StoreItem _dragOutItem = null;   // item being pulled out (animates away)
        float _offset = 0f, _targetOffset = 0f;
        float _show = 0f, _targetShow = 0f;
        DateTime _showT0 = DateTime.Now;
        float _showFrom = 0f;
        bool _showAnimating = false;
        int _hover = -1;
        int _enlarged = -1;
        int _holdIndex = -1;
        DateTime _holdStart = DateTime.MinValue;
        bool _closeHover = false;
        bool _gearHover = false;
        bool _shootHover = false;
        public event EventHandler SettingsRequested;
        Dictionary<int, float> _scales = new Dictionary<int, float>();
        int _peekIndex = -1;           // kept during the fade-out so the peek can animate away
        float _dragOutProg = 0f;       // 0..1 pull-out shrink progress
        StoreItem _deletingItem = null;
        float _deleteProg = 0f;
        DateTime _lastRightClick = DateTime.MinValue;
        int _lastRightIndex = -1;
        const float HoverScale = 1.36f;
        const float PeekScale = 2.4f;
        const float CardPad = 0f;       // card == image rect, so the picture fills the rounded frame
        DateTime _lastActive = DateTime.Now;
        Point _mouseDownPt;
        bool _maybeDrag;
        int _dragIndex = -1;
        bool _rendered = false;
        DragProxyForm _proxy = new DragProxyForm();

        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_NOACTIVATE = 0x08000000;

        public WheelForm(WheelManager mgr, Settings settings)
        {
            _mgr = mgr;
            _settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = settings.AlwaysOnTop;
            ApplyLayout();
            GiveFeedback += new GiveFeedbackEventHandler(OnGiveFeedback);
            AllowDrop = true;
            DragEnter += new DragEventHandler(OnDragOverWheel);
            DragOver += new DragEventHandler(OnDragOverWheel);
            DragLeave += new EventHandler(delegate(object o, EventArgs ev) { if (_dropActive) { _dropActive = false; Render(); } });
            DragDrop += new DragEventHandler(OnDragDropWheel);
            _anim = new Timer();
            _anim.Interval = 15;
            _anim.Tick += new EventHandler(AnimTick);
            _anim.Start();
        }

        public void ApplyLayout()
        {
            _thumb = Math.Max(40, Math.Min(260, _settings.ThumbSize));
            _R = Math.Max(120, Math.Min(700, _settings.Radius));
            _slots = Math.Max(2, Math.Min(12, _settings.Slots));
            _phiMin = (float)Math.Asin(Math.Min(0.92, (_thumb * 0.80f) / _R));
            _phiMax = (float)(Math.PI / 2) - _phiMin;
            // 留出摇杆键的空间：半径 + 键(92) + 名字标签
            int size = (int)(_R + _thumb * 1.75f + 190f);
            if (Size.Width != size) Size = new Size(size, size);
            PlaceBottomLeft();
            _rendered = false;
            if (Visible) Render();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void ApplyTopMost()
        {
            TopMost = _settings.AlwaysOnTop;
            if (Visible) { _rendered = false; Render(); }   // no Hide/Show flash
        }

        public void PlaceBottomLeft()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int size = Width;
            int left = (Sx() > 0) ? wa.Left : wa.Right - size;
            int top = (Sy() > 0) ? wa.Top : wa.Bottom - size;
            Location = new Point(left, top);
        }

        public void ShowWheel()
        {
            if (!Visible) { _show = 0f; _rendered = false; Show(); }
            SetShow(1f);
            _lastActive = DateTime.Now;
            Render();
        }

        public void HideWheel() { SetShow(0f); }
        public void ToggleWheel() { if (_targetShow > 0.5f) HideWheel(); else ShowWheel(); }

        void SetShow(float target)
        {
            if (Math.Abs(_targetShow - target) < 0.001f && _showAnimating == false) { _targetShow = target; return; }
            _showFrom = _show;
            _showT0 = DateTime.Now;
            _targetShow = target;
            _showAnimating = true;
        }

        void AnimTick(object sender, EventArgs e)
        {
            bool need = false;
            bool hide = _targetShow < _show;
            if (_showAnimating)
            {
                float dur = hide ? 0.50f : 0.30f;      // fixed, predictable duration
                float t = (float)((DateTime.Now - _showT0).TotalSeconds / dur);
                if (t >= 1f) { t = 1f; _showAnimating = false; }
                float ease = t * t * (3f - 2f * t);       // smoothstep
                _show = _showFrom + (_targetShow - _showFrom) * ease;
                need = true;
            }
            else if (_show != _targetShow) { _show = _targetShow; need = true; }

            float dop = _targetOffset - _offset;
            bool scrolling = Math.Abs(dop) > 0.02f;
            if (scrolling) { _offset += dop * 0.20f; need = true; }
            else if (_offset != _targetOffset) { _offset = _targetOffset; need = true; }

            // while the arc is sliding, freeze hover so cards don't grow/shrink alternately (no tremble)
            if (scrolling)
            {
                if (_hover != -1) { _hover = -1; need = true; }
            }
            else if (_show > 0.6f && Visible)
            {
                int hh = HitTest(PointToClient(Cursor.Position));
                bool gh = GearButtonRect().Contains(PointToClient(Cursor.Position));
                bool ch = CloseButtonRect().Contains(PointToClient(Cursor.Position));
                bool sH = ShootButtonRect().Contains(PointToClient(Cursor.Position));
                if (hh != _hover) { _hover = hh; need = true; }
                if (gh != _gearHover) { _gearHover = gh; need = true; }
                if (ch != _closeHover) { _closeHover = ch; need = true; }
                if (sH != _shootHover) { _shootHover = sH; need = true; }
            }

            if (_holdIndex >= 0 && _maybeDrag && _enlarged < 0)
                if ((DateTime.Now - _holdStart).TotalMilliseconds > 300) { _enlarged = _holdIndex; need = true; }

            // per-item scale: hover 1.36x, hold-to-peek 2.4x - ONE animation, so it can never desync
            for (int i = 0; i < _store.Items.Count; i++)
            {
                float tgt;
                if (_store.Items[i] == _dragOutItem) tgt = 0f;
                else if (i == _enlarged) tgt = PeekScale;
                else if (i == _hover) tgt = HoverScale;
                else tgt = 1f;
                float cur;
                if (!_scales.TryGetValue(i, out cur)) cur = 1f;
                float rate = (tgt > 1.5f || cur > 1.5f) ? 0.16f : 0.19f;   // peek moves a little slower
                if (Math.Abs(cur - tgt) > 0.003f) { _scales[i] = cur + (tgt - cur) * rate; need = true; }
                else if (cur != tgt) { _scales[i] = tgt; need = true; }
            }
            // hold-to-peek fade state kept in sync with the scale animation (single source of truth)
            if (_enlarged >= 0) _peekIndex = _enlarged;
            if (_enlarged < 0 && _peekIndex >= 0)
            {
                float cur; if (!_scales.TryGetValue(_peekIndex, out cur)) cur = 1f;
                if (cur <= 1.02f) _peekIndex = -1;
            }
            if (_enlarged >= 0 || _peekIndex >= 0) need = true;

            else if (_deletingItem != null)
            {
                _deleteProg += 0.055f;                     // ~0.28s collapse
                need = true;
                if (_deleteProg >= 1f)
                {
                    _store.Items.Remove(_deletingItem);
                    _thumbCache.Remove(_deletingItem);
                    _enterT0.Remove(_deletingItem);
                    _scales.Clear();
                    _deletingItem = null;
                    _deleteProg = 0f;
                    _hover = -1;
                    if (_targetOffset > Math.Max(0, _store.Items.Count - 1)) _targetOffset = Math.Max(0, _store.Items.Count - 1);
                    if (_offset > _targetOffset) _offset = _targetOffset;
                }
            }

            // keep animating while a freshly captured image is still sliding in / a delete is running
            foreach (KeyValuePair<StoreItem, DateTime> kv in _enterT0)
                if ((DateTime.Now - kv.Value).TotalSeconds < 0.5) { need = true; break; }
            if (_deletingItem != null) need = true;

            // 万能键：长按展开圆盘 / 滑动切换
            if (_keyDown)
            {
                bool radial = (_settings.SwitchMode != "swipe");
                if (radial && !_menuOpen && !_delConfirm && (DateTime.Now - _keyDownAt).TotalMilliseconds > 260)
                {
                    _menuOpen = true; _sector = -1; need = true;
                }
                if (_menuOpen && _menuT < 1f) { _menuT += (1f - _menuT) * 0.28f; need = true; }
            }
            else if (_menuT > 0.001f)
            {
                _menuT += (0f - _menuT) * 0.30f;
                if (_menuT < 0.01f) { _menuT = 0f; _menuOpen = false; _sector = -1; }
                need = true;
            }

            // 主题色过渡 + 切换闪光
            Color want = _mgr.Accent;
            if (_accentCur.ToArgb() != want.ToArgb())
            {
                _accentCur = Color.FromArgb(
                    (int)Math.Round(_accentCur.R + (want.R - _accentCur.R) * 0.22f),
                    (int)Math.Round(_accentCur.G + (want.G - _accentCur.G) * 0.22f),
                    (int)Math.Round(_accentCur.B + (want.B - _accentCur.B) * 0.22f));
                need = true;
            }
            if (_switchFlash > 0f) { _switchFlash += (0f - _switchFlash) * 0.16f; if (_switchFlash < 0.01f) _switchFlash = 0f; need = true; }

            // 删除确认态：高亮鼠标所在的半 + 超时自动取消（避免一直挂着）
            if (_delConfirm)
            {
                int want2 = -1;
                if (Visible)
                {
                    Rectangle kr2 = KeyRect();
                    Point cp2 = PointToClient(Cursor.Position);
                    if (kr2.Contains(cp2)) want2 = (cp2.X < kr2.X + kr2.Width / 2f) ? 0 : 1;
                }
                if (want2 != _delHalf) { _delHalf = want2; need = true; }
                if ((DateTime.Now - _delConfirmAt).TotalSeconds > 10) { _delConfirm = false; _delHalf = -1; need = true; }
            }
            else if (_delHalf != -1) { _delHalf = -1; need = true; }

            if (_settings.AutoHide && _targetShow > 0.5f && _show > 0.99f)
                if ((DateTime.Now - _lastActive).TotalSeconds > _settings.AutoHideSeconds) HideWheel();

            if (_show <= 0.002f && _targetShow <= 0.002f)
            {
                if (Visible) Hide();
                return;
            }
            if (need || !_rendered) Render();   // render ONLY when something changed (smooth + cheap)
        }

        // ---- geometry: a full ring whose centre sits on the chosen screen corner ----
        float Sx() { return _settings.Corner.EndsWith("L") ? 1f : -1f; }   // outward horizontal (into screen)
        float Sy() { return _settings.Corner.StartsWith("T") ? 1f : -1f; } // outward vertical (into screen)

        PointF Center()
        {
            float cx = (Sx() > 0) ? 0f : (float)ClientSize.Width;
            float cy = (Sy() > 0) ? 0f : (float)ClientSize.Height;
            return new PointF(cx, cy);
        }

        float ArcStart()
        {
            if (Sy() < 0) return (Sx() > 0) ? 270f : 180f;
            return (Sx() > 0) ? 0f : 90f;
        }

        float StepRad() { return (_phiMax - _phiMin) / Math.Max(1, _slots - 1); }

        float EffR() { return _R; }

        float ItemPhi(int i) { return _phiMin + (i - _offset) * StepRad(); }

        PointF ItemCenter(int i) { return ItemCenterAtPhi(ItemPhi(i)); }

        PointF ItemCenterAtPhi(float phi)
        {
            float r = EffR();
            PointF c = Center();
            return new PointF((float)(c.X + r * Math.Cos(phi) * Sx()), (float)(c.Y + r * Math.Sin(phi) * Sy()));
        }

        Dictionary<StoreItem, DateTime> _enterT0 = new Dictionary<StoreItem, DateTime>();

        // only a NEWLY captured image slides in; everything else is already in place
        public void MarkNew(StoreItem it)
        {
            if (it != null) _enterT0[it] = DateTime.Now;
        }

        float EnterProgress(int i)
        {
            if (i < 0 || i >= _store.Items.Count) return 1f;
            DateTime t0;
            if (!_enterT0.TryGetValue(_store.Items[i], out t0)) return 1f;
            float d = (float)(DateTime.Now - t0).TotalSeconds;
            if (d <= 0f) return 0f;
            float t = d / 0.42f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);      // smoothstep -> eased, silky
        }

        // very extreme aspect ratios get a fixed square box with a distinctive border colour
        static bool IsExtreme(StoreItem it)
        {
            if (it == null || it.Image == null) return false;
            float a = it.Image.Width / (float)Math.Max(1, it.Image.Height);
            // only markedly stretched shots (beyond ~2.8:1) get the special box treatment
            return a > 2.8f || a < (1f / 2.8f);
        }

        // card size == the image's exact aspect; extreme ones become a square box
        SizeF CardSize(StoreItem it)
        {
            float iw = (it != null && it.Image != null) ? it.Image.Width : 1f;
            float ih = (it != null && it.Image != null) ? it.Image.Height : 1f;
            if (IsExtreme(it)) return new SizeF((float)Math.Round(_thumb), (float)Math.Round(_thumb));
            float s = _thumb / Math.Max(iw, ih);
            return new SizeF(Math.Max(6f, (float)Math.Round(iw * s)), Math.Max(6f, (float)Math.Round(ih * s)));
        }

        // card = image rect grown by `pad` (hover lift). pad may be negative (pull-out shrink).
        RectangleF CardRect(int i, float pad)
        {
            if (i < 0 || i >= _store.Items.Count) return RectangleF.Empty;
            PointF c = ItemCenter(i);
            SizeF sz = CardSize(_store.Items[i]);
            float x = (float)Math.Round(c.X - sz.Width / 2f - pad);
            float y = (float)Math.Round(c.Y - sz.Height / 2f - pad);
            return new RectangleF(x, y, sz.Width + 2f * pad, sz.Height + 2f * pad);
        }

        // image rect inside a card, always the crisp nominal size
        RectangleF ImageRect(int i)
        {
            if (i < 0 || i >= _store.Items.Count) return RectangleF.Empty;
            PointF c = ItemCenter(i);
            SizeF sz = CardSize(_store.Items[i]);
            return new RectangleF((float)Math.Round(c.X - sz.Width / 2f), (float)Math.Round(c.Y - sz.Height / 2f), sz.Width, sz.Height);
        }

        // fit the whole image inside a box, preserving aspect
        static SizeF FitInside(Size img, float bw, float bh)
        {
            if (img.Width <= 0 || img.Height <= 0) return new SizeF(bw, bh);
            float s = Math.Min(bw / img.Width, bh / img.Height);
            return new SizeF(img.Width * s, img.Height * s);
        }

        // cache scaled thumbnails by rounded pixel size -> no per-frame resampling shimmer
        Dictionary<StoreItem, Dictionary<long, Bitmap>> _thumbCache = new Dictionary<StoreItem, Dictionary<long, Bitmap>>();

        // draw a bitmap honouring an alpha value (DrawImageUnscaled ignores alpha entirely)
        ImageAttributes _ia = new ImageAttributes();
        void DrawWithAlpha(Graphics g, Bitmap bmp, RectangleF dest, int alpha)
        {
            if (alpha >= 250) { g.DrawImageUnscaled(bmp, (int)dest.X, (int)dest.Y); return; }
            ColorMatrix cm = new ColorMatrix();
            cm.Matrix33 = Math.Max(0f, Math.Min(1f, alpha / 255f));
            _ia.SetColorMatrix(cm);
            g.DrawImage(bmp, new Rectangle((int)dest.X, (int)dest.Y, (int)dest.Width, (int)dest.Height),
                0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, _ia);
        }

        void PruneCaches()
        {
            if (_thumbCache.Count <= _store.Items.Count) return;
            List<StoreItem> dead = new List<StoreItem>();
            foreach (StoreItem k in _thumbCache.Keys) if (!_store.Items.Contains(k)) dead.Add(k);
            for (int i = 0; i < dead.Count; i++)
            {
                foreach (Bitmap b in _thumbCache[dead[i]].Values) { try { b.Dispose(); } catch { } }
                _thumbCache.Remove(dead[i]);
            }
        }

        Bitmap ScaledThumb(StoreItem it, int w, int h)
        {
            Dictionary<long, Bitmap> d;
            if (!_thumbCache.TryGetValue(it, out d)) { d = new Dictionary<long, Bitmap>(); _thumbCache[it] = d; }
            long key = ((long)w << 20) | (uint)h;
            Bitmap b;
            if (d.TryGetValue(key, out b)) return b;
            if (d.Count > 48)
            {
                foreach (Bitmap v in d.Values) { try { v.Dispose(); } catch { } }
                d.Clear();
            }
            b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics gg = Graphics.FromImage(b))
            {
                gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                gg.DrawImage(it.Image, new Rectangle(0, 0, w, h));
            }
            d[key] = b;
            return b;
        }

        // exactly what DrawWheel draws for item i (so grabbing matches what you see)
        RectangleF DrawnRect(int i)
        {
            if (i < 0 || i >= _store.Items.Count) return RectangleF.Empty;
            float sc; if (!_scales.TryGetValue(i, out sc)) sc = 1f;
            if (_store.Items[i] == _dragOutItem) sc *= Math.Max(0f, 1f - _dragOutProg);
            if (_store.Items[i] == _deletingItem) sc *= Math.Max(0f, 1f - _deleteProg);
            SizeF b = CardSize(_store.Items[i]);
            int iw = Math.Max(4, (int)Math.Round(b.Width * sc));
            int ih = Math.Max(4, (int)Math.Round(b.Height * sc));
            PointF pc = ItemCenter(i);
            float x = (float)Math.Round(pc.X - iw / 2f), y = (float)Math.Round(pc.Y - ih / 2f);
            return new RectangleF(x - CardPad, y - CardPad, iw + 2 * CardPad, ih + 2 * CardPad);
        }

        int HitTest(Point p)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < _store.Items.Count; i++)
            {
                float phi = ItemPhi(i);
                if (phi < _phiMin - 0.45f || phi > _phiMax + 0.45f) continue;
                if (EnterProgress(i) < 0.5f) continue;
                if (_store.Items[i] == _dragOutItem) continue;
                RectangleF rr = DrawnRect(i);
                RectangleF hit = new RectangleF(rr.X - 2, rr.Y - 2, rr.Width + 4, rr.Height + 4);
                if (hit.Contains(p))
                {
                    PointF c = ItemCenter(i);
                    float d = (float)Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y));
                    if (d < bestD) { bestD = d; best = i; }
                }
            }
            return best;
        }

        Rectangle CloseButtonRect() { return BtnRect(0); }
        Rectangle GearButtonRect() { return BtnRect(1); }
        Rectangle ShootButtonRect() { return BtnRect(2); }

        // ---- 万能键（在弧线中点）：长按弹出四扇区圆盘，拖到扇区松手执行 ----
        Rectangle KeyRect()
        {
#if NO_KEY
            return Rectangle.Empty;           // v0.2.0 变体：不含万能键
#else
            float mid = (_phiMin + _phiMax) / 2f;
            // 弧的“内侧”中点：避开缩略图，也不压住角上的按钮
            float kr2 = EffR() - _thumb * 1.25f;
            if (kr2 < 60f) kr2 = 60f;
            PointF p = ItemCenterAtPhiRadius(mid, kr2);
            int s = 92;                       // 摇杆式大圆盘
            return new Rectangle((int)Math.Round(p.X - s / 2f), (int)Math.Round(p.Y - s / 2f), s, s);
#endif
        }

        PointF ItemCenterAtPhiRadius(float phi, float radius)
        {
            PointF c = Center();
            return new PointF((float)(c.X + radius * Math.Cos(phi) * Sx()), (float)(c.Y + radius * Math.Sin(phi) * Sy()));
        }

        bool _keyDown = false;
        DateTime _keyDownAt = DateTime.MinValue;
        float _menuT = 0f;             // 0..1 radial menu expansion
        bool _menuOpen = false;
        int _sector = -1;              // 0=上新建 1=右下一个 2=下删除 3=左上一个
        bool _delConfirm = false;      // 删除确认态：摇杆左右两半 = 取消 / 确认
        DateTime _delConfirmAt = DateTime.MinValue;
        int _delHalf = -1;             // -1=不在键上 0=左半(取消) 1=右半(确认)
        Point _swipeStart;
        Color _accentCur = Color.FromArgb(0, 122, 204);
        float _switchFlash = 0f;

        PointF KeyCenter()
        {
            Rectangle r = KeyRect();
            return new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        }

        // 上=新建 右=下一个 左=上一个 下=删除
        int SectorAt(PointF p)
        {
            PointF c = KeyCenter();
            float dx = p.X - c.X, dy = p.Y - c.Y;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            if (d < 18f) return -1;
            if (Math.Abs(dy) > Math.Abs(dx)) return dy < 0 ? 0 : 2;
            return dx > 0 ? 1 : 3;
        }

        void SwitchWheel(int dir)
        {
            if (dir > 0) _mgr.Next(); else _mgr.Prev();
            _mgr.Save();
            AfterWheelSwitch();
        }

        public void RefreshWheel()
        {
            _offset = 0f; _targetOffset = 0f;
            _hover = -1; _enlarged = -1; _peekIndex = -1;
            _scales.Clear(); _enterT0.Clear();
            _switchFlash = 1f;
            Render();
        }

        void AfterWheelSwitch()
        {
            _offset = 0f; _targetOffset = 0f;
            _hover = -1; _enlarged = -1; _peekIndex = -1;
            _scales.Clear(); _enterT0.Clear();
            // 新 wheel 的图依次滑入，形成切换过渡
            for (int i = 0; i < _store.Items.Count; i++)
                _enterT0[_store.Items[i]] = DateTime.Now.AddSeconds(i * 0.045);
            _switchFlash = 1f;
            Render();
        }

        void CreateWheel()
        {
            _mgr.New();
            _mgr.Active = _mgr.Wheels.Count - 1;
            _mgr.Save();
            AfterWheelSwitch();
        }

        void DeleteWheel()
        {
            _mgr.Remove(_mgr.Active);
            _mgr.Save();
            AfterWheelSwitch();
        }
        public event EventHandler CaptureRequested;

        // buttons stack up from the corner (kept well above an auto-hiding taskbar)
        Rectangle BtnRect(int order)
        {
            PointF c = Center();
            int bx = (int)((Sx() > 0) ? c.X + 10 : c.X - 40);
            float dist = 150f + order * 40f;
            float by = c.Y + Sy() * dist;
            if (Sy() > 0) by -= 30f;
            return new Rectangle(bx, (int)by, 30, 30);
        }

        PointF HintPos(SizeF sz)
        {
            PointF c = Center();
            float bx = (Sx() > 0) ? c.X + 50 : c.X - 50 - sz.Width;
            float dy = 216f;
            float by = c.Y + Sy() * dy;
            if (Sy() > 0) by -= sz.Height;
            return new PointF(bx, by);
        }

        bool OverContent(Point p)
        {
            if (HitTest(p) >= 0) return true;
            if (CloseButtonRect().Contains(p)) return true;
            if (GearButtonRect().Contains(p)) return true;
            if (ShootButtonRect().Contains(p)) return true;
            if (KeyRect().Contains(p)) return true;
            if (_menuOpen) return true;
            PointF c = Center();
            float d = (float)Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y));
            return Math.Abs(d - _R) < _thumb * 0.75f;
        }

        void Render()
        {
            if (!IsHandleCreated || !Visible) return;
            PruneCaches();
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;

            EnsureDib(w, h);
            using (Graphics g = Graphics.FromHdc(_memDc))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);                 // zero the reused DIB (no allocation)
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                DrawWheel(g, w, h);
            }

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            Native.SIZE size = new Native.SIZE(w, h);
            Native.POINT src = new Native.POINT(0, 0);
            Native.POINT dst = new Native.POINT(Left, Top);
            Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
            bf.BlendOp = Native.AC_SRC_OVER; bf.BlendFlags = 0; bf.SourceConstantAlpha = 255; bf.AlphaFormat = Native.AC_SRC_ALPHA;
            bool ok = Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, _memDc, ref src, 0, ref bf, Native.ULW_ALPHA);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            if (!ok)
            {
                // safety fallback: render through a plain bitmap if the DIB path fails on this machine
                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (Graphics g2 = Graphics.FromImage(bmp))
                {
                    g2.SmoothingMode = SmoothingMode.AntiAlias;
                    g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g2.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g2.Clear(Color.Transparent);
                    DrawWheel(g2, w, h);
                }
                Native.PushLayered(this, bmp);
                bmp.Dispose();
            }
            _rendered = true;
        }

        IntPtr _memDc = IntPtr.Zero, _dib = IntPtr.Zero, _oldBmp = IntPtr.Zero, _bits = IntPtr.Zero;
        int _dibW, _dibH;

        void EnsureDib(int w, int h)
        {
            if (_memDc != IntPtr.Zero && _dibW == w && _dibH == h) return;
            ReleaseDib();
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            _memDc = Native.CreateCompatibleDC(screenDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            Native.BITMAPINFO bi = new Native.BITMAPINFO();
            bi.bmiHeader.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h;                 // top-down
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = 0;             // BI_RGB
            _bits = IntPtr.Zero;
            _dib = Native.CreateDIBSection(_memDc, ref bi, 0, out _bits, IntPtr.Zero, 0);
            _oldBmp = Native.SelectObject(_memDc, _dib);
            _dibW = w; _dibH = h;
        }

        void ReleaseDib()
        {
            if (_memDc == IntPtr.Zero) return;
            try
            {
                if (_oldBmp != IntPtr.Zero) Native.SelectObject(_memDc, _oldBmp);
                if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
                Native.DeleteDC(_memDc);
            }
            catch { }
            _memDc = IntPtr.Zero; _dib = IntPtr.Zero; _oldBmp = IntPtr.Zero; _bits = IntPtr.Zero;
        }

        void DrawWheel(Graphics g, int w, int h)
        {
            int a = (int)(255 * Math.Max(0f, Math.Min(1f, _show)));
            if (a <= 1) return;
            PointF c = Center();

            // ring track (quarter of the ring that lies inside the screen)
            float rr = EffR();
            float st = ArcStart();
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddArc(c.X - rr, c.Y - rr, rr * 2f, rr * 2f, st, 90f);
                if (_dropActive)
                {
                    // drop-target feedback: blue glow + bright blue track
                    using (Pen dg = new Pen(Color.FromArgb((int)(110 * a / 255f), 96, 170, 255), 40f))
                    { dg.StartCap = LineCap.Round; dg.EndCap = LineCap.Round; g.DrawPath(dg, gp); }
                    using (Pen dm = new Pen(Color.FromArgb((int)(235 * a / 255f), 120, 190, 255), 6f))
                    { dm.StartCap = LineCap.Round; dm.EndCap = LineCap.Round; g.DrawPath(dm, gp); }
                }
                using (Pen glow = new Pen(Color.FromArgb((int)(55 * a / 255f), 255, 255, 255), 30f))
                { glow.StartCap = LineCap.Round; glow.EndCap = LineCap.Round; g.DrawPath(glow, gp); }
                using (Pen mid = new Pen(Color.FromArgb((int)(110 * a / 255f), 255, 255, 255), 3f))
                { mid.StartCap = LineCap.Round; mid.EndCap = LineCap.Round; g.DrawPath(mid, gp); }
                using (Pen hair = new Pen(Color.FromArgb((int)(200 * a / 255f), 255, 255, 255), 1.3f))
                { g.DrawPath(hair, gp); }
                if (_switchFlash > 0.01f)     // 切换 Wheel 时的一圈扩散闪光
                {
                    using (Pen fp = new Pen(Color.FromArgb((int)(_switchFlash * 130f), _accentCur.R, _accentCur.G, _accentCur.B), 12f * _switchFlash + 2f))
                    { fp.StartCap = LineCap.Round; fp.EndCap = LineCap.Round; g.DrawPath(fp, gp); }
                }
            }
            // end dots on the two visible ends of the quarter
            for (int e2 = 0; e2 < 2; e2++)
            {
                double ang = (st + e2 * 90) * Math.PI / 180.0;
                float ex = (float)(c.X + rr * Math.Cos(ang));
                float ey = (float)(c.Y + rr * Math.Sin(ang));
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(170 * a / 255f), 255, 255, 255)))
                    g.FillEllipse(b, ex - 3.5f, ey - 3.5f, 7, 7);
            }

            if (_store.Items.Count == 0)
            {
                using (Font f0 = new Font("Microsoft YaHei UI", 10f))
                using (SolidBrush b0 = new SolidBrush(Color.FromArgb((int)(200 * a / 255f), 255, 255, 255)))
                {
                    string hint = "截图后会出现在这里";
                    SizeF hs = g.MeasureString(hint, f0);
                    PointF hp = HintPos(hs);
                    g.DrawString(hint, f0, b0, hp.X, hp.Y);
                }
            }

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < _store.Items.Count; i++)
                {
                    bool isEnl = (i == _enlarged);
                    if ((pass == 0) == isEnl) continue;

                    // staggered slide-in: items queue up and glide along the arc with eased motion
                    float pr = EnterProgress(i);
                    if (pr <= 0.001f) continue;
                    float phi = ItemPhi(i) + (1f - pr) * 0.30f;      // slide along the arc
                    if (phi < _phiMin - 0.50f || phi > _phiMax + 0.50f) continue;
                    PointF pc = ItemCenterAtPhi(phi);
                    int ia = (int)(a * pr);

                    float sc; if (!_scales.TryGetValue(i, out sc)) sc = 1f;
                    if (_store.Items[i] == _dragOutItem) sc *= Math.Max(0f, 1f - _dragOutProg);
                    if (_store.Items[i] == _deletingItem) { sc *= Math.Max(0f, 1f - _deleteProg); ia = (int)(ia * (1f - _deleteProg)); }
                    if (isEnl) sc *= 1f;                              // peek is a separate overlay
                    SizeF baseSz = CardSize(_store.Items[i]);
                    int iw = Math.Max(4, (int)Math.Round(baseSz.Width * sc));
                    int ih = Math.Max(4, (int)Math.Round(baseSz.Height * sc));
                    if (iw < 4 || ih < 4) continue;
                    RectangleF ir = new RectangleF((float)Math.Round(pc.X - iw / 2f), (float)Math.Round(pc.Y - ih / 2f), iw, ih);
                    RectangleF rr2 = new RectangleF(ir.X - CardPad, ir.Y - CardPad, iw + 2 * CardPad, ih + 2 * CardPad);
                    float rad = (float)Math.Round(Math.Max(5f, Math.Min(rr2.Width, rr2.Height) * 0.14f));
                    float shOff = (float)Math.Round(Math.Max(2f, rr2.Height * 0.04f));

                    using (GraphicsPath sh = Gfx.Round(new RectangleF(rr2.X, rr2.Y + shOff, rr2.Width, rr2.Height), rad))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(95 * ia / 255f), 0, 0, 0)))
                        g.FillPath(sb, sh);

                    using (GraphicsPath card = Gfx.Round(rr2, rad))
                    {
                        using (SolidBrush cb = new SolidBrush(Color.FromArgb((int)(238 * ia / 255f), 24, 26, 30)))
                            g.FillPath(cb, card);
                        if (_store.Items[i].Image != null)
                        {
                            g.SetClip(card);
                            bool ex = IsExtreme(_store.Items[i]);
                            if (ex)
                            {
                                // letterbox the picture inside the special box (no distortion)
                                SizeF isz = FitInside(_store.Items[i].Image.Size, iw - 10, ih - 10);
                                int tw = Math.Max(3, (int)Math.Round(isz.Width));
                                int th = (int)Math.Round(isz.Height); if (th < 3) th = 3;
                                Bitmap thb = ScaledThumb(_store.Items[i], tw, th);
                                RectangleF fr = new RectangleF(
                                    (float)Math.Round(pc.X - tw / 2f), (float)Math.Round(pc.Y - th / 2f), tw, th);
                                DrawWithAlpha(g, thb, fr, ia);
                            }
                            else
                            {
                                Bitmap th = ScaledThumb(_store.Items[i], iw, ih);
                                DrawWithAlpha(g, th, ir, ia);
                            }
                            g.ResetClip();
                        }
                        bool hv = (i == _hover || isEnl);
                        bool spec = IsExtreme(_store.Items[i]);
                        Color bc;
                        if (hv) bc = Color.FromArgb(96, 170, 255);
                        else if (spec) bc = Color.FromArgb(245, 166, 35);     // amber = extreme aspect
                        else bc = Color.FromArgb(255, 255, 255);
                        float bw = hv ? 3f : (spec ? 2.2f : 1.4f);
                        int ba = hv ? 255 : (spec ? 240 : 170);
                        using (Pen bp = new Pen(Color.FromArgb((int)(ba * ia / 255f), bc.R, bc.G, bc.B), bw))
                            g.DrawPath(bp, card);
                    }
                }
            }

            // (the big preview is now just a larger card scale - no separate overlay, so no desync)

            Rectangle cbr = CloseButtonRect();
            using (SolidBrush cbb = new SolidBrush(Color.FromArgb((int)((_closeHover ? 215 : 130) * a / 255f), 20, 22, 28)))
                g.FillEllipse(cbb, cbr);
            using (Pen cbp = new Pen(Color.FromArgb((int)(235 * a / 255f), 255, 255, 255), 1.8f))
            {
                g.DrawLine(cbp, cbr.Left + 10, cbr.Top + 10, cbr.Right - 10, cbr.Bottom - 10);
                g.DrawLine(cbp, cbr.Right - 10, cbr.Top + 10, cbr.Left + 10, cbr.Bottom - 10);
            }

            // gear (settings) button
            Rectangle gbr = GearButtonRect();
            using (SolidBrush gbb = new SolidBrush(Color.FromArgb((int)((_gearHover ? 215 : 130) * a / 255f), 20, 22, 28)))
                g.FillEllipse(gbb, gbr);
            float gcx = gbr.X + gbr.Width / 2f, gcy = gbr.Y + gbr.Height / 2f;
            float gro = gbr.Width * 0.28f;
            using (Pen gp2 = new Pen(Color.FromArgb((int)(235 * a / 255f), 255, 255, 255), 1.8f))
            {
                g.DrawEllipse(gp2, gcx - gro * 0.62f, gcy - gro * 0.62f, gro * 1.24f, gro * 1.24f);
                for (int k = 0; k < 8; k++)
                {
                    double th = k * Math.PI / 4.0;
                    float x1 = (float)(gcx + Math.Cos(th) * gro * 0.7f), y1 = (float)(gcy + Math.Sin(th) * gro * 0.7f);
                    float x2 = (float)(gcx + Math.Cos(th) * gro * 1.28f), y2 = (float)(gcy + Math.Sin(th) * gro * 1.28f);
                    g.DrawLine(gp2, x1, y1, x2, y2);
                }
            }

            // 万能键（弧线内侧中点的摇杆式大圆盘，半透明）
            Rectangle kr = KeyRect();
            Color acc = _accentCur;
            float kcx = kr.X + kr.Width / 2f, kcy = kr.Y + kr.Height / 2f;
            float krr = kr.Width / 2f;

            if (_delConfirm)
            {
                // 左半 = 取消（灰绿），右半 = 确认删除（红），鼠标所在半更亮
                using (GraphicsPath lp = new GraphicsPath())
                {
                    lp.AddArc(kr, 90f, 180f);
                    lp.CloseFigure();
                    int la = (_delHalf == 0) ? 250 : 205;
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb((int)(la * a / 255f), 62, 178, 112)))
                        g.FillPath(lb, lp);
                }
                using (GraphicsPath rp = new GraphicsPath())
                {
                    rp.AddArc(kr, 270f, 180f);
                    rp.CloseFigure();
                    int ra = (_delHalf == 1) ? 255 : 215;
                    using (SolidBrush rb = new SolidBrush(Color.FromArgb((int)(ra * a / 255f), 232, 64, 80)))
                        g.FillPath(rb, rp);
                }
                using (Pen kp2 = new Pen(Color.FromArgb((int)(235 * a / 255f), 255, 255, 255), 1.6f))
                    g.DrawEllipse(kp2, kr);
                using (Pen lp2 = new Pen(Color.FromArgb((int)(235 * a / 255f), 255, 255, 255), 1.4f))
                    g.DrawLine(lp2, kcx, kr.Y + 6f, kcx, kr.Bottom - 6f);
                using (Font fh = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
                {
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb((int)(245 * a / 255f), 255, 255, 255)))
                    {
                        SizeF s1 = g.MeasureString("取消", fh);
                        g.DrawString("取消", fh, tb, kcx - krr / 2f - s1.Width / 2f, kcy - s1.Height / 2f);
                        SizeF s2b = g.MeasureString("确认", fh);
                        g.DrawString("确认", fh, tb, kcx + krr / 2f - s2b.Width / 2f, kcy - s2b.Height / 2f);
                    }
                }
                using (Font f3 = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush b3 = new SolidBrush(Color.FromArgb((int)(235 * a / 255f), 255, 210, 210)))
                {
                    string t3 = "删除「" + _mgr.ActiveWheel.Name + "」？点左半取消 / 右半确认";
                    SizeF s3 = g.MeasureString(t3, f3);
                    g.DrawString(t3, f3, b3, kcx - s3.Width / 2f, kr.Y - s3.Height - 4);
                }
            }
            else
            {
            using (GraphicsPath kg = new GraphicsPath())
            {
                kg.AddEllipse(kr);
                using (SolidBrush kb = new SolidBrush(Color.FromArgb((int)((_keyDown ? 165 : 120) * a / 255f), acc.R, acc.G, acc.B)))
                    g.FillPath(kb, kg);
                using (Pen kgp = new Pen(Color.FromArgb((int)(150 * a / 255f), 255, 255, 255), 1.4f))
                    g.DrawPath(kgp, kg);
                using (Pen kin = new Pen(Color.FromArgb((int)(70 * a / 255f), 255, 255, 255), 1f))
                    g.DrawEllipse(kin, kcx - krr * 0.62f, kcy - krr * 0.62f, krr * 1.24f, krr * 1.24f);
            }
            using (Pen kp = new Pen(Color.FromArgb((int)(215 * a / 255f), 255, 255, 255), 2f))
            {
                for (int q = 0; q < 4; q++)
                {
                    double th = -Math.PI / 2 + q * Math.PI / 2;
                    float px = (float)(kcx + Math.Cos(th) * 15f), py = (float)(kcy + Math.Sin(th) * 15f);
                    g.DrawEllipse(kp, px - 3.4f, py - 3.4f, 6.8f, 6.8f);
                }
                g.DrawEllipse(kp, kcx - 4f, kcy - 4f, 8f, 8f);
            }
            }
            // 当前 wheel 名
            if (a > 60)
            {
                using (Font fw = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
                using (SolidBrush bw = new SolidBrush(Color.FromArgb((int)(240 * a / 255f), 255, 255, 255)))
                {
                    string wn = _mgr.ActiveWheel.Name;
                    SizeF ws = g.MeasureString(wn, fw);
                    float wx = kcx - ws.Width / 2f;
                    float wy = kr.Y + kr.Height + 4f;
                    using (SolidBrush bgw = new SolidBrush(Color.FromArgb((int)(165 * a / 255f), 0, 0, 0)))
                        g.FillRectangle(bgw, wx - 6, wy, ws.Width + 12, ws.Height + 2);
                    g.DrawString(wn, fw, bw, wx, wy + 1);
                }
            }

            // 圆盘菜单
            if (_menuT > 0.01f)
            {
                PointF kc = KeyCenter();
                float R = 78f * (0.55f + 0.45f * _menuT);
                int alpha = (int)(_menuT * 235);
                string[] labels = { "新建", "下一个", "删除", "上一个" };
                for (int s2 = 0; s2 < 4; s2++)
                {
                    bool sel = (_sector == s2);
                    Color sc;
                    if (s2 == 2) sc = Color.FromArgb(sel ? 230 : 170, 214, 70, 84);        // 删除=红
                    else sc = sel ? Color.FromArgb(235, acc.R, acc.G, acc.B) : Color.FromArgb(170, 26, 28, 33);
                    using (GraphicsPath gp2 = new GraphicsPath())
                    {
                        gp2.AddArc(kc.X - R, kc.Y - R, R * 2, R * 2, s2 * 90 - 135, 88);
                        gp2.AddLine(kc.X, kc.Y, kc.X, kc.Y);
                        gp2.CloseFigure();
                        using (SolidBrush sb2 = new SolidBrush(Color.FromArgb(alpha, sc.R, sc.G, sc.B)))
                            g.FillPath(sb2, gp2);
                        using (Pen sp2 = new Pen(Color.FromArgb((int)(alpha * 0.5f), 255, 255, 255), 1.2f))
                            g.DrawPath(sp2, gp2);
                    }
                    double mid = (-90 + s2 * 90) * Math.PI / 180.0;
                    float tx = (float)(kc.X + Math.Cos(mid) * R * 0.62f);
                    float ty = (float)(kc.Y + Math.Sin(mid) * R * 0.62f);
                    string lab = labels[s2];
                    using (Font f2b = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                    using (SolidBrush sb3 = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                    {
                        SizeF ls = g.MeasureString(lab, f2b);
                        g.DrawString(lab, f2b, sb3, tx - ls.Width / 2f, ty - ls.Height / 2f);
                    }
                }
            }
            Rectangle sbr = ShootButtonRect();
            using (SolidBrush sbb = new SolidBrush(Color.FromArgb((int)((_shootHover ? 240 : 190) * a / 255f), 0, 122, 204)))
                g.FillEllipse(sbb, sbr);
            using (Pen sp = new Pen(Color.FromArgb((int)(245 * a / 255f), 255, 255, 255), 1.8f))
            {
                float cx2 = sbr.X + sbr.Width / 2f, cy2 = sbr.Y + sbr.Height / 2f;
                g.DrawRectangle(sp, cx2 - 8f, cy2 - 5f, 16f, 11f);
                g.DrawEllipse(sp, cx2 - 3.4f, cy2 - 2.6f, 6.8f, 6.8f);
                g.DrawLine(sp, cx2 - 4f, cy2 - 8f, cx2 + 4f, cy2 - 8f);
            }

            if (_store.Items.Count > 0)
            {
                int cur = (int)Math.Round(_offset) + 1;
                if (cur < 1) cur = 1;
                if (cur > _store.Items.Count) cur = _store.Items.Count;
                string idx = cur + " / " + _store.Items.Count;
                float fs = Math.Max(9f, Math.Min(40f, _settings.LabelSize));
                using (Font f = new Font("Microsoft YaHei UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    SizeF sz = g.MeasureString(idx, f);
                    PointF pp = HintPos(sz);
                    RectangleF pill = new RectangleF(pp.X, pp.Y + 24, sz.Width + 16, sz.Height + 8);
                    using (GraphicsPath pg = Gfx.Round(pill, pill.Height / 2f))
                    using (SolidBrush pb = new SolidBrush(Color.FromArgb((int)(120 * a / 255f), 12, 14, 18)))
                        g.FillPath(pb, pg);
                    using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(245 * a / 255f), 255, 255, 255)))
                        g.DrawString(idx, f, br, pill.X + 8, pill.Y + (pill.Height - sz.Height) / 2f + 1);
                }
            }
        }

        void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            Cursor.Current = Cursors.Arrow;            // keep the normal pointer (no odd drag cursor)
            if (_dragOutItem != null && _dragOutProg < 1f)
            {
                _dragOutProg = Math.Min(1f, _dragOutProg + 0.16f);   // pull-out collapse during the drag
                Render();
            }
            if (_proxy.Visible) _proxy.MoveTo(OffsetPt());
        }

        Point OffsetPt()
        {
            Point p = Cursor.Position;
            return new Point(p.X + 18, p.Y + 18);
        }

        bool _dropActive = false;
        bool _returnedToWheel = false;

        // dragging back over the ring highlights it as a drop target
        void OnDragOverWheel(object sender, DragEventArgs e)
        {
            Point cp = PointToClient(new Point(e.X, e.Y));
            bool over = OverContent(cp);
            e.Effect = over ? DragDropEffects.Move : DragDropEffects.None;
            if (over != _dropActive) { _dropActive = over; Render(); }
        }

        void OnDragDropWheel(object sender, DragEventArgs e)
        {
            Point cp = PointToClient(new Point(e.X, e.Y));
            if (OverContent(cp))
            {
                _returnedToWheel = true;
                e.Effect = DragDropEffects.Move;
            }
            else e.Effect = DragDropEffects.None;
            _dropActive = false;
            Render();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _lastActive = DateTime.Now;

            if (_keyDown)
            {
                if (_settings.SwitchMode == "swipe")
                {
                    // 长按后左右滑动切换 wheel
                    if ((DateTime.Now - _keyDownAt).TotalMilliseconds > 240)
                    {
                        int dx = e.X - _swipeStart.X;
                        if (Math.Abs(dx) > 70)
                        {
                            SwitchWheel(dx > 0 ? 1 : -1);
                            _swipeStart = e.Location;      // 允许连续滑动
                        }
                    }
                }
                else if (_menuOpen)
                {
                    int s2 = SectorAt(e.Location);
                    if (s2 != _sector) { _sector = s2; Render(); }
                }
                return;
            }

            bool ch = CloseButtonRect().Contains(e.Location);
            bool gh = GearButtonRect().Contains(e.Location);
            bool sh2 = ShootButtonRect().Contains(e.Location);
            int hh = HitTest(e.Location);
            bool need = false;
            if (hh != _hover) { _hover = hh; need = true; }
            if (ch != _closeHover) { _closeHover = ch; need = true; }
            if (gh != _gearHover) { _gearHover = gh; need = true; }
            if (sh2 != _shootHover) { _shootHover = sh2; need = true; }
            if (need) Render();
            if (_maybeDrag && _dragIndex >= 0)
            {
                // 更明确的手感：按下后移动超过 10px 才算拖动（避免误触发/判定飘忽）
                if (Math.Abs(e.X - _mouseDownPt.X) > 10 || Math.Abs(e.Y - _mouseDownPt.Y) > 10)
                {
                    _maybeDrag = false; _enlarged = -1; _holdIndex = -1; Render();
                    StartDragOut(_dragIndex);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _lastActive = DateTime.Now;

            // 删除确认态：摇杆已分成左右两半，点哪边执行哪边
            if (_delConfirm)
            {
                Rectangle krd = KeyRect();
                if (krd.Contains(e.Location))
                {
                    if (e.X < krd.X + krd.Width / 2f) _delConfirm = false;      // 左半 = 取消
                    else { _delConfirm = false; DeleteWheel(); }                // 右半 = 确认删除
                }
                else _delConfirm = false;                                       // 点别处 = 取消
                Render();
                return;
            }

            if (e.Button == MouseButtons.Left && KeyRect().Contains(e.Location))
            {
                _keyDown = true; _keyDownAt = DateTime.Now;
                _menuOpen = false; _menuT = 0f; _sector = -1;
                _swipeStart = e.Location;
                return;
            }
            if (e.Button == MouseButtons.Left && ShootButtonRect().Contains(e.Location))
            {
                if (CaptureRequested != null) CaptureRequested(this, EventArgs.Empty);
                return;
            }
            if (e.Button == MouseButtons.Left && GearButtonRect().Contains(e.Location))
            {
                if (SettingsRequested != null) SettingsRequested(this, EventArgs.Empty);
                return;
            }
            if (e.Button == MouseButtons.Left && CloseButtonRect().Contains(e.Location)) { HideWheel(); return; }
            int hh = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && hh >= 0)
            {
                _maybeDrag = true; _dragIndex = hh; _mouseDownPt = e.Location;
                _holdIndex = hh; _holdStart = DateTime.Now;
            }
            else if (e.Button == MouseButtons.Right && hh >= 0)
            {
                bool single = (_settings.DeleteMode == "single");
                bool dbl = false;
                if (!single)
                {
                    dbl = (_lastRightIndex == hh && (DateTime.Now - _lastRightClick).TotalMilliseconds < 450);
                    _lastRightClick = DateTime.Now; _lastRightIndex = hh;
                }
                if (single || dbl)
                {
                    _deletingItem = _store.Items[hh];      // play the collapse, removal happens in AnimTick
                    _deleteProg = 0f;
                    _enlarged = -1; _hover = -1;
                    Render();
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_keyDown)
            {
                _keyDown = false;
                if (_menuOpen)
                {
                    int s2 = SectorAt(e.Location);
                    if (s2 == 0) CreateWheel();
                    else if (s2 == 1) SwitchWheel(1);
                    else if (s2 == 3) SwitchWheel(-1);
                    else if (s2 == 2) { _delConfirm = true; _delConfirmAt = DateTime.Now; }   // 松开后进入左右两半确认态
                    _menuOpen = false; _menuT = 0f; _sector = -1;
                }
                Render();
                return;
            }
            _maybeDrag = false; _dragIndex = -1; _holdIndex = -1;
            if (_enlarged >= 0) { _enlarged = -1; Render(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int hh = HitTest(e.Location);
            if (hh >= 0 && _store.Items[hh].Image != null)
            {
                try { Clipboard.SetImage(_store.Items[hh].Image); } catch { }
            }
        }

        void StartDragOut(int index)
        {
            if (index < 0 || index >= _store.Items.Count) return;
            StoreItem it = _store.Items[index];
            string file = _store.EnsureFile(it);
            DataObject data = new DataObject();
            if (file != null) data.SetData(DataFormats.FileDrop, new string[] { file });
            try { data.SetData(DataFormats.Bitmap, true, it.Image); } catch { }

            _maybeDrag = false; _dragIndex = -1; _holdIndex = -1;
            _dragOutItem = it; _dragOutProg = 0f;
            _enlarged = -1; _hover = -1;

            // follow-preview appears immediately, then we enter the drag at once (the pull-out
            // collapse plays DURING the drag, driven from GiveFeedback -> no start-up lag)
            try { _proxy.ShowFor(it.Image, OffsetPt()); } catch { }
            Render();

            DragDropEffects eff = DragDropEffects.None;
            try
            {
                eff = DoDragDrop(data, DragDropEffects.Copy);
            }
            catch { }
            finally { _proxy.Hide(); }

            bool taken = (eff != DragDropEffects.None) && !_returnedToWheel;
            bool returned = _returnedToWheel;
            _returnedToWheel = false;
            _dragOutItem = null;
            _dragOutProg = 0f;
            if (taken)
            {
                _store.Items.Remove(it);
                _thumbCache.Remove(it);
                _enterT0.Remove(it);
                _scales.Clear();
                if (_targetOffset > Math.Max(0, _store.Items.Count - 1)) _targetOffset = Math.Max(0, _store.Items.Count - 1);
                if (_offset > _targetOffset) _offset = _targetOffset;
            }
            else if (returned)
            {
                // dropped back onto the ring -> pop the card back in with an animation
                int idx = _store.Items.IndexOf(it);
                if (idx >= 0) _scales[idx] = 0.18f;
            }
            _hover = -1;
            Render();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _lastActive = DateTime.Now;
            _targetOffset -= e.Delta / 120;
            if (_targetOffset < 0) _targetOffset = 0;
            if (_targetOffset > Math.Max(0, _store.Items.Count - 1)) _targetOffset = Math.Max(0, _store.Items.Count - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { ReleaseDib(); } catch { } }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ShowMsg) { ShowWheel(); return; }        // second launch asks us to show
            if (m.Msg == 0x0084)
            {
                Point cp = PointToClient(Cursor.Position);
                if (cp.X >= 0 && cp.Y >= 0 && cp.X < Width && cp.Y < Height && !OverContent(cp))
                { m.Result = (IntPtr)(-1); return; }
            }
            base.WndProc(ref m);
        }

        public static readonly uint ShowMsg = Native.RegisterWindowMessage("SnapWheel_SHOW");
    }

    // 管理 Wheels：重命名 / 换色 / 新建 / 删除
    class WheelsForm : Form
    {
        WheelManager _mgr;
        ListBox _list;
        TextBox _name;
        ComboBox _color;
        bool _loading = false;

        public WheelsForm(WheelManager mgr)
        {
            _mgr = mgr;
            Text = "管理 Wheel";
            Icon = Brand.Get();
            Font = new Font("Microsoft YaHei UI", 9.5f);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(430, 330);

            _list = new ListBox();
            _list.Bounds = new Rectangle(16, 16, 240, 250);
            _list.IntegralHeight = false;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = 26;
            _list.DrawItem += new DrawItemEventHandler(OnDrawItem);
            _list.SelectedIndexChanged += new EventHandler(OnSelect);
            Controls.Add(_list);

            Label l1 = new Label(); l1.Text = "名称"; l1.Bounds = new Rectangle(272, 18, 60, 22);
            Controls.Add(l1);
            _name = new TextBox(); _name.Bounds = new Rectangle(272, 40, 140, 24);
            _name.TextChanged += new EventHandler(OnNameChanged);
            Controls.Add(_name);

            Label l2 = new Label(); l2.Text = "颜色"; l2.Bounds = new Rectangle(272, 74, 60, 22);
            Controls.Add(l2);
            _color = new ComboBox();
            _color.DropDownStyle = ComboBoxStyle.DropDownList;
            _color.Bounds = new Rectangle(272, 96, 140, 24);
            for (int i = 0; i < Palette.Names.Length; i++) _color.Items.Add(Palette.Names[i]);
            _color.SelectedIndexChanged += new EventHandler(OnColorChanged);
            Controls.Add(_color);

            RoundButton add = new RoundButton();
            add.Text = "新建"; add.Size = new Size(66, 30);
            add.Fill = Color.FromArgb(0, 122, 204); add.FillHover = Color.FromArgb(0, 140, 232);
            add.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            add.Location = new Point(272, 136);
            add.Click += new EventHandler(delegate(object o, EventArgs e2) { _mgr.New(); _mgr.Save(); Reload(_mgr.Wheels.Count - 1); });
            Controls.Add(add);

            RoundButton del = new RoundButton();
            del.Text = "删除"; del.Size = new Size(66, 30);
            del.Fill = Color.FromArgb(214, 70, 84); del.FillHover = Color.FromArgb(230, 90, 104);
            del.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            del.Location = new Point(346, 136);
            del.Click += new EventHandler(delegate(object o, EventArgs e2) {
                if (_list.SelectedIndex < 0) return;
                if (MessageBox.Show("确定删除 Wheel「" + _mgr.Wheels[_list.SelectedIndex].Name + "」及其截图？",
                        "删除 Wheel", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                _mgr.Remove(_list.SelectedIndex); _mgr.Save(); Reload(Math.Min(_list.SelectedIndex, _mgr.Wheels.Count - 1));
            });
            Controls.Add(del);

            RoundButton close = new RoundButton();
            close.Text = "完成"; close.Size = new Size(140, 34);
            close.Fill = Color.FromArgb(233, 234, 238); close.FillHover = Color.FromArgb(222, 224, 230);
            close.TextColor = Color.FromArgb(58, 60, 66);
            close.Location = new Point(272, 178);
            close.Click += new EventHandler(delegate(object o, EventArgs e2) { _mgr.Save(); DialogResult = DialogResult.OK; Close(); });
            Controls.Add(close);

            Label tip = new Label();
            tip.Text = "点一下左侧即可切换为当前 Wheel";
            tip.ForeColor = Color.FromArgb(150, 150, 158);
            tip.Bounds = new Rectangle(16, 276, 260, 22);
            Controls.Add(tip);

            Reload(_mgr.Active);
        }

        void Reload(int sel)
        {
            _loading = true;
            _list.Items.Clear();
            for (int i = 0; i < _mgr.Wheels.Count; i++)
                _list.Items.Add((i == _mgr.Active ? "● " : "   ") + _mgr.Wheels[i].Name);
            if (sel >= 0 && sel < _list.Items.Count) _list.SelectedIndex = sel;
            _loading = false;
            SyncFields();
        }

        void SyncFields()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _mgr.Wheels.Count) return;
            _loading = true;
            _name.Text = _mgr.Wheels[i].Name;
            _color.SelectedIndex = Math.Abs(_mgr.Wheels[i].ColorIndex) % Palette.Names.Length;
            _loading = false;
        }

        void OnSelect(object sender, EventArgs e)
        {
            if (_loading) return;
            int i = _list.SelectedIndex;
            if (i < 0) return;
            _mgr.Active = i;
            _mgr.Save();
            Reload(i);
        }

        void OnNameChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _mgr.Wheels.Count) return;
            _mgr.Wheels[i].Name = _name.Text;
            _list.Items[i] = (i == _mgr.Active ? "● " : "   ") + _name.Text;
            _mgr.ApplySettings();
            _mgr.Save();
        }

        void OnColorChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _mgr.Wheels.Count) return;
            _mgr.Wheels[i].ColorIndex = _color.SelectedIndex;
            _mgr.Save();
            _list.Invalidate();
        }

        void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            Wheel w = _mgr.Wheels[e.Index];
            using (SolidBrush b = new SolidBrush(w.Accent))
                e.Graphics.FillEllipse(b, e.Bounds.Left + 6, e.Bounds.Top + 7, 12, 12);
            using (SolidBrush t = new SolidBrush(e.ForeColor))
                e.Graphics.DrawString(_list.Items[e.Index].ToString(), e.Font, t, e.Bounds.Left + 26, e.Bounds.Top + 5);
        }
    }

    class SettingsForm : Form
    {
        public SettingsForm(Settings s)
        {
            Text = AppInfo.Name + " 设置";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(250, 250, 252);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20, 14, 20, 12);

            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.Dock = DockStyle.Fill;
            root.Margin = new Padding(0);
            Controls.Add(root);

            Label head = new Label();
            head.AutoSize = true;
            head.Text = AppInfo.Name + " 设置";
            head.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(32, 34, 38);
            head.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(head);

            CheckBox chkDisk = new CheckBox();
            chkDisk.AutoSize = true;
            chkDisk.Text = "保存到硬盘（否则只存内存，退出即清）";
            chkDisk.Checked = s.SaveToDisk;
            chkDisk.Margin = new Padding(0, 4, 0, 4);
            root.Controls.Add(Section("行为"));
            root.Controls.Add(chkDisk);

            CheckBox chkAutoStart = new CheckBox();
            chkAutoStart.AutoSize = true;
            chkAutoStart.Text = "开机自动启动（登录后自动在后台运行）";
            chkAutoStart.Checked = AutoRun.IsEnabled();
            chkAutoStart.Margin = new Padding(0, 4, 0, 4);
            root.Controls.Add(chkAutoStart);

            TextBox txtDir = new TextBox();
            txtDir.Text = s.Dir;
            txtDir.Width = 300;
            txtDir.Margin = new Padding(0, 5, 8, 0);
            Button browse = new Button();
            browse.Text = "浏览";
            browse.AutoSize = true;
            browse.MinimumSize = new Size(60, 26);
            browse.Margin = new Padding(0, 4, 0, 0);
            browse.Click += new EventHandler(delegate(object o, EventArgs e2) {
                FolderBrowserDialog d = new FolderBrowserDialog();
                if (d.ShowDialog() == DialogResult.OK) txtDir.Text = d.SelectedPath;
            });
            root.Controls.Add(Row(MkLabel("保存目录"), txtDir, browse));

            root.Controls.Add(Section("外观"));
            NumericUpDown numMax = Num(1, 999, s.MaxCount);
            NumericUpDown numThumb = Num(40, 260, s.ThumbSize);
            root.Controls.Add(Row(MkLabel("最多保留张数"), numMax, Gap(24), MkLabel("缩略图大小"), numThumb));

            NumericUpDown numRad = Num(120, 700, s.Radius);
            NumericUpDown numSlots = Num(2, 12, s.Slots);
            NumericUpDown numLabel = Num(9, 40, s.LabelSize);
            root.Controls.Add(Row(MkLabel("环半径"), numRad, Gap(24), MkLabel("弧上张数"), numSlots, Gap(24), MkLabel("序号字号"), numLabel));

            CheckBox chkAuto = new CheckBox();
            chkAuto.AutoSize = true;
            chkAuto.Text = "空闲后自动收起轮盘";
            chkAuto.Checked = s.AutoHide;
            chkAuto.Margin = new Padding(0, 4, 0, 4);
            NumericUpDown numSec = Num(2, 600, s.AutoHideSeconds);
            root.Controls.Add(Row(chkAuto, Gap(16), MkLabel("空闲秒数"), numSec));

            CheckBox chkTop = new CheckBox();
            chkTop.AutoSize = true;
            chkTop.Text = "总在最前（始终置顶显示）";
            chkTop.Checked = s.AlwaysOnTop;
            chkTop.Margin = new Padding(0, 4, 0, 4);
            root.Controls.Add(chkTop);

            ComboBox cmb = new ComboBox();
            cmb.DropDownStyle = ComboBoxStyle.DropDownList;
            cmb.Width = 170;
            cmb.Margin = new Padding(0, 6, 0, 0);
            cmb.Items.AddRange(HotkeyUtil.Names);
            cmb.SelectedItem = s.Hotkey;
            if (cmb.SelectedIndex < 0) cmb.SelectedIndex = 0;
            root.Controls.Add(Row(MkLabel("截图热键"), cmb));

            ComboBox cmbCorner = new ComboBox();
            cmbCorner.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCorner.Width = 170;
            cmbCorner.Margin = new Padding(0, 6, 0, 0);
            cmbCorner.Items.AddRange(new object[] { "左下角", "右下角", "左上角", "右上角" });
            cmbCorner.SelectedIndex = CornerIndex(s.Corner);
            root.Controls.Add(Row(MkLabel("圆环位置"), cmbCorner));

            ComboBox cmbDel = new ComboBox();
            cmbDel.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDel.Width = 170;
            cmbDel.Margin = new Padding(0, 6, 0, 0);
            cmbDel.Items.AddRange(new object[] { "双击右键删除", "单击右键删除" });
            cmbDel.SelectedIndex = (s.DeleteMode == "single") ? 1 : 0;
            root.Controls.Add(Row(MkLabel("删除方式"), cmbDel));

            ComboBox cmbSwitch = new ComboBox();
            cmbSwitch.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSwitch.Width = 170;
            cmbSwitch.Margin = new Padding(0, 6, 0, 0);
            cmbSwitch.Items.AddRange(new object[] { "长按万能键弹圆盘", "长按后左右滑动" });
            cmbSwitch.SelectedIndex = (s.SwitchMode == "swipe") ? 1 : 0;
            root.Controls.Add(Row(MkLabel("Wheel 切换"), cmbSwitch));

            RoundButton ok = new RoundButton();
            ok.Text = "确定";
            ok.Size = new Size(104, 36);
            ok.Fill = Color.FromArgb(0, 122, 204);
            ok.FillHover = Color.FromArgb(0, 140, 232);
            ok.TextColor = Color.White;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            ok.Margin = new Padding(0, 10, 12, 0);
            ok.Click += new EventHandler(delegate(object o, EventArgs e2) {
                s.SaveToDisk = chkDisk.Checked;
                s.Dir = txtDir.Text.Trim();
                s.MaxCount = (int)numMax.Value;
                s.AutoHide = chkAuto.Checked;
                s.AutoHideSeconds = (int)numSec.Value;
                s.AlwaysOnTop = chkTop.Checked;
                s.ThumbSize = (int)numThumb.Value;
                s.Radius = (int)numRad.Value;
                s.Slots = (int)numSlots.Value;
                s.LabelSize = (int)numLabel.Value;
                s.Corner = IndexCorner(cmbCorner.SelectedIndex);
                s.AutoStart = chkAutoStart.Checked;
                s.DeleteMode = (cmbDel.SelectedIndex == 1) ? "single" : "double";
                s.SwitchMode = (cmbSwitch.SelectedIndex == 1) ? "swipe" : "radial";
                if (cmb.SelectedItem != null) s.Hotkey = cmb.SelectedItem.ToString();
                AutoRun.Apply(s.AutoStart);
                s.Save();
                DialogResult = DialogResult.OK;
                Close();
            });
            RoundButton cancel = new RoundButton();
            cancel.Text = "取消";
            cancel.Size = new Size(104, 36);
            cancel.Fill = Color.FromArgb(234, 235, 240);
            cancel.FillHover = Color.FromArgb(222, 224, 230);
            cancel.TextColor = Color.FromArgb(58, 60, 66);
            cancel.Font = new Font("Microsoft YaHei UI", 10f);
            cancel.Margin = new Padding(0, 10, 0, 0);
            cancel.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });
            root.Controls.Add(Row(Gap(250), ok, cancel));

            Label about = new Label();
            about.AutoSize = true;
            about.Text = AppInfo.Name + "   v" + AppInfo.Version + "        作者：" + AppInfo.Author;
            about.ForeColor = Color.FromArgb(150, 150, 160);
            about.Margin = new Padding(0, 16, 0, 0);
            root.Controls.Add(about);
        }

        static int CornerIndex(string c)
        {
            if (c == "BR") return 1;
            if (c == "TL") return 2;
            if (c == "TR") return 3;
            return 0;
        }

        static string IndexCorner(int i)
        {
            if (i == 1) return "BR";
            if (i == 2) return "TL";
            if (i == 3) return "TR";
            return "BL";
        }

        static Label MkLabel(string t)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = t;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(0, 10, 12, 0);
            return l;
        }

        static Label Section(string t)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = t;
            l.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(0, 122, 204);
            l.Margin = new Padding(0, 14, 0, 2);
            return l;
        }

        static Control Gap(int w)
        {
            Control c = new Control();
            c.Width = w; c.Height = 1;
            c.Margin = new Padding(0);
            return c;
        }

        static NumericUpDown Num(int mn, int mx, int val)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = mn; n.Maximum = mx; n.Value = val;
            n.Width = 72;
            n.Height = 26;
            n.Margin = new Padding(0, 7, 10, 0);
            return n;
        }

        static FlowLayoutPanel Row(params Control[] cs)
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = false;
            f.Margin = new Padding(0, 7, 0, 7);
            for (int i = 0; i < cs.Length; i++) f.Controls.Add(cs[i]);
            return f;
        }
    }

    class HotkeyForm : Form
    {
        public event EventHandler Hotkey;
        public bool Registered = false;

        public HotkeyForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-2000, -2000);
            Size = new Size(1, 1);
            IntPtr h = Handle;
        }

        public bool Register(uint mods, uint vk)
        {
            try { Native.UnregisterHotKey(Handle, Native.HOTKEY_ID); } catch { }
            bool ok = Native.RegisterHotKey(Handle, Native.HOTKEY_ID, mods | Native.MOD_NOREPEAT, vk);
            Registered = ok;
            return ok;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == Native.HOTKEY_ID)
                if (Hotkey != null) Hotkey(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }
    }

    class AppCtx : ApplicationContext
    {
        Settings _settings;
        Store _store;
        WheelManager _wheels;
        NotifyIcon _tray;
        HotkeyForm _hotkey;
        WheelForm _wheel;

        public AppCtx()
        {
            _settings = Settings.Load();
            _wheels = new WheelManager(_settings);
            _wheels.LoadImagesFromDisk();
            _store = _wheels.ActiveStore;

            _hotkey = new HotkeyForm();
            _hotkey.Hotkey += new EventHandler(OnHotkey);

            _wheel = new WheelForm(_wheels, _settings);
            _wheel.SettingsRequested += new EventHandler(OnSettings);
            _wheel.CaptureRequested += new EventHandler(OnHotkey);

            _tray = new NotifyIcon();
            _tray.Icon = Brand.Get();
            _tray.Text = "SnapWheel 截图轮盘";
            _tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("截图", null, new EventHandler(OnHotkey));
            menu.Items.Add("显示/隐藏轮盘", null, new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); }));
            menu.Items.Add("管理 Wheel…", null, new EventHandler(OnWheels));
            menu.Items.Add("设置…", null, new EventHandler(OnSettings));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, new EventHandler(delegate(object o, EventArgs e) { Quit(); }));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); });

            RegisterHotkeyAndNotify();

            if (_settings.ShowWheelOnStart) _wheel.ShowWheel();
        }

        void RegisterHotkeyAndNotify()
        {
            uint m, v;
            bool ok = HotkeyUtil.TryParse(_settings.Hotkey, out m, out v) && _hotkey.Register(m, v);
            if (!ok)
            {
                for (int i = 0; i < HotkeyUtil.Names.Length; i++)
                {
                    string name = HotkeyUtil.Names[i];
                    if (name == _settings.Hotkey) continue;
                    if (HotkeyUtil.TryParse(name, out m, out v) && _hotkey.Register(m, v))
                    { _settings.Hotkey = name; _settings.Save(); ok = true; break; }
                }
            }
            string tip = ok ? ("已就绪，热键 " + _settings.Hotkey) : "热键注册失败，请在设置里换一个";
            try
            {
                _tray.Text = "SnapWheel 截图轮盘 (" + _settings.Hotkey + ")";
                _tray.ShowBalloonTip(3000, "SnapWheel", tip, ToolTipIcon.Info);
            }
            catch { }
        }

        void OnWheels(object sender, EventArgs e)
        {
            _wheel.TopMost = false;
            WheelsForm f = new WheelsForm(_wheels);
            f.ShowDialog();
            _wheels.ApplySettings();
            _wheel.TopMost = _settings.AlwaysOnTop;
            _wheel.RefreshWheel();
        }

        void OnSettings(object sender, EventArgs e)
        {
            bool wasTop = _wheel.TopMost;
            _wheel.TopMost = false;            // don't float above the settings dialog
            SettingsForm f = new SettingsForm(_settings);
            DialogResult r = f.ShowDialog();
            if (r == DialogResult.OK)
            {
                _wheels.ApplySettings();
                _wheel.ApplyTopMost();
                _wheel.ApplyLayout();
                RegisterHotkeyAndNotify();
            }
            else
            {
                _wheel.TopMost = wasTop;
            }
        }

        void OnHotkey(object sender, EventArgs e) { CaptureRegion(); }

        void CaptureRegion()
        {
            bool wasWheelVisible = _wheel.Visible;
            if (wasWheelVisible) _wheel.Hide();      // don't let the topmost wheel sit over the capture overlay

            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap shot = new Bitmap(vs.Width, vs.Height);
            try
            {
                using (Graphics g = Graphics.FromImage(shot))
                    g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
            }
            catch { shot.Dispose(); if (wasWheelVisible) _wheel.ShowWheel(); return; }

            OverlayForm ov = new OverlayForm(vs, shot);
            ov.ShowDialog();
            if (ov.Result != null)
            {
                Store st = _wheels.ActiveStore;
                StoreItem ni = st.Add(ov.Result);
                _wheel.MarkNew(ni);              // only the brand-new shot plays the slide-in
                _wheel.ShowWheel();
            }
            else if (wasWheelVisible)
            {
                _wheel.ShowWheel();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_hotkey != null) { try { Native.UnregisterHotKey(_hotkey.Handle, Native.HOTKEY_ID); } catch { } _hotkey.Dispose(); }
            }
            base.Dispose(disposing);
        }

        void Quit()
        {
            try { _tray.Visible = false; } catch { }
            if (_wheel != null && _wheel.Visible)
            {
                _wheel.HideWheel();                     // play the fade-out first
                Timer t = new Timer();
                t.Interval = 650;
                t.Tick += new EventHandler(delegate(object o, EventArgs e2) { t.Stop(); t.Dispose(); ExitThread(); });
                t.Start();
            }
            else ExitThread();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            System.Threading.Mutex mtx = new System.Threading.Mutex(true, "SnapWheel_SingleInstance", out createdNew);
            if (!createdNew)
            {
                // already running: ask the existing instance to show its wheel, then quit quietly
                Native.PostMessage((IntPtr)0xFFFF, WheelForm.ShowMsg, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AppCtx());
            GC.KeepAlive(mtx);
        }
    }
}
