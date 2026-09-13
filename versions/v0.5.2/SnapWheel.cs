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
using System.Net;
using System.Threading;

namespace SnapWheel
{
    static class AppInfo
    {
#if NO_KEY
        public const string Version = "0.2.22";   // 变体：多 Wheel + 框选缩放/锁定（无万能键）★ 0.2 线最终版
#else
        public const string Version = "0.5.2";   // 完整版：动画性能大优化 + 撤销删除 + 工具条摆放
#endif
        public const string Author = "exper7";
        public const string Name = "SnapWheel";
        public const string CnName = "快照轮环";        // 正式中文名（0.4.7 起）
        public const string Repo = "ExpertKT/SnapWheel";  // 自动更新检查用
    }

    static class Elev
    {
        static readonly bool _on = Detect();

        // 测试用：强制指定是不是管理员（null = 按真实权限判断）。
        // 不然"管理员模式下会怎样"这段逻辑永远只能在管理员进程里手测。
        public static bool? ForceForTest = null;

        public static bool Is { get { return ForceForTest.HasValue ? ForceForTest.Value : _on; } }

        static bool Detect()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // 用 explorer 拉起自己 → 拿到普通权限（explorer 是 Medium 完整性级别）。
        // 只管启动，退出当前实例由调用方决定（否则弹框还挂在一个正在退出的进程上）。
        public static bool RelaunchNormal()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Application.ExecutablePath + "\""); return true; }
            catch { return false; }
        }
    }
}

namespace SnapWheel
{
    static class Err
    {
        static readonly object _lock = new object();
        static DateTime _last = DateTime.MinValue;

        // 日志上限：超了就转存成 error.log.1（只留一代，上一代直接删）。
        // 之前是只增不减 —— [Frame] 慢帧诊断每 10 秒就可能写一行，挂久了日志能涨到几 MB，
        // 真出问题时反而不好翻。512KB 足够装下最近几百条，翻的时候一眼看到头。
        public static long MaxBytes = 512 * 1024;

        // 测试用：把日志指到临时文件（null = 正常的 %APPDATA%\SnapWheel\error.log）。
        // 否则跑一次 -Test，[Frame] 这些诊断行会混进用户真实日志里，
        // 以后分析"慢半拍"时分不清哪些是测试造出来的。
        public static string OverridePath = null;

        public static string LogPath()
        {
            if (OverridePath != null) return OverridePath;
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapWheel");
            try { Directory.CreateDirectory(d); } catch { }
            return Path.Combine(d, "error.log");
        }

        // 超过上限就把当前日志挪成 .1（新的一代从空文件重新开始）
        static void RotateIfNeeded(string path)
        {
            try
            {
                if (MaxBytes <= 0) return;
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;
                string old = path + ".1";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                File.Move(path, old);
            }
            catch { }
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
                    string p = LogPath();
                    RotateIfNeeded(p);
                    File.AppendAllText(p, s, Encoding.UTF8);
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

        // 不抛异常、不弹气泡，只往日志里记一段（给性能报告这类"不是错误"的诊断用）
        public static void Note(string where, string text)
        {
            try
            {
                lock (_lock)
                {
                    string p = LogPath();
                    RotateIfNeeded(p);
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]  " +
                        text + "\r\n\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        // 同一个地方短期内只提示一次，避免刷屏
        public static bool ShouldNotify()
        {
            DateTime now = DateTime.Now;
            if ((now - _last).TotalSeconds < 30) return false;
            _last = now;
            return true;
        }
    }

    // 分段耗时：想知道"这一帧的 20ms 花在哪"，就得把一帧拆开计时。
    // 默认关（用户机器上不该为诊断付代价）：跑基准时设环境变量 SNAPWHEEL_PERF=1 打开。
    // 用法：using (Perf.Section("环")) { ... } —— 关着时就是一个布尔判断，几乎零成本。
    static class Perf
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("SNAPWHEEL_PERF") == "1";

        static readonly object _lock = new object();
        static readonly Dictionary<string, double> _sum = new Dictionary<string, double>();
        static readonly Dictionary<string, int> _cnt = new Dictionary<string, int>();
        static readonly List<string> _order = new List<string>();

        public struct Scope : IDisposable
        {
            readonly string _name;
            readonly long _t0;
            public Scope(string name)
            {
                _name = name;
                _t0 = On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            }
            public void Dispose()
            {
                if (!On) return;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                Add(_name, (now - _t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
        }

        public static Scope Section(string name) { return new Scope(name); }

        static void Add(string n, double ms)
        {
            lock (_lock)
            {
                if (!_sum.ContainsKey(n)) { _sum[n] = 0; _cnt[n] = 0; _order.Add(n); }
                _sum[n] += ms; _cnt[n]++;
            }
        }

        public static void Reset()
        {
            lock (_lock) { _sum.Clear(); _cnt.Clear(); _order.Clear(); }
        }

        // 把每个分段平均多少次写进日志（跑完基准调一次）
        public static void Report(string title)
        {
            if (!On) return;
            lock (_lock)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("分段耗时 — ").Append(title).Append("\r\n");
                for (int i = 0; i < _order.Count; i++)
                {
                    string n = _order[i];
                    sb.Append("  ").Append(n.PadRight(14))
                      .Append((_sum[n] / _cnt[n]).ToString("0.00")).Append("ms   ×").Append(_cnt[n]).Append("\r\n");
                }
                Err.Note("Perf", sb.ToString());
            }
        }
    }

    static class FrameStats
    {
        public const double SlowMs = 25.0;
        const int MaxLines = 60;               // 一次运行最多写 60 行，避免日志失控

        static readonly object _lock = new object();
        static long _frames, _slow;
        static double _sum, _max;
        static string _maxState = "";
        static DateTime _windowStart = DateTime.Now;
        static int _lines;

        public static void Sample(double ms, string state)
        {
            try
            {
                lock (_lock)
                {
                    _frames++; _sum += ms;
                    if (ms > SlowMs)
                    {
                        _slow++;
                        if (ms > _max) { _max = ms; _maxState = state; }
                    }
                    if ((DateTime.Now - _windowStart).TotalSeconds >= 10) FlushLocked();
                }
            }
            catch { }
        }

        // 每 10 秒结算一次：这 10 秒内有慢帧才写一行
        static void FlushLocked()
        {
            if (_slow > 0 && _lines < MaxLines)
            {
                _lines++;
                try
                {
                    Err.Log("Frame", new Exception(
                        "最近10秒 " + _frames + " 帧，慢帧(>" + (int)SlowMs + "ms) " + _slow +
                        " 帧，平均 " + (_frames > 0 ? (_sum / _frames).ToString("0.0") : "0.0") + "ms，最慢 " +
                        _max.ToString("0.0") + "ms | 最慢帧状态: " + _maxState));
                }
                catch { }
            }
            _frames = 0; _slow = 0; _sum = 0; _max = 0; _maxState = ""; _windowStart = DateTime.Now;
        }
    }
}

namespace SnapWheel
{
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
}

namespace SnapWheel
{
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
}

namespace SnapWheel
{
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
}

namespace SnapWheel
{
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
}

namespace SnapWheel
{
    // 后悔药：删掉 / 清空的图，能一键撤回。
    //
    // 为什么不做"回收站"：
    //   回收站要建目录、要维护索引、要加一个管理窗口，还得考虑"留多久 / 留多少张"。
    //   而删除本来就是个手一抖的动作 —— 要的只是"刚删错，马上能找回来"。
    //   所以这里只在**工作内存**里多留几个引用（图本来就在轮盘内存里，不额外解码、不拷文件），
    //   托盘一句「撤销上一次删除」就放回去。退出程序即清空 —— 这是刻意的：
    //   需要长期保管的东西不该靠"删除"来存着，回收站目录无限长大反而是新问题。
    static class Undo
    {
        public const int MaxBatches = 8;                     // 最多记最近 8 次删除动作
        public const long MaxBytes = 96L * 1024 * 1024;      // 或者最多 96MB 像素，先到先算

        public class Shot
        {
            public Bitmap Image;
            public string FilePath;      // 原来落盘在哪（文件已经删了，这里只作记录/排错）
        }

        class Batch
        {
            public List<Shot> Shots = new List<Shot>();
            public Wheel Wheel;          // 从哪个盘删的（那个盘还在就放回它）
            public string WheelName = "";
            public bool ClearAll;        // true = 整盘清空，false = 删掉某几张
            public long Bytes;
        }

        static readonly List<Batch> _stack = new List<Batch>();

        public static bool CanUndo { get { return _stack.Count > 0; } }
        public static int BatchCount { get { return _stack.Count; } }

        public static int ItemCount
        {
            get { int n = 0; for (int i = 0; i < _stack.Count; i++) n += _stack[i].Shots.Count; return n; }
        }

        // 上一次删除大概是什么（给提示文字用），没有可撤销的返回 ""
        public static string LastDesc
        {
            get
            {
                if (_stack.Count == 0) return "";
                Batch b = _stack[_stack.Count - 1];
                string what = b.ClearAll ? "清空的 " : "删掉的 ";
                int n = b.Shots.Count;
                return what + n + " 张" + (string.IsNullOrEmpty(b.WheelName) ? "" : "（「" + b.WheelName + "」）");
            }
        }

        // 记一次删除。items 是刚被删掉的那些（图还在内存里，这里只留引用）
        public static void Push(Wheel w, IList<StoreItem> items, bool clearAll)
        {
            try
            {
                if (items == null || items.Count == 0) return;
                Batch b = new Batch();
                b.Wheel = w;
                b.ClearAll = clearAll;
                try { b.WheelName = w != null ? w.Name : ""; } catch { }
                for (int i = 0; i < items.Count; i++)
                {
                    StoreItem it = items[i];
                    if (it == null || it.Image == null) continue;
                    Shot sh = new Shot();
                    sh.Image = it.Image;
                    sh.FilePath = it.FilePath;
                    b.Shots.Add(sh);
                    try { b.Bytes += (long)it.Image.Width * it.Image.Height * 4; } catch { }
                }
                if (b.Shots.Count == 0) return;
                _stack.Add(b);
                Trim();
            }
            catch (Exception ex) { try { Err.Log("Undo.Push", ex); } catch { } }
        }

        static void Trim()
        {
            while (_stack.Count > MaxBatches) _stack.RemoveAt(0);
            long total = 0;
            for (int i = 0; i < _stack.Count; i++) total += _stack[i].Bytes;
            while (_stack.Count > 1 && total > MaxBytes)
            {
                total -= _stack[0].Bytes;
                _stack.RemoveAt(0);
            }
        }

        // 撤回上一次：把图放回轮盘（原盘还在就放回原盘），返回放回去的张数。
        public static int UndoLast(WheelManager mgr, out string wheelName)
        {
            wheelName = "";
            if (_stack.Count == 0) return 0;
            Batch b = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            try
            {
                Wheel target = b.Wheel;
                if (mgr != null && (target == null || !mgr.Wheels.Contains(target))) target = mgr.ActiveWheel;
                if (target == null) return 0;
                wheelName = target.Name;
                int n = 0;
                for (int i = 0; i < b.Shots.Count; i++)
                {
                    try
                    {
                        // Store.Add 自己会拷一份、并在开启落盘时重新存成文件（原文件在删除时已经没了）
                        target.Store.Add(b.Shots[i].Image);
                        n++;
                    }
                    catch (Exception ex) { try { Err.Log("Undo.Add", ex); } catch { } }
                }
                return n;
            }
            catch (Exception ex) { try { Err.Log("Undo.UndoLast", ex); } catch { } return 0; }
        }

        public static void Clear() { _stack.Clear(); }
    }
}

namespace SnapWheel
{
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

        // 测试用：把轮盘清单指到临时路径（null = 正常 %APPDATA%\SnapWheel）。
        // 测试跑一遍不该把用户真实的轮盘列表/图片目录冲掉。
        public static string OverrideMetaPath = null;

        static string MetaPath()
        {
            if (OverrideMetaPath != null) return OverrideMetaPath;
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
}

namespace SnapWheel
{
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
#if NO_KEY
        // 无万能键版：内圈不用留摇杆盘的地方，默认整体小一号（用户要求"按钮集中的同时缩小默认轮盘"）
        public int ThumbSize = 80;      // nominal thumbnail long side
#else
        public int ThumbSize = 96;      // nominal thumbnail long side
#endif
#if NO_KEY
        public int Radius = 250;        // ring radius from the screen corner（无万能键版：小一号）
#else
        public int Radius = 300;        // ring radius from the screen corner
#endif
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
        public bool CollapseMode = false;     // 收起态：像贴边小球一样缩到屏幕边上，留个可点的小把手（默认展开）
        public bool ClipboardImport = true;   // 剪贴板里出现图片时自动收进轮盘
        public bool GlassRefresh = true;      // 定时重抓玻璃底，避免轮盘挂久了糊的是旧桌面
        public bool ShowBalloon = true;       // 托盘气泡提示（关掉就不再弹右下角通知）
        public int ExpandSpeed = 100;         // 展开动画速度 %（越大越快；独立于整体动画速度）
        public int CollapseSpeed = 150;       // 收起动画速度 %（默认"快"一档，收起要干脆）
        public bool NubSingle = false;        // 只用一个把手：左边那个点一下展开、再点一下收起（底部不占地方）
        // 拖出时要不要同时给"文件"格式。
        // v0.4.8 曾把这里默认改成关，结果老用户拖到资源管理器 / 只吃文件的程序直接放不进去
        // （"缩略图拖出去放不了"就是这么来的）—— 现在默认开，Rev<3 的老配置会被迁移回开。
        public bool DragOutAsFile = true;
        public bool CheckUpdate = true;       // 启动时检查 GitHub 有没有新版本
        public bool NubHintDone = false;      // 把手用途提示是否已经自动展示过
        public bool UndoHintDone = false;     // 删除后"还能撤回"的首次提示是否已展示过
        public bool AnnotHintDone = false;    // 截图标注（工具条）的首次提示是否已展示过
        public bool PinHintDone = false;      // 贴图（中键）的首次提示是否已展示过
        public string GuideSeenVersion = "";  // 上一次自动弹出新手引导/更新说明时的版本号
        public bool TextBg = true;            // 标注文字默认带白底（可关，见截图工具条上的"文字底"）
        public string KeyActions = "new,next,delete,prev";  // 万能键四分区动作：上,右,下,左
        public int Rev = 0;                   // 配置版本号（用于默认值迁移）

        // 测试用：把配置文件指到临时路径（null = 正常的 %APPDATA%\SnapWheel）。
        // 有了它，测试才能做"存盘 -> 重新读回来"的往返验证，又不会覆盖用户真实的设置。
        public static string OverridePath = null;

        static string FilePath()
        {
            if (OverridePath != null) return OverridePath;
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
                        else if (k == "DragOutAsFile") s.DragOutAsFile = (v == "1");
                        else if (k == "CheckUpdate") s.CheckUpdate = (v == "1");
                        else if (k == "NubHintDone") s.NubHintDone = (v == "1");
                        else if (k == "UndoHintDone") s.UndoHintDone = (v == "1");
                        else if (k == "AnnotHintDone") s.AnnotHintDone = (v == "1");
                        else if (k == "PinHintDone") s.PinHintDone = (v == "1");
                        else if (k == "GuideSeenVersion") s.GuideSeenVersion = v;
                        else if (k == "TextBg") s.TextBg = (v == "1");
                        else if (k == "KeyActions" && v.Length > 0) s.KeyActions = v;
                        else if (k == "Rev") { int n; if (int.TryParse(v, out n)) s.Rev = n; }
                    }
                }
            }
            catch { }

            // ---- 配置迁移 ----
            // 只补一个版本标记。默认值（例如"收起态默认关"）只影响「全新安装」，
            // 绝不覆盖老用户自己的选择 —— 上一版会强制改，把明明开着收起的人给关掉了。
            //
            // 唯一的例外（Rev<3）：v0.4.8 把"拖出也带文件格式"的默认改成了关，而老配置里没这一行，
            // 于是升级后拖到资源管理器/桌面/某些 App 全部放不进去。这不是用户的"选择"，
            // 是默认值改动的副作用，所以这里强制恢复成开。
            bool migrated = false;
            if (s.Rev < 3) { s.DragOutAsFile = true; migrated = true; }
            s.Rev = 3;
            // 迁移必须立刻落盘：不然只改了内存里的值，配置文件还是旧的（下次启动又会"迁移"一遍，
            // 而且设置界面显示的还是旧值）。用户报的拖拽 bug 就是靠这条迁移修好的。
            if (migrated) { try { s.Save(); } catch { } }

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

        // ---------- 万能键四个分区能绑的动作 ----------
        // 顺序 = 分区顺序：0=上 1=右 2=下 3=左（和 SectorAt 一致）
        public static readonly string[] KeyActionIds =
        {
            "new", "next", "delete", "prev", "shot", "collapse", "folder", "settings", "paste", "clear", "none"
        };

        public static string KeyActionName(string id)
        {
            switch (id)
            {
                case "new": return "新建轮盘";
                case "next": return "下一个轮盘";
                case "prev": return "上一个轮盘";
                case "delete": return "删除当前轮盘";
                case "shot": return "截图";
                case "collapse": return "收起轮盘";
                case "folder": return "打开保存文件夹";
                case "settings": return "打开设置";
                case "paste": return "从剪贴板收一张";
                case "clear": return "清空这一盘（保留轮盘）";
                default: return "不设置";
            }
        }

        // 取某个分区的动作 id；配置损坏时回落到出厂默认
        public string KeyActionAt(int sector)
        {
            string[] a = (KeyActions ?? "").Split(',');
            if (sector >= 0 && sector < a.Length)
            {
                string id = a[sector].Trim();
                for (int i = 0; i < KeyActionIds.Length; i++) if (KeyActionIds[i] == id) return id;
            }
            string[] def = { "new", "next", "delete", "prev" };
            return (sector >= 0 && sector < 4) ? def[sector] : "none";
        }

        public void SetKeyAction(int sector, string id)
        {
            string[] a = (KeyActions ?? "").Split(',');
            string[] def = { "new", "next", "delete", "prev" };
            string[] outv = new string[4];
            for (int i = 0; i < 4; i++) outv[i] = (i < a.Length && a[i].Trim().Length > 0) ? a[i].Trim() : def[i];
            if (sector >= 0 && sector < 4) outv[sector] = id;
            KeyActions = string.Join(",", outv);
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
                lines.Add("DragOutAsFile=" + (DragOutAsFile ? "1" : "0"));
                lines.Add("CheckUpdate=" + (CheckUpdate ? "1" : "0"));
                lines.Add("NubHintDone=" + (NubHintDone ? "1" : "0"));
                lines.Add("UndoHintDone=" + (UndoHintDone ? "1" : "0"));
                lines.Add("AnnotHintDone=" + (AnnotHintDone ? "1" : "0"));
                lines.Add("PinHintDone=" + (PinHintDone ? "1" : "0"));
                lines.Add("GuideSeenVersion=" + (GuideSeenVersion ?? ""));
                lines.Add("TextBg=" + (TextBg ? "1" : "0"));
                lines.Add("KeyActions=" + KeyActions);
                // 配置格式版本：写了它以后就不再被默认值迁移覆盖（迁移逻辑见 Load）。
                // 这里别写死数字 —— 之前写死 2，把 Load 里刚升到 3 的迁移标记又按回去了，
                // 结果每次启动都重跑一遍迁移（而且"迁移没落盘"这类问题很难看出来）。
                if (Rev < 3) Rev = 3;
                lines.Add("Rev=" + Rev);
                File.WriteAllLines(FilePath(), lines.ToArray());
            }
            catch { }
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
}

namespace SnapWheel
{
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
}

namespace SnapWheel
{
    partial class OverlayForm : Form
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

        // 0.5.0 起：浮层也拿得到设置（可以为 null —— 测试里就不传）。
        // 用它做两件事：第一次用的时候在工具条旁边弹一次"能标注"的提示；记住文字要不要白底。
        internal Settings _set;
        bool _textBg = true;             // 标注文字是否带白底（可在工具条上切换）
        bool _annotHint = false;         // 首次提示：还没展示过就亮一下
        DateTime _annotHintAt = DateTime.MinValue;

        public OverlayForm(Rectangle virtualScreen, Bitmap shot) : this(virtualScreen, shot, null) { }

        public OverlayForm(Rectangle virtualScreen, Bitmap shot, Settings settings)
        {
            _set = settings;
            _annotHint = (settings != null && !settings.AnnotHintDone);
            _textBg = (settings == null) || settings.TextBg;
            _annotHintAt = DateTime.Now;
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
        Panel _infoPanel;
        int _panelW = 0, _panelH = 0;

        // 当前该贴在"哪块屏幕"上：有选区就用选区中心那块，没有就用鼠标所在那块。
        // 以前这里是整个虚拟屏幕（_vs）的右边缘 —— 副屏在右边时，虚拟屏幕的右边缘就是副屏，
        // 这块面板和比例胶囊就会跑到副屏上去（用户报的"老 bug"）。
        internal static Rectangle ScreenFor(Rectangle virtualScreen, Point clientPoint, bool hasPoint)
        {
            try
            {
                // 客户坐标 -> 屏幕坐标（浮层左上角 = 虚拟屏幕左上角），再问系统那块屏幕
                Point screenPt = hasPoint ? new Point(virtualScreen.Left + clientPoint.X, virtualScreen.Top + clientPoint.Y)
                                          : Cursor.Position;
                return Screen.FromPoint(screenPt).Bounds;
            }
            catch { return virtualScreen; }
        }

        // 把"贴屏幕右上角"换算成浮层客户坐标（纯计算，方便测）
        internal static Rectangle InfoPanelRect(Rectangle virtualScreen, Rectangle screen, int panelW, int panelH)
        {
            int margin = 20;
            int x = (screen.Right - virtualScreen.Left) - panelW - margin;
            int y = (screen.Top - virtualScreen.Top) + 18;
            // 夹进这块屏幕里（别压出屏幕边）
            int minX = screen.Left - virtualScreen.Left, minY = screen.Top - virtualScreen.Top;
            int maxX = (screen.Right - virtualScreen.Left) - panelW, maxY = (screen.Bottom - virtualScreen.Top) - panelH;
            if (x < minX) x = minX;
            if (x > maxX) x = maxX;
            if (y < minY) y = minY;
            if (y > maxY) y = maxY;
            return new Rectangle(x, y, panelW, panelH);
        }

        void BuildInfoPanel()
        {
            _infoPanel = new Panel();
            _panelW = (int)(400 * _k);
            _panelH = (int)(40 * _k);
            _infoPanel.BackColor = Color.FromArgb(210, 18, 20, 24);
            Controls.Add(_infoPanel);
            Panel panel = _infoPanel;
            PlaceInfoPanel();

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

        // 把信息面板摆到"当前这块屏幕"的右上角；**被选区盖住时挪到选区外面**（上 → 下）。
        // 关键是"没被盖住就别动"：拖选区的时候位置一直变，面板跟着跳会很晕。
        void PlaceInfoPanel()
        {
            if (_infoPanel == null) return;
            Point refPt = _hasSel ? new Point((int)_c.X, (int)_c.Y) : Point.Empty;
            Rectangle scr = ScreenFor(_vs, refPt, _hasSel);
            Rectangle want = InfoPanelRect(_vs, scr, _panelW, _panelH);

            if (_hasSel)
            {
                RectangleF sb = SelBounds();
                Rectangle cur = new Rectangle(_infoPanel.Left, _infoPanel.Top, _panelW, _panelH);
                // 现在的位置没被盖住 → 保持不变
                if (cur.Width > 0 && !cur.IntersectsWith(Rectangle.Round(sb))) { _panelBounds = cur; return; }
                int cl = scr.Left - _vs.Left, ct = scr.Top - _vs.Top, cb = scr.Bottom - _vs.Top;
                int above = (int)sb.Top - _panelH - 10;
                int below = (int)sb.Bottom + 10;
                if (above >= ct + 8) want.Y = above;
                else if (below + _panelH <= cb - 8) want.Y = below;
                else { _panelBounds = cur; return; }      // 上下都没地方：保持原位（配合工具条变淡，不至于太挡）
                if (want.X + _panelW > scr.Right - _vs.Left - 12) want.X = scr.Right - _vs.Left - 12 - _panelW;
                int minX = scr.Left - _vs.Left + 12;
                if (want.X < minX) want.X = minX;
            }
            if (_infoPanel.Bounds != want)
            {
                _infoPanel.Bounds = want;
                try { Invalidate(); } catch { }
            }
            _panelBounds = want;
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
            // 一直贴"当前这块屏幕"（而不是整个虚拟屏幕）—— 双屏时胶囊才不会卡在两屏中间
            Rectangle scr = ScreenFor(_vs, _hasSel ? new Point((int)_c.X, (int)_c.Y) : Point.Empty, _hasSel);
            int cl = scr.Left - _vs.Left, ct = scr.Top - _vs.Top;      // 这块屏幕在客户坐标里的左上角
            int cr = scr.Right - _vs.Left, cb = scr.Bottom - _vs.Top;
            if (_hasSel)
            {
                RectangleF bb = SelBounds();
                rowX = (int)bb.Left;
                rowY = (int)bb.Bottom + (int)(14 * _k);
                if (rowY + h > cb - 10) rowY = (int)bb.Top - h - (int)(40 * _k);
            }
            else
            {
                rowX = cl + (scr.Width - totalW) / 2;
                rowY = cb - h - (int)(44 * _k);
            }
            if (rowX < cl + 10) rowX = cl + 10;
            if (rowX + totalW > cr - 10) rowX = cr - 10 - totalW;
            if (rowY < ct + 10) rowY = ct + 10;
            if (rowY + h > cb - 10) rowY = cb - 10 - h;

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
            PlaceInfoPanel();
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

                // 标注：裁在选区里画（所见即所得），工具条最后画、不受裁剪影响
                GraphicsState st = g.Save();
                using (GraphicsPath cp = new GraphicsPath())
                {
                    cp.AddPolygon(cs);
                    g.SetClip(cp, CombineMode.Intersect);
                    DrawAnnotationShapes(g);
                }
                g.Restore(st);
            }
            PlaceToolbar();
            UpdateToolAlpha();              // 鼠标不在附近就把工具条变淡（不挡画面）
            PaintToolbar(g, _toolAlpha);
            PaintShapeSelection(g);
            PaintIntroPanel(g);
            PaintOcrBusy(g);
            DrawChips(g);
        }

        // 滚轮：选中了标注图元就调它的大小（文字改字号），否则不动
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (AnnotWheel(e)) return;
            base.OnMouseWheel(e);
        }

        // ---------- 交互 ----------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Cancel(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (AnnotMouseDown(e)) return;

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
            if (AnnotMouseMove(e)) return;
            RefreshToolAlpha();          // 靠近/离开工具条时变实/变淡（浮层不常重绘，得主动请求）
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
            if (AnnotMouseUp(e)) return;
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
            // 选了标注工具时双击是在画东西（比如连点两下画两个方框），别把它当成"确认"
            if (_tool != AnnotKind.Select) return;
            if (_hasSel && InsideSel(e.Location)) Confirm();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // 正在打字：回车只把这段字落下去，绝不确认整张截图。
            // （以前这里把键"让给输入框"，结果回车漏到下面的"回车=确认截图"分支 ——
            //   用户打个字按回车，截图当场被确认并进了轮盘，非常懵。）
            if (_textBox != null)
            {
                if (e.KeyCode == Keys.Enter) { EndText(true); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Escape) { EndText(false); e.SuppressKeyPress = true; return; }
                return;                       // 其它键交给输入框
            }
            if (_annotHint) { _annotHint = false; Invalidate(); }   // 按任意键 = 开始用（键本身照常生效）
            if (AnnotKey(e)) return;
            if (e.KeyCode == Keys.Escape) Cancel();
            else if (e.KeyCode == Keys.Enter && _hasSel) Confirm();
        }

        void Confirm()
        {
            if (_shot == null || !_hasSel) { Close(); return; }
            EndText(true);                       // 还在输入框里的文字也算数
            if (_set != null) { try { _set.TextBg = _textBg; _set.Save(); } catch { } }   // 记住"文字底"的选择
            Result = CropSelection(true);
            DialogResult = DialogResult.OK;
            Close();
        }

        // 按当前选区裁一张图（withAnnotations=false 时只裁原图）
        internal Bitmap CropSelection(bool withAnnotations)
        {
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
                if (withAnnotations) DrawAnnotationShapes(g);   // 标注用同一套坐标和变换画进去 —— 所见即所得
            }
            return crop;
        }

        void Cancel() { Result = null; DialogResult = DialogResult.Cancel; Close(); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 定时器必须停掉：窗口关了它还每 15ms 醒一次，白烧 CPU（测试里越跑越慢就是这么来的）
            try { if (_anim != null) { _anim.Stop(); _anim.Dispose(); _anim = null; } } catch { }
            DisposeAnnotationCaches();
            if (_shot != null) { _shot.Dispose(); _shot = null; }
            if (_dimmed != null) { _dimmed.Dispose(); _dimmed = null; }
            base.OnFormClosed(e);
        }

        // 有些路径（测试、异常退出）是直接 Dispose 的，不经过 OnFormClosed，这里再兜一次
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { if (_anim != null) { _anim.Stop(); _anim.Dispose(); _anim = null; } } catch { } }
            base.Dispose(disposing);
        }
    }

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
}

