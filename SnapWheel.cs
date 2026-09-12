using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnapWheel
{
    static class AppInfo
    {
        public const string Version = "0.1.1";
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
        Bitmap _dimmed;          // pre-dimmed base -> cheap per-frame paint
        Rectangle _vs;
        bool _dragging;
        Point _start;
        Rectangle _sel = Rectangle.Empty;
        bool _hasSel;

        // quick standard aspect-ratio presets, collapsed behind a toggle
        struct Chip { public string Label; public float Ratio; public Rectangle Rect; }
        Chip[] _chips;
        float _ratio = 0f;          // 0 = free
        Rectangle _toggleRect;
        bool _chipsOpen = false;
        float _chipsT = 0f;         // 0 collapsed .. 1 expanded
        Timer _anim;
        // moving an existing selection
        bool _moving;
        Point _moveStart;
        Rectangle _moveOrig;
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

            // bake the dimmed base once -> painting a frame becomes two blits (fast, high frame rate)
            _dimmed = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(_dimmed))
            {
                g.DrawImageUnscaled(shot, 0, 0);
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    g.FillRectangle(dim, 0, 0, shot.Width, shot.Height);
            }
            MeasureChips(); PlaceChips();
            _anim = new Timer();
            _anim.Interval = 15;
            _anim.Tick += new EventHandler(AnimTick);
            _anim.Start();
        }

        void AnimTick(object sender, EventArgs e)
        {
            float tgt = _chipsOpen ? 1f : 0f;
            if (Math.Abs(_chipsT - tgt) < 0.002f) { _chipsT = tgt; _anim.Stop(); Invalidate(); return; }
            _chipsT += (tgt - _chipsT) * 0.26f;      // silky ease
            Invalidate();
        }

        int[] _chipW;
        int _toggleW = 92;
        Rectangle _panelBounds;

        void MeasureChips()
        {
            string[] labels = { "自由", "1:1", "16:9", "9:16", "4:3", "3:4", "21:9" };
            _chipW = new int[labels.Length];
            using (Font f = new Font("Microsoft YaHei UI", 10f))
            using (Graphics g = CreateGraphics())
                for (int i = 0; i < labels.Length; i++)
                    _chipW[i] = (int)g.MeasureString(labels[i], f).Width + 22;
        }

        // panel position follows the selection and is always kept fully on screen
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
            if (_hasSel && _sel.Width > 4)
            {
                rowX = _sel.Left;
                rowY = _sel.Bottom + 12;
                if (rowY + h > _vs.Height - 10) rowY = _sel.Top - h - 12;   // flip above if no room below
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
            PlaceChips();                     // recompute so the panel always follows the selection
            if (_chips == null) return;
            using (Font f = new Font("Microsoft YaHei UI", 10f))
            {
                // expanding chips (slide right + fade with _chipsT)
                int shift = (int)((1f - _chipsT) * 26f);
                int al = (int)(255 * _chipsT);
                if (_chipsT > 0.01f)
                {
                    foreach (Chip c in _chips)
                    {
                        bool act = Math.Abs(c.Ratio - _ratio) < 0.001f;
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
                // toggle pill
                Rectangle tr = _toggleRect;
                using (GraphicsPath p = Gfx.Round(tr, 9f))
                using (SolidBrush b = new SolidBrush(_chipsOpen ? Color.FromArgb(225, 0, 122, 204) : Color.FromArgb(185, 22, 24, 28)))
                    g.FillPath(b, p);
                using (GraphicsPath p2 = Gfx.Round(tr, 9f))
                using (Pen pen = new Pen(Color.FromArgb(130, 255, 255, 255), 1.2f))
                    g.DrawPath(pen, p2);
                string tl = _chipsOpen ? "比例 ▼" : "比例 ▶";
                TextRenderer.DrawText(g, tl, f, tr, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.CompositingMode = CompositingMode.SourceCopy;
            if (_dimmed != null) g.DrawImageUnscaled(_dimmed, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            if (_hasSel && _sel.Width > 0 && _sel.Height > 0)
            {
                Rectangle r = _sel;
                if (_shot != null) g.DrawImage(_shot, r, r, GraphicsUnit.Pixel);
                using (Pen p = new Pen(Color.FromArgb(0, 174, 255), 2)) g.DrawRectangle(p, r);
                string txt = r.Width + " x " + r.Height;
                using (Font f = new Font("Segoe UI", 9f))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                using (SolidBrush fg = new SolidBrush(Color.White))
                {
                    SizeF sz = g.MeasureString(txt, f);
                    float tx = r.Left, ty = r.Top - sz.Height - 4;
                    if (ty < 0) ty = r.Bottom + 4;
                    g.FillRectangle(bg, tx, ty, sz.Width + 8, sz.Height + 2);
                    g.DrawString(txt, f, fg, tx + 4, ty + 1);
                }
                string hint = "双击选区保存　·　Esc 取消";
                using (Font f2 = new Font("Microsoft YaHei UI", 10f))
                using (SolidBrush fg2 = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                using (SolidBrush bg2 = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                {
                    SizeF sz2 = g.MeasureString(hint, f2);
                    // keep the hint ABOVE the selection so it never collides with the ratio panel below
                    float hx = r.Left;
                    float hy = r.Top - sz2.Height - 30;
                    if (hy < 4) hy = r.Bottom + 8;
                    g.FillRectangle(bg2, hx, hy, sz2.Width + 8, sz2.Height + 4);
                    g.DrawString(hint, f2, fg2, hx + 4, hy + 2);
                }
            }
            DrawChips(g);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // ratio toggle / chips
                if (_toggleRect.Contains(e.Location))
                {
                    _chipsOpen = !_chipsOpen;
                    _anim.Start();
                    Invalidate();
                    return;
                }
                if (_chipsOpen && _chipsT > 0.5f && _chips != null)
                {
                    int shift = (int)((1f - _chipsT) * 26f);
                    for (int i = 0; i < _chips.Length; i++)
                    {
                        Rectangle r = new Rectangle(_chips[i].Rect.X - shift, _chips[i].Rect.Y, _chips[i].Rect.Width, _chips[i].Rect.Height);
                        if (!r.Contains(e.Location)) continue;
                        _ratio = _chips[i].Ratio;
                        ApplyRatioToSel();
                        Invalidate();
                        return;
                    }
                }
                // drag an existing selection around
                if (_hasSel && _sel.Contains(e.Location))
                {
                    _moving = true; _moveStart = e.Location; _moveOrig = _sel;
                    return;
                }
                _dragging = true; _start = e.Location; _hasSel = false; _sel = Rectangle.Empty; Invalidate();
            }
            else if (e.Button == MouseButtons.Right) Cancel();
        }

        // resize the current selection to the locked ratio (keeps top-left + width)
        void ApplyRatioToSel()
        {
            if (_ratio <= 0f || !_hasSel || _sel.Width < 4) return;
            int w = _sel.Width;
            int h = (int)Math.Round(w / _ratio);
            if (h > _vs.Height) { h = _vs.Height; w = (int)Math.Round(h * _ratio); }
            if (_sel.Left + w > _vs.Width) w = _vs.Width - _sel.Left;
            if (_sel.Top + h > _vs.Height) h = _vs.Height - _sel.Top;
            _sel = new Rectangle(_sel.Left, _sel.Top, Math.Max(4, w), Math.Max(4, h));
        }

        // invalidate the changed selection AND both old/new panel positions (otherwise the panel ghosts)
        void InvalidateForSelection(Rectangle oldSel, Rectangle newSel)
        {
            Rectangle oldPanel = _panelBounds;
            Rectangle dirty = Rectangle.Union(oldSel, newSel);
            PlaceChips();
            dirty = Rectangle.Union(dirty, Rectangle.Union(oldPanel, _panelBounds));
            dirty.Inflate(60, 60);
            Invalidate(dirty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_moving)
            {
                int dx = e.X - _moveStart.X, dy = e.Y - _moveStart.Y;
                int nx = _moveOrig.X + dx, ny = _moveOrig.Y + dy;
                if (nx < 0) nx = 0;
                if (ny < 0) ny = 0;
                if (nx + _moveOrig.Width > _vs.Width) nx = _vs.Width - _moveOrig.Width;
                if (ny + _moveOrig.Height > _vs.Height) ny = _vs.Height - _moveOrig.Height;
                Rectangle old = _sel;
                _sel = new Rectangle(nx, ny, _moveOrig.Width, _moveOrig.Height);
                _hasSel = true;
                InvalidateForSelection(old, _sel);
                return;
            }
            if (_dragging)
            {
                Rectangle oldR = _sel;
                int x1 = Math.Min(_start.X, e.X), y1 = Math.Min(_start.Y, e.Y);
                int x2 = Math.Max(_start.X, e.X), y2 = Math.Max(_start.Y, e.Y);
                int w = x2 - x1, h = y2 - y1;
                if (_ratio > 0f && w > 2 && h > 2)
                {
                    if (w / (float)h > _ratio) h = (int)Math.Round(w / _ratio);
                    else w = (int)Math.Round(h * _ratio);
                    x2 = Math.Min(_vs.Width, x1 + w);
                    y2 = Math.Min(_vs.Height, y1 + h);
                }
                _sel = new Rectangle(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
                _hasSel = true;
                // repaint only the changed area (+ panel) -> smooth, high frame rate while dragging
                InvalidateForSelection(oldR, _sel);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _moving = false;
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                if (_sel.Width < 3 || _sel.Height < 3) { _hasSel = false; _sel = Rectangle.Empty; Invalidate(); }
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (_hasSel && _sel.Contains(e.Location)) Confirm();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Cancel();
            else if (e.KeyCode == Keys.Enter && _hasSel) Confirm();
        }

        void Confirm()
        {
            if (_shot == null) { Close(); return; }
            Rectangle r = _sel;
            r.Intersect(new Rectangle(0, 0, _shot.Width, _shot.Height));
            if (r.Width <= 0 || r.Height <= 0) { Close(); return; }
            Bitmap crop = new Bitmap(r.Width, r.Height);
            using (Graphics g = Graphics.FromImage(crop)) g.DrawImage(_shot, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
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
        Store _store;
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

        public WheelForm(Store store, Settings settings)
        {
            _store = store;
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
            int size = (int)(_R + _thumb * 1.75f + 40f);
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

        int HitTest(Point p)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < _store.Items.Count; i++)
            {
                float phi = ItemPhi(i);
                if (phi < _phiMin - 0.45f || phi > _phiMax + 0.45f) continue;
                if (EnterProgress(i) < 0.5f) continue;
                if (_store.Items[i] == _dragOutItem) continue;
                float sc0; if (!_scales.TryGetValue(i, out sc0)) sc0 = 1f;
                RectangleF rr = CardRect(i, Math.Max(0f, (sc0 - 1f) * Math.Min(CardSize(_store.Items[i]).Width, CardSize(_store.Items[i]).Height) / 2f));
                RectangleF hit = new RectangleF(rr.X - 3, rr.Y - 3, rr.Width + 6, rr.Height + 6);
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

            // camera (capture) button - for people who'd rather click than use a hotkey
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
                if (Math.Abs(e.X - _mouseDownPt.X) > 6 || Math.Abs(e.Y - _mouseDownPt.Y) > 6)
                {
                    _maybeDrag = false; _enlarged = -1; _holdIndex = -1; Render();
                    StartDragOut(_dragIndex);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _lastActive = DateTime.Now;
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
        NotifyIcon _tray;
        HotkeyForm _hotkey;
        WheelForm _wheel;

        public AppCtx()
        {
            _settings = Settings.Load();
            _store = new Store();
            _store.SaveToDisk = _settings.SaveToDisk;
            _store.Dir = _settings.Dir;
            _store.MaxCount = _settings.MaxCount;

            _hotkey = new HotkeyForm();
            _hotkey.Hotkey += new EventHandler(OnHotkey);

            _wheel = new WheelForm(_store, _settings);
            _wheel.SettingsRequested += new EventHandler(OnSettings);
            _wheel.CaptureRequested += new EventHandler(OnHotkey);

            _tray = new NotifyIcon();
            _tray.Icon = Brand.Get();
            _tray.Text = "SnapWheel 截图轮盘";
            _tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("截图", null, new EventHandler(OnHotkey));
            menu.Items.Add("显示/隐藏轮盘", null, new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); }));
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

        void OnSettings(object sender, EventArgs e)
        {
            bool wasTop = _wheel.TopMost;
            _wheel.TopMost = false;            // don't float above the settings dialog
            SettingsForm f = new SettingsForm(_settings);
            DialogResult r = f.ShowDialog();
            if (r == DialogResult.OK)
            {
                _store.SaveToDisk = _settings.SaveToDisk;
                _store.Dir = _settings.Dir;
                _store.MaxCount = _settings.MaxCount;
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
                StoreItem ni = _store.Add(ov.Result);
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
