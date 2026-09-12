using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnapWheel
{
    // 出错就记到 %APPDATA%\SnapWheel\error.log，不弹框、不退出。
    // 绘制里任何一处算错（比如颜色分量越界）最多让那一帧不好看，不该让整个程序挂掉。
    static class Err
    {
        static readonly object _lock = new object();
        static DateTime _last = DateTime.MinValue;

        public static string LogPath()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapWheel");
            try { Directory.CreateDirectory(d); } catch { }
            return Path.Combine(d, "error.log");
        }

        public static void Log(string where, Exception ex)
        {
            try
            {
                lock (_lock)
                {
                    string s = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]  " +
                               (ex == null ? "(null)" : ex.GetType().Name + ": " + ex.Message) + "\r\n" +
                               (ex == null ? "" : ex.StackTrace) + "\r\n\r\n";
                    File.AppendAllText(LogPath(), s, Encoding.UTF8);
                }
            }
            catch { }
            try
            {
                if (Notify != null && ShouldNotify())
                    Notify(where + "：" + (ex == null ? "未知错误" : ex.Message));
            }
            catch { }
        }

        public static Action<string> Notify;      // 由 AppCtx 挂上气泡提示

        // 同一个地方短期内只提示一次，避免刷屏
        public static bool ShouldNotify()
        {
            DateTime now = DateTime.Now;
            if ((now - _last).TotalSeconds < 30) return false;
            _last = now;
            return true;
        }
    }

    // 真·毛玻璃（只用在普通窗口上：设置/引导这些对话框）。
    // 轮盘那种 UpdateLayeredWindow 的窗口不能上 acrylic —— 它会给整个窗口矩形蒙一层灰，
    // 把屏幕角落糊成一块方块，所以轮盘用"画出来的"玻璃质感代替。
    static class Blur
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WINCOMPATTRDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [StructLayout(LayoutKind.Sequential)]
        struct ACCENTPOLICY { public int AccentState; public int AccentFlags; public int GradientColor; public int AnimationId; }

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

        const int ACCENT_DISABLED = 0;
        const int ACCENT_ENABLE_BLURBEHIND = 3;
        const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        const int WCA_ACCENT_POLICY = 19;

        public static bool Supported
        {
            get
            {
                try { return Environment.OSVersion.Version.Major >= 10 && TransparencyOn; }
                catch { return false; }
            }
        }

        // 系统里把"透明效果"关掉时 acrylic 不生效 —— 那种情况下再画半透明底会变成
        // "玻璃没了、却透着一层桌面"，字看不清，所以直接退回不透明底
        public static bool TransparencyOn
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        if (k == null) return true;
                        object v = k.GetValue("EnableTransparency");
                        if (v == null) return true;
                        return Convert.ToInt32(v) != 0;
                    }
                }
                catch { return true; }
            }
        }

        // tint = ABGR 颜色（GradientColor 是 0xAABBGGRR）
        public static bool Apply(IntPtr hwnd, int a, int r, int g, int b, bool acrylic)
        {
            try
            {
                ACCENTPOLICY ap = new ACCENTPOLICY();
                ap.AccentState = acrylic ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_BLURBEHIND;
                ap.AccentFlags = 2;                     // 四边都画
                ap.GradientColor = (a << 24) | (b << 16) | (g << 8) | r;
                WINCOMPATTRDATA d = new WINCOMPATTRDATA();
                d.Attribute = WCA_ACCENT_POLICY;
                d.Data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ACCENTPOLICY)));
                Marshal.StructureToPtr(ap, d.Data, false);
                d.SizeOfData = Marshal.SizeOf(typeof(ACCENTPOLICY));
                int hr = SetWindowCompositionAttribute(hwnd, ref d);
                Marshal.FreeHGlobal(d.Data);
                return hr != 0;
            }
            catch { return false; }
        }

        public static void Clear(IntPtr hwnd)
        {
            try
            {
                ACCENTPOLICY ap = new ACCENTPOLICY();
                ap.AccentState = ACCENT_DISABLED;
                WINCOMPATTRDATA d = new WINCOMPATTRDATA();
                d.Attribute = WCA_ACCENT_POLICY;
                d.Data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ACCENTPOLICY)));
                Marshal.StructureToPtr(ap, d.Data, false);
                d.SizeOfData = Marshal.SizeOf(typeof(ACCENTPOLICY));
                SetWindowCompositionAttribute(hwnd, ref d);
                Marshal.FreeHGlobal(d.Data);
            }
            catch { }
        }
    }

    static class AppInfo
    {
#if NO_KEY
        public const string Version = "0.2.17";   // 变体：多 Wheel + 框选缩放/锁定（无万能键）
#else
        public const string Version = "0.4.7";   // 完整版：含万能键摇杆 + 旋转 + 缩放修正
#endif
        public const string Author = "exper7";
        public const string Name = "SnapWheel";
        public const string CnName = "快照轮环";        // 正式中文名（0.4.7 起）
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int GetDpiForSystem();
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        // 优先"每显示器 DPI 感知 v2"：多屏不同缩放时不会把窗口拉伸糊掉，坐标也按物理像素走
        public static void SetDpiAwarenessBest()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }   // PER_MONITOR_AWARE_V2
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }                  // PER_MONITOR_DPI_AWARE
            try { SetProcessDPIAware(); } catch { }                                        // 退回到系统 DPI 感知
        }

        // 当前进程的缩放比例（1.0 = 96dpi）
        public static float DpiScaleOf(IntPtr hwnd)
        {
            try
            {
                uint d = 0;
                if (hwnd != IntPtr.Zero) d = GetDpiForWindow(hwnd);
                if (d == 0) d = (uint)GetDpiForSystem();
                if (d == 0) d = 96;
                return d / 96f;
            }
            catch { return 1f; }
        }
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
        // 弹框显示后强制整窗重绘一次：自定义绘制的按钮第一帧容易取到还没定下来的父底色，
        // 四角会闪一下白块（鼠标划过去才恢复）。这里补一次完整重绘，用户看不到那一帧。
        public static void RepaintAll(Form f)
        {
            try
            {
                f.Invalidate(true);
                f.Update();
                foreach (Control c in f.Controls) c.Invalidate();
                f.Update();
            }
            catch { }
        }
        // f > 0 往白里混，f < 0 往黑里混（用来做拟物高光/暗部）
        public static Color Shade(Color c, float f)
        {
            if (f >= 0f)
            {
                return Color.FromArgb(c.A,
                    (int)Math.Round(c.R + (255 - c.R) * f),
                    (int)Math.Round(c.G + (255 - c.G) * f),
                    (int)Math.Round(c.B + (255 - c.B) * f));
            }
            float t = 1f + f;      // f=-0.4 -> 0.6
            return Color.FromArgb(c.A, (int)Math.Round(c.R * t), (int)Math.Round(c.G * t), (int)Math.Round(c.B * t));
        }

        public static Color A(Color c, int a)
        {
            if (a < 0) a = 0; if (a > 255) a = 255;
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        public static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        // 缓动：先快后慢，收尾很稳
        public static float EaseOut(float t)
        {
            t = Clamp01(t);
            return 1f - (1f - t) * (1f - t) * (1f - t);
        }

        // 缓动：两头慢中间快（滑入用这个最顺）
        public static float EaseInOut(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        // 回弹一点点的收尾，显得有"弹性"
        public static float EaseBack(float t)
        {
            t = Clamp01(t);
            float s = 1.70158f;
            float u = t - 1f;
            return u * u * ((s + 1f) * u + s) + 1f;
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 0.5f) { p.AddRectangle(r); return p; }        // 圆角为 0 时别画成椭圆
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---- 新拟态 / 毛玻璃 基元 ----

        // 玻璃面板：竖向微渐变底 + 顶部内侧高光 + 底部内侧暗边（新拟态的关键就是这一上一下）
        public static void GlassPanel(Graphics g, GraphicsPath path, RectangleF r, Color fill,
                                      int topHi, int bottomShade, bool verticalGradient)
        {
            if (verticalGradient && r.Height > 2f)
            {
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new RectangleF(r.X, r.Y - 1f, r.Width, r.Height + 2f),
                    Shade(fill, 0.10f), Shade(fill, -0.10f), LinearGradientMode.Vertical))
                    g.FillPath(lg, path);
            }
            else using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);

            if (topHi > 0)
            {
                // 上半圈高光（浅色描边，像光从左上打过来）
                using (Pen hi = new Pen(Color.FromArgb(topHi, 255, 255, 255), 1.2f))
                {
                    hi.StartCap = LineCap.Round; hi.EndCap = LineCap.Round;
                    g.DrawPath(hi, path);
                }
            }
            if (bottomShade > 0)
            {
                using (Pen sh = new Pen(Color.FromArgb(bottomShade, 0, 0, 0), 1.4f))
                {
                    sh.StartCap = LineCap.Round; sh.EndCap = LineCap.Round;
                    g.DrawPath(sh, path);
                }
            }
        }

        // 新拟态圆钮：外凸（亮边在左上，暗边在右下）或内凹（反过来的 hover / 按下态）
        public static void NeuCircle(Graphics g, RectangleF r, Color surface, Color accent,
                                     bool primary, bool pressed, int hiA, int shA)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(r);
                Color baseC = primary ? accent : surface;
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new RectangleF(r.X, r.Y - 1f, r.Width, r.Height + 2f),
                    primary ? Shade(baseC, 0.22f) : Shade(baseC, 0.14f),
                    primary ? Shade(baseC, -0.20f) : Shade(baseC, -0.16f),
                    LinearGradientMode.Vertical))
                    g.FillPath(lg, p);

                if (!pressed)
                {
                    // 左上高光
                    using (Pen hi = new Pen(Color.FromArgb(hiA, 255, 255, 255), 1.4f))
                        g.DrawArc(hi, r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f, 175f, 130f);
                }
                // 右下暗边（内凹感）
                using (Pen sh = new Pen(Color.FromArgb(shA, 0, 0, 0), 1.6f))
                    g.DrawArc(sh, r.X + 0.6f, r.Y + 0.6f, r.Width - 1.2f, r.Height - 1.2f, -5f, 130f);
            }
        }

        // 柔和外阴影（新拟态的"浮起来"感）：画几层递减的圆角轮廓
        public static void SoftShadow(Graphics g, GraphicsPath path, int strength, float spread)
        {
            if (strength <= 2) return;
            for (int i = 3; i >= 1; i--)
            {
                int a = (int)(strength * (0.10f + 0.06f * (3 - i)) / 3f);
                if (a <= 0) continue;
                using (Pen p = new Pen(Color.FromArgb(a, 0, 0, 0), i * spread))
                { p.LineJoin = LineJoin.Round; g.DrawPath(p, path); }
            }
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

    // 读图“尽量全能”：
    //   1) .ico / .cur  -> Icon 类（连 256 的 PNG 压缩图标都认，保留透明）
    //   2) 其余先交给 GDI+（png/jpg/bmp/gif/tif/exif/wmf/emf…）
    //   3) 还不行就交给系统 WIC（WPF 的 BitmapDecoder）—— WebP / HEIC / AVIF / JXR / 相机 RAW
    //      只要机器上装了对应解码器（Win10/11 多数自带的 WebP/HEIC 扩展）就能读
    // 任何一步失败都安静返回 null，绝不抛异常
    static class ImageIO
    {
        public const int MaxDim = 4096;      // 存进轮盘的图最大边；再大的等比缩下来，防止内存/磁盘爆

        public static readonly string[] Exts = {
            ".png", ".apng", ".jpg", ".jpeg", ".jpe", ".jfif", ".jiff",
            ".bmp", ".dib", ".rle", ".gif", ".tif", ".tiff",
            ".ico", ".cur", ".exif", ".emf", ".wmf",
            ".webp", ".heic", ".heif", ".avif", ".jxl", ".jxr", ".wdp", ".hdp", ".dds",
            ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".pef", ".srw", ".raw"
        };

        // 打开文件对话框用的过滤器（"图片文件|*.png;*.jpg;…|所有文件|*.*"）
        public static string DialogFilter()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Exts.Length; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append('*').Append(Exts[i]);
            }
            return "图片文件|" + sb.ToString() + "|所有文件|*.*";
        }

        public static bool IsImageExt(string path)        {
            string e = "";
            try { e = Path.GetExtension(path); } catch { }
            if (string.IsNullOrEmpty(e)) return false;
            e = e.ToLowerInvariant();
            for (int i = 0; i < Exts.Length; i++) if (Exts[i] == e) return true;
            return false;
        }

        // 展开文件夹（只取一层）并过滤出图片；cap 限制总数，避免一次拖进来一大堆把内存吃满
        public static List<string> Collect(string[] paths, int cap)
        {
            List<string> ok = new List<string>();
            if (paths == null) return ok;
            for (int i = 0; i < paths.Length && ok.Count < cap; i++)
            {
                string p = paths[i];
                try
                {
                    if (Directory.Exists(p))
                    {
                        string[] fs = Directory.GetFiles(p);
                        Array.Sort(fs);
                        for (int k = 0; k < fs.Length && ok.Count < cap; k++)
                            if (IsImageExt(fs[k])) ok.Add(fs[k]);
                    }
                    else if (File.Exists(p) && IsImageExt(p)) ok.Add(p);
                }
                catch { }
            }
            return ok;
        }

        public static Bitmap Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string ext = "";
            try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch { }
            Bitmap b = null;
            if (ext == ".ico" || ext == ".cur") b = LoadIcon(path);
            if (b == null) b = LoadGdi(path);
            if (b == null) b = LoadWic(path);
            if (b == null) return null;
            return Fit(b, MaxDim);
        }

        // GDI+：先整份读进内存再解，避免 Image.FromFile 一直占着原文件
        static Bitmap LoadGdi(string path)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(raw))
                using (Image img = Image.FromStream(ms))
                    return Clone(img);
            }
            catch { return null; }
        }

        static Bitmap LoadIcon(string path)
        {
            int[] want = { 256, 128, 64, 48, 32, 16 };
            for (int i = 0; i < want.Length; i++)
            {
                try
                {
                    using (Icon ic = new Icon(path, want[i], want[i]))
                    {
                        Bitmap b = ic.ToBitmap();
                        if (b.Width > 1 && b.Height > 1) return b;
                        b.Dispose();
                    }
                }
                catch { }
            }
            try { using (Icon ic = new Icon(path)) return ic.ToBitmap(); } catch { }
            return null;
        }

        // PresentationCore 不一定在 GAC 里（本机就只在框架目录的 WPF 子目录下），
        // 所以先 Assembly.Load，失败再 LoadFrom 那个固定位置。只试一次，结果缓存。
        static Assembly _wicAsm;
        static bool _wicTried;

        static Assembly WicAsm()
        {
            if (_wicTried) return _wicAsm;
            _wicTried = true;
            try { _wicAsm = Assembly.Load("PresentationCore"); }
            catch { _wicAsm = null; }
            if (_wicAsm == null)
            {
                try
                {
                    string d = RuntimeEnvironment.GetRuntimeDirectory();
                    string p = Path.Combine(Path.Combine(d, "WPF"), "PresentationCore.dll");
                    if (File.Exists(p)) _wicAsm = Assembly.LoadFrom(p);
                }
                catch { _wicAsm = null; }
            }
            return _wicAsm;
        }

        // 系统 WIC（反射拿 PresentationCore，编不进来也不影响本体）
        static Bitmap LoadWic(string path)
        {
            try
            {
                Assembly pc = WicAsm();
                if (pc == null) return null;
                Type tDec = pc.GetType("System.Windows.Media.Imaging.BitmapDecoder", false);
                Type tOpt = pc.GetType("System.Windows.Media.Imaging.BitmapCreateOptions", false);
                Type tCch = pc.GetType("System.Windows.Media.Imaging.BitmapCacheOption", false);
                Type tEnc = pc.GetType("System.Windows.Media.Imaging.PngBitmapEncoder", false);
                if (tDec == null || tOpt == null || tCch == null || tEnc == null) return null;

                MethodInfo create = tDec.GetMethod("Create", new Type[] { typeof(Uri), tOpt, tCch });
                if (create == null) return null;
                object dec = create.Invoke(null, new object[] {
                    new Uri(path), Enum.ToObject(tOpt, 0), Enum.ToObject(tCch, 1) });
                if (dec == null) return null;

                object frames = tDec.GetProperty("Frames").GetValue(dec, null);
                IList fl = frames as IList;
                if (fl == null || fl.Count == 0) return null;
                object frame = fl[0];

                object enc = Activator.CreateInstance(tEnc);
                object encFrames = tEnc.GetProperty("Frames").GetValue(enc, null);
                IList ef = encFrames as IList;
                if (ef == null) return null;
                ef.Add(frame);

                MethodInfo save = tEnc.GetMethod("Save", new Type[] { typeof(Stream) });
                if (save == null) return null;
                using (MemoryStream ms = new MemoryStream())
                {
                    save.Invoke(enc, new object[] { ms });
                    ms.Position = 0;
                    using (Image img = Image.FromStream(ms))
                        return Clone(img);
                }
            }
            catch { return null; }
        }

        // 复制成不依赖流/文件的独立位图（保留 alpha）
        static Bitmap Clone(Image img)
        {
            Bitmap c = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(c))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, new Rectangle(0, 0, c.Width, c.Height));
            }
            return c;
        }

        // 太大就等比缩到 MaxDim（顺手把原图释放掉）
        public static Bitmap Fit(Bitmap b, int max)
        {
            if (b == null) return null;
            if (b.Width <= max && b.Height <= max) return b;
            float s = Math.Min((float)max / b.Width, (float)max / b.Height);
            int w = Math.Max(1, (int)Math.Round(b.Width * s));
            int h = Math.Max(1, (int)Math.Round(b.Height * s));
            Bitmap c = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(c))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(b, new Rectangle(0, 0, w, h));
            }
            try { b.Dispose(); } catch { }
            return c;
        }

        public static bool HasAlpha(Bitmap b)
        {
            try { return b != null && Image.IsAlphaPixelFormat(b.PixelFormat); }
            catch { return false; }
        }

        // 真正去看像素里有没有“半透明/透明”。注意 IsAlphaPixelFormat 对任何 32bpp 图都返回 true，
        // 光看格式会把所有照片都当带透明的，于是全存成 PNG（又大又没必要）。
        public static bool HasRealAlpha(Bitmap b)
        {
            if (!HasAlpha(b)) return false;
            try
            {
                BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height),
                                          ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = Math.Abs(d.Stride);
                    byte[] row = new byte[stride];
                    long baseAddr = d.Scan0.ToInt64();
                    for (int y = 0; y < b.Height; y++)
                    {
                        Marshal.Copy(new IntPtr(baseAddr + (long)y * d.Stride), row, 0, stride);
                        for (int x = 3; x < row.Length; x += 4)
                            if (row[x] != 255) return true;
                    }
                }
                finally { b.UnlockBits(d); }
            }
            catch { return true; }      // 读不出来就按“有透明”处理，宁可存 PNG 也别丢通道
            return false;
        }

        // 有真透明 -> PNG（无损）；不透明 -> JPEG（省磁盘，照片也不失真）
        public static string ExtFor(Bitmap b) { return HasRealAlpha(b) ? ".png" : ".jpg"; }

        public static void SaveAs(Bitmap b, string path)
        {
            string low = path.ToLowerInvariant();
            if (!low.EndsWith(".jpg") && !low.EndsWith(".jpeg"))
            {
                b.Save(path, ImageFormat.Png);
                return;
            }
            ImageCodecInfo jpg = null;
            try
            {
                ImageCodecInfo[] cs = ImageCodecInfo.GetImageEncoders();
                for (int i = 0; i < cs.Length; i++)
                    if (cs[i].FormatID == ImageFormat.Jpeg.Guid) { jpg = cs[i]; break; }
            }
            catch { }
            if (jpg == null) { b.Save(path, ImageFormat.Png); return; }
            using (EncoderParameters ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                b.Save(path, jpg, ep);
            }
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
        int _seq = 0;

        public StoreItem Add(Bitmap bmp) { return AddCore(bmp, ".png"); }

        public StoreItem AddCore(Bitmap bmp, string ext)
        {
            StoreItem it = new StoreItem();
            // 存自己的副本：调用方（测试 / 截图流程 / 剪贴板）之后释放原图都不该影响轮盘，
            // 否则会拿着一个"已释放的 Image"去读宽高 -> ArgumentException
            try { it.Image = new Bitmap(bmp); } catch { it.Image = bmp; }
            if (SaveToDisk && Dir.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    string f = Path.Combine(Dir, "snap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + (_seq++) + ext);
                    ImageIO.SaveAs(bmp, f);
                    it.FilePath = f;
                }
                catch { }
            }
            Items.Add(it);
            while (Items.Count > MaxCount && Items.Count > 0) Items.RemoveAt(0);
            return it;
        }

        // 从外部文件导入：解码 -> 落盘 -> 入列。失败返回 null（不抛）
        public StoreItem Import(string path)
        {
            Bitmap b = ImageIO.Load(path);
            if (b == null) return null;
            try { return AddCore(b, ImageIO.ExtFor(b)); }
            catch { try { b.Dispose(); } catch { } return null; }
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

        // 把 src 的每个设置字段拷到 dst（用于"还原默认设置"）
        public static void CopyInto(Settings src, Settings dst)
        {
            FieldInfo[] fs = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fs.Length; i++)
            {
                try { fs[i].SetValue(dst, fs[i].GetValue(src)); } catch { }
            }
        }

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
                List<string> files = new List<string>();
                try
                {
                    files.AddRange(Directory.GetFiles(st.Dir, "snap_*.png"));
                    files.AddRange(Directory.GetFiles(st.Dir, "snap_*.jpg"));
                }
                catch { }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                for (int k = 0; k < files.Count; k++)
                {
                    try
                    {
                        Bitmap b = ImageIO.Load(files[k]);     // 走统一加载器，ico/jpeg/…都能回读
                        if (b == null) continue;
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
        public int PeekPercent = 240;         // 长按放大：百分比（100 = 原大小）
        public bool IntroSeen = false;        // 是否看过新手引导
        public bool IntroAnim = true;         // 启动时播开启动画
        // ---- 外观风格（新拟态 + 扁平化 + 毛玻璃）----
        public string UiStyle = "neu";        // neu=新拟态+毛玻璃(默认) / flat=纯扁平 / solid=高对比不透明
        public int GlassPercent = 40;         // 玻璃面板不透明度 20..100
        public int CardRadius = 14;           // 卡片圆角（占最小边的百分比）0..30
        public int ShadowPercent = 55;        // 阴影强度 0..100
        public int AnimSpeed = 100;           // 动画速度 %（70 慢 / 100 标准 / 140 快）
        public int AccentIndex = -1;          // -1=跟随每个 Wheel 自己的颜色；0..7=全局统一主题色
        public bool ShowNameLabel = true;     // 显示 Wheel 名称药丸
        public bool ShowCountLabel = true;    // 显示图片计数药丸
        public int UiScale = 0;               // 界面缩放 %：0=自动（按显示器 DPI），60..250
        public bool CollapseMode = true;      // 收起态：像贴边小球一样缩到屏幕边上，留个可点的小把手
        public bool ClipboardImport = true;   // 剪贴板里出现图片时自动收进轮盘
        public bool GlassRefresh = true;      // 定时重抓玻璃底，避免轮盘挂久了糊的是旧桌面
        public bool ShowBalloon = true;       // 托盘气泡提示（关掉就不再弹右下角通知）
        public int ExpandSpeed = 100;         // 展开动画速度 %（越大越快；独立于整体动画速度）
        public int CollapseSpeed = 150;       // 收起动画速度 %（默认"快"一档，收起要干脆）
        public bool NubSingle = false;        // 只用一个把手：左边那个点一下展开、再点一下收起（底部不占地方）

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
                        else if (k == "PeekPercent") { int n; if (int.TryParse(v, out n) && n >= 120 && n <= 500) s.PeekPercent = n; }
                        else if (k == "IntroSeen") s.IntroSeen = (v == "1");
                        else if (k == "IntroAnim") s.IntroAnim = (v == "1");
                        else if (k == "UiStyle" && (v == "neu" || v == "flat" || v == "solid")) s.UiStyle = v;
                        else if (k == "GlassPercent") { int n; if (int.TryParse(v, out n) && n >= 20 && n <= 100) s.GlassPercent = n; }
                        else if (k == "CardRadius") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 30) s.CardRadius = n; }
                        else if (k == "ShadowPercent") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 100) s.ShadowPercent = n; }
                        else if (k == "AnimSpeed") { int n; if (int.TryParse(v, out n) && n >= 50 && n <= 200) s.AnimSpeed = n; }
                        else if (k == "AccentIndex") { int n; if (int.TryParse(v, out n) && n >= -1 && n <= 7) s.AccentIndex = n; }
                        else if (k == "ShowNameLabel") s.ShowNameLabel = (v == "1");
                        else if (k == "ShowCountLabel") s.ShowCountLabel = (v == "1");
                        else if (k == "UiScale") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 250) s.UiScale = n; }
                        else if (k == "CollapseMode") s.CollapseMode = (v == "1");
                        else if (k == "ClipboardImport") s.ClipboardImport = (v == "1");
                        else if (k == "GlassRefresh") s.GlassRefresh = (v == "1");
                        else if (k == "ShowBalloon") s.ShowBalloon = (v == "1");
                        else if (k == "ExpandSpeed") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 250) s.ExpandSpeed = n; }
                        else if (k == "CollapseSpeed") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 250) s.CollapseSpeed = n; }
                        else if (k == "RingSpeed") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 250) { s.ExpandSpeed = n; s.CollapseSpeed = n; } }   // 兼容旧配置
                        else if (k == "NubSingle") s.NubSingle = (v == "1");
                    }
                }
            }
            catch { }
            return s;
        }

        // 把 src 的每个设置字段拷到 dst（用于"还原默认设置"）
        public static void CopyInto(Settings src, Settings dst)
        {
            FieldInfo[] fs = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fs.Length; i++)
            {
                try { fs[i].SetValue(dst, fs[i].GetValue(src)); } catch { }
            }
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
                lines.Add("PeekPercent=" + PeekPercent);
                lines.Add("IntroSeen=" + (IntroSeen ? "1" : "0"));
                lines.Add("IntroAnim=" + (IntroAnim ? "1" : "0"));
                lines.Add("UiStyle=" + UiStyle);
                lines.Add("GlassPercent=" + GlassPercent);
                lines.Add("CardRadius=" + CardRadius);
                lines.Add("ShadowPercent=" + ShadowPercent);
                lines.Add("AnimSpeed=" + AnimSpeed);
                lines.Add("AccentIndex=" + AccentIndex);
                lines.Add("ShowNameLabel=" + (ShowNameLabel ? "1" : "0"));
                lines.Add("ShowCountLabel=" + (ShowCountLabel ? "1" : "0"));
                lines.Add("UiScale=" + UiScale);
                lines.Add("CollapseMode=" + (CollapseMode ? "1" : "0"));
                lines.Add("ClipboardImport=" + (ClipboardImport ? "1" : "0"));
                lines.Add("GlassRefresh=" + (GlassRefresh ? "1" : "0"));
                lines.Add("ShowBalloon=" + (ShowBalloon ? "1" : "0"));
                lines.Add("ExpandSpeed=" + ExpandSpeed);
                lines.Add("CollapseSpeed=" + CollapseSpeed);
                lines.Add("NubSingle=" + (NubSingle ? "1" : "0"));
                File.WriteAllLines(FilePath(), lines.ToArray());
            }
            catch { }
        }
    }

    // 扁平/新拟态风格的按钮：浅底 + 上亮下暗的柔和立体，主按钮用主题色填充
    class RoundButton : Button
    {
        public Color Fill = Color.FromArgb(0, 122, 204);
        public Color FillHover = Color.FromArgb(0, 138, 228);
        public Color TextColor = Color.White;
        public bool Primary = false;
        public bool Ghost = false;

        public RoundButton()
        {
            // 圆角外的部分交给父容器去画：SupportsTransparentBackColor + OnPaintBackground。
            // 角落永远是"父容器真实的背景"，不用自己猜颜色 ——
            // 之前自己填色，半透明窗体上取不到色就退回白/黑，四角才会出现"鼠标移上去才好"的脏块。
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            // 先让父容器把背景铺到我们这块区域（含窗体底色）
            try { base.OnPaintBackground(pevent); } catch { }

            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0, 0, Width, Height);

            bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
            bool down = MouseButtons == MouseButtons.Left && hot;
            Color c = hot ? FillHover : Fill;
            if (Ghost) c = Color.FromArgb(hot ? 240 : 200, c.R, c.G, c.B);

            using (GraphicsPath p = Gfx.Round(r, 10f))
            {
                if (down)
                {
                    // 按下：内凹（暗边在上，亮边在下）
                    using (SolidBrush b = new SolidBrush(Gfx.Shade(c, -0.10f))) g.FillPath(b, p);
                    using (Pen sh = new Pen(Color.FromArgb(70, 0, 0, 0), 1.6f)) g.DrawPath(sh, p);
                }
                else
                {
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                        new RectangleF(r.X, r.Y - 1f, r.Width, r.Height + 2f),
                        Primary ? Gfx.Shade(c, 0.16f) : Gfx.Shade(c, 0.55f),
                        Primary ? Gfx.Shade(c, -0.12f) : Gfx.Shade(c, -0.04f),
                        LinearGradientMode.Vertical))
                        g.FillPath(lg, p);
                    using (Pen hi = new Pen(Color.FromArgb(Primary ? 60 : 200, 255, 255, 255), 1.1f)) g.DrawPath(hi, p);
                    using (Pen sh = new Pen(Color.FromArgb(28, 0, 0, 0), 1f)) g.DrawPath(sh, p);
                }
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height - 1), TextColor,
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
        float _k = 1f;             // 截图浮层的缩放（高 DPI 屏上按钮/手柄/字号都要放大）

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
        int _toggleW = 92;   // 会被 PlaceChips 按 _k 覆盖
        Rectangle _panelBounds;

        public Bitmap Result;

        public OverlayForm(Rectangle virtualScreen, Bitmap shot)
        {
            _vs = virtualScreen;
            _shot = shot;
            try { _k = Math.Max(0.75f, Math.Min(3f, Native.DpiScaleOf(IntPtr.Zero))); } catch { _k = 1f; }
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
            int px = _vs.Width - (int)(420 * _k);
            Panel panel = new Panel();
            panel.Bounds = new Rectangle(px, (int)(18 * _k), (int)(400 * _k), (int)(40 * _k));
            panel.BackColor = Color.FromArgb(210, 18, 20, 24);
            Controls.Add(panel);

            Label l1 = new Label(); l1.Text = "宽"; l1.ForeColor = Color.White;
            l1.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            l1.Bounds = new Rectangle((int)(10 * _k), (int)(10 * _k), (int)(20 * _k), (int)(22 * _k)); panel.Controls.Add(l1);
            _inW = new TextBox(); _inW.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            _inW.Bounds = new Rectangle((int)(32 * _k), (int)(8 * _k), (int)(66 * _k), (int)(24 * _k));
            _inW.BackColor = Color.FromArgb(38, 40, 46); _inW.ForeColor = Color.White;
            _inW.BorderStyle = BorderStyle.FixedSingle; _inW.TextAlign = HorizontalAlignment.Center;
            panel.Controls.Add(_inW);

            Label l2 = new Label(); l2.Text = "高"; l2.ForeColor = Color.White;
            l2.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            l2.Bounds = new Rectangle((int)(108 * _k), (int)(10 * _k), (int)(20 * _k), (int)(22 * _k)); panel.Controls.Add(l2);
            _inH = new TextBox(); _inH.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            _inH.Bounds = new Rectangle((int)(130 * _k), (int)(8 * _k), (int)(66 * _k), (int)(24 * _k));
            _inH.BackColor = Color.FromArgb(38, 40, 46); _inH.ForeColor = Color.White;
            _inH.BorderStyle = BorderStyle.FixedSingle; _inH.TextAlign = HorizontalAlignment.Center;
            panel.Controls.Add(_inH);

            RoundButton apply = new RoundButton();
            apply.Text = "应用"; apply.Size = new Size((int)(58 * _k), (int)(26 * _k)); apply.Location = new Point((int)(204 * _k), (int)(7 * _k));
            apply.Fill = Color.FromArgb(0, 122, 204); apply.FillHover = Color.FromArgb(0, 140, 232);
            apply.Font = new Font("Microsoft YaHei UI", 9f * _k, FontStyle.Bold);
            apply.Click += new EventHandler(delegate(object o, EventArgs e2) { ApplySizeFromBoxes(); });
            panel.Controls.Add(apply);

            RoundButton reset = new RoundButton();
            reset.Text = "角度归零"; reset.Size = new Size((int)(84 * _k), (int)(26 * _k)); reset.Location = new Point((int)(268 * _k), (int)(7 * _k));
            reset.Fill = Color.FromArgb(70, 74, 84); reset.FillHover = Color.FromArgb(92, 98, 110);
            reset.Font = new Font("Microsoft YaHei UI", 9f * _k);
            reset.Click += new EventHandler(delegate(object o, EventArgs e2) { _ang = 0f; Invalidate(); SyncInfo(); });
            panel.Controls.Add(reset);

            _lblAngle = new Label();
            _lblAngle.ForeColor = Color.FromArgb(170, 176, 186);
            _lblAngle.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            _lblAngle.Bounds = new Rectangle((int)(10 * _k), (int)(34 * _k), (int)(380 * _k), (int)(20 * _k));
            panel.Controls.Add(_lblAngle);
            panel.Height = (int)(58 * _k);

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
            return new PointF(_c.X - v.X * (_sz.Height / 2f + 30f * _k), _c.Y - v.Y * (_sz.Height / 2f + 30f * _k));
        }
        PointF LockHandlePos()
        {
            PointF v = AxisV();
            return new PointF(_c.X - v.X * (_sz.Height / 2f + 62f * _k), _c.Y - v.Y * (_sz.Height / 2f + 62f * _k));
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
            using (Font f = new Font("Microsoft YaHei UI", 10f * _k))
            using (Graphics g = CreateGraphics())
                for (int i = 0; i < labels.Length; i++)
                    _chipW[i] = (int)g.MeasureString(labels[i], f).Width + (int)(22 * _k);
        }

        void PlaceChips()
        {
            string[] labels = { "自由", "1:1", "16:9", "9:16", "4:3", "3:4", "21:9" };
            float[] ratios = { 0f, 1f, 16f / 9f, 9f / 16f, 4f / 3f, 3f / 4f, 21f / 9f };
            if (_chipW == null) MeasureChips();
            _toggleW = (int)Math.Round(92 * _k);
            int h = (int)Math.Round(32 * _k), gap = (int)Math.Round(8 * _k);
            int chipsW = 0;
            for (int i = 0; i < _chipW.Length; i++) chipsW += _chipW[i] + gap;
            chipsW -= gap;
            int totalW = _toggleW + gap + chipsW;

            int rowX, rowY;
            if (_hasSel)
            {
                RectangleF bb = SelBounds();
                rowX = (int)bb.Left;
                rowY = (int)bb.Bottom + (int)(14 * _k);
                if (rowY + h > _vs.Height - 10) rowY = (int)bb.Top - h - (int)(40 * _k);
            }
            else
            {
                rowX = (_vs.Width - totalW) / 2;
                rowY = _vs.Height - h - (int)(44 * _k);
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
            using (Font f = new Font("Microsoft YaHei UI", 10f * _k))
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
            try { PaintOverlay(e); }
            catch (Exception ex) { Err.Log("OverlayForm.OnPaint", ex); }
        }

        void PaintOverlay(PaintEventArgs e)
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
                using (Font f = new Font("Segoe UI", 9.5f * _k))
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
                using (Font f2 = new Font("Microsoft YaHei UI", 10f * _k))
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
                if (Dist(e.Location, lh) < 15f * _k)
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
                if (Dist(e.Location, rh) < 15f * _k)
                {
                    _rotating = true;
                    _rotGrab = (float)Math.Atan2(e.Y - _c.Y, e.X - _c.X) - _ang;
                    return;
                }
                // 四角：取“最近的那个角”（小选区四角会重叠，取第一个会老是抓到左上角）
                PointF[] cs = Corners();
                int bestC = -1; float bestD = 11f * _k;
                for (int i = 0; i < 4; i++)
                {
                    float d = Dist(e.Location, cs[i]);
                    if (d < bestD) { bestD = d; bestC = i; }
                }
                if (bestC >= 0) { _resizeCorner = bestC; return; }
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
            PointF fx = cs[(_resizeCorner + 2) % 4];        // 对角：固定不动
            // 被拖的角相对于固定角，在局部坐标系里的方向（左/上两个角是负的）。
            // 之前漏了这一步：从“左上/右上/左下”任何一个角拖，算出来的 du/dv 是负的，
            // 一被夹到 6 就整块塌掉再按错误中心乱跳 —— 这就是“飞走”的根因。
            float su = (_resizeCorner == 1 || _resizeCorner == 2) ? 1f : -1f;
            float sv = (_resizeCorner == 2 || _resizeCorner == 3) ? 1f : -1f;

            // 鼠标点先夹进屏幕（到边即停）
            float mx = mouse.X, my = mouse.Y;
            if (mx < 0f) mx = 0f; if (mx > _vs.Width) mx = _vs.Width;
            if (my < 0f) my = 0f; if (my > _vs.Height) my = _vs.Height;

            float dx = mx - fx.X, dy = my - fx.Y;
            float du = (dx * u.X + dy * u.Y) * su;          // 乘符号 -> 四个角拖出来都是正尺寸
            float dv = (dx * v.X + dy * v.Y) * sv;
            if (du < 6f) du = 6f;
            if (dv < 6f) dv = 6f;
            float r = EffRatio();
            if (r > 0f) { if (du / dv > r) dv = du / r; else du = dv * r; }

            // 固定角在屏内时：再求一个“以固定角为锚点整体缩放”的最大系数 t，
            // 保证（旋转后的）外接矩形永远不出屏 —— 旋转状态下光夹鼠标点是挡不住的
            bool fxInside = fx.X >= -0.5f && fx.X <= _vs.Width + 0.5f && fx.Y >= -0.5f && fx.Y <= _vs.Height + 0.5f;
            if (fxInside)
            {
                float[] ea = { 0f, su, su, 0f };
                float[] eb = { 0f, 0f, sv, sv };
                float t = 1f;
                for (int k = 0; k < 4; k++)
                {
                    float ex = ea[k] * du * u.X + eb[k] * dv * v.X;
                    float ey = ea[k] * du * u.Y + eb[k] * dv * v.Y;
                    if (ex > 0.001f) { float q = (_vs.Width - fx.X) / ex; if (q < t) t = q; }
                    else if (ex < -0.001f) { float q = (0f - fx.X) / ex; if (q < t) t = q; }
                    if (ey > 0.001f) { float q = (_vs.Height - fx.Y) / ey; if (q < t) t = q; }
                    else if (ey < -0.001f) { float q = (0f - fx.Y) / ey; if (q < t) t = q; }
                }
                float tMin = Math.Max(6f / du, 6f / dv);
                if (t < tMin) t = tMin;
                if (t > 1f) t = 1f;
                du *= t; dv *= t;
            }

            _sz = new SizeF(du, dv);
            _c = new PointF(fx.X + u.X * su * du / 2f + v.X * sv * dv / 2f,
                            fx.Y + u.Y * su * du / 2f + v.Y * sv * dv / 2f);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasRotating = _rotating;
            _moving = false; _resizeCorner = -1; _rotating = false;
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                if (!_hasSel || _sz.Width < 3 || _sz.Height < 3) { _hasSel = false; Invalidate(); }
            }
            // 旋转结束后把（能塞下的）选区收进屏幕，避免转到边角后整块跑到屏外找不回来
            if (wasRotating && _hasSel)
            {
                RectangleF bb = SelBounds();
                float hw = bb.Width / 2f, hh = bb.Height / 2f;
                bool ch = false;
                if (hw * 2f <= _vs.Width)
                {
                    if (_c.X < hw) { _c.X = hw; ch = true; }
                    if (_c.X > _vs.Width - hw) { _c.X = _vs.Width - hw; ch = true; }
                }
                if (hh * 2f <= _vs.Height)
                {
                    if (_c.Y < hh) { _c.Y = hh; ch = true; }
                    if (_c.Y > _vs.Height - hh) { _c.Y = _vs.Height - hh; ch = true; }
                }
                if (ch) Invalidate();
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
        float PeekScale { get { return Math.Max(1.2f, Math.Min(5f, _settings.PeekPercent / 100f)); } }

        // ---------- 风格参数（新拟态 / 扁平 / 毛玻璃）----------
        bool StyleNeu() { return _settings.UiStyle != "flat" && _settings.UiStyle != "solid"; }
        bool StyleSolid() { return _settings.UiStyle == "solid"; }
        bool StyleFlatOnly() { return _settings.UiStyle == "flat"; }

        // 动画速度：>1 = 更快。所有时长都乘这个系数，保证各段动画不会各走各的
        float AnimK() { return 100f / Math.Max(50f, Math.Min(200f, (float)_settings.AnimSpeed)); }

        // 面板底色（毛玻璃的"玻璃"部分；solid 风格强制不透明，方便在花哨壁纸上也能看清）
        Color GlassBase()
        {
            if (StyleSolid()) return Color.FromArgb(30, 32, 38);
            if (StyleFlatOnly()) return Color.FromArgb(26, 28, 34);
            return Color.FromArgb(20, 23, 30);
        }

        int GlassA(int baseA)
        {
            float g = StyleSolid() ? 1f : (_settings.GlassPercent / 100f);
            int a = (int)(baseA * g);
            return a < 0 ? 0 : (a > 255 ? 255 : a);
        }

        int ShadowA(int baseA) { return (int)(baseA * _settings.ShadowPercent / 100f); }

        float CardRadOf(RectangleF r)
        {
            float rad = Math.Min(r.Width, r.Height) * (_settings.CardRadius / 100f);
            return rad < 3f ? 3f : rad;
        }

        Color AccentColor()
        {
            int i = _settings.AccentIndex;
            if (i >= 0 && i < Palette.Colors.Length) return Palette.Get(i);
            return _mgr.Accent;
        }

        float _keyT = 0f;              // 万能键按下进度 0..1
        float _keyHov = 0f;            // 万能键悬停进度 0..1（指针压上去要有反应）
        bool _keyHover = false;
        bool _nameHover = false;       // 指针停在 Wheel 名药丸上（提示"点一下改名"）
        // 圆钮按下反馈：按下先变暗缩一下，过 ~110ms 再真正执行，这样"按下去"是看得见的
        float _closeDown = 0f, _gearDown = 0f, _shootDown = 0f;
        bool _closePend = false, _gearPend = false, _shootPend = false;   // 已按下、等延迟
        bool _closeHold = false, _gearHold = false, _shootHold = false;   // 鼠标仍按着
        DateTime _closeDownAt = DateTime.MinValue;                        // 关闭键按下的时刻（判长按）
        bool _closeLong = false;                                          // 关闭键已长按到位（变红，松手退出）
        string _pendingBtn = "";
        DateTime _pendingAt = DateTime.MinValue;
        bool _intro = false;           // 正在播环的动画（拉出 / 收起都算）
        float _introT = 0f;            // 0..1：环"露出来"的程度
        float _introDur = 1.8f;        // 秒（完成一整趟 0->1 的时间基准）
        DateTime _introAt = DateTime.MinValue;
        float _ringFrom = 1f;          // 本次动画的起点值
        float _ringTo = 1f;            // 本次动画的目标值（可反向，随时改目标）

        // ---------- 收起态（像贴边小球那样，只在屏幕边上留一个可点的小把手）----------
        bool _collapsed = false;       // 已完全收起：只画把手，不画环
        bool _collapsing = false;      // 当前这次动画是"收起"方向
        bool _nubOutHover = false;     // 指针停在"拉出"把手上
        bool _nubInHover = false;      // 指针停在"收起"把手上
        float _nubHov = 0f;            // 把手悬停进度 0..1
        DateTime _collapsedAt = DateTime.MinValue;       // 收起完成的时刻（之后一小段内不允许再展开）
        DateTime _selfClipboardAt = DateTime.MinValue;   // 我们自己写剪贴板的时间（避免自己抄自己）
        string _lastClipFp = "";                          // 上一张从剪贴板收进来的图（去重用）

        public bool IsCollapsed { get { return _collapsed; } }
        public bool IsExpanded { get { return !_collapsed && !_intro; } }

        // 贴边把手：一条沿"竖直的屏幕边"（拉出），一条沿"水平的屏幕边"（收起）
        const float NubLong = 78f;     // 把手长边
        const float NubThick = 13f;    // 把手厚度（贴着屏幕边）

        // 把手 = 卷轴的两端。环的圆弧从"竖直那条边上距角落 R 处"扫到"水平那条边上距角落 R 处"，
        // 这两个端点就是卷轴的两头，把手就钉在端点上，正好接住弧的末端。
        // 卡片都往内缩了一个安全角（phiMin），所以把手不会压到最边上的那张卡。
        float _nubOutDist = -1f, _nubInDist = -1f;   // <0 = 用自动值；调参时可覆盖
        float NubDistAuto() { return _R; }
        float NubDistOut() { return _nubOutDist >= 0f ? _nubOutDist : NubDistAuto(); }
        float NubDistIn() { return _nubInDist >= 0f ? _nubInDist : NubDistAuto(); }

        // 按下时把矩形按比例缩小（以中心为基准）
        static Rectangle Shrink(Rectangle r, float k)
        {
            if (k <= 0.001f) return r;
            float s = 1f - 0.10f * k;
            int w = (int)Math.Round(r.Width * s), h = (int)Math.Round(r.Height * s);
            return new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        }

        RectangleF NubOutRect()
        {
            SizeF ls = LogicalSize();
            float cy = (Sy() > 0) ? NubDistOut() : ls.Height - NubDistOut();
            float x = (Sx() > 0) ? 0f : ls.Width - NubThick;
            return new RectangleF(x, cy - NubLong / 2f, NubThick, NubLong);
        }

        // 单把手模式：右下边那个把手不画、也不能点（任务栏自动隐藏时鼠标扫底边不会撞到它）
        public bool NubSingleMode() { return _settings.NubSingle; }

        bool CanExpandByNub()
        {
            return true;      // 不做冷却：收起后也可以立刻再展开（两个方向都随时可点）
        }

        RectangleF NubInRect()
        {
            SizeF ls = LogicalSize();
            float cx = (Sx() > 0) ? NubDistIn() : ls.Width - NubDistIn();
            float y = (Sy() > 0) ? 0f : ls.Height - NubThick;
            return new RectangleF(cx - NubLong / 2f, y, NubLong, NubThick);
        }

        // 一整趟 0 -> 1 的时间基准（不含速度设置）
        float RingUnitDurBase()
        {
            float d = 0.82f + 0.050f * _store.Items.Count;     // 5 张图约 1.1s
            if (d < 0.75f) d = 0.75f;
            if (d > 1.65f) d = 1.65f;
            return d;
        }

        // 一趟 0->1 的时长：展开和收起各用各的速度百分比（越大越快）
        float RingUnitDur() { return RingUnitDur(false); }

        float RingUnitDur(bool collapsing)
        {
            int sp = collapsing ? _settings.CollapseSpeed : _settings.ExpandSpeed;
            if (sp < 40) sp = 40;
            if (sp > 250) sp = 250;
            return RingUnitDurBase() * AnimK() * (100f / sp);
        }

        // 展开（彩虹拉出）；fast=true 用于截图流程，快一点
        public void ExpandWheel() { ExpandWheel(false); }

        public void ExpandWheel(bool fast)
        {
            if (IsExpanded && Visible) { _lastActive = DateTime.Now; return; }
            if (_settings.IntroAnim)
            {
                StartIntro();
                if (fast) { _introAt = DateTime.Now; _introDur = RingUnitDur(false) * 0.55f; }
            }
            else { _collapsed = false; _collapsing = false; _intro = false; _introT = 1f; ShowWheel(); }
            _lastActive = DateTime.Now;
        }

        // 开机直接进入收起态：窗口显示出来，但只有贴边那个小把手
        public void StartCollapsed()
        {
            _collapsed = true;
            _collapsing = false;
            _intro = false;
            _introT = 0f;
            CaptureBackdrop();
            _show = 1f; _targetShow = 1f; _showAnimating = false;
            _rendered = false;
            Show();
            Render();
            _lastActive = DateTime.Now;
        }

        // 收起（彩虹缩回）—— 收起态没开的话就退回原来的"直接隐藏"
        public void CollapseWheel() { CollapseWheel(false); }

        public void CollapseWheel(bool fast)
        {
            if (!_settings.CollapseMode) { HideWheel(); return; }
            if (_collapsed) return;
            if (!Visible) { _collapsed = true; _collapsing = false; _introT = 0f; _intro = false; _show = 1f; _targetShow = 1f; _showAnimating = false; _collapsedAt = DateTime.Now; CaptureBackdrop(); Show(); Render(); return; }
            _collapsing = true;
            _intro = true;
            _ringFrom = _introT;              // 从"现在伸到哪"开始往回收，不跳到完全展开
            _ringTo = 0f;
            _introAt = DateTime.Now;
            _introDur = RingUnitDur(true) * (fast ? 0.14f : 1f);   // 收起用「收起速度」
            _keyDown = false; _menuOpen = false; _menuT = 0f; _enlarged = -1; _hover = -1;
            _lastActive = DateTime.Now;
            Render();                       // 立刻出一帧，点了就能看到动
        }

        // 界面上"关掉轮盘"的动作：收起态开着就收起，否则完全隐藏
        public void DismissWheel()
        {
            if (_settings.CollapseMode) CollapseWheel();
            else HideWheel();
        }

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
            DragLeave += new EventHandler(delegate(object o, EventArgs ev) { ClearDropCache(); if (_dropActive) { _dropActive = false; _dropExternal = false; Render(); } });
            DragDrop += new DragEventHandler(OnDragDropWheel);
            _anim = new Timer();
            _anim.Interval = 15;
            _anim.Tick += new EventHandler(AnimTick);
            _anim.Start();
        }

        // ---------- 分辨率 / DPI 适配 ----------
        // UiK = 界面缩放系数。所有绘制都在"逻辑坐标"里做，DrawWheel 开头统一 ScaleTransform(UiK)，
        // 于是字体、图标、线宽、间距全都跟着放大，不用到处改常数。
        // 命中测试也全在逻辑坐标里算：鼠标进来先 ToLogical() 转一次。
        float UiK = 1f;

        float AutoUiK()
        {
            try { return Native.DpiScaleOf(IsHandleCreated ? Handle : IntPtr.Zero); }
            catch { return 1f; }
        }

        PointF ToLogical(Point p) { return new PointF(p.X / UiK, p.Y / UiK); }
        Point ToLogicalPt(Point p) { return new Point((int)Math.Round(p.X / UiK), (int)Math.Round(p.Y / UiK)); }
        SizeF LogicalSize() { return new SizeF(Width / UiK, Height / UiK); }

        // 把鼠标事件里的物理坐标换成逻辑坐标（其余字段原样带过来）
        MouseEventArgs LogicalArgs(MouseEventArgs e)
        {
            if (Math.Abs(UiK - 1f) < 0.001f) return e;
            return new MouseEventArgs(e.Button, e.Clicks, (int)Math.Round(e.X / UiK), (int)Math.Round(e.Y / UiK), e.Delta);
        }

        public void ApplyLayout()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            float baseSpan = Math.Max(120, Math.Min(700, _settings.Radius)) +
                             Math.Max(40, Math.Min(260, _settings.ThumbSize)) * 1.75f + 190f;

            // 自动 = 跟显示器缩放比例走（2K@125% -> 1.25，4K@150% -> 1.5），看起来大小才一致；
            // 但自动模式不会把轮盘撑得比屏幕还大
            float k = (_settings.UiScale > 0) ? (_settings.UiScale / 100f) : AutoUiK();
            if (_settings.UiScale <= 0)
            {
                float fit = Math.Min(wa.Width, wa.Height) / baseSpan;
                if (k > fit) k = fit;
            }
            UiK = Math.Max(0.6f, Math.Min(2.5f, k));

            _thumb = Math.Max(40, Math.Min(260, _settings.ThumbSize));     // 逻辑值
            _R = Math.Max(120, Math.Min(700, _settings.Radius));           // 逻辑值
            _slots = Math.Max(2, Math.Min(12, _settings.Slots));
            _phiMin = (float)Math.Asin(Math.Min(0.92, (_thumb * 0.80f) / _R));
            _phiMax = (float)(Math.PI / 2) - _phiMin;
            // 逻辑尺寸 -> 物理像素：半径 + 摇杆键(92) + 名字标签
            int size = (int)Math.Round((_R + _thumb * 1.75f + 190f) * UiK);
            if (size < 160) size = 160;
            int cap = Math.Min(wa.Width, wa.Height);
            if (size > cap) size = cap;            // 别让窗口比屏幕还大（角落外那截本来就是空的）
            if (Size.Width != size) Size = new Size(size, size);
            PlaceBottomLeft();
            _rendered = false;
            FreeBackdrop();                    // 尺寸/位置变了，玻璃底得重抓
            if (Visible) { CaptureBackdrop(); Render(); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 让窗口对截屏隐身：这样玻璃底可以随时重抓（不会把轮盘自己拍进去），
            // 顺带好处是用户截图时轮盘不会出现在图里
            try { Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); } catch { }
            try { Native.AddClipboardFormatListener(Handle); } catch { }   // 剪贴板里有新图 -> 自动收进轮盘
            // 句柄建好之后 DPI 才查得准；自动模式下补一次布局
            if (_settings.UiScale <= 0)
            {
                float want = Math.Max(0.6f, Math.Min(2.5f, AutoUiK()));
                if (Math.Abs(want - UiK) > 0.01f) ApplyLayout();
            }
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
            if (!Visible) { CaptureBackdrop(); _show = 0f; _rendered = false; Show(); }
            SetShow(1f);
            _lastActive = DateTime.Now;
            Render();
        }

        // 软件刚启动时用它：带开启动画地显示出来
        public void ShowWheelWithIntro()
        {
            if (_settings.IntroAnim) { StartIntro(); }
            else ShowWheel();
        }

        // 开启动画：整条环像彩虹一样扫出来，图片一张张沿弧线滑落，万能键/按钮/文字从屏幕外滑入并渐显
        public void StartIntro()
        {
            if (!Visible) { CaptureBackdrop(); _show = 0f; _rendered = false; Show(); }
            _show = 1f; _targetShow = 1f; _showAnimating = false;
            _collapsed = false; _collapsing = false;
            _intro = true;
            _ringFrom = _introT;
            _ringTo = 1f;
            _introAt = DateTime.Now;
            _introDur = RingUnitDur();
            _scales.Clear(); _enterT0.Clear();
            for (int i = 0; i < _store.Items.Count; i++)
                _enterT0[_store.Items[i]] = DateTime.Now.AddSeconds(0.45 + i * 0.13);
            Render();
        }

        // 开启动画里每个元素的进度（1 = 完全就位）；delay 越大越晚出场。
        // 收起时 _introT 是往回走的，同一个公式自然就变成倒放。
        //
        // delay 是 0..1 的"相对出场顺序"，不是秒！原来写的是秒（最大 0.72s + 0.55s 过渡），
        // 总时长从 2.15s 压到 1.07s 后，后面的元素根本走不到位 ——
        // 动画一结束就从半路"啪"地闪到最终位置（按钮、计数胶囊就是这么闪的）。
        float IntroP(float delay)
        {
            if (_collapsed) return 0f;
            if (!_intro) return 1f;
            const float span = 0.46f;                  // 单个元素自己的过渡长度（占总时长比例）
            float start = delay * (1f - span);
            float x = (_introT - start) / span;
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            // 每个元素自己用 easeOut：一进窗口就动起来，落点又是缓的
            float u2 = 1f - x;
            return 1f - u2 * u2 * u2;
        }

        // 从屏幕外滑进来的位移（朝角落方向，也就是朝屏幕外）
        PointF IntroShift(float p)
        {
            if (!_intro && !_collapsed) return new PointF(0f, 0f);
            float off = (1f - p) * 110f;
            return new PointF((Sx() > 0 ? -off : off), (Sy() > 0 ? -off : off));
        }

        // 收起过程中图片的淡出因子（1 -> 0，在环完全缩回之前就淡完）
        float CollapseCardP()
        {
            if (_collapsed) return 0f;
            if (!_intro || !_collapsing) return 1f;
            float v = _introT / 0.55f;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }


        public void HideWheel() { SetShow(0f); }
        public void ToggleWheel()
        {
            if (_collapsed) ShowWheelWithIntro();          // 收起态 -> 拉出来（带彩虹动画）
            else if (_targetShow > 0.5f) DismissWheel();   // 展开中 -> 收起（或隐藏）
            else ShowWheelWithIntro();
        }

        void SetShow(float target)
        {
            if (Math.Abs(_targetShow - target) < 0.001f && _showAnimating == false) { _targetShow = target; return; }
            _showFrom = _show;
            _showT0 = DateTime.Now;
            _targetShow = target;
            _showAnimating = true;
        }

        // 这一帧算错了不该让整个程序挂掉（Timer 里抛异常会直接弹崩溃框）
        void AnimTick(object sender, EventArgs e)
        {
            try { AnimTickCore(); }
            catch (Exception ex) { try { Err.Log("wheel.AnimTick", ex); } catch { } }
        }

        void AnimTickCore()
        {
            bool need = false;
            bool hide = _targetShow < _show;
            if (_showAnimating)
            {
                float dur = (hide ? 0.50f : 0.30f) * AnimK();      // 速度设置会一起缩放
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
                Point cp = ToLogicalPt(PointToClient(Cursor.Position));
                int hh = HitTest(cp);
                bool gh = GearButtonRect().Contains(cp);
                bool ch = CloseButtonRect().Contains(cp);
                bool sH = ShootButtonRect().Contains(cp);
                bool kh = KeyRect().Width > 8 && KeyRect().Contains(cp);
                bool nh = NamePillRect().Contains(cp);
                if (hh != _hover) { _hover = hh; need = true; }
                if (gh != _gearHover) { _gearHover = gh; need = true; }
                if (ch != _closeHover) { _closeHover = ch; need = true; }
                if (sH != _shootHover) { _shootHover = sH; need = true; }
                if (kh != _keyHover) { _keyHover = kh; need = true; }
                if (nh != _nameHover) { _nameHover = nh; need = true; }
            }

            if (_closeHold) { Point cp3 = ToLogicalPt(PointToClient(Cursor.Position)); if (!CloseButtonRect().Contains(cp3)) _closeHold = false; }
            if (_gearHold) { Point cp4 = ToLogicalPt(PointToClient(Cursor.Position)); if (!GearButtonRect().Contains(cp4)) _gearHold = false; }
            if (_shootHold) { Point cp5 = ToLogicalPt(PointToClient(Cursor.Position)); if (!ShootButtonRect().Contains(cp5)) _shootHold = false; }

            if (_holdIndex >= 0 && _maybeDrag && _enlarged < 0)
                if ((DateTime.Now - _holdStart).TotalMilliseconds > 300) { _enlarged = _holdIndex; need = true; }

            // per-item scale: hover 1.36x, hold-to-peek 2.4x - ONE animation, so it can never desync
            for (int i = 0; i < _store.Items.Count; i++)
            {
                float tgt;
                if (_store.Items[i] == _dragOutItem) tgt = 0f;
                else if (i == _enlarged) tgt = PeekScale;                else if (i == _hover) tgt = HoverScale;
                else tgt = 1f;
                float cur;
                if (!_scales.TryGetValue(i, out cur)) cur = 1f;
                float rate = (tgt > 1.5f || cur > 1.5f) ? 0.16f : 0.19f;   // peek moves a little slower
                rate = Math.Max(0.05f, Math.Min(0.5f, rate / AnimK()));
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

            // 提示条（"已加入 N 张图片"）淡入淡出
            if (_toast.Length > 0)
            {
                if ((DateTime.Now - _toastAt).TotalSeconds > 2.6f) _toast = "";
                else need = true;
            }

            // 开启动画进度（收起时 _introT 往回走，同一个公式就是倒放）
            if (_intro)
            {
                // 时间按"这次要走多远"算：走得近就快，走完就停 —— 展开/收起共用，随时可反向
                float span = Math.Abs(_ringTo - _ringFrom);
                float dur = Math.Max(0.10f, _introDur * span);
                float t = (float)((DateTime.Now - _introAt).TotalSeconds / dur);
                if (t >= 1f) { t = 1f; _intro = false; }
                // 主进度走"线性"：这样展开和收起在时间上是对称的
                // （之前用 easeOutCubic 作用在主进度上，展开时动作集中在开头、
                //   收起时集中在结尾，于是"展开比收起快太多"）
                _introT = _ringFrom + (_ringTo - _ringFrom) * t;
                if (!_intro)
                {
                    _introT = _ringTo;
                    if (_collapsing) { _collapsed = true; _collapsing = false; _collapsedAt = DateTime.Now; }   // 收完了：进入收起态
                }
                need = true;
            }

            // 关闭键长按判定：按住 0.65s 就"变红"，松手退出
            if (_closeHold && !_closeLong && (DateTime.Now - _closeDownAt).TotalMilliseconds > 650)
            { _closeLong = true; need = true; }

            // 圆钮按下反馈 + 延迟执行
            {
                float cd = (_closePend || _closeHold) ? 1f : 0f;
                float gd = (_gearPend || _gearHold) ? 1f : 0f;
                float sd = (_shootPend || _shootHold) ? 1f : 0f;
                if (Math.Abs(_closeDown - cd) > 0.01f) { _closeDown += (cd - _closeDown) * 0.35f; need = true; }
                if (Math.Abs(_gearDown - gd) > 0.01f) { _gearDown += (gd - _gearDown) * 0.35f; need = true; }
                if (Math.Abs(_shootDown - sd) > 0.01f) { _shootDown += (sd - _shootDown) * 0.35f; need = true; }
                if (_pendingBtn.Length > 0 && (DateTime.Now - _pendingAt).TotalMilliseconds > 110)
                {
                    string b = _pendingBtn; _pendingBtn = "";
                    _closePend = _gearPend = _shootPend = false;
                    if (b == "close") DismissWheel();
                    else if (b == "gear") { if (SettingsRequested != null) SettingsRequested(this, EventArgs.Empty); }
                    else if (b == "shoot") { if (CaptureRequested != null) CaptureRequested(this, EventArgs.Empty); }
                    need = true;
                }
                if (_closeDown > 0.01f || _gearDown > 0.01f || _shootDown > 0.01f) need = true;
            }

            // 把手悬停反馈
            {
                bool wantOut = false, wantIn = false;
                if (Visible && _show > 0.6f)
                {
                    Point hp = ToLogicalPt(PointToClient(Cursor.Position));
                    if (_collapsed) wantOut = NubOutRect().Contains(hp);
                    else if (NubSingleMode()) wantOut = NubOutRect().Contains(hp);
                    else if (!_intro) wantIn = NubInRect().Contains(hp);
                }
                if (wantOut != _nubOutHover) { _nubOutHover = wantOut; need = true; }
                if (wantIn != _nubInHover) { _nubInHover = wantIn; need = true; }
                float wantH = (wantOut || wantIn) ? 1f : 0f;
                if (Math.Abs(_nubHov - wantH) > 0.006f) { _nubHov += (wantH - _nubHov) * 0.24f; need = true; }
                else if (_nubHov != wantH) { _nubHov = wantH; need = true; }
                if (_nubHov > 0.01f) need = true;
            }

            // 玻璃底定时重抓：轮盘一直挂着也不会"糊的是半小时前的桌面"
            // （窗口已设置 WDA_EXCLUDEFROMCAPTURE，抓屏不会把轮盘自己拍进去，所以显示中也能抓）
            if (_settings.GlassRefresh && Visible && _show > 0.99f && !_intro)
            {
                if ((DateTime.Now - _backdropAt).TotalSeconds > 1.6)
                {
                    _backdropAt = DateTime.Now;
                    if (_hover < 0 && !_menuOpen && _enlarged < 0 && !_dropActive && _dragOutItem == null && _deletingItem == null)
                    {
                        bool hoverAny = _gearHover || _closeHover || _shootHover || _keyHover || _nameHover;
                        // 把手悬停/按钮按下时绝不重抓：抓屏+模糊要几十毫秒，会让人觉得"点了没反应"
                        bool busy = _nubHov > 0.01f || _nubOutHover || _nubInHover
                                    || _closePend || _gearPend || _shootPend
                                    || _closeDown > 0.01f || _gearDown > 0.01f || _shootDown > 0.01f;
                        if (!hoverAny && !_keyDown && !busy)
                        {
                            CaptureBackdrop();
                            _rendered = false;
                            need = true;
                        }
                    }
                }
            }

            // 万能键按下/松开 + 悬停 的弹性反馈
            {
                float kt = _keyDown ? 1f : 0f;
                float cur = _keyT;
                if (Math.Abs(cur - kt) > 0.004f) { _keyT = cur + (kt - cur) * (kt > cur ? 0.35f : 0.22f); need = true; }
                else if (cur != kt) { _keyT = kt; need = true; }

                float kh = _keyHover ? 1f : 0f;
                float curH = _keyHov;
                if (Math.Abs(curH - kh) > 0.006f) { _keyHov = curH + (kh - curH) * 0.24f; need = true; }
                else if (curH != kh) { _keyHov = kh; need = true; }
                if (_keyHov > 0.01f) need = true;         // 悬停时保持刷新，光晕是渐变的
            }

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
            Color want = AccentColor();
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
                    Point cp2 = ToLogicalPt(PointToClient(Cursor.Position));
                    if (kr2.Contains(cp2)) want2 = (cp2.X < kr2.X + kr2.Width / 2f) ? 0 : 1;
                }
                if (want2 != _delHalf) { _delHalf = want2; need = true; }
                if ((DateTime.Now - _delConfirmAt).TotalSeconds > 10) { _delConfirm = false; _delHalf = -1; need = true; }
            }
            else if (_delHalf != -1) { _delHalf = -1; need = true; }

            if (_settings.AutoHide && !_collapsed && _targetShow > 0.5f && _show > 0.99f && !_intro)
                if ((DateTime.Now - _lastActive).TotalSeconds > _settings.AutoHideSeconds) DismissWheel();

            if (_show <= 0.002f && _targetShow <= 0.002f)
            {
                if (Visible) { Hide(); CaptureBackdrop(); }   // 隐藏后再抓一次，下次显示时玻璃底是新的
                return;
            }
            if (need || !_rendered) Render();   // render ONLY when something changed (smooth + cheap)
        }

        // ---- geometry: a full ring whose centre sits on the chosen screen corner ----
        float Sx() { return _settings.Corner.EndsWith("L") ? 1f : -1f; }   // outward horizontal (into screen)
        float Sy() { return _settings.Corner.StartsWith("T") ? 1f : -1f; } // outward vertical (into screen)

        PointF Center()
        {
            // 逻辑坐标（绘制带 UiK 缩放，命中测试也统一用逻辑坐标）
            SizeF ls = LogicalSize();
            float cx = (Sx() > 0) ? 0f : ls.Width;
            float cy = (Sy() > 0) ? 0f : ls.Height;
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
            float t = d / (0.42f * AnimK());
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);      // smoothstep -> eased, silky
        }

        // 只有“真的拉得很长”的图才进特殊方框：宽高比超过 ExtremeRatio:1（或反过来）才算。
        // 想改判定松紧，只动这一个数就行：越小越容易进方框，越大越严格。
        public const float ExtremeRatio = 4.5f;

        // very extreme aspect ratios get a fixed square box with a distinctive border colour
        static bool IsExtreme(StoreItem it)
        {
            if (it == null || it.Image == null) return false;
            float a = ImgW(it) / Math.Max(1f, ImgH(it));
            return a > ExtremeRatio || a < (1f / ExtremeRatio);
        }

        // card size == the image's exact aspect; extreme ones become a square box
        // 已释放的 Image 不是 null，直接读宽高会抛 ArgumentException —— 统一走这里兜住
        static float ImgW(StoreItem it) { try { return (it != null && it.Image != null) ? it.Image.Width : 1f; } catch { return 1f; } }
        static float ImgH(StoreItem it) { try { return (it != null && it.Image != null) ? it.Image.Height : 1f; } catch { return 1f; } }

        SizeF CardSize(StoreItem it)
        {
            float iw = ImgW(it);
            float ih = ImgH(it);
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
            if (alpha >= 250) { g.DrawImage(bmp, dest); return; }
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
            // w/h 是逻辑尺寸；实际按物理像素生成，缩放到高 DPI 屏上才不会发虚
            int dw = Math.Max(1, (int)Math.Round(w * UiK));
            int dh = Math.Max(1, (int)Math.Round(h * UiK));
            Dictionary<long, Bitmap> d;
            if (!_thumbCache.TryGetValue(it, out d)) { d = new Dictionary<long, Bitmap>(); _thumbCache[it] = d; }
            long key = ((long)dw << 20) | (uint)dh;
            Bitmap b;
            if (d.TryGetValue(key, out b)) return b;
            if (d.Count > 48)
            {
                foreach (Bitmap v in d.Values) { try { v.Dispose(); } catch { } }
                d.Clear();
            }
            b = new Bitmap(dw, dh, PixelFormat.Format32bppPArgb);
            using (Graphics gg = Graphics.FromImage(b))
            {
                gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                gg.DrawImage(it.Image, new Rectangle(0, 0, dw, dh));
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

        // ---------- 轮盘上的真·毛玻璃 ----------
        // 分层窗口不能上系统 acrylic（会给整个窗口矩形蒙灰），所以自己来：
        // 显示之前把轮盘背后那块屏幕抓下来 → 缩到 1/6 做盒式模糊 → 放大回去，
        // 画面板时把这块模糊底裁进形状里，再叠玻璃色 —— 透过去的确实是真桌面。
        DateTime _backdropAt = DateTime.MinValue;
        Bitmap _backdropBlur;
        bool _backdropValid;
        Point _backdropOffset = new Point(0, 0);

        void FreeBackdrop()
        {
            if (_backdropBlur != null) { try { _backdropBlur.Dispose(); } catch { } _backdropBlur = null; }
            _backdropValid = false;
        }

        public void CaptureBackdrop()
        {
            FreeBackdrop();
            if (StyleFlatOnly()) return;
            try
            {
                if (Width < 20 || Height < 20) return;
                Rectangle vs = SystemInformation.VirtualScreen;
                Rectangle want = new Rectangle(Left, Top, Width, Height);
                Rectangle got = Rectangle.Intersect(want, vs);
                if (got.Width < 8 || got.Height < 8) return;
                using (Bitmap full = new Bitmap(got.Width, got.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(full))
                        g.CopyFromScreen(got.Left, got.Top, 0, 0, new Size(got.Width, got.Height), CopyPixelOperation.SourceCopy);
                    _backdropBlur = BlurBitmap(full, 6);
                    _backdropOffset = new Point(got.Left - want.Left, got.Top - want.Top);
                    _backdropValid = true;
                    _backdropAt = DateTime.Now;
                }
            }
            catch { FreeBackdrop(); }
        }

        // 缩小 -> 盒式模糊 -> 放大，得到"毛玻璃"那种糊
        static Bitmap BlurBitmap(Bitmap src, int down)
        {
            int w = Math.Max(2, src.Width / down), h = Math.Max(2, src.Height / down);
            using (Bitmap small = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
                BoxBlur(small, 3);
                BoxBlur(small, 3);
                BoxBlur(small, 2);
                Bitmap big = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(small, new Rectangle(0, 0, big.Width, big.Height));
                }
                return big;
            }
        }

        static void BoxBlur(Bitmap bmp, int r)
        {
            try
            {
                Rectangle rc = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData d = bmp.LockBits(rc, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    int W = d.Width, H = d.Height, n = W * H;
                    int[] px = new int[n];
                    int[] tmp = new int[n];
                    Marshal.Copy(d.Scan0, px, 0, n);
                    for (int y = 0; y < H; y++)
                    {
                        int row = y * W;
                        for (int x = 0; x < W; x++)
                        {
                            int a = 0, rr = 0, gg = 0, bb = 0, c = 0;
                            for (int k = -r; k <= r; k++)
                            {
                                int xx = x + k; if (xx < 0) xx = 0; if (xx >= W) xx = W - 1;
                                int v = px[row + xx];
                                a += (v >> 24) & 0xFF; rr += (v >> 16) & 0xFF; gg += (v >> 8) & 0xFF; bb += v & 0xFF; c++;
                            }
                            tmp[row + x] = ((a / c) << 24) | ((rr / c) << 16) | ((gg / c) << 8) | (bb / c);
                        }
                    }
                    for (int x = 0; x < W; x++)
                        for (int y = 0; y < H; y++)
                        {
                            int a = 0, rr = 0, gg = 0, bb = 0, c = 0;
                            for (int k = -r; k <= r; k++)
                            {
                                int yy = y + k; if (yy < 0) yy = 0; if (yy >= H) yy = H - 1;
                                int v = tmp[yy * W + x];
                                a += (v >> 24) & 0xFF; rr += (v >> 16) & 0xFF; gg += (v >> 8) & 0xFF; bb += v & 0xFF; c++;
                            }
                            px[y * W + x] = ((a / c) << 24) | ((rr / c) << 16) | ((gg / c) << 8) | (bb / c);
                        }
                    Marshal.Copy(px, 0, d.Scan0, n);
                }
                finally { bmp.UnlockBits(d); }
            }
            catch { }
        }

        bool UseBackdrop() { return _backdropValid && _backdropBlur != null && !StyleFlatOnly(); }

        // 把模糊背景裁进这个形状里（画玻璃面板前先调它）
        // alpha 必须传进来：否则淡出动画时玻璃底不跟着变淡，面板会像"卡住"一样不消失
        void BackdropClip(Graphics g, GraphicsPath path, int alpha)
        {
            if (!UseBackdrop() || alpha <= 2) return;
            try
            {
                GraphicsState st = g.Save();
                g.SetClip(path, CombineMode.Replace);
                // 这块模糊底是"物理像素"尺寸，而当前处在 UiK 缩放后的逻辑坐标系里，
                // 必须除回去，否则高 DPI 下会画得又大又偏 —— 毛玻璃直接就废了
                float bw = _backdropBlur.Width, bh = _backdropBlur.Height;
                RectangleF dest = new RectangleF(_backdropOffset.X / UiK, _backdropOffset.Y / UiK,
                                                 bw / UiK, bh / UiK);
                int al = alpha > 255 ? 255 : alpha;
                if (al >= 250) g.DrawImage(_backdropBlur, dest);
                else
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = al / 255f;
                    _iaBack.SetColorMatrix(cm);
                    g.DrawImage(_backdropBlur,
                        new Rectangle((int)Math.Round(dest.X), (int)Math.Round(dest.Y),
                                      Math.Max(1, (int)Math.Round(dest.Width)), Math.Max(1, (int)Math.Round(dest.Height))),
                        0, 0, bw, bh, GraphicsUnit.Pixel, _iaBack);
                }
                // 压一层黑：不管背后是亮桌面还是暗桌面，面板都能保持"深色玻璃"、字看得清
                int dk = (int)(26 * (StyleSolid() ? 1f : _settings.GlassPercent / 100f) * al / 255f);
                if (dk > 0)
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(dk, 0, 0, 0)))
                        g.FillPath(sb, path);
                g.Restore(st);
            }
            catch { try { g.ResetClip(); } catch { } }
        }

        ImageAttributes _iaBack = new ImageAttributes();

        // 名字太长就把中间省略掉，免得药丸撑太宽压到别的东西
        public static string FitName(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            if (max <= 1) return s.Substring(0, 1);
            int head = (max - 1) / 2, tail = max - 1 - head;
            return s.Substring(0, head) + "…" + s.Substring(s.Length - tail, tail);
        }

        // Wheel 名药丸的矩形（画的时候记下来，命中测试用同一个，改了名字也不会错位）
        RectangleF _namePillRect = RectangleF.Empty;
        RectangleF NamePillRect()
        {
            if (!_settings.ShowNameLabel) return RectangleF.Empty;
            if (_namePillRect.Width > 1f) return _namePillRect;
            // 还没画过（刚启动/刚开关过标签）就先按万能键位置估一个，保证点得到
            Rectangle kr = KeyRect();
            if (kr.Width < 8) return RectangleF.Empty;
            return new RectangleF(kr.X + kr.Width / 2f - 60f, kr.Bottom + 4f, 120f, 30f);
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

        // 点名字药丸改名
        void RenameWheel()
        {
            Wheel w = _mgr.ActiveWheel;
            string old = w.Name;
            try
            {
                using (RenameForm rf = new RenameForm(old))
                {
                    TopMost = false;                     // 轮盘别盖在弹框上面
                    rf.TopMost = true;
                    DialogResult r = rf.ShowDialog();
                    TopMost = _settings.AlwaysOnTop;
                    if (r != DialogResult.OK) { Render(); return; }
                    string nv = rf.Value;
                    if (!string.IsNullOrEmpty(nv) && nv != old)
                    {
                        w.Name = nv;
                        _mgr.Save();
                        ShowToast("已改名为「" + nv + "」");
                    }
                }
            }
            catch { }
            Render();
        }
        public event EventHandler CaptureRequested;
        public event EventHandler ExitRequested;      // 长按关闭键 -> 完全退出

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
            // 计数标签放到环外侧的 45° 对角线上，彻底离开万能键和角上的按钮
            PointF c = Center();
            float d = EffR() + 78f;
            float dx = d * 0.7071f, dy = d * 0.7071f;
            float bx = (Sx() > 0) ? c.X + dx : c.X - dx;
            float by = (Sy() > 0) ? c.Y + dy : c.Y - dy;
            return new PointF(bx - sz.Width / 2f, by - sz.Height / 2f);
        }

        bool OverContent(Point p)
        {
            // 贴边把手：收起态只有它可点；展开态它也算内容（不然点它会穿透到桌面）
            if (NubSingleMode())
            {
                if (NubOutRect().Contains(p)) return true;
            }
            else if (_collapsed) return NubOutRect().Contains(p);
            if (!NubSingleMode() && NubInRect().Contains(p)) return true;
            if (HitTest(p) >= 0) return true;
            if (CloseButtonRect().Contains(p)) return true;
            if (GearButtonRect().Contains(p)) return true;
            if (ShootButtonRect().Contains(p)) return true;
            if (KeyRect().Contains(p)) return true;
            // 名字药丸也要算"内容"，否则点它会直接穿透到桌面（改名点不动的根因）
            RectangleF np = NamePillRect();
            if (!np.IsEmpty)
            {
                RectangleF hit = np;
                if (_nameHover) hit = new RectangleF(np.X, np.Y, np.Width + 96f, np.Height);   // 连右边的提示一起点
                if (hit.Contains(p)) return true;
            }
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
                try { DrawWheel(g, w, h); }
                catch (Exception ex) { Err.Log("DrawWheel", ex); }      // 画错一帧总好过整个程序崩掉
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
                    try { DrawWheel(g2, w, h); }
                    catch (Exception ex) { Err.Log("DrawWheel-fallback", ex); }
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

        // 铺一层“看不见的接住区”：分层窗口是按 alpha 做命中测试的 —— alpha=0 的地方
        // 系统会当作不存在，拖到那儿鼠标消息/拖放都直接穿到底下的窗口去（这就是“拖上去没反应”的根因）。
        // alpha=1 肉眼完全看不出来，但系统会认为这里有东西，于是拖放能找上我们。
        // 范围 = 环带（含一点余量）+ 摇杆键，也就是“看上去是轮盘”的那一片。
        void DrawDropCatcher(Graphics g)
        {
            PointF c = Center();
            float R = EffR();
            float outer = R + _thumb * 1.15f;
            float inner = Math.Max(0f, R - _thumb * 1.15f);
            float st = ArcStart();
            using (GraphicsPath gp = new GraphicsPath(FillMode.Alternate))
            {
                gp.AddArc(c.X - outer, c.Y - outer, outer * 2f, outer * 2f, st, 90f);
                gp.AddLine(c.X, c.Y, c.X, c.Y);
                gp.CloseFigure();
                if (inner > 2f)
                {
                    gp.AddArc(c.X - inner, c.Y - inner, inner * 2f, inner * 2f, st, 90f);
                    gp.AddLine(c.X, c.Y, c.X, c.Y);
                    gp.CloseFigure();
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                    g.FillPath(b, gp);
            }
            Rectangle kr = KeyRect();
            using (SolidBrush kb = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                g.FillEllipse(kb, kr);
        }

        // 万能键：新拟态玻璃圆盘 —— 玻璃底 + 上亮下暗 + 主题色核心，按下时核心点亮并轻微放大
        void DrawKeyDisc(Graphics g, int a, Color acc, Rectangle kr, float kcx, float kcy, float krr)
        {
            float kt = Gfx.Clamp01(_keyT);
            float hv = Gfx.Clamp01(_keyHov);
            // 悬停时轻微放大 + 高光变亮；按下时再强一点
            float sc = 1f + 0.045f * hv + 0.05f * kt;
            float rr = krr * sc;
            RectangleF disc = new RectangleF(kcx - rr, kcy - rr, rr * 2f, rr * 2f);
            bool neu = StyleNeu();
            if (hv > 0.01f) a = Math.Min(255, (int)(a * (1f + 0.12f * hv)));

            // 1) 外发光：悬停时明显变亮变大
            if (neu && _settings.ShadowPercent > 8)
            {
                using (GraphicsPath gp = new GraphicsPath())
                {
                    float ho = rr + 12f + 10f * kt + 12f * hv;
                    gp.AddEllipse(kcx - ho, kcy - ho, ho * 2f, ho * 2f);
                    using (PathGradientBrush halo = new PathGradientBrush(gp))
                    {
                        halo.CenterPoint = new PointF(kcx, kcy);
                        halo.CenterColor = Gfx.A(acc, (int)((48 + 90 * kt + 70 * hv) * a / 255f));
                        halo.SurroundColors = new Color[] { Gfx.A(acc, 0) };
                        g.FillPath(halo, gp);
                    }
                }
            }

            // 2) 玻璃盘身
            using (GraphicsPath body = new GraphicsPath())
            {
                body.AddEllipse(disc);
                BackdropClip(g, body, a);
                Gfx.GlassPanel(g, body, disc,
                    Gfx.A(GlassBase(), GlassA((int)((178 + 28 * kt) * a / 255f))),
                    (int)((neu ? 46 : 22) * a / 255f),
                    (int)((neu ? 52 : 0) * a / 255f),
                    !StyleFlatOnly());
                // 3) 主题色内芯（按下的进度决定点亮的程度）
                float cr = rr * (0.52f + 0.06f * kt);
                using (GraphicsPath core = new GraphicsPath())
                {
                    core.AddEllipse(kcx - cr, kcy - cr, cr * 2f, cr * 2f);
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                        new RectangleF(kcx - cr, kcy - cr - 1f, cr * 2f, cr * 2f + 2f),
                        Gfx.A(Gfx.Shade(acc, 0.22f + 0.10f * hv), (int)(((StyleFlatOnly() ? 200 : 118) + 60 * kt + 55 * hv) * a / 255f)),
                        Gfx.A(Gfx.Shade(acc, -0.28f), (int)(((StyleFlatOnly() ? 170 : 88) + 62 * kt + 50 * hv) * a / 255f)),
                        LinearGradientMode.Vertical))
                        g.FillPath(lg, core);
                    using (Pen cp = new Pen(Gfx.A(Gfx.Shade(acc, 0.35f), (int)((110 + 90 * kt + 60 * hv) * a / 255f)), 1.2f))
                        g.DrawPath(cp, core);
                }
            }

            // 4) 摇杆点：扁平的小白点，按下时稍微散开
            float dsz = 6.2f + 1.4f * kt;
            float spread = 15f + 2f * kt;
            using (SolidBrush db = new SolidBrush(Color.FromArgb((int)((238 + 17 * hv) * a / 255f), 255, 255, 255)))
            {
                for (int q = 0; q < 4; q++)
                {
                    double th = -Math.PI / 2 + q * Math.PI / 2;
                    float px = (float)(kcx + Math.Cos(th) * spread), py = (float)(kcy + Math.Sin(th) * spread);
                    g.FillEllipse(db, px - dsz / 2f, py - dsz / 2f, dsz, dsz);
                }
                float cs = 7.4f + 2.2f * kt;
                g.FillEllipse(db, kcx - cs / 2f, kcy - cs / 2f, cs, cs);
            }
        }

        void DrawWheel(Graphics g, int w, int h)
        {
            int a = (int)(255 * Math.Max(0f, Math.Min(1f, _show)));
            if (a <= 1) return;
            // 统一缩放：后面所有绘制都按逻辑坐标来，字体/图标/间距自动跟着 DPI 走
            if (Math.Abs(UiK - 1f) > 0.001f) g.ScaleTransform(UiK, UiK);
            PointF c = Center();

            DrawDropCatcher(g);

            if (_collapsed)              // 收起态：只留边上那个小把手
            {
                DrawNubs(g, a);
                return;
            }

            // ring track (quarter of the ring that lies inside the screen)
            float rr = EffR();
            float st = ArcStart();
            // 开启动画：环像彩虹一样从一端扫出来
            float sweepP = IntroP(0f);
            float sweep = 90f * (0.02f + 0.98f * Gfx.EaseInOut(sweepP));
            float ringA = (int)(a * Math.Min(1f, 0.35f + 0.65f * sweepP));
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddArc(c.X - rr, c.Y - rr, rr * 2f, rr * 2f, st, sweep);
                if (_dropActive)
                {
                    // drop-target feedback: blue glow + bright blue track（外部文件=偏绿，自己的图=偏蓝）
                    Color dc = _dropExternal ? Color.FromArgb(86, 214, 138) : Color.FromArgb(96, 170, 255);
                    Color dch = Color.FromArgb(255, Math.Min(255, dc.R + 24), Math.Min(255, dc.G + 24), Math.Min(255, dc.B + 24));
                    using (Pen dg = new Pen(Color.FromArgb((int)(110 * ringA / 255f), dc.R, dc.G, dc.B), 40f))
                    { dg.StartCap = LineCap.Round; dg.EndCap = LineCap.Round; g.DrawPath(dg, gp); }
                    using (Pen dm = new Pen(Color.FromArgb((int)(235 * ringA / 255f), dch.R, dch.G, dch.B), 6f))
                    { dm.StartCap = LineCap.Round; dm.EndCap = LineCap.Round; g.DrawPath(dm, gp); }
                }
                // 环：扁平化处理 —— 一条细亮线为主，新拟态风格再垫一层柔和的光晕
                if (StyleNeu() && _settings.ShadowPercent > 8)
                    using (Pen glow = new Pen(Gfx.A(_accentCur, (int)(34 * ringA / 255f)), 22f))
                    { glow.StartCap = LineCap.Round; glow.EndCap = LineCap.Round; g.DrawPath(glow, gp); }
                using (Pen mid = new Pen(Color.FromArgb((int)((StyleFlatOnly() ? 78 : 92) * ringA / 255f), 255, 255, 255), 2.2f))
                { mid.StartCap = LineCap.Round; mid.EndCap = LineCap.Round; g.DrawPath(mid, gp); }
                using (Pen hair = new Pen(Color.FromArgb((int)((StyleFlatOnly() ? 210 : 235) * ringA / 255f), 255, 255, 255), 1.3f))
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
                if (sweep < (e2 == 0 ? 1f : 89f)) continue;         // 扫到哪儿亮到哪儿
                double ang = (st + e2 * 90) * Math.PI / 180.0;
                float ex = (float)(c.X + rr * Math.Cos(ang));
                float ey = (float)(c.Y + rr * Math.Sin(ang));
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(170 * ringA / 255f), 255, 255, 255)))
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
                    if (!_collapsed) pr *= CollapseCardP();          // 收起时图片先淡出、沿弧退回角落
                    if (pr <= 0.001f) continue;
                    float slide = (_intro || _collapsing) ? 0.62f : 0.30f;   // 开启动画时排得更开，像排队滑下来
                    float phi = ItemPhi(i) + (1f - pr) * slide;      // slide along the arc
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
                    float rad = CardRadOf(rr2);
                    float shOff = (float)Math.Round(Math.Max(2f, rr2.Height * 0.04f));

                    // 阴影：新拟态用柔和的漫射阴影，纯扁平就一层淡淡的投影
                    int shA = ShadowA(95);
                    if (shA > 2)
                    {
                        if (StyleFlatOnly())
                        {
                            using (GraphicsPath sh = Gfx.Round(new RectangleF(rr2.X, rr2.Y + shOff, rr2.Width, rr2.Height), rad))
                            using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(shA * ia / 255f / 2.2f), 0, 0, 0)))
                                g.FillPath(sb, sh);
                        }
                        else
                        {
                            using (GraphicsPath sh = Gfx.Round(new RectangleF(rr2.X + 1f, rr2.Y + shOff * 1.4f, rr2.Width, rr2.Height), rad))
                                Gfx.SoftShadow(g, sh, (int)(shA * ia / 255f / 2.4f), 2.6f);
                        }
                    }

                    using (GraphicsPath card = Gfx.Round(rr2, rad))
                    {
                        // 毛玻璃底（真背景）+ 新拟态的上下明暗边
                        BackdropClip(g, card, ia);
                        int baseA = GlassA(188);
                        Color fill = Gfx.A(GlassBase(), (int)(baseA * ia / 255f));
                        Gfx.GlassPanel(g, card, rr2, fill,
                            (int)((StyleNeu() ? 34 : 16) * ia / 255f),
                            (int)((StyleNeu() ? 40 : 0) * ia / 255f),
                            !StyleFlatOnly());
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

            // 按下反馈：缩小一点 + 描边更亮，让"按下去"看得见
            Rectangle cbr = Shrink(CloseButtonRect(), _closeDown);
            Color acc = _accentCur;
            float pb0 = IntroP(0.30f), pb1 = IntroP(0.40f), pb2 = IntroP(0.50f);
            PointF sh0 = IntroShift(pb0), sh1 = IntroShift(pb1), sh2 = IntroShift(pb2);
            int ab0 = (int)(a * pb0), ab1 = (int)(a * pb1), ab2 = (int)(a * pb2);

            if (pb0 > 0.01f)
            {
                System.Drawing.Drawing2D.Matrix m0 = g.Transform;
                g.TranslateTransform(sh0.X, sh0.Y);
                Rectangle cbr0 = cbr;
                using (GraphicsPath cbp2 = new GraphicsPath()) { cbp2.AddEllipse(cbr0); BackdropClip(g, cbp2, ab0); cbp2.Dispose(); }
                Color closeAcc = _closeLong ? Color.FromArgb(232, 72, 62) : acc;   // 长按变红 = 松手会退出
                int closeFill = _closeLong ? 236 : (_closeHover ? 206 : 172);
                Gfx.NeuCircle(g, cbr0, Gfx.A(GlassBase(), GlassA((int)(closeFill * ab0 / 255f))),
                    Gfx.A(closeAcc, (int)(200 * ab0 / 255f)), false, false,
                    (int)((StyleNeu() ? 60 : 24) * ab0 / 255f), (int)((StyleNeu() ? 60 : 0) * ab0 / 255f));
                using (Pen cbp = new Pen(Color.FromArgb((int)((238 + 17 * _closeDown) * ab0 / 255f), 255, 255, 255), 1.8f + 1.4f * _closeDown + 0.8f * (_closeLong ? 1f : 0f)))
                {
                    float pad = 10 + 2f * _closeDown;
                    g.DrawLine(cbp, cbr0.Left + pad, cbr0.Top + pad, cbr0.Right - pad, cbr0.Bottom - pad);
                    g.DrawLine(cbp, cbr0.Right - pad, cbr0.Top + pad, cbr0.Left + pad, cbr0.Bottom - pad);
                }
                g.Transform = m0;
            }

            // gear (settings) button
            if (pb1 > 0.01f)
            {
                System.Drawing.Drawing2D.Matrix m1 = g.Transform;
                g.TranslateTransform(sh1.X, sh1.Y);
                Rectangle gbr = Shrink(GearButtonRect(), _gearDown);
                using (GraphicsPath gbp2 = new GraphicsPath()) { gbp2.AddEllipse(gbr); BackdropClip(g, gbp2, ab1); gbp2.Dispose(); }
                Gfx.NeuCircle(g, gbr, Gfx.A(GlassBase(), GlassA((int)((_gearHover ? 206 : 172) * ab1 / 255f))),
                    Gfx.A(acc, (int)(200 * ab1 / 255f)), false, false,
                    (int)((StyleNeu() ? 60 : 24) * ab1 / 255f), (int)((StyleNeu() ? 60 : 0) * ab1 / 255f));
                float gcx = gbr.X + gbr.Width / 2f, gcy = gbr.Y + gbr.Height / 2f;
                float gro = gbr.Width * 0.28f;
                using (Pen gp2 = new Pen(Color.FromArgb((int)(238 * ab1 / 255f), 255, 255, 255), 1.8f))
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
                g.Transform = m1;
            }

            // 万能键（弧线内侧中点的摇杆式大圆盘）
            // NO_KEY 变体里 KeyRect() 是空的，这里必须直接跳过，否则会拿 0 尺寸去建画刷
            Rectangle kr = KeyRect();
            float kcx = kr.X + kr.Width / 2f, kcy = kr.Y + kr.Height / 2f;
            float krr = kr.Width / 2f;
            float pk = IntroP(0.16f);
            PointF sk = IntroShift(pk);
            bool hasKey = kr.Width > 8 && kr.Height > 8;
            if (hasKey && pk > 0.01f) { g.TranslateTransform(sk.X, sk.Y); }

            if (_delConfirm && hasKey)
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
                    string t3 = "删除「" + FitName(_mgr.ActiveWheel.Name, 12) + "」？点左半取消 / 右半确认";
                    SizeF s3 = g.MeasureString(t3, f3);
                    g.DrawString(t3, f3, b3, kcx - s3.Width / 2f, kr.Y - s3.Height - 4);
                }
            }
            else if (hasKey)
            {
            // 万能键：玻璃盘 + 主题色内芯（新拟态 + 扁平 + 毛玻璃）
            DrawKeyDisc(g, (int)(a * pk), acc, kr, kcx, kcy, krr);
            }
            if (hasKey && pk > 0.01f) g.TranslateTransform(-sk.X, -sk.Y);   // 恢复，别影响后面的元素

            // 当前 wheel 名：药丸底 + 主题色圆点（可在设置里关掉）
            float pn = IntroP(0.62f);
            if (hasKey && a > 60 && pn > 0.01f && _settings.ShowNameLabel)
            {
                PointF sn = IntroShift(pn);
                int an = (int)(a * pn);
                g.TranslateTransform(sn.X, sn.Y);
                using (Font fw = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
                {
                    string wn = FitName(_mgr.ActiveWheel.Name, 12);
                    SizeF ws = g.MeasureString(wn, fw);
                    float dot = 9f;
                    float pw2 = ws.Width + dot + 30f, ph2 = ws.Height + 8f;
                    float wx = kcx - pw2 / 2f;
                    float wy = kr.Y + kr.Height + 4f;
                    RectangleF pill2 = new RectangleF(wx, wy, pw2, ph2);
                    _namePillRect = pill2;                       // 记下来给命中测试用（点它能改名）
                    using (GraphicsPath pg2 = Gfx.Round(pill2, ph2 / 2f))
                    {
                        BackdropClip(g, pg2, an);
                        Gfx.GlassPanel(g, pg2, pill2, Gfx.A(GlassBase(), GlassA((int)((_nameHover ? 210 : 176) * an / 255f))),
                            (int)((StyleNeu() ? 40 : 18) * an / 255f), (int)((StyleNeu() ? 34 : 0) * an / 255f), !StyleFlatOnly());
                        using (Pen bp2 = new Pen(Gfx.A(Gfx.Shade(acc, 0.15f), (int)((_nameHover ? 235 : 120) * an / 255f)), _nameHover ? 1.6f : 1.1f))
                            g.DrawPath(bp2, pg2);
                    }
                    float dy2 = pill2.Y + ph2 / 2f;
                    using (SolidBrush db2 = new SolidBrush(Color.FromArgb((int)(250 * an / 255f), acc.R, acc.G, acc.B)))
                        g.FillEllipse(db2, pill2.X + 11f, dy2 - dot / 2f, dot, dot);
                    using (SolidBrush bw = new SolidBrush(Color.FromArgb((int)(245 * an / 255f), 255, 255, 255)))
                        g.DrawString(wn, fw, bw, pill2.X + 13f + dot, pill2.Y + (ph2 - ws.Height) / 2f + 1);
                    // 悬停时在右边补一句"点一下改名"
                    if (_nameHover && an > 80)
                    {
                        using (Font ft = new Font("Microsoft YaHei UI", 9f))
                        using (SolidBrush bt = new SolidBrush(Color.FromArgb((int)(220 * an / 255f), 235, 238, 245)))
                            g.DrawString("点一下改名", ft, bt, pill2.Right + 8f, dy2 - ft.Height / 2f + 1);
                    }
                }
                g.TranslateTransform(-sn.X, -sn.Y);
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
                        // 玻璃扇区 + 选中时主题色点亮（新拟态：外圈加一道高光）
                        BackdropClip(g, gp2, alpha);
                        Color scFill = sel ? Gfx.A(Gfx.Shade(acc, 0.05f), (int)(alpha * 0.92f))
                                           : (s2 == 2 ? Color.FromArgb((int)(alpha * 0.72f), 150, 46, 58)
                                                      : Gfx.A(GlassBase(), (int)(alpha * 0.86f)));
                        using (SolidBrush sb2 = new SolidBrush(scFill))
                            g.FillPath(sb2, gp2);
                        using (Pen sp2 = new Pen(Color.FromArgb((int)(alpha * (sel ? 0.75f : 0.42f)), 255, 255, 255), 1.2f))
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
            Rectangle sbr = Shrink(ShootButtonRect(), _shootDown);
            if (pb2 > 0.01f)
            {
                g.TranslateTransform(sh2.X, sh2.Y);
                using (GraphicsPath sbp2 = new GraphicsPath()) { sbp2.AddEllipse(sbr); BackdropClip(g, sbp2, ab2); sbp2.Dispose(); }
                using (SolidBrush sbbs = new SolidBrush(Color.FromArgb((int)((_shootHover ? 240 : 190) * ab2 / 255f), 0, 122, 204)))
                    g.FillEllipse(sbbs, sbr);
                using (Pen sp = new Pen(Color.FromArgb((int)(245 * ab2 / 255f), 255, 255, 255), 1.8f))
                {
                    float cx2 = sbr.X + sbr.Width / 2f, cy2 = sbr.Y + sbr.Height / 2f;
                    g.DrawRectangle(sp, cx2 - 8f, cy2 - 5f, 16f, 11f);
                    g.DrawEllipse(sp, cx2 - 3.4f, cy2 - 2.6f, 6.8f, 6.8f);
                    g.DrawLine(sp, cx2 - 4f, cy2 - 8f, cx2 + 4f, cy2 - 8f);
                }
                g.TranslateTransform(-sh2.X, -sh2.Y);
            }

            if (_store.Items.Count > 0 && _settings.ShowCountLabel)
            {
                int cur = (int)Math.Round(_offset) + 1;
                if (cur < 1) cur = 1;
                if (cur > _store.Items.Count) cur = _store.Items.Count;
                string idx = cur + " / " + _store.Items.Count;
                float fs = Math.Max(9f, Math.Min(40f, _settings.LabelSize));
                float pc2 = IntroP(0.72f);
                if (pc2 > 0.01f)
                {
                PointF sc2 = IntroShift(pc2);
                int ac2 = (int)(a * pc2);
                g.TranslateTransform(sc2.X, sc2.Y);
                using (Font f = new Font("Microsoft YaHei UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    SizeF sz = g.MeasureString(idx, f);
                    float ip = sz.Height * 0.72f;                    // 前置的小圆点
                    PointF tp = HintPos(new SizeF(sz.Width + ip + 8f, sz.Height));
                    RectangleF pill = new RectangleF(tp.X - 9f, tp.Y - 3f, sz.Width + ip + 26f, sz.Height + 6f);
                    using (GraphicsPath pg = Gfx.Round(pill, pill.Height / 2f))
                    {
                        BackdropClip(g, pg, ac2);
                        Gfx.GlassPanel(g, pg, pill, Gfx.A(GlassBase(), GlassA((int)(176 * ac2 / 255f))),
                            (int)((StyleNeu() ? 38 : 16) * ac2 / 255f), (int)((StyleNeu() ? 32 : 0) * ac2 / 255f), !StyleFlatOnly());
                        using (Pen pp2 = new Pen(Gfx.A(Gfx.Shade(acc, 0.15f), (int)(110 * ac2 / 255f)), 1.1f))
                            g.DrawPath(pp2, pg);
                    }
                    float dotY = pill.Y + pill.Height / 2f;
                    using (SolidBrush db = new SolidBrush(Color.FromArgb((int)(245 * ac2 / 255f), acc.R, acc.G, acc.B)))
                        g.FillEllipse(db, pill.X + 10f, dotY - ip / 2f, ip, ip);
                    using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(246 * ac2 / 255f), 255, 255, 255)))
                        g.DrawString(idx, f, br, pill.X + 12f + ip, pill.Y + (pill.Height - sz.Height) / 2f + 1);
                }
                g.TranslateTransform(-sc2.X, -sc2.Y);
                }
            }

            // 外部文件拖到轮盘上方：提示松手加入
            if (_dropActive && _dropExternal && a > 90)
            {
                string tip = "松手把 " + _dropCount + " 张图片加入「" + FitName(_mgr.ActiveWheel.Name, 12) + "」";
                using (Font f = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
                {
                    SizeF sz = g.MeasureString(tip, f);
                    float px = c.X + Sx() * (EffR() * 0.78f) - sz.Width / 2f;
                    float py = c.Y + Sy() * (EffR() * 0.78f) - sz.Height / 2f;
                    RectangleF pill = new RectangleF(px - 14, py - 7, sz.Width + 28, sz.Height + 14);
                    using (GraphicsPath pg = Gfx.Round(pill, pill.Height / 2f))
                    {
                        using (SolidBrush pb = new SolidBrush(Color.FromArgb((int)(235 * a / 255f), 34, 120, 86)))
                            g.FillPath(pb, pg);
                        using (Pen pp2 = new Pen(Color.FromArgb((int)(220 * a / 255f), 150, 245, 190), 1.6f))
                            g.DrawPath(pp2, pg);
                    }
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb((int)(250 * a / 255f), 255, 255, 255)))
                        g.DrawString(tip, f, tb, pill.X + 14, pill.Y + 6);
                }
            }

            DrawToast(g, a);

            DrawNubs(g, a);      // 展开状态下也画一个"收起"把手（贴着另一条屏幕边）
        }

        // ---- 贴边小把手：收起态画"拉出"、展开态画"收起" ----
        // 两个把手按环的进度交叉淡入淡出（并各自从屏幕边滑出来），不会"啪"地换一个
        void DrawNubs(Graphics g, int a)
        {
            float k = _collapsed ? 0f : (_intro ? _introT : 1f);    // 0=完全收起，1=完全展开
            if (NubSingleMode())
            {
                DrawNubOne(g, a, true, 1f);                          // 只有一个把手，始终可见
                return;
            }
            if (k < 0.995f) DrawNubOne(g, a, true, 1f - k);
            if (k > 0.005f) DrawNubOne(g, a, false, k);
        }

        void DrawNubOne(Graphics g, int a, bool outMode, float vis)
        {
            if (vis <= 0.004f) return;
            RectangleF r = outMode ? NubOutRect() : NubInRect();
            bool hov = vis > 0.98f && (outMode ? _nubOutHover : _nubInHover);
            float k = vis > 0.98f ? _nubHov : 0f;
            Color acc = _accentCur;
            int alpha = (int)(a * vis * vis * (0.62f + 0.38f * k));   // vis 平方：淡出更干脆
            // 出场/退场时贴着屏幕边滑一下（像从边里抽出来）
            float slide = (1f - vis) * 16f;

            // 悬停时稍微长一点、厚一点，像"被拉出来一点"
            float grow = 10f * k;
            bool vertical = r.Height > r.Width;
            RectangleF rr = vertical
                ? new RectangleF(r.X, r.Y - grow / 2f, r.Width, r.Height + grow)
                : new RectangleF(r.X - grow / 2f, r.Y, r.Width + grow, r.Height);
            if (vertical) rr = new RectangleF(rr.X - (Sx() > 0 ? slide : -slide), rr.Y, rr.Width, rr.Height);
            else rr = new RectangleF(rr.X, rr.Y + (Sy() > 0 ? slide : -slide), rr.Width, rr.Height);

            using (GraphicsPath p = Gfx.Round(rr, Math.Min(rr.Width, rr.Height) / 2f))
            {
                BackdropClip(g, p, alpha);
                using (SolidBrush b = new SolidBrush(Gfx.A(GlassBase(), (int)((hov ? 214 : 178) * alpha / 255f))))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Gfx.A(acc, (int)((hov ? 210 : 130) * alpha / 255f)), 1.4f))
                    g.DrawPath(pen, p);
            }

            // 三个小点（抓手感）+ 一个指向"将要动的方向"的小三角
            float cx = rr.X + rr.Width / 2f, cy = rr.Y + rr.Height / 2f;
            using (SolidBrush db = new SolidBrush(Gfx.A(acc, (int)((hov ? 255 : 200) * alpha / 255f))))
            {
                float ds = 3.4f + 1.0f * k;
                float gap = 9f;
                for (int i = -1; i <= 1; i++)
                {
                    if (vertical) g.FillEllipse(db, cx - ds / 2f, cy + i * gap - ds / 2f, ds, ds);
                    else g.FillEllipse(db, cx + i * gap - ds / 2f, cy - ds / 2f, ds, ds);
                }
            }
            // 方向提示三角：拉出 = 指向屏幕里；收起 = 指向贴着的那条屏幕边
            //  竖着的把手（在竖直的屏幕边上）：箭头朝 左右
            //  横着的把手（在水平的屏幕边上）：箭头朝 上下
            // 箭头指向"点一下会发生什么"：拉出->朝屏幕里；收起->朝屏幕边外
            bool willExpand = outMode;
            if (NubSingleMode()) willExpand = _collapsed;
            float ax2 = 0f, ay2 = 0f;
            if (vertical) ax2 = willExpand ? Sx() : -Sx();
            else ay2 = willExpand ? Sy() : -Sy();
            // 三角放在"远离角落"的那一端旁边，避开中间的三个点
            float along = NubLong * 0.30f;
            float px0 = vertical ? cx : cx + along * (Sx() > 0 ? 1f : -1f);
            float py0 = vertical ? cy + along * (Sy() > 0 ? 1f : -1f) : cy;
            using (GraphicsPath ar = new GraphicsPath())
            {
                float s2 = 4.8f + 1.4f * k;
                if (vertical)
                {
                    ar.AddPolygon(new PointF[] {
                        new PointF(px0 - ax2 * s2 * 0.55f, py0 - s2 * 0.85f),
                        new PointF(px0 - ax2 * s2 * 0.55f, py0 + s2 * 0.85f),
                        new PointF(px0 + ax2 * s2 * 0.75f, py0)
                    });
                }
                else
                {
                    ar.AddPolygon(new PointF[] {
                        new PointF(px0 - s2 * 0.85f, py0 - ay2 * s2 * 0.55f),
                        new PointF(px0 + s2 * 0.85f, py0 - ay2 * s2 * 0.55f),
                        new PointF(px0, py0 + ay2 * s2 * 0.75f)
                    });
                }
                using (SolidBrush ab3 = new SolidBrush(Gfx.A(acc, (int)((hov ? 255 : 215) * alpha / 255f))))
                    g.FillPath(ab3, ar);
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
        bool _dropExternal = false;      // 拖进来的是“外面的文件”（不是轮盘自己的图）
        int _dropCount = 0;
        object _dropCacheKey = null;
        List<string> _dropCacheFiles = null;
        DateTime _dropCacheAt = DateTime.MinValue;
        const string DragFmt = "SnapWheelMove";     // 标记：这是轮盘自己在拖的图

        // 一次拖拽里 DragOver 会疯狂触发，扫文件夹太浪费：同一次拖拽只算一次，
        // 就算 DataObject 每次都换了新壳，也至少 400ms 才重算一次
        List<string> DropCandidates(IDataObject data)
        {
            if (data == null) return null;
            if (ReferenceEquals(data, _dropCacheKey)) return _dropCacheFiles;
            if (_dropCacheFiles != null && (DateTime.Now - _dropCacheAt).TotalMilliseconds < 400) return _dropCacheFiles;
            List<string> r = null;
            try
            {
                if (!data.GetDataPresent(DragFmt) && data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] paths = data.GetData(DataFormats.FileDrop) as string[];
                    r = ImageIO.Collect(paths, 50);
                }
            }
            catch { }
            _dropCacheKey = data; _dropCacheFiles = r; _dropCacheAt = DateTime.Now;
            return r;
        }

        void ClearDropCache() { _dropCacheKey = null; _dropCacheFiles = null; _dropCacheAt = DateTime.MinValue; }

        // 拖进来的到底是啥：文件（含文件夹）还是直接一张位图（网页/其它程序里拖出来的图）
        static bool HasBitmapData(IDataObject data)
        {
            if (data == null) return false;
            try { return data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib); }
            catch { return false; }
        }

        // 外部图片：整个窗口范围内都接收（不再要求必须正好压在图/环上 —— 太严格会让人以为坏了）
        void OnDragOverWheel(object sender, DragEventArgs e)
        {
            bool mine = false, ext = false; int n = 0;
            try { mine = e.Data != null && e.Data.GetDataPresent(DragFmt); } catch { }
            if (!mine)
            {
                List<string> fs = DropCandidates(e.Data);
                if (fs != null && fs.Count > 0) { ext = true; n = fs.Count; }
                else if (HasBitmapData(e.Data)) { ext = true; n = 1; }
            }
            if (mine)
            {
                bool over = OverContent(ToLogicalPt(PointToClient(new Point(e.X, e.Y))));
                e.Effect = over ? DragDropEffects.Move : DragDropEffects.None;
                if (over != _dropActive || _dropExternal || _dropCount != 0)
                { _dropActive = over; _dropExternal = false; _dropCount = 0; Render(); }
            }
            else
            {
                e.Effect = ext ? DragDropEffects.Copy : DragDropEffects.None;
                if (!_dropActive || !_dropExternal || n != _dropCount)
                { _dropActive = ext; _dropExternal = ext; _dropCount = n; Render(); }
            }
        }

        void OnDragDropWheel(object sender, DragEventArgs e)
        {
            _dropActive = false; _dropExternal = false;
            bool mine = false;
            try { mine = e.Data != null && e.Data.GetDataPresent(DragFmt); } catch { }
            if (mine)
            {
                bool over = OverContent(ToLogicalPt(PointToClient(new Point(e.X, e.Y))));
                if (over) { _returnedToWheel = true; e.Effect = DragDropEffects.Move; }
                else e.Effect = DragDropEffects.None;
            }
            else
            {
                List<string> fs = DropCandidates(e.Data);
                if (fs != null && fs.Count > 0) { e.Effect = DragDropEffects.Copy; ImportFiles(fs); }
                else if (HasBitmapData(e.Data))
                {
                    e.Effect = DragDropEffects.Copy;
                    ImportBitmap(e.Data);
                }
                else e.Effect = DragDropEffects.None;
            }
            ClearDropCache();
            Render();
        }

        // 直接拖过来的一张位图（不是文件）：网页、看图软件、聊天窗口里拖出来的图都走这里
        public void ImportBitmap(IDataObject data)
        {
            Bitmap bmp = null;
            try
            {
                object o = null;
                try { o = data.GetData(DataFormats.Bitmap); } catch { }
                if (o == null) { try { o = data.GetData(DataFormats.Dib); } catch { } }
                if (o is Bitmap) bmp = new Bitmap((Bitmap)o);
                else if (o is Image) bmp = new Bitmap((Image)o);
                else if (o is Stream)
                {
                    using (Stream s = (Stream)o)
                    {
                        long pos = 0; try { pos = s.Position; } catch { }
                        try { using (Image im = Image.FromStream(s)) bmp = new Bitmap(im); }
                        catch { try { s.Position = pos; using (Image im2 = Image.FromStream(s)) bmp = new Bitmap(im2); } catch { } }
                    }
                }
                else if (o is byte[])
                {
                    byte[] raw = (byte[])o;
                    try { using (MemoryStream ms = new MemoryStream(raw)) using (Image im = Image.FromStream(ms)) bmp = new Bitmap(im); }
                    catch { }
                }
            }
            catch { bmp = null; }
            if (bmp == null) { ShowToast("这张图读不出来"); Render(); return; }
            bmp = ImageIO.Fit(bmp, ImageIO.MaxDim);
            StoreItem it = _store.AddCore(bmp, ImageIO.ExtFor(bmp));
            _enterT0[it] = DateTime.Now;
            _targetOffset = Math.Max(0, _store.Items.Count - 1);
            _hover = -1; _enlarged = -1;
            ShowToast("已加入 1 张图片");
            Render();
        }

        // 剪贴板里出现图片就自动收进轮盘（可关）；和自己写的剪贴板做区分，并做去重
        void OnClipboardChanged()
        {
            if (!_settings.ClipboardImport) return;
            if ((DateTime.Now - _selfClipboardAt).TotalSeconds < 1.5) return;   // 我们自己刚写的，跳过
            if (_dragOutItem != null) return;                                   // 正在拖出，别掺和
            Bitmap copy = null;
            string fp = "";
            try
            {
                if (!Clipboard.ContainsImage()) return;
                using (Image im = Clipboard.GetImage())
                {
                    if (im == null || im.Width < 2 || im.Height < 2) return;
                    fp = Fingerprint(im);
                    if (fp == _lastClipFp) return;                              // 同一张图，不重复收
                    copy = new Bitmap(im);
                }
            }
            catch { return; }                                                   // 剪贴板被别人占着，忽略这次
            if (copy == null) return;
            try
            {
                copy = ImageIO.Fit(copy, ImageIO.MaxDim);
                StoreItem it = _store.AddCore(copy, ImageIO.ExtFor(copy));
                _lastClipFp = fp;
                if (it != null)
                {
                    _enterT0[it] = DateTime.Now;
                    _targetOffset = Math.Max(0, _store.Items.Count - 1);
                    _hover = -1; _enlarged = -1;
                    if (_collapsed && _settings.ShowBalloon) Err.Notify("已从剪贴板收进 1 张图");
                    else ShowToast("已从剪贴板收进 1 张图");
                    if (Visible) Render();
                }
            }
            catch { }
        }

        // 便宜的指纹：尺寸 + 采样若干点，用来判断"是不是同一张图"
        static string Fingerprint(Image im)
        {
            try
            {
                using (Bitmap b = new Bitmap(im, new Size(Math.Min(16, im.Width), Math.Min(16, im.Height))))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(im.Width).Append('x').Append(im.Height).Append(':' );
                    for (int y = 0; y < b.Height; y += 3)
                        for (int x = 0; x < b.Width; x += 3)
                            sb.Append(b.GetPixel(x, y).ToArgb().ToString("X8"));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        // 把外部图片收进当前 wheel（失败的单张跳过，不打断其它）
        public void ImportFiles(List<string> files)
        {
            if (files == null || files.Count == 0) return;
            int ok = 0, bad = 0;
            for (int i = 0; i < files.Count; i++)
            {
                StoreItem it = null;
                try { it = _store.Import(files[i]); } catch { }
                if (it == null) { bad++; continue; }
                _enterT0[it] = DateTime.Now.AddSeconds(ok * 0.07);    // 依次滑入
                ok++;
            }
            _targetOffset = Math.Max(0, _store.Items.Count - 1);      // 视口跟到最后一张
            _hover = -1; _enlarged = -1;
            if (ok > 0) ShowToast("已加入 " + ok + " 张图片" + (bad > 0 ? "（" + bad + " 张读不了）" : ""));
            else ShowToast("这些文件读不出图片");
            Render();
        }

        // 左下角的小提示条
        string _toast = "";
        DateTime _toastAt = DateTime.MinValue;
        public void ShowToast(string s)
        {
            _toast = s == null ? "" : s;
            _toastAt = DateTime.Now;
        }

        void DrawToast(Graphics g, int a)
        {
            if (_toast.Length == 0) return;
            float age = (float)(DateTime.Now - _toastAt).TotalSeconds;
            if (age > 2.6f) return;
            float t = 1f;
            if (age < 0.18f) t = age / 0.18f;
            else if (age > 2.1f) t = Math.Max(0f, (2.6f - age) / 0.5f);
            int ta = (int)(235 * t * a / 255f);
            if (ta <= 2) return;
            using (Font f = new Font("Microsoft YaHei UI", 10f))
            {
                SizeF sz = g.MeasureString(_toast, f);
                float w = sz.Width + 34f, h = sz.Height + 16f;
                SizeF ls2 = LogicalSize();
                float x = 26f + (1f - t) * 14f, y = ls2.Height - h - 26f;
                using (GraphicsPath pp = Gfx.Round(new RectangleF(x, y, w, h), h / 2f))
                {
                    BackdropClip(g, pp, ta);
                    Gfx.GlassPanel(g, pp, new RectangleF(x, y, w, h), Gfx.A(GlassBase(), GlassA((int)(ta * 0.86f))),
                        (int)(ta * 0.16f), (int)(ta * 0.14f), !StyleFlatOnly());
                    using (Pen p = new Pen(Gfx.A(Gfx.Shade(_accentCur, 0.15f), (int)(ta * 0.42f)), 1.2f))
                        g.DrawPath(p, pp);
                }
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(ta, 255, 255, 255)))
                    g.DrawString(_toast, f, tb, x + 17f, y + 8f);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _lastActive = DateTime.Now;
            e = LogicalArgs(e);

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
            e = LogicalArgs(e);          // 鼠标物理坐标 -> 逻辑坐标（缩放后命中测试才对得上）

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
            // 点 Wheel 名药丸 -> 直接改名
            if (e.Button == MouseButtons.Left && NamePillRect().Contains(e.Location))
            {
                RenameWheel();
                return;
            }
            if (e.Button == MouseButtons.Left && ShootButtonRect().Contains(e.Location))
            {
                _shootPend = true; _shootHold = true; _pendingBtn = "shoot"; _pendingAt = DateTime.Now;
                Render();
                return;
            }
            if (e.Button == MouseButtons.Left && GearButtonRect().Contains(e.Location))
            {
                _gearPend = true; _gearHold = true; _pendingBtn = "gear"; _pendingAt = DateTime.Now;
                Render();
                return;
            }
            // 贴边把手：拉出 / 收起（动画中也能点，随时可掉头）
            if (e.Button == MouseButtons.Left && NubOutRect().Contains(e.Location))
            {
                if (_collapsed)
                {
                    if (CanExpandByNub()) ExpandWheel();
                }
                else if (NubSingleMode()) CollapseWheel();   // 单把手模式：同一个把手负责收起
                return;
            }
            if (!NubSingleMode() && e.Button == MouseButtons.Left && !_collapsed && NubInRect().Contains(e.Location))
            { CollapseWheel(); return; }

            if (e.Button == MouseButtons.Left && CloseButtonRect().Contains(e.Location))
            {
                // 短按 = 关掉轮盘；长按（0.65s）= 变红，松手直接退出 SnapWheel
                _closeHold = true;
                _closeDownAt = DateTime.Now;
                _closeLong = false;
                _pendingBtn = "";                 // 不走那个 110ms 延迟
                Render();
                return;
            }
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
            e = LogicalArgs(e);
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
            if (_closeHold)
            {
                bool wasLong = _closeLong;
                _closeHold = false; _closeLong = false;
                if (wasLong) { ShowToast("正在退出 SnapWheel…"); Render(); if (ExitRequested != null) ExitRequested(this, EventArgs.Empty); return; }
                HideWheel();          // 短按：直接关掉轮盘（不是收起）
                return;
            }
            _gearHold = _shootHold = false;
            _maybeDrag = false; _dragIndex = -1; _holdIndex = -1;
            if (_enlarged >= 0) { _enlarged = -1; Render(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            e = LogicalArgs(e);
            int hh = HitTest(e.Location);
            if (hh >= 0 && _store.Items[hh].Image != null)
            {
                _selfClipboardAt = DateTime.Now;          // 标记一下，别把它当成"用户复制的新图"又收一遍
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
            try { data.SetData(DragFmt, 1); } catch { }        // 标记成“轮盘自己的拖拽”，别当成外部导入

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
                // 拖回轮盘：和刚截完图一样，重新播一次缩略图滑入动画
                _scales.Remove(_store.Items.IndexOf(it));
                MarkNew(it);
                _targetOffset = Math.Max(0, _store.Items.Count - 1);
                ShowToast("已放回「" + _mgr.ActiveWheel.Name + "」");
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
            if (disposing)
            {
                try { ReleaseDib(); } catch { }
                try { if (IsHandleCreated) Native.RemoveClipboardFormatListener(Handle); } catch { }
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ShowMsg) { ShowWheelWithIntro(); return; }   // second launch asks us to show
            if (m.Msg == 0x031D) { OnClipboardChanged(); return; }      // WM_CLIPBOARDUPDATE：剪贴板有新图
            if (m.Msg == 0x02E0 && _settings.UiScale <= 0)              // WM_DPICHANGED：系统缩放变了，重排一次
            { try { ApplyLayout(); } catch { } }
            if (m.Msg == 0x0084)
            {
                Point cpRaw = PointToClient(Cursor.Position);
                // 空地方点穿（不误点）；但左键按着的时候不做穿透 ——
                // 那多半正在拖拽，穿透会让系统找不到拖放目标，图就掉不进来了
                bool dragging = (Control.MouseButtons & MouseButtons.Left) != 0;
                bool inWin = cpRaw.X >= 0 && cpRaw.Y >= 0 && cpRaw.X < Width && cpRaw.Y < Height;
                if (!dragging && inWin && !OverContent(ToLogicalPt(cpRaw)))
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Blur.Supported)
            {
                BackColor = Color.FromArgb(240, 247, 248, 251);
                try { Blur.Apply(Handle, 232, 246, 248, 252, true); } catch { }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);
        }

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
        TableLayoutPanel _root;
        // 应用真·毛玻璃：窗口背景半透明 + 系统 acrylic 模糊；系统不支持就退回不透明浅底
        void ApplyGlass()
        {
            // 对话框用干净的浅色实底：半透明窗体 + 子控件（按钮/输入框）在 Windows 上
            // 容易出现"四角没画到、重绘才恢复"的脏块，实测得不偿失。
            // 真正需要毛玻璃的地方是轮盘本体，那边是自己绘制的，好控制。
            BackColor = Color.FromArgb(248, 249, 252);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyGlass();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 半透明底：让 acrylic 透出来
            if (Blur.Supported)
            {
                using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
                return;
            }
            base.OnPaintBackground(e);
        }


        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);          // 补一次整窗重绘，按钮四角不会闪白块
        }

        public SettingsForm(Settings s)
        {
            Text = AppInfo.Name + " 设置  ·  BETA";
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
            root.ColumnCount = 2;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.Dock = DockStyle.Fill;
            root.Margin = new Padding(0);
            _root = root;
            Controls.Add(root);

            // 左右两栏：内容多也不会把窗口顶出屏幕（原来一列排下来 970px 高）
            TableLayoutPanel colL = new TableLayoutPanel();
            colL.ColumnCount = 1;
            colL.AutoSize = true;
            colL.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            colL.Margin = new Padding(0, 0, 34, 0);
            TableLayoutPanel colR = new TableLayoutPanel();
            colR.ColumnCount = 1;
            colR.AutoSize = true;
            colR.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            colR.Margin = new Padding(0);

            Label head = new Label();
            head.AutoSize = true;
            head.Text = AppInfo.Name + " 设置";
            head.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(32, 34, 38);
            head.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(head);
            root.SetColumnSpan(head, 2);
            root.Controls.Add(colL, 0, 1);
            root.Controls.Add(colR, 1, 1);

            CheckBox chkDisk = new CheckBox();
            chkDisk.AutoSize = true;
            chkDisk.Text = "保存到硬盘（否则只存内存，退出即清）";
            chkDisk.Checked = s.SaveToDisk;
            chkDisk.Margin = new Padding(0, 4, 0, 4);
            colL.Controls.Add(Section("行为"));
            colL.Controls.Add(chkDisk);

            CheckBox chkAutoStart = new CheckBox();
            chkAutoStart.AutoSize = true;
            chkAutoStart.Text = "开机自动启动（登录后自动在后台运行）";
            chkAutoStart.Checked = AutoRun.IsEnabled();
            chkAutoStart.Margin = new Padding(0, 4, 0, 4);
            colL.Controls.Add(chkAutoStart);

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
            colL.Controls.Add(Row(MkLabel("保存目录"), txtDir, browse));

            colL.Controls.Add(Section("外观"));
            NumericUpDown numMax = Num(1, 999, s.MaxCount);
            NumericUpDown numThumb = Num(40, 260, s.ThumbSize);
            colL.Controls.Add(Row(MkLabel("最多保留张数"), numMax, Gap(24), MkLabel("缩略图大小"), numThumb));

            NumericUpDown numRad = Num(120, 700, s.Radius);
            NumericUpDown numSlots = Num(2, 12, s.Slots);
            NumericUpDown numLabel = Num(9, 40, s.LabelSize);
            colL.Controls.Add(Row(MkLabel("环半径"), numRad, Gap(24), MkLabel("弧上张数"), numSlots, Gap(24), MkLabel("序号字号"), numLabel));

            // 分辨率 / DPI 适配
            ComboBox cmbScale = new ComboBox();
            cmbScale.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbScale.FlatStyle = FlatStyle.Flat;
            cmbScale.Width = 170;
            cmbScale.Margin = new Padding(0, 6, 0, 0);
            int[] scVals = { 0, 80, 90, 100, 110, 125, 150, 175, 200, 250 };
            cmbScale.Items.Add("自动（按显示器 DPI）");
            for (int i = 1; i < scVals.Length; i++) cmbScale.Items.Add(scVals[i] + "%");
            cmbScale.SelectedIndex = 0;
            for (int i = 0; i < scVals.Length; i++) if (scVals[i] == s.UiScale) cmbScale.SelectedIndex = i;
            Label hint = new Label();
            hint.AutoSize = true;
            hint.Text = "（整块轮盘等比放大，含文字和图标）";
            hint.ForeColor = Color.FromArgb(150, 152, 160);
            hint.Margin = new Padding(0, 10, 0, 0);
            colL.Controls.Add(Row(MkLabel("界面缩放"), cmbScale, Gap(12), hint));

            NumericUpDown numPeek = Num(120, 500, s.PeekPercent);

            // 收起态：像贴边小球一样，缩到屏幕边上留个小把手
            CheckBox chkCollapse = new CheckBox();
            chkCollapse.AutoSize = true;
            chkCollapse.Text = "收起状态：缩到屏幕边上留个小把手";
            chkCollapse.Checked = s.CollapseMode;
            colL.Controls.Add(Row(chkCollapse));

            // 收起 / 展开的动画速度（独立于上面的"动画速度"）
            ComboBox cmbRing = new ComboBox();
            cmbRing.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbRing.FlatStyle = FlatStyle.Flat;
            cmbRing.Width = 130;
            cmbRing.Margin = new Padding(0, 6, 0, 0);
            int[] ringVals = { 220, 150, 100, 80, 60, 45 };
            string[] ringNames = { "极快", "快", "标准", "慢", "很慢", "最慢" };
            for (int i = 0; i < ringNames.Length; i++) cmbRing.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            cmbRing.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.ExpandSpeed) cmbRing.SelectedIndex = i;
            Label hintRing = new Label();
            hintRing.AutoSize = true;
            hintRing.Text = "（只管收起 / 展开；百分比越大越快）";
            hintRing.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing.Margin = new Padding(0, 10, 0, 0);
            colL.Controls.Add(Row(MkLabel("展开速度"), cmbRing, Gap(10), hintRing));

            ComboBox cmbRing2 = new ComboBox();
            cmbRing2.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbRing2.FlatStyle = FlatStyle.Flat;
            cmbRing2.Width = 130;
            cmbRing2.Margin = new Padding(0, 6, 0, 0);
            for (int i = 0; i < ringNames.Length; i++) cmbRing2.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            cmbRing2.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.CollapseSpeed) cmbRing2.SelectedIndex = i;
            Label hintRing2 = new Label();
            hintRing2.AutoSize = true;
            hintRing2.Text = "（默认比展开快一档，收起要干脆）";
            hintRing2.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing2.Margin = new Padding(0, 10, 0, 0);
            colL.Controls.Add(Row(MkLabel("收起速度"), cmbRing2, Gap(10), hintRing2));

            CheckBox chkSingle = new CheckBox();
            chkSingle.AutoSize = true;
            chkSingle.Text = "只用一个把手：左边那个点一下展开、再点一下收起（任务栏自动隐藏时更省事）";
            chkSingle.Checked = s.NubSingle;
            colL.Controls.Add(Row(chkSingle));

            CheckBox chkBalloon = new CheckBox();
            chkBalloon.AutoSize = true;
            chkBalloon.Text = "显示托盘气泡提示（关掉就不再弹右下角通知）";
            chkBalloon.Checked = s.ShowBalloon;
            colL.Controls.Add(Row(chkBalloon));

            // 剪贴板自动收纳 + 玻璃底定时刷新
            CheckBox chkClip = new CheckBox();
            chkClip.AutoSize = true;
            chkClip.Text = "复制图片后自动收进轮盘";
            chkClip.Checked = s.ClipboardImport;
            CheckBox chkGlassRefresh = new CheckBox();
            chkGlassRefresh.AutoSize = true;
            chkGlassRefresh.Text = "毛玻璃定时刷新（轮盘挂久了背景也是新的）";
            chkGlassRefresh.Checked = s.GlassRefresh;
            colL.Controls.Add(Row(chkClip));
            colL.Controls.Add(Row(chkGlassRefresh));
            CheckBox chkIntroAnim = new CheckBox();
            chkIntroAnim.AutoSize = true;
            chkIntroAnim.Text = "启动时播放开启动画";
            chkIntroAnim.Checked = s.IntroAnim;
            chkIntroAnim.Margin = new Padding(0, 10, 0, 0);
            colL.Controls.Add(Row(MkLabel("长按放大(%)"), numPeek, Gap(24), chkIntroAnim));

            // ---------------- 风格（新拟态 + 扁平化 + 毛玻璃）----------------
            colR.Controls.Add(Section("风格"));

            ComboBox cmbStyle = new ComboBox();
            cmbStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbStyle.FlatStyle = FlatStyle.Flat;
            cmbStyle.Width = 170;
            cmbStyle.Margin = new Padding(0, 6, 0, 0);
            cmbStyle.Items.AddRange(new object[] { "新拟态 + 毛玻璃", "纯扁平", "高对比（不透明）" });
            cmbStyle.SelectedIndex = (s.UiStyle == "flat") ? 1 : (s.UiStyle == "solid" ? 2 : 0);

            ComboBox cmbAccent = new ComboBox();
            cmbAccent.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbAccent.FlatStyle = FlatStyle.Flat;
            cmbAccent.Width = 170;
            cmbAccent.Margin = new Padding(0, 6, 0, 0);
            cmbAccent.Items.Add("跟随 Wheel 颜色");
            for (int i = 0; i < Palette.Names.Length; i++) cmbAccent.Items.Add("统一：" + Palette.Names[i]);
            cmbAccent.SelectedIndex = (s.AccentIndex >= 0 && s.AccentIndex < Palette.Names.Length) ? s.AccentIndex + 1 : 0;
            colR.Controls.Add(Row(MkLabel("界面风格"), cmbStyle, Gap(24), MkLabel("主题色"), cmbAccent));

            NumericUpDown numGlass = Num(20, 100, s.GlassPercent);
            NumericUpDown numRadius = Num(0, 30, s.CardRadius);
            NumericUpDown numShadow = Num(0, 100, s.ShadowPercent);
            colR.Controls.Add(Row(MkLabel("玻璃不透明度"), numGlass, Gap(16), MkLabel("圆角(%)"), numRadius,
                                  Gap(16), MkLabel("阴影强度"), numShadow));

            ComboBox cmbAnim = new ComboBox();
            cmbAnim.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbAnim.FlatStyle = FlatStyle.Flat;
            cmbAnim.Width = 170;
            cmbAnim.Margin = new Padding(0, 6, 0, 0);
            cmbAnim.Items.AddRange(new object[] { "慢", "标准", "快" });
            cmbAnim.SelectedIndex = (s.AnimSpeed <= 85) ? 0 : (s.AnimSpeed >= 120 ? 2 : 1);

            CheckBox chkName = new CheckBox();
            chkName.AutoSize = true;
            chkName.Text = "显示名称标签";
            chkName.Checked = s.ShowNameLabel;
            chkName.Margin = new Padding(0, 10, 0, 0);
            CheckBox chkCount = new CheckBox();
            chkCount.AutoSize = true;
            chkCount.Text = "显示计数标签";
            chkCount.Checked = s.ShowCountLabel;
            chkCount.Margin = new Padding(20, 10, 0, 0);
            colR.Controls.Add(Row(MkLabel("动画速度"), cmbAnim, Gap(24), chkName, chkCount));

            CheckBox chkAuto = new CheckBox();
            chkAuto.AutoSize = true;
            chkAuto.Text = "空闲后自动收起轮盘";
            chkAuto.Checked = s.AutoHide;
            chkAuto.Margin = new Padding(0, 4, 0, 4);
            NumericUpDown numSec = Num(2, 600, s.AutoHideSeconds);
            colL.Controls.Add(Row(chkAuto, Gap(16), MkLabel("空闲秒数"), numSec));

            CheckBox chkTop = new CheckBox();
            chkTop.AutoSize = true;
            chkTop.Text = "总在最前（始终置顶显示）";
            chkTop.Checked = s.AlwaysOnTop;
            chkTop.Margin = new Padding(0, 4, 0, 4);
            colL.Controls.Add(chkTop);

            ComboBox cmb = new ComboBox();
            cmb.DropDownStyle = ComboBoxStyle.DropDownList;
            cmb.Width = 170;
            cmb.Margin = new Padding(0, 6, 0, 0);
            cmb.Items.AddRange(HotkeyUtil.Names);
            cmb.SelectedItem = s.Hotkey;
            if (cmb.SelectedIndex < 0) cmb.SelectedIndex = 0;
            colR.Controls.Add(Row(MkLabel("截图热键"), cmb));

            ComboBox cmbCorner = new ComboBox();
            cmbCorner.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCorner.Width = 170;
            cmbCorner.Margin = new Padding(0, 6, 0, 0);
            cmbCorner.Items.AddRange(new object[] { "左下角", "右下角", "左上角", "右上角" });
            cmbCorner.SelectedIndex = CornerIndex(s.Corner);
            colR.Controls.Add(Row(MkLabel("圆环位置"), cmbCorner));

            ComboBox cmbDel = new ComboBox();
            cmbDel.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDel.Width = 170;
            cmbDel.Margin = new Padding(0, 6, 0, 0);
            cmbDel.Items.AddRange(new object[] { "双击右键删除", "单击右键删除" });
            cmbDel.SelectedIndex = (s.DeleteMode == "single") ? 1 : 0;
            colR.Controls.Add(Row(MkLabel("删除方式"), cmbDel));

            ComboBox cmbSwitch = new ComboBox();
            cmbSwitch.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSwitch.Width = 170;
            cmbSwitch.Margin = new Padding(0, 6, 0, 0);
            cmbSwitch.Items.AddRange(new object[] { "长按万能键弹圆盘", "长按后左右滑动" });
            cmbSwitch.SelectedIndex = (s.SwitchMode == "swipe") ? 1 : 0;
            colR.Controls.Add(Row(MkLabel("Wheel 切换"), cmbSwitch));

            RoundButton ok = new RoundButton();
            ok.Text = "确定";
            ok.Size = new Size(104, 36);
            ok.Fill = Color.FromArgb(0, 122, 204);
            ok.FillHover = Color.FromArgb(0, 140, 232);
            ok.TextColor = Color.White;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            ok.Margin = new Padding(10, 0, 0, 0);
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
                s.PeekPercent = (int)numPeek.Value;
                s.UiScale = scVals[cmbScale.SelectedIndex < 0 ? 0 : cmbScale.SelectedIndex];
                s.CollapseMode = chkCollapse.Checked;
                s.ClipboardImport = chkClip.Checked;
                s.GlassRefresh = chkGlassRefresh.Checked;
                s.ExpandSpeed = ringVals[cmbRing.SelectedIndex < 0 ? 2 : cmbRing.SelectedIndex];
                s.CollapseSpeed = ringVals[cmbRing2.SelectedIndex < 0 ? 2 : cmbRing2.SelectedIndex];
                s.NubSingle = chkSingle.Checked;
                s.ShowBalloon = chkBalloon.Checked;
                s.IntroAnim = chkIntroAnim.Checked;
                s.UiStyle = (cmbStyle.SelectedIndex == 1) ? "flat" : (cmbStyle.SelectedIndex == 2 ? "solid" : "neu");
                s.AccentIndex = cmbAccent.SelectedIndex - 1;
                s.GlassPercent = (int)numGlass.Value;
                s.CardRadius = (int)numRadius.Value;
                s.ShadowPercent = (int)numShadow.Value;
                s.AnimSpeed = (cmbAnim.SelectedIndex == 0) ? 70 : (cmbAnim.SelectedIndex == 2 ? 140 : 100);
                s.ShowNameLabel = chkName.Checked;
                s.ShowCountLabel = chkCount.Checked;
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
            cancel.Margin = new Padding(10, 0, 0, 0);
            cancel.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });

            RoundButton guide = new RoundButton();
            guide.Text = "新手引导";
            guide.Size = new Size(104, 36);
            guide.Fill = Color.FromArgb(236, 240, 246);
            guide.FillHover = Color.FromArgb(226, 233, 243);
            guide.TextColor = Color.FromArgb(40, 90, 150);
            guide.Font = new Font("Microsoft YaHei UI", 10f);
            guide.Margin = new Padding(0, 0, 0, 0);
            guide.Click += new EventHandler(delegate(object o, EventArgs e2)
            { GuideForm gf = new GuideForm(); gf.ShowDialog(this); });

            // 还原默认设置：只重置设置项，不动你的图片和 Wheel 内容
            RoundButton reset = new RoundButton();
            reset.Text = "还原默认";
            reset.Size = new Size(104, 36);
            reset.Fill = Color.FromArgb(252, 238, 236);
            reset.FillHover = Color.FromArgb(248, 224, 220);
            reset.TextColor = Color.FromArgb(178, 66, 52);
            reset.Font = new Font("Microsoft YaHei UI", 10f);
            reset.Margin = new Padding(10, 0, 0, 0);
            reset.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                DialogResult r2 = MessageBox.Show(this,
                    "把所有设置恢复成默认值？\r\n\r\n（不会动你的图片和 Wheel 内容，只重置外观/行为等设置项）",
                    "还原默认设置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r2 != DialogResult.OK) return;
                Settings def = new Settings();
                Settings.CopyInto(def, s);
                AutoRun.Apply(s.AutoStart);
                s.Save();
                DialogResult = DialogResult.OK;
                Close();
            });

            // 按钮行：用四列表格把确定/取消靠右对齐。
            // 原来是一个 250px 的假占位控件硬顶 + FlowLayoutPanel，窗口 AutoSize 一算就错位/被裁
            TableLayoutPanel btnRow = new TableLayoutPanel();
            btnRow.ColumnCount = 5;
            btnRow.RowCount = 1;
            btnRow.AutoSize = true;
            btnRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            btnRow.Dock = DockStyle.Fill;
            btnRow.Margin = new Padding(0, 14, 0, 0);
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.Controls.Add(guide, 0, 0);
            btnRow.Controls.Add(reset, 1, 0);
            btnRow.Controls.Add(new Panel(), 2, 0);
            btnRow.Controls.Add(ok, 3, 0);
            btnRow.Controls.Add(cancel, 4, 0);
            btnRow.AutoSize = false;
            btnRow.Height = 42;
            btnRow.Dock = DockStyle.Fill;
            root.Controls.Add(btnRow);
            root.SetColumnSpan(btnRow, 2);

            Label about = new Label();
            about.AutoSize = true;
            about.Text = AppInfo.Name + "   v" + AppInfo.Version + "   ·   by " + AppInfo.Author + "   ·   BETA";
            about.ForeColor = Color.FromArgb(150, 150, 160);
            about.Margin = new Padding(0, 16, 0, 0);
            root.Controls.Add(about);

            // 屏幕矮的时候别把窗口顶出屏幕（两栏布局后一般用不到，保险起见留个上限）
            try
            {
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                MaximumSize = new Size((int)(wa.Width * 0.95), (int)(wa.Height * 0.94));
            }
            catch { }
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
            f.Margin = new Padding(0, 5, 0, 5);   // 行距：设置项变多了，压紧一点免得窗口太高
            for (int i = 0; i < cs.Length; i++) f.Controls.Add(cs[i]);
            return f;
        }
    }

    // 点 Wheel 名药丸弹出来的小改名框
    class RenameForm : Form
    {
        public const int MaxName = 12;      // 名字最长 12 个字（药丸宽度可控）
        public string Value = "";
        TextBox _box;

        public RenameForm(string cur)
        {
            Text = "给这个 Wheel 起个名";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(250, 250, 252);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(360, 128);

            Label l = new Label();
            l.Text = "名字（最多 " + MaxName + " 个字，会显示在轮盘上）";
            l.ForeColor = Color.FromArgb(110, 114, 124);
            l.AutoSize = true;
            l.Location = new Point(22, 18);
            Controls.Add(l);

            _box = new TextBox();
            _box.Text = cur;
            _box.Font = new Font("Microsoft YaHei UI", 11f);
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.Location = new Point(24, 44);
            _box.Width = 250;
            _box.MaxLength = MaxName;      // 直接限制输入长度，避免打到超长
            Controls.Add(_box);

            RoundButton ok = new RoundButton();
            ok.Text = "改好了";
            ok.Size = new Size(104, 34);
            ok.Fill = Color.FromArgb(0, 122, 204);
            ok.FillHover = Color.FromArgb(0, 140, 232);
            ok.TextColor = Color.White;
            ok.Primary = true;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            ok.Location = new Point(ClientSize.Width - 24 - 104, ClientSize.Height - 46);
            ok.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                Value = _box.Text.Trim();
                if (Value == null || Value.Length == 0) Value = cur;
                if (Value.Length > MaxName) Value = Value.Substring(0, MaxName);   // 名字太长会把药丸撑宽、挡住旁边
                DialogResult = DialogResult.OK;
                Close();
            });
            Controls.Add(ok);

            RoundButton cancel = new RoundButton();
            cancel.Text = "取消";
            cancel.Size = new Size(90, 34);
            cancel.Fill = Color.FromArgb(234, 235, 240);
            cancel.FillHover = Color.FromArgb(222, 224, 230);
            cancel.TextColor = Color.FromArgb(58, 60, 66);
            cancel.Font = new Font("Microsoft YaHei UI", 10f);
            cancel.Location = new Point(ok.Left - 10 - 90, ok.Top);
            cancel.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);
            _box.Focus();
            _box.SelectAll();
        }
    }

    // 新手引导：第一次打开时自动出现一次，之后可以从托盘/设置里再叫出来
    class GuideForm : Form
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Blur.Supported)
            {
                BackColor = Color.FromArgb(240, 247, 248, 251);
                try { Blur.Apply(Handle, 232, 246, 248, 252, true); } catch { }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);
        }

        public GuideForm()
        {
            Text = AppInfo.Name + " 新手上路";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(540, 100);         // 先占位，最后按内容重算
            SuspendLayout();

            Label head = new Label();
            head.Text = "欢迎用 SnapWheel 快照轮环";
            head.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.AutoSize = true;
            head.Location = new Point(28, 24);
            Controls.Add(head);

            Label sub = new Label();
            sub.Text = "轮盘平时就待在屏幕角落里，用鼠标滚轮就能翻图。";
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.AutoSize = true;
            sub.Location = new Point(31, 60);
            Controls.Add(sub);

            int y = 100;
            AddTip(28, ref y, "截图", "按 " + Settings.Load().Hotkey + " 拖框选区域，四角缩放、拖旋转键转角度，双击/回车确认。");
            AddTip(28, ref y, "存图与翻页", "截完的图会顺着弧线滑进轮盘；鼠标放在环上滚滚轮就能翻。");
            AddTip(28, ref y, "拖出去 / 拖回来", "缩略图拖到微信或文件夹就用上了；从桌面把图片拖到环带上松手就收回来。");
            AddTip(28, ref y, "长按看大图", "缩略图按住约 0.3 秒放大预览，放大倍数在设置里可调。");
            AddTip(28, ref y, "万能键", "长按环内侧那个圆盘：上=新建 Wheel，右=下一个，下=删除，左=上一个；也可以改成左右滑动切。");
            AddTip(28, ref y, "收起 / 拉出", "轮盘收起后只在屏幕边上留一个小把手：点它就用彩虹动画把轮盘拉出来；展开时另一条边上有个「收起」把手。设置里可以关掉这个模式。");
            AddTip(28, ref y, "剪贴板收纳", "任何地方「复制」一张图（截图工具、网页右键、微信里都行）都会自动滑进轮盘，不用手动拖。不想要可以在设置里关掉。");
            AddTip(28, ref y, "工具箱", "托盘右键还有：导入图片、新手引导、重播开启动画、设置。");

            Label tip = new Label();
            tip.Text = "小提示：如果拖图片拖不进去，检查是不是用「以管理员身份运行」启动的（Windows 会拦掉跨权限的拖拽）。";
            tip.ForeColor = Color.FromArgb(168, 122, 36);
            tip.AutoSize = false;
            tip.Size = new Size(486, 42);
            tip.Location = new Point(31, y + 4);
            Controls.Add(tip);
            y += 52;

            RoundButton go = new RoundButton();
            go.Text = "开始使用";
            go.Size = new Size(124, 38);
            go.Fill = Color.FromArgb(0, 122, 204);
            go.FillHover = Color.FromArgb(0, 140, 232);
            go.TextColor = Color.White;
            go.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            go.Location = new Point(540 - 28 - 124, y + 12);
            go.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Controls.Add(go);
            AcceptButton = go;

            ClientSize = new Size(540, y + 12 + 38 + 24);
            ResumeLayout();
        }

        void AddTip(int x, ref int y, string title, string body)
        {
            Label t = new Label();
            t.Text = "• " + title;
            t.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            t.ForeColor = Color.FromArgb(0, 110, 190);
            t.AutoSize = true;
            t.Location = new Point(x + 3, y);
            Controls.Add(t);

            Label b = new Label();
            b.Text = body;
            b.ForeColor = Color.FromArgb(70, 74, 84);
            b.AutoSize = false;
            b.Size = new Size(486, 20);
            b.Location = new Point(x + 16, y + 21);
            Controls.Add(b);
            y += 51;
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
            _wheel.ExitRequested += new EventHandler(delegate(object o, EventArgs e2) { Application.Exit(); });

            _tray = new NotifyIcon();
            _tray.Icon = Brand.Get();
            _tray.Text = "SnapWheel 快照轮环";
            _tray.Visible = true;
            Err.Notify = delegate(string msg)             // 出问题时托盘冒个泡，程序继续跑
            {
                if (!_settings.ShowBalloon) return;        // 设置里可以关掉右下角通知
                try { _tray.ShowBalloonTip(4000, "SnapWheel 遇到一个问题（已记录）", msg, ToolTipIcon.Warning); }
                catch { }
            };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("截图", null, new EventHandler(OnHotkey));
            menu.Items.Add("导入图片…", null, new EventHandler(OnImport));
            menu.Items.Add("新手引导", null, new EventHandler(OnGuide));
            menu.Items.Add("重播开启动画", null, new EventHandler(delegate(object o, EventArgs e) { _wheel.StartIntro(); }));
            if (IsElevated())
                menu.Items.Add("以普通权限重启（拖拽才有用）", null, new EventHandler(OnRelaunchNormal));
            menu.Items.Add("显示/隐藏轮盘", null, new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); }));
            menu.Items.Add("管理 Wheel…", null, new EventHandler(OnWheels));
            menu.Items.Add("设置…", null, new EventHandler(OnSettings));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, new EventHandler(delegate(object o, EventArgs e) { Quit(); }));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); });

            RegisterHotkeyAndNotify();

            if (IsElevated() && _settings.ShowBalloon)
                try
                {
                    _tray.ShowBalloonTip(6000, "SnapWheel 以管理员身份运行",
                        "Windows 会拦掉管理员进程和桌面/资源管理器之间的拖拽。想在轮盘上拖进拖出图片，请用普通权限运行（右键托盘图标 → 以普通权限重启）。",
                        ToolTipIcon.Warning);
                }
                catch { }

            if (_settings.ShowWheelOnStart)
            {
                // 开了收起态：开机就只贴边待着（像贴边小球），点一下才拉出来
                if (_settings.CollapseMode) _wheel.StartCollapsed();
                else _wheel.ShowWheelWithIntro();
            }
            else if (_settings.CollapseMode)
            {
                _wheel.StartCollapsed();     // 就算开机不显示轮盘，也留个贴边把手，否则没法鼠标叫出来
            }

            // 第一次打开：先播开启动画，再弹一次新手引导（看过就不再弹）
            if (!_settings.IntroSeen)
            {
                _settings.IntroSeen = true;
                _settings.Save();
                Timer g = new Timer();
                g.Interval = 900;
                g.Tick += new EventHandler(delegate(object o, EventArgs e2)
                {
                    g.Stop(); g.Dispose();
                    try { GuideForm gf = new GuideForm(); gf.ShowDialog(); } catch { }
                });
                g.Start();
            }
        }

        void OnGuide(object sender, EventArgs e)
        {
            try { GuideForm gf = new GuideForm(); gf.ShowDialog(); } catch { }
        }

        // 是不是以管理员身份在跑？管理员进程收不到（也发不出）普通权限程序的拖拽 —— Windows 的 UIPI 拦的
        static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // 用 explorer 拉起自己 -> 拿到普通权限（explorer 是 Medium）
        void OnRelaunchNormal(object sender, EventArgs e)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Application.ExecutablePath + "\""); }
            catch { }
            Quit();
        }

        // 托盘「导入图片…」：不想拖的时候也能从任意位置选图加进当前 wheel
        void OnImport(object sender, EventArgs e)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "把图片加入轮盘";
                d.Multiselect = true;
                d.Filter = ImageIO.DialogFilter();
                d.RestoreDirectory = true;
                if (d.ShowDialog() != DialogResult.OK) return;
                List<string> files = ImageIO.Collect(d.FileNames, 50);
                _wheel.ShowWheel();
                _wheel.ImportFiles(files);
            }
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
                _tray.Text = "SnapWheel 快照轮环 (" + _settings.Hotkey + ")";
                if (_settings.ShowBalloon || !ok) _tray.ShowBalloonTip(3000, "SnapWheel", tip, ToolTipIcon.Info);
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
                // 收起态被关掉时，别让轮盘卡在"只剩个把手"的状态里
                if (!_settings.CollapseMode && _wheel.IsCollapsed) { _wheel.ExpandWheel(); }
                else if (!_settings.CollapseMode) { _wheel.HideWheel(); }
            }
            else
            {
                _wheel.TopMost = wasTop;
            }
        }

        void OnHotkey(object sender, EventArgs e) { CaptureRegion(); }

        void CaptureRegion()
        {
            bool wasExpanded = _wheel.Visible && _wheel.IsExpanded;
            if (wasExpanded && _settings.CollapseMode)
            {
                // 先播收起动画，收完了再弹截图浮层（有过程感，也不挡浮层）
                _wheel.CollapseWheel(true);
                for (int i = 0; i < 90 && !_wheel.IsCollapsed; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(8); }
            }
            else if (_wheel.Visible) _wheel.Hide();   // don't let the topmost wheel sit over the capture overlay

            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap shot = new Bitmap(vs.Width, vs.Height);
            try
            {
                using (Graphics g = Graphics.FromImage(shot))
                    g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
            }
            catch { shot.Dispose(); if (wasExpanded) _wheel.ExpandWheel(); return; }

            OverlayForm ov = new OverlayForm(vs, shot);
            ov.ShowDialog();
            if (ov.Result != null)
            {
                Store st = _wheels.ActiveStore;
                StoreItem ni = st.Add(ov.Result);
                _wheel.MarkNew(ni);              // only the brand-new shot plays the slide-in
                // 截完播拉出动画（收起态拉出来最自然；原来是展开的就直接显示）
                if (_settings.CollapseMode) _wheel.ExpandWheel(true);   // 截图流程：拉出也快一点
                else _wheel.ShowWheel();
            }
            else if (wasExpanded)
            {
                _wheel.ExpandWheel(true);        // 取消了截图，也把轮盘拉回来
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
            try { Native.SetDpiAwarenessBest(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 全局兜底：任何没被接住的异常都只记日志（+偶尔提醒一次），绝不再让「.NET Framework 未处理异常」把程序打死
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += new System.Threading.ThreadExceptionEventHandler(delegate(object o, System.Threading.ThreadExceptionEventArgs ea)
            {
                Err.Log("UI线程", ea.Exception);
            });
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(delegate(object o, UnhandledExceptionEventArgs ea)
            {
                Err.Log("非UI线程", ea.ExceptionObject as Exception);
            });

            Application.Run(new AppCtx());
            GC.KeepAlive(mtx);
        }
    }
}