namespace SnapWheel
{
    // 截图标注：箭头 / 方框 / 马赛克 / 文字（roadmap 里"加标注"那条 ——
    // 有了它，截完在轮盘里就能直接圈重点，不用再拖去别的软件）。
    //
    // 坐标：全部用浮层的客户坐标（= 截图位图自己的坐标，shot 画在 0,0），
    // 所以预览和"确认时合成进图片"可以共用同一套画法 —— 所见即所得。
    //
    // 交互：
    //   工具条在选区左下角（放不下就翻到上方）：选择 / 箭头 / 方框 / 马赛克 / 文字 | 颜色 ×4 | 文字底 | A- A+ | 撤销
    //   快捷键：Esc 取消截图（选中图元时先取消选中）、Enter 确认、Ctrl+Z 撤销、A 箭头、R 方框、
    //           M 马赛克、T 文字、V 选择、1~4 颜色、B 文字底、Del 删除选中、[ ] 改字号
    //   选中一个图元后：拖动 = 移动，滚轮 = 改字号（文字）/ 粗细（其它），Del = 删除
    partial class OverlayForm
    {
        enum AnnotKind { Select = 0, Arrow = 1, Rect = 2, Mosaic = 3, Text = 4, Ocr = 5 }

        class Shape
        {
            public AnnotKind Kind;
            public PointF A, B;          // 箭头/方框/马赛克 = 起止点；文字 = A 是位置
            public string Text;
            public Color Color;
            public float W = 3f;
            public float Size = 20f;     // 文字字号（会再乘 _k）
            public Bitmap Cache;         // 马赛克结果缓存（每帧重算太贵）
            public Rectangle CacheRect;
        }

        static readonly Color[] AnnotColors = {
            Color.FromArgb(238, 70, 90),    // 红（默认，圈重点最常用）
            Color.FromArgb(250, 176, 42),   // 黄
            Color.FromArgb(0, 150, 240),    // 蓝
            Color.FromArgb(26, 28, 34)      // 黑
        };

        readonly List<Shape> _shapes = new List<Shape>();
        Shape _drawing = null;               // 正在拖的那一个（松手才进 _shapes）
        Shape _sel = null;                   // 当前选中的图元（可拖动/改字号/删除）
        Shape _dragShape = null;             // 正在拖动的图元
        PointF _dragFromShape;
        AnnotKind _tool = AnnotKind.Select;
        Color _annotColor = AnnotColors[0];
        TextBox _textBox = null;
        Rectangle _toolRect = Rectangle.Empty;      // 工具条整体
        Rectangle[] _toolBtns = new Rectangle[0];   // 每个按钮的位置（含颜色点、撤销）
        int _toolHover = -1;
        Rectangle _introRect = Rectangle.Empty;     // 首次教程面板

        const int BtnW = 34;                 // 都会被 _k 缩放
        const int BtnH = 30;
        const int Gap = 6;
        const int IdxBg = 6 + 4;             // 工具 6 个（选择/箭头/方框/马赛克/文字/取字），颜色点占 6..9
        const int IdxSizeDown = IdxBg + 1;
        const int IdxSizeUp = IdxBg + 2;
        const int IdxUndo = IdxBg + 3;
        const int BtnCount = IdxBg + 4;

        // ---------- 几何 / 命中 ----------
        static RectangleF RectOf(PointF a, PointF b)
        {
            return new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        }

        static Rectangle ToRect(RectangleF r)
        {
            return new Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), Math.Max(1, (int)Math.Round(r.Width)), Math.Max(1, (int)Math.Round(r.Height)));
        }

        SizeF TextSize(Shape s)
        {
            string t = string.IsNullOrEmpty(s.Text) ? " " : s.Text;
            using (Font f = new Font("Microsoft YaHei UI", Math.Max(6f, s.Size * _k), FontStyle.Bold))
            {
                Size sz = TextRenderer.MeasureText(t, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                return new SizeF(sz.Width, sz.Height);
            }
        }

        // 图元的外框（文字按实际排版量；其它按起止点）
        RectangleF ShapeBounds(Shape s)
        {
            if (s == null) return RectangleF.Empty;
            if (s.Kind != AnnotKind.Text) return RectOf(s.A, s.B);
            SizeF sz = TextSize(s);
            return new RectangleF(s.A.X - 3 * _k, s.A.Y - 2 * _k, sz.Width + 7 * _k, sz.Height + 5 * _k);
        }

        // 命中图元：从后往前找（后画的在上层）
        Shape HitShape(PointF p)
        {
            for (int i = _shapes.Count - 1; i >= 0; i--)
            {
                Shape s = _shapes[i];
                RectangleF r = ShapeBounds(s);
                if (s.Kind != AnnotKind.Text)
                {
                    float pad = Math.Max(6f, s.W * _k + 3f);
                    r.Inflate(pad, pad);
                }
                if (r.Contains(p)) return s;
            }
            return null;
        }

        void SelectShape(Shape s)
        {
            if (_sel == s) return;
            _sel = s;
            Invalidate();
        }

        void MoveShape(Shape s, float dx, float dy)
        {
            s.A = new PointF(s.A.X + dx, s.A.Y + dy);
            if (s.Kind != AnnotKind.Text) s.B = new PointF(s.B.X + dx, s.B.Y + dy);
            if (s.Cache != null) { try { s.Cache.Dispose(); } catch { } s.Cache = null; }   // 马赛克跟着挪，得重算
        }

        // 滚轮/按钮调大小：文字改字号，其它改线条粗细
        void ResizeShape(Shape s, float delta)
        {
            if (s == null) return;
            if (s.Kind == AnnotKind.Text)
            {
                s.Size = Math.Max(9f, Math.Min(160f, s.Size + delta * 2f));
                _textSize = s.Size;          // 下一个新文字也用这个大小
            }
            else
                s.W = Math.Max(1f, Math.Min(24f, s.W + delta * 0.4f));
            Invalidate();
        }

        // ---------- 工具条布局 ----------
        // 一条硬规则：**工具条绝不压住选区**（压住就是在挡你要截的内容）。
        // 四个方向依次试，全试不到才允许压一点：
        //   1 选区下方  2 选区上方  3 选区右侧（竖排）  4 选区左侧（竖排）
        // 以前只试上下两个方向，选区一高（比如竖着截一整条）就只能压在截图上 ——
        // 结果就是"工具栏挡住了截图区域"。
        int _toolAlpha = 255;        // 鼠标不在附近时自动变淡（不挡内容），靠近就完全不透明
        bool _toolVertical = false;  // 贴在选区左右两侧时改成竖排
        bool _toolOverlap = false;   // 实在没地方、只能压住选区（这时画得更透）

        void PlaceToolbar()
        {
            int bw = (int)(BtnW * _k), bh = (int)(BtnH * _k), gp = (int)(Gap * _k);
            int n = BtnCount;
            RectangleF sb = SelBounds();

            // 按"当前这块屏幕"来算（多屏时别摆到别的屏去）
            Rectangle scr = ScreenFor(_vs, _hasSel ? new Point((int)_c.X, (int)_c.Y) : Point.Empty, _hasSel);
            Rectangle scrLocal = new Rectangle(scr.Left - _vs.Left, scr.Top - _vs.Top, scr.Width, scr.Height);

            // 左下角那个「比例」按钮（以及展开后的面板）别被压住
            Rectangle avoid = _toggleRect;
            if (_chipsOpen && _chips != null && _chips.Length > 0) avoid = Rectangle.Union(avoid, _chips[0].Rect);

            bool vertical, overlap;
            Rectangle me = ToolbarRect(scrLocal, sb, n, bw, bh, gp, gp, avoid, out vertical, out overlap);

            // 高 DPI 小屏（比如 1080p 开 150%）：竖排长度可能比屏幕还高 —— 那就把按钮间距压紧再试一次，
            // 宁可排得挤一点，也别去压住用户要截的地方。
            int gapUse = gp;
            if (vertical && me.Height > scrLocal.Height - 16 && gp > 3)
            {
                int cg = Math.Max(2, gp / 3);
                bool v2, o2;
                Rectangle m2 = ToolbarRect(scrLocal, sb, n, bw, bh, cg, cg, avoid, out v2, out o2);
                if (v2 && m2.Height <= scrLocal.Height - 16) { me = m2; vertical = v2; overlap = o2; gapUse = cg; }
            }

            _toolVertical = vertical;
            _toolOverlap = overlap;
            _toolRect = me;
            _toolBtns = new Rectangle[n];
            int cx = me.X + gapUse, cy = me.Y + gapUse;
            for (int i = 0; i < n; i++)
            {
                _toolBtns[i] = new Rectangle(cx, cy, bw, bh);
                if (vertical) cy += bh + gapUse; else cx += bw + gapUse;
            }
        }

        // 工具条到底摆哪（纯计算，离线可测）：**绝不压住选区**是硬规则。
        // 四个方向依次试，全试不到才允许压一点：
        //   1 选区下方  2 选区上方  3 选区右侧（竖排）  4 选区左侧（竖排）
        // 以前只试上下两个方向，选区一高（比如竖着截一整条）就只能压在截图上 ——
        // 用户看到的就是"工具栏挡住了截图区域"。
        internal static Rectangle ToolbarRect(Rectangle screen, RectangleF sel, int n, int bw, int bh, int gapBetween, int outer,
                                              Rectangle avoid, out bool vertical, out bool overlap)
        {
            vertical = false; overlap = false;
            int rowLen = n * bw + (n - 1) * gapBetween + outer * 2;   // 排成一排/一列时的总长
            int thick = bh + outer * 2;                               // 另一边的厚度
            int cl = screen.Left + 8, ct = screen.Top + 8, cr = screen.Right - 8, cb = screen.Bottom - 8;

            const int gap = 12;
            int sx0 = (int)sel.Left, sy0 = (int)sel.Top;
            int sx1 = (int)Math.Ceiling(sel.Right), sy1 = (int)Math.Ceiling(sel.Bottom);
            int rightX = sx1 + gap, leftX = sx0 - thick - gap;
            int belowY = sy1 + gap, aboveY = sy0 - thick - gap;

            int x = 0, y = 0;

            int hx = (int)sel.Left;                        // 横排：跟选区左对齐，再夹进屏幕
            if (hx + rowLen > cr) hx = cr - rowLen;
            if (hx < cl) hx = cl;

            int vy = (int)sel.Top;                         // 竖排：跟选区上对齐，再夹进屏幕
            if (vy + rowLen > cb) vy = cb - rowLen;
            if (vy < ct) vy = ct;

            if (belowY + thick <= cb) { x = hx; y = belowY; }                                    // 1 下方
            else if (aboveY >= ct) { x = hx; y = aboveY; }                                       // 2 上方
            else if (rowLen <= cb - ct && rightX + thick <= cr) { vertical = true; x = rightX; y = vy; }   // 3 右侧竖排
            else if (rowLen <= cb - ct && leftX >= cl) { vertical = true; x = leftX; y = vy; }             // 4 左侧竖排
            else
            {
                // 5 四处都没空（选区几乎铺满整屏）：压到"外面更空"的那一侧，并标记"压住了"——
                //   画的时候会压得更透（配合"鼠标不在附近就变淡"），至少不糊住看不清
                overlap = true;
                int roomAbove = sy0 - ct, roomBelow = cb - sy1;
                x = hx;
                y = (roomBelow >= roomAbove) ? Math.Min(cb - thick, belowY) : Math.Max(ct, aboveY);
                if (y < ct) y = ct;
                if (y + thick > cb) y = cb - thick;
            }
            if (x < cl) x = cl;

            int w = vertical ? thick : rowLen;
            int h = vertical ? rowLen : thick;
            Rectangle me = new Rectangle(x, y, w, h);

            if (me.IntersectsWith(avoid))
            {
                // 先试试横着躲开，再试竖着躲开；只有两样都不行才认命（并且更透）
                int altY = (y <= avoid.Top) ? avoid.Bottom + 6 : avoid.Top - h - 6;
                int altX = (x <= avoid.Left) ? avoid.Right + 6 : avoid.Left - w - 6;
                Rectangle candX = new Rectangle(altX, y, w, h);
                Rectangle candY = new Rectangle(x, altY, w, h);
                if (altX >= cl && altX + w <= cr && !candX.IntersectsWith(avoid) && !HitsSel(candX, sel))
                    me = candX;
                else if (altY >= ct && altY + h <= cb && !candY.IntersectsWith(avoid) && !HitsSel(candY, sel))
                    me = candY;
                else
                {
                    if (altY >= ct && altY + h <= cb) me = candY;
                    if (me.Y < ct) me.Y = ct;
                    if (me.Y + h > cb) me.Y = cb - h;
                    if (me.X < cl) me.X = cl;
                    overlap = true;
                }
            }
            return me;
        }

        static bool HitsSel(Rectangle r, RectangleF sb)
        {
            return r.IntersectsWith(Rectangle.Round(sb));
        }

        bool ToolbarVisible()
        {
            // 拖框选的过程中先不显示：那会儿工具条会追着鼠标、正好压在你要选的地方
            if (_dragging) return false;
            return _hasSel && _sz.Width > 20 && _sz.Height > 20;
        }

        // 鼠标离工具条远就变淡（不挡截图），靠近就恢复不透明。
        // 只做"远/近"两档、阈值给足余量，不做连续渐变 —— 免得看着晃。
        // 压住选区时（_toolOverlap）基础透明度更低，尽量别挡住底下那张图。
        // 返回"透明度变了没有"：浮层不是每帧重绘，变了得主动请求重绘，否则永远看不到变化。
        bool UpdateToolAlpha()
        {
            if (_toolRect.Width == 0) { _toolAlpha = 255; return false; }
            int want;
            try
            {
                Point cp = PointToClient(Cursor.Position);
                int dx = 0, dy = 0;
                if (cp.X < _toolRect.Left) dx = _toolRect.Left - cp.X;
                else if (cp.X > _toolRect.Right) dx = cp.X - _toolRect.Right;
                if (cp.Y < _toolRect.Top) dy = _toolRect.Top - cp.Y;
                else if (cp.Y > _toolRect.Bottom) dy = cp.Y - _toolRect.Bottom;
                int d = (int)Math.Sqrt(dx * dx + dy * dy);
                int idle = _toolOverlap ? 108 : 165;
                want = (d < 90) ? 255 : idle;
            }
            catch { want = 255; }
            if (want == _toolAlpha) return false;
            _toolAlpha = want;
            return true;
        }

        // 鼠标一动就调一次：只有真的需要变淡/变实才重绘
        void RefreshToolAlpha()
        {
            if (UpdateToolAlpha()) Invalidate();
        }

        // ---------- 画标注内容（预览与合成共用） ----------
        void DrawAnnotationShapes(Graphics g)
        {
            for (int i = 0; i < _shapes.Count; i++) DrawOne(g, _shapes[i]);
            if (_drawing != null) DrawOne(g, _drawing);
        }

        void DrawOne(Graphics g, Shape s)
        {
            if (s == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            switch (s.Kind)
            {
                case AnnotKind.Arrow:
                    {
                        using (Pen p = new Pen(s.Color, s.W * _k))
                        {
                            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                            g.DrawLine(p, s.A, s.B);
                        }
                        DrawArrowHead(g, s.A, s.B, s.Color, s.W * _k);
                        break;
                    }
                case AnnotKind.Rect:
                    {
                        RectangleF r = RectOf(s.A, s.B);
                        using (Pen p = new Pen(s.Color, s.W * _k))
                        {
                            p.Alignment = PenAlignment.Inset;
                            using (GraphicsPath path = Gfx.Round(r, Math.Min(6f, r.Height / 3f)))
                                g.DrawPath(p, path);
                        }
                        break;
                    }
                case AnnotKind.Mosaic:
                    {
                        Rectangle r = ToRect(RectOf(s.A, s.B));
                        if (r.Width < 2 || r.Height < 2) break;
                        if (s.Cache == null || s.CacheRect != r)
                        {
                            if (s.Cache != null) { try { s.Cache.Dispose(); } catch { } s.Cache = null; }
                            s.Cache = MosaicOf(_shot, r);
                            s.CacheRect = r;
                        }
                        if (s.Cache == null) break;
                        g.DrawImageUnscaled(s.Cache, r.Left, r.Top);
                        break;
                    }
                case AnnotKind.Ocr:
                    {
                        // 取字时拖出来的框：只是"要认哪一块"的示意，不会画进图里
                        RectangleF r = RectOf(s.A, s.B);
                        using (Pen p = new Pen(Color.FromArgb(245, 166, 35), 1.8f * _k))
                        {
                            p.DashStyle = DashStyle.Dash;
                            g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                        }
                        break;
                    }
                case AnnotKind.Text:
                    {
                        if (string.IsNullOrEmpty(s.Text)) break;
                        float fs = Math.Max(6f, s.Size * _k);
                        if (_textBg)
                        {
                            using (Font f = new Font("Microsoft YaHei UI", fs, FontStyle.Bold))
                            using (SolidBrush bg = new SolidBrush(Color.FromArgb(165, 255, 255, 255)))
                            {
                                SizeF sz = g.MeasureString(s.Text, f);
                                g.FillRectangle(bg, s.A.X - 3 * _k, s.A.Y - 2 * _k, sz.Width + 6 * _k, sz.Height + 2 * _k);
                            }
                        }
                        using (Font f = new Font("Microsoft YaHei UI", fs, FontStyle.Bold))
                        using (SolidBrush b = new SolidBrush(s.Color))
                            g.DrawString(s.Text, f, b, s.A);
                        break;
                    }
            }
        }

        void DrawArrowHead(Graphics g, PointF from, PointF to, Color c, float w)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 2f) return;
            float ux = dx / len, uy = dy / len;
            float head = Math.Max(9f * _k, w * 3.2f);
            float half = head * 0.5f;
            PointF p1 = new PointF(to.X - ux * head - uy * half, to.Y - uy * head + ux * half);
            PointF p2 = new PointF(to.X - ux * head + uy * half, to.Y - uy * head - ux * half);
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, new PointF[] { to, p1, p2 });
        }

        // 马赛克：先把这块缩小，再放大回原来的大小（放大用最近邻 → 变成色块）。
        // 返回的位图尺寸 == 请求的矩形，所以画的时候直接贴在 r 的左上角就行。
        static Bitmap MosaicOf(Bitmap src, Rectangle r)
        {
            if (src == null || r.Width < 2 || r.Height < 2) return null;
            Rectangle clip = Rectangle.Intersect(r, new Rectangle(0, 0, src.Width, src.Height));
            if (clip.Width < 2 || clip.Height < 2) return null;
            int block = Math.Max(6, (int)Math.Round(Math.Min(clip.Width, clip.Height) / 12f));
            int sw = Math.Max(1, clip.Width / block), sh = Math.Max(1, clip.Height / block);
            try
            {
                Bitmap big = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
                using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, sw, sh), clip, GraphicsUnit.Pixel);
                    }
                    using (Graphics g = Graphics.FromImage(big))
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.SmoothingMode = SmoothingMode.None;
                        g.DrawImage(small, new Rectangle(0, 0, r.Width, r.Height));
                    }
                }
                return big;
            }
            catch { return null; }
        }

        // 关掉浮层时把马赛克缓存放掉（不然每画一次就漏一块内存）
        void DisposeAnnotationCaches()
        {
            for (int i = 0; i < _shapes.Count; i++)
                if (_shapes[i].Cache != null) { try { _shapes[i].Cache.Dispose(); } catch { } _shapes[i].Cache = null; }
            if (_drawing != null && _drawing.Cache != null) { try { _drawing.Cache.Dispose(); } catch { } _drawing.Cache = null; }
        }

        // ---------- 画工具条 ----------
        void PaintToolbar(Graphics g, int alpha)
        {
            if (!ToolbarVisible() || _toolBtns.Length == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath bgp = Gfx.Round(_toolRect, 10f * _k))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(238 * alpha / 255f), 22, 24, 28)))
                    g.FillPath(b, bgp);
                using (Pen p = new Pen(Color.FromArgb((int)(60 * alpha / 255f), 255, 255, 255), 1f))
                    g.DrawPath(p, bgp);
            }

            int a = Math.Max(0, Math.Min(255, alpha));
            for (int i = 0; i < _toolBtns.Length; i++)
            {
                Rectangle r = _toolBtns[i];
                bool isTool = i < 6;
                bool sel = isTool && ((AnnotKind)i == _tool);
                if (sel || i == _toolHover)
                {
                    using (GraphicsPath bp = Gfx.Round(r, 7f * _k))
                    using (SolidBrush b = new SolidBrush(sel ? Color.FromArgb((int)(235 * a / 255f), 0, 122, 204)
                                                              : Color.FromArgb((int)(90 * a / 255f), 255, 255, 255)))
                        g.FillPath(b, bp);
                }

                Color ic = Color.FromArgb(a, 255, 255, 255);   // 图标跟着一起淡，不然底淡了图标还刺眼
                if (i >= 6 && i < 6 + AnnotColors.Length)
                {
                    // 颜色点：当前色描粗白边；其余也描一圈细边 —— 黑点在深色工具条上不然看不见
                    int ci = i - 6;
                    bool cur = (_annotColor.ToArgb() == AnnotColors[ci].ToArgb());
                    int d = (int)(15 * _k);
                    Rectangle cr = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
                    using (SolidBrush b = new SolidBrush(AnnotColors[ci])) g.FillEllipse(b, cr);
                    using (Pen ring = new Pen(Color.FromArgb((int)((cur ? 255 : 140) * a / 255f), 255, 255, 255), cur ? 2.2f : 1.2f))
                        g.DrawEllipse(ring, cr);
                    continue;
                }

                RectangleF d2 = Inset(r, 9f * _k);
                switch (i)
                {
                    case 0:      // 选择（指针）
                        {
                            using (SolidBrush b = new SolidBrush(ic))
                                g.FillPolygon(b, new PointF[] {
                                    new PointF(d2.Left + d2.Width * 0.25f, d2.Top),
                                    new PointF(d2.Left + d2.Width * 0.25f, d2.Bottom),
                                    new PointF(d2.Left + d2.Width * 0.62f, d2.Bottom - d2.Height * 0.30f),
                                    new PointF(d2.Right, d2.Top + d2.Height * 0.12f) });
                            break;
                        }
                    case 1:      // 箭头
                        {
                            using (Pen p = new Pen(ic, 2f * _k))
                            {
                                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                                g.DrawLine(p, d2.Left, d2.Bottom, d2.Right * 0.92f + d2.Left * 0.08f, d2.Top + d2.Height * 0.08f);
                            }
                            using (SolidBrush b = new SolidBrush(ic))
                            {
                                g.FillPolygon(b, new PointF[] {
                                    new PointF(d2.Right, d2.Top),
                                    new PointF(d2.Right - d2.Width * 0.42f, d2.Top + d2.Height * 0.10f),
                                    new PointF(d2.Right - d2.Width * 0.10f, d2.Top + d2.Height * 0.42f) });
                            }
                            break;
                        }
                    case 2:      // 方框
                        using (Pen p = new Pen(ic, 2f * _k)) g.DrawRectangle(p, d2.Left, d2.Top, d2.Width, d2.Height);
                        break;
                    case 3:      // 马赛克：田字格
                        {
                            float hw = d2.Width / 2f, hh = d2.Height / 2f;
                            using (SolidBrush b = new SolidBrush(ic))
                            {
                                g.FillRectangle(b, d2.Left, d2.Top, hw - 1, hh - 1);
                                g.FillRectangle(b, d2.Left + hw + 1, d2.Top, hw - 1, hh - 1);
                                g.FillRectangle(b, d2.Left, d2.Top + hh + 1, hw - 1, hh - 1);
                                g.FillRectangle(b, d2.Left + hw + 1, d2.Top + hh + 1, hw - 1, hh - 1);
                            }
                            break;
                        }
                    case 4:      // 文字
                        using (Font f = new Font("Microsoft YaHei UI", 12f * _k, FontStyle.Bold))
                        using (SolidBrush b = new SolidBrush(ic))
                        {
                            StringFormat sf = new StringFormat();
                            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                            g.DrawString("T", f, b, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                        }
                        break;
                    case IdxBg:  // 文字底：方块填实=带白底，只描边=不带底
                        {
                            RectangleF sq = new RectangleF(d2.Left, d2.Top + d2.Height * 0.12f, d2.Width, d2.Height * 0.88f);
                            if (_textBg)
                                using (SolidBrush b = new SolidBrush(ic)) g.FillRectangle(b, sq);
                            using (Pen p = new Pen(ic, 1.4f * _k)) g.DrawRectangle(p, sq.X, sq.Y, sq.Width, sq.Height);
                            using (Font f = new Font("Microsoft YaHei UI", 8.5f * _k, FontStyle.Bold))
                            using (SolidBrush b = new SolidBrush(_textBg ? Color.FromArgb(a, 22, 24, 28) : ic))
                            {
                                StringFormat sf = new StringFormat();
                                sf.Alignment = StringAlignment.Center;
                                sf.LineAlignment = StringAlignment.Center;
                                g.DrawString("T", f, b, sq, sf);
                            }
                            break;
                        }
                    case IdxSizeDown:   // A-
                    case IdxSizeUp:     // A+
                        {
                            using (Font f = new Font("Microsoft YaHei UI", 13f * _k, FontStyle.Bold))
                            using (SolidBrush b = new SolidBrush(ic))
                                g.DrawString("A", f, b, d2.Left - 1 * _k, d2.Top - 1 * _k);
                            using (Pen p = new Pen(ic, 1.8f * _k))
                            {
                                float mx = d2.Right - 2 * _k, my = d2.Top + d2.Height * 0.34f;
                                g.DrawLine(p, mx - 5 * _k, my, mx, my);
                                if (i == IdxSizeUp) g.DrawLine(p, mx - 2.5f * _k, my - 2.5f * _k, mx - 2.5f * _k, my + 2.5f * _k);
                            }
                            break;
                        }
                    case 5:             // 取字工具：一个"字"比任何图标都好认
                        using (Font f = new Font("Microsoft YaHei UI", 13f * _k, FontStyle.Bold))
                        using (SolidBrush b = new SolidBrush(Ocr.Available ? ic : Color.FromArgb((int)(120 * a / 255f), 255, 255, 255)))
                        {
                            StringFormat sf = new StringFormat();
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            g.DrawString("字", f, b, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                        }
                        break;
                    default:     // 撤销
                        using (Pen p = new Pen(_shapes.Count > 0 ? ic : Color.FromArgb((int)(110 * a / 255f), 255, 255, 255), 2f * _k))
                        {
                            g.DrawArc(p, d2.Left, d2.Top + d2.Height * 0.15f, d2.Width, d2.Height * 0.9f, 30, 250);
                            g.DrawLine(p, d2.Left + d2.Width * 0.02f, d2.Top + d2.Height * 0.42f, d2.Left + d2.Width * 0.28f, d2.Top + d2.Height * 0.10f);
                            g.DrawLine(p, d2.Left + d2.Width * 0.02f, d2.Top + d2.Height * 0.42f, d2.Left + d2.Width * 0.32f, d2.Top + d2.Height * 0.55f);
                        }
                        break;
                }
            }
        }

        static RectangleF Inset(Rectangle r, float pad)
        {
            return new RectangleF(r.X + pad, r.Y + pad, Math.Max(2, r.Width - pad * 2), Math.Max(2, r.Height - pad * 2));
        }

        // 选中的图元：虚线框 + 一句"能怎么改"（拖动/滚轮/删除 全靠它被看见）
        void PaintShapeSelection(Graphics g)
        {
            if (_sel == null || _shapes.Count == 0) return;
            RectangleF r = ShapeBounds(_sel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Color.FromArgb(235, 0, 174, 255), 1.6f))
            {
                p.DashStyle = DashStyle.Dash;
                g.DrawRectangle(p, r.X - 3 * _k, r.Y - 3 * _k, r.Width + 6 * _k, r.Height + 6 * _k);
            }
            string hint = (_sel.Kind == AnnotKind.Text) ? "拖动移动　·　滚轮 / A+/A- 改字号　·　Del 删除"
                                                        : "拖动移动　·　滚轮改粗细　·　Del 删除";
            using (Font f = new Font("Microsoft YaHei UI", 9f * _k, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(hint, f);
                int pad = (int)(7 * _k);
                int w = (int)sz.Width + pad * 2, h = (int)sz.Height + pad;
                int x = (int)(r.Left);
                int y = (int)(r.Top - h - 8 * _k);
                if (y < 6) y = (int)(r.Bottom + 8 * _k);
                if (x + w > _vs.Width - 6) x = _vs.Width - 6 - w;
                if (x < 6) x = 6;
                using (GraphicsPath bp = Gfx.Round(new Rectangle(x, y, w, h), 7f * _k))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(238, 22, 24, 28)))
                    g.FillPath(b, bp);
                using (SolidBrush tb = new SolidBrush(Color.White))
                    g.DrawString(hint, f, tb, x + pad, y + pad / 2f);
            }
        }

        // 第一次进截图界面：中间来一块正经的教程面板（不是角落里一句小提示）
        void PaintIntroPanel(Graphics g)
        {
            if (!_annotHint) return;
            int w = (int)(440 * _k), h = (int)(292 * _k);
            RectangleF sb = _hasSel ? SelBounds() : new RectangleF(_vs.Width / 2f - 200 * _k, _vs.Height / 2f - 130 * _k, 400 * _k, 260 * _k);
            int x = (int)(sb.Left + (sb.Width - w) / 2f);
            int y = (int)(sb.Top + Math.Max(20 * _k, (sb.Height - h) / 2f));
            if (x < 12) x = 12;
            if (x + w > _vs.Width - 12) x = _vs.Width - 12 - w;
            if (y < 12) y = 12;
            if (y + h > _vs.Height - 12) y = _vs.Height - 12 - h;
            _introRect = new Rectangle(x, y, w, h);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath bp = Gfx.Round(_introRect, 16f * _k))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(245, 22, 24, 30))) g.FillPath(b, bp);
                using (Pen pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1.4f)) g.DrawPath(pen, bp);
            }

            int pad = (int)(22 * _k);
            using (Font ft = new Font("Microsoft YaHei UI", 15f * _k, FontStyle.Bold))
            using (SolidBrush bt = new SolidBrush(Color.White))
                g.DrawString("截图浮层：三步搞定", ft, bt, x + pad, y + pad - 4 * _k);

            string[] lines = {
                "①  按住左键拖出要截的区域（四角缩放、圆点旋转、中间拖动）",
                "②  用下面的工具条标注：箭头 A · 方框 R · 马赛克 M · 文字 T",
                "      颜色 1~4 · 文字底 B · 字号 A+/A- 或滚轮 · Ctrl+Z 撤销",
                "      画完的文字/方框可以直接拖动、滚轮改大小，Del 删掉",
                "③  选「字」工具（或按 O）拖一个框圈住文字 = 取字，框越小越准；",
                "      取字窗口里还能一键翻译成中文/英文",
                "④  双击选区或按回车 = 确认（Esc 取消），图直接进轮盘"
            };
            int ly = y + pad + (int)(34 * _k);
            using (Font fl = new Font("Microsoft YaHei UI", 10f * _k))
            using (SolidBrush bl = new SolidBrush(Color.FromArgb(226, 232, 240)))
                for (int i = 0; i < lines.Length; i++)
                    g.DrawString(lines[i], fl, bl, x + pad, ly + i * (int)(24 * _k));

            Rectangle btn = new Rectangle(x + w - pad - (int)(124 * _k), y + h - pad - (int)(34 * _k), (int)(124 * _k), (int)(34 * _k));
            using (GraphicsPath bp = Gfx.Round(btn, 9f * _k))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 122, 204)))
                g.FillPath(b, bp);
            using (Font fb = new Font("Microsoft YaHei UI", 10.5f * _k, FontStyle.Bold))
            using (SolidBrush bb = new SolidBrush(Color.White))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString("开始用", fb, bb, btn, sf);
            }
            using (Font fh = new Font("Microsoft YaHei UI", 8.5f * _k))
            using (SolidBrush bh = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                g.DrawString("点一下面板或按任意键就开始（只提示这一次）", fh, bh, x + pad, y + h - pad - (int)(18 * _k));
        }

        // ---------- 鼠标 / 键盘钩子（由 OverlayForm 主文件调进来） ----------
        bool AnnotMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return false;

            if (_annotHint)
            {
                bool inPanel = _introRect.Contains(e.Location);
                _annotHint = false;
                Invalidate();
                if (inPanel) return true;        // 点面板本身：就当作"我知道了"
            }

            if (ToolbarVisible())
            {
                for (int i = 0; i < _toolBtns.Length; i++)
                {
                    if (!_toolBtns[i].Contains(e.Location)) continue;
                    if (i < 6) { EndText(true); _tool = (AnnotKind)i; }
                    else if (i < 6 + AnnotColors.Length) { _annotColor = AnnotColors[i - 6]; }
                    else if (i == IdxBg) { _textBg = !_textBg; SaveTextBg(); }
                    else if (i == IdxSizeDown) { if (_sel != null) ResizeShape(_sel, -1f); else SetNextTextSize(_textSize - 2f); }
                    else if (i == IdxSizeUp) { if (_sel != null) ResizeShape(_sel, 1f); else SetNextTextSize(_textSize + 2f); }

                    else Undo();
                    Invalidate();
                    return true;
                }
                if (_toolRect.Contains(e.Location)) return true;   // 点在工具条空白处：别当成长按选图
            }

            // 点到已有的图元上：选中它并准备拖动（选择工具、文字工具都支持）
            Shape hit = HitShape(e.Location);
            if (hit != null && (_tool == AnnotKind.Select || _tool == AnnotKind.Text))
            {
                EndText(true);
                SelectShape(hit);
                _dragShape = hit;
                _dragFromShape = e.Location;
                return true;
            }
            if (hit != null && hit.Kind == AnnotKind.Text && _tool != AnnotKind.Select)
            {
                EndText(true);
                SelectShape(hit);
                _dragShape = hit;
                _dragFromShape = e.Location;
                return true;
            }

            if (_tool == AnnotKind.Select)
            {
                SelectShape(null);
                return false;                  // 交回给原来的框选/移动逻辑
            }
            if (!_hasSel) return false;
            if (!InsideSel(e.Location))
            {
                // 选了标注工具还点到选区外：什么都不做。
                // 否则会落回"新建选区"，把刚画的标注全丢掉（画错一笔就白干，太气人）。
                // 想重新框选按 V（或 Esc 重来）。
                return true;
            }
            if (_textBox != null) EndText(true);

            if (_tool == AnnotKind.Text) { BeginText(e.Location); return true; }

            if (_tool == AnnotKind.Ocr)
            {
                // 取字工具：拖一个框圈住要认的文字（框小=只是想认整块选区）
                _drawing = new Shape();
                _drawing.Kind = AnnotKind.Ocr;
                _drawing.A = e.Location;
                _drawing.B = e.Location;
                SelectShape(null);
                Invalidate();
                return true;
            }

            _drawing = new Shape();
            _drawing.Kind = _tool;
            _drawing.A = e.Location;
            _drawing.B = e.Location;
            _drawing.Color = _annotColor;
            _drawing.W = 3f;
            SelectShape(null);
            Invalidate();
            return true;
        }

        bool AnnotMouseMove(MouseEventArgs e)
        {
            RefreshToolAlpha();          // 靠近/离开工具条时变实/变淡
            if (_dragShape != null)
            {
                MoveShape(_dragShape, e.Location.X - _dragFromShape.X, e.Location.Y - _dragFromShape.Y);
                _dragFromShape = e.Location;
                Invalidate();
                return true;
            }

            int h = -1;
            if (ToolbarVisible())
                for (int i = 0; i < _toolBtns.Length; i++) if (_toolBtns[i].Contains(e.Location)) { h = i; break; }
            if (h != _toolHover) { _toolHover = h; Invalidate(); }

            if (_drawing == null) return false;
            _drawing.B = e.Location;
            Invalidate();
            return true;
        }

        bool AnnotMouseUp(MouseEventArgs e)
        {
            if (_dragShape != null) { _dragShape = null; Invalidate(); return true; }
            if (_drawing == null) return false;
            Shape s = _drawing;
            _drawing = null;
            if (s.Kind == AnnotKind.Ocr)
            {
                // 取字：不去动 _shapes（它不是标注，不该被画进成品图）
                RectangleF rc = RectOf(s.A, s.B);
                Invalidate();
                DoOcrRegion(rc);
                return true;
            }
            RectangleF r = RectOf(s.A, s.B);
            bool ok = (s.Kind == AnnotKind.Arrow) || (r.Width >= 4 && r.Height >= 4);
            if (ok) { _shapes.Add(s); _annotHint = false; }
            Invalidate();
            return true;
        }

        // 滚轮：选中了图元就改大小（文字改字号），没选中就还给主逻辑
        internal bool AnnotWheel(MouseEventArgs e)
        {
            if (_sel == null) return false;
            ResizeShape(_sel, e.Delta > 0 ? 1f : -1f);
            return true;
        }

        // 返回 true = 这个键已经被标注逻辑用掉了
        bool AnnotKey(KeyEventArgs e)
        {
            if (_textBox != null) return false;        // 正在打字：键都归输入框

            bool ctrl = (e.Modifiers & Keys.Control) == Keys.Control;
            if (ctrl && e.KeyCode == Keys.Z) { Undo(); return true; }
            if (ctrl) return false;

            switch (e.KeyCode)
            {
                case Keys.V: _tool = AnnotKind.Select; break;
                case Keys.A: _tool = AnnotKind.Arrow; break;
                case Keys.R: _tool = AnnotKind.Rect; break;
                case Keys.M: _tool = AnnotKind.Mosaic; break;
                case Keys.T: _tool = AnnotKind.Text; break;
                case Keys.O: _tool = AnnotKind.Ocr; break;      // O = 取字（OCR）
                case Keys.B: _textBg = !_textBg; SaveTextBg(); break;
                case Keys.OemOpenBrackets: ResizeShape(_sel, -1f); return true;
                case Keys.OemCloseBrackets: ResizeShape(_sel, 1f); return true;
                case Keys.Delete:
                case Keys.Back:
                    if (_sel != null)
                    {
                        _shapes.Remove(_sel);
                        if (_sel.Cache != null) { try { _sel.Cache.Dispose(); } catch { } }
                        _sel = null;
                        Invalidate();
                        return true;
                    }
                    return false;
                case Keys.Escape:
                    if (_sel != null) { _sel = null; Invalidate(); return true; }   // 先取消选中，再按一次才是取消截图
                    return false;
                case Keys.D1: case Keys.NumPad1: _annotColor = AnnotColors[0]; break;
                case Keys.D2: case Keys.NumPad2: _annotColor = AnnotColors[1]; break;
                case Keys.D3: case Keys.NumPad3: _annotColor = AnnotColors[2]; break;
                case Keys.D4: case Keys.NumPad4: _annotColor = AnnotColors[3]; break;
                default: return false;
            }
            Invalidate();
            return true;
        }

        // "取字中…"的小提示：识别在后台跑，但得让用户看见"它在干活"（不显示的话还是像卡住）
        void PaintOcrBusy(Graphics g)
        {
            if (!_ocrBusy) return;
            string txt = "取字中…";
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Font f = new Font("Microsoft YaHei UI", 11f * _k, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(txt, f);
                int pad = (int)(14 * _k);
                int w = (int)sz.Width + pad * 2, h = (int)sz.Height + pad;
                Rectangle box = new Rectangle(_vs.Width / 2 - w / 2, 24, w, h);
                using (GraphicsPath bp = Gfx.Round(box, 9f * _k))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(238, 22, 24, 28)))
                    g.FillPath(b, bp);
                using (SolidBrush tb = new SolidBrush(Color.White))
                    g.DrawString(txt, f, tb, box.X + pad, box.Y + pad / 2f);
            }
        }

        void SaveTextBg()
        {
            if (_set == null) return;
            try { _set.TextBg = _textBg; _set.Save(); } catch { }
        }

        // 取字（OCR）：识别整块选区里的文字。
        // 更准的用法是选「字」工具拖一个框（DoOcrRegion）—— 框小一点、只圈文字，识别率明显更好。
        internal void DoOcr()
        {
            if (!_hasSel || _shot == null || _sz.Width < 4 || _sz.Height < 4) return;
            EndText(true);
            Bitmap crop = null;
            try { crop = CropSelection(false); } catch { }
            if (crop != null) StartOcrAsync(crop);
        }

        // 拖出来的框里取字：从**原图**（不带标注）裁这一块去认，框越贴合文字越准
        internal void DoOcrRegion(RectangleF rect)
        {
            if (!_hasSel || _shot == null) return;
            Rectangle rc = ToRect(rect);
            if (rc.Width < 10 || rc.Height < 10) { DoOcr(); return; }      // 只是点了一下：认整块选区
            rc = Rectangle.Intersect(rc, new Rectangle(0, 0, _shot.Width, _shot.Height));
            if (rc.Width < 4 || rc.Height < 4) return;
            EndText(true);
            try
            {
                Bitmap crop = new Bitmap(rc.Width, rc.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(crop)) g.DrawImageUnscaled(_shot, -rc.Left, -rc.Top);
                StartOcrAsync(crop);
            }
            catch (Exception ex) { Err.Log("OcrCrop", ex); }
        }

        bool _ocrBusy = false;

        // 取字丢到后台线程去做 —— 以前是同步跑的：界面整整卡 100~300ms、鼠标变等待圈、
        // 还没有任何反馈，用户当然觉得"性能垃圾"。现在轮盘照常能用，识别完结果框自己弹出来。
        void StartOcrAsync(Bitmap crop)
        {
            if (crop == null) return;
            if (_ocrBusy) { try { crop.Dispose(); } catch { } return; }     // 上一次还没完，直接忽略这一次
            _ocrBusy = true;

            // 先在 UI 线程把像素拷出来：后台线程就完全不碰 GDI 位图了
            byte[] px = null; int pw = 0, ph = 0;
            try { px = Ocr.PixelsOf(crop, out pw, out ph); } catch { px = null; }
            if (px == null) { try { crop.Dispose(); } catch { } _ocrBusy = false; return; }
            try { crop.Dispose(); } catch { }        // 像素到手，位图就可以扔了

            Invalidate();                            // 让"取字中…"立刻显示出来
            byte[] data = px; int w = pw, h = ph;
            System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate()
            {
                string err = null, txt = null;
                try { txt = Ocr.RecognizePixels(data, w, h, out err); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        _ocrBusy = false;
                        ShowOcrResult(txt, err);
                    }));
                }
                catch { _ocrBusy = false; }
            }));
            th.IsBackground = true;
            th.Start();
        }

        void ShowOcrResult(string txt, string err)
        {
            if (txt == null)
            {
                try
                {
                    MessageBox.Show(this, err ?? "识别失败了", "取字", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
                return;
            }
            try
            {
                bool wasTop = TopMost;
                TopMost = false;
                using (OcrForm of = new OcrForm(txt))
                {
                    of.TopMost = true;
                    of.ShowDialog(this);
                }
                TopMost = wasTop;
            }
            catch (Exception ex) { Err.Log("OcrForm", ex); }
        }

        void Undo()
        {
            if (_shapes.Count == 0) return;
            Shape last = _shapes[_shapes.Count - 1];
            _shapes.RemoveAt(_shapes.Count - 1);
            if (last.Cache != null) { try { last.Cache.Dispose(); } catch { } }
            if (_sel == last) _sel = null;
            Invalidate();
        }

        // ---------- 文字工具 ----------
        void BeginText(Point at)
        {
            EndText(true);
            _textBox = new TextBox();
            _textBox.Font = new Font("Microsoft YaHei UI", Math.Max(9f, _textSize * _k), FontStyle.Bold);
            _textBox.ForeColor = _annotColor;
            _textBox.BackColor = Color.White;      // 不能给带透明度的颜色，WinForms 控件不支持
            _textBox.BorderStyle = BorderStyle.FixedSingle;
            _textBox.Location = new Point(at.X, Math.Max(0, at.Y));
            _textBox.Width = (int)(170 * _k);
            _textBox.KeyDown += new KeyEventHandler(delegate(object o, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; EndText(true); }
                else if (ke.KeyCode == Keys.Escape) { ke.SuppressKeyPress = true; EndText(false); }
            });
            // 点到别处（比如去点工具条）也要把字落下，不然打好的字会莫名其妙丢掉
            _textBox.Leave += new EventHandler(delegate(object o, EventArgs e2) { EndText(true); });
            Controls.Add(_textBox);
            _textBox.Focus();
        }

        // 新文字用的字号：跟着上一个文字走（改过一次就不用每次再调）
        float _textSize = 20f;

        // commit=true 且非空 -> 落成一个文字标注，并自动选中它（接着就能拖动/改字号）
        void EndText(bool commit)
        {
            if (_textBox == null) return;
            TextBox tb = _textBox;
            _textBox = null;
            string txt = tb.Text;
            Point at = tb.Location;
            try { Controls.Remove(tb); tb.Dispose(); } catch { }
            if (commit && !string.IsNullOrEmpty(txt))
            {
                Shape s = new Shape();
                s.Kind = AnnotKind.Text;
                s.A = new PointF(at.X, at.Y);
                s.Text = txt;
                s.Color = _annotColor;
                s.Size = _textSize;
                _shapes.Add(s);
                _sel = s;                       // 画完就选中：可以直接拖 / 滚轮改大小
                _annotHint = false;
            }
            Invalidate();
        }

        // 选了字号后，下一个新文字也用它
        internal void SetNextTextSize(float size)
        {
            _textSize = Math.Max(9f, Math.Min(160f, size));
            if (_sel != null && _sel.Kind == AnnotKind.Text)
            {
                _sel.Size = _textSize;
                Invalidate();
            }
        }
    }
}

namespace SnapWheel
{
    // 贴图（图钉）：把一张图钉在屏幕上，随时对照着看 —— Snipaste 的招牌能力，也是"截图之后
    // 拿来用"这条主线上最自然的一步：不用再拖来拖去，看的时候它就在那儿。
    // 入口：轮盘上中键单击一张缩略图（左键=拖出去、右键=删除、双击=复制，中键是空的）。
    // 交互：左键拖 = 移动；滚轮 = 缩放（以光标为锚点）；双击 / Esc / 右上角 × = 关掉。
    class PinForm : Form
    {
        public const float MinZoom = 0.10f;
        public const float MaxZoom = 4.00f;
        const int Pad = 1;                       // 1px 描边，浅色背景下也能看清边界

        readonly Bitmap _img;
        float _zoom = 1f;
        bool _drag = false;
        Point _dragFrom;
        Point _formFrom;
        bool _closeHover = false;

        public float Zoom { get { return _zoom; } }
        public Bitmap Image { get { return _img; } }

        public PinForm(Bitmap img, Point at)
        {
            // 自己留一份：图钉要活得比轮盘上那张缩略图久（轮盘里删掉了它也该还钉着）
            try { _img = new Bitmap(img); } catch { _img = img; }

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            // 必须关掉自动缩放：贴图要的是"1 个像素就是 1 个像素"，
            // 默认的 Font 缩放会按字体/DPI 把窗口尺寸改掉（测出来 122 变 136）
            AutoScaleMode = AutoScaleMode.None;
            // 系统的"最小窗口宽度"是 136px（SM_CXMINTRACK），不显式设 MinimumSize 的话，
            // 小图会被撑到 136 宽、右边多出一条白边 —— 40x30 这种小截图就废了
            MinimumSize = new Size(16, 16);
            Text = "SnapWheel 贴图";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            ApplyZoom(1f, false);
            Location = ClampToScreen(new Point(at.X - Width / 2, at.Y - Height / 2));

            // 比屏幕还大的图（整屏截图之类）：开机先缩到能放进屏幕，别一贴上来糊满整个桌面。
            // 只缩不放 —— 小图还是原尺寸贴。
            try
            {
                Rectangle wa = Screen.FromPoint(at).WorkingArea;
                float fit = 1f;
                if (_img.Width > wa.Width * 0.9f) fit = Math.Min(fit, wa.Width * 0.9f / _img.Width);
                if (_img.Height > wa.Height * 0.9f) fit = Math.Min(fit, wa.Height * 0.9f / _img.Height);
                if (fit < 1f) { ApplyZoom(fit, false); Location = ClampToScreen(new Point(at.X - Width / 2, at.Y - Height / 2)); }
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { Activate(); } catch { }        // 要能接住 Esc
        }

        public void SetZoom(float z) { ApplyZoom(z, true); }

        // 缩放：以窗口中心为准（滚轮那条走 OnMouseWheel 的光标锚点版本）
        void ApplyZoom(float z, bool keepCenter)
        {
            if (z < MinZoom) z = MinZoom;
            if (z > MaxZoom) z = MaxZoom;
            Point c = new Point(Left + Width / 2, Top + Height / 2);
            _zoom = z;
            int w = Math.Max(12, (int)Math.Round(_img.Width * _zoom) + Pad * 2);
            int h = Math.Max(12, (int)Math.Round(_img.Height * _zoom) + Pad * 2);
            ClientSize = new Size(w, h);
            if (keepCenter) Location = ClampToScreen(new Point(c.X - Width / 2, c.Y - Height / 2));
            Invalidate();
        }

        // 别让它跑到屏幕外面去（两块屏 / 拔掉显示器之后重新摆位都靠这个兜底）
        Point ClampToScreen(Point p)
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            int x = Math.Max(vs.Left, Math.Min(p.X, vs.Right - Width));
            int y = Math.Max(vs.Top, Math.Min(p.Y, vs.Bottom - Height));
            return new Point(x, y);
        }

        Rectangle CloseRect()
        {
            int s = 22;
            return new Rectangle(Width - s - 4, 4, s, s);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.White);               // 带透明的图也有个白底，不至于透出桌面内容
            // 按图片自己的缩放尺寸画，别按窗口大小拉伸 —— 窗口万一被系统撑大，图也不该变形
            int iw = Math.Max(1, (int)Math.Round(_img.Width * _zoom));
            int ih = Math.Max(1, (int)Math.Round(_img.Height * _zoom));
            g.DrawImage(_img, new Rectangle(Pad, Pad, iw, ih));
            using (Pen bp = new Pen(Color.FromArgb(190, 70, 74, 84), 1f))
                g.DrawRectangle(bp, 0, 0, Width - 1, Height - 1);

            if (_closeHover && Width > 40 && Height > 40)
            {
                Rectangle r = CloseRect();
                using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 38, 42, 50)))
                    g.FillEllipse(b, r);
                using (Pen p = new Pen(Color.White, 1.8f))
                {
                    int m = 7;
                    g.DrawLine(p, r.Left + m, r.Top + m, r.Right - m, r.Bottom - m);
                    g.DrawLine(p, r.Right - m, r.Top + m, r.Left + m, r.Bottom - m);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_closeHover && CloseRect().Contains(e.Location)) { Close(); return; }
            _drag = true;
            _dragFrom = PointToScreen(e.Location);
            _formFrom = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = Width > 40 && Height > 40 && CloseRect().Contains(e.Location);
            if (h != _closeHover) { _closeHover = h; Invalidate(); }
            try { Cursor = h ? Cursors.Hand : (_drag ? Cursors.SizeAll : Cursors.Default); } catch { }
            if (!_drag) return;
            Point now = PointToScreen(e.Location);
            Location = new Point(_formFrom.X + (now.X - _dragFrom.X), _formFrom.Y + (now.Y - _dragFrom.Y));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = false;
            try { Cursor = Cursors.Default; } catch { }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
            // 以光标为锚点：光标底下那块内容原地不动，缩放才不会"跑"
            Point anchorScreen = PointToScreen(e.Location);
            float relX = (e.Location.X - Pad) / Math.Max(0.01f, _img.Width * _zoom);
            float relY = (e.Location.Y - Pad) / Math.Max(0.01f, _img.Height * _zoom);
            ApplyZoom(_zoom * factor, false);
            Location = ClampToScreen(new Point(
                (int)Math.Round(anchorScreen.X - Pad - relX * _img.Width * _zoom),
                (int)Math.Round(anchorScreen.Y - Pad - relY * _img.Height * _zoom)));
        }

        protected override void OnDoubleClick(EventArgs e) { Close(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _img != null) { try { _img.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}

namespace SnapWheel
{
    // 取字（OCR）：用 Windows 10/11 系统自带的 Windows.Media.Ocr —— 就是系统"截图工具"里
    // 那个"文本操作"用的同一套引擎。**不引入任何第三方库**，"一个 exe、零依赖"这条不破。
    //
    // 为什么代码长这样：它是 WinRT 组件，而我们是 csc 直接编译、机器上没有 Windows SDK 的
    // winmd（没法在编译期引用那些类型）。所以：
    //   · 类型全部用 Type.GetType("…, Windows.Foundation, ContentType=WindowsRuntime") 在运行时拿
    //   · 异步用 System.Runtime.WindowsRuntime（.NET 框架自带，不是第三方）里的 AsTask 桥接成同步
    //   · 图片走 内存流(PNG) → WinRT 随机访问流 → BitmapDecoder → SoftwareBitmap
    // 任何一步失败都返回 null + 一句人话，绝不把异常抛到界面上。
    static class Ocr
    {
        static bool _probed;
        static object _engine;
        static string _lang = "";
        static string _why = "";

        public static bool Available { get { Probe(); return _engine != null; } }
        public static string Language { get { Probe(); return _lang; } }
        public static string Why { get { Probe(); return _why; } }

        static Type WinRT(string name)
        {
            try { return Type.GetType(name + ", Windows.Foundation, ContentType=WindowsRuntime"); }
            catch { return null; }
        }

        static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                Type t = WinRT("Windows.Media.Ocr.OcrEngine");
                if (t == null) { _why = "这台系统没有 OCR 组件（需要 Windows 10 及以上）"; return; }

                // 1) 按系统/用户语言直接来一个
                MethodInfo fromUser = t.GetMethod("TryCreateFromUserProfileLanguages", BindingFlags.Public | BindingFlags.Static);
                if (fromUser != null)
                {
                    try { _engine = fromUser.Invoke(null, null); } catch { _engine = null; }
                }
                if (_engine != null) { _lang = LangOf(_engine); return; }

                // 2) 退而求其次：从系统已装的识别语言里挑一个（优先中文）
                object list = null;
                PropertyInfo prop = t.GetProperty("AvailableRecognizerLanguages", BindingFlags.Public | BindingFlags.Static);
                if (prop != null) { try { list = prop.GetValue(null, null); } catch { } }
                if (list == null) { _why = "系统没有安装任何 OCR 识别语言（设置 → 时间和语言 → 语言 → 该语言的「可选功能」里勾选「光学字符识别」）"; return; }

                List<object> langs = new List<object>();
                System.Collections.IEnumerable en = list as System.Collections.IEnumerable;
                if (en != null) { foreach (object o in en) langs.Add(o); }
                else
                {
                    // 有些投影只给 Size/GetAt
                    PropertyInfo size = list.GetType().GetProperty("Size");
                    MethodInfo getAt = list.GetType().GetMethod("GetAt");
                    if (size != null && getAt != null)
                    {
                        int n = (int)size.GetValue(list, null);
                        for (int i = 0; i < n; i++) langs.Add(getAt.Invoke(list, new object[] { i }));
                    }
                }
                MethodInfo fromLang = t.GetMethod("TryCreateFromLanguage", BindingFlags.Public | BindingFlags.Static);
                object pick = null;
                for (int i = 0; i < langs.Count; i++)
                    if (LangOf(langs[i]).StartsWith("zh")) { pick = langs[i]; break; }
                if (pick == null && langs.Count > 0) pick = langs[0];
                if (pick == null) { _why = "系统没有安装任何 OCR 识别语言"; return; }
                if (fromLang != null) { try { _engine = fromLang.Invoke(null, new object[] { pick }); } catch { } }
                if (_engine != null) _lang = LangOf(_engine);
                else _why = "OCR 引擎创建失败（语言包可能不完整）";
            }
            catch (Exception ex)
            {
                _why = "OCR 不可用：" + ex.Message;
            }
        }

        static string LangOf(object langObj)
        {
            try
            {
                if (langObj == null) return "";
                PropertyInfo p = langObj.GetType().GetProperty("LanguageTag");
                if (p != null) return (string)p.GetValue(langObj, null) ?? "";
                PropertyInfo pl = langObj.GetType().GetProperty("RecognizerLanguage");
                if (pl != null)
                {
                    object l = pl.GetValue(langObj, null);
                    if (l != null) return (string)l.GetType().GetProperty("LanguageTag").GetValue(l, null) ?? "";
                }
            }
            catch { }
            return "";
        }

        // IAsyncOperation<T> -> 同步拿结果（用 System.Runtime.WindowsRuntime 的 AsTask 桥接）
        static object Await(object op, string resultTypeName, int timeoutMs)
        {
            if (op == null) return null;
            Type resType = WinRT(resultTypeName);
            MethodInfo asTask = null;
            foreach (MethodInfo mi in typeof(System.WindowsRuntimeSystemExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (mi.Name != "AsTask" || !mi.IsGenericMethod) continue;
                if (mi.GetGenericArguments().Length != 1) continue;
                ParameterInfo[] ps = mi.GetParameters();
                if (ps.Length != 1) continue;
                asTask = mi;
                break;
            }
            if (asTask == null) throw new Exception("找不到 AsTask 桥接方法");
            System.Threading.Tasks.Task task = (System.Threading.Tasks.Task)asTask.MakeGenericMethod(resType).Invoke(null, new object[] { op });
            if (!task.Wait(timeoutMs)) throw new Exception("识别超时");
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        static object RandomAccessStreamOf(byte[] bytes)
        {
            // 同一程序集里的 WindowsRuntimeStreamExtensions（按程序集名 Type.GetType 解析不到，就直接从这个程序集里取）
            Type ext = typeof(System.WindowsRuntimeSystemExtensions).Assembly.GetType("System.IO.WindowsRuntimeStreamExtensions");
            if (ext == null) throw new Exception("缺少 System.Runtime.WindowsRuntime");
            MethodInfo m = ext.GetMethod("AsRandomAccessStream", new Type[] { typeof(Stream) });
            if (m == null) throw new Exception("找不到 AsRandomAccessStream");
            MemoryStream ms = new MemoryStream(bytes, false);
            return m.Invoke(null, new object[] { ms });
        }

        // 直接把像素喂给 OCR：省掉"位图→PNG→再解码"这一趟来回。
        // 这一步是给后台线程用的 —— 传进来的是已经拷好的 BGRA 字节，后台线程不碰任何 GDI 对象。
        static object SoftwareBitmapFromPixels(byte[] bgra, int w, int h)
        {
            Type bufExt = typeof(System.WindowsRuntimeSystemExtensions).Assembly
                .GetType("System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions");
            if (bufExt == null) return null;
            MethodInfo asBuffer = bufExt.GetMethod("AsBuffer", new Type[] { typeof(byte[]) });
            if (asBuffer == null) return null;
            object ibuf = asBuffer.Invoke(null, new object[] { bgra });

            Type sbT = WinRT("Windows.Graphics.Imaging.SoftwareBitmap");
            Type fmtT = WinRT("Windows.Graphics.Imaging.BitmapPixelFormat");
            Type alphaT = WinRT("Windows.Graphics.Imaging.BitmapAlphaMode");
            Type ibufT = WinRT("Windows.Storage.Streams.IBuffer");
            if (sbT == null || fmtT == null || alphaT == null || ibufT == null) return null;
            MethodInfo create = sbT.GetMethod("CreateCopyFromBuffer", new Type[] { ibufT, fmtT, typeof(int), typeof(int), alphaT });
            if (create == null) return null;
            object fmt = Enum.Parse(fmtT, "Bgra8");
            object alpha = Enum.Parse(alphaT, "Premultiplied");
            return create.Invoke(null, new object[] { ibuf, fmt, w, h, alpha });
        }

        // 把一张位图的像素拷成 BGRA 字节（在 UI 线程调用，之后可以安全地丢给后台线程）
        public static byte[] PixelsOf(Bitmap bmp, out int w, out int h)
        {
            w = bmp.Width; h = bmp.Height;
            Rectangle rc = new Rectangle(0, 0, w, h);
            BitmapData d = bmp.LockBits(rc, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int stride = d.Stride;
                byte[] raw = new byte[Math.Abs(stride) * h];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, raw, 0, raw.Length);
                // 去掉行尾填充，拼成紧凑的 w*4 每行（WinRT 那边要求连续）
                byte[] packed = new byte[w * 4 * h];
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(raw, y * Math.Abs(stride), packed, y * w * 4, w * 4);
                return packed;
            }
            finally { bmp.UnlockBits(d); }
        }

        // 识别已经拷好的像素（后台线程可调）
        public static string RecognizePixels(byte[] bgra, int w, int h, out string error)
        {
            error = null;
            Probe();
            if (_engine == null) { error = _why; return null; }
            try
            {
                object sw = SoftwareBitmapFromPixels(bgra, w, h);
                if (sw == null) { error = "这台系统不支持直接把像素交给 OCR"; return null; }
                float wordH;
                string txt = RecognizeSoftwareBitmap(sw, out error, out wordH);
                if (txt == null) return null;

                // 字太小就放大再认一遍 —— 这是准确率的关键。
                // 实测（900x380 合成图，字符级准确率）：14px 的字在 1x 下只有 25%，放大 2 倍到 92%；
                // 20px 是 28% -> 96%；连 32px 低对比度也是 13% -> 99%。屏幕截图里的正文多半就是
                // 14~20px，所以"不准"基本都是这个原因。
                // 判据两条，缺一不可：
                //   · 量到了文字框高度且偏小（< 24px）→ 按高度算放大倍数
                //   · 第一遍"认出来的字太少"（含什么都没认出来）→ 高度也不可信，直接上 2 倍
                string bigger = null;
                float k = 2f;
                double area = (double)w * h;
                if (area * k * k > 8.0e6) k = (float)Math.Sqrt(8.0e6 / area);
                if (k < 1f) k = 1f;
                if (k > 1.15f)
                {
                    if (k < 1.5f) k = 1.5f;
                    if (k > 3f) k = 3f;
                    int nw = (int)(w * k), nh = (int)(h * k);
                    if (nw <= 10000 && nh <= 10000 && nw * nh < 40 * 1000 * 1000)
                    {
                        byte[] scaled = ScalePixels(bgra, w, h, nw, nh);
                        if (scaled != null)
                        {
                            object sw2 = SoftwareBitmapFromPixels(scaled, nw, nh);
                            if (sw2 != null)
                            {
                                string e2 = null; float h2;
                                string t2 = RecognizeSoftwareBitmap(sw2, out e2, out h2);
                                // 放大后认出的字更多就更可信（实测基本都更多）
                                if (t2 != null) bigger = t2;
                            }
                        }
                    }
                }
                return bigger ?? txt;
            }
            catch (Exception ex)
            {
                Exception real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                error = real.Message;
                try { Err.Log("Ocr", real); } catch { }
                return null;
            }
        }

        static int Chars(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            for (int i = 0; i < s.Length; i++) if (!char.IsWhiteSpace(s[i])) n++;
            return n;
        }

        // 像素放大（自己算，不用 GDI 位图 —— 这样后台线程完全不碰 GDI）。
        // 双线性足够：OCR 要的是"字够大"，不是像素级完美。
        // 放大像素：用 GDI+ 的高质量双三次（自己写的双线性更糊，低对比度文字会被糊掉 —— 实测差很多）。
        // 这里在后台线程里**新建**位图、用完就扔，不碰任何别的线程的 GDI 对象，所以是安全的。
        static byte[] ScalePixels(byte[] src, int w, int h, int nw, int nh)
        {
            try
            {
                using (Bitmap small = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    BitmapData d = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        int stride = d.Stride;
                        byte[] row = new byte[w * 4];
                        for (int y = 0; y < h; y++)
                        {
                            Buffer.BlockCopy(src, y * w * 4, row, 0, w * 4);
                            System.Runtime.InteropServices.Marshal.Copy(row, 0, (IntPtr)((long)d.Scan0 + (long)y * stride), w * 4);
                        }
                    }
                    finally { small.UnlockBits(d); }

                    using (Bitmap big = new Bitmap(nw, nh, PixelFormat.Format32bppPArgb))
                    {
                        using (Graphics g = Graphics.FromImage(big))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(small, new Rectangle(0, 0, nw, nh));
                        }
                        int aw, ah;
                        return PixelsOf(big, out aw, out ah);
                    }
                }
            }
            catch { return null; }
        }
        // 识别一张图里的文字。成功返回文字（可能为空串 = 图上没字），失败返回 null 并给出 error
        public static string Recognize(Bitmap bmp, out string error)
        {
            error = null;
            if (bmp == null) { error = "没有图"; return null; }
            Probe();
            if (_engine == null) { error = _why; return null; }
            try
            {
                // 引擎对超大图有上限（MaxImageDimension，一般 10000），超过就先缩一下
                Bitmap work = bmp;
                bool own = false;
                try
                {
                    int maxDim = 10000;
                    PropertyInfo mp = WinRT("Windows.Media.Ocr.OcrEngine").GetProperty("MaxImageDimension", BindingFlags.Public | BindingFlags.Static);
                    if (mp != null) { object v = mp.GetValue(null, null); if (v is int) maxDim = (int)v; }
                    if (bmp.Width > maxDim || bmp.Height > maxDim)
                    {
                        double k = Math.Min((double)maxDim / bmp.Width, (double)maxDim / bmp.Height);
                        work = new Bitmap(bmp, new Size(Math.Max(1, (int)(bmp.Width * k)), Math.Max(1, (int)(bmp.Height * k))));
                        own = true;
                    }
                }
                catch { }

                // 首选：直接把像素交过去（省掉 PNG 编码/解码那 6~20ms）
                int pw = 0, ph = 0;
                byte[] px = null;
                try { px = PixelsOf(work, out pw, out ph); } catch { px = null; }
                if (own) { try { work.Dispose(); } catch { } }
                if (px != null)
                {
                    string r = RecognizePixels(px, pw, ph, out error);
                    if (r != null || error == null) return r;
                    // 直接喂像素失败就退回老路（PNG）
                }

                byte[] png;
                using (MemoryStream ms = new MemoryStream())
                {
                    work.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }

                Type decT = WinRT("Windows.Graphics.Imaging.BitmapDecoder");
                MethodInfo create = decT.GetMethod("CreateAsync", BindingFlags.Public | BindingFlags.Static, null, new Type[] { WinRT("Windows.Storage.Streams.IRandomAccessStream") }, null);
                if (create == null) throw new Exception("找不到 BitmapDecoder.CreateAsync");
                object decoder = Await(create.Invoke(null, new object[] { RandomAccessStreamOf(png) }), "Windows.Graphics.Imaging.BitmapDecoder", 15000);
                MethodInfo getSb = decoder.GetType().GetMethod("GetSoftwareBitmapAsync", Type.EmptyTypes);   // 它有 4 个重载，必须指定"无参"那个
                object sw = Await(getSb.Invoke(decoder, null), "Windows.Graphics.Imaging.SoftwareBitmap", 15000);
                float mh;
                return RecognizeSoftwareBitmap(sw, out error, out mh);
            }
            catch (Exception ex)
            {
                Exception real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                error = real.Message;
                try { Err.Log("Ocr", real); } catch { }
                return null;
            }
        }

        static string RecognizeSoftwareBitmap(object sw, out string error, out float medianWordHeight)
        {
            error = null;
            medianWordHeight = 0f;
            try
            {
                MethodInfo rec = _engine.GetType().GetMethod("RecognizeAsync", new Type[] { WinRT("Windows.Graphics.Imaging.SoftwareBitmap") });
                object result = Await(rec.Invoke(_engine, new object[] { sw }), "Windows.Media.Ocr.OcrResult", 30000);
                try { ((IDisposable)sw).Dispose(); } catch { }

                // 按行拼（比整段 Text 更接近原文排版），顺便量一下文字框高度（判断"字有多小"）
                StringBuilder sb = new StringBuilder();
                System.Collections.Generic.List<float> hs = new System.Collections.Generic.List<float>();
                object lines = null;
                PropertyInfo lp = result.GetType().GetProperty("Lines");
                if (lp != null) lines = lp.GetValue(result, null);
                System.Collections.IEnumerable le = lines as System.Collections.IEnumerable;
                if (le != null)
                {
                    foreach (object line in le)
                    {
                        PropertyInfo tp = line.GetType().GetProperty("Text");
                        object t = tp == null ? null : tp.GetValue(line, null);
                        if (t != null) sb.AppendLine(((string)t).TrimEnd());

                        PropertyInfo wp = line.GetType().GetProperty("Words");
                        object words = wp == null ? null : wp.GetValue(line, null);
                        System.Collections.IEnumerable we = words as System.Collections.IEnumerable;
                        if (we == null) continue;
                        foreach (object word in we)
                        {
                            PropertyInfo bp = word.GetType().GetProperty("BoundingRect");
                            object box = bp == null ? null : bp.GetValue(word, null);
                            if (box == null) continue;
                            PropertyInfo hp = box.GetType().GetProperty("Height");
                            if (hp == null) continue;
                            object hv = hp.GetValue(box, null);
                            if (hv is float) hs.Add((float)hv);
                            else if (hv is double) hs.Add((float)(double)hv);
                        }
                    }
                }
                if (hs.Count > 0)
                {
                    hs.Sort();
                    medianWordHeight = hs[hs.Count / 2];
                }

                string text = sb.ToString().Trim();
                if (text.Length == 0)
                {
                    PropertyInfo tp = result.GetType().GetProperty("Text");
                    if (tp != null) { object t = tp.GetValue(result, null); if (t != null) text = ((string)t).Trim(); }
                }
                return TightenCjk(text);
            }
            catch (Exception ex)
            {
                Exception real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                error = real.Message;
                try { Err.Log("Ocr", real); } catch { }
                return null;
            }
        }

        // 开机后台热身：第一次取字经常要几百毫秒（引擎要激活），先在后台认一张小图把它焐热，
        // 用户第一次真用的时候就是 20ms 级别了。失败就失败，不影响任何功能。
        public static void WarmUpAsync()
        {
            try
            {
                System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate()
                {
                    try
                    {
                        if (!Available) return;
                        using (Bitmap b = new Bitmap(240, 64, PixelFormat.Format32bppPArgb))
                        {
                            using (Graphics g = Graphics.FromImage(b))
                            {
                                g.Clear(Color.White);
                                using (Font f = new Font("Microsoft YaHei UI", 14f))
                                using (SolidBrush br = new SolidBrush(Color.Black))
                                    g.DrawString("warm up 热身", f, br, 6, 6);
                            }
                            string e;
                            Recognize(b, out e);
                        }
                    }
                    catch { }
                }));
                th.IsBackground = true;
                try { th.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }
                th.Start();
            }
            catch { }
        }

        static bool IsCjk(char c)
        {
            return (c >= 0x3000 && c <= 0x303F)     // CJK 标点
                || (c >= 0x3400 && c <= 0x4DBF)     // 扩展 A
                || (c >= 0x4E00 && c <= 0x9FFF)     // 基本区
                || (c >= 0xF900 && c <= 0xFAFF)     // 兼容
                || (c >= 0xFF00 && c <= 0xFFEF);    // 全角
        }

        // Windows OCR 认中文时会逐字插空格（"本 周 报 告 已 发 出"），用的时候太难看，得拼回去。
        // 规则：空格两边只要有一边是中日韩字符（或标点）就去掉；数字之间也去掉（"1 2" -> "12"）；
        // 纯英文单词之间的空格保留（"Deadline is Friday"）。
        static string TightenCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\u3000')
                {
                    char p = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
                    char n = (i + 1 < s.Length) ? s[i + 1] : '\0';
                    if (IsCjk(p) || IsCjk(n)) continue;
                    if (char.IsDigit(p) && char.IsDigit(n)) continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}

namespace SnapWheel
{
    // 翻译：把 OCR 出来的文字一键翻成中文/英文。
    //
    // 为什么用 MyMemory：它是少数"不要 API key"的接口，实测这台机器能通；
    // Google 那个免费端点国内是 429/连不上。只发一个普通的 HTTPS GET，
    // 不碰系统网络设置、不改代理；失败就把原因原样告诉用户，绝不假装成功。
    static class Translate
    {
        const int MaxChunk = 420;      // 免费接口对单次请求长度有限制，长文切段
        // 一次请求的时限。原来 9 秒偏紧：长文要按段顺序发好几次，网络一慢就会看到
        // "翻译接口连不上：超时"（实测接口本身只要 1.2 秒，是偶发抖动把 9 秒吃掉了）。
        const int TimeoutMs = 15000;

        static bool IsCjk(char c)
        {
            return (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF)
                || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF);
        }

        // 中文占三成以上就当它是中文 -> 翻成英文；否则翻成中文
        public static bool LooksChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int cjk = 0, total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) continue;
                total++;
                if (IsCjk(c)) cjk++;
            }
            return total > 0 && cjk * 10 >= total * 3;
        }

        public static string TargetLabel(string text) { return LooksChinese(text) ? "英文" : "中文"; }

        // 成功返回译文；失败返回 null 并给出人话原因
        public static string Run(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) { error = "没有要翻译的文字"; return null; }
            string src = LooksChinese(text) ? "zh-CN" : "en";
            string dst = LooksChinese(text) ? "en" : "zh-CN";

            StringBuilder outp = new StringBuilder();
            string[] chunks = Split(text, MaxChunk);
            int empty = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                string one = One(chunks[i], src, dst, out error);
                if (one == null) return null;
                one = one.Trim();
                if (one.Length == 0) { empty++; continue; }
                if (outp.Length > 0) outp.Append('\n');       // 分段译完拼回去，一段一行
                outp.Append(one);
            }
            if (outp.Length == 0)
            {
                error = empty > 0 ? "接口没返回译文（多半是被限流了），过一会儿再试" : "没有要翻译的文字";
                return null;
            }
            return outp.ToString();
        }

        static string[] Split(string s, int max)
        {
            if (s.Length <= max) return new string[] { s };
            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();
            int start = 0;
            while (start < s.Length)
            {
                int len = Math.Min(max, s.Length - start);
                // 尽量在换行/句号/空格处断开，别把句子切两半
                if (start + len < s.Length)
                {
                    int cut = -1;
                    for (int k = start + len; k > start + max / 2; k--)
                    {
                        char c = s[k - 1];
                        if (c == '\n' || c == '。' || c == '！' || c == '？' || c == '.' || c == '!' || c == '?' || c == ' ') { cut = k; break; }
                    }
                    if (cut > start) len = cut - start;
                }
                parts.Add(s.Substring(start, len));
                start += len;
            }
            return parts.ToArray();
        }

        static string One(string text, string src, string dst, out string error)
        {
            error = null;
            try
            {
                // .NET Framework 默认只肯用 TLS 1.0/SSL3，现代接口一律要求 TLS 1.2 ——
                // 不设这句就会报"未能创建 SSL/TLS 安全通道"（实测就是这个错）
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) +
                             "&langpair=" + Uri.EscapeDataString(src) + "%7C" + Uri.EscapeDataString(dst) + "&de=snapwheel@example.com";
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                req.UserAgent = "SnapWheel/" + AppInfo.Version;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    string t = ReadResult(json, out error);
                    if (t == null) return null;
                    return t;
                }
            }
            catch (WebException wex)
            {
                error = "翻译接口连不上：" + (wex.Status == WebExceptionStatus.Timeout ? "超时" : wex.Message);
                return null;
            }
            catch (Exception ex) { error = "翻译失败：" + ex.Message; return null; }
        }

        // 从接口返回里读出结果：成功=译文（可能为空串，表示这一段没内容）；
        // 失败=null，并把"人话原因"写进 error。
        // 几种失败要分开，不然用户看到的永远是同一句"内容看不懂"：
        //   限流（MYMEMORY WARNING / responseDetails 带 LIMIT）-> 说清楚是被限流了
        //   别的错误状态 -> 把接口给的原因原样带出来
        internal static string ReadResult(string json, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(json)) { error = "接口没有返回内容"; return null; }

            string t = ExtractField(json, "translatedText");
            string details = ExtractField(json, "responseDetails");
            string status = ExtractRaw(json, "responseStatus");

            bool limited = (t != null && t.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0)
                        || (details != null && (details.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase) >= 0
                                             || details.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) >= 0));
            if (limited)
            {
                error = "免费翻译额度用完了（MyMemory 限流）——过一会儿再试";
                return null;
            }
            if (!string.IsNullOrEmpty(status) && status != "200")
            {
                error = "翻译接口报错：" + (string.IsNullOrEmpty(details) ? status : details);
                return null;
            }
            if (t == null) { error = "接口返回的内容看不懂（可能被限流了）"; return null; }
            return t.Trim();
        }

        // 读一个"可能是字符串也可能是数字"的字段（MyMemory 的 responseStatus 是不带引号的 200）
        internal static string ExtractRaw(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = json.IndexOf(':', k);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            int start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '.' || json[i] == '-')) i++;
            return i > start ? json.Substring(start, i - start) : null;
        }

        // 从 {"responseData":{"translatedText":"..."}} 里把那个字段抠出来（不引 JSON 库，
        // 只需要一个字段：先找 key，再按 JSON 字符串规则解转义）
        internal static string ExtractField(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = json.IndexOf(':', k);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    i += 2;
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 3 < json.Length)
                            {
                                int code;
                                if (int.TryParse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                                { sb.Append((char)code); i += 4; }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}

namespace SnapWheel
{
    // 轮盘主窗口：字段 / 构造 / 生命周期 / 布局 / 几何 / 对外接口。
    // 绘制、动画、毛玻璃背景、鼠标键盘与拖放分别在 61/62/63/64 几个 partial 里。
    partial class WheelForm : Form
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

        float _closeHoldP = 0f;                                           // 长按进度 0..1（画红色进度环）

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

        float _nubAppearT = 1f;        // 把手"出现"进度 0..1（启动时不要突然冒出来）

        DateTime _nubAppearAt = DateTime.MinValue;

        float _nubHintT = 0f;                            // 把手"点我展开/收起"提示的淡入进度

        bool _adminTipShown = false;                     // 管理员"拖不动"的说明每次运行只弹一次

        DateTime _firstRunHintUntil = DateTime.MinValue; // 首次运行自动亮提示的截止时刻

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

        // 光标是否还在关闭键附近（留 26px 余量：手抖不算离开，明显挪开才作废）
        bool _testIgnoreLeave = false;      // 测试用：跳过光标判断（合成事件时真实光标不在按钮上）
        bool CursorOverCloseButton()
        {
            if (_testIgnoreLeave) return true;
            try
            {
                Point cp = ToLogicalPt(PointToClient(Cursor.Position));
                Rectangle r = CloseButtonRect();
                r.Inflate(26, 26);
                return r.Contains(cp);
            }
            catch { return true; }   // 取不到就当作还在按钮上，别误取消
        }


        // 单把手模式：右下边那个把手不画、也不能点（任务栏自动隐藏时鼠标扫底边不会撞到它）
        public bool NubSingleMode() { return _settings.NubSingle; }

        bool CanExpandByNub()
        {
            return true;      // 不做冷却：收起后也可以立刻再展开（两个方向都随时可点）
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

            // 首次运行（展开状态）：让"点我收起"把手提示自动亮一次
            if (!_settings.NubHintDone)
            {
                _firstRunHintUntil = DateTime.Now.AddSeconds(14);
                _settings.NubHintDone = true;
            }
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
            if (Visible) { RequestBackdropAsync(); Render(); }
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

        // 万能键长按多久弹圆盘：从 260ms 收到 140ms（更跟手），仍能区分"点一下"和"长按"
        const int KeyMenuDelayMs = 140;

        float _menuT = 0f;             // 0..1 radial menu expansion

        bool _menuOpen = false;

        int _sector = -1;              // 0=上 1=右 2=下 3=左（动作可由用户自定义）

        bool _delConfirm = false;      // 删除确认态：摇杆左右两半 = 取消 / 确认

        DateTime _delConfirmAt = DateTime.MinValue;

        int _delHalf = -1;             // -1=不在键上 0=左半(取消) 1=右半(确认)

        Point _swipeStart;

        Color _accentCur = Color.FromArgb(0, 122, 204);

        float _switchFlash = 0f;

        Point _backdropOffset = new Point(0, 0);

        readonly object _glassLock = new object();
        int _frameNo = 0;                      // 第几帧（换底交叉淡入靠它做到"一帧只混一次"）
        readonly Dictionary<StoreItem, long> _cardSizeMemo = new Dictionary<StoreItem, long>();   // 上一帧每张卡片的尺寸（判断"能不能用贴片缓存"）


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


        void SwitchWheel(int dir)
        {
            if (dir > 0) _mgr.Next(); else _mgr.Prev();
            _mgr.Save();
            AfterWheelSwitch();
        }


        public void RemoveItem(StoreItem it, bool deleteFile)
        {
            if (it == null) return;
            // 先留一份"后悔药"（只留引用，不拷图）：托盘「撤销上一次删除」靠它
            if (deleteFile)
            {
                Wheel w = null;
                try { w = _mgr.ActiveWheel; } catch { }
                Undo.Push(w, new StoreItem[] { it }, false);
            }
            try { _store.Items.Remove(it); } catch { }
            try { _thumbCache.Remove(it); } catch { }
            try { _enterT0.Remove(it); } catch { }
            _scales.Clear();
            if (deleteFile)
            {
                try { if (it.FilePath != null && it.FilePath.Length > 0 && File.Exists(it.FilePath)) File.Delete(it.FilePath); }
                catch { }
                // 第一次删图时说清楚"还能找回来" —— 否则没人知道托里有这个后悔药
                if (_settings != null && !_settings.UndoHintDone)
                {
                    _settings.UndoHintDone = true;
                    try { _settings.Save(); } catch { }
                    ShowToast("已删掉这张 —— 托盘右键「撤销上一次删除」可以找回来");
                }
            }
            _hover = -1; _enlarged = -1; _peekIndex = -1;
            if (_targetOffset > Math.Max(0, _store.Items.Count - 1)) _targetOffset = Math.Max(0, _store.Items.Count - 1);
            if (_offset > _targetOffset) _offset = _targetOffset;
            _rendered = false;
            Render();
        }


        // 一键清空当前轮盘里的图片，轮盘本身保留
        public void ClearCurrentWheel()
        {
            int n = _store.Items.Count;
            try
            {
                StoreItem[] all = _store.Items.ToArray();
                // 清空也是一次删除：整批留一份，能整体撤回
                if (all.Length > 0)
                {
                    Wheel w = null;
                    try { w = _mgr.ActiveWheel; } catch { }
                    Undo.Push(w, new List<StoreItem>(all), true);
                }
                for (int i = 0; i < all.Length; i++)
                {
                    try { if (all[i].FilePath != null && File.Exists(all[i].FilePath)) File.Delete(all[i].FilePath); } catch { }
                }
                _store.Items.Clear();
                _thumbCache.Clear();
                _enterT0.Clear();
                _scales.Clear();
                _offset = 0f; _targetOffset = 0f; _hover = -1; _enlarged = -1; _peekIndex = -1;
                _deletingItem = null; _deleteProg = 0f;
                _rendered = false;
                Render();
                ShowToast(n > 0 ? ("已清空这一盘：" + n + " 张（托盘 → 撤销上一次删除 可以找回来）") : "这一盘本来就是空的");
            }
            catch (Exception ex) { Err.Log("ClearCurrentWheel", ex); }
        }


        // 设置窗口点了确定之后，界面上要做的收尾。
        // 抽成方法是为了能写行为测试 —— 以前这里曾混进一句 HideWheel()（收起态关掉时），
        // 结果每次点确定，轮盘都当场消失，而当时 150 项绘制测试一个都发现不了。
        public void AfterSettingsApplied()
        {
            ApplyTopMost();
            ApplyLayout();
            // 收起态被关掉时，别让轮盘卡在"只剩个把手"的状态里
            if (!_settings.CollapseMode && _collapsed) ExpandWheel();
            RefreshWheel();            // 强制重绘：万能键上的动作名等设置改完要立刻生效
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

        // 管理员模式下"拖了半天啥也没发生"时抛出去：让 AppCtx 弹说明（要不要换普通权限）
        public event EventHandler AdminHelpRequested;

        // 中键点缩略图：把这张图贴（钉）到屏幕上；at = 想钉的位置（屏幕坐标，图片以它为中心）
        public event Action<Bitmap, Point> PinRequested;


        IntPtr _memDc = IntPtr.Zero, _dib = IntPtr.Zero, _oldBmp = IntPtr.Zero, _bits = IntPtr.Zero;

        int _dibW, _dibH;


        // 圆盘上给动作名用的短标签（太长会画不下）
        static string KeyActionShort(string id)
        {
            switch (id)
            {
                case "new": return "新建";
                case "next": return "下一个";
                case "prev": return "上一个";
                case "delete": return "删除";
                case "shot": return "截图";
                case "collapse": return "收起";
                case "folder": return "文件夹";
                case "settings": return "设置";
                case "paste": return "收一张";
                case "clear": return "清空";
                default: return "";
            }
        }


        bool _dropActive = false;

        bool _returnedToWheel = false;

        bool _dropExternal = false;      // 拖进来的是“外面的文件”（不是轮盘自己的图）

        int _dropCount = 0;

        object _dropCacheKey = null;

        List<string> _dropCacheFiles = null;

        DateTime _dropCacheAt = DateTime.MinValue;

        const string DragFmt = "SnapWheelMove";     // 标记：这是轮盘自己在拖的图


        // 左下角的小提示条
        string _toast = "";

        DateTime _toastAt = DateTime.MinValue;

        public void ShowToast(string s)
        {
            _toast = s == null ? "" : s;
            _toastAt = DateTime.Now;
        }


        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 动画定时器必须停掉：不然窗口关掉之后它还每 15ms 醒一次，
                // 白烧 CPU、还会对着已经释放的窗口去重绘（测试里尤其明显：越跑越慢）
                try { if (_anim != null) { _anim.Stop(); _anim.Dispose(); _anim = null; } } catch { }
                try { ReleaseDib(); } catch { }
                try { FreeBackdrop(); } catch { }
                try { DropLayers(); } catch { }
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
}

namespace SnapWheel
{
    partial class WheelForm
    {
        void DrawWithAlpha(Graphics g, Bitmap bmp, RectangleF dest, int alpha)
        {
            // 目标尺寸跟位图**正好 1:1** 时绕开重采样：
            // 画布上开着 HighQualityBicubic，即便一张图是 1:1 贴上去，GDI+ 也会老老实实走双三次插值
            // —— 实测每张约 0.5ms（一张卡片三块贴片 + 一张缩略图 ≈ 2ms，8 张卡片一帧就是 13ms）。
            // 关键：**不能取整坐标、也不能复位变换**。取整会让贴片/缩略图相对卡片边框跳 1 个像素
            // （卡片边框是按精确小数坐标画的），用户看到的就是"缩略图边框抽搐"。
            // 所以保留变换，只在 1:1 时把插值换成最近邻：既省掉重采样，位置也跟原来一模一样。
            if (Math.Abs(dest.Width * UiK - bmp.Width) < 0.6f && Math.Abs(dest.Height * UiK - bmp.Height) < 0.6f)
            {
                InterpolationMode oldIm = g.InterpolationMode;
                PixelOffsetMode oldPo = g.PixelOffsetMode;
                try
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    if (alpha >= 250) g.DrawImage(bmp, dest);
                    else
                    {
                        ColorMatrix cm = new ColorMatrix();
                        cm.Matrix33 = Math.Max(0f, Math.Min(1f, alpha / 255f));
                        _ia.SetColorMatrix(cm);
                        g.DrawImage(bmp, new Rectangle((int)dest.X, (int)dest.Y, (int)dest.Width, (int)dest.Height),
                            0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, _ia);
                    }
                }
                finally
                {
                    g.InterpolationMode = oldIm;
                    g.PixelOffsetMode = oldPo;
                }
                return;
            }
            if (alpha >= 250) { g.DrawImage(bmp, dest); return; }
            ColorMatrix cm2 = new ColorMatrix();
            cm2.Matrix33 = Math.Max(0f, Math.Min(1f, alpha / 255f));
            _ia.SetColorMatrix(cm2);
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
            // w/h 是逻辑尺寸；实际按物理像素生成，缩放到高 DPI 屏上才不会发虚。
            // 注意：尺寸**不能量化**。量化会让"1:1 贴图"变成重采样贴图，实测反而更慢
            // （缩略图那一段 2.98 → 3.72ms），所以这里保持精确尺寸。
            int dw = Math.Max(1, (int)Math.Round(w * UiK));
            int dh = Math.Max(1, (int)Math.Round(h * UiK));
            Dictionary<long, Bitmap> d;
            if (!_thumbCache.TryGetValue(it, out d)) { d = new Dictionary<long, Bitmap>(); _thumbCache[it] = d; }
            long key = ((long)dw << 20) | (uint)dh;
            Bitmap b;
            if (d.TryGetValue(key, out b)) return b;

            // 没命中：优先拿"比目标略大的现成缩略图"当源。放大预览时尺寸每帧都在变，
            // 每帧都从原图重做一次高质量缩放会掉帧；从已经缩过的大图再缩下去便宜得多，
            // 画质几乎没差别（都是双三次，源本身也是高质量缩出来的）。
            Bitmap src = it.Image;
            long bestArea = 0;
            foreach (KeyValuePair<long, Bitmap> kv in d)
            {
                Bitmap cand = kv.Value;
                if (cand.Width >= dw && cand.Height >= dh)
                {
                    long area = (long)cand.Width * cand.Height;
                    if (bestArea == 0 || area < bestArea) { bestArea = area; src = cand; }
                }
            }

            if (d.Count > 64)
            {
                foreach (Bitmap v in d.Values) { try { v.Dispose(); } catch { } }
                d.Clear();
                src = it.Image;
            }
            b = new Bitmap(dw, dh, PixelFormat.Format32bppPArgb);
            using (Perf.Section("2c9-缩略图生成"))
            using (Graphics gg = Graphics.FromImage(b))
            {
                gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                gg.DrawImage(src, new Rectangle(0, 0, dw, dh));
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


        void Render()
        {
            if (!IsHandleCreated || !Visible) return;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try { RenderCore(); }
            finally
            {
                sw.Stop();
                FrameStats.Sample(sw.Elapsed.TotalMilliseconds, FrameState());
            }
        }


        // 慢帧要能说清"当时界面是什么状态"，否则只知道慢、不知道因为什么慢
        string FrameState()
        {
            return "show=" + _show.ToString("0.00") + " target=" + _targetShow.ToString("0.00") +
                   " 图=" + _store.Items.Count + " 缩略图缓存=" + _thumbCache.Count +
                   " offset=" + _offset.ToString("0.0") + "/" + _targetOffset.ToString("0.0") +
                   " hover=" + _hover + " 放大=" + _enlarged + " peek=" + _peekIndex +
                   " 菜单=" + _menuOpen + " intro=" + _intro + " 收起中=" + _collapsing + " 已收起=" + _collapsed +
                   " 删除中=" + (_deletingItem != null) + " 展开动画=" + _showAnimating + " 提示=" + (_toast.Length > 0) +
                   " 拖出=" + (_dragOutItem != null ? "进行中" : "无") + (_lastDragInfo.Length > 0 ? " 上次【" + _lastDragInfo + "】" : "");
        }

        string _lastDragInfo = "";      // 上一次拖出去的结果（只给日志看：格式 / 目标有没有接收）


        void RenderCore()
        {
            PruneCaches();
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;

            _frameNo++;
            EnsureDib(w, h);
            using (Graphics g = Graphics.FromHdc(_memDc))
            {
                using (Perf.Section("1-清屏"))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.Clear(Color.Transparent);                 // zero the reused DIB (no allocation)
                    g.CompositingMode = CompositingMode.SourceOver;
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                try
                {
                    using (Perf.Section("2-绘制内容")) DrawWheel(g, w, h);
                }
                catch (Exception ex) { Err.Log("DrawWheel", ex); }      // 画错一帧总好过整个程序崩掉
            }

            using (Perf.Section("3-推送窗口"))
            {
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
            }
            _rendered = true;
        }


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
            // 收起态不许画接住区：轮盘已经收成一个小把手，但这一片"看不见的 alpha=1 区域"
            // 仍然会让窗口吃住鼠标和拖放 —— 用户原话是"收起来了却好像还在这，挡着我点别的东西"。
            if (_collapsed) return;
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

            // 4) 摇杆点：中心点 + 圆盘展开时四个方向标出各自的动作名（当前指向的高亮）
            float dsz = 6.2f + 1.4f * kt;
            using (SolidBrush db = new SolidBrush(Color.FromArgb((int)((238 + 17 * hv) * a / 255f), 255, 255, 255)))
            {
                float cs = 7.4f + 2.2f * kt;
                g.FillEllipse(db, kcx - cs / 2f, kcy - cs / 2f, cs, cs);

                if (_menuT > 0.05f)
                {
                    float[] dx4 = { 0f, 1f, 0f, -1f };
                    float[] dy4 = { -1f, 0f, 1f, 0f };
                    float lr = rr * 0.60f;
                    using (Font kf = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold))
                    {
                        for (int q = 0; q < 4; q++)
                        {
                            string txt = KeyActionShort(_settings.KeyActionAt(q));
                            if (txt.Length == 0) continue;              // "不设置"就不画
                            float px = kcx + dx4[q] * lr, py = kcy + dy4[q] * lr;
                            bool act = (_sector == q);
                            float ka = _menuT * (_sector < 0 ? 0.78f : (act ? 1f : 0.42f));
                            SizeF ts = g.MeasureString(txt, kf);
                            if (act)                                     // 指向的那个：白底 + 深字
                            {
                                float hr = Math.Max(ts.Width, ts.Height) * 0.5f + 5f;
                                using (SolidBrush hb = new SolidBrush(Color.FromArgb((int)(215 * ka * a / 255f), 255, 255, 255)))
                                    g.FillEllipse(hb, px - hr, py - hr, hr * 2f, hr * 2f);
                            }
                            Color tc = act ? Color.FromArgb(26, 28, 34) : Color.FromArgb(255, 255, 255);
                            using (SolidBrush tb = new SolidBrush(Color.FromArgb((int)(250 * ka * a / 255f), tc.R, tc.G, tc.B)))
                                g.DrawString(txt, kf, tb, px - ts.Width / 2f, py - ts.Height / 2f);
                        }
                    }
                }
            }
        }


        void DrawWheel(Graphics g, int w, int h)
        {
            int a = (int)(255 * Math.Max(0f, Math.Min(1f, _show)));
            if (a <= 1) return;
            // 统一缩放：后面所有绘制都按逻辑坐标来，字体/图标/间距自动跟着 DPI 走
            if (Math.Abs(UiK - 1f) > 0.001f) g.ScaleTransform(UiK, UiK);
            PointF c = Center();

            using (Perf.Section("2a-接住区")) DrawDropCatcher(g);

            if (_collapsed)              // 收起态：只留边上那个小把手
            {
                DrawNubs(g, a);
                return;
            }

            // 环与控件都可能是"缓存好的一层"（稳态下整块贴图），见 TryBlitLayer
            bool backLayer = TryBlitLayer(g, 0);
            if (!backLayer)
            {
                using (Perf.Section("2b-环")) DrawRing(g, a);
                StoreLayer(0, g, a, false);
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
                using (Perf.Section("2c-缩略图"))
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
                    // 贴片缓存只在"卡片尺寸不动"时用：尺寸每帧都在变的话（放大预览 / 删除 / 拖动 / 收起动画），
                    // 每帧都会生成新贴片，反而比直接画更贵 —— 实测过这一版更慢，所以加了这道门槛。
                    // 判定标准是"尺寸跟上一帧一样吗"，而不是"缩放是不是 1"——
                    // 放大预览停在某个倍数上时尺寸同样稳定，也该用上贴片。
                    long sizeKey = ((long)iw << 20) | (uint)ih;
                    long lastSize;
                    bool sizeStable = _cardSizeMemo.TryGetValue(_store.Items[i], out lastSize) && lastSize == sizeKey;
                    _cardSizeMemo[_store.Items[i]] = sizeKey;
                    bool usePlate = sizeStable && _deletingItem == null && _dragOutItem == null
                                    && !_collapsing && !_intro && !_showAnimating && _show >= 0.999f;

                    // 阴影：新拟态用柔和的漫射阴影，纯扁平就一层淡淡的投影
                    // 缓存成贴片（同一尺寸/样式的卡片每帧画出来一模一样）
                    int shA = ShadowA(95);
                    if (shA > 2)
                    using (Perf.Section("2c1-阴影"))
                    {
                        float pad = 14f;
                        RectangleF area = new RectangleF(rr2.X - pad, rr2.Y - pad, rr2.Width + pad * 2, rr2.Height + pad * 2);
                        string key = "shd|" + (StyleFlatOnly() ? "f" : "n") + "|" + (int)rr2.Width + "|" + (int)rr2.Height +
                                     "|" + (int)rad + "|" + shA + "|" + (int)shOff;
                        Bitmap plate = PlateIf(usePlate, key, area, delegate(Graphics pg)
                        {
                            if (StyleFlatOnly())
                            {
                                using (GraphicsPath sh = Gfx.Round(new RectangleF(rr2.X, rr2.Y + shOff, rr2.Width, rr2.Height), rad))
                                using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(shA / 2.2f), 0, 0, 0)))
                                    pg.FillPath(sb, sh);
                            }
                            else
                            {
                                using (GraphicsPath sh = Gfx.Round(new RectangleF(rr2.X + 1f, rr2.Y + shOff * 1.4f, rr2.Width, rr2.Height), rad))
                                    Gfx.SoftShadow(pg, sh, (int)(shA / 2.4f), 2.6f);
                            }
                        });
                        if (plate != null) DrawWithAlpha(g, plate, area, ia);
                    }

                    using (GraphicsPath card = Gfx.Round(rr2, rad))
                    {
                        // 毛玻璃底（真背景）+ 新拟态的上下明暗边
                        using (Perf.Section("2c2-卡片底"))
                        {
                            BackdropClip(g, card, ia);
                            int baseA = GlassA(188);
                            Color fill = Gfx.A(GlassBase(), baseA);
                            string pkey = "pan|" + _settings.UiStyle + "|" + _settings.GlassPercent + "|" +
                                          (int)rr2.Width + "|" + (int)rr2.Height + "|" + (int)rad + "|" + baseA;
                            Bitmap plate = PlateIf(usePlate, pkey, rr2, delegate(Graphics pg)
                            {
                                using (GraphicsPath p2 = Gfx.Round(rr2, rad))
                                    Gfx.GlassPanel(pg, p2, rr2, fill,
                                        (StyleNeu() ? 34 : 16), (StyleNeu() ? 40 : 0), !StyleFlatOnly());
                            });
                            if (plate != null) DrawWithAlpha(g, plate, rr2, ia);
                            else
                            {
                                Color fill2 = Gfx.A(GlassBase(), (int)(baseA * ia / 255f));
                                Gfx.GlassPanel(g, card, rr2, fill2,
                                    (int)((StyleNeu() ? 34 : 16) * ia / 255f),
                                    (int)((StyleNeu() ? 40 : 0) * ia / 255f),
                                    !StyleFlatOnly());
                            }
                        }
                        if (_store.Items[i].Image != null)
                        using (Perf.Section("2c3-图片"))
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
                        using (Perf.Section("2c4-描边"))
                        {
                            Color bc;
                            if (hv) bc = Color.FromArgb(96, 170, 255);
                            else if (spec) bc = Color.FromArgb(245, 166, 35);     // amber = extreme aspect
                            else bc = Color.FromArgb(255, 255, 255);
                            float bw = hv ? 3f : (spec ? 2.2f : 1.4f);
                            int ba = hv ? 255 : (spec ? 240 : 170);
                            float bpad = bw + 3f;
                            RectangleF bArea = new RectangleF(rr2.X - bpad, rr2.Y - bpad, rr2.Width + bpad * 2, rr2.Height + bpad * 2);
                            string bkey = "brd|" + (int)rr2.Width + "|" + (int)rr2.Height + "|" + (int)rad + "|" +
                                          bc.ToArgb() + "|" + (int)(bw * 10) + "|" + ba;
                            Bitmap bplate = PlateIf(usePlate, bkey, bArea, delegate(Graphics pg)
                            {
                                using (GraphicsPath p3 = Gfx.Round(rr2, rad))
                                using (Pen bp = new Pen(Color.FromArgb(ba, bc.R, bc.G, bc.B), bw))
                                    pg.DrawPath(bp, p3);
                            });
                            if (bplate != null) DrawWithAlpha(g, bplate, bArea, ia);
                            else
                                using (Pen bp = new Pen(Color.FromArgb((int)(ba * ia / 255f), bc.R, bc.G, bc.B), bw))
                                    g.DrawPath(bp, card);
                        }
                    }
                }
            }

            // (the big preview is now just a larger card scale - no separate overlay, so no desync)

            bool frontLayer = TryBlitLayer(g, 1);
            if (!frontLayer)
            {
                using (Perf.Section("2d-控件")) DrawControls(g, a);
                StoreLayer(1, g, a, false);
            }
            DrawCountPill(g, a);       // 跟滚动位置绑定的那一个，不进缓存层
        }


        // ---- 贴边小把手：收起态画"拉出"、展开态画"收起" ----
        // 两个把手按环的进度交叉淡入淡出（并各自从屏幕边滑出来），不会"啪"地换一个
        void DrawNubs(Graphics g, int a)
        {
            float k = _collapsed ? 0f : (_intro ? _introT : 1f);    // 0=完全收起，1=完全展开
            float ap = _nubAppearT >= 1f ? 1f : 1f - (1f - _nubAppearT) * (1f - _nubAppearT);   // 出现用 easeOut
            if (NubSingleMode())
            {
                DrawNubOne(g, a, true, ap);                          // 只有一个把手，始终可见
                return;
            }
            if (k < 0.995f) DrawNubOne(g, a, true, (1f - k) * ap);
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

            // ---- 用途提示：首次运行自动亮一次，之后悬停才显示 ----
            // 以前把手只有三个点和一个小三角，新用户根本不知道它是干嘛的。
            float hintA = vis > 0.98f ? _nubHintT : 0f;
            if (hintA > 0.02f)
            {
                string ht = willExpand ? "点我展开" : "点我收起";
                using (Font hf = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                {
                    SizeF ts = g.MeasureString(ht, hf);
                    float pw = ts.Width + 16f, ph = ts.Height + 8f;
                    float hx, hy;
                    if (vertical)   // 竖直边上的把手：提示放到屏幕里侧（右边）
                    {
                        hx = rr.Right + 8f;
                        hy = rr.Y + rr.Height / 2f - ph / 2f;
                    }
                    else            // 水平边上的把手：提示放到屏幕里侧（上边）
                    {
                        hx = rr.X + rr.Width / 2f - pw / 2f;
                        hy = rr.Y - ph - 8f;
                    }
                    RectangleF pr3 = new RectangleF(hx, hy, pw, ph);
                    int ha = (int)(hintA * 245);
                    using (GraphicsPath hp2 = Gfx.Round(pr3, ph / 2f))
                    {
                        BackdropClip(g, hp2, ha);
                        using (SolidBrush hb = new SolidBrush(Color.FromArgb((int)(ha * 0.62f), 22, 24, 30)))
                            g.FillPath(hb, hp2);
                        using (Pen hpn = new Pen(Gfx.A(acc, (int)(ha * 0.55f)), 1.3f))
                            g.DrawPath(hpn, hp2);
                    }
                    using (SolidBrush htx = new SolidBrush(Color.FromArgb(ha, 255, 255, 255)))
                        g.DrawString(ht, hf, htx, hx + 8f, hy + 4f);
                }
            }
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

        // ---- 环（含接住区之外的环带、两端小圆点）----
        // 抽成方法是为了能整块缓存成"静态层"：稳态下每帧只贴一张图，不再一笔一笔重画。
        void DrawRing(Graphics g, int a)
        {
            PointF c = Center();
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
        }


        // ---- 控件层：关闭键 / 设置键 / 万能键 / 名字药丸 / 提示条 / 把手 ----
        void DrawControls(Graphics g, int a)
        {
            // 按下反馈：缩小一点 + 描边更亮，让"按下去"看得见
            PointF c = Center();
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
                // 长按时：底色由玻璃色渐变到红色（用 _closeHoldP 过渡，不是突然变），
                // 外边再画一圈红色进度环 —— 按下去就知道还差多久松手
                float hp = _closeHoldP;
                Color glassSurf = Gfx.A(GlassBase(), GlassA((int)((_closeHover ? UiFeel.SurfaceHover : (_closeDown > 0.5f ? UiFeel.SurfacePress : UiFeel.SurfaceIdle)) * ab0 / 255f)));
                Color redSurf = Gfx.A(Color.FromArgb(236, 74, 62), (int)(238 * ab0 / 255f));
                Color closeSurf = hp > 0.001f
                    ? Color.FromArgb(
                        (int)(glassSurf.A + (redSurf.A - glassSurf.A) * hp),
                        (int)(glassSurf.R + (redSurf.R - glassSurf.R) * hp),
                        (int)(glassSurf.G + (redSurf.G - glassSurf.G) * hp),
                        (int)(glassSurf.B + (redSurf.B - glassSurf.B) * hp))
                    : glassSurf;
                Color closeAcc = hp > 0.001f
                    ? Color.FromArgb((int)(236 * hp + 255 * (1 - hp)), (int)(74 + 181 * (1 - hp)), (int)(62 + 193 * (1 - hp)))
                    : acc;
                Gfx.NeuCircle(g, cbr0, closeSurf, Gfx.A(closeAcc, (int)(200 * ab0 / 255f)), hp > 0.5f, false,
                    (int)((StyleNeu() ? 60 : 24) * ab0 / 255f), (int)((StyleNeu() ? 60 : 0) * ab0 / 255f));
                if (hp > 0.01f)
                {
                    using (Pen pr = new Pen(Color.FromArgb((int)(240 * ab0 / 255f), 236, 74, 62), 3.2f))
                    {
                        pr.StartCap = LineCap.Round; pr.EndCap = LineCap.Round;
                        g.DrawArc(pr, cbr0.X - 3f, cbr0.Y - 3f, cbr0.Width + 6f, cbr0.Height + 6f, -90f, 360f * hp);
                    }
                }
                // （长按提示条挪到最后统一画：这里画会被后面的万能键盖住）
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
                Gfx.NeuCircle(g, gbr, Gfx.A(GlassBase(), GlassA((int)((_gearHover ? UiFeel.SurfaceHover : (_gearDown > 0.5f ? UiFeel.SurfacePress : UiFeel.SurfaceIdle)) * ab1 / 255f))),
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
                using (SolidBrush sbbs = new SolidBrush(Color.FromArgb((int)((_shootHover ? UiFeel.SolidHover : UiFeel.SolidIdle) * ab2 / 255f), 0, 122, 204)))
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

            // 计数胶囊「3 / 8」跟滚动位置有关，单独draw（见 DrawCountPill）：
            // 留在这一层里的话，滚动时签名每帧都变，整层缓存就废了。

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

            // 长按关闭键的提示条：位置放在关闭键正上方（避开万能键），并且最后画，不会被盖住
            if (_closeHoldP > 0.10f)
            {
                Rectangle cbr8 = Shrink(CloseButtonRect(), _closeDown);
                int ab8 = (int)(a * IntroP(0.30f));
                if (ab8 < 8) ab8 = 8;
                int ta = (int)(Math.Min(1f, (_closeHoldP - 0.10f) / 0.25f) * 240 * ab8 / 255f);
                string tip2 = _closeLong ? "松手退出 · 移开取消" : "按住不放 · 移开可取消";
                using (Font ft2 = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                using (SolidBrush tb2 = new SolidBrush(Color.FromArgb(ta, 255, 255, 255)))
                {
                    SizeF ts2 = g.MeasureString(tip2, ft2);
                    // 放在轮盘左下角那条提示带（和 toast 同一位置）：
                    // 按钮上方被万能键占着、旁边被缩略图占着，只有这里是干净的
                    SizeF ls3 = LogicalSize();
                    float px2 = 26f;
                    float py2 = ls3.Height - ts2.Height - 26f;   // 再往下让开计数胶囊
                    RectangleF pr2 = new RectangleF(px2 - 8f, py2 - 4f, ts2.Width + 16f, ts2.Height + 8f);
                    using (GraphicsPath clPath = Gfx.Round(pr2, pr2.Height / 2f))
                    {
                        BackdropClip(g, clPath, ta);
                        using (SolidBrush clBg = new SolidBrush(Color.FromArgb((int)(ta * 0.62f), 22, 24, 30))) g.FillPath(clBg, clPath);
                        using (Pen clPen = new Pen(Color.FromArgb((int)(ta * 0.55f), 236, 74, 62), 1.4f)) g.DrawPath(clPen, clPath);
                    }
                    g.DrawString(tip2, ft2, tb2, px2, py2);
                }
            }

            DrawToast(g, a);

            DrawNubs(g, a);      // 展开状态下也画一个"收起"把手（贴着另一条屏幕边）
        }
        // 计数胶囊「当前 / 总数」：跟着滚动位置变，所以每帧单独画（不进缓存层）
        void DrawCountPill(Graphics g, int a)
        {
            Color acc = _accentCur;
            if (_store.Items.Count == 0 || !_settings.ShowCountLabel) return;
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

    }
}

namespace SnapWheel
{
    partial class WheelForm
    {

        RectangleF NubOutRect()
        {
            SizeF ls = LogicalSize();
            float cy = (Sy() > 0) ? NubDistOut() : ls.Height - NubDistOut();
            float x = (Sx() > 0) ? 0f : ls.Width - NubThick;
            return new RectangleF(x, cy - NubLong / 2f, NubThick, NubLong);
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
            // 固定时长。早先是 0.82 + 0.05*图片数 —— 图一多收起就明显更慢，
            // 但展开/收起本来是一段固定几何过渡，跟盘里存了几张图没关系。
            return 1.0f;
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
            // _collapsing 时 _collapsed 还是 false（收完才置位），所以这里必须把"正在收起"排除掉：
            // 否则收起动画走到一半时点展开会被当成"已经展开了"直接 return ——
            // 用户看到的就是"收起后半夜没法立马展开"（点了没反应，动画继续缩回去）。
            if (IsExpanded && Visible && !_collapsing) { _lastActive = DateTime.Now; return; }
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
            _nubAppearT = 0f;                      // 把手从屏幕边滑出来，不要"啪"地出现
            _nubAppearAt = DateTime.Now;
            // 首次运行：把手旁边自动亮一次"点我展开"，让新用户知道它是干嘛的
            if (!_settings.NubHintDone)
            {
                _firstRunHintUntil = DateTime.Now.AddSeconds(14);
                _settings.NubHintDone = true;
            }
            // 后台抓玻璃底（同步抓一次要 12~16ms，会正好把"点开轮盘"那一下顶慢；
            // 窗口设了 WDA_EXCLUDEFROMCAPTURE，显示中抓也不会拍到轮盘自己）
            RequestBackdropAsync();
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
            if (!Visible) { _collapsed = true; _collapsing = false; _introT = 0f; _intro = false; _show = 1f; _targetShow = 1f; _showAnimating = false; _collapsedAt = DateTime.Now; RequestBackdropAsync(); Show(); Render(); return; }
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


        public void ShowWheel()
        {
            if (!Visible) { RequestBackdropAsync(); _show = 0f; _rendered = false; Show(); }
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
            if (!Visible) { RequestBackdropAsync(); _show = 0f; _rendered = false; Show(); }
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
            // 每个元素自己的过渡曲线。
            // 原来这里是 easeOutCubic（1-(1-x)^3）：一进窗口就窜出去 —— 按 66 帧算，
            // 头两帧每帧要走十几像素，后面几帧几乎不动，看起来就是"啪一下到位、然后爬"；
            // 而图片是缓缓滑进来的，于是万能键/按钮/胶囊这几样就显得"帧率更低"。
            // 换成 smoothstep：**窗口和总时长一个字没动**，只把头尾放缓、把位移摊匀。
            return x * x * (3f - 2f * x);
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

            // 删除动画：必须独立判断（原来写成 else if，挂在"放大预览"后面 ——
            // 放大预览一开着动画就不推进，于是"有动画但没删掉"）
            if (_deletingItem != null)
            {
                _deleteProg += 0.055f;                     // ~0.28s collapse
                need = true;
                if (_deleteProg >= 1f)
                {
                    StoreItem victim = _deletingItem;
                    _deletingItem = null;
                    _deleteProg = 0f;
                    RemoveItem(victim, true);
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

            // 把手出现动画（启动时）
            if (_nubAppearT < 1f)
            {
                _nubAppearT += (float)((DateTime.Now - _nubAppearAt).TotalSeconds / 0.55f);
                if (_nubAppearT >= 1f) _nubAppearT = 1f;
                _nubAppearAt = DateTime.Now;
                need = true;
            }

            // 关闭键长按：画一条红色进度环，按住 0.65s 变满 -> 变红，松手退出。
            // 同时兜住"鼠标已经松开但 MouseUp 没收到"的情况（按住时轻微移动不该取消）。
            if (_closeHold)
            {
                // 后悔机制：长按期间把鼠标挪开就作废（稍微留点余量，手抖不算离开）
                if (!CursorOverCloseButton()) CancelCloseHold();
            }
            if (_closeHold)
            {
                float p = (float)Math.Min(1.0, (DateTime.Now - _closeDownAt).TotalMilliseconds / 650.0);
                if (Math.Abs(p - _closeHoldP) > 0.004f) { _closeHoldP = p; need = true; }
                if (p >= 1f && !_closeLong) { _closeLong = true; need = true; }
                need = true;                     // 进度环一直动
            }
            else if (_closeHoldP > 0.004f)
            {
                // 作废/松手之后，红色是"渐变退回去"的，不是瞬间复位
                _closeHoldP += (0f - _closeHoldP) * 0.20f;
                if (_closeHoldP < 0.004f) _closeHoldP = 0f;
                need = true;
            }

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

                // 把手用途提示（"点我展开/收起"）：悬停时亮；首次运行的头 14 秒也自动亮一次
                bool wantHint = wantOut || wantIn || DateTime.Now < _firstRunHintUntil;
                float wantHT = wantHint ? 1f : 0f;
                if (Math.Abs(_nubHintT - wantHT) > 0.006f) { _nubHintT += (wantHT - _nubHintT) * 0.18f; need = true; }
                else if (_nubHintT != wantHT) { _nubHintT = wantHT; need = true; }
                if (_nubHintT > 0.01f) need = true;
            }

            // 后台抓好的玻璃底：在 UI 线程这里换上。
            // 但动画期间不换（尤其是展开/收起那趟）—— 换底会强制整窗重绘，正好卡在动画中间，看着就"顿"。
            // 悬停/交互期间也不换：换底会强制整窗重绘，正好把交互那一下顶慢（"慢半拍"）
            if (!_intro && _hover < 0 && !_closeHover && !_gearHover && !_shootHover && !_keyHover)
                ApplyPendingBackdrop();

            // 背景交叉淡入（换背景时玻璃颜色渐变，不跳）
            if (_backdropOld != null && _backdropFade < 1f)
            {
                _backdropFade += (float)((DateTime.Now - _backdropFadeAt).TotalSeconds / 0.38f);
                if (_backdropFade >= 1f) { _backdropFade = 1f; try { _backdropOld.Dispose(); } catch { } _backdropOld = null; }
                _backdropFadeAt = DateTime.Now;
                need = true;
            }

            // 玻璃底定时重抓：轮盘一直挂着也不会"糊的是半小时前的桌面"
            // （窗口已设置 WDA_EXCLUDEFROMCAPTURE，抓屏不会把轮盘自己拍进去，所以显示中也能抓）
            if (_settings.GlassRefresh && Visible && _show > 0.99f && !_intro)
            {
                if ((DateTime.Now - _backdropAt).TotalSeconds > 3.5)
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
                            // 抓屏+模糊（几十毫秒）放到后台线程做，UI 线程下一帧换上去，绝不卡动画
                            RequestBackdropAsync();
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
                if (radial && !_menuOpen && !_delConfirm && (DateTime.Now - _keyDownAt).TotalMilliseconds > KeyMenuDelayMs)
                {
                    _menuOpen = true; _sector = -1; need = true;
                }
                if (_menuOpen && _menuT < 1f) { _menuT += (1f - _menuT) * 0.28f; need = true; }
            }
            else if (_menuT > 0.001f)
            {
                _menuT += (0f - _menuT) * 0.22f;
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
                if (Visible) { Hide(); RequestBackdropAsync(); }   // 隐藏后再抓一次，下次显示时玻璃底是新的（后台抓，别卡 UI）
                return;
            }
            if (need || !_rendered) Render();   // render ONLY when something changed (smooth + cheap)
        }


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
    }
}

namespace SnapWheel
{
    partial class WheelForm
    {

        int GlassA(int baseA)
        {
            float g = StyleSolid() ? 1f : (_settings.GlassPercent / 100f);
            int a = (int)(baseA * g);
            return a < 0 ? 0 : (a > 255 ? 255 : a);
        }


        // ---------- 轮盘上的真·毛玻璃 ----------
        // 分层窗口不能上系统 acrylic（会给整个窗口矩形蒙灰），所以自己来：
        // 显示之前把轮盘背后那块屏幕抓下来 → 缩到 1/6 做盒式模糊 → 放大回去，
        // 画面板时把这块模糊底裁进形状里，再叠玻璃色 —— 透过去的确实是真桌面。
        DateTime _backdropAt = DateTime.MinValue;

        Bitmap _backdropBlur;

        bool _backdropValid;

        Bitmap _backdropOld;                 // 上一张模糊背景（换背景时交叉淡入，避免玻璃颜色突然一跳）

        float _backdropFade = 1f;            // 1 = 新背景完全不透明

        DateTime _backdropFadeAt = DateTime.MinValue;


        void FreeBackdrop()
        {
            if (_backdropBlur != null) { try { _backdropBlur.Dispose(); } catch { } _backdropBlur = null; }
            if (_backdropOld != null) { try { _backdropOld.Dispose(); } catch { } _backdropOld = null; }
            // 后台线程糊好、还没换上来的那张也要放掉：窗口关掉后它还挂在这儿白占内存
            if (_backdropMix != null) { try { _backdropMix.Dispose(); } catch { } _backdropMix = null; }
            _backdropMixFrame = -1;
            lock (_glassLock)
            {
                if (_glassPending != null) { try { _glassPending.Dispose(); } catch { } _glassPending = null; }
            }
            _backdropFade = 1f;
            _backdropValid = false;
        }


        // ---------- 毛玻璃后台抓取 ----------
        // 抓屏 + 高斯模糊要几十毫秒，原来是在 UI 线程里做的，动画期间会卡一下。
        // 现在：后台线程负责抓+糊，UI 线程只在下一帧把结果换上（沿用已有的交叉淡入，视觉不变）。
        volatile bool _glassBusy = false;

        Bitmap _glassPending = null;

        Point _glassPendingOffset = Point.Empty;


        public void RequestBackdropAsync()
        {
            if (StyleFlatOnly()) { FreeBackdrop(); return; }
            if (_glassBusy) return;
            if (Width < 20 || Height < 20) return;
            Rectangle vs = SystemInformation.VirtualScreen;
            Rectangle want = new Rectangle(Left, Top, Width, Height);
            Rectangle got = Rectangle.Intersect(want, vs);
            if (got.Width < 8 || got.Height < 8) return;
            _glassBusy = true;
            Point off = new Point(got.Left - want.Left, got.Top - want.Top);
            System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate()
            {
                Bitmap fresh = null;
                try
                {
                    using (Bitmap full = new Bitmap(got.Width, got.Height, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(full))
                            g.CopyFromScreen(got.Left, got.Top, 0, 0, new Size(got.Width, got.Height), CopyPixelOperation.SourceCopy);
                        fresh = BlurBitmap(full, 6);
                    }
                }
                catch { fresh = null; }
                lock (_glassLock)
                {
                    if (_glassPending != null) { try { _glassPending.Dispose(); } catch { } }
                    _glassPending = fresh;
                    _glassPendingOffset = off;
                }
                _glassBusy = false;
            }));
            th.IsBackground = true;
            try { th.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }   // 别和 UI 抢 CPU
            th.Start();
        }


        // 由 AnimTick 在 UI 线程调用：把后台糊好的底换上去
        void ApplyPendingBackdrop()
        {
            Bitmap fresh = null;
            Point off = Point.Empty;
            lock (_glassLock)
            {
                if (_glassPending == null) return;
                fresh = _glassPending; off = _glassPendingOffset; _glassPending = null;
            }
            if (StyleFlatOnly()) { try { fresh.Dispose(); } catch { } return; }
            if (_backdropBlur != null && Visible)
            {
                if (_backdropOld != null) { try { _backdropOld.Dispose(); } catch { } }
                _backdropOld = _backdropBlur;
                _backdropFade = 0f;
                _backdropFadeAt = DateTime.Now;
            }
            else
            {
                if (_backdropBlur != null) { try { _backdropBlur.Dispose(); } catch { } }
                if (!Visible && _backdropOld != null) { try { _backdropOld.Dispose(); } catch { } _backdropOld = null; _backdropFade = 1f; }
            }
            _backdropBlur = fresh;
            _backdropOffset = off;
            _backdropValid = true;
            _backdropAt = DateTime.Now;
            _rendered = false;
        }


        // 同步版：当场抓屏 + 模糊，要 12~16ms（窗口 658x658 实测），会卡住 UI 线程。
        // 正常路径一律用 RequestBackdropAsync —— 这里留着只是为了排查问题和测试对比。
        public void CaptureBackdrop()
        {
            if (StyleFlatOnly()) { FreeBackdrop(); return; }
            try
            {
                if (Width < 20 || Height < 20) return;
                Rectangle vs = SystemInformation.VirtualScreen;
                Rectangle want = new Rectangle(Left, Top, Width, Height);
                Rectangle got = Rectangle.Intersect(want, vs);
                if (got.Width < 8 || got.Height < 8) return;
                Bitmap fresh = null;
                using (Bitmap full = new Bitmap(got.Width, got.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(full))
                        g.CopyFromScreen(got.Left, got.Top, 0, 0, new Size(got.Width, got.Height), CopyPixelOperation.SourceCopy);
                    fresh = BlurBitmap(full, 6);
                }
                // 换背景不要"啪"地一跳：把旧图留着做交叉淡入（只有显示中才有必要看着它过渡）
                if (_backdropBlur != null && Visible)
                {
                    if (_backdropOld != null) { try { _backdropOld.Dispose(); } catch { } }
                    _backdropOld = _backdropBlur;
                    _backdropFade = 0f;
                    _backdropFadeAt = DateTime.Now;
                }
                else
                {
                    if (_backdropBlur != null) { try { _backdropBlur.Dispose(); } catch { } }
                    if (!Visible && _backdropOld != null) { try { _backdropOld.Dispose(); } catch { } _backdropOld = null; _backdropFade = 1f; }
                }
                _backdropBlur = fresh;
                _backdropOffset = new Point(got.Left - want.Left, got.Top - want.Top);
                _backdropValid = true;
                _backdropAt = DateTime.Now;
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

        // ---- 换底交叉淡入：整帧只混一次 ----
        Bitmap _backdropMix;          // 旧底 + 新底按当前进度混好的一张图
        int _backdropMixFrame = -1;   // 是哪一帧混的（同一帧里多张卡片共用）

        // 把"旧底"和"新底"按 _backdropFade 混成一张：旧底在下、新底按进度压上去，
        // 跟原来逐卡片混出来的画面一致（原来就是先画旧、再按 fade 压新）。
        Bitmap BackdropMix()
        {
            if (_backdropOld == null || _backdropBlur == null) return null;
            // 隔帧重建：淡入进度每帧只动 4% 左右，每两帧算一次肉眼看不出，
            // 但省掉一次全窗口混合（125% 下约 2~3ms/帧）。
            if (_backdropMix != null && _frameNo - _backdropMixFrame < 2) return _backdropMix;
            try
            {
                if (_backdropMix == null || _backdropMix.Width != _backdropBlur.Width || _backdropMix.Height != _backdropBlur.Height)
                {
                    if (_backdropMix != null) { try { _backdropMix.Dispose(); } catch { } }
                    _backdropMix = new Bitmap(_backdropBlur.Width, _backdropBlur.Height, PixelFormat.Format32bppPArgb);
                }
                using (Graphics g = Graphics.FromImage(_backdropMix))
                {
                    // SourceCopy 直接铺旧底（等于清屏 + 画旧底，省一次全窗口填充）
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(_backdropOld, new Rectangle(0, 0, _backdropMix.Width, _backdropMix.Height));
                    g.CompositingMode = CompositingMode.SourceOver;
                    float fade = Math.Max(0.06f, Math.Min(1f, _backdropFade));
                    ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = fade;
                    _iaBack.SetColorMatrix(cm);
                    g.DrawImage(_backdropBlur, new Rectangle(0, 0, _backdropMix.Width, _backdropMix.Height),
                        0, 0, _backdropBlur.Width, _backdropBlur.Height, GraphicsUnit.Pixel, _iaBack);
                }
                _backdropMixFrame = _frameNo;
                return _backdropMix;
            }
            catch { return null; }
        }

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
                bool crossFade = _backdropOld != null && _backdropFade < 0.999f;
                if (crossFade)
                {
                    // 换底那 0.38 秒里，以前是**每张卡片各混一遍**（旧底 + 新底两张全窗口图），
                    // 8 张卡片就是 16 次全窗口绘制 —— 实测这一档每帧 20ms 就是这么来的。
                    // 现在整帧只混一次（BackdropMix），每张卡片只画一张图。
                    Bitmap mix = BackdropMix();
                    if (mix != null)
                    {
                        Rectangle mdest = new Rectangle((int)Math.Round(dest.X), (int)Math.Round(dest.Y),
                            Math.Max(1, (int)Math.Round(mix.Width / UiK)), Math.Max(1, (int)Math.Round(mix.Height / UiK)));
                        if (al >= 250) g.DrawImage(mix, mdest);        // 满 alpha 就别走 ColorMatrix（慢路径）
                        else
                        {
                            ColorMatrix cmo = new ColorMatrix(); cmo.Matrix33 = al / 255f;
                            _iaBack.SetColorMatrix(cmo);
                            g.DrawImage(mix, mdest, 0, 0, mix.Width, mix.Height, GraphicsUnit.Pixel, _iaBack);
                        }
                    }
                }
                else
                {
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
    }
}

namespace SnapWheel
{
    partial class WheelForm
    {

        // 作废这次长按（红色渐变退回，完全不会触发退出）
        public void CancelCloseHold()
        {
            if (!_closeHold && !_closeLong) return;
            _closeHold = false;
            _closeLong = false;
            try { Capture = false; } catch { }
        }


        // 按下时把矩形按比例缩小（以中心为基准）
        static Rectangle Shrink(Rectangle r, float k)
        {
            if (k <= 0.001f) return r;
            float s = 1f - 0.10f * k;
            int w = (int)Math.Round(r.Width * s), h = (int)Math.Round(r.Height * s);
            return new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
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


        // 真正把一张图从轮盘拿走：内存 + 硬盘文件。
        // 关键：图片列表是靠"扫描保存目录"恢复的，只删内存不删文件，下次启动它又回来了。
        // 右键单击/双击删除进入点。上一张还在播删除动画就先把它真正删掉：
        // 连点/来回点时"正在删的那张"只有一个，后来者会覆盖前者，否则两张都删不掉。
        // （单独抽成方法是为了能直接测这条行为 —— 这正是之前测试漏掉的四个 bug 之一）
        void BeginDelete(StoreItem it)
        {
            if (_deletingItem != null)
            {
                StoreItem prev = _deletingItem;
                _deletingItem = null; _deleteProg = 0f;
                RemoveItem(prev, true);
            }
            _deletingItem = it;        // 这张交给 AnimTick 播完动画再删
            _deleteProg = 0f;
        }


        // 万能键圆盘松手时执行对应分区的动作（动作由用户在设置里自定义）
        void DoKeyAction(int sector)
        {            string a = _settings.KeyActionAt(sector);
            try
            {
                switch (a)
                {
                    case "new": CreateWheel(); break;
                    case "next": SwitchWheel(1); break;
                    case "prev": SwitchWheel(-1); break;
                    case "delete": _delConfirm = true; _delConfirmAt = DateTime.Now; break;  // 原地进入左右两半确认
                    case "shot": if (CaptureRequested != null) CaptureRequested(this, EventArgs.Empty); break;
                    case "collapse": DismissWheel(); break;
                    case "folder":
                        try { if (!Directory.Exists(_settings.Dir)) Directory.CreateDirectory(_settings.Dir); System.Diagnostics.Process.Start(_settings.Dir); } catch { }
                        break;
                    case "settings": if (SettingsRequested != null) SettingsRequested(this, EventArgs.Empty); break;
                    case "clear": ClearCurrentWheel(); break;
                    case "paste":
                        try { IDataObject dob = Clipboard.GetDataObject(); if (dob != null) ImportBitmap(dob); } catch { }
                        break;
                    default: break;   // "none"：什么都不做
                }
            }
            catch (Exception ex) { Err.Log("DoKeyAction", ex); }
        }


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
            e = LogicalArgs(e);

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
                else if (_collapsing)
                {
                    // 收起动画正走在半路上：这时候点把手应当是"我改主意了，拉回来"。
                    // 以前这里会再收一次（等于没反应），用户看到的就是"收起到后半程没法立马展开"。
                    ExpandWheel();
                }
                else if (NubSingleMode()) CollapseWheel();   // 单把手模式：同一个把手负责收起
                return;
            }
            if (!NubSingleMode() && e.Button == MouseButtons.Left && !_collapsed && NubInRect().Contains(e.Location))
            {
                if (_collapsing) ExpandWheel(); else CollapseWheel();
                return;
            }

            if (e.Button == MouseButtons.Left && CloseButtonRect().Contains(e.Location))
            {
                // 短按 = 关掉轮盘；长按（0.65s）= 变红，松手直接退出 SnapWheel
                _closeHold = true;
                _closeDownAt = DateTime.Now;
                _closeLong = false;
                _closeHoldP = 0f;
                try { Capture = true; } catch { }     // 捕获鼠标：手抖移出按钮也不会漏掉 MouseUp
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
                    BeginDelete(hh >= 0 && hh < _store.Items.Count ? _store.Items[hh] : null);
                    _enlarged = -1; _hover = -1;
                    Render();
                }
            }
            else if (e.Button == MouseButtons.Middle && hh >= 0 && hh < _store.Items.Count)
            {
                // 中键：把这张图贴（钉）到屏幕上。左键已经用来拖出去、右键用来删、双击用来复制，
                // 中键是唯一还空着的"点一下立刻做点什么"。
                StoreItem it = _store.Items[hh];
                if (it != null && it.Image != null && PinRequested != null)
                {
                    try { PinRequested(it.Image, PointToScreen(e.Location)); ShowToast("已贴到屏幕上（双击它或按 Esc 关掉）"); }
                    catch (Exception ex) { Err.Log("PinRequested", ex); }
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
                    if (s2 >= 0) DoKeyAction(s2);
                    // 这里别再 _menuT = 0，否则圆盘是"啪"地消失；留给 AnimTick 收缩淡出
                    _menuOpen = false; _sector = -1;
                }
                Render();
                return;
            }
            if (_closeHold)
            {
                try { Capture = false; } catch { }
                bool wasLong = _closeLong || _closeHoldP >= 0.999f;
                _closeHold = false; _closeLong = false; _closeHoldP = 0f;
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


        // 管理员模式下拖出去：Windows 的 UIPI 会拦掉跨权限拖拽，DoDragDrop 只会返回 None，
        // 用户看到的就是"拖了半天，图又弹回来了"。以前只有一条开机气泡（很多人把气泡关了，
        // 比如本机设置里 ShowBalloon=0），所以这里在真正拖不动的那一刻直接说清楚：
        // 先 toast 一句，再弹一次说明（每次运行只弹一次）。
        void NotifyAdminDragBlocked()
        {
            if (!Elev.Is) return;
            if (_adminTipShown)
            {
                ShowToast("管理员模式：拖拽被 Windows 拦着（托盘右键 → 管理员模式说明）");
                return;
            }
            _adminTipShown = true;
            ShowToast("管理员模式：拖不出去，是 Windows 拦的");
            // 拖拽结束后再弹：这一刻还在鼠标事件/DoDragDrop 的调用栈里，直接弹模态框容易打架
            if (AdminHelpRequested != null)
                try { BeginInvoke(new MethodInvoker(delegate { try { AdminHelpRequested(this, EventArgs.Empty); } catch { } })); }
                catch { }
        }


        // 组装"拖出去"的载荷。抽成单独方法是为了能测到底给了哪些格式 ——
        // v0.4.8 把"文件格式"默认关掉了，结果拖到资源管理器 / 只吃文件的程序直接放不进去，
        // 用户报的"缩略图拖出去放不了"就是它。少给一个格式 = 少一半能被接收的地方。
        internal DataObject BuildDragData(StoreItem it)
        {
            DataObject data = new DataObject();
            string file = null;
            try { file = _store.EnsureFile(it); } catch { }
            // 图片格式：拖到微信 / Word / PS 这类接受图片的地方，直接就是一张图
            try { data.SetData(DataFormats.Bitmap, true, it.Image); } catch { }
            // 文件格式：拖到桌面 / 资源管理器 / 只认文件的程序全靠它
            if (_settings.DragOutAsFile && file != null)
            {
                try { data.SetData(DataFormats.FileDrop, new string[] { file }); } catch { }
            }
            try { data.SetData(DragFmt, 1); } catch { }        // 标记成"轮盘自己的拖拽"，别当成外部导入
            return data;
        }

        void StartDragOut(int index)
        {
            if (index < 0 || index >= _store.Items.Count) return;
            StoreItem it = _store.Items[index];
            DataObject data = BuildDragData(it);

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
            // 记下来给 [Frame] 日志用：下次"拖不出去"时，日志里能直接看出是没被接收还是被拦了
            try
            {
                string[] fmts = data.GetFormats(false);
                _lastDragInfo = "给了 " + fmts.Length + " 种格式(" + string.Join("/", fmts) + ") 结果=" + eff +
                                (returned ? " 拖回了轮盘" : "") + (_settings.DragOutAsFile ? "" : " 未带文件格式");
            }
            catch { _lastDragInfo = "结果=" + eff; }
            if (!taken && !returned) NotifyAdminDragBlocked();
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
    }
}

namespace SnapWheel
{
    // 分层缓存（v0.5.2 性能优化）
    //
    // 为什么要它：实测一帧 12.7ms 里，控件层（关闭键/设置键/万能键/名字/把手）占 5.45ms、
    // 环占 0.48ms，推送分层窗口只占 0.38ms —— 也就是说慢在"一笔一笔重画矢量图形"上，
    // 不是慢在推屏。而这些矢量图形在**稳态**下每一帧画出来是一模一样的（悬停/按下/动画
    // 之外没有变化），所以最划算的做法是：画一次存成位图，之后每帧只贴一张图。
    //
    // 正确的关键在"什么时候能用缓存"：
    //   1) 只在稳态用（没有入场/收起/展开动画、alpha 全亮、没有控件在悬停或按下、没提示条）；
    //   2) 用一份**签名**把这一层依赖的所有状态都算进去（几何/风格/强调色/控件状态…），
    //      签名一变就重画。签名是 64 位哈希，不是字符串，每帧算一次几乎不要钱。
    //   两个都做到，视觉上就跟"每帧重画"完全一致（贴图是 1:1 设备像素，不做缩放）。
    partial class WheelForm
    {
        Bitmap _layerBack;         // 环（画在图片下面）
        Bitmap _layerFront;        // 控件层（画在图片上面）
        long _layerBackSig, _layerFrontSig;

        // 签名用的混合函数（FNV 风格；64 位下碰撞可以忽略）
        static long Mix(long h, long v) { unchecked { return (h ^ v) * 1099511628211L; } }
        static long Mix(long h, double v) { return Mix(h, BitConverter.DoubleToInt64Bits(Math.Round(v, 2))); }
        static long Mix(long h, bool v) { return Mix(h, v ? 1L : 0L); }
        static long Mix(long h, string s)
        {
            long x = 1469598103934665603L;
            if (s != null) for (int i = 0; i < s.Length; i++) x = unchecked((x ^ s[i]) * 1099511628211L);
            return Mix(h, x);
        }
        static long Sig0() { return 1469598103934665603L; }

        // 这一层现在能不能用缓存（"稳态"判定，宁可少用也别画错）
        bool LayerAllowed(int which)
        {
            if (_store == null || _settings == null) return false;
            if (_show < 0.999f || _intro || _collapsing || _showAnimating) return false;
            if (_deletingItem != null || _dragOutItem != null || _dropActive) return false;
            if (_switchFlash > 0.01f) return false;
            if (which == 0) return true;
            // 控件层：任何"动着的 / 按下的 / 弹着的"状态都不用缓存
            if (_toast.Length > 0 || _delConfirm || _menuOpen || _menuT > 0.001f) return false;
            if (_closeDown > 0.01f || _closeHover || _closePend || _closeHoldP > 0.001f || _closeLong) return false;
            if (_gearDown > 0.01f || _gearHover || _gearPend) return false;
            if (_shootDown > 0.01f || _shootHover || _shootPend) return false;
            if (_keyDown || _keyHover) return false;
            if (_nameHover) return false;
            if (_nubAppearT < 0.999f || _nubHov > 0.01f || _nubOutHover || _nubInHover) return false;
            return true;
        }

        // 这一层依赖的全部状态
        long LayerSig(int which)
        {
            long h = Sig0();
            h = Mix(h, which);
            h = Mix(h, Width); h = Mix(h, Height); h = Mix(h, (double)UiK);
            h = Mix(h, _collapsed);
            h = Mix(h, _accentCur.ToArgb());
            h = Mix(h, _settings.UiStyle);
            h = Mix(h, _settings.GlassPercent); h = Mix(h, _settings.ShadowPercent);
            h = Mix(h, _settings.UiScale); h = Mix(h, _settings.NubSingle);
            h = Mix(h, _settings.ShowNameLabel); h = Mix(h, _settings.ShowCountLabel);
            h = Mix(h, _settings.LabelSize); h = Mix(h, _settings.KeyActions);
            // 几何相关的也进签名：换了贴边角落、但窗口尺寸恰好没变时，环的位置是会变的，
            // 漏掉这一项就会贴出"上一个角落"的旧层 —— 这种 bug 光看窗口尺寸发现不了。
            h = Mix(h, _settings.Corner); h = Mix(h, _settings.Radius);
            h = Mix(h, _settings.ThumbSize); h = Mix(h, _settings.CardRadius);
            h = Mix(h, Left); h = Mix(h, Top);
            // 悬停/按下的**进度值**也必须进签名：按钮的反馈是"渐变"出来的（_keyHov/_closeDown…），
            // 只看那几个 bool 的话，过渡期间会一直贴旧层 —— 表现就是"鼠标放上去没反应"。
            h = Mix(h, (double)_keyHov); h = Mix(h, (double)_keyT);
            h = Mix(h, (double)_closeDown); h = Mix(h, (double)_gearDown); h = Mix(h, (double)_shootDown);
            h = Mix(h, (double)_closeHoldP); h = Mix(h, (double)_nubHov);
            h = Mix(h, (double)_nubAppearT); h = Mix(h, (double)_nubHintT);
            h = Mix(h, (double)_nubOutDist); h = Mix(h, (double)_nubInDist);
            if (which == 0)
            {
                h = Mix(h, (double)EffR());
                h = Mix(h, _store.Items.Count);
                return h;
            }
            h = Mix(h, _store.Items.Count);                       // 计数胶囊
            h = Mix(h, _mgr.ActiveWheel.Name);                    // 名字药丸
            for (int i = 0; i < 4; i++) h = Mix(h, KeyActionShort(_settings.KeyActionAt(i)));   // 万能键四个分区上的字
            return h;
        }

        // 命中就直接贴（1:1 设备像素：临时把画布变换复位再贴，保证不缩放、不采样）
        bool TryBlitLayer(Graphics g, int which)
        {
            Bitmap bmp = (which == 0) ? _layerBack : _layerFront;
            if (bmp == null || bmp.Width != Width || bmp.Height != Height) return false;
            if (!LayerAllowed(which)) return false;
            if ((which == 0 ? _layerBackSig : _layerFrontSig) != LayerSig(which)) return false;
            BlitLayer(g, bmp);
            return true;
        }

        void BlitLayer(Graphics g, Bitmap bmp)
        {
            System.Drawing.Drawing2D.Matrix m = g.Transform;
            try
            {
                g.ResetTransform();
                g.DrawImageUnscaled(bmp, 0, 0);
            }
            catch { }
            finally { try { g.Transform = m; } catch { } }
        }

        // 把这一层画进缓存（用同一份绘制代码，所以画出来跟原来一模一样），并顺手贴到当前帧
        void StoreLayer(int which, Graphics g, int a, bool force)
        {
            try
            {
                if (!force && !LayerAllowed(which)) return;
                Bitmap bmp = (which == 0) ? _layerBack : _layerFront;
                if (bmp == null || bmp.Width != Width || bmp.Height != Height)
                {
                    if (bmp != null) { try { bmp.Dispose(); } catch { } }
                    bmp = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                    if (which == 0) _layerBack = bmp; else _layerFront = bmp;
                }
                using (Graphics lg = Graphics.FromImage(bmp))
                {
                    lg.CompositingMode = CompositingMode.SourceCopy;
                    lg.Clear(Color.Transparent);
                    lg.CompositingMode = CompositingMode.SourceOver;
                    lg.SmoothingMode = SmoothingMode.AntiAlias;
                    lg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    lg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    lg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    if (Math.Abs(UiK - 1f) > 0.001f) lg.ScaleTransform(UiK, UiK);
                    if (which == 0) DrawRing(lg, a); else DrawControls(lg, a);
                }
                if (which == 0) _layerBackSig = LayerSig(0); else _layerFrontSig = LayerSig(1);
                // 注意：**不要再贴一次**。调用方在 store 之前已经直接画过一遍了，
                // 再贴一层等于半透明元素叠两遍 —— 用户看到的就是"一放万能键所有 UI 一起闪"。
                // 缓存从下一帧开始生效，那一帧省下的时间才是我们要的。
            }
            catch { /* 缓存失败就当没缓存：外面已经照常画过了 */ }
        }

        // 尺寸/风格变了、或者窗口关掉时把缓存扔掉
        void DropLayers()
        {
            if (_layerBack != null) { try { _layerBack.Dispose(); } catch { } _layerBack = null; }
            if (_layerFront != null) { try { _layerFront.Dispose(); } catch { } _layerFront = null; }
            _layerBackSig = 0; _layerFrontSig = 0;
            foreach (Bitmap b in _plates.Values) { try { b.Dispose(); } catch { } }
            _plates.Clear();
        }

        // ---- 卡片的小贴片（阴影 / 面板底 / 描边）----
        // 每张卡片每帧要重画：柔和阴影（三层宽描边模拟模糊）、圆角玻璃底（渐变 + 两条内描边）、描边。
        // 实测这块每张卡片约 1.2ms，8 张就接近 10ms；但"尺寸和状态不变"时每帧画出来一模一样，
        // 所以按 (尺寸, 圆角, 样式, 状态) 缓存成小位图，每帧只贴一次。
        // alpha（淡入淡出 / 收起 / 拖动）在贴的时候用 ColorMatrix 乘上去，跟逐元素乘 alpha 等价。
        readonly Dictionary<string, Bitmap> _plates = new Dictionary<string, Bitmap>();

        // 把区域对齐到 24px 网格（往外扩）：放大预览时卡片尺寸每帧变一点点，
        // 量化之后同一个网格里的尺寸共用一张贴片 —— 贴片画布略大一点，内容是精确坐标画的，
        // 贴回去还是 1:1，不引入任何缩放。
        static RectangleF Quantize(RectangleF r)
        {
            const float grid = 24f;
            float x = (float)(Math.Floor(r.X / grid) * grid);
            float y = (float)(Math.Floor(r.Y / grid) * grid);
            float rr = (float)(Math.Ceiling(r.Right / grid) * grid);
            float bb = (float)(Math.Ceiling(r.Bottom / grid) * grid);
            return new RectangleF(x, y, rr - x, bb - y);
        }

        // 门槛版：尺寸不稳定的卡片（动画中）直接返回 null，让调用方走"直接画"
        Bitmap PlateIf(bool on, string key, RectangleF area, Action<Graphics> draw)
        {
            return on ? Plate(key, area, draw) : null;
        }

        // 同一帧最多新建这么多张贴片：滚动时同时冒出来好几张新卡片的话，
        // 一帧里连做五六张贴片会把这一帧顶到 20ms 以上（尖峰就是这么来的）。
        // 超出的那些这一帧走"直接画"（画出来一样，只是没那么便宜），下一帧再建。
        const int MaxPlateGenPerFrame = 2;
        int _plateGenFrame = -1, _plateGenCount = 0;

        Bitmap Plate(string key, RectangleF area, Action<Graphics> draw)
        {
            Bitmap b;
            if (_plates.TryGetValue(key, out b)) return b;
            if (area.Width < 1f || area.Height < 1f) return null;
            if (_plateGenFrame != _frameNo) { _plateGenFrame = _frameNo; _plateGenCount = 0; }
            if (_plateGenCount >= MaxPlateGenPerFrame) return null;
            _plateGenCount++;
            if (_plates.Count > 120)
            {
                foreach (Bitmap v in _plates.Values) { try { v.Dispose(); } catch { } }
                _plates.Clear();
            }
            try
            {
                int pw = Math.Max(1, (int)Math.Ceiling(area.Width * UiK));
                int ph = Math.Max(1, (int)Math.Ceiling(area.Height * UiK));
                b = new Bitmap(pw, ph, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    if (Math.Abs(UiK - 1f) > 0.001f) g.ScaleTransform(UiK, UiK);
                    g.TranslateTransform(-area.X, -area.Y);      // 贴片内部照旧用轮盘的绝对坐标
                    draw(g);
                }
                _plates[key] = b;
                return b;
            }
            catch { return null; }
        }
    }
}

namespace SnapWheel
{
    // 手感常量（v0.5.2）：悬停/按下的数值以前散落在各个绘制分支里（166/172/206/240/252…），
    // 调一次要翻好几个地方，而且各处强弱不一致 —— 用户的原话是"点下去的反馈较弱"。
    // 统一放这里：① 悬停明显亮起来 ② 按下沉下去 + 更亮 ③ 松开自动回弹（进度值由 AnimTick 推）。
    static class UiFeel
    {
        // 表面亮度（玻璃/主色底的不透明度百分比，最后会乘玻璃设置）
        public const int SurfaceIdle = 166;     // 常态
        public const int SurfaceHover = 252;    // 悬停：明显亮一档
        public const int SurfacePress = 255;    // 按下：最亮

        // 图标/文字
        public const int IconIdle = 236;
        public const int IconHover = 255;

        // 按下时往下沉多少逻辑像素（松开回弹由动画进度负责）
        public const float SinkPx = 2f;

        // 毛玻璃面板上的主色底（截图按钮那类实心按钮）
        public const int SolidIdle = 190;
        public const int SolidHover = 240;
    }
}
namespace SnapWheel
{
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
}

namespace SnapWheel
{
    class SettingsForm : Form, IMessageFilter
    {
        // ============================ 布局总则（务必先读） ============================
        // 1. 这个窗口的布局一律"坐标明确"：每张 TableLayoutPanel 都写死 RowCount/ColumnCount，
        //    每个控件都用 Add(控件, 列, 行) 指明格子 —— **绝不靠添加顺序排行**。
        //    v0.5.2 提速时就是因为挪了添加顺序，标题跑到最底下、按钮跑到最上面（"头和屁股长反了"）。
        // 2. 四页内容 = 四张页面格，同一时间只显示一张；每页都是"两列 + 行"的明确坐标。
        // 3. 每页的控件**第一次翻到那页才建**（懒建）：构造量降到 1/4，这是打开设置变快的主因。
        //    没建过的页 = 没被看过 = 没被改过，所以"确定"时跳过它（值保持原样，不会被写回默认值）。
        // ==========================================================================
        TableLayoutPanel _root;
        Panel _body;
        PageDial _dial;
        readonly TableLayoutPanel[] _pages = new TableLayoutPanel[4];
        readonly Action[] _builders = new Action[4];
        readonly bool[] _built = new bool[4];
        int _cur = -1;
        Settings _s;
        bool _filterAdded;

        // ---- 第 1 页「行为与快捷键」 ----
        CheckBox _chkDisk, _chkAutoStart, _chkAuto, _chkTop, _chkClip, _chkBalloon, _chkUpdate, _chkDragFile;
        TextBox _txtDir;
        NumericUpDown _numSec;
        ComboBox _cmbHotkey, _cmbCorner, _cmbDel, _cmbSwitch;
        // ---- 第 2 页「轮盘与外观」 ----
        NumericUpDown _numMax, _numThumb, _numRad, _numSlots, _numLabel, _numPeek;
        ComboBox _cmbScale, _cmbRing, _cmbRing2;
        CheckBox _chkCollapse, _chkSingle, _chkIntroAnim;
        // ---- 第 3 页「风格」 ----
        ComboBox _cmbStyle, _cmbAccent, _cmbAnim;
        CheckBox _chkName, _chkCount, _chkGlassRefresh;
        // ---- 第 4 页「万能键与高级」 ----
        readonly ComboBox[] _keyBox = new ComboBox[4];
        NumericUpDown _numGlass, _numRadius, _numShadow;
        TableLayoutPanel _advR;

        static readonly int[] scVals = { 0, 80, 90, 100, 110, 125, 150, 175, 200, 250 };
        static readonly int[] ringVals = { 220, 150, 100, 80, 60, 45 };
        static readonly string[] ringNames = { "极快", "快", "标准", "慢", "很慢", "最慢" };

        // 应用真·毛玻璃：窗口背景半透明 + 系统 acrylic 模糊；系统不支持就退回不透明浅底
        void ApplyGlass()
        {
            // 对话框用干净的浅色实底：半透明窗体 + 子控件（按钮/输入框）在 Windows 上
            // 容易出现"四角没画到、重绘才恢复"的脏块，实测得不偿失。
            // 真正需要毛玻璃的地方是轮盘本体，那边是自己绘制的，好控制。
            BackColor = Color.FromArgb(248, 249, 252);
            if (_dial != null) _dial.BackColor = BackColor;   // 分页器是自绘控件，底色要跟窗口一模一样
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyGlass();
            // 滚轮翻页：装在应用级过滤器上，鼠标在窗口任何位置滚都算（数值框/下拉框也不会截胡）
            if (!_filterAdded) { try { Application.AddMessageFilter(this); _filterAdded = true; } catch { } }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _filterAdded)
            {
                try { Application.RemoveMessageFilter(this); } catch { }
                _filterAdded = false;
            }
            base.Dispose(disposing);
        }

        // 滚轮 = 翻页（设置窗口每页都不滚动，滚轮专门干这个）
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            if (m.Msg == WM_MOUSEWHEEL && Visible && ContainsFocus)
            {
                long w = m.WParam.ToInt64();
                int delta = (int)((w >> 16) & 0xFFFF);
                if (delta > 0x7FFF) delta -= 0x10000;
                if (delta != 0) { ShowPage(_cur + (delta > 0 ? -1 : 1)); return true; }
            }
            return false;
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
            _s = s;
            Text = AppInfo.Name + " 设置  ·  BETA";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(250, 250, 252);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = false;                     // 固定大小：翻页代替滚动，窗口不再随内容长高长胖
            ClientSize = new Size(760, 560);
            Padding = new Padding(20, 14, 20, 12);

            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.AutoSize = false;
            root.Dock = DockStyle.Fill;
            root.Margin = new Padding(0);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));    // 0 标题
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));    // 1 轮盘式分页器
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 2 当前页
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));    // 3 按钮行（右下）
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));    // 4 版本行
            _root = root;

            Label head = new Label();
            head.AutoSize = true;
            head.Text = AppInfo.Name + " 设置";
            head.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(32, 34, 38);
            head.Margin = new Padding(0, 0, 0, 6);
            root.Controls.Add(head, 0, 0);

            // 分页器：顶部小圆弧，四个扇区 = 四页（点扇区 / 滚轮翻页），新拟态凸起 + 当前页高亮
            _dial = new PageDial();
            _dial.Names = new string[] { "行为与快捷键", "轮盘与外观", "风格", "万能键与高级" };
            _dial.BackColor = Color.FromArgb(250, 250, 252);
            _dial.Dock = DockStyle.Fill;
            _dial.Margin = new Padding(0);
            _dial.PagePicked += new EventHandler(delegate(object o, EventArgs e2) { ShowPage(_dial.Current); });
            root.Controls.Add(_dial, 0, 1);

            // 四张页面格：先建好挂上（空白），内容懒建；非当前页 Visible=false
            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Margin = new Padding(0);
            _body = body;
            for (int i = 0; i < 4; i++)
            {
                TableLayoutPanel pg = NewGrid(2);
                pg.Visible = false;
                _pages[i] = pg;
                body.Controls.Add(pg);
            }
            root.Controls.Add(body, 0, 2);
            _builders[0] = BuildPage1;
            _builders[1] = BuildPage2;
            _builders[2] = BuildPage3;
            _builders[3] = BuildPage4;

            // ---------------- 底部按钮（新手引导 / 还原默认 / 确定 取消）行为一字未改 ----------------
            RoundButton ok = new RoundButton();
            ok.Text = "确定";
            ok.Size = new Size(104, 36);
            ok.Fill = Color.FromArgb(0, 122, 204);
            ok.FillHover = Color.FromArgb(0, 140, 232);
            ok.TextColor = Color.White;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            ok.Margin = new Padding(10, 2, 0, 0);
            ok.Click += new EventHandler(delegate(object o, EventArgs e2) {
                SaveFromUi();
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
            cancel.Margin = new Padding(10, 2, 0, 0);
            cancel.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });

            RoundButton guide = new RoundButton();
            guide.Text = "新手引导";
            guide.Size = new Size(104, 36);
            guide.Fill = Color.FromArgb(236, 240, 246);
            guide.FillHover = Color.FromArgb(226, 233, 243);
            guide.TextColor = Color.FromArgb(40, 90, 150);
            guide.Font = new Font("Microsoft YaHei UI", 10f);
            guide.Margin = new Padding(0, 2, 0, 0);
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
            reset.Margin = new Padding(10, 2, 0, 0);
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

            // 按钮行：用五列表格把确定/取消靠右对齐（引导/还原在左）。
            TableLayoutPanel btnRow = new TableLayoutPanel();
            btnRow.ColumnCount = 5;
            btnRow.RowCount = 1;
            btnRow.AutoSize = false;
            btnRow.Dock = DockStyle.Fill;
            btnRow.Margin = new Padding(0);
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            btnRow.Controls.Add(guide, 0, 0);
            btnRow.Controls.Add(reset, 1, 0);
            btnRow.Controls.Add(new Panel(), 2, 0);
            btnRow.Controls.Add(ok, 3, 0);
            btnRow.Controls.Add(cancel, 4, 0);
            root.Controls.Add(btnRow, 0, 3);

            Label about = new Label();
            about.AutoSize = true;
            about.Text = AppInfo.Name + "   v" + AppInfo.Version + "   ·   by " + AppInfo.Author + "   ·   BETA";
            about.ForeColor = Color.FromArgb(150, 150, 160);
            about.Margin = new Padding(0, 4, 0, 0);
            root.Controls.Add(about, 0, 4);

            ShowPage(0);             // 只建第 1 页
            Controls.Add(root);      // 全部建完才挂上去：整棵树只排一次
            PerformLayout();
        }

        // ============================ 四页的内容 ============================
        // 每页一张"两列 + 行"的格子，行号写死；一格里放一个控件（成组的行用 Row(...) 包一层）。
        // 行号从 0 开始，写 RowCount 时要 ≥ 最大行号 + 1，否则那行不显示。

        // ---- 第 1 页：行为与快捷键 ----
        void BuildPage1()
        {
            TableLayoutPanel g = _pages[0];
            SetupRows(g, 8);
            Settings s = _s;

            g.Controls.Add(Section("行为"), 0, 0);
            g.Controls.Add(Section("快捷键与操作"), 1, 0);

            _chkDisk = new CheckBox();
            _chkDisk.AutoSize = true;
            _chkDisk.Text = "保存到硬盘（否则只存内存，退出即清）";
            _chkDisk.Checked = s.SaveToDisk;
            _chkDisk.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkDisk, 0, 1);

            _cmbHotkey = new ComboBox();
            _cmbHotkey.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbHotkey.Width = 170;
            _cmbHotkey.Margin = new Padding(0, 6, 0, 0);
            _cmbHotkey.Items.AddRange(HotkeyUtil.Names);
            _cmbHotkey.SelectedItem = s.Hotkey;
            if (_cmbHotkey.SelectedIndex < 0) _cmbHotkey.SelectedIndex = 0;
            g.Controls.Add(Row(MkLabel("截图热键"), _cmbHotkey), 1, 1);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.AutoSize = true;
            _chkAutoStart.Text = "开机自动启动（登录后自动在后台运行）";
            _chkAutoStart.Checked = AutoRun.IsEnabled();
            _chkAutoStart.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkAutoStart, 0, 2);

            _cmbCorner = new ComboBox();
            _cmbCorner.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCorner.Width = 170;
            _cmbCorner.Margin = new Padding(0, 6, 0, 0);
            _cmbCorner.Items.AddRange(new object[] { "左下角", "右下角", "左上角", "右上角" });
            _cmbCorner.SelectedIndex = CornerIndex(s.Corner);
            g.Controls.Add(Row(MkLabel("圆环位置"), _cmbCorner), 1, 2);

            _chkAuto = new CheckBox();
            _chkAuto.AutoSize = true;
            _chkAuto.Text = "空闲后自动收起轮盘";
            _chkAuto.Checked = s.AutoHide;
            _chkAuto.Margin = new Padding(0, 4, 0, 4);
            _numSec = Num(2, 600, s.AutoHideSeconds);
            g.Controls.Add(Row(_chkAuto, Gap(16), MkLabel("空闲秒数"), _numSec), 0, 3);

            _cmbDel = new ComboBox();
            _cmbDel.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDel.Width = 170;
            _cmbDel.Margin = new Padding(0, 6, 0, 0);
            _cmbDel.Items.AddRange(new object[] { "双击右键删除", "单击右键删除" });
            _cmbDel.SelectedIndex = (s.DeleteMode == "single") ? 1 : 0;
            g.Controls.Add(Row(MkLabel("删除方式"), _cmbDel), 1, 3);

            _chkTop = new CheckBox();
            _chkTop.AutoSize = true;
            _chkTop.Text = "总在最前（始终置顶显示）";
            _chkTop.Checked = s.AlwaysOnTop;
            _chkTop.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkTop, 0, 4);

            _cmbSwitch = new ComboBox();
            _cmbSwitch.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbSwitch.Width = 170;
            _cmbSwitch.Margin = new Padding(0, 6, 0, 0);
            _cmbSwitch.Items.AddRange(new object[] { "长按万能键弹圆盘", "长按后左右滑动" });
            _cmbSwitch.SelectedIndex = (s.SwitchMode == "swipe") ? 1 : 0;
            g.Controls.Add(Row(MkLabel("Wheel 切换"), _cmbSwitch), 1, 4);

            // 保存目录这一行本来就宽，横跨两列（否则两列加起来会顶破窗口宽度）
            _txtDir = new TextBox();
            _txtDir.Text = s.Dir;
            _txtDir.Width = 300;
            _txtDir.Margin = new Padding(0, 5, 8, 0);
            Button browse = new Button();
            browse.Text = "浏览";
            browse.AutoSize = true;
            browse.MinimumSize = new Size(60, 26);
            browse.Margin = new Padding(0, 4, 0, 0);
            browse.Click += new EventHandler(delegate(object o, EventArgs e2) {
                FolderBrowserDialog d = new FolderBrowserDialog();
                if (d.ShowDialog() == DialogResult.OK) _txtDir.Text = d.SelectedPath;
            });
            Control dirRow = Row(MkLabel("保存目录"), _txtDir, browse);
            g.Controls.Add(dirRow, 0, 5);
            g.SetColumnSpan(dirRow, 2);

            _chkClip = new CheckBox();
            _chkClip.AutoSize = true;
            _chkClip.Text = "复制图片后自动收进轮盘";
            _chkClip.Checked = s.ClipboardImport;
            g.Controls.Add(Row(_chkClip), 0, 6);

            _chkBalloon = new CheckBox();
            _chkBalloon.AutoSize = true;
            _chkBalloon.Text = "显示托盘气泡提示（关掉就不再弹右下角通知）";
            _chkBalloon.Checked = s.ShowBalloon;
            g.Controls.Add(Row(_chkBalloon), 1, 6);

            _chkUpdate = new CheckBox();
            _chkUpdate.AutoSize = true;
            _chkUpdate.Text = "启动时检查有没有新版本（只提示，不自动安装）";
            _chkUpdate.Checked = s.CheckUpdate;
            g.Controls.Add(Row(_chkUpdate), 0, 7);

            _chkDragFile = new CheckBox();
            _chkDragFile.AutoSize = true;
            _chkDragFile.Text = "拖出时同时带上\"文件\"（拖到桌面/文件夹会落地成文件）";
            _chkDragFile.Checked = s.DragOutAsFile;
            g.Controls.Add(Row(_chkDragFile), 1, 7);
        }

        // ---- 第 2 页：轮盘与外观 ----
        void BuildPage2()
        {
            TableLayoutPanel g = _pages[1];
            SetupRows(g, 8);
            Settings s = _s;

            g.Controls.Add(Section("外观"), 0, 0);

            _numMax = Num(1, 999, s.MaxCount);
            _numThumb = Num(40, 260, s.ThumbSize);
            g.Controls.Add(Row(MkLabel("最多保留张数"), _numMax, Gap(24), MkLabel("缩略图大小"), _numThumb), 0, 1);

            _chkCollapse = new CheckBox();
            _chkCollapse.AutoSize = true;
            _chkCollapse.Text = "收起状态：缩到屏幕边上留个小把手";
            _chkCollapse.Checked = s.CollapseMode;
            g.Controls.Add(Row(_chkCollapse), 1, 1);

            _numPeek = Num(120, 500, s.PeekPercent);
            Control peekRow = Row(MkLabel("长按放大(%)"), _numPeek);
            g.Controls.Add(peekRow, 0, 2);
            g.SetColumnSpan(peekRow, 2);

            // 下面这几行本身就宽（标签 + 下拉 + 说明），横跨两列 —— 两列并排会顶破窗口宽度
            _cmbScale = new ComboBox();
            _cmbScale.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbScale.FlatStyle = FlatStyle.Flat;
            _cmbScale.Width = 170;
            _cmbScale.Margin = new Padding(0, 6, 0, 0);
            _cmbScale.Items.Add("自动（按显示器 DPI）");
            for (int i = 1; i < scVals.Length; i++) _cmbScale.Items.Add(scVals[i] + "%");
            _cmbScale.SelectedIndex = 0;
            for (int i = 0; i < scVals.Length; i++) if (scVals[i] == s.UiScale) _cmbScale.SelectedIndex = i;
            Label hint = new Label();
            hint.AutoSize = true;
            hint.Text = "（整块轮盘等比放大，含文字和图标）";
            hint.ForeColor = Color.FromArgb(150, 152, 160);
            hint.Margin = new Padding(0, 10, 0, 0);
            Control scaleRow = Row(MkLabel("界面缩放"), _cmbScale, Gap(12), hint);
            g.Controls.Add(scaleRow, 0, 3);
            g.SetColumnSpan(scaleRow, 2);

            // 收起 / 展开的动画速度（独立于"动画速度"）
            _cmbRing = new ComboBox();
            _cmbRing.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRing.FlatStyle = FlatStyle.Flat;
            _cmbRing.Width = 130;
            _cmbRing.Margin = new Padding(0, 6, 0, 0);
            for (int i = 0; i < ringNames.Length; i++) _cmbRing.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            _cmbRing.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.ExpandSpeed) _cmbRing.SelectedIndex = i;
            Label hintRing = new Label();
            hintRing.AutoSize = true;
            hintRing.Text = "（只管收起 / 展开；百分比越大越快）";
            hintRing.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing.Margin = new Padding(0, 10, 0, 0);
            Control ringRow = Row(MkLabel("展开速度"), _cmbRing, Gap(10), hintRing);
            g.Controls.Add(ringRow, 0, 4);
            g.SetColumnSpan(ringRow, 2);

            _cmbRing2 = new ComboBox();
            _cmbRing2.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRing2.FlatStyle = FlatStyle.Flat;
            _cmbRing2.Width = 130;
            _cmbRing2.Margin = new Padding(0, 6, 0, 0);
            for (int i = 0; i < ringNames.Length; i++) _cmbRing2.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            _cmbRing2.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.CollapseSpeed) _cmbRing2.SelectedIndex = i;
            Label hintRing2 = new Label();
            hintRing2.AutoSize = true;
            hintRing2.Text = "（默认比展开快一档，收起要干脆）";
            hintRing2.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing2.Margin = new Padding(0, 10, 0, 0);
            Control ring2Row = Row(MkLabel("收起速度"), _cmbRing2, Gap(10), hintRing2);
            g.Controls.Add(ring2Row, 0, 5);
            g.SetColumnSpan(ring2Row, 2);

            _numRad = Num(120, 700, s.Radius);
            _numSlots = Num(2, 12, s.Slots);
            _numLabel = Num(9, 40, s.LabelSize);
            Control radRow = Row(MkLabel("环半径"), _numRad, Gap(24), MkLabel("弧上张数"), _numSlots,
                                 Gap(24), MkLabel("序号字号"), _numLabel);
            g.Controls.Add(radRow, 0, 6);
            g.SetColumnSpan(radRow, 2);

            _chkSingle = new CheckBox();
            _chkSingle.AutoSize = true;
            _chkSingle.Text = "只用一个把手：左边那个点一下展开、再点一下收起（任务栏自动隐藏时更省事）";
            _chkSingle.Checked = s.NubSingle;
            Control singleRow = Row(_chkSingle);
            g.Controls.Add(singleRow, 0, 7);
            g.SetColumnSpan(singleRow, 2);
        }

        // ---- 第 3 页：风格 ----
        void BuildPage3()
        {
            TableLayoutPanel g = _pages[2];
            SetupRows(g, 4);
            Settings s = _s;

            g.Controls.Add(Section("风格"), 0, 0);

            _cmbStyle = new ComboBox();
            _cmbStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbStyle.FlatStyle = FlatStyle.Flat;
            _cmbStyle.Width = 170;
            _cmbStyle.Margin = new Padding(0, 6, 0, 0);
            _cmbStyle.Items.AddRange(new object[] { "新拟态 + 毛玻璃", "纯扁平", "高对比（不透明）" });
            _cmbStyle.SelectedIndex = (s.UiStyle == "flat") ? 1 : (s.UiStyle == "solid" ? 2 : 0);

            _cmbAccent = new ComboBox();
            _cmbAccent.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAccent.FlatStyle = FlatStyle.Flat;
            _cmbAccent.Width = 170;
            _cmbAccent.Margin = new Padding(0, 6, 0, 0);
            _cmbAccent.Items.Add("跟随 Wheel 颜色");
            for (int i = 0; i < Palette.Names.Length; i++) _cmbAccent.Items.Add("统一：" + Palette.Names[i]);
            _cmbAccent.SelectedIndex = (s.AccentIndex >= 0 && s.AccentIndex < Palette.Names.Length) ? s.AccentIndex + 1 : 0;
            Control styleRow = Row(MkLabel("界面风格"), _cmbStyle, Gap(24), MkLabel("主题色"), _cmbAccent);
            g.Controls.Add(styleRow, 0, 1);
            g.SetColumnSpan(styleRow, 2);

            _cmbAnim = new ComboBox();
            _cmbAnim.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAnim.FlatStyle = FlatStyle.Flat;
            _cmbAnim.Width = 170;
            _cmbAnim.Margin = new Padding(0, 6, 0, 0);
            _cmbAnim.Items.AddRange(new object[] { "慢", "标准", "快" });
            _cmbAnim.SelectedIndex = (s.AnimSpeed <= 85) ? 0 : (s.AnimSpeed >= 120 ? 2 : 1);

            _chkName = new CheckBox();
            _chkName.AutoSize = true;
            _chkName.Text = "显示名称标签";
            _chkName.Checked = s.ShowNameLabel;
            _chkName.Margin = new Padding(0, 10, 0, 0);
            _chkCount = new CheckBox();
            _chkCount.AutoSize = true;
            _chkCount.Text = "显示计数标签";
            _chkCount.Checked = s.ShowCountLabel;
            _chkCount.Margin = new Padding(20, 10, 0, 0);
            Control animRow = Row(MkLabel("动画速度"), _cmbAnim, Gap(24), _chkName, _chkCount);
            g.Controls.Add(animRow, 0, 2);
            g.SetColumnSpan(animRow, 2);

            _chkGlassRefresh = new CheckBox();
            _chkGlassRefresh.AutoSize = true;
            _chkGlassRefresh.Text = "毛玻璃定时刷新（轮盘挂久了背景也是新的）";
            _chkGlassRefresh.Checked = s.GlassRefresh;
            g.Controls.Add(Row(_chkGlassRefresh), 0, 3);

            _chkIntroAnim = new CheckBox();
            _chkIntroAnim.AutoSize = true;
            _chkIntroAnim.Text = "启动时播放开启动画";
            _chkIntroAnim.Checked = s.IntroAnim;
            g.Controls.Add(Row(_chkIntroAnim), 1, 3);
        }

        // ---- 第 4 页：万能键与高级 ----
        void BuildPage4()
        {
            TableLayoutPanel g = _pages[3];
            SetupRows(g, 7);
            Settings s = _s;

            g.Controls.Add(Section("万能键"), 0, 0);

            // 四个分区各绑一个动作（以前是写死的）。选中就立刻写进设置：
            // 不依赖"确定"里那段保存循环（之前那里没生效）。
            string[] keyDir = { "上", "右", "下", "左" };
            for (int i = 0; i < 4; i++)
            {
                ComboBox kb = new ComboBox();
                kb.DropDownStyle = ComboBoxStyle.DropDownList;
                kb.Width = 150;
                kb.Margin = new Padding(0, 6, 14, 0);
                for (int j = 0; j < Settings.KeyActionIds.Length; j++)
                    kb.Items.Add(Settings.KeyActionName(Settings.KeyActionIds[j]));
                string cur = s.KeyActionAt(i);
                int idx = 0;
                for (int j = 0; j < Settings.KeyActionIds.Length; j++) if (Settings.KeyActionIds[j] == cur) idx = j;
                kb.SelectedIndex = idx;
                _keyBox[i] = kb;
                {
                    int myI = i; ComboBox self = kb;
                    kb.SelectedIndexChanged += new EventHandler(delegate(object o, EventArgs e2) {
                        if (self.SelectedIndex >= 0) s.SetKeyAction(myI, Settings.KeyActionIds[self.SelectedIndex]);
                    });
                }
            }
            // 一行放两个方向，省竖直空间
            g.Controls.Add(Row(MkLabel(keyDir[0]), _keyBox[0], Gap(16), MkLabel(keyDir[1]), _keyBox[1]), 0, 1);
            g.Controls.Add(Row(MkLabel(keyDir[2]), _keyBox[2], Gap(16), MkLabel(keyDir[3]), _keyBox[3]), 0, 2);

            Label keyHint = MkLabel("按住万能键弹出圆盘，往哪个方向松手就执行哪个动作");
            keyHint.ForeColor = Color.FromArgb(140, 146, 158);
            Control keyHintRow = Row(keyHint);
            g.Controls.Add(keyHintRow, 0, 3);
            g.SetColumnSpan(keyHintRow, 2);

            // ---------- 高级（外观微调）：默认折叠，需要时勾一下 ----------
            // 这几项对大多数人是噪音（第一次用不懂该选什么），所以默认藏起来。
            g.Controls.Add(Section("高级"), 0, 4);

            _advR = new TableLayoutPanel();
            _advR.ColumnCount = 1;
            _advR.RowCount = 1;
            _advR.AutoSize = true;
            _advR.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _advR.Margin = new Padding(0);
            _advR.Visible = false;
            _advR.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _advR.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _numGlass = Num(20, 100, s.GlassPercent);
            _numRadius = Num(0, 30, s.CardRadius);
            _numShadow = Num(0, 100, s.ShadowPercent);
            _advR.Controls.Add(Row(MkLabel("玻璃不透明度"), _numGlass, Gap(16), MkLabel("圆角(%)"), _numRadius,
                                   Gap(16), MkLabel("阴影强度"), _numShadow), 0, 0);

            CheckBox chkAdv = new CheckBox();
            chkAdv.AutoSize = true;
            chkAdv.Text = "显示高级选项（外观微调：玻璃 / 圆角 / 阴影）";
            chkAdv.Margin = new Padding(0, 10, 0, 0);
            chkAdv.CheckedChanged += new EventHandler(delegate(object o, EventArgs e2) {
                _advR.Visible = chkAdv.Checked;
                _advR.PerformLayout();
                PerformLayout();
            });
            g.Controls.Add(chkAdv, 0, 5);
            g.Controls.Add(_advR, 0, 6);
        }

        // ============================ 翻页 ============================
        // 第一次翻到某页才建那页的控件；没建过的页 = 没看过 = 没改过。
        void ShowPage(int i)
        {
            if (i < 0) i = 0;
            if (i > _pages.Length - 1) i = _pages.Length - 1;
            if (!_built[i])
            {
                _built[i] = true;
                _pages[i].SuspendLayout();
                _builders[i]();
                _pages[i].ResumeLayout(true);
            }
            _cur = i;
            for (int k = 0; k < _pages.Length; k++) _pages[k].Visible = (k == i);
            if (_dial != null) { _dial.Current = i; _dial.Invalidate(); }
            if (_body != null) _body.PerformLayout();
        }

        // ============================ 保存 ============================
        // 把界面上改过的值写回设置。**只写"建过"的页**：没建过的页用户没看过，
        // 保持原值即可（绝不会把它覆盖回默认值 —— 老版本这里踩过一次"确定后改动打回原形"）。
        void SaveFromUi()
        {
            Settings s = _s;

            if (_built[0])
            {
                s.SaveToDisk = _chkDisk.Checked;
                s.Dir = _txtDir.Text.Trim();
                s.AutoHide = _chkAuto.Checked;
                s.AutoHideSeconds = (int)_numSec.Value;
                s.AlwaysOnTop = _chkTop.Checked;
                s.Corner = IndexCorner(_cmbCorner.SelectedIndex);
                s.AutoStart = _chkAutoStart.Checked;
                s.DeleteMode = (_cmbDel.SelectedIndex == 1) ? "single" : "double";
                s.SwitchMode = (_cmbSwitch.SelectedIndex == 1) ? "swipe" : "radial";
                s.ClipboardImport = _chkClip.Checked;
                s.ShowBalloon = _chkBalloon.Checked;
                s.CheckUpdate = _chkUpdate.Checked;
                s.DragOutAsFile = _chkDragFile.Checked;
            }
            if (_built[1])
            {
                s.MaxCount = (int)_numMax.Value;
                s.ThumbSize = (int)_numThumb.Value;
                s.Radius = (int)_numRad.Value;
                s.Slots = (int)_numSlots.Value;
                s.LabelSize = (int)_numLabel.Value;
                s.PeekPercent = (int)_numPeek.Value;
                s.UiScale = scVals[_cmbScale.SelectedIndex < 0 ? 0 : _cmbScale.SelectedIndex];
                s.CollapseMode = _chkCollapse.Checked;
                s.ExpandSpeed = ringVals[_cmbRing.SelectedIndex < 0 ? 2 : _cmbRing.SelectedIndex];
                s.CollapseSpeed = ringVals[_cmbRing2.SelectedIndex < 0 ? 2 : _cmbRing2.SelectedIndex];
                s.NubSingle = _chkSingle.Checked;
            }
            if (_built[2])
            {
                s.UiStyle = (_cmbStyle.SelectedIndex == 1) ? "flat" : (_cmbStyle.SelectedIndex == 2 ? "solid" : "neu");
                s.AccentIndex = _cmbAccent.SelectedIndex - 1;
                s.AnimSpeed = (_cmbAnim.SelectedIndex == 0) ? 70 : (_cmbAnim.SelectedIndex == 2 ? 140 : 100);
                s.ShowNameLabel = _chkName.Checked;
                s.ShowCountLabel = _chkCount.Checked;
                s.GlassRefresh = _chkGlassRefresh.Checked;
                s.IntroAnim = _chkIntroAnim.Checked;     // 注意：这个控件在第 3 页（"动画细节"），别放进上一块
            }
            if (_built[3])
            {
                s.GlassPercent = (int)_numGlass.Value;
                s.CardRadius = (int)_numRadius.Value;
                s.ShadowPercent = (int)_numShadow.Value;
                // 保存万能键四分区。这里必须立刻 s.Save() 落盘：
                // 否则下次打开设置窗口会从文件里读到旧值，一点确定就把刚改的打回原形
                // （"圆盘上的动作名改完不变"的根因）。这条行为现在由 tests\behavior-test.cs 守着。
                try
                {
                    for (int ki = 0; ki < 4; ki++)
                    {
                        if (_keyBox[ki] == null) continue;
                        int ksel = _keyBox[ki].SelectedIndex;
                        if (ksel < 0) ksel = 0;
                        s.SetKeyAction(ki, Settings.KeyActionIds[ksel]);
                    }
                    s.Save();
                }
                catch (Exception kex) { Err.Log("SettingsSaveKey", kex); }
            }
            if (_built[0] && _cmbHotkey != null && _cmbHotkey.SelectedItem != null) s.Hotkey = _cmbHotkey.SelectedItem.ToString();
            AutoRun.Apply(s.AutoStart);
            s.Save();
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

        // 新建一张格子：列样式先给全，行样式由各页 SetupRows 按自己的行数补
        static TableLayoutPanel NewGrid(int cols)
        {
            TableLayoutPanel g = new TableLayoutPanel();
            g.ColumnCount = cols;
            g.RowCount = 1;
            g.AutoSize = false;
            g.Dock = DockStyle.Fill;
            g.Margin = new Padding(0);
            for (int i = 0; i < cols; i++) g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return g;
        }

        // 指定行数并补满 RowStyles（行数必须 ≥ 用到的最大行号 + 1，否则那一行不显示）
        static void SetupRows(TableLayoutPanel g, int rows)
        {
            g.RowCount = rows;
            g.RowStyles.Clear();
            for (int i = 0; i < rows; i++) g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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

    // ============================ 轮盘式分页器 ============================
    // 顶部一个小圆弧，四个扇区 = 四页：与主界面同一套视觉语言（新拟态的"上亮下暗"凸起感），
    // 当前页高亮成主题色、数字变白。点扇区翻页，滚轮由 SettingsForm 的消息过滤器接管。
    class PageDial : Control
    {
        public string[] Names = new string[0];
        public int Current;
        public event EventHandler PagePicked;
        int _hover = -1;

        static readonly Color Accent = Color.FromArgb(0, 122, 204);
        static readonly Color Surface = Color.FromArgb(238, 240, 245);
        static readonly Color SurfaceHot = Color.FromArgb(246, 249, 253);
        static readonly Font NumFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        static readonly Font TitleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);

        const float SweepTotal = 150f;      // 整个圆弧张开的度数（其余留白，看起来才像"顶部一小段弧"）
        const float BandW = 24f;            // 弧的厚度

        public PageDial()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        int Count { get { return Names == null ? 0 : Names.Length; } }
        float Cx { get { return Width / 2f; } }
        float Cy { get { return Height - 4f; } }                    // 圆心落在控件底边上：只露出上半圆
        float Ro { get { return Math.Min(92f, Height - 8f); } }
        float Ri { get { return Ro - BandW; } }

        // 扇区环带路径（外弧顺着画、内弧倒着画，中间留一点缝，瓣与瓣之间才看得出分界）
        static GraphicsPath Band(float cx, float cy, float ri, float ro, float start, float sweep)
        {
            GraphicsPath p = new GraphicsPath();
            if (sweep <= 0.1f) return p;
            p.AddArc(new RectangleF(cx - ro, cy - ro, ro * 2f, ro * 2f), start, sweep);
            p.AddArc(new RectangleF(cx - ri, cy - ri, ri * 2f, ri * 2f), start + sweep, -sweep);
            p.CloseFigure();
            return p;
        }

        void Sector(int i, out float start, out float sweep, out PointF mid)
        {
            int n = Math.Max(1, Count);
            sweep = SweepTotal / n;
            start = 270f - SweepTotal / 2f + sweep * i;
            double a = (start + sweep / 2f) * Math.PI / 180.0;
            float mr = (Ri + Ro) / 2f;
            mid = new PointF(Cx + (float)(Math.Cos(a) * mr), Cy + (float)(Math.Sin(a) * mr));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            int n = Count;
            if (n <= 0) return;

            for (int i = 0; i < n; i++)
            {
                float start, sweep; PointF mid;
                Sector(i, out start, out sweep, out mid);
                bool cur = (i == Current);
                bool hot = (i == _hover);
                using (GraphicsPath p = Band(Cx, Cy, Ri, Ro, start + 1.2f, sweep - 2.4f))
                {
                    RectangleF box = p.GetBounds();
                    // 凸起感：顶上一条高光、底下一条暗边（GlassPanel 一上一下，跟轮盘控件同一套路）
                    Gfx.GlassPanel(g, p, box, cur ? Accent : (hot ? SurfaceHot : Surface),
                                   cur ? 120 : 190, cur ? 80 : 46, true);
                    if (cur)
                        using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1.2f))
                        { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawPath(pen, p); }
                }
                // 扇区里的序号
                Rectangle numRc = new Rectangle((int)mid.X - 12, (int)mid.Y - 9, 24, 18);
                TextRenderer.DrawText(g, (i + 1).ToString(), NumFont, numRc,
                    cur ? Color.White : Color.FromArgb(112, 120, 134),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // 圆环内圈里写当前页的名字（翻页时立刻跟着变）
            string t = (Current >= 0 && Current < n) ? Names[Current] : "";
            int tw = TextRenderer.MeasureText(t, TitleFont).Width + 12;
            Rectangle rc = new Rectangle((int)(Cx - tw / 2f), (int)(Cy - Ri + 34f), tw, 26);
            TextRenderer.DrawText(g, t, TitleFont, rc, Color.FromArgb(64, 70, 82),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // 命中的扇区；没命中返回 -1（环带内外各放宽 6px，好点一点）
        int HitTest(Point p)
        {
            int n = Count;
            if (n <= 0) return -1;
            float dx = p.X - Cx, dy = p.Y - Cy;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            if (d > Ro + 6f || d < Ri - 6f) return -1;
            double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (ang < 0) ang += 360.0;
            double rel = ang - (270.0 - SweepTotal / 2.0);
            if (rel < 0) rel += 360.0;
            if (rel > SweepTotal) return -1;
            int idx = (int)(rel / (SweepTotal / n));
            return idx < n ? idx : n - 1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = HitTest(e.Location);
            if (i >= 0 && i != Current)
            {
                Current = i;
                Invalidate();
                if (PagePicked != null) PagePicked(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = HitTest(e.Location);
            if (i != _hover) { _hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }
    }
}

namespace SnapWheel
{
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

        public GuideForm() : this(AppInfo.Name + " 快照轮环 · 使用说明", "轮盘平时就待在屏幕角落里，用鼠标滚轮就能翻图。", false) { }

        // firstEver=true：全新安装的欢迎引导；false：升级后自动弹的"这次多了什么"
        public GuideForm(bool firstEver) : this(
            firstEver ? "欢迎用 SnapWheel 快照轮环" : ("SnapWheel 更新到 v" + AppInfo.Version),
            firstEver ? "轮盘平时就待在屏幕角落里，用鼠标滚轮就能翻图。"
                      : "这次加了新东西 —— 下面标了「新」的两条就是，一分钟看完就能用上。",
            !firstEver) { }

        GuideForm(string title, string subtitle, bool markNew)
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
            head.Text = title;
            head.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.AutoSize = true;
            head.Location = new Point(28, 24);
            Controls.Add(head);

            Label sub = new Label();
            sub.Text = subtitle;
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.AutoSize = true;
            sub.Location = new Point(31, 60);
            Controls.Add(sub);

            int y = 100;
            string nw = markNew ? "【新】" : "";
            AddTip(28, ref y, "第 1 步：截一张", "按 " + Settings.Load().Hotkey + " 拖框选区域，四角缩放、拖旋转键转角度，双击/回车确认。");
            AddTip(28, ref y, nw + "截完直接标注", "浮层上有条工具条：箭头 / 方框 / 马赛克 / 文字，四个颜色可选，Ctrl+Z 撤销。确认之后标注就跟着图一起进轮盘 —— 圈重点不用再去别的软件。");
            AddTip(28, ref y, "第 2 步：拖出去（最常用）", "把环上的缩略图直接拖进微信 / QQ / 文档 / 文件夹，松开就发出去 —— 不用先保存、再选文件。这一下就是它的全部意义。");
            AddTip(28, ref y, nw + "要对照着看：贴到屏幕上", "缩略图上按一下鼠标中键（就是滚轮键），这张图就钉在屏幕上了：滚轮缩放、拖着挪位置、双击或 Esc 关掉。写东西时对着参考图很方便。");
            AddTip(28, ref y, "反过来：拖回来", "从桌面、网页、聊天窗口里把图片拖到环带上松手，就收进轮盘了，随时能再拖出去。");
            AddTip(28, ref y, "连拖都不用：复制即收纳", "在任何地方「复制」一张图（截图工具、网页右键、微信里都行），它会自动滑进轮盘。不想要可以在设置里关掉。");
            AddTip(28, ref y, "按住看大图", "缩略图按住约 0.3 秒放大预览，放大倍数在设置里可调。");
            AddTip(28, ref y, "万能键（可以改成你要的）", "长按环内侧那个圆盘会弹出四个方向，往哪个方向松手就执行哪个动作。默认：上=新建轮盘，右=下一个，下=删除，左=上一个 —— 四个动作都能在设置里换。");
            AddTip(28, ref y, "收起态（默认关）", "打开后不用时会缩成屏幕边上的小把手，点一下用彩虹动画拉出来。想让桌面更干净再开。");
            AddTip(28, ref y, "托盘", "托盘右键还有：导入图片、新手引导、重播开启动画、设置、退出。");

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

    class AdminForm : Form
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

        public AdminForm()
        {
            Text = AppInfo.Name + " 管理员模式";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 100);           // 先占位，最后按内容重算
            SuspendLayout();

            Label head = new Label();
            head.Text = "管理员模式下，拖拽会被 Windows 拦住";
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.AutoSize = true;
            head.Location = new Point(28, 24);
            Controls.Add(head);

            Label sub = new Label();
            sub.Text = "不是 SnapWheel 的毛病，是系统的安全限制（UIPI）：管理员进程和普通程序（资源管理器、微信、浏览器）之间不允许互相拖拽。";
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.AutoSize = false;
            sub.Size = new Size(506, 40);
            sub.Location = new Point(31, 58);
            Controls.Add(sub);

            int y = 108;
            AddTip(28, ref y, "想拖拽 → 换普通权限", "点下面那个按钮：SnapWheel 会先退出，再由资源管理器用普通权限重新启动。设置、轮盘、存的图片都不受影响。");
            AddTip(28, ref y, "不换权限也能用", "托盘右键「导入图片…」能直接选文件收进轮盘；在任何地方「复制」一张图，它也会自动滑进来 —— 这两个都不受权限影响。");
            AddTip(28, ref y, "什么时候才需要管理员", "只有要截「管理员窗口」（任务管理器、某些安装程序）时才需要；平时用普通权限最省事，拖拽也正常。");

            RoundButton go = new RoundButton();
            go.Text = "以普通权限重启";
            go.Size = new Size(150, 38);
            go.Fill = Color.FromArgb(0, 122, 204);
            go.FillHover = Color.FromArgb(0, 140, 232);
            go.TextColor = Color.White;
            go.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            go.Location = new Point(560 - 28 - 150, y + 12);
            go.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Controls.Add(go);
            AcceptButton = go;

            RoundButton no = new RoundButton();
            no.Text = "知道了";
            no.Size = new Size(104, 38);
            no.Fill = Color.FromArgb(238, 240, 245);
            no.FillHover = Color.FromArgb(226, 230, 238);
            no.TextColor = Color.FromArgb(60, 64, 74);
            no.Font = new Font("Microsoft YaHei UI", 10f);
            no.Location = new Point(560 - 28 - 150 - 12 - 104, y + 12);
            no.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });
            Controls.Add(no);
            CancelButton = no;

            ClientSize = new Size(560, y + 12 + 38 + 24);
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
            b.Size = new Size(506, 20);
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
}

namespace SnapWheel
{
    // 取字（OCR）的结果框：原文可编辑 + 一键翻译 + 各自可复制。
    // 打开时自动把原文放进剪贴板 —— 取字的目的就是"拿去用"，少一步是一步。
    class OcrForm : Form
    {
        readonly TextBox _src;
        readonly TextBox _dst;
        readonly RoundButton _tr;
        readonly Label _trState;
        bool _busy;
        string _translated = "";

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

        public OcrForm(string text)
        {
            if (text == null) text = "";
            bool copied = false;
            try { Clipboard.SetText(text); copied = true; } catch { }

            int chars = text.Replace("\r", "").Replace("\n", "").Length;
            Text = AppInfo.Name + " 取字";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 560);
            MinimumSize = new Size(420, 380);
            SuspendLayout();

            Label head = new Label();
            head.Text = chars > 0 ? ("认出来 " + chars + " 个字") : "没认出文字";
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.AutoSize = true;
            head.Location = new Point(24, 16);
            Controls.Add(head);

            Label sub = new Label();
            sub.Text = chars > 0
                ? (copied ? "原文已复制到剪贴板；要用译文点下面的「翻译」"
                          : "下面就是识别结果，可以改完再复制")
                : "换一块更清晰、字更大的区域再试试；倾斜或花哨的字体识别率会低一些";
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.AutoSize = true;
            sub.Location = new Point(27, 46);
            Controls.Add(sub);

            Label l1 = new Label();
            l1.Text = "原文";
            l1.ForeColor = Color.FromArgb(120, 124, 134);
            l1.AutoSize = true;
            l1.Location = new Point(24, 74);
            Controls.Add(l1);

            _src = new TextBox();
            _src.Multiline = true;
            _src.ScrollBars = ScrollBars.Both;
            _src.WordWrap = true;
            _src.Font = new Font("Microsoft YaHei UI", 11f);
            _src.BorderStyle = BorderStyle.FixedSingle;
            _src.BackColor = Color.White;
            _src.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _src.Location = new Point(24, 94);
            _src.Size = new Size(ClientSize.Width - 48, 170);
            _src.Text = text;
            Controls.Add(_src);

            // 翻译
            _tr = new RoundButton();
            _tr.Text = chars > 0 ? ("翻译成" + Translate.TargetLabel(text)) : "翻译";
            _tr.Size = new Size(132, 34);
            _tr.Fill = Color.FromArgb(0, 122, 204);
            _tr.FillHover = Color.FromArgb(0, 140, 232);
            _tr.TextColor = Color.White;
            _tr.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            _tr.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _tr.Location = new Point(24, 274);
            _tr.Click += new EventHandler(delegate(object o, EventArgs e2) { DoTranslate(); });
            Controls.Add(_tr);

            RoundButton copySrc = new RoundButton();
            copySrc.Text = "复制原文";
            copySrc.Size = new Size(102, 34);
            copySrc.Fill = Color.FromArgb(238, 240, 245);
            copySrc.FillHover = Color.FromArgb(226, 230, 238);
            copySrc.TextColor = Color.FromArgb(60, 64, 74);
            copySrc.Font = new Font("Microsoft YaHei UI", 10f);
            copySrc.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            copySrc.Location = new Point(164, 274);
            copySrc.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                try { Clipboard.SetText(_src.Text); _trState.Text = "原文已复制"; } catch { }
            });
            Controls.Add(copySrc);

            _trState = new Label();
            _trState.Text = "译文";
            _trState.ForeColor = Color.FromArgb(120, 124, 134);
            _trState.AutoSize = true;
            _trState.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _trState.Location = new Point(280, 284);
            Controls.Add(_trState);

            _dst = new TextBox();
            _dst.Multiline = true;
            _dst.ScrollBars = ScrollBars.Both;
            _dst.WordWrap = true;
            _dst.ReadOnly = true;
            _dst.Font = new Font("Microsoft YaHei UI", 11f);
            _dst.BorderStyle = BorderStyle.FixedSingle;
            _dst.BackColor = Color.FromArgb(248, 249, 252);
            _dst.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _dst.Location = new Point(24, 316);
            _dst.Size = new Size(ClientSize.Width - 48, 170);
            Controls.Add(_dst);

            RoundButton copyDst = new RoundButton();
            copyDst.Text = "复制译文";
            copyDst.Size = new Size(102, 34);
            copyDst.Fill = Color.FromArgb(238, 240, 245);
            copyDst.FillHover = Color.FromArgb(226, 230, 238);
            copyDst.TextColor = Color.FromArgb(60, 64, 74);
            copyDst.Font = new Font("Microsoft YaHei UI", 10f);
            copyDst.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            copyDst.Location = new Point(24, ClientSize.Height - 48);
            copyDst.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                if (_translated.Length == 0) { _trState.Text = "还没翻译呢"; return; }
                try { Clipboard.SetText(_translated); _trState.Text = "译文已复制"; } catch { }
            });
            Controls.Add(copyDst);

            RoundButton close = new RoundButton();
            close.Text = "关闭";
            close.Size = new Size(96, 36);
            close.Fill = Color.FromArgb(0, 122, 204);
            close.FillHover = Color.FromArgb(0, 140, 232);
            close.TextColor = Color.White;
            close.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.Location = new Point(ClientSize.Width - 24 - 96, ClientSize.Height - 48);
            close.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;

            ResumeLayout();
        }

        // 翻译丢到后台线程去做：网络慢的时候窗口不能卡死（这就是"别做成鸡肋"的意思）
        void DoTranslate()
        {
            if (_busy) return;
            string text = _src.Text;
            if (text.Trim().Length == 0) { _trState.Text = "没有要翻译的文字"; return; }
            _busy = true;
            _tr.Enabled = false;
            _trState.Text = "翻译中…（用 MyMemory 免费接口，要联网）";
            _dst.Text = "";
            _translated = "";

            string src = text;
            Thread th = new Thread(new ThreadStart(delegate()
            {
                string err;
                string result = Translate.Run(src, out err);
                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        _busy = false;
                        _tr.Enabled = true;
                        if (result == null)
                        {
                            _trState.Text = err ?? "翻译失败";
                            _dst.Text = "（翻译失败：" + (_trState.Text) + "）";
                        }
                        else
                        {
                            _translated = result;
                            _dst.Text = result;
                            _trState.Text = "译文（已可复制）";
                        }
                    }));
                }
                catch { }
            }));
            th.IsBackground = true;
            th.Start();
        }
    }
}

namespace SnapWheel
{
    class AppCtx : ApplicationContext
    {
        Settings _settings;
        Store _store;
        WheelManager _wheels;
        NotifyIcon _tray;
        HotkeyForm _hotkey;
        WheelForm _wheel;
        // 贴在屏幕上的那些图钉（中键点缩略图产生），退出时一起收掉
        readonly System.Collections.Generic.List<PinForm> _pins = new System.Collections.Generic.List<PinForm>();

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
            _wheel.AdminHelpRequested += new EventHandler(OnAdminHelp);
            _wheel.PinRequested += new Action<Bitmap, Point>(OnPin);
            _wheel.ExitRequested += new EventHandler(delegate(object o, EventArgs e2) { Application.Exit(); });

            _tray = new NotifyIcon();
            _tray.Icon = Brand.Get();
            _tray.Text = "SnapWheel 快照轮环";
            _tray.Visible = true;
            Err.Notify = delegate(string msg)             // 出问题时托盘冒个泡，程序继续跑
            {
                if (!_settings.ShowBalloon) return;        // 设置里可以关掉右下角通知
                try { _tray.ShowBalloonTip(4000, "SnapWheel 快照轮环遇到一个问题（已记录）", msg, ToolTipIcon.Warning); }
                catch { }
            };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("截图", null, new EventHandler(OnHotkey));
            menu.Items.Add("导入图片…", null, new EventHandler(OnImport));
            menu.Items.Add("新手引导", null, new EventHandler(OnGuide));
            menu.Items.Add("重播开启动画", null, new EventHandler(delegate(object o, EventArgs e) { _wheel.StartIntro(); }));
            if (Elev.Is)
                menu.Items.Add("管理员模式说明…（拖拽为什么不动）", null, new EventHandler(OnAdminHelp));
            menu.Items.Add("显示/隐藏轮盘", null, new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); }));
            menu.Items.Add("关掉所有贴图", null, new EventHandler(delegate(object o, EventArgs e) { CloseAllPins(); }));
            menu.Items.Add("取字：识别剪贴板里的图", null, new EventHandler(OnOcrClipboard));
            menu.Items.Add("撤销上一次删除", null, new EventHandler(OnUndoDelete));
            menu.Items.Add("管理 Wheel…", null, new EventHandler(OnWheels));
            menu.Items.Add("设置…", null, new EventHandler(OnSettings));
            menu.Items.Add("打开项目主页", null, new EventHandler(delegate(object o, EventArgs e) {
                try { System.Diagnostics.Process.Start("https://github.com/" + AppInfo.Repo); } catch { }
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, new EventHandler(delegate(object o, EventArgs e) { Quit(); }));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); });

            RegisterHotkeyAndNotify();

            // 启动后到后台检查有没有新版本（不挡启动；设置里可以关）
            if (_settings.CheckUpdate)
            {
                System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(CheckUpdate));
                th.IsBackground = true;
                th.Start();
            }

            if (Elev.Is && _settings.ShowBalloon)
                try
                {
                    _tray.ShowBalloonTip(6000, "SnapWheel 快照轮环以管理员身份运行",
                        "Windows 会拦掉管理员进程和桌面/资源管理器之间的拖拽。想在轮盘上拖进拖出图片，请用普通权限运行（托盘右键 → 管理员模式说明）。",
                        ToolTipIcon.Warning);
                }
                catch { }

            // 取字（OCR）引擎在后台焐热：第一次真用的时候就不用等引擎激活那几百毫秒
            Ocr.WarmUpAsync();

            if (_settings.ShowWheelOnStart)
            {
                // 开机一律把轮盘**展开**（带开启动画）。
                // 收起态是"用完自己收起来"的东西，不该让人一开机只看到屏幕边上一小条 ——
                // 新用户会以为没启动，老用户也得先点一下才看得到内容。
                // 收起功能没动：点关闭键（或长按它）照样能收成把手，自动隐藏也照旧。
                _wheel.ShowWheelWithIntro();
            }
            else if (_settings.CollapseMode)
            {
                _wheel.StartCollapsed();     // 就算开机不显示轮盘，也留个贴边把手，否则没法鼠标叫出来
            }

            // 新功能首次提示：中键贴图这条只在轮盘上冒一句 —— 新功能藏在托盘菜单里没人找得到。
            // （管理员那条让位：拖不动的时候会当场弹说明，不缺这一次。）
            if (!_settings.PinHintDone)
            {
                _settings.PinHintDone = true;
                _settings.Save();
                _wheel.ShowToast("新功能：缩略图上按鼠标中键 = 把图钉在屏幕上");
            }
            else if (Elev.Is)
                _wheel.ShowToast("管理员模式：拖拽会被 Windows 拦（托盘右键看说明）");

            // 第一次打开、或者换到没见过的版本：都自动弹一次引导（"看过就不再弹"只对同一版本成立）。
            // 需要自己去托盘里找的引导留不住人，所以升级后也主动亮一次。
            bool firstEver = !_settings.IntroSeen;
            bool newVersion = (_settings.GuideSeenVersion != AppInfo.Version);
            if (firstEver || newVersion)
            {
                _settings.IntroSeen = true;
                _settings.GuideSeenVersion = AppInfo.Version;
                _settings.Save();
                Timer g = new Timer();
                g.Interval = 900;
                g.Tick += new EventHandler(delegate(object o, EventArgs e2)
                {
                    g.Stop(); g.Dispose();
                    try { GuideForm gf = new GuideForm(firstEver); gf.ShowDialog(); } catch { }
                });
                g.Start();
            }
        }

        void OnGuide(object sender, EventArgs e)
        {
            try { GuideForm gf = new GuideForm(); gf.ShowDialog(); } catch { }
        }

        // 贴图（图钉）：轮盘中键点了一张缩略图 -> 在这儿开一个 PinForm 钉在屏幕上。
        // at 是鼠标的屏幕坐标，PinForm 自己会以它为中心摆好、并夹进屏幕范围。
        void OnPin(Bitmap img, Point at)
        {
            try
            {
                PinForm p = new PinForm(img, at);
                p.FormClosed += new FormClosedEventHandler(delegate(object o, FormClosedEventArgs e2)
                {
                    try { _pins.Remove(p); } catch { }
                });
                _pins.Add(p);
                p.Show();
                p.BringToFront();
            }
            catch (Exception ex) { Err.Log("Pin", ex); }
        }

        void CloseAllPins()
        {
            // 复制一份再遍历：FormClosed 里会从 _pins 里移除
            PinForm[] arr = _pins.ToArray();
            for (int i = 0; i < arr.Length; i++) { try { arr[i].Close(); } catch { } }
            _pins.Clear();
        }

        // 撤销上一次删除（后悔药）：把刚删掉/刚清空的那些图放回轮盘。
        // 图一直在工作内存里，所以这里是"立刻"生效的，不需要任何目录或索引。
        void OnUndoDelete(object sender, EventArgs e)
        {
            try
            {
                if (!Undo.CanUndo)
                {
                    MessageBox.Show("没有可撤销的删除。\n\n（只记得住本次运行中最近 " + Undo.MaxBatches + " 次删除，退出程序就清空 —— 需要长期保存的图请拖到文件夹里存好。）",
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string desc = Undo.LastDesc;
                string wheel = "";
                int n = Undo.UndoLast(_wheels, out wheel);
                if (n <= 0)
                {
                    MessageBox.Show("没能放回去（原轮盘可能已经被删掉了）。", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _wheel.RefreshWheel();
                _wheel.ShowToast("已放回 " + n + " 张到「" + wheel + "」" + (string.IsNullOrEmpty(desc) ? "" : "（" + desc + "）"));
            }
            catch (Exception ex) { Err.Log("UndoDelete", ex); }
        }

        // 取字（OCR）：把剪贴板里的图认成文字。懒得截图时最顺手 —— 微信里复制一张图直接取字。
        void OnOcrClipboard(object sender, EventArgs e)
        {
            Bitmap img = null;
            try
            {
                if (Clipboard.ContainsImage()) img = Clipboard.GetImage() as Bitmap;
            }
            catch (Exception ex) { Err.Log("OcrClipboard", ex); }
            if (img == null)
            {
                try
                {
                    MessageBox.Show("剪贴板里没有图片。先复制一张图（或截图），再来点这里。",
                        AppInfo.Name + " 取字", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
                return;
            }
            string err = null, txt = null;
            Cursor prev = null;
            try { prev = Cursor.Current; Cursor.Current = Cursors.WaitCursor; } catch { }
            try { txt = Ocr.Recognize(img, out err); }
            catch (Exception ex) { err = ex.Message; }
            finally { try { Cursor.Current = prev; } catch { } try { img.Dispose(); } catch { } }

            if (txt == null)
            {
                try { MessageBox.Show(err ?? "识别失败了", AppInfo.Name + " 取字", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
                return;
            }
            try
            {
                using (OcrForm of = new OcrForm(txt)) { of.ShowDialog(); }
            }
            catch (Exception ex) { Err.Log("OcrForm", ex); }
        }

        // 管理员模式说明框：托盘菜单、"拖不动"的那一刻都走这里。
        // 点「以普通权限重启」= 让资源管理器拉起自己（拿到 Medium 完整性级别）然后退出当前实例。
        void OnAdminHelp(object sender, EventArgs e)
        {
            bool restart = false;
            try
            {
                using (AdminForm af = new AdminForm())
                {
                    bool wasTop = _wheel.TopMost;
                    _wheel.TopMost = false;              // 轮盘别盖在弹框上面
                    af.TopMost = true;
                    restart = (af.ShowDialog() == DialogResult.OK);
                    _wheel.TopMost = wasTop;
                }
            }
            catch { }

            if (!restart) return;
            if (Elev.RelaunchNormal()) Quit();
            else
            {
                try
                {
                    MessageBox.Show("没能自动重启。请关掉 SnapWheel，再右键 SnapWheel.exe →「以普通权限运行」。",
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            }
        }

        // 托盘「导入图片…」：不想拖的时候也能从任意位置选图加进当前 wheel
        // 检查 GitHub Releases 有没有新版本：只提示，绝不自动下载/替换
        void CheckUpdate()
        {
            try
            {
                // .NET 4.0 默认只开 TLS 1.0，GitHub 会直接拒绝 —— 必须显式开 TLS 1.2
                System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;
                System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                    "https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest");
                req.UserAgent = AppInfo.Name + "/" + AppInfo.Version;
                req.Timeout = 8000;
                using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                {
                    string body = sr.ReadToEnd();
                    System.Text.RegularExpressions.Match m =
                        System.Text.RegularExpressions.Regex.Match(body, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
                    if (!m.Success) return;
                    string remote = m.Groups[1].Value;
                    if (!NewerVersion(remote, AppInfo.Version)) return;
                    string msg = "有新版本 v" + remote + "（当前 v" + AppInfo.Version + "）。右键托盘图标 →「打开项目主页」可以下载。";
                    try
                    {
                        _wheel.BeginInvoke((MethodInvoker)delegate
                        {
                            try { if (_settings.ShowBalloon) _tray.ShowBalloonTip(8000, "SnapWheel 快照轮环 有新版本", msg, ToolTipIcon.Info); } catch { }
                        });
                    }
                    catch { }
                }
            }
            catch { }   // 没网 / 超时 / 被拦，都静默失败，绝不打扰使用
        }

        static bool NewerVersion(string a, string b)
        {
            try
            {
                string[] x = a.Split('.');
                string[] y = b.Split('.');
                for (int i = 0; i < 3; i++)
                {
                    int xi = 0, yi = 0;
                    if (i < x.Length) int.TryParse(x[i], out xi);
                    if (i < y.Length) int.TryParse(y[i], out yi);
                    if (xi != yi) return xi > yi;
                }
            }
            catch { }
            return false;
        }

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
                // 热键提示只在"第一次运行"或"注册失败"时弹，平时开机不打扰
                if ((_settings.ShowBalloon && !_settings.IntroSeen) || !ok)
                    _tray.ShowBalloonTip(3000, "SnapWheel 快照轮环", tip, ToolTipIcon.Info);
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
                RegisterHotkeyAndNotify();
                // 界面侧收尾都在这里：以前这句里还夹着一句 HideWheel()，
                // 结果每次点设置里的确定，轮盘都当场消失（详见 WheelForm.AfterSettingsApplied）
                _wheel.AfterSettingsApplied();
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

            // 第一次用截图浮层：让工具条旁边亮一次"能标注"的提示（只亮这一次）
            if (!_settings.AnnotHintDone)
            {
                _settings.AnnotHintDone = true;
                _settings.Save();
            }
            OverlayForm ov = new OverlayForm(vs, shot, _settings);
            ov.ShowDialog();
            if (ov.Result != null)
            {
                Store st = _wheels.ActiveStore;
                StoreItem ni = st.Add(ov.Result);
                _wheel.MarkNew(ni);              // only the brand-new shot plays the slide-in
                // 关键：浮层关掉之后重抓一次背景。
                // 之前是拿着"截图浮层还在时抓的"背景去显示玻璃，所以截图完轮盘是暗的，
                // 过一会儿定时刷新才突然变亮 —— 现在这里立刻换新背景（带淡入过渡）。
                try { _wheel.RequestBackdropAsync(); } catch { }
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
            try { CloseAllPins(); } catch { }     // 贴图不是主窗口，不留着它们挡住桌面
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
