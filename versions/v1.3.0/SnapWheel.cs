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
using System.Globalization;
using System.Drawing.Text;
using System.Threading;
using Microsoft.VisualBasic.FileIO;
using System.Net;
using System.Diagnostics;
using System.Net.Security;

namespace SnapWheel
{
    static class AppInfo
    {
#if NO_KEY
        public const string Version = "0.2.22";   // 变体：多 Wheel + 框选缩放/锁定（无万能键）★ 0.2 线最终版
#else
        public const string Version = "1.3.0";   // 支持 Windows 7（随包本地取字组件）+ 取字引擎可在界面里切换
#endif
        public const string Author = "exper7";
        public const string Name = "SnapWheel";
        public const string CnName = "快照轮环";        // 正式中文名（0.4.7 起）
        public const string Repo = "ExpertKT/SnapWheel";  // 自动更新检查用

        // ---------- 版本比较（全项目唯一一份）----------
        // 为什么必须只有一份：两处都要它 ——
        //   ① 更新检查：「远端这个版本比本地新吗」；
        //   ② 引导窗口：「这条说明的加入版本，比用户上次看过的那版新吗」—— 新的才标【新】。
        // 两处各写一份迟早会不一致（这正是反例 #1「度量与绘制同源」的同一个形状）。
        // 所以放在最底层的 AppInfo 里，Update 也回头来调这里。
        public static int[] ParseVer(string s)
        {
            int[] r = new int[3];
            if (string.IsNullOrEmpty(s)) return r;
            s = s.TrimStart('v', 'V');
            string[] parts = s.Split('.', '-', '+');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                int v = 0;
                int.TryParse(parts[i], System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out v);
                r[i] = v;
            }
            return r;
        }

        /// <summary>a 是不是比 b 新（"0.9.4" 比 "0.9.3" 新 → true）。</summary>
        public static bool IsNewer(string a, string b)
        {
            int[] x = ParseVer(a), y = ParseVer(b);
            for (int i = 0; i < 3; i++)
            {
                if (x[i] != y[i]) return x[i] > y[i];
            }
            return false;
        }
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
        // ⚠️ 只记「最慢帧」的状态会**骗人**：最慢那一帧往往正好是"刚换完底、正在做混合"的那一帧，
        // 于是状态里永远显示着"交叉淡入没走完"，看着像根因，其实是采样偏差（我就差点据此修错地方）。
        // 再记一份**最后一帧**的状态，两者差得远就说明"最慢帧"不能代表常态。
        static string _lastState = "";
        static DateTime _windowStart = DateTime.Now;
        static int _lines;

        public static void Sample(double ms, string state)
        {
            try
            {
                lock (_lock)
                {
                    _frames++; _sum += ms;
                    _lastState = state;
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
                        _max.ToString("0.0") + "ms，tick=" + WheelForm.AnimTickCountForTest +
                        " | 最慢帧状态: " + _maxState +
                        " ||| 最后一帧状态: " + _lastState));
                }
                catch { }
            }
            _frames = 0; _slow = 0; _sum = 0; _max = 0; _maxState = ""; _lastState = ""; _windowStart = DateTime.Now;
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
        [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int index);   // Win2000+：Win7 上拿 DPI 只能靠它
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        public const uint SRCCOPY = 0x00CC0020;
        // 0.6.0 滚动长截图：给目标窗口发合成的滚轮消息（这是产品功能，不是测试里的模拟输入）
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        public const uint WM_MOUSEWHEEL = 0x020A;
        // 置顶但**不激活**：TopMost=false/true 或 BringToFront() 会抢一次前台焦点，
        // 那样托盘右键菜单、打赏窗口这些"一失焦就自动关闭"的界面点出来就会过一会儿自己没了。
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        public const uint WDA_NONE = 0x00;                 // 恢复正常（能被录屏/截图拍到）
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        // 剪贴板序号：剪贴板内容一变这个计数器就往前走（系统给的全局值，同一会话里所有进程共享）。
        // 用它判断"这次 WM_CLIPBOARDUPDATE 是不是我们自己刚写出来的那一下"，比把整张图读回来算指纹便宜得多
        // （读一张 1600x1000 实测 ~10ms，正好落在"缩略图滑入"那几帧上）。返回 0 表示读不到，调用方要兜底。
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();

        // ==================== 任务栏状态（0.9.11 轮盘靠边方式） ====================
        // 为什么需要它：Windows 的"工作区"**总是**把任务栏那一条扣掉，
        // 哪怕任务栏是自动隐藏的也照样预留（本机实测：Bounds 1707×1067，WorkingArea 1707×1019，
        // 底下那 48px 明明看不见、鼠标一碰就冒出来，可工作区就是不给）。
        // 结果自动隐藏任务栏的用户看到的轮盘底下总悬着一条缝，像"没靠到底"。
        // ABM_GETSTATE 拿到的是 appbar 状态位，ABS_AUTOHIDE 就是"当前处于自动隐藏模式"。
        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
        public const uint ABM_GETSTATE = 0x00000004;
        public const int ABS_AUTOHIDE = 0x0000001;

        // 任务栏现在是不是"自动隐藏"状态。读不到就一律当"不是"（保守：宁可留一条缝，也不要盖住任务栏）。
        public static bool TaskbarAutoHide()
        {
            try
            {
                APPBARDATA d = new APPBARDATA();
                d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
                IntPtr r = SHAppBarMessage(ABM_GETSTATE, ref d);
                return ((long)r & ABS_AUTOHIDE) != 0;
            }
            catch { return false; }
        }

        // 纯判断，不碰任何系统状态 —— 这样 tests\edge-anchor-test.cs 能把六种组合全跑一遍。
        // reserved = 工作区确实比屏幕小（有东西占了一条），autohide = 任务栏正处于自动隐藏。
        public static bool AnchorIsScreen(string mode, bool reserved, bool autohide)
        {
            if (mode == "screen") return true;
            if (mode == "work") return false;
            return reserved && autohide;      // auto
        }

        // 轮盘该贴着哪块矩形靠边（mode = Settings.EdgeAnchor："auto" / "screen" / "work"）。
        //   screen：永远贴屏幕物理边（任务栏不是自动隐藏时会被压住一角）
        //   work  ：永远贴工作区边（永远避让任务栏，也就是老行为）
        //   auto  ：工作区确实被扣掉了一块、且任务栏正处于自动隐藏 → 贴屏幕边；否则贴工作区边
        public static Rectangle AnchorRect(string mode)
        {
            Rectangle b, w;
            try { b = Screen.PrimaryScreen.Bounds; w = Screen.PrimaryScreen.WorkingArea; }
            catch { return new Rectangle(0, 0, 1024, 768); }
            bool reserved = (w.Width < b.Width) || (w.Height < b.Height);
            bool autohide = reserved && TaskbarAutoHide();
            return AnchorIsScreen(mode, reserved, autohide) ? b : w;
        }


        // 优先"每显示器 DPI 感知 v2"：多屏不同缩放时不会把窗口拉伸糊掉，坐标也按物理像素走
        public static void SetDpiAwarenessBest()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }   // PER_MONITOR_AWARE_V2
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }                  // PER_MONITOR_DPI_AWARE
            try { SetProcessDPIAware(); } catch { }                                        // 退回到系统 DPI 感知
        }

        // 当前进程的缩放比例（1.0 = 96dpi）
        //
        // 为什么每一步都得自己一个 try：GetDpiForWindow / GetDpiForSystem 是 Win10 1607 才有的导出，
        // **Win7 上调用会抛 EntryPointNotFoundException**。原来三个调用挤在同一个 try 里，
        // Win7 上第一个就抛、直接返回 1f —— 于是 125% 缩放的老机器上整个界面按 100% 画，偏小。
        // 最后那条 GetDeviceCaps(LOGPIXELSX) 是 Win2000 就有的老路，Win7 上真正管用的就是它。
        public static float DpiScaleOf(IntPtr hwnd)
        {
            uint d = 0;
            try { if (hwnd != IntPtr.Zero) d = GetDpiForWindow(hwnd); } catch { }      // Win10 1607+
            if (d == 0) { try { d = (uint)GetDpiForSystem(); } catch { } }             // Win10 1607+
            if (d == 0)
            {
                IntPtr dc = IntPtr.Zero;
                try
                {
                    dc = GetDC(IntPtr.Zero);
                    if (dc != IntPtr.Zero) d = (uint)GetDeviceCaps(dc, 88);            // LOGPIXELSX = 88
                }
                catch { }
                finally { if (dc != IntPtr.Zero) { try { ReleaseDC(IntPtr.Zero, dc); } catch { } } }
            }
            if (d == 0) d = 96;
            return d / 96f;
        }
        // ==================== 传递模式（0.9.0）需要的三个 API ====================
        //
        // 关于"怎么知道用户按了 WASD"：项目里**不用**低级键盘钩子（WH_KEYBOARD_LL）——
        // 钩子会进到系统输入链里，容易被安全软件当成可疑行为，而且必须自己保证一定卸载。
        // 这里改成**定时器轮询** GetAsyncKeyState：同样能拿到"这个键现在是不是按着"，
        // 代码少得多、也不需要任何全局钩子。代价是有一点点轮询开销（每 15ms 查几个键，可忽略）。

        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

        // 把真实光标移到指定屏幕坐标 —— 传递模式"放下"时要让目标程序看到光标就在那里
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);

        // 模拟鼠标按键/移动。mouse_event 虽然被官方标成"过时"（推荐 SendInput），
        // 但它更简短、在 32/64 位下都能直接用，功能和 SendInput 的鼠标部分是等价的。
        // 传递模式只用它做一件事：把"按下 → 移动 → 松开"这个真实拖放过程演给目标程序看。
        [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

        public const uint MOUSEEVENTF_MOVE     = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP   = 0x0004;

        // 把某个窗口拉到前台。传递模式"放下"时必须先做这一步 ——
        // 轮盘现在是不激活窗口（当初为了不抢别人的焦点、让用户能 Alt+Tab 切过去），
        // 而 Windows 有个规则：**非活动窗口的第一次点击会被系统用来"激活那个窗口"，应用收不到**。
        // 我们的模拟点击正是那"第一次点击"，于是被白白消耗掉，轮盘永远等不到"按下"，
        // OLE 拖放（DoDragDrop）也就永远不会启动。
        // 用户实测的现象"鼠标从起点移到终点、然后什么都没发生"，根因就在这里。
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

        // ==================== 低级键盘钩子（传递模式用） ====================
        //
        // 为什么必须用它：传递模式读按键用的是"轮询"（GetAsyncKeyState），轮询**只看状态、不拦截**，
        // 所以用户按 WASD 时那些字母照常送给了前台窗口 —— 输入法弹出来、字母也被打进去了（用户实测）。
        // 要拦下来只能在系统输入链上装钩子，把属于传递模式的那几个键吃掉。
        //
        // 注意：钩子必须**一定卸载**（放在 OnFormClosed 里），否则会一直挂在那里影响全局输入。
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        public const int WH_KEYBOARD_LL = 13;
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
    // ============================ 省电模式（0.5.3） ============================
    // 用户的笔记本长期在电池上跑，这个程序有两处持续耗电：
    //   ① 毛玻璃底**每 3.5 秒**抓屏 + 模糊一次（几十毫秒 CPU，实测抓屏同步要 12~16ms，模糊在后台线程）；
    //   ② 动画定时器 15ms 一帧的持续重绘（有动画时每帧都要重画整窗）。
    // 省电模式（设置里默认开）：**只在电池供电时**做两件事 —— 暂停"定时重抓玻璃底"、把重绘**隔帧**一次。
    //
    // 电池侦测：`SystemInformation.PowerStatus.PowerLineStatus == Offline`（在电池上）。
    //   ⚠️ 结果**缓存 5 秒**：这个属性每次都要问系统，绝不能每帧/每次 tick 去调；
    //      缓存后"插电 / 拔电"最多 5 秒内生效（用户感知不到，也不会白烧 CPU）。
    //   ⚠️ 测试用 `ForceOnBatteryForTest`（照 `Elev.ForceForTest` 的路子）：不然"电池下会怎样"
    //      永远只能在真拔了电的机器上手测。
    static class Power
    {
        public static bool? ForceOnBatteryForTest = null;   // true=当电池，false=当插电，null=按真实状态

        static bool _onBattery;
        static DateTime _at = DateTime.MinValue;
        const double CacheSec = 5.0;

        public static bool OnBattery()
        {
            if (ForceOnBatteryForTest.HasValue) return ForceOnBatteryForTest.Value;
            if ((DateTime.Now - _at).TotalSeconds < CacheSec) return _onBattery;
            bool v;
            try { v = (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline); }
            catch { v = false; }                 // 读不到就按"插电"处理：宁可费点电，也别把功能省没了
            _onBattery = v;
            _at = DateTime.Now;
            return v;
        }

        // 测试/工具用：把缓存作废（换完状态立刻生效，不用等 5 秒）
        public static void ResetCacheForTest()
        {
            _at = DateTime.MinValue;
        }
    }
}

namespace SnapWheel
{
    // ======================= DPI 缩放（0.5.3 修订 / 0.6.0 全面铺开） =======================
    // 为什么需要它：本程序是 per-monitor DPI aware 的，所以 150% 缩放下**字体是按 DPI 放大渲染的**，
    // 而代码里写死的像素（位置 / 尺寸 / 行高 / 边距）不会跟着变 —— 结果就是文字比格子大：
    // 长句被裁掉右半边、按钮被挤出窗口、说明文字只剩一行。用户报的「凡是涉及界面的都显示不全」
    // 就是这个根因（设置窗口在 0.5.3 里已经单独修过，其余对话框当时没跟上）。
    //
    // 约定（和 75-SettingsForm 里那套一模一样，**别再发明第二套**）：
    //   · **长度类**（x / y / 宽 / 高 / 行高 / 边距 / 按钮尺寸）一律过 S() 乘 K；
    //   · **字体磅值不乘** —— GDI+ 已经按 DPI 渲染过一遍，再乘就是双倍放大；
    //   · 说明性文字的 Label 尽量交给 Wrap()：AutoSize + MaximumSize(宽, 0) 让它自己折行、
    //     自己报 PreferredHeight。这比"把高度算准"可靠得多 —— 文字长一点、缩放换一档都不会裁。
    static class Ui
    {
        static float _k = -1f;

        // 测试/截图工具用：强行指定缩放系数（0 = 按真实 DPI）
        public static float ForceKForTest = 0f;

        public static float K
        {
            get
            {
                if (_k < 0f) _k = Calc();
                return _k;
            }
        }

        static float Calc()
        {
            float k;
            try { k = ForceKForTest > 0f ? ForceKForTest : Native.DpiScaleOf(IntPtr.Zero); }
            catch { k = 1f; }
            if (!(k >= 1f)) k = 1f;      // 小于 100% 不缩：缩了字更小、反而更看不清
            if (k > 3f) k = 3f;
            return k;
        }

        public static void ResetCacheForTest() { _k = -1f; }

        public static int S(int v) { return (int)Math.Round(v * K); }
        public static int S(float v) { return (int)Math.Round(v * K); }
        public static Size Sz(int w, int h) { return new Size(S(w), S(h)); }
        public static Point Pt(int x, int y) { return new Point(S(x), S(y)); }
        public static Padding Pad(int all) { return new Padding(S(all)); }
        public static Padding Pad(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }

        // 把一段说明文字变成"自己会折行、自己报高度"的标签：
        // maxW 是它允许占的最大宽度（**已经乘过 K 的物理像素**，传窗体的内容宽）。
        public static Label Wrap(Label l, int maxW)
        {
            l.AutoSize = true;
            l.MaximumSize = new Size(maxW, 0);
            return l;
        }

        // 单行不折行的标签（标题这类）：AutoSize 就够了，宽度也别卡死
        public static Label OneLine(Label l)
        {
            l.AutoSize = true;
            return l;
        }

        // 一行文字在给定宽度下要占多高（AutoSize=false 的 Label 想手动排时用）
        public static int TextH(Control c, string text, int w)
        {
            try
            {
                Size s = TextRenderer.MeasureText(text, c.Font, new Size(w, int.MaxValue),
                                                  TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                return Math.Max(c.Font.Height, s.Height);
            }
            catch { return c.Font.Height; }
        }
    }
}

namespace SnapWheel
{
    // ==================== 界面语言（0.6.0 第一轮 i18n） ====================
    // 设计取舍：**不做 key → 文案 的字典**，而是 Lang.T("中文", "English") 就地双写。
    //   · 好处：改造一处只需把字符串包一层，不用先在字典里登记、也不会出现"键对不上"；
    //     翻译和代码在同一个地方，读代码时能立刻看到两种语言，漏翻一眼就能发现。
    //   · 代价：字符串在源码里出现两次（可接受 —— 这个程序本来就是单文件、零依赖的路线）。
    //
    // ⚠️ 语言**在启动时确定**，切换后需要重启生效：界面文字散布在几十个窗口/绘制代码里，
    //    运行时热切换要重建所有已打开的窗口，风险远大于收益。设置里会明确提示"重启后生效"。
    static class Lang
    {
        static string _cur = "zh";

        public static void Init(string code)
        {
            _cur = (code != null && code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ? "en" : "zh";
        }

        public static string Cur { get { return _cur; } }
        public static bool En { get { return _cur == "en"; } }

        // 界面文字：En 为 true 时取英文
        public static string T(string zh, string en)
        {
            return _cur == "en" ? en : zh;
        }

        // 系统语言猜一个默认值（首次运行、用户还没选过时用）
        public static string Guess()
        {
            try
            {
                string n = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                return n == "zh" ? "zh" : "en";
            }
            catch { return "zh"; }
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
    // ==================== 绘制度量层（0.8.0） ====================
    //
    // 为什么要有这一层：项目里同一个错误犯了四次，全都是「量的时候用一套参数、画的时候用另一套」——
    //   ① 竖版宣传图：用 GDI+ 的 MeasureString 量、却用另一种方式画 → 实际更宽 → 右边缘被切；
    //   ② 表情面板：格子按字号算，而实际行高远大于字号 → 表情被裁；
    //   ③ 符号标注：量框用中文字体、画的时候用符号字体 → 宽高不同 → 框和符号错位；锚点也不一致；
    //   ④ GDI 与 GDI+ 混用：用 TextRenderer(GDI) 画，却期望它响应 GDI+ 的旋转/平移变换 ——
    //      它完全不理会，于是预览正常、一合成到成品图就跑到图外。
    //
    // 根子是「约定没有被强制」：调用方可以自由地"这次这样量、那次那样画"。
    // 这一层的做法：把「量」的参数和结果封装成一个 Fit 值，**画的时候只接受 Fit** ——
    //   想不同源都做不到（因为画的时候拿不到字体/字号，只能从 Fit 里取）。
    //
    // 另一个决定：**全线用 GDI+（Graphics.DrawString / Graphics.MeasureString），不用 GDI（TextRenderer）**。
    //   原因是实测（见 docs/LEARNING.md §2.4）：GDI 的 DrawText 会沿整个目标表面处理裁剪区域，
    //   在 2560×1440 的位图上单次要 9.2ms，而同样操作在小位图上只要 0.43ms —— 相差 21 倍；
    //   GDI+ 的 DrawString 与目标大小无关（0.015ms）。
    //   两者都用 GDI+，量出来的宽度和画出来的宽度才自然是同一套。
    //
    // 用法：
    //   var fit = DrawKit.Measure(g, text, 12, DrawKit.UI, FontStyle.Regular);
    //   DrawKit.Draw(g, fit, box, Color.White, Align.Center);
    // 或者一步到位（超宽会自动缩字号）：
    //   DrawKit.DrawFitted(g, text, box, Color.White, 14, box.Width, DrawKit.UI, FontStyle.Bold, Align.Center);

    enum Align { Near, Center, Far }

    static class DrawKit
    {
        public const string UI = "Microsoft YaHei UI";      // 界面常用
        public const string Symbol = "Segoe UI Symbol";     // 符号（①②★→ 这类）

        // 一次「量」的结果。它带着画的时候需要的一切 ——
        // 调用方拿不到单独的字号，也就没法"另起一套"去画。
        public struct Fit
        {
            public string Text;
            public string FontName;
            public int Pt;
            public FontStyle Style;
            // GDI+ MeasureString 用的是「含行距的排版框」，和 DrawString 的矩形语义一致；
            // 这里存的是排版框尺寸（用它对齐才不会偏）。
            public SizeF Layout;
            public bool Valid;
        }

        static FontStyle SafeStyle(string fontName, FontStyle st)
        {
            // 有些字体没有 Bold/Italic 变体，构造时会抛异常或静默回退，这里统一兜一下
            try
            {
                using (Font f = new Font(fontName, 12f, st)) { }
                return st;
            }
            catch { return FontStyle.Regular; }
        }

        // ---- 量 ----
        public static Fit Measure(Graphics g, string text, int pt, string fontName, FontStyle style)
        {
            Fit fit = new Fit();
            if (string.IsNullOrEmpty(text) || pt <= 0 || g == null) { fit.Valid = false; return fit; }
            if (pt < 6) pt = 6;
            if (pt > 400) pt = 400;
            try
            {
                fontName = string.IsNullOrEmpty(fontName) ? UI : fontName;
                style = SafeStyle(fontName, style);
                using (Font f = new Font(fontName, pt, style))
                {
                    // StringFormat.GenericTypographic 更贴近实际绘制宽度；默认那个会多留边距
                    SizeF sz = g.MeasureString(text, f, new PointF(0, 0), StringFormat.GenericTypographic);
                    fit.Text = text; fit.FontName = fontName; fit.Pt = pt; fit.Style = style;
                    fit.Layout = sz; fit.Valid = sz.Width > 0 && sz.Height > 0;
                }
            }
            catch { fit.Valid = false; }
            return fit;
        }

        // 量的时候用 GDI+，所以需要一个 Graphics。没有的话给个 1x1 的临时画布（结果一样）。
        public static Fit Measure(string text, int pt, string fontName, FontStyle style)
        {
            using (Bitmap b = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(b))
                return Measure(g, text, pt, fontName, style);
        }

        // ---- 画 ----
        // 只接受 Fit：字体、字号、样式都从它里面取，调用方无法"另起一套"。
        public static void Draw(Graphics g, Fit fit, RectangleF box, Color color, Align align)
        {
            if (!fit.Valid || g == null || string.IsNullOrEmpty(fit.Text)) return;
            DrawInternal(g, fit.Text, fit.Pt, fit.FontName, fit.Style, box, color, align);
        }

        // 一步到位：量 → 若超宽就按比例缩字号 → 画。
        // 「超宽自动缩」是画宣传图、竖版图时反复需要的东西，收进来省得每处各写一遍。
        public static void DrawFitted(Graphics g, string text, RectangleF box, Color color,
                                      int pt, int maxW, string fontName, FontStyle style, Align align)
        {
            if (g == null || string.IsNullOrEmpty(text) || box.Width <= 1) return;
            if (pt < 6) pt = 6;
            if (maxW <= 0) maxW = (int)box.Width;
            try
            {
                fontName = string.IsNullOrEmpty(fontName) ? UI : fontName;
                style = SafeStyle(fontName, style);
                // 超宽就缩。注意：字号与宽度**不是线性关系**（字体渲染有舍入和 hinting），
                // 按比例算一次往往还差几个像素 —— 测试就抓到过这个：773px 的文本缩进 192px 的框，
                // 按比例算完仍溢出 8px。所以这里**迭代缩小 + 留 2% 余量**，最多试 4 轮。
                int use = pt;
                for (int iter = 0; iter < 6; iter++)
                {
                    SizeF sz;
                    using (Font probe = new Font(fontName, use, style))
                        sz = g.MeasureString(text, probe, new PointF(0, 0), StringFormat.GenericTypographic);
                    if (sz.Width <= maxW || use <= 6) break;
                    int next = (int)Math.Floor(use * (maxW / sz.Width) * 0.90);   // 留 10% 余量：小字号下 MeasureString 的舍入误差占比很大（测试实测仍溢出 8px）
                    if (next >= use) next = use - 1;
                    use = Math.Max(6, next);
                }
                // 缩到最小字号仍放不下（文字太长或框太窄）→ 逐字截断 + 省略号。
                // UI 常识：宁可少显示几个字，也不要让文字压到别的控件上。
                // 这条是被自动化测试逼出来的：测试里 30 个中文字塞进 192px 的框，
                // 即使缩到下限 6pt 也需要约 232px —— 原来的实现只会一直溢出。
                string show = text;
                using (Font pf = new Font(fontName, use, style))
                {
                    SizeF sz2 = g.MeasureString(show, pf, new PointF(0, 0), StringFormat.GenericTypographic);
                    if (sz2.Width > maxW)
                    {
                        int n = show.Length;
                        while (n > 1)
                        {
                            n--;
                            string t2 = show.Substring(0, n) + "…";
                            if (g.MeasureString(t2, pf, new PointF(0, 0), StringFormat.GenericTypographic).Width <= maxW) { show = t2; break; }
                        }
                    }
                }
                DrawInternal(g, show, use, fontName, style, box, color, align);
            }
            catch { }
        }

        // 内部唯一的绘制出口：量和画都在这条路径上，不可能不同源
        static void DrawInternal(Graphics g, string text, int pt, string fontName, FontStyle style,
                                 RectangleF box, Color color, Align align)
        {
            using (Font f = new Font(fontName, pt, style))
            {
                // ⚠️ 必须先把框撑够高，否则**一行都画不出来**。
                //
                // 实测（探针逐档试出来的）：
                //   · 30pt 的中文，`MeasureString` 报 **50.8**，而 `DrawString` 实际需要 **≥52**；
                //   · box 高 50 → 墨迹 **0**（整行消失）；box 高 52 → 正常输出。
                // 也就是说差这 1.2px，`DrawString` **不裁、不缩，直接什么都不画** ——
                // 调用方以为自己只是"框小了一点"，实际看到的是"文字没了"。
                //
                // 所以这里**用 `Font.Height`（字体行高）来撑框，并多留 2px 余量**，
                // 不要用 `MeasureString` 的高度 —— 又一次是"量的高度 ≠ 画需要的高度"。
                float need = f.Height + 2f;
                if (need > box.Height)
                {
                    float cy = box.Y + box.Height / 2f;
                    box = new RectangleF(box.X, cy - need / 2f, box.Width, need);
                }

                StringFormat fmt = new StringFormat(StringFormat.GenericTypographic);
                fmt.Alignment = (align == Align.Near) ? StringAlignment.Near
                              : (align == Align.Center) ? StringAlignment.Center : StringAlignment.Far;
                fmt.LineAlignment = StringAlignment.Center;
                fmt.FormatFlags |= StringFormatFlags.NoWrap;
                using (SolidBrush b = new SolidBrush(color))
                    g.DrawString(text, f, b, box, fmt);
            }
        }

        // ---- 常用组合：胶囊/按钮上的一行字（居中 + 自动缩） ----
        public static void ChipText(Graphics g, string text, RectangleF box, Color color, int pt, FontStyle style)
        {
            DrawFitted(g, text, box, color, pt, (int)(box.Width - 8), UI, style, Align.Center);
        }

        // ---- 常用组合：左对齐的一行说明（不缩，超了就让它溢出，方便发现排版问题） ----
        public static void Line(Graphics g, string text, float x, float y, int pt, Color color, FontStyle style)
        {
            if (g == null || string.IsNullOrEmpty(text)) return;
            try
            {
                using (Font f = new Font(UI, pt, SafeStyle(UI, style)))
                {
                    SizeF sz = g.MeasureString(text, f, new PointF(0, 0), StringFormat.GenericTypographic);
                    using (SolidBrush b = new SolidBrush(color))
                        g.DrawString(text, f, b, new RectangleF(x, y, sz.Width + 2, sz.Height + 2), StringFormat.GenericTypographic);
                }
            }
            catch { }
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
            return CollectCore(paths, cap, true);
        }

        // 拖放用：跟 Collect 一样展开文件夹，但**不过滤** —— PDF / zip / 文档都收得进来。
        // 1.3.0 起环上能放别的东西：能不能当图读由 Store.ImportDropped 一个地方判（读不出就是文件格），
        // 不再在"收集候选"这一步就把非图片扔掉。
        public static List<string> CollectAll(string[] paths, int cap)
        {
            return CollectCore(paths, cap, false);
        }

        static List<string> CollectCore(string[] paths, int cap, bool imagesOnly)
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
                            if (!imagesOnly || IsImageExt(fs[k])) ok.Add(fs[k]);
                    }
                    else if (File.Exists(p) && (!imagesOnly || IsImageExt(p))) ok.Add(p);
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
            // ⚠ 解码器必须收尾，缓存方式也不能用 OnDemand（1）：
            //   BitmapDecoder.Create(Uri, …) 是懒读的，它会**一直握着那个文件的读句柄**
            //   （OnDemand / Default 都把流留到解码器被 GC 才关），而且**解码失败时那个流
            //   一样不会被顺手关掉**。实测后果：拖进来一个普通文件（.txt / .pdf / .zip ——
            //   我们都会先"试着当图片读一次"）之后，本进程就把**用户的原文件**锁住了，
            //   紧接着的「送回收站」必然报「另一个程序正在使用此文件」
            //   → 「移进来」永远做不到，用户看到的是一句没法解释的提示。
            //   改用 OnLoad（2：整帧读进内存、随后关流），并在 finally 里把解码器 Dispose。
            object dec = null;
            try
            {
                Assembly pc = WicAsm();
                if (pc == null) return null;
                Type tDec = pc.GetType("System.Windows.Media.Imaging.BitmapDecoder", false);
                Type tOpt = pc.GetType("System.Windows.Media.Imaging.BitmapCreateOptions", false);
                Type tCch = pc.GetType("System.Windows.Media.Imaging.BitmapCacheOption", false);
                Type tEnc = pc.GetType("System.Windows.Media.Imaging.PngBitmapEncoder", false);
                if (tDec == null || tOpt == null || tCch == null || tEnc == null) return null;

                MethodInfo create = tDec.GetMethod("Create", new Type[] { typeof(Stream), tOpt, tCch });
                if (create == null) return null;
                // **不把文件名交给 WIC**：它的 Create(Uri, …) 会自己开一个文件流，
                // 解码失败时那个流不会被关掉（实测：一个 .txt 只走过它一次，本进程就把
                // 用户的原文件锁住了 —— 紧接着的「送回收站」报"另一个程序正在使用此文件"，
                // "移进来"因此永远做不到）。自己读进内存再交出去，句柄在我们手里、立刻关。
                byte[] raw;
                try { raw = File.ReadAllBytes(path); } catch { return null; }
                using (MemoryStream src = new MemoryStream(raw))
                {
                    dec = create.Invoke(null, new object[] {
                        src, Enum.ToObject(tOpt, 0), Enum.ToObject(tCch, 2) });
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
            }
            catch { return null; }
            finally
            {
                // 不管成功、失败还是提前 return，都把解码器（连同它可能握着的流）关掉
                IDisposable dd = dec as IDisposable;
                if (dd != null) { try { dd.Dispose(); } catch { } }
            }
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
    // ==================== "这张剪贴板是我们自己写的"登记簿 + 往剪贴板写图的唯一入口 ====================
    //
    // 两件事，都是被同一个功能逼出来的（截图"同时复制到剪贴板"）：
    //
    // 1) 别把自己的图又收一盘。轮盘一直挂着剪贴板监听（WheelForm 的 WM_CLIPBOARDUPDATE →
    //    OnClipboardChanged，设置项 ClipboardImport 默认开），我们自己写进去的成品图会被它
    //    当成"外面复制的新图"再收一次 —— 截一次图出两张缩略图。谁写谁登记，监听先比对。
    //    故意**不用**"写完 N 毫秒内忽略"那种时间窗：它会把用户这段时间里真正复制的一张图也吞掉，
    //    而且窗口一过就失效（截图浮层是模态的，消息什么时候被泵到并不确定）。
    //
    // 2) 别在 UI 线程上写。一张 1600x1000 的图，PNG 编码 ~35ms + OLE 把 Bitmap 刷成 DIB ~45ms
    //    —— 实测整条路径在 UI 线程上要 80ms，落在"缩略图滑入"的帧上就是一帧 50ms
    //    （探针实测：基线每帧 2.4ms，写剪贴板那一帧 50.2ms；丢到工作线程后最慢 10.3ms）。
    //    所以写入交给一个**专用 STA 工作线程**（OLE 剪贴板只能在 STA 上碰），UI 线程只付一次
    //    位图拷贝（实测 ~7ms）—— 这次拷贝是必须的：轮盘那几帧正拿着同一张 GDI+ 位图在画缩略图，
    //    后台线程再去编码它，探针里直接撞出"对象当前正在其他地方使用"。
    //
    // 判定用两道，先便宜后兜底：
    //   ① 剪贴板序号（GetClipboardSequenceNumber）：没变就是我们自己刚写的那一下 → 直接跳过，
    //      **连图都不用读**（读一张 1600x1000 要 ~10ms，也落在动画帧上）；
    //   ② 指纹（尺寸 + 11 个采样点）：序号对不上时兜底 —— 这一步在"导入外部图"那条路径上本来
    //      就要读图，所以不额外花钱。
    // 两道都"只跳过一次"：命中即清；不命中（剪贴板已被别的东西替换）也清，绝不长期屏蔽。
    static class SelfClipboard
    {
        static string _fp = "";                            // 登记时算好的指纹（尺寸 + 采样像素），空 = 没有登记
        static int _w, _h;                                 // 登记时的尺寸（跟着指纹一起记，方便诊断）
        static DateTime _at = DateTime.MinValue;           // 登记时刻：只作为记录/诊断，**不参与判定**
        static long _seq = 0;                              // 我们自己写完之后剪贴板的序号（0 = 没有登记）
        static Thread _writer = null;                      // 正在后台写剪贴板的那条线程（测试/收尾要等它）

        public static bool Pending { get { return _fp.Length > 0; } }
        public static int NoteWidth { get { return _w; } }
        public static int NoteHeight { get { return _h; } }
        public static DateTime NoteAt { get { return _at; } }

        // 便宜的指纹：尺寸 + 采样若干像素。
        // 只比尺寸不够 —— "外面复制一张同样大小的图"会被误判成自己写的那张（用户点名要能区分）；
        // 所以采样点是四角 + 中心 + 四个 1/4 点 + 两个 1/3 点，11 个 GetPixel，够便宜也够准。
        internal static string Fingerprint(Image im)
        {
            Bitmap b = im as Bitmap;
            bool own = false;
            try
            {
                if (im == null || im.Width <= 0 || im.Height <= 0) return "";
                if (b == null) { b = new Bitmap(im); own = true; }
                int w = b.Width, h = b.Height;
                int[] xs = { 0, w - 1, 0, w - 1, w / 2, w / 4, w * 3 / 4, w / 4, w * 3 / 4, w / 3, w * 2 / 3 };
                int[] ys = { 0, 0, h - 1, h - 1, h / 2, h / 4, h / 4, h * 3 / 4, h * 3 / 4, h / 2, h / 2 };
                StringBuilder sb = new StringBuilder(16 + xs.Length * 8);
                sb.Append(w).Append('x').Append(h).Append(':');
                for (int i = 0; i < xs.Length; i++)
                {
                    int x = xs[i] < 0 ? 0 : (xs[i] > w - 1 ? w - 1 : xs[i]);
                    int y = ys[i] < 0 ? 0 : (ys[i] > h - 1 ? h - 1 : ys[i]);
                    sb.Append(b.GetPixel(x, y).ToArgb().ToString("X8"));
                }
                return sb.ToString();
            }
            catch { return ""; }
            finally { if (own && b != null) { try { b.Dispose(); } catch { } } }
        }

        // 写剪贴板之前调用：把"我要写的这张图"记下来（指纹 + 尺寸 + 时刻）
        public static void Note(Image im)
        {
            try
            {
                _fp = Fingerprint(im);
                _w = im == null ? 0 : im.Width;
                _h = im == null ? 0 : im.Height;
                _at = DateTime.Now;
            }
            catch { _fp = ""; _w = _h = 0; _at = DateTime.MinValue; }
        }

        // 写完之后记下"这一下把剪贴板序号推到了多少"（工作线程/同步写完之后调用）
        public static void NoteSequence()
        {
            try { _seq = Native.GetClipboardSequenceNumber(); }
            catch { _seq = 0; }
        }

        // ---- 第一道（便宜）：序号没变 = 还是我们自己刚写的那一下 ----
        // 命中 → true 并把登记里那张图的指纹带出去（给 _lastClipFp 去重用，省得再读一次图），同时清登记。
        public static bool TakeBySequence(out string fp)
        {
            fp = null;
            long s = _seq;
            if (s == 0 || _fp.Length == 0) return false;        // 没登记：直接用图去比（第二道）
            uint now;
            try { now = Native.GetClipboardSequenceNumber(); } catch { return false; }
            if (now == 0 || now != s) return false;              // 剪贴板已经被别的东西动过 → 交给第二道去判断
            fp = _fp;
            Clear();
            return true;
        }

        // ---- 第二道（兜底）：拿剪贴板里那张图的指纹来比 ----
        // 命中 → true（这一次跳过导入）并且清登记（只跳过一次）；
        // 不命中 → false，同样清登记（剪贴板已经被别的东西替换，这条登记过期了）。
        public static bool IsOurs(string fp)
        {
            try
            {
                if (_fp.Length == 0 || fp == null || fp.Length == 0) return false;
                return fp == _fp;
            }
            finally { Clear(); }
        }

        public static void Clear()
        {
            _fp = ""; _w = _h = 0; _at = DateTime.MinValue; _seq = 0;
        }

        // ============================ 往剪贴板写图（唯一入口） ============================
        // UI 线程调用：登记指纹 → 拷一份 → 交给专用 STA 工作线程去写（PNG 编码与 OLE flush 都在那边）。
        // 失败只写日志、绝不弹框（截图流程不能被剪贴板打断）。
        public static void BeginWrite(Image src)
        {
            try
            {
                Note(src);                          // 先登记（指纹是原图的；拷贝出来像素一模一样，比对得上）
                Bitmap copy = new Bitmap(src);      // UI 线程只付这一次拷贝（1600x1000 实测 ~7ms）
                Thread t = new Thread(delegate () { Write(copy); });
                t.SetApartmentState(ApartmentState.STA);   // OLE 剪贴板只能在 STA 线程上碰
                t.IsBackground = true;
                _writer = t;
                t.Start();
            }
            catch (Exception ex) { Err.Log("SelfClipboard.BeginWrite", ex); }
        }

        // 工作线程里的正事：一次把三种格式放上去，并立刻持久化（copy=true）
        static void Write(Bitmap img)
        {
            try
            {
                //   Bitmap / DIB —— 画图、Word、微信这些"粘贴图片"走的就是这两个；
                //   PNG        —— 认这个格式的程序（浏览器、部分编辑器/截图工具）能拿到
                //                 带 alpha 的无损原图，而且不会像 DIB 那样掉透明通道。
                DataObject data = new DataObject();
                data.SetImage(img);
                using (MemoryStream png = new MemoryStream())
                {
                    img.Save(png, ImageFormat.Png);
                    png.Position = 0;                     // 交给剪贴板前把读指针拨回开头
                    data.SetData("PNG", false, png);      // false = 原样给字节流，别自动转成 .NET 对象
                    // copy=true：立刻把数据刷进剪贴板（OleFlushClipboard），
                    // 所以这个 MemoryStream（以及拷出来的位图）之后被释放，粘贴方照样能拿到完整 PNG，
                    // 程序退出后剪贴板里也还在。
                    Clipboard.SetDataObject(data, true);
                }
                NoteSequence();                           // 记下写完之后剪贴板的序号（监听的便宜判据）
            }
            catch (Exception ex) { Err.Log("SelfClipboard.Write", ex); }
            finally { try { img.Dispose(); } catch { } }   // 拷出来的那一份，写完就还
        }

        // 等后台那次写剪贴板收工（测试、以及"要立刻读剪贴板"的地方用；正常流程没人等它）
        public static bool WaitIdle(int ms)
        {
            Thread t = _writer;
            if (t == null) return true;
            try { return t.Join(ms); } catch { return false; }
        }
    }
}

namespace SnapWheel
{
    // 一格是什么种类。1.3.0 起环上不再是"一格一张图"：拖什么进来，那一格就是什么（DIRECTIONS §4.1）。
    // Image = 截图 / 拖进来的图；Text = 拖进来的一段文字；File = 拖进来的一个文件（图片以外）。
    enum CellKind { Image, Text, File }

    class StoreItem
    {
        public CellKind Kind = CellKind.Image;
        // 图格：这张图。文字格 / 文件格没有位图（画的时候按 Kind 现画）——
        // 想拿"这格的位图"之前先看 Kind，别直接 Image.Width（那些地方全在 60/61/64/90 里，见 DIRECTIONS §4.1）。
        public Bitmap Image;
        public string FilePath;   // 拖出去 / 落盘用的那个文件（文字格 = 落到盘上的 .txt）
        public string Text = "";  // 文字格的内容
        public string Name = "";  // 文件格 / 文字格显示用的名字
        // 这一格的 FilePath 是不是**环自己的文件**（在环目录里，我们自己复制/写出来的）：
        // 只有 Owned 的才允许删。引用来的（拖进来的原文件、「移进来」没成功时引用的那个）
        // 绝不能动 —— 删格子时顺着 FilePath 删下去，删掉的就是用户的原件（真丢过数据）。
        public bool Owned;
    }

    class Store
    {
        public readonly List<StoreItem> Items = new List<StoreItem>();
        public bool SaveToDisk = false;
        public string Dir = "";            // 环自己的目录：截图、文字格、移进来的文件都放这儿
        public bool MoveInOnDrop = false;  // 拖进来的文件：「留一份」(false) / 「移进来」(true)
        public int MaxCount = 50;
        public int MaxTextLen = 5000;      // 文字格最多收这么多字（别把一整本书拖进来）
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
                    it.Owned = true;
                }
                catch { }
            }
            Items.Add(it);
            Trim();
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

        // 拖进来的一个文件：能当图读就当图（1.2 的老行为），读不出来就当一个**文件格**。
        // 1.2 是直接报「这些文件读不出图片」把 PDF / zip / 文档全挡在门外，这一版不再挡。
        public StoreItem ImportDropped(string path, bool moveIn, out string note)
        {
            note = "";
            Bitmap b = ImageIO.Load(path);
            if (b != null)
            {
                try { return AddCore(b, ImageIO.ExtFor(b)); }
                catch { try { b.Dispose(); } catch { } return null; }
            }
            return AddFile(path, moveIn, out note);
        }

        // 文字格：内容就是那段文字（1.3.0 只收用户主动拖进来的，剪贴板文字留给 1.8 文字环）
        public StoreItem AddText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (text.Length > MaxTextLen) text = text.Substring(0, MaxTextLen);
            StoreItem it = new StoreItem();
            it.Kind = CellKind.Text;
            it.Text = text;
            it.Name = Title(text);
            WriteText(it);
            Items.Add(it);
            Trim();
            return it;
        }

        // 重启时把 WriteText 落下的 text_*.txt 读回成文字格（30-Wheel 的 LoadImagesFromDisk 调它）。
        // 不带 Trim()：加载是"把已经在那里的东西摆回来"，中途 Trim 会把刚读回来的前面几格挤掉。
        // 文件是环自己写的（前缀 text_ 只有 WriteText 会产出），所以 Owned = true。
        public StoreItem AddTextFromDisk(string path)
        {
            try
            {
                string text = File.ReadAllText(path);      // 自己写过 BOM，这里自动认
                if (string.IsNullOrEmpty(text)) return null;
                if (text.Length > MaxTextLen) text = text.Substring(0, MaxTextLen);
                StoreItem it = new StoreItem();
                it.Kind = CellKind.Text;
                it.Text = text;
                it.Name = Title(text);
                it.FilePath = path;
                it.Owned = true;
                Items.Add(it);
                return it;
            }
            catch { return null; }
        }

        // 重启时把 CopyIn 落下的 file_*_原名 读回成文件格（30-Wheel 的 LoadImagesFromDisk 调它）。
        // 显示名用**用户原来的文件名**，界面上不该出现 file_20260928_... 那一串。
        // 只认前缀形状对得上的（OriginalNameOfCopy 认不出就返回 null）：这份是环自己复制进来的，
        // 所以 Owned = true；用户自己放进目录的文件没有前缀，永远不会被这里认领、也就不会被删。
        public StoreItem AddFileFromDisk(string path)
        {
            try
            {
                string orig = OriginalNameOfCopy(path);
                if (string.IsNullOrEmpty(orig) || !File.Exists(path)) return null;
                StoreItem it = new StoreItem();
                it.Kind = CellKind.File;
                it.Name = orig;
                it.FilePath = path;
                it.Owned = true;
                Items.Add(it);
                return it;
            }
            catch { return null; }
        }

        // 文件格。moveIn=true 时「移进来」：**先复制进环自己的目录，再把原文件送回收站**。
        // 顺序不能反 —— 复制失败（磁盘满 / 没权限）时，原文件必须还在。
        public StoreItem AddFile(string path, bool moveIn, out string note)
        {
            note = "";
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            StoreItem it = new StoreItem();
            it.Kind = CellKind.File;
            it.Name = Path.GetFileName(path);

            string own = CopyIn(path);
            if (own == null)
            {
                // 环还没有自己的目录（设置里存盘关掉 / 没填目录）：只能引用原文件，那就**一定不去动它**
                it.FilePath = path;
                note = moveIn
                    ? Lang.T("环还没有自己的目录，这一格先引用原文件（原文件没动）", "The ring has no folder of its own, so this cell references the original (which was left alone)")
                    : Lang.T("环还没有自己的目录，这一格引用原文件", "The ring has no folder of its own, so this cell references the original");
                Items.Add(it);
                Trim();
                return it;
            }

            it.FilePath = own;
            it.Owned = true;   // 副本在环目录里，是我们自己的
            if (!moveIn)
            {
                note = Lang.T("已留一份到环自己的目录（要从原地移走：设置第 1 页勾「移进来」）",
                              "Kept a copy in the ring's own folder (to move the original in: tick \"Move dropped files in\" on page 1 of Settings)");
            }
            else
            {
                string err;
                if (Recycle.Send(path, out err))
                {
                    note = Lang.T("已移进来（原文件在回收站里，可恢复）", "Moved in (the original is in the Recycle Bin and can be restored)");
                }
                else
                {
                    // 拿不到回收站：**保留这一份副本**（格子依然指向环目录里的副本）。
                    // 这里原来是"把副本删掉、改成引用原文件" —— 那样删格子时就会顺着 FilePath
                    // 把用户的原件永久删掉（回收站里也找不回来），实测就这么丢过文件。
                    // 原件本来就没动，留着副本最多是多占一份空间，比丢数据强得多。
                    Err.Note("Import", "送回收站失败，原件没动，环里留了一份副本：" + path + " → " + own +
                                       "  原因：" + (string.IsNullOrEmpty(err) ? "(没给出原因)" : err));
                    // 原因照抄给用户（他要能一眼看到为什么、也能把它发回来），但掐一下长度：
                    // 提示条再宽也只能占一屏，超长的原文留在 error.log 里。
                    string why = (err ?? "").Trim();
                    if (why.Length > 60) why = why.Substring(0, 60) + "…";
                    note = Lang.T("原件没动 —— 环里留了一份副本（" + why + "）",
                                  "The original was left alone - a copy was kept in the ring (" + why + ")");
                }
            }
            Items.Add(it);
            Trim();
            return it;
        }

        // 从环里撤掉一格：**只有环自己的文件才删**。引用来的（用户的原件）一概不动 ——
        // 这里是整个程序里唯一删文件的地方，60-WheelForm 的单删和批量删都必须走它。
        // 返回 true = 真删掉了一个我们自己的文件。
        public bool DropFile(StoreItem it)
        {
            if (it == null || !it.Owned) return false;
            string p = it.FilePath;
            if (string.IsNullOrEmpty(p)) return false;
            try
            {
                if (!File.Exists(p)) return false;
                File.Delete(p);
                return true;
            }
            catch (Exception ex) { Err.Log("DropFile", ex); return false; }
        }

        // 撤掉整个环之前调用：只删**我们自己写出来的**文件，引用来的原件一概不动。
        // 走的就是 DropFile 那道闸 —— 别在别处再写一遍 File.Delete。
        public void ClearOwnedFiles()
        {
            for (int i = 0; i < Items.Count; i++) DropFile(Items[i]);
        }

        public string EnsureFile(StoreItem it)
        {
            if (it == null) return null;
            if (it.Kind == CellKind.File)
                return (it.FilePath != null && File.Exists(it.FilePath)) ? it.FilePath : null;
            if (it.Kind == CellKind.Text)
            {
                if (it.FilePath != null && File.Exists(it.FilePath)) return it.FilePath;
                try
                {
                    // 临时文件不记进 FilePath：那不是"这一格东西的家"
                    string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_" + Guid.NewGuid().ToString("N") + ".txt");
                    File.WriteAllText(tmp, it.Text, new UTF8Encoding(true));
                    return tmp;
                }
                catch { return null; }
            }
            if (it.Image == null) return null;
            if (it.FilePath != null && File.Exists(it.FilePath)) return it.FilePath;
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_" + Guid.NewGuid().ToString("N") + ".png");
                it.Image.Save(tmp, ImageFormat.Png);
                it.FilePath = tmp;
                it.Owned = true;   // 这个临时文件也是我们写的，撤格子时该跟着删
                return tmp;
            }
            catch { return null; }
        }

        // 文字格也落一份到盘上：跟图片格一样，"这一格东西在哪"永远有答案（拖出去 / 拖进别的程序都用它）
        void WriteText(StoreItem it)
        {
            if (!SaveToDisk || Dir.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Dir);
                string f = Path.Combine(Dir, "text_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + (_seq++) + ".txt");
                // 带 BOM：Win7 记事本不认无 BOM 的 UTF-8（会显示乱码）
                File.WriteAllText(f, it.Text, new UTF8Encoding(true));
                it.FilePath = f;
                it.Owned = true;
            }
            catch { }
        }

        // 复制进环自己的目录，返回副本路径；没有目录 / 复制失败返回 null。
        // 副本名 = file_<19位时间戳>_<用户原名>：前缀是"这份是环自己复制的"的唯一标记，
        // 重启时靠它把文件格认领回来（不加标记就分不清哪份是复制进来的、哪份是用户自己放进
        // 目录的 —— 认错了就会把用户的文件当自己的删掉，见 tests\ring-restart-test.cs）。
        // 同名不覆盖（覆盖别人的东西是不可逆的错；时间戳到毫秒，撞上再加 -2 / -3）。
        string CopyIn(string path)
        {
            // 跟 WriteText / AddCore 一个规矩：存盘关掉就当"环没有自己的目录"。
            // 原来这里只看 Dir.Length，于是 SaveToDisk=false 的测试（ui-shot）也会往用户真实的
            // 环目录里塞文件 —— 我自己就被这个坑过一次（往用户的 1\ 里留了 14 个种子文件）。
            if (!SaveToDisk || Dir.Length == 0) return null;
            try
            {
                Directory.CreateDirectory(Dir);
                string name = Path.GetFileName(path);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string dst = CopyName(stamp, name, 0);
                int n = 2;
                while (File.Exists(dst)) dst = CopyName(stamp, name, n++);
                File.Copy(path, dst, false);
                return dst;
            }
            catch { return null; }
        }

        internal const string CopyPrefix = "file_";

        // 副本名的形状：file_ + 19 位时间戳 +（可选 "-序号"）+ "_" + 原文件名
        string CopyName(string stamp, string name, int dup)
        {
            return Path.Combine(Dir, CopyPrefix + stamp + (dup > 1 ? "-" + dup : "") + "_" + name);
        }

        // 从环目录里的副本名反推用户原来的文件名；不是我们复制的（没有前缀 / 形状不对）返回 null。
        internal static string OriginalNameOfCopy(string copyPath)
        {
            string n = Path.GetFileName(copyPath);
            if (string.IsNullOrEmpty(n) || !n.StartsWith(CopyPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            string rest = n.Substring(CopyPrefix.Length);
            if (rest.Length < 21) return null;                  // 时间戳 19 位 + '_' + 至少 1 个字
            int i = 19;
            if (rest[i] == '-')                                 // 撞名时的时间戳后缀，跳过去
            {
                int j = i + 1;
                while (j < rest.Length && rest[j] >= '0' && rest[j] <= '9') j++;
                if (j >= rest.Length || rest[j] != '_') return null;
                return rest.Substring(j + 1);
            }
            return rest[i] == '_' ? rest.Substring(i + 1) : null;
        }

        static string Title(string text)
        {
            int i = 0;
            while (i < text.Length && (text[i] == '\r' || text[i] == '\n' || text[i] == ' ' || text[i] == '\t')) i++;
            int j = i;
            while (j < text.Length && text[j] != '\r' && text[j] != '\n') j++;
            string line = text.Substring(i, j - i).Trim();
            if (line.Length == 0) line = text.Trim();
            if (line.Length > 40) line = line.Substring(0, 40) + "…";
            return line;
        }

        void Trim()
        {
            while (Items.Count > MaxCount && Items.Count > 0) Items.RemoveAt(0);
        }
    }
}

namespace SnapWheel
{
    // 本地使用计数：**只写在本机**，不上传、不联网、不收集内容。
    //
    // 为什么需要它：
    //   这个项目最近几个"往哪走"的方向都是**靠想定的**，然后被真实数据否掉 ——
    //   我提议"把搬运做到极致"，而日志显示传递模式（就是那个方向）用户自己用了 12 次就再没打开过。
    //   与其继续猜第二轮，不如让程序如实记一周，用表说话。
    //
    // 三条硬规矩：
    //   ① **默认关**。要显式打开（托盘右键 →「记录本地使用统计」）才会写一行。
    //   ② 只记**事件名和计数**（"长截图发生了一次"），**不记内容**：不记截图内容、
    //      不记窗口标题、不记文件名、不记你在哪儿用了它。看到这份文件也还原不出你干了什么。
    //   ③ **绝不影响功能**。任何一步失败都静默吞掉 —— 统计是给人看的，不是程序的一部分。
    static class Usage
    {
        public static bool On;                       // 由设置驱动
        public static string OverridePath = null;    // 测试用

        static readonly object _lock = new object();
        static bool _wroteHeader;

        const long MaxBytes = 512 * 1024;            // 超了就轮转成 .1

        public static string Path
        {
            get
            {
                if (!string.IsNullOrEmpty(OverridePath)) return OverridePath;
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return System.IO.Path.Combine(dir, "SnapWheel", "usage-log.tsv");
            }
        }

        /// <summary>记一件事。ev 是事件名（英文、稳定），detail 是可选的一点点上下文（数字为主）。</summary>
        public static void Ev(string ev, string detail)
        {
            if (!On) return;
            try
            {
                lock (_lock)
                {
                    string p = Path;
                    string dir = System.IO.Path.GetDirectoryName(p);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    try { if (File.Exists(p) && new FileInfo(p).Length > MaxBytes) File.Move(p, p + ".1"); }
                    catch { }

                    StringBuilder sb = new StringBuilder();
                    if (!_wroteHeader && !File.Exists(p))
                    {
                        sb.Append("# SnapWheel 本地使用统计（只在本机，不上传）\n");
                        sb.Append("# 格式：时间 \t 事件 \t 细节\n");
                        sb.Append("# 打开/关闭：托盘右键 →「记录本地使用统计」\n");
                        _wroteHeader = true;
                    }
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    sb.Append('\t').Append(ev);
                    sb.Append('\t').Append(detail == null ? "" : detail.Replace('\t', ' ').Replace('\n', ' '));
                    sb.Append('\n');

                    File.AppendAllText(p, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch { }   // 统计绝不能影响功能
        }

        public static void Ev(string ev) { Ev(ev, ""); }
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
    // 把文件送进回收站 ——「移进来」要用（见 DIRECTIONS §4.1.2）。
    //
    // 为什么用 Microsoft.VisualBasic：它是 .NET Framework 自带的程序集（不是第三方包），
    // 一行就能拿到"可恢复的删除"，省掉自己写 SHFileOperationW / IFileOperation 那套 COM 互操作。
    // 代价只是 csc 命令行多一个 /r:Microsoft.VisualBasic.dll（和 OCR 用 WinRT 桥接程序集是同一性质）。
    // 已在真 Win7 x86 + .NET 4.0 上实测：Microsoft.VisualBasic 10.0.0.0，送回收站后回收站条目 +1。
    //
    // 刻意**不**把"不是本地固定盘"当成"删掉就行"：网络盘 / 回收站被关掉的卷上，
    // 底层的 FOF_ALLOWUNDO 会静默变成永久删除。那种位置 Available 直接返回 false，
    // 调用方（Store.AddFile）据此**不动原文件**。
    static class Recycle
    {
        public static bool Available(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;
                return new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch { return false; }
        }

        // 送进回收站。返回 false 时 error 里有原因，并且**文件一定还在原地**。
        public static bool Send(string path, out string error)
        {
            error = "";
            if (!File.Exists(path)) { error = Lang.T("文件不在了", "The file is gone"); return false; }
            if (!Available(path))
            {
                error = Lang.T("这个位置拿不到回收站（网络盘，或回收站被关掉的卷）", "No Recycle Bin for that location (network drive, or the Bin is disabled on that volume)");
                return false;
            }
            try
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                if (File.Exists(path)) { error = Lang.T("送回收站失败", "Could not send it to the Recycle Bin"); return false; }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}

namespace SnapWheel
{
    static class Palette
    {
        public static readonly string[] Names = { Lang.T("蓝", "Blue"), Lang.T("红", "Red"), Lang.T("琥珀", "Amber"), Lang.T("绿", "Green"), Lang.T("紫", "Purple"), Lang.T("青", "Teal"), Lang.T("橙", "Orange"), Lang.T("灰", "Grey") };
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
        // 一个环声明它**收什么**（§4.2 里那份声明文本的 takes 字段）。
        // 0=什么都收（默认，跟 1.3.0 之前一样）1=只收图片 2=只收文字。
        // 为什么没有"只收文件"：文件本来就是兜底那一种（认不出是图就当文件收），
        // 一个"只收文件"的环在用户脑子里就等于"什么都收"，多一个选项只多一个困惑。
        public int Takes = 0;
        public const int TakesAny = 0, TakesImage = 1, TakesText = 2;
        public Store Store = new Store();
        public Wheel() { Id = Guid.NewGuid().ToString("N").Substring(0, 8); Name = Lang.T("项目", "Project"); ColorIndex = 0; }
        public Color Accent { get { return Palette.Get(ColorIndex); } }

        // 这种格子收不收 —— 收进来之前唯一的判据（拖放、剪贴板自动收纳都问这一个地方）
        public bool Accepts(CellKind k)
        {
            if (Takes == TakesImage) return k == CellKind.Image;
            if (Takes == TakesText) return k == CellKind.Text;
            return true;
        }

        // 界面用语：改名字那个小窗口里给用户看的三档
        public string TakesName()
        {
            if (Takes == TakesImage) return Lang.T("只收图片", "images only");
            if (Takes == TakesText) return Lang.T("只收文字", "text only");
            return Lang.T("什么都收", "anything");
        }

        // 拒收时那句话里的名词：「这个环只收图片」
        public string TakesLabel()
        {
            if (Takes == TakesImage) return Lang.T("图片", "images");
            if (Takes == TakesText) return Lang.T("文字", "text");
            return "";
        }
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
            if (string.IsNullOrWhiteSpace(name)) name = Lang.T("项目", "Project");
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
                st.MoveInOnDrop = _s.MoveInOnDrop;
                // 环自己的目录**一直有**（不只是"要存截图"的时候）：1.3.0 起文字格、移进来的文件都放这儿。
                // SaveToDisk 只管截图要不要写进去（见 Store.AddCore）。
                st.Dir = !string.IsNullOrEmpty(_s.Dir)
                    ? Path.Combine(_s.Dir, SafeName(Wheels[i].Name))
                    : "";
            }
        }

        public Wheel New()
        {
            Wheel w = new Wheel();
            w.Name = UnusedName();
            w.ColorIndex = Wheels.Count % Palette.Colors.Length;
            Wheels.Add(w);
            ApplySettings();
            return w;
        }

        // 名字里的序号取「当前没人用」的最小值。
        // 原来是 Wheels.Count + 1 —— 删掉中间一个环再新建就会撞名
        // （三个环 项目1/项目2/项目3，删掉 项目2 之后 count=2，下一个又叫 项目3），
        // 而环的目录名是按显示名生成的：撞名 = 两个环共用同一个目录 = 删一个连带删掉另一个的图。
        public string UnusedName()
        {
            for (int n = 1; ; n++)
            {
                string candidate = Lang.T("项目", "Project") + n;
                bool taken = false;
                for (int j = 0; j < Wheels.Count; j++)
                    if (Wheels[j].Name == candidate) { taken = true; break; }
                if (!taken) return candidate;
            }
        }

        public void Remove(int i)
        {
            if (Wheels.Count <= 1) { Wheels.Clear(); New(); Active = 0; return; }   // never run out of wheels
            Wheel w = Wheels[i];
            // 只删**我们自己写出来的**文件（走 DropFile 那道闸，引用来的原件一概不动），
            // 目录本身等它空了、而且没别的环在用时再删。
            // 为什么不能直接 Directory.Delete(w.Store.Dir, true)：目录名是按显示名生成的，
            // 两个环同名就共用一个目录，递归删会把**另一个环**的图一起删掉（实测过，真丢数据）。
            w.Store.ClearOwnedFiles();
            try
            {
                if (!string.IsNullOrEmpty(w.Store.Dir) && Directory.Exists(w.Store.Dir)
                    && Directory.GetFileSystemEntries(w.Store.Dir).Length == 0
                    && !DirSharedWithOther(w, i))
                    Directory.Delete(w.Store.Dir, false);
            }
            catch { }
            Wheels.RemoveAt(i);
            if (Active >= Wheels.Count) Active = Wheels.Count - 1;
            if (Active < 0) Active = 0;
        }

        // 有没有别的环也在用这个目录（两个环同名就会共用）
        bool DirSharedWithOther(Wheel w, int skip)
        {
            for (int j = 0; j < Wheels.Count; j++)
                if (j != skip && Wheels[j].Store.Dir == w.Store.Dir) return true;
            return false;
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
                    lines.Add("wheel=" + Wheels[i].Id + "|" + Wheels[i].Name + "|" + Wheels[i].ColorIndex + "|" + Wheels[i].Takes);
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
                        // 第 4 段是 takes，1.3.0 才有的字段 —— 老的可能只有 3 段，读不到就当"什么都收"
                        if (p.Length > 3) { int tk; if (int.TryParse(p[3], out tk)) w.Takes = tk; }
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
                    // 1.3.0 的文字格也是环自己落的盘（text_*.txt），不回读的话它重启就没了，
                    // 而文件还占着 —— 界面永远清不掉它（这一条原来只在"已知缺口"外面）。
                    files.AddRange(Directory.GetFiles(st.Dir, "text_*.txt"));
                    // 文件格的副本（CopyIn 落下的 file_<时间戳>_<原名>）。只扫这个前缀：
                    // 用户自己放进目录的文件没有前缀，认领它就是拿用户的东西当自己的（会删掉它）。
                    files.AddRange(Directory.GetFiles(st.Dir, Store.CopyPrefix + "*"));
                }
                catch { }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                for (int k = 0; k < files.Count; k++)
                {
                    try
                    {
                        if (Path.GetFileName(files[k]).StartsWith("text_", StringComparison.OrdinalIgnoreCase))
                        {
                            st.AddTextFromDisk(files[k]);      // 文字格：内容在文件里，不用加载器
                            continue;
                        }
                        if (Path.GetFileName(files[k]).StartsWith(Store.CopyPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            st.AddFileFromDisk(files[k]);      // 文件格：名字就是它，不用读内容
                            continue;
                        }
                        Bitmap b = ImageIO.Load(files[k]);     // 走统一加载器，ico/jpeg/…都能回读
                        if (b == null) continue;
                        StoreItem it = new StoreItem();
                        it.Image = b;
                        it.FilePath = files[k];
                        it.Owned = true;   // 这些 snap_* 是环自己写的，撤格子时该跟着删
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
        public bool SaveToDisk = true;   // 0.6.0 起默认开：截完就存一份到磁盘（用户习惯）
        // 拖进来的文件怎么处理：false = 留一份（默认，原文件不动）；true = 移进来（从原地移走、原文件进回收站）
        public bool MoveInOnDrop = false;
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
        public string DeleteMode = "single";  // 0.6.0 起默认单击删除（用户习惯），右键删除请选 double
        public string SwitchMode = "radial";  // "radialLang.T(" (万能键圆盘) or ", " (universal key dial) or ")swipe" (长按滑动切换)
        public int PeekPercent = 240;         // 长按放大：百分比（100 = 原大小）
        public bool IntroSeen = false;        // 是否看过新手引导
        public bool IntroAnim = true;         // 启动时播开启动画
        // ---- 外观风格（新拟态 + 扁平化 + 毛玻璃）----
        public string UiStyle = "neu";        // neu=新拟态+毛玻璃(默认) / flat=纯扁平 / solid=高对比不透明
        // 界面语言："zh" / "en"（0.6.0 第一轮 i18n；切换后重启生效）
        public string UiLanguage = "";           // ""=跟随系统语言（默认）/ "zh" / "en"
        public int GlassPercent = 40;         // 玻璃面板不透明度 20..100
        public int CardRadius = 14;           // 卡片圆角（占最小边的百分比）0..30
        public int ShadowPercent = 55;        // 阴影强度 0..100
        public int AnimSpeed = 100;           // 动画速度 %（70 慢 / 100 标准 / 140 快）
        public int AccentIndex = -1;          // -1=跟随每个 Wheel 自己的颜色；0..7=全局统一主题色
        public bool ShowNameLabel = true;     // 显示 Wheel 名称药丸
        public bool ShowCountLabel = true;    // 显示图片计数药丸
        // 诊断模式：轮盘上每个元素都画出自己的名字和边框。
        // 为什么需要它：用户报"某个地方很生硬"时，我这边只有文字描述，
        // 而界面上有十几个长得差不多的元素（三个圆按钮、名字药丸、计数胶囊、提示条、两个把手…）。
        // 打开它、截一张图发过来，"你指的是哪个"就不用再猜 —— 这个项目为此连着来回过三次。
        public bool DiagMode = false;
        // 本地使用统计：**默认关**。打开后只在本机记"某件事发生了一次"，
        // 不记内容、不联网。用来回答"我到底在用它做什么" —— 这个问题靠想是想不出来的。
        public bool UsageLog = false;
        // 轮盘贴哪条边："auto"（默认，任务栏自动隐藏时贴屏幕边）/ "screen" / "work"。
        //
        // 为什么要有它：Windows 的"工作区"**总是**扣掉任务栏那一条 ——
        // **哪怕任务栏是自动隐藏的，也照样预留**（实测 48 像素）。
        // 于是用工作区定位时，自动隐藏的用户会看到轮盘底下悬着一条看不见的空隙，像没靠到底。
        // 而反过来，任务栏常显时贴屏幕边又会让环压住任务栏一角。
        // 两种都说得通，所以做成可选的，默认自动判断。
        public string EdgeAnchor = "auto";
        // ---- v1.0「有生命感」的三个开关（都是默认开、都可以关）----
        // 为什么默认开：它们都不改功能，只改"看起来怎么样"；关掉的入口留着，是因为
        // 审美这件事没有标准答案 —— 用户不喜欢就该能关上，而不是被迫接受。
        //   涟漪   ：加进来一张新图时，从那一格扩散开一圈淡淡的光
        //   环的影子：让环"浮"在桌面上（和缩略图用的同一套柔和阴影）
        //   时间感 ：早上主题色偏暖、深夜整块自己暗一点
        public bool Ripple = true;
        public bool RingShadow = true;
        public bool DayMood = true;
        public int UiScale = 0;               // 界面缩放 %：0=自动（按显示器 DPI），60..250
        public bool CollapseMode = true;      // 0.6.0 起默认开：不用时缩到屏幕边上的小把手（用户习惯）
        public bool ClipboardImport = false;  // 0.6.0 起默认关：复制图片不再自动收进轮盘（免得Lang.T("复制一下就被抓走", "Copy to collect")）
        public bool CopyOnCapture = true;     // 截图确认时同时把图放进剪贴板（要立刻粘贴就直接 Ctrl+V）
        public bool GlassRefresh = true;      // 定时重抓玻璃底，避免轮盘挂久了糊的是旧桌面
        // 演示模式：让轮盘**能被录屏/截图拍到**。
        // 默认对屏幕捕获隐身（WDA_EXCLUDEFROMCAPTURE），好处是自己截图时轮盘不会进图；
        // 但那个 API 在 Win10 2004+ 对**所有**基于 Windows.Graphics.Capture 的捕获都生效，
        // 录屏也就一起拍不到了（用户报的"录视频时轮盘不出现、截图界面却正常"）。
        public bool Recordable = false;
        public bool ShowBalloon = false;      // 0.6.0 起默认关：不弹托盘气泡
        public int ExpandSpeed = 100;         // 展开动画速度 %（越大越快；独立于整体动画速度）
        public int CollapseSpeed = 150;       // 收起动画速度 %（默认"快"一档，收起要干脆）
        public bool NubSingle = false;        // 只用一个把手：左边那个点一下展开、再点一下收起（底部不占地方）
        // 收进新图（截图 / 剪贴板 / 导入）之后要不要把滚动位置重置到最新那张。
        // 开（默认）：视口跟到最新那张 —— 滑入动画看得见；关：完全不碰用户的滚动位置。
        public bool ResetScrollOnCapture = true;
        // 缩略图拖出去之后，环上要不要**留一份**（默认留：拖出是 Copy 语义，随时能再拖一次、或拖给第二个窗口）
        public bool KeepAfterDragOut = true;
        // 设置窗口的客户区尺寸（像素）。0 = 没设过 → 按"内容首选尺寸 × DPI"算默认值。
        // 用户拖过窗口之后在关闭时写回这里，下次打开就用他拖出来的大小（会夹进 [最小, 最大]）。
        // 省电模式（默认开）：**只在电池供电时**暂停毛玻璃定时刷新 + 重绘隔帧一次（见 12-Power.cs）。
        public bool PowerSave = false;        // 0.6.0 起默认关（用户实测后选择关掉；电池党可在设置第 4 页打开）
        // ---- 翻译（0.6.0）：留空就走内置的免费引擎链（有道 → MyMemory 保底）----
        // 填了就走你自己那套 OpenAI 兼容接口（DeepSeek / 豆包 / 通义 / 本地 Ollama 都行），
        // 质量最好，也顺带把"机翻腔"消掉。URL 可以只填到 /v1，程序会自己补 /chat/completions。
        // ⚠️ Key 是**明文**存在 %APPDATA%\SnapWheel\settings.ini 里的（本机文件，不会上传到任何地方）。
        public string LlmUrl = "";
        public string LlmKey = "";
        public string LlmModel = "deepseek-chat";
        public int WinW = 0;
        public int WinH = 0;
        // 拖出时要不要同时给"文件"格式。
        // v0.4.8 曾把这里默认改成关，结果老用户拖到资源管理器 / 只吃文件的程序直接放不进去
        // （"缩略图拖出去放不了"就是这么来的）—— 现在默认开，Rev<3 的老配置会被迁移回开。
        public bool DragOutAsFile = true;
        public bool CheckUpdate = true;       // 启动时检查 GitHub 有没有新版本
        public bool NubHintDone = false;      // 把手用途提示是否已经自动展示过
        public bool UndoHintDone = false;     // 删除后Lang.T("还能撤回", "You can undo it")的首次提示是否已展示过
        public bool AnnotHintDone = false;    // 截图标注（工具条）的首次提示是否已展示过
        public bool PinHintDone = false;      // 贴图（中键）的首次提示是否已展示过
        public string GuideSeenVersion = "";  // 上一次自动弹出新手引导/更新说明时的版本号
        public bool TextBg = true;            // 标注文字默认带白底（可关，见截图工具条上的"文字底"）
        // ---- 取字用哪个引擎（在取字结果框里就能切，见 81-OcrForm.cs）----
        //   auto   ：系统自带 OCR 优先；系统没有（Win7）就用随包的本地组件 —— 出厂默认
        //   system ：只用 Windows.Media.Ocr（快，但短标题/小字容易整行漏掉）
        //   native ：只用随包的 PP-OCR 组件（漏行少，慢一些）
        // 为什么要做成可切的：两条路的错法不一样（实测同一张图，系统认不出「验证」两个字，
        // 本地组件认得出来），认不出来的时候用户自己换一条再认一次，比问我"哪个更好"快。
        public string OcrEngine = "auto";
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
                        else if (k == "MoveInOnDrop") s.MoveInOnDrop = (v == "1");
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
                else if (k == "UiLanguage" && (v == "zh" || v == "en")) s.UiLanguage = v;
                        else if (k == "GlassPercent") { int n; if (int.TryParse(v, out n) && n >= 20 && n <= 100) s.GlassPercent = n; }
                        else if (k == "CardRadius") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 30) s.CardRadius = n; }
                        else if (k == "ShadowPercent") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 100) s.ShadowPercent = n; }
                        else if (k == "AnimSpeed") { int n; if (int.TryParse(v, out n) && n >= 50 && n <= 200) s.AnimSpeed = n; }
                        else if (k == "AccentIndex") { int n; if (int.TryParse(v, out n) && n >= -1 && n <= 7) s.AccentIndex = n; }
                        else if (k == "ShowNameLabel") s.ShowNameLabel = (v == "1");
                        else if (k == "ShowCountLabel") s.ShowCountLabel = (v == "1");
            else if (k == "DiagMode") s.DiagMode = (v == "1");
            else if (k == "UsageLog") s.UsageLog = (v == "1");
            else if (k == "EdgeAnchor" && (v == "auto" || v == "screen" || v == "work")) s.EdgeAnchor = v;
            else if (k == "Ripple") s.Ripple = (v == "1");
            else if (k == "RingShadow") s.RingShadow = (v == "1");
            else if (k == "DayMood") s.DayMood = (v == "1");
                        else if (k == "UiScale") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 250) s.UiScale = n; }
                        else if (k == "CollapseMode") s.CollapseMode = (v == "1");
                        else if (k == "ClipboardImport") s.ClipboardImport = (v == "1");
                        else if (k == "CopyOnCapture") s.CopyOnCapture = (v == "1");
                        else if (k == "GlassRefresh") s.GlassRefresh = (v == "1");
            else if (k == "Recordable") s.Recordable = (v == "1");
                        else if (k == "ShowBalloon") s.ShowBalloon = (v == "1");
                        else if (k == "ExpandSpeed") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 250) s.ExpandSpeed = n; }
                        else if (k == "CollapseSpeed") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 250) s.CollapseSpeed = n; }
                        else if (k == "RingSpeed") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 250) { s.ExpandSpeed = n; s.CollapseSpeed = n; } }   // 兼容旧配置
                        else if (k == "NubSingle") s.NubSingle = (v == "1");
                        else if (k == "ResetScrollOnCapture") s.ResetScrollOnCapture = (v == "1");
                        else if (k == "KeepAfterDragOut") s.KeepAfterDragOut = (v == "1");
                        else if (k == "PowerSave") s.PowerSave = (v == "1");
                        // 翻译接口（0.6.0）：值里可能有 '='（URL 的 query、key 的 base64），
                        // 所以读取那一侧必须只按**第一个** '=' 切分 —— 见本文件顶部解析处的注释
                        else if (k == "LlmUrl") s.LlmUrl = v;
                        else if (k == "LlmKey") s.LlmKey = v;
                        else if (k == "LlmModel") s.LlmModel = v;
                        else if (k == "WinW") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 10000) s.WinW = n; }
                        else if (k == "WinH") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 10000) s.WinH = n; }
                        else if (k == "DragOutAsFile") s.DragOutAsFile = (v == "1");
                        else if (k == "CheckUpdate") s.CheckUpdate = (v == "1");
                        else if (k == "NubHintDone") s.NubHintDone = (v == "1");
                        else if (k == "UndoHintDone") s.UndoHintDone = (v == "1");
                        else if (k == "AnnotHintDone") s.AnnotHintDone = (v == "1");
                        else if (k == "PinHintDone") s.PinHintDone = (v == "1");
                        else if (k == "GuideSeenVersion") s.GuideSeenVersion = v;
                        else if (k == "TextBg") s.TextBg = (v == "1");
                        else if (k == "OcrEngine" && (v == "auto" || v == "system" || v == "native")) s.OcrEngine = v;
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

        // 只改一个字段就落盘 —— 给"不在设置窗口里改的"开关用（目前只有取字引擎：
        // 它在取字结果框里就能切，那个框手里没有 Settings 实例）。
        // 先读盘再改那一个字段，别的设置项原样保留。
        public static void SaveOcrEngine(string v)
        {
            try
            {
                if (v != "auto" && v != "system" && v != "native") return;
                Settings s = Load();
                s.OcrEngine = v;
                s.Save();
            }
            catch { }
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
                case "new": return Lang.T("新建轮盘", "New wheel");
                case "next": return Lang.T("下一个轮盘", "Next wheel");
                case "prev": return Lang.T("上一个轮盘", "Previous wheel");
                case "delete": return Lang.T("删除当前轮盘", "Delete current wheel");
                case "shot": return Lang.T("截图", "Screenshot");
                case "collapse": return Lang.T("收起轮盘", "Collapse ring");
                case "folder": return Lang.T("打开保存文件夹", "Open save folder");
                case "settings": return Lang.T("打开设置", "Open settings");
                case "paste": return Lang.T("从剪贴板收一张", "Collect from clipboard");
                case "clear": return Lang.T("清空这一盘（保留轮盘）", "Clear items (keep wheel)");
                default: return Lang.T("不设置", "None");
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
                lines.Add("MoveInOnDrop=" + (MoveInOnDrop ? "1" : "0"));
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
                lines.Add("UiLanguage=" + UiLanguage);
                lines.Add("GlassPercent=" + GlassPercent);
                lines.Add("CardRadius=" + CardRadius);
                lines.Add("ShadowPercent=" + ShadowPercent);
                lines.Add("AnimSpeed=" + AnimSpeed);
                lines.Add("AccentIndex=" + AccentIndex);
                lines.Add("ShowNameLabel=" + (ShowNameLabel ? "1" : "0"));
                lines.Add("ShowCountLabel=" + (ShowCountLabel ? "1" : "0"));
            lines.Add("DiagMode=" + (DiagMode ? "1" : "0"));
            lines.Add("UsageLog=" + (UsageLog ? "1" : "0"));
            lines.Add("EdgeAnchor=" + EdgeAnchor);
            lines.Add("Ripple=" + (Ripple ? "1" : "0"));
            lines.Add("RingShadow=" + (RingShadow ? "1" : "0"));
            lines.Add("DayMood=" + (DayMood ? "1" : "0"));
                lines.Add("UiScale=" + UiScale);
                lines.Add("CollapseMode=" + (CollapseMode ? "1" : "0"));
                lines.Add("ClipboardImport=" + (ClipboardImport ? "1" : "0"));
                lines.Add("CopyOnCapture=" + (CopyOnCapture ? "1" : "0"));
                lines.Add("GlassRefresh=" + (GlassRefresh ? "1" : "0"));
        lines.Add("Recordable=" + (Recordable ? "1" : "0"));
                lines.Add("ShowBalloon=" + (ShowBalloon ? "1" : "0"));
                lines.Add("ExpandSpeed=" + ExpandSpeed);
                lines.Add("CollapseSpeed=" + CollapseSpeed);
                lines.Add("NubSingle=" + (NubSingle ? "1" : "0"));
                lines.Add("ResetScrollOnCapture=" + (ResetScrollOnCapture ? "1" : "0"));
                lines.Add("KeepAfterDragOut=" + (KeepAfterDragOut ? "1" : "0"));
                lines.Add("PowerSave=" + (PowerSave ? "1" : "0"));
                lines.Add("LlmUrl=" + LlmUrl);
                lines.Add("LlmKey=" + LlmKey);
                lines.Add("LlmModel=" + LlmModel);
                lines.Add("WinW=" + WinW);
                lines.Add("WinH=" + WinH);
                lines.Add("DragOutAsFile=" + (DragOutAsFile ? "1" : "0"));
                lines.Add("CheckUpdate=" + (CheckUpdate ? "1" : "0"));
                lines.Add("NubHintDone=" + (NubHintDone ? "1" : "0"));
                lines.Add("UndoHintDone=" + (UndoHintDone ? "1" : "0"));
                lines.Add("AnnotHintDone=" + (AnnotHintDone ? "1" : "0"));
                lines.Add("PinHintDone=" + (PinHintDone ? "1" : "0"));
                lines.Add("GuideSeenVersion=" + (GuideSeenVersion ?? ""));
                lines.Add("TextBg=" + (TextBg ? "1" : "0"));
                lines.Add("OcrEngine=" + OcrEngine);
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

            // 圆角按高度算（28% 高度）：写死 10f 的话，高 DPI 下按钮被放大 1.5 倍、圆角却不变，看着就不搭了
            using (GraphicsPath p = Gfx.Round(r, Math.Max(2f, r.Height * 0.28f)))
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
    // 截图浮层：比例胶囊（摆位、测量、绘制）（从 50-OverlayForm.cs 拆出，纯搬移，行为不变）。
    partial class OverlayForm
    {
        // ---------- 比例胶囊 ----------

        // 胶囊这一行的**纵向落位**（纯计算，离线可测：tests\ui-probe.cs 拿一块假的 1024×768 屏幕直接调它）。
        // 抽出来的理由和 ToolbarRect 一样：屏幕一大一小结果完全不同，
        // 本机 1067 高的屏上下都塞得下，小屏那种撞法永远看不到。
        internal static int ChipRowY(RectangleF sel, int h, Rectangle tool, int ct, int cb, float k)
        {
            int rowY = (int)sel.Bottom + (int)(14 * k);
            // 展开后会变宽、而且和工具栏抢同一条位置（都在选区下方）—— 重叠时往下让开，
            // 否则一展开就把工具栏盖住（用户反馈"比例的展开会遮挡工具栏"）。
            // 避开的是**工具栏** tool，不是胶囊自己上一帧的矩形 —— 拿它比较等于没比（用户反馈比例 bug 没修复）。
            if (HitsToolY(rowY, h, tool, k)) rowY = tool.Bottom + (int)(8 * k);
            // 下面塞不下 → 挪到选区上方
            if (rowY + h > cb - 10) rowY = (int)sel.Top - h - (int)(40 * k);
            // **挪到上面之后必须再查一次**（0.9.10 修）：
            // 屏幕矮的时候工具栏自己也只能摆在选区上方（它下面同样塞不下），胶囊正好落进它里面。
            // CI 的 1024×768 上必现 —— 胶囊 338..370 vs 工具条 356..398。
            // 之前只在"往下让"那条路上查了工具栏，往上挪这条路上没查，于是绕了一圈又撞回去。
            if (HitsToolY(rowY, h, tool, k)) rowY = tool.Top - h - (int)(8 * k);
            return rowY;
        }

        // 这一行会不会撞上工具栏（上下各留 8×k 的缝）
        static bool HitsToolY(int y, int h, Rectangle tool, float k)
        {
            return tool.Width > 0 && y + h > tool.Top && y < tool.Bottom + (int)(8 * k);
        }

        void MeasureChips()
        {
            string[] labels = { Lang.T("自由", "Free"), "1:1", "16:9", "9:16", "4:3", "3:4", "21:9" };
            _chipW = new int[labels.Length];
            using (Font f = new Font("Microsoft YaHei UI", 10f * _k))
            using (Graphics g = CreateGraphics())
                for (int i = 0; i < labels.Length; i++)
                    _chipW[i] = (int)g.MeasureString(labels[i], f).Width + (int)(22 * _k);
        }

        void PlaceChips()
        {
            string[] labels = { Lang.T("自由", "Free"), "1:1", "16:9", "9:16", "4:3", "3:4", "21:9" };
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
                rowY = ChipRowY(bb, h, _toolRect, ct, cb, _k);
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
            // 没框选就没有比例可设：不显示胶囊，免得按钮悬在半空（原来按展开后的总宽居中，收起时按钮偏左）
            if (!_hasSel) { _toggleRect = Rectangle.Empty; _panelBounds = Rectangle.Empty; return; }
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
                        // 用 DrawString（GDI+）而不是 TextRenderer：GDI 不认半透明色，alpha 被忽略，
                        // 收起时字不会渐隐、到某一帧直接消失（用户反馈"没有动画过渡"）。
                        StringFormat sfC = new StringFormat();
                        sfC.Alignment = StringAlignment.Center;
                        sfC.LineAlignment = StringAlignment.Center;
                        using (SolidBrush tb = new SolidBrush(Color.FromArgb(al, 255, 255, 255)))
                            g.DrawString(c.Label, f, tb, new RectangleF(r.X, r.Y, r.Width, r.Height), sfC);
                    }
                }
                Rectangle tr = _toggleRect;
                using (GraphicsPath p = Gfx.Round(tr, 9f))
                using (SolidBrush b = new SolidBrush(_chipsOpen ? Color.FromArgb(225, 0, 122, 204) : Color.FromArgb(185, 22, 24, 28)))
                    g.FillPath(b, p);
                using (GraphicsPath p2 = Gfx.Round(tr, 9f))
                using (Pen pen = new Pen(Color.FromArgb(130, 255, 255, 255), 1.2f))
                    g.DrawPath(pen, p2);
                // ⚠️ 这里必须用 DrawString（GDI+），**不能**用 TextRenderer（GDI）：
                //   实测在 2560x1440 的目标位图上，TextRenderer.DrawText 单次要 9.2ms，
                //   而 DrawString 只要 0.015ms —— 相差约 600 倍。原因：GDI 的 DrawText 会沿
                //   着整个目标表面处理裁剪区域，位图越大越慢；GDI+ 与目标大小无关。
                //   这一处就是"比例动画卡顿"的真正元凶（DrawChips 整体 8.2ms 几乎全在这）。
                StringFormat sfT = new StringFormat();
                sfT.Alignment = StringAlignment.Center;
                sfT.LineAlignment = StringAlignment.Center;
                using (SolidBrush tbT = new SolidBrush(Color.White))
                    g.DrawString(_chipsOpen ? Lang.T("比例 ▼", "▼") : Lang.T("比例 ▶", "▶"), f, tbT,
                        new RectangleF(tr.X, tr.Y, tr.Width, tr.Height), sfT);
            }
        }

    }
}

namespace SnapWheel
{
    // 截图浮层：右上角信息面板（宽高输入、角度显示）（从 50-OverlayForm.cs 拆出，纯搬移，行为不变）。
    partial class OverlayForm
    {
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
            _infoPanel = new BufferedPanel();
            _panelW = (int)(400 * _k);
            _panelH = (int)(40 * _k);
            _infoPanel.BackColor = Color.FromArgb(210, 18, 20, 24);
            Controls.Add(_infoPanel);
            Panel panel = _infoPanel;
            PlaceInfoPanel();

            Label l1 = new Label(); l1.Text = Lang.T("宽", "W"); l1.ForeColor = Color.White;
            l1.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            l1.Bounds = new Rectangle((int)(10 * _k), (int)(10 * _k), (int)(20 * _k), (int)(22 * _k)); panel.Controls.Add(l1);
            _inW = new TextBox(); _inW.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            _inW.Bounds = new Rectangle((int)(32 * _k), (int)(8 * _k), (int)(66 * _k), (int)(24 * _k));
            _inW.BackColor = Color.FromArgb(38, 40, 46); _inW.ForeColor = Color.White;
            _inW.BorderStyle = BorderStyle.FixedSingle; _inW.TextAlign = HorizontalAlignment.Center;
            panel.Controls.Add(_inW);

            Label l2 = new Label(); l2.Text = Lang.T("高", "H"); l2.ForeColor = Color.White;
            l2.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            l2.Bounds = new Rectangle((int)(108 * _k), (int)(10 * _k), (int)(20 * _k), (int)(22 * _k)); panel.Controls.Add(l2);
            _inH = new TextBox(); _inH.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            _inH.Bounds = new Rectangle((int)(130 * _k), (int)(8 * _k), (int)(66 * _k), (int)(24 * _k));
            _inH.BackColor = Color.FromArgb(38, 40, 46); _inH.ForeColor = Color.White;
            _inH.BorderStyle = BorderStyle.FixedSingle; _inH.TextAlign = HorizontalAlignment.Center;
            panel.Controls.Add(_inH);

            RoundButton apply = new RoundButton();
            apply.Text = Lang.T("应用", "Apply"); apply.Size = new Size((int)(58 * _k), (int)(26 * _k)); apply.Location = new Point((int)(204 * _k), (int)(7 * _k));
            apply.Fill = Color.FromArgb(0, 122, 204); apply.FillHover = Color.FromArgb(0, 140, 232);
            apply.Font = new Font("Microsoft YaHei UI", 9f * _k, FontStyle.Bold);
            apply.Click += new EventHandler(delegate(object o, EventArgs e2) { ApplySizeFromBoxes(); });
            panel.Controls.Add(apply);

            RoundButton reset = new RoundButton();
            reset.Text = Lang.T("角度归零", "Reset angle"); reset.Size = new Size((int)(84 * _k), (int)(26 * _k)); reset.Location = new Point((int)(268 * _k), (int)(7 * _k));
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
            _lblAngle.Text = Lang.T("角度 ", "Angle ") + a + "°" + (_locked ? Lang.T("　·　比例已锁定", " · aspect locked") : "") + (_hasSel ? "" : Lang.T("　·　拖拽以框选", " · drag to select"));
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
        // 0.6.0：浮层工具条上的「长图」按钮 —— 点它就带着当前选区去跑滚动长截图（不再走托盘）
        public bool WantLongShot = false;
        public Rectangle LongShotRegion = Rectangle.Empty;

        // 1.0.0：浮层工具条上的「贴图」按钮 —— 框完就能直接钉到屏幕上，
        // 同时**照常存进轮环**（走的是原来那条 ov.Result 路，不是另开一条）。
        // 之前贴图只能"截完 → 等轮盘拉出来 → 中键点缩略图"，中间隔着两步，
        // 用户的原话是"框选完就能选择贴图"。
        public bool WantPin = false;
        // 钉哪儿：用**选区中心**（屏幕坐标），PinForm 会以它为中心摆好、并夹进屏幕范围
        public Point PinAt = Point.Empty;

        // 右键已经按下、还没抬起 —— 退出要等抬起的理由见 OnMouseUp
        bool _rightPending;
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


        // 开双缓冲的 Panel：_infoPanel 是实心不透明面板，而浮层会频繁重绘，
        // 没有自己的双缓冲就会和窗体交界处闪烁（用户反馈的"右上角尺寸面板一直在闪"）。
        class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                UpdateStyles();
            }
        }


        void OnBoxKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { ApplySizeFromBoxes(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { Cancel(); }
        }



        void AnimTick(object sender, EventArgs e)
        {
            // 只重绘胶囊那一小块。原来这里是 Invalidate() —— 全窗体重绘，而浮层是**全屏大小**
            // （2560x1440），比例展开动画每帧重画整个屏幕，用户反馈的"比例动画卡顿"就是它。
            Rectangle dirty = _panelBounds;
            if (_toggleRect.Width > 0) dirty = Rectangle.Union(dirty, _toggleRect);   // 并上按钮那块，否则展开/收起留残影
            dirty.Inflate(60, 60);                 // 展开时两侧还有位移，留点余量
            if (dirty.Width <= 0 || dirty.Height <= 0) dirty = ClientRectangle;

            float tgt = _chipsOpen ? 1f : 0f;
            if (Math.Abs(_chipsT - tgt) < 0.002f) { _chipsT = tgt; _anim.Stop(); Invalidate(dirty); return; }
            _chipsT += (tgt - _chipsT) * 0.26f;
            Invalidate(dirty);
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





        // ---------- 绘制 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintOverlay(e); }
            catch (Exception ex) { Err.Log("OverlayForm.OnPaint", ex); }
        }

        // 只把 src 的指定区域贴上去：源矩形 = 目标矩形 = 该区域。
        // 不用 DrawImageUnscaled（它贴整张图，配合 SetClip 也只是"看不见"而已，代价照付）。
        static void BlitRegion(Graphics g, Bitmap src, Rectangle dest)
        {
            if (src == null) return;
            Rectangle r = Rectangle.Intersect(dest, new Rectangle(0, 0, src.Width, src.Height));
            if (r.Width <= 0 || r.Height <= 0) return;
            g.DrawImage(src, r, r, GraphicsUnit.Pixel);
        }
        void PaintOverlay(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // 把绘制裁剪到脏区：不然即使只 Invalidate 一小块，这里照样重贴整张全屏底图，
            // "局部重绘"就白做了（用户反馈比例动画帧率低，根因就在这）。
            try { g.SetClip(e.ClipRectangle); } catch { }
            g.CompositingMode = CompositingMode.SourceCopy;
            // 只贴脏区那一小块：SetClip 只限制"往哪里画"，GDI+ 依然会扫描整张源图，
            // 所以整屏底图每帧照样要处理 —— 这就是比例动画帧率低的真正原因（实测见下）。
            if (_dimmed != null) BlitRegion(g, _dimmed, e.ClipRectangle);
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
                        g.DrawImageUnscaled(_shot, 0, 0);   // 实测整图贴与局部贴耗时相同(0.31ms)，不必特殊处理
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

                string hint = Lang.T("双击保存　·　拖角缩放　·　拖圆点旋转　·　Esc 取消", "Double-click save · corners resize · dot rotate · Esc cancel");
                using (Font f2 = new Font("Microsoft YaHei UI", 10f * _k))
                using (SolidBrush fg2 = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                using (SolidBrush bg2 = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                {
                    SizeF sz2 = g.MeasureString(hint, f2);
                    // ⚠️ 提示条必须排在**所有顶部手柄之上**，不能只按"选区上沿减 36"。
                    // 旋转手柄在选区上方 30px、锁定手柄在 62px，两个半径都是 13 ——
                    // 旧写法 hy = Top - 高 - 36 正好落在这一行上：
                    // 只要选区**窄于约 600px**，提示条从左边起、那半截就被选区顶部中央的圆盘压住
                    // （渲染出浮层图一眼就能看到字被圆盘盖掉一截）。
                    // 手柄的几何在 RotateHandlePos / LockHandlePos 里，改那边要同步这里。
                    float topHandles = 62f * _k + 13f;      // 最高的那个（锁定）手柄的顶端
                    float hx = bb2.Left;
                    float hy = bb2.Top - topHandles - sz2.Height - 4;
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
            if (e.Button == MouseButtons.Right) { _rightPending = true; return; }   // 退出见 OnMouseUp（等到抬起才关）
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
            // ⚠️ 右键**必须等到抬起**才退出，不能在按下时就 Cancel()。
            // 按下就关窗的话，紧接着那一下"右键抬起"会落到浮层**下面**的窗口上 ——
            // 桌面收到抬起就弹右键菜单（用户报的"右键退出截图模式时会顺便右键到桌面，出来列表"）。
            // 等到抬起再关，按下和抬起就都被浮层吃掉了，桌面什么也收不到。
            // 浮层在按下时已经捕获了鼠标，所以抬起一定会回到这里，不会漏。
            if (e.Button == MouseButtons.Right)
            {
                if (_rightPending) { _rightPending = false; Cancel(); }
                return;
            }
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
            if (_set != null) { try { _set.TextBg = _textBg; _set.Save(); } catch { } }   // 记住Lang.T("文字底", "Text background")的选择
            Result = CropSelection(true);

            // 顺手把这张图放进剪贴板。"截完立刻粘一次"（Win+Shift+S 之后 Ctrl+V）是最高频的用法，
            // 以前截完只在环上，要粘得先从角落把图拖出去 —— 比系统截图慢一步。
            // 关掉设置就不碰剪贴板。整件事（登记 + 位图拷贝 + STA 工作线程里的 PNG 编码与 OLE flush）
            // 都在 SelfClipboard 里：**绝不能在 UI 线程上写**，那 80ms 正好落在"缩略图滑入"的帧上。
            if ((_set == null) || _set.CopyOnCapture) SelfClipboard.BeginWrite(Result);

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
                g.DrawImageUnscaled(_shot, 0, 0);   // 实测整图贴与局部贴耗时相同(0.31ms)，不必特殊处理
                if (withAnnotations) DrawAnnotationShapes(g);   // 标注用同一套坐标和变换画进去 —— 所见即所得
            }
            return crop;
        }

        // 浮层显示后主动抢一次前台：光在构造里设 TopMost = true 是不够的 ——
        // 若此时已有别的置顶窗口（或从托盘菜单/热键触发），浮层会落在下层，
        // 用户看到的就是"点了截图，截图界面出现在下层"。
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                TopMost = true;
                BringToFront();
                Activate();
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE);   // 注意：这里**不**带 NOACTIVATE，要真的激活
            }
            catch { }
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
    // 标注：标注的数据模型（图元类型、命中框、外框计算）（从 51-OverlayForm.Annotate.cs 拆出，纯搬移，行为不变）。
    partial class OverlayForm
    {
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
            // 度量用的字体必须和绘制用的**同一个**，否则框和内容对不上（符号画的时候用 Segoe UI Symbol）
            string ff2 = (s.Kind == AnnotKind.Emoji) ? "Segoe UI Symbol" : "Microsoft YaHei UI";
            using (Font f = new Font(ff2, Math.Max(6f, s.Size * _k), (s.Kind == AnnotKind.Emoji) ? FontStyle.Regular : FontStyle.Bold))
            {
                Size sz = TextRenderer.MeasureText(t, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                return new SizeF(sz.Width, sz.Height);
            }
        }

        // 图元的外框（文字按实际排版量；其它按起止点）
        RectangleF ShapeBounds(Shape s)
        {
            if (s == null) return RectangleF.Empty;
            if (!IsTextLike(s)) return RectOf(s.A, s.B);
            if (s.Kind == AnnotKind.Emoji)   // 符号：A 是**中心**（与绘制一致）；文字用的是左上角锚点
            {
                SizeF esz = TextSize(s);
                return new RectangleF(s.A.X - esz.Width / 2f, s.A.Y - esz.Height / 2f, esz.Width, esz.Height);
            }
            SizeF sz = TextSize(s);
            return new RectangleF(s.A.X - 3 * _k, s.A.Y - 2 * _k, sz.Width + 7 * _k, sz.Height + 5 * _k);
        }

    }
}

namespace SnapWheel
{
    // 标注：工具条布局（摆位、可见性、悬停变淡）（从 51-OverlayForm.Annotate.cs 拆出，纯搬移，行为不变）。
    partial class OverlayForm
    {
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

    }
}

namespace SnapWheel
{
    // 标注：标注的绘制（各图元、工具条、引导面板、取字中提示）（从 51-OverlayForm.Annotate.cs 拆出，纯搬移，行为不变）。
    partial class OverlayForm
    {
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
                    case AnnotKind.Emoji:
                    {
                        // ⚠️ 必须用 Graphics.DrawString（GDI+），**不能**用 TextRenderer：
                        //   合成最终图时这个 Graphics 上有 Translate/Rotate 变换（按选区裁剪+旋转），
                        //   而 TextRenderer 走 GDI，完全不响应 GDI+ 的变换 —— 于是预览看着正常、
                        //   一合成符号就跑到图外（用户反馈"贴上去了出图没有"）。文字标注一直用 DrawString，
                        //   所以从来没这个问题。
                        float sf2 = Math.Max(10f, s.Size * _k);
                        using (Font f = new Font("Segoe UI Symbol", sf2))
                        using (SolidBrush b = new SolidBrush(s.Color))
                        {
                            StringFormat fmt = new StringFormat();
                            fmt.Alignment = StringAlignment.Center;
                            fmt.LineAlignment = StringAlignment.Center;
                            g.DrawString(s.Text, f, b,
                                new RectangleF(s.A.X - sf2, s.A.Y - sf2 * 0.85f, sf2 * 2f, sf2 * 1.7f), fmt);
                        }
                        break;
                    }
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

        // ---------- 画工具条 ----------
        void PaintToolbar(Graphics g, int alpha)
        {
            if (!ToolbarVisible() || _toolBtns.Length == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 工具条上的 T / A / 字 是白字深底：ClearType 的彩色次像素边在这上面
            // 会渲染出红蓝描边，放大看就是"字歪了/有重影"。这里强制灰度抗锯齿。
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
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
                // 1.0.0「贴图」按钮：**常亮实心**，不做成"又一个线条图标"。
                // 用户原话："贴图按钮的存在感不能太弱"。
                // 这条栏上其余全是"深底 + 细线条图标"，只在两种情况才有底色：鼠标悬停、以及
                // **当前选中的工具**。贴图是唯一一个**永远**实心的 —— 所以哪怕选中的工具也在左边亮着，
                // 它还是整条栏的视觉落点之一，一眼能找到。
                bool isPin = (i == IdxPin);
                if (isPin)
                {
                    using (GraphicsPath bp = Gfx.Round(r, 7f * _k))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)((i == _toolHover ? 255 : 232) * a / 255f), 0, 138, 228)))
                        g.FillPath(b, bp);
                }
                else if (sel || i == _toolHover)
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
                            // 用"矩形 + StringFormat 居中"来画，和旁边 T / 字 一致。
                            // 原来用的是 g.DrawString("A", f, b, x, y) —— 那个重载是**左上角定位**、
                            // 不参与垂直居中，所以 A 看起来偏下、也歪（用户反馈"两个字体大小都偏下歪了"）。
                            using (Font f = new Font("Microsoft YaHei UI", 13f * _k, FontStyle.Bold))
                            using (SolidBrush b = new SolidBrush(ic))
                            {
                                StringFormat sfA = new StringFormat();
                                sfA.Alignment = StringAlignment.Center;
                                sfA.LineAlignment = StringAlignment.Center;
                                // A 只占左边 3/4：右边要留给减号/加号，否则会被 A 挡住
                                g.DrawString("A", f, b, new RectangleF(r.X, r.Y, r.Width * 0.75f, r.Height), sfA);
                            }
                            using (Pen p = new Pen(ic, 1.8f * _k))
                            {
                                float mx = d2.Right - 2 * _k, my = d2.Top + d2.Height * 0.34f;
                                g.DrawLine(p, mx - 5 * _k, my, mx, my);
                                if (i == IdxSizeUp) g.DrawLine(p, mx - 2.5f * _k, my - 2.5f * _k, mx - 2.5f * _k, my + 2.5f * _k);
                            }
                            break;
                        }
                    case IdxLong:       // 长图：一页纸 + 上下箭头
                    // 注意：上下箭头原来画到 ±14*k，而按钮内高只有约 26px —— 会顶出按钮框（渲染出来就能看到）。
                    // 收到 ±11，留出边距。
                    float lx = d2.Left + d2.Width / 2f, ly = d2.Top + d2.Height / 2f;
                    using (Pen pl = new Pen(Color.FromArgb(226, 232, 240), 1.6f))
                    {
                        g.DrawRectangle(pl, lx - 5f * _k, ly - 6.5f * _k, 10f * _k, 13f * _k);
                        g.DrawLine(pl, lx, ly - 7.5f * _k, lx, ly - 11f * _k);
                        g.DrawLine(pl, lx - 2.2f * _k, ly - 9f * _k, lx, ly - 11f * _k);
                        g.DrawLine(pl, lx + 2.2f * _k, ly - 9f * _k, lx, ly - 11f * _k);
                        g.DrawLine(pl, lx, ly + 7.5f * _k, lx, ly + 11f * _k);
                        g.DrawLine(pl, lx - 2.2f * _k, ly + 9f * _k, lx, ly + 11f * _k);
                        g.DrawLine(pl, lx + 2.2f * _k, ly + 9f * _k, lx, ly + 11f * _k);
                    }
                    break;
                    case IdxPin:        // 贴图：一颗**钉子**（扁头 + 直杆 + 尖），画在常亮底色上
                    {
                        float px = d2.Left + d2.Width / 2f, py = d2.Top + d2.Height / 2f;
                        using (SolidBrush bw2 = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                        {
                            // 整个钉子轮廓用**一个多边形**画完：扁头 → 直杆 → 尖。
                            // 上一版是"圆头 + 尖针"，圆头配那个收腰的尖看着不像钉子（用户说看着像别的东西），
                            // 而且圆头和针分两次填、交界会各抗锯齿一次留一道接缝。
                            // 一个多边形两件事都解决：轮廓明确、没有内部接缝。
                            // 比例很重要：钉子**比宽高**（约 2.6:1）。
                            // 第一版头宽 11px、整体才 11px 高，方方正正一条横杠压着一条短杆 ——
                            // 渲染出来就是个字母「T」。把杆拉长、头收窄才对。
                            float hw = 3.6f * _k;      // 扁头半宽
                            float hy = py - 9.2f * _k; // 扁头顶
                            float hb = py - 6.6f * _k; // 扁头底（厚度 2.6k）
                            float sw = 1.3f * _k;      // 杆半宽
                            float sy = py + 4.2f * _k; // 杆底（从这里开始收成尖）
                            float ty = py + 9.8f * _k; // 尖端
                            g.FillPolygon(bw2, new PointF[] {
                                new PointF(px - hw, hy),
                                new PointF(px + hw, hy),
                                new PointF(px + hw, hb),
                                new PointF(px + sw, hb),
                                new PointF(px + sw, sy),
                                new PointF(px,      ty),
                                new PointF(px - sw, sy),
                                new PointF(px - sw, hb),
                                new PointF(px - hw, hb) });
                        }
                    }
                    break;
                    case IdxSave:       // 另存为：向下箭头 + 底线（存盘）
                    {
                        float sx = d2.Left + d2.Width / 2f, sy = d2.Top + d2.Height / 2f;
                        using (Pen ps = new Pen(Color.FromArgb(226, 232, 240), 1.6f))
                        {
                            g.DrawLine(ps, sx, sy - 8f * _k, sx, sy + 3f * _k);
                            g.DrawLine(ps, sx - 4f * _k, sy - 1f * _k, sx, sy + 3f * _k);
                            g.DrawLine(ps, sx + 4f * _k, sy - 1f * _k, sx, sy + 3f * _k);
                            g.DrawLine(ps, sx - 7f * _k, sy + 8f * _k, sx + 7f * _k, sy + 8f * _k);
                        }
                    }
                    break;
                    case IdxEmoji:      // 贴 emoji：画一张笑脸（比任何图标都好认）
                    {
                        float ex = d2.Left + d2.Width / 2f, ey = d2.Top + d2.Height / 2f, er = 8f * _k;
                        using (Pen pe = new Pen(Color.FromArgb(238, 200, 90), 1.5f))
                        {
                            g.DrawEllipse(pe, ex - er, ey - er, er * 2f, er * 2f);
                            using (SolidBrush be = new SolidBrush(Color.FromArgb(238, 200, 90)))
                            {
                                g.FillEllipse(be, ex - 3.4f * _k, ey - 3.2f * _k, 2f * _k, 2f * _k);
                                g.FillEllipse(be, ex + 1.4f * _k, ey - 3.2f * _k, 2f * _k, 2f * _k);
                            }
                            g.DrawArc(pe, ex - 4.4f * _k, ey - 1.6f * _k, 8.8f * _k, 6f * _k, 20, 140);
                        }
                    }
                    break;
                    case 5:             // 取字工具：一个Lang.T("字", "Aa")比任何图标都好认
                        using (Font f = new Font("Microsoft YaHei UI", 13f * _k, FontStyle.Bold))
                        using (SolidBrush b = new SolidBrush(Ocr.Available ? ic : Color.FromArgb((int)(120 * a / 255f), 255, 255, 255)))
                        {
                            StringFormat sf = new StringFormat();
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            g.DrawString(Lang.T("字", "Aa"), f, b, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                        }
                        break;
                    case IdxRedo:   // 重做：撤销那个图形的**左右镜像**
                        // 镜像怎么算的（别每次重推一遍）：
                        //   关于竖直轴镜像 = 角度 θ → 180° - θ，而镜像会把走向翻过来。
                        //   撤销那段是从 205° 顺时针扫 260°（走到 465°=105°），
                        //   镜像后覆盖 [75°, 335°]，所以这里从 75° 顺时针扫 260°；
                        //   箭头落在镜像后的起点 335° 上，尖端朝**右**。
                        {
                            Color rc = _redo.Count > 0 ? ic : Color.FromArgb((int)(110 * a / 255f), 255, 255, 255);
                            float cx = d2.Left + d2.Width / 2f, cy = d2.Top + d2.Height / 2f;
                            float rr = 8f * _k;
                            using (Pen p = new Pen(rc, 1.8f * _k))
                            {
                                p.StartCap = LineCap.Round;
                                p.EndCap = LineCap.Round;
                                g.DrawArc(p, cx - rr, cy - rr, rr * 2f, rr * 2f, 75f, 260f);
                            }
                            using (SolidBrush b = new SolidBrush(rc))
                            {
                                double A0 = 335.0 * Math.PI / 180.0;
                                float ax = cx + rr * (float)Math.Cos(A0);
                                float ay = cy + rr * (float)Math.Sin(A0);
                                float ah = 3.4f * _k;
                                g.FillPolygon(b, new PointF[] {
                                    new PointF(ax + ah, ay),
                                    new PointF(ax - ah * 0.5f, ay - ah * 0.85f),
                                    new PointF(ax - ah * 0.5f, ay + ah * 0.85f) });
                            }
                        }
                        break;
                    default:     // 撤销：手绘「开口圆环 + 向左箭头」
                        // 为什么不再用字符（走过两轮弯路，都在这里记清楚）：
                        //   · ↶ (U+21B6)：弧线天生只占半格、字号偏小，看着就是"异常"；
                        //   · ⟲ (U+27F2)：候选对照图里 24pt 是漂亮圆环，但**按钮里只有 17pt，
                        //     字形被 hinting 简化成"半圆 + 一竖"**——用户看到的仍然是异常。
                        // 结论：小字号下依赖字体字形不可靠，改成手绘，尺寸完全由 _k 决定。
                        //
                        // 形状：圆弧从左上方（205°）顺时针扫 260°，缺口留在左侧；
                        //       缺口上端接一个朝左的实心三角 —— 这就是通用的"撤销/回退"符号。
                        //
                        // 半径这里**故意写死 8*k，而不是去撑满 d2**：
                        // d2 = Inset(按钮, 9*k)，34x30 的按钮算出来只有 16x12，
                        // 撑满它半径只剩 4.8px，放大了看就是一团看不清的小弯钩（第一版踩过）。
                        // 8*k 和旁边的笑脸(er=8k)、另存为(±8k) 是同一个量级。
                        {
                            Color uc = _shapes.Count > 0 ? ic : Color.FromArgb((int)(110 * a / 255f), 255, 255, 255);
                            float cx = d2.Left + d2.Width / 2f, cy = d2.Top + d2.Height / 2f;
                            float rr = 8f * _k;
                            using (Pen p = new Pen(uc, 1.8f * _k))
                            {
                                p.StartCap = LineCap.Round;
                                p.EndCap = LineCap.Round;
                                g.DrawArc(p, cx - rr, cy - rr, rr * 2f, rr * 2f, 205f, 260f);
                            }
                            using (SolidBrush b = new SolidBrush(uc))
                            {
                                // 三角箭头落在圆弧的起点（205°），尖端朝左
                                double A0 = 205.0 * Math.PI / 180.0;
                                float ax = cx + rr * (float)Math.Cos(A0);
                                float ay = cy + rr * (float)Math.Sin(A0);
                                float ah = 3.4f * _k;
                                g.FillPolygon(b, new PointF[] {
                                    new PointF(ax - ah, ay),
                                    new PointF(ax + ah * 0.5f, ay - ah * 0.85f),
                                    new PointF(ax + ah * 0.5f, ay + ah * 0.85f) });
                            }
                        }
                        break;
                }
            }
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
            string hint = (_sel.Kind == AnnotKind.Text) ? Lang.T("拖动移动　·　滚轮 / A+/A- 改字号　·　Del 删除", "Drag · wheel or A+/A- resize · Del delete")
                                                        : Lang.T("拖动移动　·　滚轮改粗细　·　Del 删除", "Drag · wheel thickness · Del delete");
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
                g.DrawString(Lang.T("截图浮层：三步搞定", "The capture overlay in three steps"), ft, bt, x + pad, y + pad - 4 * _k);

            string[] lines = {
                Lang.T("①  按住左键拖出要截的区域（四角缩放、圆点旋转、中间拖动）", "1. Drag with the left button to pick an area (corners resize, the dot rotates, the middle moves it)"),
                Lang.T("②  用下面的工具条标注：箭头 A · 方框 R · 马赛克 M · 文字 T", "2. Annotate with the toolbar below: arrow A · box R · mosaic M · text T"),
                Lang.T("      颜色 1~4 · 文字底 B · 字号 A+/A- 或滚轮 · Ctrl+Z 撤销", "      colours 1-4 · text background B · size A+/A- or wheel · Ctrl+Z to undo"),
                Lang.T("      画完的文字/方框可以直接拖动、滚轮改大小，Del 删掉", "      drawn text and boxes can be dragged, resized with the wheel, deleted with Del"),
                Lang.T("③  选「字」工具（或按 O）拖一个框圈住文字 = 取字，框越小越准；", "3. Pick the OCR tool (or press O) and drag a box around text; the tighter the box, the better"),
                Lang.T("      取字窗口里还能一键翻译成中文/英文", "      the OCR window can also translate to Chinese or English in one click"),
                Lang.T("④  双击选区或按回车 = 确认（Esc 取消），图直接进轮盘", "4. Double-click the selection or press Enter to confirm (Esc cancels); the image goes into the ring")
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
                g.DrawString(Lang.T("开始用", "Start using it"), fb, bb, btn, sf);
            }
            using (Font fh = new Font("Microsoft YaHei UI", 8.5f * _k))
            using (SolidBrush bh = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                g.DrawString(Lang.T("点一下面板或按任意键就开始（只提示这一次）", "Click the panel or press any key to begin (shown once)"), fh, bh, x + pad, y + h - pad - (int)(18 * _k));
        }

        // Lang.T("取字中…", "Recognising…")的小提示：识别在后台跑，但得让用户看见"它在干活"（不显示的话还是像卡住）
        void PaintOcrBusy(Graphics g)
        {
            if (!_ocrBusy) return;
            string txt = Lang.T("取字中…", "Recognising…");
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
        enum AnnotKind { Select = 0, Arrow = 1, Rect = 2, Mosaic = 3, Text = 4, Ocr = 5, Emoji = 6 }

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
        // 重做栈：撤销时把弹出的图元放这儿，重做时再拿回来。
        // 规则和所有编辑器一样：**一旦提交了新图元，重做栈就清空** ——
        // 否则"撤销 → 画新的 → 重做"会把一条早就作废的旧线重新贴回来。
        readonly List<Shape> _redo = new List<Shape>();
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
        const int IdxRedo = IdxBg + 4;     // 0.9.10：重做（撤销的反向，只有撤销一直很别扭）
        internal const int IdxLong = IdxBg + 5;   // 0.6.0：滚动长截图（拿当前选区当抓帧区域，不再走托盘）
        // internal：测试要按它点按钮。**别再在测试里抄一份下标** —— ui-probe 抄过的 n=18 就这么过期的。
        const int IdxSave = IdxBg + 6;     // 0.7.0：另存为（把当前框选含标注存到指定位置）
        const int IdxEmoji = IdxBg + 7;    // 0.7.0：贴 emoji（弹面板选一个，插入后可拖可缩放）
        const int IdxPin = IdxBg + 8;      // 1.0.0：贴图（框完直接钉到屏幕上，同时照常进轮环）
        // internal 而不是私有：测试要按它算工具条几何。**别再在测试里抄一份数字** ——
        // ui-probe 里原来硬编码着 n=18，这次加一个按钮就直接过期了（纯函数测试会继续绿，但测的是旧几何）。
        internal const int BtnCount = IdxBg + 9;





        // 命中图元：从后往前找（后画的在上层）
        Shape HitShape(PointF p)
        {
            for (int i = _shapes.Count - 1; i >= 0; i--)
            {
                Shape s = _shapes[i];
                RectangleF r = ShapeBounds(s);
                if (!IsTextLike(s))
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
            if (!IsTextLike(s)) s.B = new PointF(s.B.X + dx, s.B.Y + dy);
            if (s.Cache != null) { try { s.Cache.Dispose(); } catch { } s.Cache = null; }   // 马赛克跟着挪，得重算
        }

        // 滚轮/按钮调大小：文字改字号，其它改线条粗细
        void ResizeShape(Shape s, float delta)
        {
            if (s == null) return;
            if (IsTextLike(s))
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






        // 鼠标一动就调一次：只有真的需要变淡/变实才重绘
        void RefreshToolAlpha()
        {
            if (UpdateToolAlpha()) Invalidate();
        }


        // 文字类图元（文字 / emoji）：都按"位置 + 字号"描述，命中与拖动逻辑相同
        static bool IsTextLike(Shape s) { return s.Kind == AnnotKind.Text || s.Kind == AnnotKind.Emoji; }

        // 0.7.0：另存为 —— 把当前框选（含标注）存到用户指定的位置。这一张仍然留在轮盘里。
        void SaveAs()
        {
            if (!_hasSel || _vs.Width < 4 || _vs.Height < 4) return;
            Usage.Ev("SaveAs");
            Bitmap bmp = CropSelection(true);      // true = 标注一起合成进去
            if (bmp == null) return;
            try
            {
                using (System.Windows.Forms.SaveFileDialog d = new System.Windows.Forms.SaveFileDialog())
                {
                    d.Title = Lang.T("另存为", "Save as");
                    d.Filter = "PNG|*.png|JPEG|*.jpg|BMP|*.bmp";
                    d.FileName = "SnapWheel_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    string ext = (Path.GetExtension(d.FileName) ?? "").ToLowerInvariant();
                    if (ext == ".jpg" || ext == ".jpeg")
                    {
                        // JPEG 不支持透明：先铺白底，否则透明区会变黑
                        using (Bitmap flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
                        {
                            using (Graphics gg = Graphics.FromImage(flat)) { gg.Clear(Color.White); gg.DrawImage(bmp, 0, 0); }
                            flat.Save(d.FileName, ImageFormat.Jpeg);
                        }
                    }
                    else if (ext == ".bmp") bmp.Save(d.FileName, ImageFormat.Bmp);
                    else bmp.Save(d.FileName, ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Lang.T("保存失败：", "Save failed: ") + ex.Message, AppInfo.Name);
            }
            finally { bmp.Dispose(); }
        }

        // 0.7.0：贴 emoji —— 弹面板选一个，插到选区中心；之后和文字一样可拖动、可缩放、可删除。
        // 面板是**非模态**的：模态窗口不会失去激活，"点到外面就关"那条就永远不触发（第一版栽在这）。
        // 0.7.0：贴符号 —— 弹面板选一个（面板顶部可选颜色）；之后和文字一样可拖动、可缩放、可删除。
        // 面板是非模态的：模态窗口不会失去激活，"点到外面就关"那条就永远不触发。
        void PickEmoji()
        {
            Rectangle r = _toolBtns[IdxEmoji];
            Point sp = PointToScreen(new Point(r.Left, r.Bottom + 6));
            SymbolPicker.Popup(this, sp, _k, _annotColor, delegate(string g, Color c)
            {
                Shape s = new Shape();
                s.Kind = AnnotKind.Emoji;
                s.Text = g;
                s.Color = c;                                   // 面板里选的颜色
                s.Size = Math.Max(24f, _textSize * 1.4f);
                s.A = new PointF(_hasSel ? _c.X : _vs.Width / 2f, _hasSel ? _c.Y : _vs.Height / 2f);
                s.B = s.A;
                Commit(s);
                _sel = s;
                _tool = AnnotKind.Select;
                Invalidate();
            });
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


        static RectangleF Inset(Rectangle r, float pad)
        {
            return new RectangleF(r.X + pad, r.Y + pad, Math.Max(2, r.Width - pad * 2), Math.Max(2, r.Height - pad * 2));
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
                if (inPanel) return true;        // 点面板本身：就当作Lang.T("我知道了", "Got it")
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
                    else if (i == IdxLong)
                    {
                        // 0.6.0：把当前选区交给 App 去跑滚动长截图（只拼这一块，不再抓整屏）
                        WantLongShot = true;
                        // ⚠️ 这里**绝对不能**用 ScreenFor()。
                        //    它是个容易看错的函数：名字像"给我这个点的屏幕坐标"，
                        //    实际返回的是「**这个点在哪块显示器上**」—— 也就是那块屏的 Bounds。
                        //    拿它当抓帧区域等于抓**整个屏幕**，任务栏自然就跟着进长图了
                        //    （用户报的"长截图会把任务栏截进去"就是这么来的）。
                        //    LongShotForm 那边的字段注释本来就写着"就是浮层里的选区" ——
                        //    本意如此，只是这个值从 0.6.0 起一直传的是整屏。
                        //
                        //    要的是选区在**屏幕坐标**下的矩形：浮层的客户坐标 + 虚拟屏幕左上角
                        //    （和 ScreenFor 内部换算是同一条约定）。
                        RectangleF sb = SelBounds();
                        int lx = (int)Math.Floor(sb.Left), ly = (int)Math.Floor(sb.Top);
                        LongShotRegion = new Rectangle(
                            _vs.Left + lx, _vs.Top + ly,
                            Math.Max(1, (int)Math.Ceiling(sb.Right) - lx),
                            Math.Max(1, (int)Math.Ceiling(sb.Bottom) - ly));
                        DialogResult = DialogResult.OK;
                        Close();
                    }

                    else if (i == IdxPin)
                    {
                        // 1.0.0：框完直接贴到屏幕上，**同时照常存进轮环**。
                        //
                        // ⚠️ 这里必须调 Confirm()，不能自己写一句 DialogResult=OK; Close();
                        //    第一版我照抄了上面「长图」那条出口（那句是给"交给 App 去跑另一件事"用的），
                        //    结果 `Result` 是空的 —— 因为 Result 只在 Confirm() 里由 CropSelection 生成。
                        //    那样图既不会进轮环、也不会进剪贴板，正好把用户要的"自动保存到轮环"弄没了。
                        //    走 Confirm() 就等于"用户按了确定"，只是额外带一个"钉上去"的意图。
                        WantPin = true;
                        // 钉在**选区中心**：用户框哪儿，图就出现在哪儿。
                        //
                        // ⚠️ 别用 ScreenFor() 来算这个 —— 它是"**选区在哪块屏幕上**"（返回那块屏幕的
                        //    Bounds），上面「长图」要的是那个（它要在整块屏上抓帧）。
                        //    第一版我拿它的中心当坐标，结果不管框哪儿都钉到**显示器正中央**。
                        //    这里要的是选区自己的位置：浮层的客户坐标 + 虚拟屏幕左上角 = 屏幕坐标
                        //    （和 ScreenFor 内部换算用的是同一条约定：浮层左上角 = 虚拟屏幕左上角）。
                        PinAt = new Point(_vs.Left + (int)_c.X, _vs.Top + (int)_c.Y);
                        Confirm();
                    }
                    else if (i == IdxSave) { SaveAs(); Invalidate(); return true; }
                    else if (i == IdxEmoji) { PickEmoji(); Invalidate(); return true; }
                    else if (i == IdxRedo) Redo();
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
            if (ok) { Commit(s); _annotHint = false; }
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
            bool shift = (e.Modifiers & Keys.Shift) == Keys.Shift;
            // 撤销 / 重做：Ctrl+Z 与 Ctrl+Y 是 Windows 上的通用约定，
            // Ctrl+Shift+Z 是另一派约定（Mac / 很多编辑器），两个都收，不让用户去猜。
            if (ctrl && e.KeyCode == Keys.Z) { if (shift) Redo(); else Undo(); return true; }
            if (ctrl && e.KeyCode == Keys.Y) { Redo(); return true; }
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

            Invalidate();                            // 让Lang.T("取字中…", "Recognising…")立刻显示出来
            byte[] data = px; int w = pw, h = ph;
            System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate()
            {
                string err = null, txt = null;
                try { txt = Ocr.RecognizePixels(data, w, h, out err); }
                catch (Exception ex) { err = ex.Message; }
                Usage.Ev("Ocr", err != null ? ("失败:" + err) : ("认出 " + (txt == null ? 0 : txt.Trim().Length) + " 字"));
                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        _ocrBusy = false;
                        ShowOcrResult(txt, err, data, w, h);
                    }));
                }
                catch { _ocrBusy = false; }
            }));
            th.IsBackground = true;
            th.Start();
        }

        void ShowOcrResult(string txt, string err, byte[] data, int w, int h)
        {
            if (txt == null)
            {
                try
                {
                    MessageBox.Show(this, err ?? Lang.T("识别失败了", "Recognition failed"), Lang.T("取字", "OCR"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
                return;
            }
            try
            {
                bool wasTop = TopMost;
                TopMost = false;
                // 像素留着：结果框里换引擎时要拿同一块选区重新认一遍
                using (OcrForm of = new OcrForm(txt, delegate(out string e2) { return Ocr.RecognizePixels(data, w, h, out e2); }))
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
            if (_sel == last) _sel = null;
            // 注意：**不要**在这里 Dispose(last.Cache)。
            // 马赛克的 Cache 是那张算好的马赛克位图，重做时要原样拿回来；
            // 撤了就释放的话，重做出来的马赛克会是一片空白。
            _redo.Add(last);
            Invalidate();
        }

        // 本地统计用：这次标注用了几个图元、分别是哪些工具。
        // 记"用了哪些工具"而不只是"用了几个" —— 后者回答不了"该往标注里补什么"。
        public string ShapeCount()
        {
            if (_shapes.Count == 0) return "0";
            Dictionary<string, int> c = new Dictionary<string, int>();
            for (int i = 0; i < _shapes.Count; i++)
            {
                string k = _shapes[i].Kind.ToString();
                if (!c.ContainsKey(k)) c[k] = 0;
                c[k]++;
            }
            StringBuilder sb = new StringBuilder(_shapes.Count.ToString());
            foreach (KeyValuePair<string, int> kv in c) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            return sb.ToString();
        }

        void Redo()
        {
            if (_redo.Count == 0) return;
            Shape s = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _shapes.Add(s);
            _sel = s;
            Invalidate();
        }

        // 提交一个新图元：重做链到此为止（见 _redo 的说明）
        void Commit(Shape s)
        {
            _shapes.Add(s);
            ClearRedo();
        }

        void ClearRedo()
        {
            for (int i = 0; i < _redo.Count; i++)
            {
                Shape s = _redo[i];
                if (s.Cache != null) { try { s.Cache.Dispose(); } catch { } }
            }
            _redo.Clear();
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
                Commit(s);
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
    // ==================== 符号面板（0.7.0） ====================
    // 为什么不是 emoji：Windows 的彩色 emoji 靠 Segoe UI Emoji 的 COLR/CPAL 表，
    //   而 .NET Framework 的整条文本栈都不读这两张表 —— GDI / GDI+ / WPF FormattedText /
    //   WPF TextBlock 四条路全试过，统统只能画出黑色剪影（做过像素分析确认）。
    //   真彩色要上 Direct2D COM 互操作，对"单 exe、零依赖、纯 GDI 自绘"这个项目代价太大。
    // 所以改用**纯矢量符号**：字体直接有这些字形，单色、可自由上色、放大不糊，
    //   而且风格和现有的箭头/方框/文字标注完全一致。
    //
    // 面板结构：顶部一排颜色（红/黄/蓝/黑，与标注色一致），下面是三组符号。
    // 点一个符号 → 回调（符号 + 当前颜色）。非模态 Show(owner)，点到别处/按 Esc 都会关。
    class SymbolPicker : Form
    {
        static readonly string[] GroupNames = { Lang.T("标记", "Marks"), Lang.T("箭头", "Arrows"), Lang.T("编号 / 其它", "Numbers / misc") };
        static readonly string[][] Sets = {
            new string[] {
                "✓","✔","✗","✘","☑","☒","●","○","■","□","▲","△",
                "◆","◇","★","☆","♥","♡","※","§","¶","†","‡","✚"
            },
            new string[] {
                "→","←","↑","↓","↔","↕","⇒","⇐","⇑","⇓","➜","➤",
                "⟶","⟵","⤴","⤵","↻","↺","⇢","⇠","⇡","⇣","⇉","⇄"
            },
            new string[] {
                "①","②","③","④","⑤","⑥","⑦","⑧","⑨","⑩","⑪","⑫",
                "✎","✏","✂","✉","☎","♪","♫","⚑","⚐","☀","☂","❄"
            }
        };

        Action<string, Color> _onPick;
        Color _color = Color.FromArgb(238, 70, 90);
        bool _picked;
        ColorDot[] _dots;

        // 面板弹出的位置：贴在工具条上那个按钮的下面（越界会翻到上方）
        public static void Popup(Form owner, Point screenPt, double scale, Color initial, Action<string, Color> onPick)
        {
            SymbolPicker pk = new SymbolPicker(scale, initial, onPick);
            try
            {
                Rectangle scr = Screen.FromPoint(screenPt).WorkingArea;
                int x = screenPt.X, y = screenPt.Y;
                if (x + pk.Width > scr.Right) x = Math.Max(scr.Left, scr.Right - pk.Width);
                if (y + pk.Height > scr.Bottom) y = Math.Max(scr.Top, screenPt.Y - pk.Height - 60);
                pk.Location = new Point(x, y);
            }
            catch { pk.Location = screenPt; }
            pk.Show(owner);
            pk.Activate();
        }

        SymbolPicker(double scale, Color initial, Action<string, Color> onPick)
        {
            _onPick = onPick;
            _color = initial;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(40, 42, 50);
            double k = scale <= 0 ? 1.0 : scale;

            const int Cols = 12;
            int pad = (int)(10 * k);
            int headH = (int)(24 * k);
            int cell = (int)(48 * k);        // 原来 40 装不下 20pt 的符号，会被裁掉一截
            int colorH = (int)(34 * k);

            int rows = 0;
            for (int i = 0; i < Sets.Length; i++) rows += 1 + (Sets[i].Length + Cols - 1) / Cols;
            ClientSize = new Size(pad * 2 + Cols * cell, pad * 2 + colorH + Sets.Length * headH + rows * cell);

            // 顶部：颜色（与标注工具条同一组颜色）
            Label cl = new Label();
            cl.Text = Lang.T("颜色", "Colour");
            cl.ForeColor = Color.FromArgb(155, 165, 182);
            cl.Font = new Font("Microsoft YaHei UI", 9f * (float)k);
            cl.AutoSize = false;
            cl.TextAlign = ContentAlignment.MiddleLeft;
            cl.Location = new Point(pad, pad);
            cl.Size = new Size((int)(52 * k), colorH);
            Controls.Add(cl);

            _dots = new ColorDot[AnnotColorsStatic.Length];
            for (int i = 0; i < AnnotColorsStatic.Length; i++)
            {
                ColorDot d = new ColorDot();
                d.C = AnnotColorsStatic[i];
                d.Selected = (AnnotColorsStatic[i].ToArgb() == _color.ToArgb());
                d.Location = new Point(pad + (int)(58 * k) + i * (int)(30 * k), pad + (int)(4 * k));
                d.Size = new Size((int)(26 * k), (int)(26 * k));
                d.Click += new EventHandler(OnColor);
                Controls.Add(d);
                _dots[i] = d;
            }

            int y = pad + colorH;
            for (int gi = 0; gi < Sets.Length; gi++)
            {
                Label head = new Label();
                head.Text = GroupNames[gi];
                head.ForeColor = Color.FromArgb(155, 165, 182);
                head.Font = new Font("Microsoft YaHei UI", 9f * (float)k);
                head.AutoSize = false;
                head.TextAlign = ContentAlignment.MiddleLeft;
                head.Location = new Point(pad, y);
                head.Size = new Size(ClientSize.Width - pad * 2, headH);
                Controls.Add(head);
                y += headH;

                for (int i = 0; i < Sets[gi].Length; i++)
                {
                    int r = i / Cols, c = i % Cols;
                    GlyphCell el = new GlyphCell();
                    el.Glyph = Sets[gi][i];
                    el.Px = (float)(19 * k);
                    el.Ink = Color.FromArgb(228, 236, 246);
                    el.Location = new Point(pad + c * cell, y + r * cell);
                    el.Size = new Size(cell, cell);
                    el.Click += new EventHandler(OnPickGlyph);
                    Controls.Add(el);
                }
                y += ((Sets[gi].Length + Cols - 1) / Cols) * cell;
            }

            Deactivate += new EventHandler(delegate(object o, EventArgs e) { if (!_picked) Close(); });
            KeyPreview = true;
            KeyDown += new KeyEventHandler(delegate(object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { _picked = true; Close(); }
            });
        }

        // 和标注工具条共用同一组颜色（红/黄/蓝/黑）
        static readonly Color[] AnnotColorsStatic = {
            Color.FromArgb(238, 70, 90),
            Color.FromArgb(250, 176, 42),
            Color.FromArgb(0, 150, 240),
            Color.FromArgb(26, 28, 34)
        };

        void OnColor(object sender, EventArgs e)
        {
            ColorDot d = sender as ColorDot;
            if (d == null) return;
            _color = d.C;
            for (int i = 0; i < _dots.Length; i++) { _dots[i].Selected = (_dots[i] == d); _dots[i].Invalidate(); }
        }

        void OnPickGlyph(object sender, EventArgs e)
        {
            GlyphCell el = sender as GlyphCell;
            _picked = true;
            try { if (el != null && _onPick != null) _onPick(el.Glyph, _color); } catch { }
            Close();
        }

        // 一个符号格子
        class GlyphCell : Control
        {
            public string Glyph = "";
            public float Px = 20f;
            public Color Ink = Color.White;
            bool _hot;

            public GlyphCell()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                Cursor = Cursors.Hand;
            }
            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                if (_hot)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(80, 120, 170, 235)))
                        e.Graphics.FillRectangle(b, 0, 0, Width, Height);
                using (Font f = new Font("Segoe UI Symbol", Px))
                {
                    // 按实际度量居中放（不用 VerticalCenter 标志：单字符时它会偏上，看着像被裁）
                    Size gsz = TextRenderer.MeasureText(Glyph, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(e.Graphics, Glyph, f,
                        new Point((Width - gsz.Width) / 2, (Height - gsz.Height) / 2), Ink, TextFormatFlags.NoPadding);
                }
            }
        }

        // 一个颜色圆点
        class ColorDot : Control
        {
            public Color C = Color.Red;
            public bool Selected;
            bool _hot;

            public ColorDot()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                Cursor = Cursors.Hand;
            }
            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(2, 2, Width - 5, Height - 5);
                using (SolidBrush b = new SolidBrush(C)) e.Graphics.FillEllipse(b, r);
                if (Selected || _hot)
                    using (Pen p = new Pen(Color.FromArgb(Selected ? 255 : 140, 255, 255, 255), Selected ? 2.2f : 1.2f))
                        e.Graphics.DrawEllipse(p, r);
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
    //
    // ⚠️ DPI（0.5.3 修订）：这个窗口里**一个字都没有**（FormBorderStyle.None，标题栏根本不画；也没有 Label），
    // 所以不存在"字体按 DPI 放大、写死的格子没跟着放大、长句被裁掉"那类问题 —— 这里没有会裁字的固定像素。
    // 会跟着 DPI 走的只有下面这几处**界面元素**，所以它们过一遍 Ui.S()：
    //   · 右上角那个 × 按钮：大小 / 边距 / × 两笔的留白与粗细；
    //   · "窗口够不够大才画这个按钮"的门槛 —— 它必须和按钮同单位一起乘 K，
    //     不然 150% 下 33px 的按钮会盖满一张 45px 的小贴图。
    // **有意不乘 K 的**（都是"内容像素"，乘了贴图本身就画错了）：
    //   · 图片显示尺寸 / 缩放 / 居中 / 贴到屏幕的位置：贴图的规矩是"1 个像素就是 1 个像素"，
    //     iw = 图宽 × 缩放，全在 ApplyZoom / OnPaint / OnMouseWheel 里算，一个数都不动；
    //   · Pad = 1 那圈描边：它参与"图显示多大"（w = 图宽×缩放 + Pad*2），是图片外框的一部分；
    //   · MinimumSize 的 16：只是个"远小于系统最小宽度 136px"的哨兵值，不是版面尺寸。
    class PinForm : Form
    {
        public const float MinZoom = 0.10f;
        public const float MaxZoom = 4.00f;
        const int Pad = 1;                       // 1px 描边，浅色背景下也能看清边界（参与图尺寸计算，不乘 K）

        // ---- 右上角 × 按钮的逻辑尺寸（用的时候一律过 Ui.S）----
        const int CloseSize = 22;                // 圆的直径
        const int CloseMargin = 4;               // 距窗口右上角的边距
        const int CloseGap = 7;                  // × 两笔到圆边的留白
        const int CloseMinWin = 40;              // 窗口小于这个尺寸就不画按钮（和按钮同单位一起乘 K）

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
            // 小图会被撑到 136 宽、右边多出一条白边 —— 40x30 这种小截图就废了。
            // 16 是"远小于系统下限"的哨兵值，不是版面尺寸，所以不乘 K（乘成 24 也还是小于 136，没意义）
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
            // 12 是"图缩到极小时窗口别没了"的兜底，和上面的图片尺寸同属"内容像素"这一路
            //（MinimumSize=16 也兜过一次），所以**不乘 K** —— 乘了它就不再等于"图的像素 × 缩放 + 描边"，
            // 下面按光标锚点算位置的地方会跟着错
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

        // × 按钮的矩形（贴在窗口右上角）。22 / 4 是**界面元素**的尺寸，所以过 Ui.S 跟着 DPI 长：
        // 150% 下写死 22px 的按钮太小、不好点，而且它必须和下面那个"够不够大才显示"的门槛同尺度。
        Rectangle CloseRect()
        {
            int s = Ui.S(CloseSize), m = Ui.S(CloseMargin);
            return new Rectangle(Width - s - m, m, s, s);
        }

        // 窗口小到放不下按钮就不画、也不响应（双击 / Esc 照样能关）。
        // 门槛和按钮一起过 K，所以"多小的贴图会没有按钮"这件事在任何 DPI 下都一致 ——
        // 不乘的话 150% 下 33px 的按钮会盖满一张 45px 的小贴图。
        bool CloseVisible() { return Width > Ui.S(CloseMinWin) && Height > Ui.S(CloseMinWin); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.White);               // 带透明的图也有个白底，不至于透出桌面内容
            // 按图片自己的缩放尺寸画，别按窗口大小拉伸 —— 窗口万一被系统撑大，图也不该变形
            int iw = Math.Max(1, (int)Math.Round(_img.Width * _zoom));
            int ih = Math.Max(1, (int)Math.Round(_img.Height * _zoom));
            // Pad 那圈描边保持 1px、位置也不乘 K：它和 iw / ih 是一套算式里的东西
            // （图显示尺寸 = 图宽 × 缩放 + Pad*2），属"内容像素"；乘 K 会让贴出来的图比 iw 大一圈、取景对不上
            g.DrawImage(_img, new Rectangle(Pad, Pad, iw, ih));
            using (Pen bp = new Pen(Color.FromArgb(190, 70, 74, 84), 1f))
                g.DrawRectangle(bp, 0, 0, Width - 1, Height - 1);   // -1 是"画在边界内"的调整，不是尺寸

            if (_closeHover && CloseVisible())
            {
                Rectangle r = CloseRect();
                using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 38, 42, 50)))
                    g.FillEllipse(b, r);
                // 笔宽也跟着按钮一起长（100% 下 Ui.K=1，等于没变）：按钮变大了、两笔却还是原来那么细，
                // 看着像个细叉。这里乘的是**线条粗细**，不是字体磅值，不存在双倍放大
                using (Pen p = new Pen(Color.White, 1.8f * Ui.K))
                {
                    int m = Ui.S(CloseGap);
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
            bool h = CloseVisible() && CloseRect().Contains(e.Location);
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
        static bool _native;        // 走 57-OcrNative.cs 那个本地引擎（Win7 兜底）
        static string _lang = "";
        static string _why = "";

        // 用户在取字结果框里选的引擎（35-Settings.cs 存着，启动时灌进来）：
        //   auto   = 系统自带优先，拿不到就退本地组件（老行为，Win7 自动走本地）
        //   system = 只用系统自带的 Windows.Media.Ocr
        //   native = 只用随包的本地组件（要求程序旁边有 ocr 目录）
        // 为什么要让它可选：两个引擎的短处不一样 —— 系统那个快（整屏 <1 秒）但短标题、
        // 小字容易整行漏（实测「验证」两字整行消失）；本地组件漏字少但整屏要 3~4 秒。
        public static string Engine = "auto";

        public static bool Available { get { Probe(); return _engine != null || _native; } }
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
            if (UseNative()) { _native = true; return; }
            // 用户点名要本地组件、可它没装上：**绝不偷偷换成系统引擎** —— 那样他选了
            // 「本地组件」还是系统那个结果，只会以为"切换是坏的"。直接报它为什么不可用。
            if (string.Equals(Engine, "native", StringComparison.OrdinalIgnoreCase))
            {
                _why = OcrNative.Why.Length > 0 ? OcrNative.Why
                     : Lang.T("没有找到随包的取字组件（程序旁边要有 ocr 目录）", "The bundled OCR component was not found (an 'ocr' folder must sit next to the program)");
                return;
            }
            ProbeWinRT();
        }

        // 换了引擎之后把上一次的探测结果清掉，下一次识别按新引擎重新探一遍。
        // _engine 是 WinRT 那个引擎对象、_native 是"走本地"的标记，两个都得清。
        public static void Reconfigure()
        {
            _probed = false;
            _engine = null;
            _native = false;
            _lang = "";
            _why = "";
        }

        // 走哪条路？
        //   · 系统自带 OCR（Win10/11）优先 —— 更准、更小、不用额外文件
        //   · 拿不到（Win7 / Server Core / 精简版）就退到本地引擎（57-OcrNative.cs）
        // 环境变量 SNAPWHEEL_OCR=native / system 比设置里的选择更强（测试要用它，
        // 否则"兜底那条路"永远只在 Win7 上被跑过）。
        static bool UseNative()
        {
            string env = Environment.GetEnvironmentVariable("SNAPWHEEL_OCR");
            if (string.Equals(env, "native", StringComparison.OrdinalIgnoreCase)) return OcrNative.Available;
            if (string.Equals(env, "system", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(Engine, "native", StringComparison.OrdinalIgnoreCase)) return OcrNative.Available;
            if (string.Equals(Engine, "system", StringComparison.OrdinalIgnoreCase)) return false;
            if (WinRT("Windows.Media.Ocr.OcrEngine") != null) return false;
            return OcrNative.Available;
        }

        static void ProbeWinRT()
        {
            try
            {
                Type t = WinRT("Windows.Media.Ocr.OcrEngine");
                if (t == null)
                {
                    // Win7 上本来就没有系统 OCR。本地引擎也没装成的话，把"本地引擎为什么不可用"
                    // 直接告诉用户（那句话会说明 ocr 目录里该放什么），比只说"需要 Windows 10"有用。
                    if (Environment.OSVersion.Version.Major < 10 && OcrNative.Why.Length > 0) _why = OcrNative.Why;
                    else _why = Lang.T("这台系统没有 OCR 组件（需要 Windows 10 及以上）", "This system has no OCR component (Windows 10 or newer required)");
                    return;
                }

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
                if (list == null) { _why = Lang.T("系统没有安装任何 OCR 识别语言（设置 → 时间和语言 → 语言 → 该语言的「可选功能」里勾选「光学字符识别」）", "No OCR language is installed (Settings → Time & Language → Language → optional features → add \"Optical character recognition\")"); return; }

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
                if (pick == null) { _why = Lang.T("系统没有安装任何 OCR 识别语言", "No OCR language is installed"); return; }
                if (fromLang != null) { try { _engine = fromLang.Invoke(null, new object[] { pick }); } catch { } }
                if (_engine != null) _lang = LangOf(_engine);
                else _why = Lang.T("OCR 引擎创建失败（语言包可能不完整）", "Could not create the OCR engine (the language pack may be incomplete)");
            }
            catch (Exception ex)
            {
                _why = Lang.T("OCR 不可用：", "OCR unavailable: ") + ex.Message;
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
            if (!task.Wait(timeoutMs)) throw new Exception(Lang.T("识别超时", "Recognition timed out"));
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
            // 本地引擎吃原始像素：上面的 Stretch（低对比度拉伸）是给系统引擎调的参，
            // PP-OCR 是拿自然图训练的，没在真机上验过的事不往上加。
            if (_native) return OcrNative.RecognizePixels(bgra, w, h, out error);
            if (_engine == null) { error = _why; return null; }
            try
            {
                // ① 低对比度先拉伸：暗色主题截图、半透明面板上的浅灰字最容易认错，
                //    而直方图拉开之后再交给引擎，实测能明显少错字（见 Stretch 的说明）
                bool stretched;
                byte[] pre = Stretch(bgra, w, h, out stretched);
                object sw = SoftwareBitmapFromPixels(pre, w, h);
                if (sw == null) { error = Lang.T("这台系统不支持直接把像素交给 OCR", "This system cannot hand pixels directly to OCR"); return null; }
                float wordH;
                string txt = RecognizeSoftwareBitmap(sw, out error, out wordH);
                if (txt == null) return null;

                // 字太小就放大再认一遍 —— 这是准确率的关键。
                // 实测（900x380 合成图，字符级准确率）：14px 的字在 1x 下只有 25%，放大 2 倍到 92%；
                // 20px 是 28% -> 96%；连 32px 低对比度也是 13% -> 99%。屏幕截图里的正文多半就是
                // 14~20px，所以"不准"基本都是这个原因。
                //
                // 0.6.0 修正：这段注释原来写着"判据两条，缺一不可"，但代码里**一条都没用** ——
                // 实际上是无条件放大 2 倍、再把放大结果**无条件**当答案（量到的字高 `wordH` 声明了却从没读过）。
                // 现在：按量到的字高决定倍数，并且**择优**（放大那份只有认出的字更多才采用）。
                int chars1 = Chars(txt);
                float k;
                if (chars1 < 8) k = 3f;                                 // 几乎没认出来 -> 字高不可信，直接 3 倍
                else if (wordH > 0.5f && wordH < 12f) k = 3f;           // 很小的字
                else if (wordH > 0.5f && wordH < 18f) k = 2.5f;
                else if (wordH > 0.5f && wordH < 26f) k = 2f;           // 常见正文
                else k = 1.5f;                                          // 已经够大：只补一点点

                double area = (double)w * h;
                if (area * k * k > 8.0e6) k = (float)Math.Sqrt(8.0e6 / area);   // 放大后别超过 8M 像素
                if (k < 1f) k = 1f;
                if (k > 3f) k = 3f;
                string bigger = null;
                if (k > 1.05f)
                {
                    int nw = (int)(w * k), nh = (int)(h * k);
                    if (nw <= 10000 && nh <= 10000 && nw * nh < 40 * 1000 * 1000)
                    {
                        byte[] scaled = ScalePixels(pre, w, h, nw, nh);
                        if (scaled != null)
                        {
                            object sw2 = SoftwareBitmapFromPixels(scaled, nw, nh);
                            if (sw2 != null)
                            {
                                string e2 = null; float h2;
                                string t2 = RecognizeSoftwareBitmap(sw2, out e2, out h2);
                                // 择优：认出的字**更多**才用放大那份（原来是无条件采用，可能反而更差）
                                if (t2 != null && Chars(t2) > chars1) bigger = t2;
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

        // 低对比度拉伸：只在"图确实偏灰"时才动，而且是**线性拉伸直方图**（不改颜色关系、不做二值化 ——
        // 二值化会把抗锯齿边缘咬碎，引擎反而更容易认错）。
        // 判据：取亮度直方图的 2% / 98% 分位，跨度 < 200 才拉伸（也就是"最暗的 2% 和最亮的 2% 挤在
        // 中间一小段里"）。对比度本来就好的图（跨度 >= 200）原样返回，连一次拷贝都不做。
        // 为什么要它：暗色主题的窗口、半透明面板上的浅灰字，直方图全挤在 60~140 这一段，
        // 引擎的字形分割很容易切错 —— 拉开之后错字明显变少。
        static byte[] Stretch(byte[] src, int w, int h, out bool changed)
        {
            changed = false;
            if (src == null || w <= 8 || h <= 8 || src.Length < w * h * 4) return src;

            int[] hist = new int[256];
            int n = 0;
            for (int i = 0; i + 3 < src.Length; i += 4)
            {
                int lum = (src[i] * 29 + src[i + 1] * 150 + src[i + 2] * 77) >> 8;   // BGRA 的亮度近似
                hist[lum]++;
                n++;
            }
            if (n < 2000) return src;                     // 小图不值得折腾

            // ⚠️ 这里刻意取 **0.2% 分位**而不是常说的 2%：
            // 低对比度图的绝大多数像素都是**背景色**，取 2% 分位时往下数 2% 就已经数到背景上了，
            // 于是 hi 会等于 lo、跨度算成 0，被判成"几乎是纯色，拉也白拉"而永远不拉伸。
            // （0.6.0 实测：一张灰底浅灰字（亮度 84~110）的图就是这样被跳过的，识别结果是空的。）
            // 0.2% 既能认到真正的浅色文字，又能滤掉零星噪点。
            int cut = n / 500;                            // 0.2%
            int lo = 0, hi = 255, acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= cut) { lo = i; break; } }
            acc = 0;
            for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc >= cut) { hi = i; break; } }
            if (hi - lo >= 200 || hi - lo < 16) return src;   // 对比度够好 / 几乎是纯色

            byte[] map = new byte[256];
            double sc = 255.0 / (hi - lo);
            for (int i = 0; i < 256; i++)
            {
                int v = (int)((i - lo) * sc);
                map[i] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
            }
            byte[] outp = new byte[src.Length];
            Buffer.BlockCopy(src, 0, outp, 0, src.Length);
            for (int i = 0; i + 3 < outp.Length; i += 4)
            {
                outp[i] = map[src[i]];                    // B
                outp[i + 1] = map[src[i + 1]];            // G
                outp[i + 2] = map[src[i + 2]];            // R
                // A 不动：截图是不透明的，动它反而会改变预乘关系
            }
            changed = true;
            return outp;
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
            if (bmp == null) { error = Lang.T("没有图", "No image"); return null; }
            Probe();
            if (_engine == null && !_native) { error = _why; return null; }
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
                    // 0.6.0：又高又长的图（滚动长截图拼出来的那种）先**切条**再识别 ——
                    // 整张丢给引擎会被降采样，小字全糊（见 92-OcrTall.cs）。
                    if (OcrTall.ShouldSplit(pw, ph))
                    {
                        string tall = OcrTall.Recognize(px, pw, ph, out error);
                        if (tall != null) return tall;
                        error = null;      // 切条没成功就退回整张识别，至少能出点东西
                    }
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
    // 取字（OCR）的 **Win7 兜底引擎**：lw.OpenCVDNN.PPOCR —— PP-OCR 的纯 CPU 原生库。
    //
    // 为什么需要它：系统自带的 Windows.Media.Ocr 是 Windows 10 才有的组件，Win7 上
    // 56-Ocr.cs 里那条路整条拿不到（Type.GetType 返回 null）。也就是说，**Win7 上"取字"
    // 本来是直接不可用的**，而取字是快照轮环的刚需之一（DIRECTIONS.md §12 的 Win7 节点）。
    //
    // 为什么选它（选型与实测数字见 DIRECTIONS.md §13.5）：
    //   · MIT、纯 CPU、静态链接（不需要另外装 VC 运行库）、官方 CI 就在校验
    //     "Win7 PE subsystem 6.01 + 静态依赖 + 导出 ABI"，并且**同时出 x64 / x86 两个包** ——
    //     我们是 AnyCPU，32 位 Win7 上跑的是 x86 进程，两个位数都得能配上。
    //   · 它是 **C ABI**（`extern "C"` + `__cdecl`），不是 C++ 类库，P/Invoke 能直接对上，
    //     既不用写 C++ shim，也不用托管包装。
    //   · 我们**不改它一个字节**，只把 DLL + 模型当外部引擎用（"搬"的粒度是 ABI，不是源码）。
    //
    // 为什么用 LoadLibrary + 函数指针，而不是 DllImport("lw.OpenCVDNN.PPOCR.dll")：
    //   DllImport 的名字只能是编译期写死的字符串，靠进程搜索路径去找文件；而这里要的是
    //   "**exe 旁边那个 ocr 目录里有什么就用什么**" —— Win10/11 用户根本不装这个目录
    //   （他们有系统自带 OCR，更好也更小），所以"目录不在"必须是**一条安静的降级路径**，
    //   不能变成一个 TypeInitializationException。
    //   手写函数指针还顺手解决了第二件事：配错位数（32 位进程配了 x64 的 DLL）时
    //   LoadLibrary 会返回 ERROR_BAD_EXE_FORMAT(193)，这条能变成一句人话，
    //   而不是一句"找不到入口点"。
    //
    // 线程：DLL 自己保证"一个 handle 上串行"（头文件原话：A handle serializes its own OCR calls），
    // 所以我们不需要额外加锁；但我们**绝不**在识别进行中销毁 handle。
    static class OcrNative
    {
        // SDK 规定的文件名，不能改（换成别的名字就不是这套 ABI 了）
        const string DllName = "lw.OpenCVDNN.PPOCR.dll";
        const int ErrorBadExeFormat = 193;
        const int ErrorCapacity = 1024;
        const int CpuThreads = 2;          // 给界面留核：识别是后台线程跑的，别把机器吃满

        // 检测（找文字框）那一步的**长边上限**。引擎自己的默认值是 960，而 960 会把
        // 大截图里的字先缩小再检测 —— 一张 5120×1600 的双屏截图会被压掉 5.3 倍，
        // 12~14 px 的界面小字缩到 2~3 px，检测器直接看不见（实测：同一张图
        // 长边 960 → 认出 8 个文本框 / 39 字，原分辨率 → 27 个 / 79 字，
        // 且「老老实实地遵守嘱咐」这类整行在 960 下被截成半句）。
        // 2560 是实测选出来的折中：5120×1600 的真截图，2560 与 4096 认出的内容
        // 逐行比对只差标点/噪点变体（没有哪句真文字只在 4096 出现），但
        //   本机 x64    2560 → 3.6 s / 1485 字   4096 → 5.1 s / 1586 字
        //   Win7 x86 VM 2560 → 4.3 s / 472 MB    4096 → 5.8 s / 961 MB
        // 内存是按"缩过之后"的尺寸算的（与输入多大无关），32 位进程里 961 MB 太贴
        // 上限，所以默认压到 2560：常见屏幕（≤2560 宽）仍然原样进检测，不缩。
        // 需要 A/B 或临时改回去时用环境变量 SNAPWHEEL_OCR_LIMIT。
        const int DefaultLimitSideLen = 2560;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        delegate int CreateW(string detModel, string recModel, string dictionary, int cpuThreads, out IntPtr handle, StringBuilder error, int errorCapacity);

        // ppocr_config_w 的 C# 影子（字段名、顺序、类型都照 sdk\native\include\ppocr_api.h:48-70 抄，
        // 错一个字段就是踩内存）。为什么要用它：只有 create_ex 这条入口能改 limit_side_len，
        // 简版 ppocr_create_w 没有这个参数。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PpocrConfig
        {
            public int struct_size;
            [MarshalAs(UnmanagedType.LPWStr)] public string det_model_path;
            [MarshalAs(UnmanagedType.LPWStr)] public string rec_model_path;
            [MarshalAs(UnmanagedType.LPWStr)] public string rec_dict_path;
            [MarshalAs(UnmanagedType.LPWStr)] public string cls_model_path;
            public int limit_side_len;
            public double det_db_thresh;
            public double det_db_box_thresh;
            public double det_db_unclip_ratio;
            public int use_dilation;
            public int use_angle_cls;
            public double cls_thresh;
            public int cls_batch_num;
            public int rec_batch_num;
            public int rec_img_h;
            public int rec_img_w;
            public int rec_predictor_num;
            public int cpu_threads;
        }

        // 默认值由 DLL 自己填（ppocr_config_init），我们只覆盖上面那条 limit 和线程数 ——
        // 其余阈值保持厂商默认，改动面越小越好排查。
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void ConfigInit(ref PpocrConfig config);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        delegate int CreateExW(ref PpocrConfig config, out IntPtr handle, StringBuilder error, int errorCapacity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int OcrBgr(IntPtr handle, IntPtr pixels, int width, int height, int channels, int stride, out IntPtr utf8Json, out int jsonSize, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder error, int errorCapacity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void FreePtr(IntPtr memory);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DestroyHandle(IntPtr handle);

        static bool _probed;
        static IntPtr _handle = IntPtr.Zero;
        static string _why = "";
        static OcrBgr _ocr;
        static FreePtr _free;

        public static bool Available { get { Probe(); return _handle != IntPtr.Zero; } }
        public static string Why { get { Probe(); return _why; } }
        public static int Limit { get { return LimitSideLen(); } }

        // SNAPWHEEL_OCR_LIMIT 是给测试/排查用的覆盖口（A/B 同一张图），不是给用户配置的开关。
        static int LimitSideLen()
        {
            try
            {
                string s = Environment.GetEnvironmentVariable("SNAPWHEEL_OCR_LIMIT");
                int v;
                if (!string.IsNullOrEmpty(s) && int.TryParse(s, out v) && v >= 128) return v;
            }
            catch { }
            return DefaultLimitSideLen;
        }

        // ---------------- 组件在哪 ----------------
        //
        // 约定（按优先级）：
        //   ① 环境变量 SNAPWHEEL_OCR_DIR 指到哪就用哪（测试和排查用，不写在文档里当用法）
        //   ② exe 旁边的 ocr\ 目录
        //   ③ 直接就在 exe 旁边
        // 目录里要有 DllName + 模型（模型可以在目录里，也可以在它的 inference\ 子目录里）。
        static string PayloadDir()
        {
            List<string> cands = new List<string>();
            string env = null;
            try { env = Environment.GetEnvironmentVariable("SNAPWHEEL_OCR_DIR"); } catch { }
            if (!string.IsNullOrEmpty(env)) cands.Add(env);

            string exeDir = null;
            try { exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { }
            if (!string.IsNullOrEmpty(exeDir))
            {
                cands.Add(Path.Combine(exeDir, "ocr"));
                cands.Add(exeDir);
            }

            for (int i = 0; i < cands.Count; i++)
                if (File.Exists(Path.Combine(cands[i], DllName)) && FindModels(cands[i]) != null) return cands[i];
            return null;
        }

        // 模型文件名不写死：只认 *det*.onnx / *rec*.onnx / *dict*.txt 这三条规律。
        // 为什么：包换版本时（v5 → v6 → …）文件名会变，写死了就得改代码。
        // 代价是"目录里有多个候选"时取排序第一个 —— 这种目录本来就是我们自己发的，够用。
        static string[] FindModels(string dir)
        {
            string det = FindOne(dir, "*det*.onnx");
            string rec = FindOne(dir, "*rec*.onnx");
            string dict = FindOne(dir, "*dict*.txt");
            if (det == null || rec == null || dict == null) return null;
            return new string[] { det, rec, dict };
        }

        static string FindOne(string dir, string pattern)
        {
            foreach (string d in new string[] { dir, Path.Combine(dir, "inference") })
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    string[] hits = Directory.GetFiles(d, pattern);
                    if (hits.Length == 0) continue;
                    Array.Sort(hits, StringComparer.OrdinalIgnoreCase);
                    return hits[0];
                }
                catch { }
            }
            return null;
        }

        // ---------------- 开局 ----------------
        static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                string dir = PayloadDir();
                if (dir == null)
                {
                    _why = Lang.T(
                        "没有本地取字组件（Win7 需要 SnapWheel.exe 旁边放一个 ocr 目录，里面是 " + DllName + " 和模型）",
                        "No local OCR component (on Windows 7 put an \"ocr\" folder next to SnapWheel.exe containing " + DllName + " and the models)");
                    return;
                }

                string dllPath = Path.Combine(dir, DllName);
                IntPtr mod = LoadLibraryW(dllPath);
                if (mod == IntPtr.Zero)
                {
                    int code = Marshal.GetLastWin32Error();
                    _why = code == ErrorBadExeFormat
                        ? Lang.T("本地取字组件的位数不对（32 位系统要用 x86 版的 " + DllName + "）",
                                 "The local OCR component has the wrong bitness (a 32-bit system needs the x86 " + DllName + ")")
                        : Lang.T("本地取字组件加载失败（错误 " + code + "）：" + dllPath,
                                 "Could not load the local OCR component (error " + code + "): " + dllPath);
                    try { Err.Log("Ocr", new Exception(_why)); } catch { }
                    return;
                }

                CreateW create = Bind<CreateW>(mod, "ppocr_create_w");
                CreateExW createEx = Bind<CreateExW>(mod, "ppocr_create_ex_w");
                ConfigInit configInit = Bind<ConfigInit>(mod, "ppocr_config_init");
                _ocr = Bind<OcrBgr>(mod, "ppocr_ocr_bgr");
                _free = Bind<FreePtr>(mod, "ppocr_free");
                DestroyHandle destroy = Bind<DestroyHandle>(mod, "ppocr_destroy");
                if ((create == null && (createEx == null || configInit == null)) || _ocr == null || _free == null)
                {
                    _why = Lang.T("本地取字组件不完整（缺导出函数）", "The local OCR component is incomplete (missing exports)");
                    try { Err.Log("Ocr", new Exception(_why)); } catch { }
                    return;
                }
                GC.KeepAlive(destroy);   // 留着引用，免得将来误删导出检查

                string[] models = FindModels(dir);
                StringBuilder err = new StringBuilder(ErrorCapacity);
                IntPtr handle = IntPtr.Zero;
                int rc;
                if (createEx != null && configInit != null)
                {
                    PpocrConfig cfg = new PpocrConfig();
                    configInit(ref cfg);
                    cfg.det_model_path = models[0];
                    cfg.rec_model_path = models[1];
                    cfg.rec_dict_path = models[2];
                    cfg.limit_side_len = LimitSideLen();
                    cfg.cpu_threads = CpuThreads;
                    rc = createEx(ref cfg, out handle, err, err.Capacity);
                }
                else
                {
                    // 老版本 DLL 没有 create_ex：退回简版入口（会吃到 960 的默认上限）
                    rc = create(models[0], models[1], models[2], CpuThreads, out handle, err, err.Capacity);
                }
                if (rc != 0 || handle == IntPtr.Zero)
                {
                    _why = Lang.T("本地取字引擎初始化失败（" + rc + "）：" + err.ToString(),
                                  "Could not initialize the local OCR engine (" + rc + "): " + err.ToString());
                    try { Err.Log("Ocr", new Exception(_why)); } catch { }
                    return;
                }
                _handle = handle;
            }
            catch (Exception ex)
            {
                _why = Lang.T("本地取字组件不可用：", "The local OCR component is unavailable: ") + ex.Message;
                try { Err.Log("Ocr", ex); } catch { }
            }
        }

        static T Bind<T>(IntPtr module, string name) where T : class
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) return null;
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        // ---------------- 认字 ----------------
        // 传进来的是**紧凑的 BGRA 像素**（就是 Ocr.PixelsOf 拷出来的那份），
        // 所以 channels=4、stride=w*4 直接喂 —— 一个字节都不用转。
        public static string RecognizePixels(byte[] bgra, int w, int h, out string error)
        {
            error = null;
            Probe();
            if (_handle == IntPtr.Zero) { error = _why; return null; }
            if (bgra == null || w <= 0 || h <= 0 || bgra.Length < w * 4 * h)
            {
                error = Lang.T("像素数据不完整", "The pixel data is incomplete");
                return null;
            }

            GCHandle pin = default(GCHandle);
            try
            {
                pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                IntPtr json = IntPtr.Zero;
                int size = 0;
                StringBuilder err = new StringBuilder(ErrorCapacity);
                int rc = _ocr(_handle, pin.AddrOfPinnedObject(), w, h, 4, w * 4, out json, out size, err, err.Capacity);
                if (rc != 0 || json == IntPtr.Zero || size <= 0)
                {
                    error = Lang.T("识别失败（" + rc + "）：" + err.ToString(), "Recognition failed (" + rc + "): " + err.ToString());
                    try { Err.Log("Ocr", new Exception(error)); } catch { }
                    return null;
                }
                try
                {
                    byte[] utf8 = new byte[size];
                    Marshal.Copy(json, utf8, 0, size);
                    return JoinTexts(Encoding.UTF8.GetString(utf8));
                }
                finally { try { _free(json); } catch { } }   // 头文件：utf8_json 由 DLL 分配，必须 ppocr_free
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { Err.Log("Ocr", ex); } catch { }
                return null;
            }
            finally { if (pin.IsAllocated) pin.Free(); }
        }

        // JSON → 一段文字。
        //
        // 为什么自己扫而不用 JavaScriptSerializer：那个在 System.Web.Extensions 里，
        // 是 ASP.NET 的程序集，"一个 exe、零依赖"这条不破但要多背一个程序集引用和一条
        // 运行时解析路径（Win7 上还可能只装了 Client Profile 里没有它）。
        // 而我们要的信息只有一样：按顺序取出每个文本块的 "text"。
        // 输出格式是**我们自己钉死版本的那个 DLL** 产的（{"elapsed_ms":…,"results":[{"text":…},…]}），
        // 不是什么通用 JSON —— 扫不出东西时下面会明确报错，不会悄悄返回空字符串。
        static string JoinTexts(string json)
        {
            List<string> lines = new List<string>();
            if (!string.IsNullOrEmpty(json))
            {
                int i = 0;
                while (true)
                {
                    int k = json.IndexOf("\"text\"", i, StringComparison.Ordinal);
                    if (k < 0) break;
                    k += 6;
                    while (k < json.Length && (json[k] == ' ' || json[k] == '\t' || json[k] == ':')) k++;
                    if (k >= json.Length || json[k] != '"') { i = k; continue; }
                    k++;
                    StringBuilder sb = new StringBuilder();
                    while (k < json.Length && json[k] != '"')
                    {
                        char c = json[k];
                        if (c == '\\' && k + 1 < json.Length)
                        {
                            k++;
                            char e = json[k];
                            if (e == 'n') sb.Append('\n');
                            else if (e == 'r') sb.Append('\r');
                            else if (e == 't') sb.Append('\t');
                            else if (e == 'b') sb.Append('\b');
                            else if (e == 'f') sb.Append('\f');
                            else if (e == 'u')
                            {
                                int cp;
                                if (k + 4 < json.Length && int.TryParse(json.Substring(k + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                                {
                                    sb.Append((char)cp);
                                    k += 4;
                                }
                            }
                            else sb.Append(e);          // \" \\ \/ 等：原样就是那个字符
                        }
                        else sb.Append(c);
                        k++;
                    }
                    string t = sb.ToString().Trim();
                    if (t.Length > 0) lines.Add(t);
                    i = k + 1;
                }
            }
            return string.Join("\n", lines.ToArray());
        }
    }
}

namespace SnapWheel
{
    // ==================== 翻译（0.6.0：重做引擎链 + 去机翻腔） ====================
    // 为什么换掉原来那套：原来只有 MyMemory 一个源（它本质是"翻译记忆库"，质量参差 + 老限流），
    // 用户原话是「翻译后机翻过于严重」。2026-09-14 在本机实测同一句英文：
    //   原文    Failed to load the resource bundle. Please make sure the application is not
    //           running in compatibility mode.
    //   MyMemory 加载资源捆绑包失败。请确保应用程序未在兼容模式下运行。      <- 生硬
    //   有道      加载资源包失败。请确保应用程序没有在兼容模式下运行。        <- 明显自然
    // 同时实测：腾讯 transmart 返回空、Google gtx 直接 429（国内不通）、
    // 豆包/DeepSeek 的官方 API 没有免 key 的（都得注册 key）——
    // 所以策略是三层：
    //   ① 用户自己在设置里填的 OpenAI 兼容接口（URL + key + 模型名）——DeepSeek / 豆包 / 通义 / 本地 Ollama 都兼容。
    //      填了就用它：质量最好，也真正消掉机翻腔。没填就跳过，不打扰。
    //   ② 有道 aidemo：**免费、无 key、国内直连**，质量明显好于 MyMemory —— 默认走这层。
    //   ③ MyMemory：保底（境外、可能不稳，但聊胜于无）。
    // 三层都失败才报错，并且把**最有说服力的那个原因**告诉用户（配置问题优先于网络问题）。
    //
    // 另外：换引擎只能改善，不能根除"腔调"。所以最后再过一道 Tone()：
    // 中英标点归位、中文之间多余空格删掉、行首尾空格与多余空行清理。
    // 这一步刻意做得**很保守** —— 绝不碰版本号/小数/网址里的点（v0.5.3、1.5、http://a.b 都原样）。
    static class Translate
    {
        const int MaxChunk = 420;          // 单次请求的长度上限，长文切段
        const int TimeoutMs = 15000;       // 免费接口一条的时限（长文要发好几次）
        const int LlmTimeoutMs = 30000;    // 大模型慢一些，单独给宽一点

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

        public static string TargetLabel(string text) { return LooksChinese(text) ? Lang.T("英文", "English") : Lang.T("中文", "Chinese"); }

        // 成功返回译文；失败返回 null 并给出人话原因
        public static string Run(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) { error = Lang.T("没有要翻译的文字", "Nothing to translate"); return null; }
            string src = LooksChinese(text) ? "zh-CN" : "en";
            string dst = LooksChinese(text) ? "en" : "zh-CN";
            bool toChinese = (dst != "en");

            StringBuilder outp = new StringBuilder();
            string[] chunks = Split(text, MaxChunk);
            // 配置只读一次就够：One() 里每段都去 Load() 会把 settings.ini 反复读好几遍（长文切段多）
            Settings st = null;
            try { st = Settings.Load(); } catch { st = null; }
            int empty = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                string one = One(chunks[i], src, dst, st, out error);
                if (one == null) return null;
                one = Tone(one, toChinese).Trim();
                if (one.Length == 0) { empty++; continue; }
                if (outp.Length > 0) outp.Append('\n');       // 分段译完拼回去，一段一行
                outp.Append(one);
            }
            if (outp.Length == 0)
            {
                error = empty > 0 ? Lang.T("接口没返回译文（多半是被限流了），过一会儿再试", "The API returned no translation (likely rate-limited) - try again shortly") : Lang.T("没有要翻译的文字", "Nothing to translate");
                return null;
            }
            return outp.ToString();
        }

        static string[] Split(string s, int max)
        {
            if (s.Length <= max) return new string[] { s };
            List<string> parts = new List<string>();
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

        // ============================ 引擎链 ============================
        // 一段文字：按 ① LLM ② 有道 ③ MyMemory 的顺序试，谁先成功用谁。
        // 注意：① 是用户自己填的，**填了但失败不该让功能整个不可用** —— 记下原因继续往下退，
        // 只有当后面也全失败时，才把最先失败的那个原因（配置问题）报出来。
        static string One(string text, string src, string dst, Settings st, out string error)
        {
            error = null;

            if (st != null && !string.IsNullOrEmpty(st.LlmUrl) && st.LlmUrl.Trim().Length > 0)
            {
                string e1;
                string r1 = OneLlm(text, src, dst, st, out e1);
                if (r1 != null) return r1;
                error = e1;
            }

            string e2;
            string r2 = OneYoudao(text, src, dst, out e2);
            if (r2 != null) return r2;
            if (string.IsNullOrEmpty(error)) error = e2;

            string e3;
            string r3 = OneMyMemory(text, src, dst, out e3);
            if (r3 != null) return r3;
            if (string.IsNullOrEmpty(error)) error = e3;

            return null;
        }

        static void Tls()
        {
            // .NET Framework 默认只肯用 TLS 1.0/SSL3，现代接口一律要 TLS 1.2 ——
            // 不设这句就报"未能创建 SSL/TLS 安全通道"（实测就是这个错）
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        // ---------------- ① 大模型（OpenAI 兼容：DeepSeek / 豆包 / 通义 / Ollama 都能用） ----------------
        static string OneLlm(string text, string src, string dst, Settings st, out string error)
        {
            error = null;
            try
            {
                Tls();
                string url = st.LlmUrl.Trim();
                if (url.Length == 0) { error = Lang.T("没填翻译接口地址", "No translation API URL is configured"); return null; }
                // 允许只填到 /v1，剩下那截自动补；填全了就用填的
                if (url.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    if (url.EndsWith("/")) url = url.Substring(0, url.Length - 1);
                    url += "/chat/completions";
                }
                string want = (dst == "en") ? Lang.T("英文", "English") : Lang.T("简体中文", "Simplified Chinese");
                string sys = Lang.T("你是翻译引擎。只输出译文本身：不要解释、不要引号、不要 Markdown 标记。", "You are a translation engine. Output only the translation itself: no explanations, no quotes, no Markdown.")
                           + Lang.T("严格保持原文的换行与段落结构，不要合并或增删句子。", "Preserve the original line breaks and paragraph structure exactly; do not merge, add or drop sentences.");
                string usr = Lang.T("把下面的内容翻译成", "Translate the following into ") + want + "：\n" + text;

                StringBuilder body = new StringBuilder();
                body.Append("{\"model\":\"").Append(JsonEsc(st.LlmModel)).Append("\"");
                body.Append(",\"temperature\":0.2,\"stream\":false,\"messages\":[");
                body.Append("{\"role\":\"system\",\"content\":\"").Append(JsonEsc(sys)).Append("\"},");
                body.Append("{\"role\":\"user\",\"content\":\"").Append(JsonEsc(usr)).Append("\"}]}");

                byte[] data = Encoding.UTF8.GetBytes(body.ToString());
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = LlmTimeoutMs;
                req.ReadWriteTimeout = LlmTimeoutMs;
                req.UserAgent = "SnapWheel/" + AppInfo.Version;
                if (!string.IsNullOrEmpty(st.LlmKey))
                    req.Headers["Authorization"] = "Bearer " + st.LlmKey.Trim();
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    string t = ExtractField(json, "content");
                    if (t == null)
                    {
                        error = Lang.T("翻译接口没按 OpenAI 格式返回（该填 /v1/chat/completions 那种地址）", "The API did not return OpenAI-style output (the URL should look like /v1/chat/completions)");
                        return null;
                    }
                    return t;
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse hr = wex.Response as HttpWebResponse;
                string code = hr != null ? ("HTTP " + (int)hr.StatusCode) : (wex.Status == WebExceptionStatus.Timeout ? Lang.T("超时", "Timed out") : wex.Message);
                error = Lang.T("自填翻译接口连不上（", "Custom translation API unreachable (") + code + Lang.T("）——检查地址 / key / 模型名", ") - check the URL / key / model name");
                return null;
            }
            catch (Exception ex) { error = Lang.T("自填翻译接口失败：", "Custom translation API failed: ") + ex.Message; return null; }
        }

        // ---------------- ② 有道（免费、无 key、国内直连） ----------------
        static string OneYoudao(string text, string src, string dst, out string error)
        {
            error = null;
            try
            {
                Tls();
                string body = "q=" + Uri.EscapeDataString(text) + "&from=" + YdLang(src) + "&to=" + YdLang(dst);
                byte[] data = Encoding.UTF8.GetBytes(body);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://aidemo.youdao.com/trans");
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded";
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                req.UserAgent = "SnapWheel/" + AppInfo.Version;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    return ReadYoudao(json, dst, out error);
                }
            }
            catch (WebException wex)
            {
                error = Lang.T("免费翻译接口连不上：", "Free translation API unreachable: ") + (wex.Status == WebExceptionStatus.Timeout ? Lang.T("超时", "Timed out") : wex.Message);
                return null;
            }
            catch (Exception ex) { error = Lang.T("翻译失败：", "Translation failed: ") + ex.Message; return null; }
        }

        // MyMemory 用 zh-CN，有道用 zh-CHS —— 别混用（混了有道会把中文当未知语言）
        static string YdLang(string code)
        {
            if (code == "zh-CN" || code == "zh" || code == "zh-Hans") return "zh-CHS";
            if (code == "zh-TW" || code == "zh-Hant") return "zh-CHT";
            return code;
        }

        // 有道的返回长这样（字段顺序不保证）：
        //   {"tSpeakUrl":"...","requestId":"...","query":"原文","translation":["译文"],"mTerminalDict":{...},...}
        // 解析要点：
        //   · 取**最后一个** "translation" —— 用户原文里万一带这个词，它出现在更早的 query 字段里，取最后一个才不会读错；
        //   · translation 是**数组**，可能按句切分成多项，全都要（中文之间不加空格、英文之间加空格）。
        internal static string ReadYoudao(string json, string dst, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(json)) { error = Lang.T("翻译接口没有返回内容", "The API returned no content"); return null; }
            int k = json.LastIndexOf("\"translation\"", StringComparison.Ordinal);
            if (k < 0) { error = Lang.T("翻译接口返回的内容看不懂（可能被限流了）", "The API returned something unreadable (possibly rate-limited)"); return null; }
            int lb = json.IndexOf('[', k);
            if (lb < 0) { error = Lang.T("翻译接口返回的内容看不懂", "The API returned something unreadable"); return null; }
            int rb = json.IndexOf(']', lb);
            if (rb < 0) rb = json.Length;

            List<string> parts = new List<string>();
            int i = lb + 1;
            while (i < rb)
            {
                if (json[i] != '"') { i++; continue; }
                int next;
                string one = ReadJsonString(json, i, out next);
                if (next <= i) break;
                i = next;
                if (one != null) parts.Add(one);
            }
            if (parts.Count == 0) { error = Lang.T("免费翻译额度用完了（限流）——过一会儿再试", "Free translation quota exhausted (rate-limited) - try again shortly"); return null; }

            string sep = (dst == "en") ? " " : "";
            StringBuilder sb = new StringBuilder();
            for (int n = 0; n < parts.Count; n++)
            {
                if (n > 0) sb.Append(sep);
                sb.Append(parts[n]);
            }
            return sb.ToString();
        }

        // ---------------- ③ MyMemory（保底，原来的实现） ----------------
        static string OneMyMemory(string text, string src, string dst, out string error)
        {
            error = null;
            try
            {
                Tls();
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
                error = Lang.T("备用翻译接口连不上：", "Fallback translation API unreachable: ") + (wex.Status == WebExceptionStatus.Timeout ? Lang.T("超时", "Timed out") : wex.Message);
                return null;
            }
            catch (Exception ex) { error = Lang.T("翻译失败：", "Translation failed: ") + ex.Message; return null; }
        }

        // 从 MyMemory 返回里读结果：成功=译文（可能为空串）；失败=null，并把人话原因写进 error。
        // 几种失败要分开，不然用户看到的永远是同一句"内容看不懂"：
        //   限流（MYMEMORY WARNING / responseDetails 带 LIMIT）-> 说清楚是被限流了
        //   别的错误状态 -> 把接口给的原因原样带出来
        internal static string ReadResult(string json, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(json)) { error = Lang.T("备用接口没有返回内容", "The fallback API returned no content"); return null; }

            string t = ExtractField(json, "translatedText");
            string details = ExtractField(json, "responseDetails");
            string status = ExtractRaw(json, "responseStatus");

            bool limited = (t != null && t.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0)
                        || (details != null && (details.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase) >= 0
                                             || details.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) >= 0));
            if (limited)
            {
                error = Lang.T("免费翻译额度用完了（MyMemory 限流）——过一会儿再试", "Free translation quota exhausted (MyMemory rate limit) - try again shortly");
                return null;
            }
            if (!string.IsNullOrEmpty(status) && status != "200")
            {
                error = Lang.T("备用翻译接口报错：", "Fallback translation API error: ") + (string.IsNullOrEmpty(details) ? status : details);
                return null;
            }
            if (t == null) { error = Lang.T("备用接口返回的内容看不懂（可能被限流了）", "The fallback API returned something unreadable (possibly rate-limited)"); return null; }
            return t.Trim();
        }

        // ============================ 去机翻腔（保守后处理） ============================
        // 只做四件事，都是"引擎不该做但经常不做"的收尾：
        //   ① 去掉引擎自己带回来的包裹引号（有时它会很客气地把译文用引号括起来）
        //   ② 翻成中文时把"被中文字夹着"的半角标点改成全角 —— **绝不动版本号/小数/网址里的点**
        //   ③ 中文与中文之间的半角空格删掉（机翻常见："加载 资源包 失败"）
        //   ④ 行首尾空格、连续 3 个以上空行清理
        internal static string Tone(string t, bool toChinese)
        {
            if (string.IsNullOrEmpty(t)) return t;
            t = t.Trim();

            // ① 整段被一对引号包着才脱 —— 只脱最外层，里面本来就有引号的不动
            if (t.Length >= 2)
            {
                char a = t[0], b = t[t.Length - 1];
                if ((a == '"' && b == '"') || (a == '“' && b == '”') || (a == '\'' && b == '\''))
                    t = t.Substring(1, t.Length - 2).Trim();
            }

            if (toChinese) t = PunctToCjk(t);
            t = TightenCjk(t);
            t = Tidy(t);
            return t;
        }

        // 半角 -> 全角：只有"前一个字是中文、后一个字不是数字/字母"时才换。
        // 这样 v0.5.3 / 3.14 / api.example.com / e.g. 里的点全都原样保留。
        static string PunctToCjk(string s)
        {
            StringBuilder o = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                char n = (i + 1 < s.Length) ? s[i + 1] : '\0';
                bool isP = (c == ',' || c == '.' || c == '?' || c == '!' || c == ':' || c == ';');
                if (isP && o.Length > 0 && IsCjk(o[o.Length - 1]))
                {
                    bool nextBad = (n >= '0' && n <= '9') || (n >= 'a' && n <= 'z') || (n >= 'A' && n <= 'Z');
                    if (!nextBad)
                    {
                        o.Append(c == ',' ? '，' : c == '.' ? '。' : c == '?' ? '？' : c == '!' ? '！' : c == ':' ? '：' : '；');
                        continue;
                    }
                }
                o.Append(c);
            }
            return o.ToString();
        }

        // 中文之间的半角空格删掉；顺便把全角空格当半角看
        static string TightenCjk(string s)
        {
            StringBuilder o = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\u3000')
                {
                    char prev = o.Length > 0 ? o[o.Length - 1] : '\0';
                    char next = (i + 1 < s.Length) ? s[i + 1] : '\0';
                    if (IsCjk(prev) && IsCjk(next)) continue;      // 中文 中 文 -> 删掉这个空格
                }
                o.Append(c);
            }
            return o.ToString();
        }

        // 行尾空格 + 连续空行压缩
        static string Tidy(string s)
        {
            string[] lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            StringBuilder o = new StringBuilder(s.Length);
            int blank = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i].TrimEnd();
                if (ln.Trim().Length == 0)
                {
                    blank++;
                    if (blank > 1) continue;                      // 最多留一个空行
                }
                else blank = 0;
                if (o.Length > 0) o.Append('\n');
                o.Append(ln);
            }
            return o.ToString().Trim();
        }

        // ============================ JSON 小工具（不引 JSON 库） ============================
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

        // 从 {"responseData":{"translatedText":"..."}} 里把那个字段抠出来
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
            int next;
            return ReadJsonString(json, i, out next);
        }

        // 从 s[i]（必须是引号）读一个 JSON 字符串，next = 引号之后的下一格；解转义（\n \uXXXX ...）
        static string ReadJsonString(string s, int i, out int next)
        {
            next = i;
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    i += 2;
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 3 < s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                                { sb.Append((char)code); i += 4; }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    continue;
                }
                if (c == '"') { i++; break; }
                sb.Append(c);
                i++;
            }
            next = i;
            return sb.ToString();
        }

        // 拼 JSON 请求体时用：引号/反斜杠/换行必须转义，否则大模型接口直接 400
        static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder o = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': o.Append("\\\""); break;
                    case '\\': o.Append("\\\\"); break;
                    case '\n': o.Append("\\n"); break;
                    case '\r': o.Append("\\r"); break;
                    case '\t': o.Append("\\t"); break;
                    default:
                        if (c < ' ') o.Append("\\u").Append(((int)c).ToString("x4"));
                        else o.Append(c);
                        break;
                }
            }
            return o.ToString();
        }
    }
}

namespace SnapWheel
{
    // ==================== 滚动长截图：拼接引擎（0.6.0 阶段 1） ====================
    // 用户拍板的交互：**他手动滚，程序跟着无缝拼接**（不给目标窗口发合成滚轮消息 ——
    // 那样既要猜滚几格、又要处理惯性/节流，还挑窗口）。
    //
    // 流程（浮层那边调用）：
    //   1) 进入长图模式，抓第一屏 -> LongShot.Start(first)
    //   2) 用户滚动目标内容；每次"停稳"后再抓一屏 -> LongShot.Push(frame)
    //        返回 true  = 接上了（返回新增了几行）
    //        返回 false = 这一屏没对上（页面动画/滚动没发生/滚太多）—— 提示用户再滚一下，不破坏已有画布
    //   3) 用户按 Enter/双击结束 -> LongShot.Result 拿到整张长图
    //
    // 算法（只找**竖直**偏移，横向不动 —— 滚动截图不会左右移）：
    //   · 拿"上一屏的底部带"当模板（默认取最后 240 行，并避开最底 8 行：那里常常是滚动条圆角、
    //     提示条、渐变淡出，它们不随内容滚动，会把匹配带偏）；
    //   · 在新屏里沿竖直方向搜 d（= 这一屏新露出多少行），使 new[y-d] ≈ prev[y] 最接近；
    //   · 用行方向 2 行取 1、列方向 2 px 取 1 的灰度采样算平均绝对差（SAD），
    //     满分辨率太慢（一次搜索要几百 MB 次比较），采样后误差仍在 1 个灰度级内，够用；
    //   · 判"对上了"要有置信度，两条同时满足：
    //       ① 最佳 d 的平均差 < MatchTol（灰度级，默认 10）
    //       ② 最佳 d 明显好于次优（去掉最佳附近 ±24 行后的最好值）：best*1.8 < second
    //       否则返回 false —— **宁可让用户再滚一次，也不要拼错**（拼错会留下一条错位的接缝）。
    //   · 右侧 24px 与最底 8~24px 一律不参与匹配：滚动条/水印/窗口圆角都在那儿，且它们不滚动。
    //
    // 内存：画布最高 MaxCanvasH 行，按 32bpp 算 20000×1200×4B ≈ 96MB —— 到顶就停下并告诉用户。
    // 线程：这里全是 LockBits + byte[]，不碰 GDI 绘制，可以在后台线程跑（浮层抓屏是另一回事）。
    // 一次长截图 = 一个 LongShot 实例（0.6.0 改成实例类了：它要记住"上一屏"和画布）。
    // 里面的 Find / Sample 是纯函数，保持 static，方便单独验证。
    sealed class LongShot
    {
        public const int MaxCanvasH = 20000;    // 画布高度上限
        public const int BandRows = 100;        // 模板带高度（薄一点 → 能检测的单帧滚动量更大）
        // ★ 模板带**放在屏幕高度的 62% 处**，绝不贴屏幕底边 —— 原因见 Find() 里的说明
        //   （屏幕最底下通常是任务栏，它在截图里是静止的，拿它当模板永远匹配不上）。
        public const float BandCenterFrac = 0.72f;
        // 第二段验证带（放在屏幕 28% 处）：同一个偏移 d 必须在**两段互不相邻的画面**上都对得上才算数。
        // 这是防"假匹配"的关键 —— 网页里到处是周期（表格行、列表项、等距的卡片），单段匹配时
        // 一个错误的 d 也可能把线条对齐（实测滚过头时会挑出 300 这种错偏移，接缝整条错位）；
        // 两段隔得远，要同时骗过两边的概率低得多。
        public const float Band2CenterFrac = 0.28f;
        // ⚠️ 这个带**必须薄**，原因是一条硬约束：
        //     能检测出来的最大滚动量 d ≤ 模板带顶行离屏幕顶的距离（bandTop），
        //     因为模板的每一行 y 都要能在新屏里找到 y-d ≥ 0。
        //   带取 240 行时 bandTop 只剩 352（600 高的屏）→ d 只能搜到 240 左右，
        //   而人滚一次常常就是 300~400 行 —— 于是每一帧都"找不到重叠"，只能挑到某个
        //   局部最优（实测挑出了 189、160 这种数），拼出来的图是错的。
        //   0.6.0 用合成长页逐帧验证时就是这么暴露的。120 行：样本 ~2.9 万个点，够稳；
        //   bandTop 抬到 472，一次滚到 470 行都还认得出来。
        public const int SkipBottom = 8;        // 模板带离屏幕底边的距离（躲开滚动条/圆角）
        public const int SkipRight = 24;        // 右侧不参与匹配的宽度（滚动条）
        public const int MinNewRows = 6;        // 小于这个行数算"没滚"，不拼
        // 判据阈值：合成图测试里像素完全一致（真匹配代价 ≈ 0、差异比例 ≈ 0），但**真实屏幕不是** ——
        // 浏览器平滑滚动会让内容做子像素重采样、光标在闪、还有视频/动画，前后帧不可能逐像素相同。
        // 所以这里按"真实场景"放宽（0.6.0 实测：贴屏幕底边的模板带 + 过严的阈值，会让真实使用里
        // 一帧都接不上）；防止误匹配主要靠下面那条"best×1.8 必须小于 second"的置信度判据。
        public const double MatchTol = 32.0;     // 代价上限（真实屏幕实测 best 20~24；配合"小步滚动"重叠区大，这个值够用）
        public const double MaxBadRatio = 0.13;  // 差异像素比例上限（真实屏幕实测 6%~8%，原来 5% 把每一帧都拒了）
        public const int MinCanvasLeft = 0;

        // 一次匹配的结果，方便浮层显示"这次接上了多少行 / 为什么没接上"
        public sealed class Match
        {
            public int NewRows;        // 新露出多少行（0 = 没成功）
            public double Score;       // 代价（越小越像）
            public double Second;      // 次优代价（用来看置信度）
            public double BadRatio;    // 差异像素比例（最能说明"到底像不像"）
            public string Why;         // 失败原因（人话）
            public bool Ok { get { return NewRows > 0; } }
        }

        Bitmap _canvas;                // 已拼好的长图（32bppPArgb）
        int _w, _h;                    // 屏幕宽高（= 单帧尺寸，全程不变；变了就重来）
        int _canvasH;                  // 画布已用高度
        byte[] _prev;                  // 上一屏的灰度采样（宽 _w、高 _h，每像素 1 字节）
        int _sw;                       // 采样后的行宽（= ceil((_w - SkipRight) / 2)）
        int _sh;                       // 高（= _h，行不跳采样，只有列跳）
        string _why;
        int _shotCount;
        bool _stillTrimmed;          // 第一帧那条静止区（任务栏）裁掉了没有

        public int Height { get { return _canvasH; } }
        public int Shots { get { return _shotCount; } }
        public string LastWhy { get { return _why; } }
        public Bitmap Result { get { return _canvas; } }
        public bool Full { get { return _canvasH >= MaxCanvasH; } }

        // 开一张新长图：把第一屏整张贴进画布
        public bool Start(Bitmap first, out string error)
        {
            error = null;
            _why = null;
            _shotCount = 0;
            if (first == null) { error = Lang.T("没有拿到第一屏", "Did not get the first screen"); return false; }
            _w = first.Width; _h = first.Height;
            if (_h < BandRows * 2 + MinNewRows) { error = Lang.T("这一屏太矮，滚动长图用不了", "This area is too short for a scrolling capture"); return false; }

            _canvasH = _h;
            if (_canvasH > MaxCanvasH) _canvasH = MaxCanvasH;
            _canvas = new Bitmap(_w, MaxCanvasH, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(_canvas))
            {
                g.DrawImageUnscaled(first, 0, 0);
            }
            _sw = (_w - SkipRight + 1) / 2;
            _sh = _h;
            _prev = Sample(first);
            _shotCount = 1;
            return true;
        }

        // 新一屏：算偏移 -> 命中就把新增的那些行追加到画布下方
        public bool Push(Bitmap frame, out int addedRows)
        {
            addedRows = 0;
            _why = null;
            if (_canvas == null || frame == null) { _why = Lang.T("还没开始长图", "Not started yet"); return false; }
            if (frame.Width != _w || frame.Height != _h) { _why = Lang.T("画面尺寸变了（换了窗口/显示器？），这张长图到此为止", "The screen size changed (another window or monitor?), so this capture stops here"); return false; }
            if (Full) { _why = Lang.T("长图已经到最大高度了", "The image reached its maximum height"); return false; }

            byte[] cur = Sample(frame);
            Match m = Find(_prev, cur, _w, _h, _sw, _sh);
            // 诊断：把每次判定的依据写进日志（真实屏幕上"接不上"时，这是唯一能看出卡在哪的东西）
            try
            {
                Err.Log("LongShot", new Exception("帧 " + _shotCount + " " + _w + "x" + _h + " -> "
                    + (m.Ok ? ("接上 " + m.NewRows + Lang.T(" 行", " rows")) : "拒绝")
                    + " best=" + m.Score.ToString("0.00") + " second=" + m.Second.ToString("0.00")
                    + " bad=" + m.BadRatio.ToString("0.000") + " why=" + (m.Why == null ? "-" : m.Why)));
            }
            catch { }
            if (!m.Ok)
            {
                _why = m.Why;
                _prev = cur;   // 关键：失败也把基准推到当前帧，否则下一拍还在跟起点帧比，越滚越对不上
                return false;
            }

            addedRows = m.NewRows;
            int room = MaxCanvasH - _canvasH;
            if (addedRows > room) addedRows = room;
            if (addedRows <= 0) { _why = Lang.T("长图已经到最大高度了", "The image reached its maximum height"); return false; }

            // 把新屏的**最后 addedRows 行**贴到画布下方：这就是新露出来的内容
            // ⚠️ 取"新露出的内容"必须避开屏幕底部的**静止区**（典型就是任务栏）：它不随页面滚动移动，
            // 直接取屏幕最底部的 addedRows 行，等于每一帧都把任务栏又贴进长图一次 ——
            // 结果就是"长图里全是堆叠的任务栏、几乎没有内容"（用户实测）。
            //
            // ⚠️⚠️ 但"静止"**不能只看"这一行两帧一模一样"**。
            //    空白行在两帧里当然也一样 —— 于是一张有大片留白的**普通网页**，
            //    底部会被判成"一大片静止区"，srcY 被抬高，**每一帧都重复贴一段已经贴过的内容**。
            //    这就是用户报的"错位"，而且它对"正常页面"也会发作（合成长页测试一直没暴露它，
            //    因为合成图里全是密排的文字、没有留白）。
            //    正确判据要**同时看两个假设**，顺序不能反：
            //      · 滚动假设：cur[y] ≈ prev[y-d] → 这一行跟着页面滚了 → 到底了，停
            //      · 静止假设：cur[y] ≈ prev[y]   → 这一行没动 → 才可能是任务栏
            //    先看滚动假设：**空白行在滚动假设下也成立**（两边都白）→ 直接停、still=0。
            //    这正是我们要的保守默认 —— 宁可当成"会滚"，也不要凭空抬高 srcY。
            //    只有"滚动假设不成立、静止假设成立"的行才算静止区。
            int bandBot2 = (int)(_h * BandCenterFrac) + BandRows / 2;   // 和 Find 里那条模板带同一条
            int stillCap = _h - bandBot2 - 2;
            // ⚠️⚠️ **只在"两个假设都验得了"的行里找静止区** —— 也就是从 `h-d-1` 往上扫。
            //
            // 屏幕最底下那 d 行，参照行（本帧 y+d 行）在**屏幕外**：那些内容本来就不在上一帧里，
            // 是刚滚进来的。所以拿它们判"跟不跟得上滚动"是**问不出答案的**。
            // 旧代码从 `h-1` 开始扫，于是每一帧都至少把 d 行算成"静止" → `still >= d` →
            // `srcY = h - still - d` 每帧往上一挪 d → **每段重复一次、周期正好 = d**。
            // 用户那张真长图量出来的重复周期是 125，而引擎日志里 d 也正好是 125 —— 完全对上。
            //
            // 底部那 d 行怎么办：如果紧挨着它们上面的那段是静止的，就认为静止区**一直延伸到屏幕底边**
            // （任务栏正是这样）；否则它们就是普通的新内容。
            int stillAbove = 0;
            for (int y = _h - addedRows - 1; y > bandBot2 && stillAbove < stillCap; y--)
            {
                if (RowDiffOffset(_prev, cur, _sw, y, addedRows) <= 3.0) break;   // 跟得上滚动 → 静止区到此为止
                stillAbove++;
            }
            int still = stillAbove > 0 ? stillAbove + addedRows : 0;
            // ⚠️ take 必须就是"实际画了几行"。
            //    原来是 take/srcY/_canvasH 三个量分开算的，srcY<0 时 take 会变成 _h-still、
            //    比真正画上去的 addedRows 大，于是画布上留下空行 —— **之后每一帧的落点整体偏移**。
            // ⚠️ 第一帧是**整屏**贴进画布的（见 Start），但它底部的 still 行是**静止区**、不是页面内容。
            //    不裁掉的话，画布开头就带着一条任务栏，之后所有内容都跟着错位
            //    （实测：任务栏 48px 的用例正好在第 552 行 = 600-48 处开始对不上）。
            if (!_stillTrimmed && still > 0)
            {
                _stillTrimmed = true;
                _canvasH -= still;
                if (_canvasH < 1) _canvasH = 1;
            }
            int take = addedRows;
            int srcY = _h - still - take;
            if (srcY < 0) { srcY = 0; take = _h - still; }
            if (take <= 0) { _why = "没有可拼的新内容"; return false; }
            using (Graphics g = Graphics.FromImage(_canvas))
            {
                g.DrawImage(frame, new Rectangle(0, _canvasH, _w, take),
                                   new Rectangle(0, srcY, _w, take), GraphicsUnit.Pixel);
            }
            _canvasH += take;
            _shotCount++;
            _prev = cur;
            return true;
        }

        // 结束：把画布裁到实际高度，返回一张干净的长图
        public Bitmap Finish()
        {
            if (_canvas == null) return null;
            if (_canvasH >= _canvas.Height) return _canvas;
            Bitmap r = new Bitmap(_w, Math.Max(1, _canvasH), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(r))
            {
                g.DrawImage(_canvas, new Rectangle(0, 0, _w, _canvasH),
                                    new Rectangle(0, 0, _w, _canvasH), GraphicsUnit.Pixel);
            }
            return r;
        }

        // ============================ 匹配 ============================
        // prev / cur：上一屏与这一屏的灰度采样（列已按 2px 采样，宽 = _sw，高 = _sh）
        // 返回：新露出多少行 + 置信度
        // 单个候选偏移 d 的代价（越小越像）。
        // 代价 = **差异像素比例 × 100** + 平均灰度差。
        // 为什么主判据是"差异像素比例"而不是平均差：
        //   真匹配（同一段内容）像素级几乎完全一样，只有抗锯齿边缘会差一点 —— 差异像素比例 ~0.1%；
        //   假匹配（周期图案对齐，比如表格线对齐了但格子里的字没对齐）平均差可能看着也不大，
        //   但"明显不同的像素"会成片出现，比例能到 2%~5%。
        //   平均差会被大片相同背景稀释（这正是用例 2 滚过头时"线条对齐"假匹配能溜过去的原因）。
        // 输出 badRatio 供调用方判定；样本太少返回 MaxValue 表示这个候选不算数。
        static double Score(byte[] prev, byte[] cur, int sw, int bandTop, int bandBot, int bandTop2, int bandBot2,
                            int d, bool[] sameRow, out double badRatio)
        {
            long sad = 0; int n = 0, bad = 0;
            AddBand(prev, cur, sw, bandTop, bandBot, d, sameRow, ref sad, ref n, ref bad);
            AddBand(prev, cur, sw, bandTop2, bandBot2, d, sameRow, ref sad, ref n, ref bad);
            if (n < 200) { badRatio = 1; return double.MaxValue; }
            badRatio = (double)bad / n;
            return badRatio * 100.0 + (double)sad / n;
        }

        // 把一段横带上的"模板行 y 对新屏行 y-d"累加进统计
        static void AddBand(byte[] prev, byte[] cur, int sw, int bandTop, int bandBot, int d, bool[] sameRow,
                            ref long sad, ref int n, ref int bad)
        {
            for (int y = bandTop; y < bandBot; y += 3)
            {
                int y2 = y - d;
                if (y2 < 0) continue;
                // ⚠️ 跳过"两帧里**同一行**本来就一样"的行 —— 那要么是 sticky 固定顶栏，要么是空白行，
                // 两种都不携带"滚了多少"的信息，拿来比只会把真匹配污染掉。
                //
                // 不做这一步的话，长图**在真实网页上根本接不上**：网页几乎都有 sticky 顶栏，
                // 而偏移 d 下的参照行 y2 = y-d 会整段落进那一块固定头里。
                // 实测（合成 sticky 头用例）：带2 的 48% 样本在拿"页面内容"比"固定头"，
                // bad 从 0.000 涨到 0.21，于是每一帧都被拒、长图停在第一屏。
                if (sameRow != null && y2 < sameRow.Length && sameRow[y2]) continue;
                int o1 = y * sw, o2 = y2 * sw;
                for (int x = 0; x < sw; x += 4)
                {
                    int a = prev[o1 + x], b = cur[o2 + x];
                    int diff = (a > b) ? (a - b) : (b - a);
                    sad += diff;
                    if (diff > 12) bad++;          // 12 个灰度级以上就算"这个像素不一样"
                    n++;
                }
            }
        }

        internal static Match Find(byte[] prev, byte[] cur, int w, int h, int sw, int sh)
        {
            Match m = new Match();
            // 模板带的**位置**是这套算法最容易踩的坑：不能贴屏幕底边。
            // 屏幕最底下通常是**任务栏** —— 它在抓屏里是静止的、不跟着页面滚动走，
            // 拿它当模板的话"怎么对都对得上"（甚至对它自己 SAD≈0），匹配必然失败或挑到假偏移。
            // 0.6.0 实测：真实屏幕上"压根接不上"就是这个原因，而合成长页测试没暴露它
            // （合成图里没有任务栏）。
            // 现在取"屏幕高度 62% 处"为中心的一条带：稳稳落在内容区，上不碰标题栏、下不碰任务栏。
            int bandBot = (int)(h * BandCenterFrac) + BandRows / 2;
            if (bandBot > h - SkipBottom) bandBot = h - SkipBottom;
            int bandTop = bandBot - BandRows;
            if (bandTop < 0) bandTop = 0;

            int maxD = bandTop;                               // d 最大到"模板带顶行"：再大模板就顶出屏幕了
            if (maxD > 500) maxD = 500;                       // 上限：再大的单帧滚动本来也难保证拼对，还极费时间
            if (maxD > h - 16) maxD = h - 16;                 // 保险（矮屏）

            // 第二段验证带（屏幕 28% 处）：和主带隔得远，专门用来拆穿"周期图案对齐"的假匹配
            int band2Bot = (int)(h * Band2CenterFrac) + BandRows / 2;
            if (band2Bot > h - SkipBottom) band2Bot = h - SkipBottom;
            int band2Top = band2Bot - BandRows;
            if (band2Top < 0) band2Top = 0;
            // 先算一遍"两帧里同一行是不是本来就一样"（每帧一次，别放进 d 循环里 —— 那会把匹配器的开销翻倍）
            bool[] sameRow = new bool[h];
            for (int y = 0; y < h; y++) sameRow[y] = RowDiff(prev, cur, sw, y) <= 3.0;
            double best = double.MaxValue, second = double.MaxValue;
            int bestD = 0;
            double bestBad = 1;

            // 第一遍：找代价最小的 d
            for (int d = MinNewRows; d <= maxD; d++)
            {
                double bad;
                double cost = Score(prev, cur, sw, bandTop, bandBot, band2Top, band2Bot, d, sameRow, out bad);
                if (cost < best) { best = cost; bestD = d; bestBad = bad; }
            }

            // 第二遍：在**排除最佳附近 ±24 行**之后找次优。
            // ⚠️ 这一步不能省、也不能用"顺手记录次优"的写法：相邻偏移（d±1、d±2…）的分数
            // 天然几乎一样（内容本来就平滑），顺手记下来的"次优"永远是 best+一点点，
            // 于是下面的置信度判据必然判成"不够独特"，**每一帧都会被拒** ——
            // 0.6.0 的合成长页验证就是这么暴露出来的（五帧全拒、长图停在第一屏高度）。
            if (bestD != 0)
            {
                for (int d = MinNewRows; d <= maxD; d++)
                {
                    if (d > bestD - 24 && d < bestD + 24) continue;
                    double bad;
                    double cost = Score(prev, cur, sw, bandTop, bandBot, band2Top, band2Bot, d, sameRow, out bad);
                    if (cost < second) second = cost;
                }
            }

            m.Score = best == double.MaxValue ? -1 : best;
            m.Second = second == double.MaxValue ? -1 : second;
            m.BadRatio = bestBad;
            if (bestD == 0)
            {
                m.Why = Lang.T("找不到重叠区（这一屏和上一屏对不上），再滚一下试试", "No overlap found (this screen does not match the previous one) - try scrolling again");
                return m;
            }
            // ① 主判据：差异像素比例。周期图案（表格线/列表项）对齐时线条能对上，
            //    但格子里的字对不上 —— 那一片片"不一样的像素"就是靠这个挡下来的，
            //    实测（用例 2：一次滚过头）平均差只有 4 点几，单看平均差会放它过去。
            if (bestBad > MaxBadRatio)
            {
                m.Why = Lang.T("这一屏对不上（滚过头了，或者画面里在动），慢一点再滚一下", "No match (scrolled too far, or something is moving) - scroll more slowly");
                return m;
            }
            if (best > MatchTol)
            {
                m.Why = Lang.T("这一屏没对上（画面变化太大或滚过头了），再滚一下试试", "No match (the content changed too much, or scrolled too far) - try again");
                return m;
            }
            // 置信度：次优不能和最优一样好 —— 否则说明"怎么对都对得上"（多半是纯色/重复内容），宁可让用户再滚
            // 0.6.0：原来这里还有一条"次优必须明显更差"的相对置信度判据，已删除 ——
            // 真实屏幕实测 best 与 second 天然只差 1（内容相似度本就是连续渐变的），
            // 那条判据只会一路拒。现在由上面的绝对判据（代价上限 + 差异像素比例）把关。
            m.NewRows = bestD;
            return m;
        }

        // 某一行在两帧之间的平均灰度差，但按**滚动偏移**对齐：cur[y] 对 prev[y-d]。
        // 空白行在"同位置"和"按偏移"两种假设下都成立（两边都白），
        // 而跟着滚动的实内容只在"按偏移"下成立 —— 这两条一比就能把空白和静止区分开。
        static double RowDiffOffset(byte[] a, byte[] b, int sw, int y, int d)
        {
            if (a == null || b == null) return 999;
            // ⚠️⚠️ 方向：**页面往下滚 d，内容往上走** —— 所以"上一帧的 y+d 行"才是"这一帧的 y 行"：
            //      cur[y] == prev[y + d]
            // 这里原来写的是 y-d（**符号反了**），于是一直在拿"错位的两行"比，
            // 正文行的 dScroll 永远是大的（实测 81.8），"跟得上滚动"那条判据**从来没真正生效过**，
            // 全靠后面那句"两不像就收手"兜着 —— 这也正是"一去掉那句就全线崩"的原因。
            // （匹配器里的 AddBand 用 y-d 是对的：它枚举的是"上一帧的行 y"，对应"这一帧的 y-d"。）
            int y2 = y + d;
            if (y2 >= a.Length / sw) return 999;
            long s = 0; int n = 0;
            for (int x = 0; x < sw; x += 3) { int v = a[y2 * sw + x] - b[y * sw + x]; s += v < 0 ? -v : v; n++; }
            return n == 0 ? 999 : (double)s / n;
        }

        // 某一行在两帧之间的平均灰度差（用于找屏幕底部的静止区）
        static double RowDiff(byte[] a, byte[] b, int sw, int y)
        {
            if (a == null || b == null) return 999;
            int o = y * sw; long s = 0; int n = 0;
            for (int x = 0; x < sw; x += 3) { int d = a[o + x] - b[o + x]; s += d < 0 ? -d : d; n++; }
            return n == 0 ? 999 : (double)s / n;
        }

        // 灰度采样：列方向 2px 取 1（跳过右侧 SkipRight），行方向全取（行是匹配方向，不能跳）
        // 采样比全分辨率快 2 倍，实测对匹配精度没有影响（同一图案的相邻列几乎一样）
        //
        // ⚠️ 这里刻意**不用 unsafe**：本项目的 csc 没开 /unsafe（照 56-Ocr.cs 的 ScalePixels 走
        //    Marshal.Copy 逐行搬），开了开关又要动 build.ps1，没必要。
        internal static byte[] Sample(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int sw = (w - SkipRight + 1) / 2;
            byte[] outp = new byte[sw * h];
            byte[] row = new byte[w * 4];
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy((IntPtr)((long)d.Scan0 + (long)y * d.Stride), row, 0, w * 4);
                    int o = y * sw;
                    for (int x = 0, i = 0; i < sw; x += 2, i++)
                    {
                        // 亮度近似：0.114B + 0.587G + 0.299R（整数版，避免浮点）
                        int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        outp[o + i] = (byte)((b * 29 + g * 150 + r * 77) >> 8);
                    }
                }
            }
            finally { bmp.UnlockBits(d); }
            return outp;
        }
    }
}

namespace SnapWheel
{
    // ==================== 滚动长截图（0.6.0）：交互层 ====================
    // 交互是"**他自己滚，程序跟着无缝拼接**"——不给目标窗口发合成滚轮消息，所以不挑窗口、
    // 也不怕惯性滚动和滚动节流。
    //
    // 0.6.0 修订：入口从托盘搬到**截图浮层的工具条**（用户要求），而且**只抓他在浮层里框出来的那块选区**：
    //   · 以前抓整个屏幕，长图里混着任务栏、侧边栏、别的窗口；现在你在哪个区域滚，就只拼那个区域。
    //   · 提示条贴在选区**上方**（放不下就改到下方），不会挡住你要看的内容。
    //
    // 这个窗口就是那条提示条：显示已接了几段、长图现在多高、失配时直接说原因；Enter 出图、Esc 取消。
    //
    // 三个关键点：
    //   ① 窗口设了 WDA_EXCLUDEFROMCAPTURE —— 抓屏拍不到它自己（否则每帧都带着这条提示、长图一路重复）。
    //   ② 每 200ms 抓一帧交给 LongShot.Push 去判：接得上就接，接不上就等下一帧。
    //      **不单独做"停稳检测"**：滚动中抓到的帧本来就匹配不上，判据交给拼接算法，逻辑只有一份。
    //   ③ 抓屏用一张复用的位图，不每 200ms 分配一次（选区大时那是十几 MB）。
    class LongShotForm : Form, IMessageFilter
    {
        public Bitmap Result;

        readonly Rectangle _region;           // 要拼的那块屏幕区域（就是浮层里的选区）
        readonly LongShot _ls = new LongShot();
        readonly Timer _t;
        readonly Bitmap _scratch;             // 复用的抓屏位图（选区尺寸）
        string _msg = Lang.T("滚到哪儿它接哪儿", "It stitches as it scrolls");
        bool _err = false;
        bool _busy = false;
        int _shots = 0;
        IntPtr _target = IntPtr.Zero;         // 选区下面那个窗口（滚轮消息发给它）
        byte[] _prevGray;                     // 上一帧的灰度采样（判断画面有没有在动）
        int _tick = 0, _stalls = 0;
        const int ScrollSteps = 1;            // 每次自动滚 1 格：步长小 → 重叠区大 → 匹配得上（3 格实测一次滚 300~500px，重叠太少）
        const double StillTol = 3.0;          // 判定画面没动的灰度差阈值

        public LongShotForm(Rectangle region)
        {
            if (region.Width < 16 || region.Height < 16)
                region = new Rectangle(region.Left, region.Top, Math.Max(16, region.Width), Math.Max(16, region.Height));

            // ── 把抓帧区域**夹进工作区**：一次把"任务栏那一条"排除掉 ──
            //
            // 为什么是这 3 行，而不是在拼接引擎里写一套"静止区自动检测"：
            //   用户报的症状是"长图里有任务栏、而且后面内容重复"。根因是他**框了整屏**，
            //   把任务栏也框了进去 —— 而任务栏不跟着页面滚，拼出来自然是重复的一条。
            //
            //   引擎里那套"猜哪几行没动"的检测，我试着修了好几轮：真机上有半透明任务栏、
            //   有亚像素滚动，逐行像素**在原理上就分不干净**（"跟不上滚动的正文"和
            //   "半透明任务栏"是同一个形态）。它已经花掉几轮、还没修干净。
            //
            //   而这 3 行是**确定性的**：不猜，直接排除。任务栏本来就不该出现在长图里 ——
            //   没有谁截长图是为了留住任务栏。
            //
            // 夹完之后如果太小（比如整块都在任务栏里），就不动它 —— 让下游照旧报"区域太小"。
            try
            {
                Rectangle wa = Screen.FromRectangle(region).WorkingArea;
                Rectangle clipped = Rectangle.Intersect(region, wa);
                if (clipped.Width >= 16 && clipped.Height >= 16) region = clipped;
            }
            catch { }

            _region = region;

            // 双缓冲：抓帧/状态刷新时提示条不再闪（用户反馈：进入滚动时 UI 在抖）
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(24, 26, 32);
            Font = new Font("Microsoft YaHei UI", 9.5f);

            int barH = Ui.S(86);
            int barW = Math.Max(Ui.S(420), _region.Width);
            Rectangle scr;
            try { scr = Screen.FromRectangle(_region).Bounds; }
            catch { scr = new Rectangle(_region.Left, _region.Top, barW, barH); }

            int by = _region.Top - barH - Ui.S(8);          // 默认贴在选区上方
            if (by < scr.Top) by = Math.Min(scr.Bottom - barH, _region.Bottom + Ui.S(8));   // 上方放不下就放下方
            int bx = Math.Max(scr.Left, Math.Min(_region.Left, scr.Right - barW));
            ClientSize = new Size(barW, barH);
            Location = new Point(bx, by);

            try { _scratch = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppPArgb); }
            catch { _scratch = null; }

            string err = null;
            Bitmap first = Grab();
            bool ok = (first != null) && _ls.Start(first, out err);
            if (!ok) { _msg = err ?? Lang.T("没能开始长截图", "Could not start the scrolling capture"); _err = true; }

            // Esc/Enter 用应用级消息过滤来收：提示条是无边框置顶窗口，焦点很容易被下面的
            // 目标程序抢走（用户反馈 Esc 按了没用，只能用鼠标点提示条退出）。
            Application.AddMessageFilter(this);

            // 选区正中心下面是谁？滚轮消息就发给他（自动滚动，不用用户自己滚）
            try { _target = Native.WindowFromPoint(new Native.POINT(_region.Left + _region.Width / 2, _region.Top + _region.Height / 2)); }
            catch { _target = IntPtr.Zero; }
            _prevGray = (first == null) ? null : LongShot.Sample(first);

            _t = new Timer();
            _t.Interval = 300;      // 300ms 抓一帧：200ms 太密，会把目标程序的滚动拖得不平滑（用户反馈）
            _t.Tick += new EventHandler(OnTick);
            if (ok) _t.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 抓屏不许拍到自己（不然长图上会一路重复这条提示条）
            try { Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); } catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();                 // 要能收到 Enter / Esc
            BringToFront();
        }

        // 抓一帧（写进复用位图里）
        Bitmap Grab()
        {
            if (_scratch == null) return null;
            try
            {
                using (Graphics g = Graphics.FromImage(_scratch))
                    g.CopyFromScreen(_region.Left, _region.Top, 0, 0,
                                     new Size(_scratch.Width, _scratch.Height), CopyPixelOperation.SourceCopy);
                return _scratch;
            }
            catch { return null; }
        }

        void OnTick(object o, EventArgs e)
        {
            if (_busy || _ls.Result == null) return;
            _busy = true;
            try
            {
                if (_ls.Full) { _msg = Lang.T("已经到最大高度了，按 Enter 出图", "Maximum height reached - press Enter to finish"); Invalidate(); return; }

                Bitmap frame = Grab();
                if (frame == null) { _msg = Lang.T("抓屏失败（可能被安全软件拦了）", "Screen capture failed (possibly blocked by security software)"); _err = true; Invalidate(); return; }

                byte[] g2 = LongShot.Sample(frame);
                _tick++;

                if (_tick == 1) { _prevGray = g2; ScrollNext(); _msg = Lang.T("开始自动滚动…", "Auto-scrolling…"); Invalidate(); return; }

                double diff = Diff(_prevGray, g2);
                _prevGray = g2;

                if (diff < StillTol)
                {
                    // 画面几乎没动：到底了，或者目标窗口不吃合成的滚轮消息
                    _stalls++;
                    if (_stalls >= 2) { _msg = Lang.T("到底了，正在出图", "Reached the end, generating the image"); Invalidate(); Finish(); return; }
                    _msg = Lang.T("画面没动（", "No movement (") + _stalls + Lang.T("/2），再试一次", "/2), trying again");
                    Invalidate();
                    ScrollNext();
                    return;
                }

                _stalls = 0;
                int added = 0;
                if (_ls.Push(frame, out added)) { _shots++; _err = false; _msg = Lang.T("已接 ", "Stitched ") + added + Lang.T(" 行", " rows"); }
                else { _err = true; _msg = (_ls.LastWhy == null ? Lang.T("这一屏没对上", "This screen does not line up") : _ls.LastWhy); }
                Invalidate();
                ScrollNext();
            }
            catch (Exception ex)
            {
                try { Err.Log("LongShot", ex); } catch { }
            }
            finally { _busy = false; }
        }

        // 给选区正中心下面那个窗口发一个合成的滚轮消息（往下滚几格）。
        // 这是**产品功能**（用户要求：滚动交给他自己太不可控，速度不重要、可用性优先），
        // 与验证时不许模拟真实输入那条纪律是两件事。
        void ScrollNext()
        {
            if (_target == IntPtr.Zero) return;
            try
            {
                int delta = -120 * ScrollSteps;                 // 负数 = 向下滚
                int wp = (delta << 16);
                int lx = _region.Left + _region.Width / 2;
                int ly = _region.Top + _region.Height / 2;
                int lp = (lx & 0xFFFF) | ((ly & 0xFFFF) << 16);
                Native.PostMessage(_target, Native.WM_MOUSEWHEEL, (IntPtr)wp, (IntPtr)lp);
            }
            catch { }
        }

        // 两帧灰度采样之间变化有多大（抽样算平均差）
        static double Diff(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 999;
            long s = 0; int n = 0;
            for (int i = 0; i < a.Length; i += 37) { int d = a[i] - b[i]; s += d < 0 ? -d : d; n++; }
            return n == 0 ? 999 : (double)s / n;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Finish();     // 在条上点一下也算结束
        }

        // 全局按键过滤：不管焦点在哪个窗口，Esc 取消、Enter 出图
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_KEYDOWN = 0x0100;
            if (m.Msg == WM_KEYDOWN)
            {
                int k = m.WParam.ToInt32();
                if (k == 27) { Cancel(); return true; }        // Esc
                if (k == 13) { Finish(); return true; }        // Enter
            }
            return false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { Finish(); return; }
            if (e.KeyCode == Keys.Escape) { Cancel(); return; }
            base.OnKeyDown(e);
        }

        void Finish()
        {
            try { _t.Stop(); } catch { }
            try { Result = _ls.Finish(); } catch { }
            DialogResult = Result == null ? DialogResult.Cancel : DialogResult.OK;
            Close();
        }

        void Cancel()
        {
            try { _t.Stop(); } catch { }
            Result = null;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int pad = Ui.S(14);
            int ih = Ui.S(44);
            Rectangle icon = new Rectangle(pad, (ClientSize.Height - ih) / 2, ih, ih);
            using (GraphicsPath p = Gfx.Round(icon, Ui.S(10)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 122, 204)))
                g.FillPath(b, p);
            using (Font f = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold))
                TextRenderer.DrawText(g, "↕", f, icon, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int x = icon.Right + Ui.S(12);
            int top = Ui.S(10);
            TextRenderer.DrawText(g, Lang.T("滚动长截图：自动滚动中，不用你操作", "Scrolling capture: auto-scrolling, no action needed"), Font, new Point(x, top),
                Color.FromArgb(236, 238, 244), TextFormatFlags.NoPadding);
            using (Font fs = new Font("Microsoft YaHei UI", 8.5f))
                TextRenderer.DrawText(g, Lang.T("到底会自动停 · Enter 提前出图 · Esc 取消", "Stops at the end · Enter finishes early · Esc cancels"), fs, new Point(x, top + Ui.S(20)),
                    Color.FromArgb(150, 154, 164), TextFormatFlags.NoPadding);

            string line;
            Color lc = Color.FromArgb(150, 154, 164);
            if (_err) { line = _msg; lc = Color.FromArgb(240, 190, 120); }
            else if (_shots == 0) line = Lang.T("还没接上：在那块区域里往下滚滚轮", "Not stitched yet: scroll down inside that area");
            else line = Lang.T("已接 ", "Stitched ") + _shots + Lang.T(" 段 · 长图 ", " sections · image ") + _ls.Height + Lang.T(" px 高", " px tall");

            int rw = Math.Max(Ui.S(200), ClientSize.Width - x - pad);   // 自适应：原来写死 300，长句子会被右边缘裁掉
            Rectangle rr = new Rectangle(ClientSize.Width - pad - rw, top, rw, Ui.S(36));
            TextRenderer.DrawText(g, line, Font, rr, lc,
                TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPadding);

            int pw = rw, ph = Ui.S(4);
            int py = rr.Bottom - Ui.S(2);
            Rectangle track = new Rectangle(rr.Right - pw, py, pw, ph);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 64, 74))) g.FillRectangle(b, track);
            double frac = (double)_ls.Height / LongShot.MaxCanvasH;
            if (frac < 0) frac = 0; if (frac > 1) frac = 1;
            Rectangle fill = new Rectangle(track.X, track.Y, Math.Max(1, (int)(track.Width * frac)), ph);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 140, 232))) g.FillRectangle(b, fill);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 定时器必须停 + 释放（之前项目里泄漏的 15ms 定时器把测试拖到 300 秒）
            try { Application.RemoveMessageFilter(this); } catch { }
            try { if (_t != null) { _t.Stop(); _t.Dispose(); } } catch { }
            try { if (_scratch != null) _scratch.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { if (_t != null) { _t.Stop(); _t.Dispose(); } } catch { } }
            base.Dispose(disposing);
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

        // 现在停在弧下端的是第几张（0 起）。外部只读 —— 传递模式靠它决定"搬哪一张"。
        // 注意用的是 _targetOffset（用户意图）而不是 _offset（动画当前值）：
        // 滚轮刚滚完那一下，动画还在追，但用户心里已经是新那张了。
        public int CurrentIndex

        {
            get
            {
                try
                {
                    int n = _store == null ? 0 : _store.Items.Count;
                    if (n <= 0) return -1;
                    int i = (int)Math.Round(_targetOffset);
                    if (i < 0) i = 0;
                    if (i > n - 1) i = n - 1;
                    return i;
                }
                catch { return -1; }
            }
        }

        /// <summary>
        /// 把轮盘滚到第 i 张（0 起）。给传递模式的"换一张"用。
        /// 改的是 _targetOffset（意图），画面会自己平滑滚过去 —— 和用户滚轮的效果一致。
        /// </summary>
        public void SelectIndex(int i)
        {
            try
            {
                int n = _store == null ? 0 : _store.Items.Count;
                if (n <= 0) return;
                if (i < 0) i = 0;
                if (i > n - 1) i = n - 1;
                _targetOffset = i;
                Invalidate();
            }
            catch { }
        }

        // 第 i 张缩略图在**屏幕**上的位置（返回矩形，X/Y 存的是中心点）。
        // 传递模式用它当「按下」的起点：传递模式是真的去动鼠标模拟拖放，
        // 起点必须落在那张缩略图真正所在的地方 —— 用窗口中心当起点，
        // 有些程序不会认（它们要求按下点确实在某张图上）。
        public Rectangle ItemScreenRect(int i)
        {
            try
            {
                RectangleF r = DrawnRect(i);
                if (r.Width < 2 || r.Height < 2) return Rectangle.Empty;
                Point tl = PointToScreen(new Point((int)Math.Round(r.X), (int)Math.Round(r.Y)));
                int w = (int)Math.Round(r.Width), h = (int)Math.Round(r.Height);
                return new Rectangle(tl.X + w / 2, tl.Y + h / 2, w, h);
            }
            catch { return Rectangle.Empty; }
        }
        // 删除后让上面的图滑下来用的过渡量：删除瞬间设成 -一个步距（抵消刚发生的那格下移），
        // 再由 AnimTick 每帧衰减回 0 —— 图从旧位置平滑滑到新位置，而不是瞬间跳过去。
        float _phiShift = 0f;
        int _delShiftFrom = -1;
        DateTime _topMostAt = DateTime.MinValue;
        // 置顶抑制计数：>0 时不做周期置顶（截图/设置/打赏/引导期间）。用计数而不是布尔，
        // 这样多处嵌套也不会互相把对方的状态冲掉。
        public static int SuppressTopMost = 0;

        // 传递模式期间抑制"空闲自动收起"。轮盘默认 8 秒不活动就隐藏，
        // 而传递模式恰恰要求用户自己切屏过去（动不动就超过 8 秒）——
        // 轮盘一藏，放下的"起点"就落在一片空桌面上，拖放从根上不会开始。
        // （用户实测"鼠标真的动了、但目标程序不认"，根因就是这个。）
        public static int SuppressAutoHide = 0;
        static int _suppressSeen = 0;      // 安全阀：抑制计数开始 > 0 的时刻
        static int _suppressHideSeen = 0;  // 自动收起抑制的安全阀（同上）
        // 三个小按钮的发光进度（0..1 平滑趋近）：悬停时光是淡进来的，不是啪一下亮（用户反馈）
        float _closeGlow = 0f, _gearGlow = 0f, _shootGlow = 0f;   // 上一次校验置顶的时间（见 AnimTick）        // 只给被删那张及其上方的图加补偿：下面的图本来就不该动
        // ---- 省电模式（见 12-Power.cs）----
        bool _powerSkipped;      // 上一帧是不是被"省电"跳过了（绝不连续跳两帧）
        bool _forceDraw;         // 这一帧必须画（输入导致的：悬停/按下/滚轮）
        public static int RenderCountForTest = 0;   // 测试用：真正画了多少帧
        // 测试用：把窗口的扩展样式读出来。分层 / 不激活 / 不占任务栏这几条**就是**"截图不在最顶层"
        // 那类 P0 的判据，而 CreateParams 是 protected，外面读不到 —— 所以开一个小口子。
        public int ExStyleForTest { get { return CreateParams.ExStyle; } }
        // 排查用：动画定时器真的跳了多少次。和渲染帧数放一起看，才能分清"帧率低"到底是
        // 「定时器没跳」还是「跳了但没有任何东西要求重画」—— 这两种的修法完全相反。
        public static int AnimTickCountForTest = 0;
        public static int SkipCountForTest = 0;     // 测试用：省电跳过多少帧
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
        // 空态提示「截图后会出现在这里」↔ 计数胶囊「3 / 8」的交叉淡入参数：
        //   1 = 完全显示空态提示，0 = 完全显示计数胶囊。
        // 为什么用一个参数管两头：这两个东西**永远不会同时出现**，是交替的。
        // 各管各的就会两边都突然消失/突然出现（用户反馈的原话），合成一个参数才可能真的"交叉"。
        float _emptyT = 1f;
        bool _emptySynced = false;     // 首帧直接对齐，别让程序刚启动就播一次没意义的过渡
        float _dragOutProg = 0f;       // 0..1 pull-out shrink progress
        // ---- 拖出去的反馈（v1.0）----
        // 为什么要有这套：拖出去的**默认是"留一份"**（KeepAfterDragOut=true，拖拽本身也是 Copy 语义）——
        // 图**没走**。而原来无论哪种模式，卡片都在拖的过程中一路缩小到看不见，
        // 画的正是"被抽走"，**在骗人**（用户会以为图没了，其实还在）。
        // 所以分两套画法（见 64-WheelForm.Input.cs 的 StartDragOut）：
        //   留一份：拖拽中「提起来」（不缩小）+ 松手后那一格**颤一下**、向外一道短促拖痕、短暂高亮；**格子不合拢**
        //   移走  ：拖拽中照旧「被抽走」+ 松手后空位**慢慢合拢**（复用删除动画的 _phiShift）
        float _dragLift = 0f;          // 0..1 拖拽中"提起来"的程度（留一份模式；移走模式一直是 0）
        float _dragPulseT = 1f;        // 0..1 松手后"颤一下 + 高亮"的进度（1 = 已经结束）
        int _dragPulseIdx = -1;        // 颤的是哪一格（移走模式没有格子可颤，为 -1）
        DateTime _dragPulseAt = DateTime.MinValue;
        float _dragTrailT = 1f;        // 0..1 那道向外拖痕的进度（1 = 已经结束）
        DateTime _dragTrailAt = DateTime.MinValue;
        PointF _dragTrailA, _dragTrailB;   // 拖痕的起止（逻辑坐标：格子中心 → 松手那一刻的指针）
        StoreItem _deletingItem = null;
        int _delIdx = -1;              // 删除发起时被删那张的下标（删除完成后让视口平滑跟进）
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

        bool _nameHover = false;       // 指针停在 Wheel 名药丸上（提示Lang.T("点一下改名", "Click to rename")）

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

        bool _collapsing = false;      // 当前这次动画是Lang.T("收起", "Collapse")方向

        bool _nubOutHover = false;     // 指针停在"拉出"把手上

        bool _nubInHover = false;      // 指针停在Lang.T("收起", "Collapse")把手上

        float _nubHov = 0f;            // 把手悬停进度 0..1

        float _nubAppearT = 1f;        // 把手"出现"进度 0..1（启动时不要突然冒出来）

        DateTime _nubAppearAt = DateTime.MinValue;

        float _nubHintT = 0f;                            // 把手"点我展开/收起"提示的淡入进度

        bool _adminTipShown = false;                     // 管理员Lang.T("拖不动", "Cannot drag")的说明每次运行只弹一次

        DateTime _firstRunHintUntil = DateTime.MinValue; // 首次运行自动亮提示的截止时刻

        DateTime _collapsedAt = DateTime.MinValue;       // 收起完成的时刻（之后一小段内不允许再展开）

        string _lastClipFp = "";                          // 上一张从剪贴板收进来的图（去重用）
        // 注：以前这里还有一个 _selfClipboardAt（"自己写完剪贴板 1.5 秒内不导入"的时间窗），
        // 已经换成 SelfClipboard 的按图指纹登记：时间窗会连用户在这段时间里真正复制的一张图一起吞掉，
        // 而且窗口一过就失效（截图浮层是模态的，消息什么时候被泵到并不确定）。


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
            // 视口初始位置 = "最新那张顶在弧上端"（0.5.3 的锚点，见 OffsetForNewest）。
            // 必须在 ApplyLayout 之后设（_slots / _phiMin / _phiMax 都是它算出来的），
            // 也必须在这里设：Store 在构造时就把保存目录里的图恢复了，开机第一眼要给最新那批；
            // 不设的话第一张新图会从"下端那一格"往上爬到上端（反方向的动画）。
            _offset = _targetOffset = OffsetForNewest();
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

            // 首次运行（展开状态）：让Lang.T("点我收起", "Click to collapse")把手提示自动亮一次
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


        // 界面缩放系数的最终裁决（纯计算，离线可测：tests\render-smoke.cs 拿小屏幕直接调它）。
        //   wanted   = 用户/DPI 想要的系数（自动档是 AutoUiK()，手动档是 UiScale/100）
        //   baseSpan = 轮盘逻辑尺寸（半径 + 缩略图 + 标签那一整套，见 ApplyLayout）
        // **手动档也要过这道夹子**：以前只有自动档夹，于是小屏幕上手动设 200%/250% 时
        // 窗口被屏幕顶住、内容却没缩，环和把手直接跑到窗口外。
        // 抽出来的理由和 ToolbarRect 一样 —— 这个 bug 只在"屏幕比自己这台小"时才出现，
        // 本机 1067 高的屏上永远看不到（CI 的 1024×768 上必现）。
        internal static float ResolveUiK(float wanted, int scrW, int scrH, float baseSpan)
        {
            if (baseSpan > 1f)
            {
                float fit = Math.Min(scrW, scrH) / baseSpan;
                if (wanted > fit) wanted = fit;
            }
            return Math.Max(0.6f, Math.Min(2.5f, wanted));
        }

        public void ApplyLayout()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            float baseSpan = Math.Max(120, Math.Min(700, _settings.Radius)) +
                             Math.Max(40, Math.Min(260, _settings.ThumbSize)) * 1.75f + 190f;

            // 自动 = 跟显示器缩放比例走（2K@125% -> 1.25，4K@150% -> 1.5），看起来大小才一致；
            // 但**不管自动还是手动**，都不会把轮盘撑得比屏幕还大。
            float k = (_settings.UiScale > 0) ? (_settings.UiScale / 100f) : AutoUiK();
            // 这一夹以前只在"自动"档生效（0.9.10 修）。后果：小屏幕 + 手动设了 200%/250% 时，
            // 窗口被下面的 cap 顶回屏幕尺寸，**内容却没跟着缩** —— 环和把手直接跑到窗口外面去。
            // 本机 1067 高的屏怎么试都碰不到；CI 的 1024×768 上必现（把手跑到窗口外）。
            // 用户选了"装不下就自动缩到装得下"：宁可轮盘小一点，也不要被裁掉。
            UiK = ResolveUiK(k, wa.Width, wa.Height, baseSpan);

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


        // 轮盘要不要对屏幕捕获隐身。
        //
        // 默认隐身（WDA_EXCLUDEFROMCAPTURE）：自己截图 / 抓玻璃底时不会把轮盘拍进去。
        // 但那个 API 在 Windows 10 2004+ 上对**所有**基于 Windows.Graphics.Capture 的捕获都生效，
        // **录屏也算** —— 于是用户录演示视频时轮盘根本不出现，而截图浮层（普通窗口、没设这个标记）正常。
        // 演示模式打开就撤掉它。
        // 代价：自己截图时轮盘会进图，所以演示模式下**同时停掉毛玻璃定时刷新**
        // （否则轮盘会把自己的影子糊进自己的玻璃里）。
        public void ApplyCaptureVisibility()
        {
            try
            {
                if (Handle == IntPtr.Zero) return;
                Native.SetWindowDisplayAffinity(Handle,
                    _settings.Recordable ? Native.WDA_NONE : Native.WDA_EXCLUDEFROMCAPTURE);
                _rendered = false;      // 让下一帧重画，别留旧缓存
            }
            catch (Exception ex) { Err.Log("CaptureVisibility", ex); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 让窗口对截屏隐身：这样玻璃底可以随时重抓（不会把轮盘自己拍进去），
            // 顺带好处是用户截图时轮盘不会出现在图里。
            // ⚠️ 但演示模式要能被录到 —— 详见 ApplyCaptureVisibility。
            ApplyCaptureVisibility();
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
            // 贴哪条边由设置决定（见 Native.AnchorRect 里的说明）：
            // 默认 auto = 任务栏自动隐藏时贴屏幕物理边，否则贴工作区边。
            Rectangle wa = Native.AnchorRect(_settings.EdgeAnchor);
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

        float ItemPhi(int i)
        {
            float p = _phiMin + (i - _offset) * StepRad();
            // 删除后的滑动补偿只加在被删那张及其上方的图上：要的是上面的图滑下来填补空位，
            // 下面的图原地不动（之前是全局补偿，等于整盘都在动）。
            if (_delShiftFrom >= 0 && i >= _delShiftFrom) p += _phiShift;
            return p;
        }

        // ============================ 视口锚点（0.5.3） ============================
        // `_offset` 的含义没变：**落在弧起点那一格（_phiMin，靠屏幕角落那端）上的图片下标**。
        //
        // 堆叠规则（用户要的"容器渐渐装满"）：
        //   · **没堆满（Count ≤ Slots）**：锚在弧**起点**（_offset = 0）→ 第 0 张贴住 _phiMin，
        //     往上一格一格摞。新图从弧上端进来、一路滑到"当前那摞的最上面一格"，所以前几张
        //     滑得远（像往容器里放），越摞越高，底下几格是被填满的、不留空。
        //     ⚠️ 这里以前写的是 `Count - Slots`：张数少于 Slots 时它**是负数**，整摞被顶到弧的**上端**、
        //     下面几格永远空着，新图只滑一小段就停（用户报的"滑下来但没滑到底"）。
        //   · **堆满（Count > Slots）**：_offset = Count - Slots → 最新那张顶在弧**上端**（_phiMax），
        //     老图依次往下排；再来新图时 _offset 整体 +1 = 新图从上端挤进来、老图一起被往下挤一格，
        //     最下面那张滑出可见弧（v0.5.3 前半段就是这条，不变）。
        float OffsetForNewest() { return Math.Max(0, _store.Items.Count - _slots); }
        // 滚动范围（0.6.0 修正）：`_offset` 是**弧下端那一格**的下标（见 ItemPhi），所以
        //   · 最小值 0        = 最旧那张落在弧起点（一路往回看）
        //   · 最大值 Count-Slots = 最新那张顶在弧上端（默认视图）
        // 原来两头的语义写反了（Min=Count-Slots、Max=Count-1），后果有两个，
        // 而且看起来毫不相干：① 计数胶囊「几 / 几」的序号一直超出总数、被夹成常数，
        // 滚动时数字不动；② 滚到"极限"时可见范围滑出弧外，最下面那张被切掉一半。
        float MinOffset() { return 0; }
        // 上限放到"最后一张贴住弧起点"：用户可以一路滚到最新那张（它自己滑出弧外不要紧），
        // 这样才叫自由滚动。注意这和"默认视图"是两件事 —— 默认视图仍然是 OffsetForNewest()
        // （最新那张顶在弧上端），那是"收进新图后视口跟到哪儿"，不是滚动的边界。
        float MaxOffset() { return Math.Max(0, _store.Items.Count - 1); }

        // 入场起点：新图一律**从弧的上端滑下来**（不是从"它自己格子上方一点点"开始）。
        //   · 堆满时它自己的格子就在弧上端 → 还是老样子（0.30 / 开启动画时 0.62），从弧外挤进来；
        //   · 没堆满时它的格子在弧中下部 → 这里把起点抬到 _phiMax 再往外 0.10，于是一路滑到底。
        // 上限 1.10 是防呆（弧本身只有 _phiMax-_phiMin ≈ 0.96 宽，再多就是无意义的空滑）。
        float EnterSlide(int i, float baseSlide)
        {
            float d = (_phiMax + 0.10f) - ItemPhi(i);
            if (d > 1.10f) d = 1.10f;
            return d > baseSlide ? d : baseSlide;
        }


        PointF ItemCenter(int i) { return ItemCenterAtPhi(ItemPhi(i)); }

        PointF ItemCenterAtPhi(float phi)
        {
            float r = EffR();
            PointF c = Center();
            return new PointF((float)(c.X + r * Math.Cos(phi) * Sx()), (float)(c.Y + r * Math.Sin(phi) * Sy()));
        }


        Dictionary<StoreItem, DateTime> _enterT0 = new Dictionary<StoreItem, DateTime>();
        // "刚进来的那一格"的微光时刻（v1.0）。**和 _enterT0 必须分开**：
        // _enterT0 是"滑入动画从什么时候开始"，切轮盘时会把新轮盘的每张图都排一遍（依次滑入），
        // 拿它当"新图"用的话，**每次切轮盘满环都会一起发亮** —— 微光立刻变得毫无意义。
        // 只有真的加进来一张（截图 / 导入 / 剪贴板 / 拖进来）才记这一份。
        Dictionary<StoreItem, DateTime> _freshT0 = new Dictionary<StoreItem, DateTime>();
        // 切轮盘时名字药丸"翻一下"的进度（1 = 已经结束）。光靠环上一圈闪光太轻，
        // 而"我现在在哪个轮盘上"是**持续性**信息，值得让名字本身动一下（见 61c 的绘制）。
        float _nameSwapT = 1f;
        DateTime _nameSwapAt = DateTime.MinValue;


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
            // 文字格 / 文件格：一张横着的纸片。宽度跟图片格对齐（环的节奏才不乱），矮一点 ——
            // 竖着的卡片上面放几行字看起来像被压扁的图，不像一张纸（1.3.0）。
            if (it != null && it.Image == null)
                return new SizeF((float)Math.Round(_thumb), Math.Max(24f, (float)Math.Round(_thumb * 0.66f)));
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
                // 只删环自己的文件；引用来的原文件一律不动（见 Store.DropFile）
                _store.DropFile(it);
                // 第一次删图时说清楚"还能找回来" —— 否则没人知道托里有这个后悔药
                if (_settings != null && !_settings.UndoHintDone)
                {
                    _settings.UndoHintDone = true;
                    try { _settings.Save(); } catch { }
                    ShowToast(Lang.T("已删掉这张 —— 托盘右键「撤销上一次删除」可以找回来", "Deleted - use \"Undo last delete\" in the tray menu to bring it back"));
                }
            }
            _hover = -1; _enlarged = -1; _peekIndex = -1;
            if (_targetOffset > MaxOffset()) _targetOffset = MaxOffset();
            if (_targetOffset < MinOffset()) _targetOffset = MinOffset();
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
                    // 同上：引用来的原文件不动
                    _store.DropFile(all[i]);
                }
                _store.Items.Clear();
                _thumbCache.Clear();
                _enterT0.Clear();
                _scales.Clear();
                _offset = 0f; _targetOffset = 0f; _hover = -1; _enlarged = -1; _peekIndex = -1;
                _deletingItem = null; _deleteProg = 0f;
                _rendered = false;
                Render();
                ShowToast(n > 0 ? (Lang.T("已清空这一盘：", "Cleared this wheel: ") + n + Lang.T(" 张（托盘 → 撤销上一次删除 可以找回来）", " item(s) (tray -> Undo last delete to restore)")) : Lang.T("这一盘本来就是空的", "This wheel was already empty"));
            }
            catch (Exception ex) { Err.Log("ClearCurrentWheel", ex); }
        }


        // 设置窗口点了确定之后，界面上要做的收尾。
        // 抽成方法是为了能写行为测试 —— 以前这里曾混进一句 HideWheel()（收起态关掉时），
        // 结果每次点确定，轮盘都当场消失，而当时 150 项绘制测试一个都发现不了。
        public void AfterSettingsApplied()
        {
            ApplyTopMost();
            ApplyCaptureVisibility();   // 演示模式开关改完立刻生效（不然要重启才管用）
            ApplyLayout();
            // 收起态被关掉时，别让轮盘卡在"只剩个把手"的状态里
            if (!_settings.CollapseMode && _collapsed) ExpandWheel();
            RefreshWheel();            // 强制重绘：万能键上的动作名等设置改完要立刻生效
        }


        public void RefreshWheel()
        {
            // 回到"最新那张顶在弧上端"这个默认视口（0.5.3 起这就是默认位；以前是 0 = 最老那张在下端）
            _offset = _targetOffset = OffsetForNewest();
            _hover = -1; _enlarged = -1; _peekIndex = -1;
            _scales.Clear(); _enterT0.Clear(); _freshT0.Clear();
            _switchFlash = 1f;
            _nameSwapT = 0f; _nameSwapAt = DateTime.Now;      // 名字药丸翻一下
            Render();
        }


        void AfterWheelSwitch()
        {
            _offset = _targetOffset = OffsetForNewest();
            _hover = -1; _enlarged = -1; _peekIndex = -1;
            _scales.Clear(); _enterT0.Clear(); _freshT0.Clear();
            // 新 wheel 的图依次滑入，形成切换过渡
            for (int i = 0; i < _store.Items.Count; i++)
                _enterT0[_store.Items[i]] = DateTime.Now.AddSeconds(i * 0.045);
            _switchFlash = 1f;
            _nameSwapT = 0f; _nameSwapAt = DateTime.Now;      // 名字药丸翻一下（"我在哪个轮盘上"要看得见）
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
                using (RenameForm rf = new RenameForm(old, w.Takes))
                {
                    TopMost = false;                     // 轮盘别盖在弹框上面
                    rf.TopMost = true;
                    DialogResult r = rf.ShowDialog();
                    TopMost = _settings.AlwaysOnTop;
                    if (r != DialogResult.OK) { Render(); return; }
                    string nv = rf.Value;
                    string msg = "";
                    if (!string.IsNullOrEmpty(nv) && nv != old)
                    {
                        w.Name = nv;
                        msg = Lang.T("已改名为「", "Renamed to \"") + nv + "」";
                    }
                    // 收什么也在这个窗口里改（takes）：改了两样就合成一句话说，免得后一句把前一句顶掉
                    if (rf.Takes != w.Takes)
                    {
                        w.Takes = rf.Takes;
                        msg += (msg.Length > 0 ? " · " : "") + Lang.T("这个环现在收：", "This ring now takes: ") + w.TakesName();
                    }
                    if (msg.Length > 0) { _mgr.Save(); ShowToast(msg); }
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
                case "new": return Lang.T("新建", "New");
                case "next": return Lang.T("下一个", "Next");
                case "prev": return Lang.T("上一个", "Previous");
                case "delete": return Lang.T("删除", "Delete");
                case "shot": return Lang.T("截图", "Screenshot");
                case "collapse": return Lang.T("收起", "Collapse");
                case "folder": return Lang.T("文件夹", "Folder");
                case "settings": return Lang.T("设置", "Settings");
                case "paste": return Lang.T("收一张", "Collect one");
                case "clear": return Lang.T("清空", "Clear");
                default: return "";
            }
        }


        bool _dropActive = false;

        // 拖放态的**过渡进度**（0..1）。`_dropActive` 是个 bool，画的时候直接用它就是硬切 ——
        // 用户反馈的"绿提示出现消失、还有环随之变绿复原，全都没有过渡"就是这个。
        // 环上的绿光晕和那条提示都乘它，于是两边**同时**淡入淡出（本来就该是一件事）。
        float _dropVis = 0f;
        DateTime _dropVisAt = DateTime.MinValue;
        // 记住"这次拖放是不是**外部文件**"：淡出期间 `_dropExternal` 可能已经被清掉了，
        // 不记住的话 —— 要么提示在淡出中途突然消失，要么**自己的图拖到环上时也冒出绿提示**
        // （`_dropActive` 对我们自己的拖拽同样是真的，只是不该显示那条"加入图片"的提示）。
        bool _dropExternalShown = false;

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

        // 提示条的显示时长：**按长度给**（用户反馈"这句提示很长"，长提示两秒半看不完）。
        // 短提示仍然是 2.6 秒，长的最多给到 5.6 秒。
        float _toastDur = 2.6f;

        public void ShowToast(string s)
        {
            _toast = s == null ? "" : s;
            _toastAt = DateTime.Now;
            _toastDur = 2.6f + Math.Min(3.0f, Math.Max(0, _toast.Length - 18) * 0.045f);
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
    // 轮盘绘制：渲染管线与缓存（分层贴图、缩略图缓存、帧状态）（从 61-WheelForm.Draw.cs 拆出来，纯搬移，行为不变）。
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

        // animating：这张卡片当前的缩放**不是静止值**（放大预览 / 悬停 / 删除 / 拖动 / 收起动画中）。
        // 为什么要传这个：下面两处"救急"逻辑原来都挂在「目标尺寸是否超过原图」上，
        // 而那个条件**漏掉了最常见的一种情况** —— 大图放大后仍然小于原图宽
        // （2560x1440 的图放大到 346x194 并没有超过原图）。于是对最容易卡的大图，
        // 两条救急全部不生效。见下面中转图那段。
        Bitmap ScaledThumb(StoreItem it, int w, int h, bool animating)
        {
            // w/h 是逻辑尺寸；实际按物理像素生成，缩放到高 DPI 屏上才不会发虚。
            // 注意：尺寸**不能量化**。量化会让"1:1 贴图"变成重采样贴图，实测反而更慢
            // （缩略图那一段 2.98 → 3.72ms），所以这里保持精确尺寸。
            int dw = Math.Max(1, (int)Math.Round(w * UiK));
            int dh = Math.Max(1, (int)Math.Round(h * UiK));
            Dictionary<long, Bitmap> d;
            if (!_thumbCache.TryGetValue(it, out d)) { d = new Dictionary<long, Bitmap>(); _thumbCache[it] = d; }
            // 尺寸量化只在**尺寸正在动**的时候做：
            //   · 静止时（animating=false）必须保持精确尺寸 —— 1:1 贴图靠它，量化会把它变成重采样；
            //   · 动画中每帧尺寸都不同，不量化就帧帧未命中、缓存很快被撑满清空，
            //     又退回到每帧从原图做高质量双三次（用户反馈：大图的放大动画缓慢且有抖动）。
            if (animating || dw > it.Image.Width || dh > it.Image.Height)
            {
                dw = Math.Max(1, (dw + 7) / 8 * 8);
                dh = Math.Max(1, (dh + 7) / 8 * 8);
            }
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

            if (d.Count > 200)
            {
                // 只清小的、留住面积最大的那张：清空的话下一帧又得从原图重做一次高质量缩放，
                // 大图上那一下就是几十毫秒（放大动画卡顿的主要来源）。
                long keepKey = 0, keepArea = 0;
                foreach (System.Collections.Generic.KeyValuePair<long, Bitmap> kv in d)
                {
                    long a2 = (long)kv.Value.Width * kv.Value.Height;
                    if (a2 > keepArea) { keepArea = a2; keepKey = kv.Key; }
                }
                System.Collections.Generic.List<long> dead = new System.Collections.Generic.List<long>();
                foreach (System.Collections.Generic.KeyValuePair<long, Bitmap> kv in d)
                    if (kv.Key != keepKey) dead.Add(kv.Key);
                for (int q = 0; q < dead.Count; q++) { try { d[dead[q]].Dispose(); } catch { } d.Remove(dead[q]); }
                src = it.Image;
                if (d.Count > 0) foreach (System.Collections.Generic.KeyValuePair<long, Bitmap> kv in d) { src = kv.Value; break; }
            }
            // 放大的时候（目标比原图还大）如果没找到"够大"的现成图，就退而用缓存里**最大的那张**当源。
            // 从已有的小图放大，比从几千像素的原图缩下来快一个数量级 —— 而画质几乎看不出差别
            // （都是小尺寸重采样）。"大图的缩略图动画第一次播会卡"就是这个原因：
            // 首次没有大尺寸缓存，于是每一帧都从原图重做一次高质量双三次。
            if (src == it.Image && (dw > it.Image.Width || dh > it.Image.Height))
            {
                long bigA = 0;
                foreach (KeyValuePair<long, Bitmap> kv in d)
                {
                    long a3 = (long)kv.Value.Width * kv.Value.Height;
                    if (a3 > bigA) { bigA = a3; src = kv.Value; }
                }
            }
            // 上面那条「退而用缓存里最大的那张」为什么救不了大图：
            // 它只在**目标比原图还大**时才生效，而 2560x1440 的图放大到 346x194 并没有超过原图宽。
            // 而且就算强行用它（拿 144x81 的小缩略图放大 2.4 倍），画质会明显发虚 ——
            // 那是拿"不卡"换"更糊"，不划算。
            //
            // 所以这里补的是那一段真正缺的东西：**动画的头几帧，先从原图做一张"最终倍率"的中转图**
            // （稳定 key，整段动画只生成一次），后面的帧全部从它往下缩 —— 又快又清楚。
            // 大图上"第一次长按放大掉帧"就是这么来的：首次没有大尺寸缓存，于是每一帧
            // 都从几千像素的原图重做一次高质量双三次。
            if (animating && src == it.Image)
            {
                SizeF bs = CardSize(it);
                int mw = (int)Math.Round(bs.Width * PeekScale * UiK);
                int mh = (int)Math.Round(bs.Height * PeekScale * UiK);
                if (mw < dw) mw = dw;
                if (mh < dh) mh = dh;
                mw = Math.Max(8, (mw + 7) / 8 * 8);
                mh = Math.Max(8, (mh + 7) / 8 * 8);
                long mkey = ((long)mw << 20) | (uint)mh;
                Bitmap mid;
                if (d.TryGetValue(mkey, out mid))
                {
                    if (mid.Width >= dw && mid.Height >= dh) src = mid;
                }
                else
                {
                    mid = new Bitmap(mw, mh, PixelFormat.Format32bppPArgb);
                    using (Graphics gm = Graphics.FromImage(mid))
                    {
                        gm.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        gm.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        gm.DrawImage(it.Image, new Rectangle(0, 0, mw, mh));
                    }
                    d[mkey] = mid;
                    src = mid;
                }
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

        // public：托盘的「诊断模式」开关要立刻重画一帧，否则要等下一次动画 tick 才看到效果
        public void Render()
        {
            if (!IsHandleCreated || !Visible) return;
            RenderCountForTest++;               // 测试用（省电验收要数「到底画了几帧」）
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

    }
}

namespace SnapWheel
{
    // 轮盘绘制：万能键圆盘、四方向摇杆、小按钮发光（从 61-WheelForm.Draw.cs 拆出来，纯搬移，行为不变）。
    partial class WheelForm
    {
        // 万能键：新拟态玻璃圆盘 —— 玻璃底 + 上亮下暗 + 主题色核心，按下时核心点亮并轻微放大
        // 小按钮悬停时的外发光：和万能键同款（PathGradientBrush 中心亮、外围透明）。
        // 抽成方法而不是内联三份：内联会和各自作用域里的局部变量重名（编译期才发现，很烦）。
        void DrawBtnGlow(Graphics g, Rectangle r, float hot)
        {
            if (hot <= 0.01f || !StyleNeu() || _settings.ShadowPercent <= 8) return;
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(r.Left - 11f, r.Top - 11f, r.Width + 22f, r.Height + 22f);
                using (PathGradientBrush halo = new PathGradientBrush(gp))
                {
                    Color acc = _accentCur;   // 光色跟随当前 wheel 的主题色（切 wheel 会变）
                    halo.CenterPoint = new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
                    halo.CenterColor = Gfx.A(acc, (int)(128 * hot));
                    halo.SurroundColors = new Color[] { Gfx.A(acc, 0) };
                    g.FillPath(halo, gp);
                }
            }
        }

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

        // ---- 贴边小把手：收起态画"拉出"、展开态画Lang.T("收起", "Collapse") ----
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
            Diag(outMode ? "把手（展开/收起）" : "把手（另一个）", r);
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
            //
            // 这里**乘** vis（和把手本体同一个曲线），不要写成 `vis > 0.98f ? _nubHintT : 0f`：
            // 那种写法是硬切 —— 把手本身在平滑地淡入淡出，旁边那行提示却在跨过 0.98 的那一帧
            // 突然出现/突然消失。用户反馈的"展开和收起的时候提示没有过渡"就是它。
            float hintA = _nubHintT * vis * vis;
            if (hintA > 0.02f)
            {
                string ht = willExpand ? Lang.T("点我展开", "Click to expand") : Lang.T("点我收起", "Click to collapse");
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

    }
}

namespace SnapWheel
{
    // 轮盘绘制：三个小按钮与计数胶囊（从 61-WheelForm.Draw.cs 拆出来，纯搬移，行为不变）。
    partial class WheelForm
    {
        // ---- 控件层：关闭键 / 设置键 / 万能键 / 名字药丸 / 提示条 / 把手 ----
        void DrawControls(Graphics g, int a)
        {
            // 按下反馈：缩小一点 + 描边更亮，让"按下去"看得见
            PointF c = Center();
            Rectangle cbr = Shrink(CloseButtonRect(), _closeDown);
            Diag("关闭键（短按收起 / 长按 0.65s 退出）", cbr);
            DrawBtnGlow(g, cbr, _closeGlow);
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
            Diag("设置键", gbr);
            DrawBtnGlow(g, gbr, _gearGlow);
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
            if (kr.Width > 8 && kr.Height > 8) Diag("万能键（圆盘）", kr);
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
                        SizeF s1 = g.MeasureString(Lang.T("取消", "Cancel"), fh);
                        g.DrawString(Lang.T("取消", "Cancel"), fh, tb, kcx - krr / 2f - s1.Width / 2f, kcy - s1.Height / 2f);
                        SizeF s2b = g.MeasureString(Lang.T("确认", "Confirm"), fh);
                        g.DrawString(Lang.T("确认", "Confirm"), fh, tb, kcx + krr / 2f - s2b.Width / 2f, kcy - s2b.Height / 2f);
                    }
                }
                using (Font f3 = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush b3 = new SolidBrush(Color.FromArgb((int)(235 * a / 255f), 255, 210, 210)))
                {
                    string t3 = Lang.T("删除「", "Delete \"") + FitName(_mgr.ActiveWheel.Name, 12) + Lang.T("」？点左半取消 / 右半确认", "\"? Left half cancels / right half confirms");
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
                    // 0.6.0 修正：这一块**恢复成原来的样子**（位置、形状都没动）。
                    // 我上一版把它挪到弧上端外侧、还弯成了弧形 —— 用户明确说"项目名字不用变位置"。
                    // 要"随弧弯"的是「几 / 几」那个计数胶囊（见 DrawCountPill），不是这个。
                    SizeF ws = g.MeasureString(wn, fw);
                    float dot = 9f;
                    float pw2 = ws.Width + dot + 30f, ph2 = ws.Height + 8f;
                    // 切轮盘时"翻一下"（v1.0）：药丸弹一下 + 边框用主题色亮一下。
                    // 为什么是名字而不是别处：**"我现在在哪个轮盘上"是持续性信息** ——
                    // 原来只有环上闪一下，太轻，一眨眼就过去了，切完还得再确认一次。
                    float pop = _nameSwapT < 1f ? (float)Math.Sin(_nameSwapT * Math.PI) : 0f;   // 0→1→0
                    float wx = kcx - pw2 / 2f;
                    float wy = kr.Y + kr.Height + 4f;
                    RectangleF pill2 = new RectangleF(kcx - pw2 / 2f, wy, pw2, ph2);   // **基础**矩形（不含缩放）
                    _namePillRect = pill2;                       // 记下来给命中测试用（点它能改名）
                    // 整块内容绕**药丸中心**一起缩放 —— 药丸、圆点、文字是同一份几何，缩一个变换就够了。
                    // ⚠️ 上一版是"只把矩形宽高改大"，字还是原字号、只是被重新居中：
                    //    看起来就是"框在动、字不动"，用户一眼就看出来了。
                    //    用变换而不是各自算尺寸，是因为后者迟早会有一处忘掉（这个项目在
                    //    "量的时候用一套、画的时候用另一套"上摔过四次）。
                    float popSc = 1f + 0.09f * pop;
                    float liftY = -2.5f * pop;                   // 顺便抬高一点，弹跳感更像"翻了一下"
                    GraphicsState stPill = g.Save();
                    try
                    {
                        float pcx = pill2.X + pill2.Width / 2f, pcy = pill2.Y + pill2.Height / 2f;
                        g.TranslateTransform(pcx, pcy + liftY);
                        g.ScaleTransform(popSc, popSc);
                        g.TranslateTransform(-pcx, -pcy);
                        Diag("名字药丸（当前轮盘名）",
                             new RectangleF(pcx - pill2.Width * popSc / 2f, pcy + liftY - pill2.Height * popSc / 2f,
                                            pill2.Width * popSc, pill2.Height * popSc));
                        using (GraphicsPath pg2 = Gfx.Round(pill2, ph2 / 2f))
                    {
                        BackdropClip(g, pg2, an);
                        Gfx.GlassPanel(g, pg2, pill2, Gfx.A(GlassBase(), GlassA((int)((_nameHover ? 210 : 176) * an / 255f))),
                            (int)((StyleNeu() ? 40 : 18) * an / 255f), (int)((StyleNeu() ? 34 : 0) * an / 255f), !StyleFlatOnly());
                        int bpA = (int)((_nameHover ? 235 : 120) * an / 255f);
                        Color bpC = Gfx.Shade(acc, 0.15f);
                        if (pop > 0.001f) { bpA = Math.Min(255, bpA + (int)(150 * pop)); bpC = acc; }
                        using (Pen bp2 = new Pen(Gfx.A(bpC, bpA), (_nameHover ? 1.6f : 1.1f) + 1.4f * pop))
                            g.DrawPath(bp2, pg2);
                    }
                    float dy2 = pill2.Y + ph2 / 2f;
                    using (SolidBrush db2 = new SolidBrush(Color.FromArgb((int)(250 * an / 255f), acc.R, acc.G, acc.B)))
                        g.FillEllipse(db2, pill2.X + 11f, dy2 - dot / 2f, dot, dot);
                        using (SolidBrush bw = new SolidBrush(Color.FromArgb((int)(245 * an / 255f), 255, 255, 255)))
                            g.DrawString(wn, fw, bw, pill2.X + 13f + dot, pill2.Y + (ph2 - ws.Height) / 2f + 1);
                    }
                    finally { g.Restore(stPill); }
                    // 悬停时在右边补一句Lang.T("点一下改名", "Click to rename")
                    if (_nameHover && an > 80)
                    {
                        using (Font ft = new Font("Microsoft YaHei UI", 9f))
                        using (SolidBrush bt = new SolidBrush(Color.FromArgb((int)(220 * an / 255f), 235, 238, 245)))
                            g.DrawString(Lang.T("点一下改名", "Click to rename"), ft, bt, pill2.Right + 8f,
                                         pill2.Y + pill2.Height / 2f - ft.Height / 2f + 1);   // 用基础矩形算
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
                string[] labels = { Lang.T("新建", "New"), Lang.T("下一个", "Next"), Lang.T("删除", "Delete"), Lang.T("上一个", "Previous") };
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
            Diag("截图键", sbr);
            DrawBtnGlow(g, sbr, _shootGlow);
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

            // ⚠️ 「拖放提示」和「长按关闭键提示」原来画在这里（DrawControls 中间）——
            // 已挪到 DrawWheel 最上面那一组，见那边的图层说明。
            // 挪走的直接原因：拖放提示就画在这两行下面一点，而**计数胶囊画在整层之后**，
            // 于是「松手把 3 张图加入「项目1」」被「几 / 几」盖住了字（用户实机复现）。
            // 顺带补上了 Diag 登记 —— 它以前**连名字都没有**，任何几何检查都看不见它，
            // 这是我第一轮改错地方（去改提示条）却毫无察觉的原因。

            // ⚠️ 提示条**不在这里画**（v1.0 挪走）。它是"回应你刚做的一个动作"的瞬时反馈，
            // 必须压在所有**常驻**元素之上 —— 而这一层里的按钮/药丸/把手都是常驻的。
            // 原来它排在这一层中间，于是画在它后面的计数胶囊永远盖住它
            // （用户实机复现：「松手把 3 张图加入「项目1」」被「几 / 几」挡住了字）。
            // 现在统一在 DrawWheel 的最上面按"瞬时反馈 > 常驻元素"的顺序画，见那边的图层说明。

            DrawNubs(g, a);      // 展开状态下也画一个Lang.T("收起", "Collapse")把手（贴着另一条屏幕边）
        }

        // 计数胶囊「当前 / 总数」：跟着滚动位置变，所以每帧单独画（不进缓存层）
        void DrawCountPill(Graphics g, int a)
        {
            Color acc = _accentCur;
            if (!_settings.ShowCountLabel) return;
            // 没图就没有「几 / 几」可显示 —— 别在淡出期间画出「1 / 0」这种数字
            if (_store.Items.Count == 0) return;
            // 和空态提示共用 _emptyT 做交叉淡入：有图时它淡入（同一时刻提示正在淡出）。
            // 再乘收起进度 —— 环和卡片缩回去的时候，胶囊也得跟着退，不能等 _collapsed 置位那一刻跳掉。
            // （展开那一路本来就有 IntroP，见下面的 pc2。）
            float vis = (1f - _emptyT) * CollapseCardP();
            if (vis <= 0.02f) return;
            a = (int)(a * vis);
                // 0.5.3：视口锚点改成"最新那张顶在弧上端"之后，`_offset` 是**弧下端那一格**的下标，
                // 所以可见区里最靠上（最新）的那张 = _offset + Slots。这么写，默认视口下就是 N/N
                // （最新那张在最上面），往上滚会依次变小 —— 和以前"跟着滚动位置变"的语义一致。
                int cur = (int)Math.Round(_targetOffset) + 1;   // 序号 = 弧下端那一张（1 起算）：滚轮滚到哪张就显示哪张，范围 1..总数
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
                    // 0.6.0 弧线设计语言：计数胶囊也是**沿弧弯出来的弧带**、文字逐字沿弧转。
                    // 角度放在弧下端之外（phi 更小），与弧上端的名称胶囊左右对称，
                    // 中间那段"万能键左上方"留给三个小按钮。
                    PointF cc2 = Center();
                    float sx3 = Sx(), sy3 = Sy();
                    float ip = sz.Height * 0.72f;                    // 前置的小圆点
                    float h3 = sz.Height + 12f;
                    float r3 = EffR() + 78f;                       // 回到 45° 对角线外侧那一档（原来的位置）
                    float mid3 = (_phiMin + _phiMax) / 2f;         // 45° 对角线方向
                    // 弧长按内容算：圆点 + 间隔 + 文字 + 两端留白 —— 这样数字绝不会被胶囊边缘切到
                    float needLen = ip + sz.Width * 1.04f + 30f;   // 圆点(直径+端头留白) + 间隙 + 文字 + 尾端留白
                    float half3 = needLen / 2f / r3;
                    using (GraphicsPath pg = ArcUi.Capsule(cc2, sx3, sy3, r3, h3, mid3 - half3, mid3 + half3))
                    {
                        RectangleF bnd3 = pg.GetBounds();
                        Diag("计数胶囊「几 / 几」", bnd3);
                        BackdropClip(g, pg, ac2);
                        Gfx.GlassPanel(g, pg, bnd3, Gfx.A(GlassBase(), GlassA((int)(176 * ac2 / 255f))),
                            (int)((StyleNeu() ? 38 : 16) * ac2 / 255f), (int)((StyleNeu() ? 32 : 0) * ac2 / 255f), !StyleFlatOnly());
                        using (Pen pp2 = new Pen(Gfx.A(Gfx.Shade(acc, 0.15f), (int)(110 * ac2 / 255f)), 1.1f))
                            g.DrawPath(pp2, pg);
                    }
                    // 内容按水平直线摆（弧长很短时和弧的差别可忽略），左右严格留白 ——
                    // 用户反馈的计数胶囊数字显示问题就是这里排版太挤导致数字被切。
                    float dotAng = (ip + 12f) / r3;                    // 圆点直径 + 端头留白
                    PointF dp3 = ArcUi.Polar(cc2, sx3, sy3, mid3 + half3 - dotAng / 2f, r3);   // 圆点在弧的前一端（这个角下 phi 大的一侧才是屏幕左上方）
                    // （圆点与文字都按弧坐标摆，不再用屏幕直线坐标）
                    using (SolidBrush db = new SolidBrush(Color.FromArgb((int)(245 * ac2 / 255f), acc.R, acc.G, acc.B)))
                        g.FillEllipse(db, dp3.X - ip / 2f, dp3.Y - ip / 2f, ip, ip);
                    using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(246 * ac2 / 255f), 255, 255, 255)))
                    // 数字也沿弧排（用户要求跟胶囊同一条弧）：弧长按内容算足了，整串都在胶囊里
                    {
                    float step3 = (sz.Width * 1.04f / Math.Max(1, idx.Length)) / r3;   // 每字一个角：按实测字宽，紧凑
                    float textMid = mid3 + half3 - dotAng - (ip / 2f + 6f + sz.Width * 1.04f / 2f) / r3;   // 文字排在圆点之后，中间留 6px 缝（用户反馈：字和圆点会重叠）
                        ArcUi.ArcText(g, idx, f, br, cc2, sx3, sy3, r3, textMid, step3, 0.45f);
                    }
                    }
                g.TranslateTransform(-sc2.X, -sc2.Y);
                }
        }

        // ============================ 状态区（瞬时消息都放这儿）============================
        //
        // 位置：**轮盘靠着的那只角的对角**。轮盘只吃一只角，对角那一大片永远是空的；
        // 而轮盘自己那只角同时挤着万能键、名字药丸、三个圆按钮和最下面那张卡片 ——
        // 实测在那儿「往上让」只是撞到别的东西（试了三个方向），根本没空位。
        //
        // 谁用这块地方：操作回执（Toast）、拖放提示、长按关闭键提示。
        // **同一时刻只显示最紧急的一条**（见 DrawWheel 里的 statusTaken）——
        // 三条不同时出现是这里的前提，所以它们可以共用一格而不会互相压住。
        internal RectangleF StatusArea(SizeF textSz, float padX, float padY)
        {
            SizeF ls = LogicalSize();
            float w = textSz.Width + padX * 2f, h = textSz.Height + padY * 2f;
            // **夹在屏幕里**：原来直接按文字宽度算，一条宽过屏幕的提示（"原件没动 ——
            // 环里留了一份副本（另一个程序正在使用此文件…）"）算出来的 x 是负数 ——
            // 用户实测"左边一部分都出屏幕外了"。宽度和高度都得留出 26px 的边距。
            float maxW = ls.Width - 52f, maxH = ls.Height - 52f;
            if (maxW > 80f && w > maxW) w = maxW;
            if (maxH > 60f && h > maxH) h = maxH;
            float x = Sx() > 0 ? (ls.Width - w - 26f) : 26f;
            float y = Sy() > 0 ? (ls.Height - h - 26f) : 26f;
            return new RectangleF(x, y, w, h);
        }

        // 外部文件拖到轮盘上方：提示"松手加入"。
        //
        // 位置改过两次才对，两次都记下来：
        //   · 第一版贴在"环的对角线"上 → 撞了计数胶囊 + 两张缩略图（就是用户截图那张）。
        //   · 第二版甩到"对角的状态区" → 顺序和重叠都对了，但**用户说离轮盘太远、甚至没注意到**。
        //     反馈的价值在于"贴着你正在操作的东西"，甩到画面另一头等于没有反馈。
        //   · 现在：回到轮盘身边，钉在**上把手（弧在侧边那一端）的外侧**，并**避开缩略图** ——
        //     卡片都往内缩了一个安全角 phiMin，把手外侧那块天生是空的。
        // ⚠️ 这个元素以前**连 Diag 名字都没有** —— 于是任何几何检查都看不见它，
        //   我一度改错地方却毫无察觉。加元素必须登记，这是硬规矩。
        void DrawDropHint(Graphics g, int a)
        {
            // **跟着 _dropVis 淡入淡出**：用户反馈"出现消失没有任何过渡"。
            // 退出时 _dropActive 已经是 false 了，所以这里不能再按 bool 判 —— 要按进度判，
            // 否则淡出根本没机会发生（一松手它就没了）。
            float dv = _dropVis;
            if (dv <= 0.02f) return;
            // 只有**外部文件**拖放才有这条提示（我们自己的图拖到环上是蓝色那一档，不显示"加入图片"）
            if (!_dropExternalShown) return;
            a = (int)(a * dv);
            if (a <= 2) return;
            string tip = Lang.T("松手把 ", "Release to add ") + _dropCount + Lang.T(" 张图片加入「", " item(s) to \"") + FitName(_mgr.ActiveWheel.Name, 12) + "」";
            using (Font f = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(tip, f);
                float w = sz.Width + 28f, h = sz.Height + 14f;
                SizeF ls = LogicalSize();
                RectangleF nub = NubOutRect();          // 弧在侧边那一端的把手 = 用户说的"上把手"
                // 往**远离角落**的那一侧让出去：下贴轮盘就往上，上贴轮盘就往下
                float y = (Sy() < 0) ? (nub.Y - h - 10f) : (nub.Bottom + 10f);
                float x = (Sx() > 0) ? 6f : (ls.Width - w - 6f);
                // 淡入的那一下再往下"浮"上来 12px（和别处一样：位置也参与过渡，不只是透明度）
                y += (Sy() < 0 ? 1f : -1f) * (1f - dv) * 12f;
                if (y < 6f) y = 6f;
                if (y + h > ls.Height - 6f) y = ls.Height - h - 6f;
                if (x + w > ls.Width - 6f) x = ls.Width - w - 6f;
                if (x < 6f) x = 6f;
                RectangleF pill = new RectangleF(x, y, w, h);
                Diag("拖放提示（松手加入）", pill);
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

        // 长按关闭键的提示（"按住不放 · 移开可取消"）。
        // 原来贴在关闭键上方 —— 那里和名字药丸叠着，只是"画得晚所以盖住了"而已，
        // 属于"顺序凑合"，不是"位置合适"。现在统一进状态区。
        // 关闭键本身已经有一圈红色进度环，"按住了"这件事在按钮上看得见，文字不必贴着它。
        void DrawCloseHoldHint(Graphics g, int a)
        {
            if (_closeHoldP <= 0.10f) return;
            int ab8 = (int)(a * IntroP(0.30f));
            if (ab8 < 8) ab8 = 8;
            int ta = (int)(Math.Min(1f, (_closeHoldP - 0.10f) / 0.25f) * 240 * ab8 / 255f);
            if (ta <= 2) return;
            string tip2 = _closeLong ? Lang.T("松手退出 · 移开取消", "Release to exit · move away to cancel")
                                     : Lang.T("按住不放 · 移开可取消", "Hold · move away to cancel");
            using (Font ft2 = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
            using (SolidBrush tb2 = new SolidBrush(Color.FromArgb(ta, 255, 255, 255)))
            {
                SizeF ts2 = g.MeasureString(tip2, ft2);
                RectangleF pr2 = StatusArea(ts2, 8f, 4f);
                Diag("长按关闭键提示", pr2);
                using (GraphicsPath clPath = Gfx.Round(pr2, pr2.Height / 2f))
                {
                    BackdropClip(g, clPath, ta);
                    using (SolidBrush clBg = new SolidBrush(Color.FromArgb((int)(ta * 0.62f), 22, 24, 30))) g.FillPath(clBg, clPath);
                    using (Pen clPen = new Pen(Color.FromArgb((int)(ta * 0.55f), 236, 74, 62), 1.4f)) g.DrawPath(clPen, clPath);
                }
                g.DrawString(tip2, ft2, tb2, pr2.X + 8f, pr2.Y + 4f);
            }
        }

    }
}

namespace SnapWheel
{
    // 轮盘的"气氛"层（v1.0）：让已有的东西活起来的那几件事。
    //
    // 它们有一个共同点：**都不新增任何功能**，只是在已有的东西上补节奏和反馈。
    // 单独放一个文件，是因为它们的性质一样 —— 都是"跟着时间走的标量 + 一处绘制"，
    // 而 61-WheelForm.Draw.cs 已经够长了。
    //
    // 里面所有的"随时间变化"都遵守同一条纪律：**有始有终**。
    // 空转满帧那两次事故（45fps）都是"永远差一点点"的形状，
    // 所以这里每个标量都必须能**真正走到终点**，并且终点上有吸附。
    partial class WheelForm
    {
        // ==================== ① 环的厚度：内容越多越粗 ====================
        // 空环最细，堆满最粗。只到 1.55 倍就封顶 —— 再粗就变成"一个游泳圈"压在缩略图底下了。
        // 纯函数（取数量），所以测试能直接喂数字验，不用去摆真实状态。
        internal static float RingThickOf(int count, int slots)
        {
            int sl = slots < 2 ? 2 : slots;
            float f = (float)count / sl;
            if (f > 1f) f = 1f;
            if (f < 0f) f = 0f;
            return 1f + 0.55f * f;
        }
        float RingThick() { return RingThickOf(_store == null ? 0 : _store.Items.Count, _slots); }

        // ==================== ② 时间感：早上偏暖、深夜自己暗下去 ====================
        // 纯函数（参数是"几点"），所以测试能把 24 个小时全跑一遍，而不用去改系统时间。
        //   DayDim  ：深夜整块暗下去多少（加在整体不透明度上）
        //   DayWarm ：早上偏暖的程度（0 = 不偏，1 = 最暖）
        // 取值刻意保守 —— 用户的第一反应不该是"轮盘怎么变淡了"，而该是"晚上看着舒服"。
        internal static float DayDimOf(int hour)
        {
            if (hour >= 9 && hour < 21) return 0f;                 // 白天完全不动
            if (hour >= 21) return (hour - 21) / 5f * 0.15f;        // 21 → 02 慢慢暗到 0.15
            if (hour >= 6) return (9 - hour) / 3f * 0.09f;          // 06 → 09 从 0.09 回到 0
            return 0.15f;                                           // 深夜最暗
        }
        internal static float DayWarmOf(int hour)
        {
            if (hour < 6 || hour >= 20) return 0f;
            if (hour <= 10) return (hour - 6) / 4f;                 // 06 → 10 暖起来
            if (hour <= 16) return 1f;                              // 10 → 16 最暖
            return 1f - (hour - 16) / 4f;                           // 16 → 20 暖意退掉
        }
        float DayDim() { return _settings != null && !_settings.DayMood ? 0f : DayDimOf(DateTime.Now.Hour); }
        float DayWarm() { return _settings != null && !_settings.DayMood ? 0f : DayWarmOf(DateTime.Now.Hour); }

        // 把主题色往暖里偏一点（早上）。用插值而不是替换，白天/晚上都还是原来的主题色。
        Color DayTint(Color c)
        {
            float w = DayWarm();
            if (w <= 0.01f) return c;
            int r = c.R + (int)((255 - c.R) * 0.22f * w);
            int g = c.G + (int)((196 - c.G) * 0.10f * w);
            int b = c.B - (int)(c.B * 0.16f * w);
            if (r > 255) r = 255; if (g > 255) g = 255; if (b < 0) b = 0;
            return Color.FromArgb(c.A, r, g, b);
        }

        // ==================== ③ 新来的那一格有微光 ====================
        // 亮 2.2 秒，再用 2.6 秒冷下去 —— 抬眼就知道"哪张是刚截的"，不用去数。
        internal const double FreshHoldSec = 2.2, FreshCoolSec = 2.6;
        internal static float FreshGlowAt(double ageSec)
        {
            if (ageSec < 0) return 0f;
            if (ageSec <= FreshHoldSec) return 1f;
            if (ageSec >= FreshHoldSec + FreshCoolSec) return 0f;
            float t = (float)((ageSec - FreshHoldSec) / FreshCoolSec);
            return 1f - t * t * (3f - 2f * t);        // smoothstep 冷下去
        }
        float FreshGlow(int i)
        {
            if (i < 0 || i >= _store.Items.Count) return 0f;
            DateTime t0;
            if (!_freshT0.TryGetValue(_store.Items[i], out t0)) return 0f;
            return FreshGlowAt((DateTime.Now - t0).TotalSeconds);
        }
        // 还有没有"正在冷却的微光"（有的话这一帧必须画，而且这一层不能用缓存）
        bool FreshActive()
        {
            foreach (System.Collections.Generic.KeyValuePair<StoreItem, DateTime> kv in _freshT0)
                if ((DateTime.Now - kv.Value).TotalSeconds < FreshHoldSec + FreshCoolSec + 0.05) return true;
            return false;
        }
        // 微光总强度（进层签名用）：只把"还有没有微光"区别出来就够，不必精确到每一格
        double FreshGlowSum()
        {
            double s = 0;
            foreach (System.Collections.Generic.KeyValuePair<StoreItem, DateTime> kv in _freshT0)
                s += FreshGlowAt((DateTime.Now - kv.Value).TotalSeconds);
            return s;
        }

        // ==================== ④ 涟漪 ====================
        // 加进来一张新图时，从那一格扩散开一圈淡淡的光 —— "东西进来了"这件事有了形状。
        // 做成独立标量（不是每格一份），所以同时来一堆图时也只有一圈，不会糊成一团。
        float _rippleT = 1f;
        DateTime _rippleAt = DateTime.MinValue;
        PointF _rippleAt2 = PointF.Empty;
        float _rippleRad = 80f;

        internal void StartRipple(PointF center)
        {
            if (_settings != null && !_settings.Ripple) return;
            _rippleAt2 = center;
            _rippleT = 0f;
            _rippleAt = DateTime.Now;
            float ls = Math.Max(LogicalSize().Width, LogicalSize().Height);
            _rippleRad = ls * 0.42f;
        }

        // 由 AnimTick 推动；returns true 表示"这一帧还得画"
        bool RippleTick()
        {
            if (_rippleT >= 1f) return false;
            _rippleT += (float)((DateTime.Now - _rippleAt).TotalSeconds / 0.62f);
            if (_rippleT >= 1f || DateTime.Now < _rippleAt) _rippleT = 1f;
            _rippleAt = DateTime.Now;
            return true;
        }

        void DrawRipple(Graphics g, float a)
        {
            if (_rippleT >= 1f || a <= 2f) return;
            float t = _rippleT;
            float e = 1f - (1f - t) * (1f - t);                 // ease-out：一开始快，后面慢下来
            float r = _rippleRad * e;
            if (r < 3f) return;
            int alpha = (int)(70 * (1f - t) * (1f - t) * a / 255f);
            if (alpha < 3) return;
            Color ac = DayTint(_accentCur);
            using (Pen p = new Pen(Color.FromArgb(alpha, ac.R, ac.G, ac.B), 3.6f * (1f - t * 0.6f)))
                g.DrawEllipse(p, _rippleAt2.X - r, _rippleAt2.Y - r, r * 2f, r * 2f);
            // 里面再一圈更淡的，看起来才像水波而不是一个圆圈
            int alpha2 = (int)(38 * (1f - t) * (1f - t) * a / 255f);
            if (alpha2 >= 3)
                using (Pen p = new Pen(Color.FromArgb(alpha2, ac.R, ac.G, ac.B), 2.2f))
                {
                    float r2 = r * 0.72f;
                    g.DrawEllipse(p, _rippleAt2.X - r2, _rippleAt2.Y - r2, r2 * 2f, r2 * 2f);
                }
        }

        // ==================== ⑤ 环的影子 ====================
        // 让环"浮"在桌面上而不是画在上面。和缩略图的阴影同一套路：柔和的漫射阴影。
        void DrawRingShadow(Graphics g, GraphicsPath track, float rr, float a)
        {
            if (_settings != null && !_settings.RingShadow) return;
            if (StyleFlatOnly() || _settings.ShadowPercent <= 8) return;
            int sa = (int)(ShadowA(70) * 0.85f);
            if (sa < 4 || a <= 2f) return;
            // GraphicsState 在 .NET 4.0 里**没实现 IDisposable**（只有 Save/Restore），
            // 所以这里不能写 using —— 必须 try/finally 保证还原。
            GraphicsState st = g.Save();
            try
            {
                g.TranslateTransform(2.5f, 4.5f);              // 光从左上来，影子往右下走
                using (Pen sp = new Pen(Color.FromArgb((int)(sa * a / 255f), 0, 0, 0), 16f + 6f * (RingThick() - 1f) / 0.55f))
                { sp.StartCap = LineCap.Round; sp.EndCap = LineCap.Round; g.DrawPath(sp, track); }
            }
            finally { g.Restore(st); }
        }
    }
}

namespace SnapWheel
{
    // 非图片格怎么画：文字格 / 文件格都是"纸"。
    // 这是 §九「材质跟着内容走」的第一行 —— 截图=玻璃、文字=纸；
    // 颜色=磨砂、盯屏=暗色雷达 那两行要等 1.6 / 1.7 有那种格子了才存在。
    partial class WheelForm
    {
        // 纸的颜色**不跟着主题走**：暗色桌面上贴一张纸是这套设计里少数"实物感"的来源，
        // 反过来（深色纸）在浅色主题上会像一块补丁。文字格 / 文件格都是纸，靠顶部那条色带区分。
        internal static readonly Color PaperBg = Color.FromArgb(252, 250, 244);
        internal static readonly Color PaperInk = Color.FromArgb(44, 42, 40);
        internal static readonly Color PaperSub = Color.FromArgb(146, 144, 140);

        // 文件格按种类上色。用户实测反馈（2026-09-27）："不同文件类型辨识度还是不高，可以做出颜色的差别" ——
        // 一张 PDF 和一张压缩包原来都是同一张灰白纸，扫一眼分不出来。
        // 分组刻意粗（文档 / 表格 / 演示 / 压缩包 / 音视频 / 程序）：分得太细反而记不住，要的是"一眼分得出"。
        // 认不出来的后缀就给 PaperSub（不瞎猜），这样"不知道怎么归类"和"就是一坨文件"是同一种样子。
        internal static readonly Color KindDoc = Color.FromArgb(54, 116, 208);
        internal static readonly Color KindSheet = Color.FromArgb(36, 140, 92);
        internal static readonly Color KindSlide = Color.FromArgb(226, 120, 50);
        internal static readonly Color KindPack = Color.FromArgb(198, 150, 40);
        internal static readonly Color KindMedia = Color.FromArgb(132, 90, 202);
        internal static readonly Color KindProg = Color.FromArgb(64, 72, 86);   // 比纸深一档的冷灰 —— (96,104,116) 那档染 12% 之后跟"认不出来"的纸色几乎一样（2026-09-27 对着出图改的）

        internal static Color FileKindColor(string nameOrPath)
        {
            string ext = "";
            try { ext = Path.GetExtension(nameOrPath == null ? "" : nameOrPath).ToLowerInvariant(); } catch { }
            switch (ext)
            {
                case ".pdf": case ".doc": case ".docx": case ".rtf": case ".odt": case ".wps":
                case ".txt": case ".md": case ".log": case ".ini": case ".json": case ".xml":
                    return KindDoc;
                case ".xls": case ".xlsx": case ".csv": case ".ods":
                    return KindSheet;
                case ".ppt": case ".pptx": case ".odp":
                    return KindSlide;
                case ".zip": case ".rar": case ".7z": case ".tar": case ".gz": case ".bz2": case ".xz":
                    return KindPack;
                case ".mp3": case ".wav": case ".flac": case ".m4a": case ".ape":
                case ".mp4": case ".mkv": case ".avi": case ".mov": case ".wmv": case ".flv":
                    return KindMedia;
                case ".exe": case ".msi": case ".bat": case ".cmd": case ".ps1": case ".dll": case ".sys":
                    return KindProg;
                default:
                    return PaperSub;
            }
        }

        // 把颜色 c 按 k 混进 baseC（k=0 原样、k=1 全是 c）。只用来给纸底染一点点种类色。
        internal static Color Tint(Color baseC, Color c, float k)
        {
            if (k < 0f) k = 0f; if (k > 1f) k = 1f;
            return Color.FromArgb(baseC.A,
                (int)Math.Round(baseC.R + (c.R - baseC.R) * k),
                (int)Math.Round(baseC.G + (c.G - baseC.G) * k),
                (int)Math.Round(baseC.B + (c.B - baseC.B) * k));
        }

        // 把一段文字按宽度切成若干行。中文没有空格，只能逐字量宽度。
        // 独立成 static：测试可以直接调，不需要窗口、不需要渲染。
        // 返回值最多 maxLines 行；内容没画完时把最后一行收成省略号（"框里还有"这件事必须看得见）。
        internal static List<string> CellLines(Graphics g, Font f, string text, float maxW, int maxLines)
        {
            List<string> lines = new List<string>();
            if (g == null || f == null || string.IsNullOrEmpty(text) || maxLines <= 0 || maxW <= 1f) return lines;
            bool cut = false;
            string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            StringFormat sf = StringFormat.GenericTypographic;
            for (int li = 0; li < raw.Length && lines.Count < maxLines; li++)
            {
                string s = raw[li];
                if (s.Length == 0) { lines.Add(""); continue; }
                int start = 0;
                while (start < s.Length && lines.Count < maxLines)
                {
                    int n = 1;
                    try
                    {
                        // 一个字一个字地加，加到再加一个就超宽为止（超长的那个字自己占一行，不留空）
                        while (start + n < s.Length &&
                               g.MeasureString(s.Substring(start, n + 1), f, new PointF(0, 0), sf).Width <= maxW) n++;
                    }
                    catch { n = s.Length - start; }
                    lines.Add(s.Substring(start, n));
                    start += n;
                    if (lines.Count >= maxLines && (start < s.Length || li < raw.Length - 1)) cut = true;
                }
            }
            if (cut && lines.Count > 0)
            {
                // 收省略号时要把尾部削掉几个字：直接往满行后面加"…"会顶出框外
                //（这一条是测试钉出来的 —— 加了省略号之后那一行的宽度必须还在 maxW 以内）
                string last = lines[lines.Count - 1].TrimEnd();
                while (last.Length > 1)
                {
                    string cand = last + "…";
                    float w;
                    try { w = g.MeasureString(cand, f, new PointF(0, 0), sf).Width; } catch { break; }
                    if (w <= maxW) { last = cand; break; }
                    last = last.Substring(0, last.Length - 1);
                }
                lines[lines.Count - 1] = last;
            }
            return lines;
        }

        // 画一格文字 / 文件。rr = 卡片矩形（悬浮 / 动画的缩放已经在里面），rad = 圆角，ia = 整格透明度。
        // 调用方负责别把图片格送进来（那一条走 2c3-图片）。
        void DrawPaperCell(Graphics g, StoreItem it, RectangleF rr, float rad, int ia)
        {
            if (g == null || it == null) return;
            bool isText = (it.Kind == CellKind.Text);
            // 种类色：文字格=主题色，文件格=看后缀（见 FileKindColor）。纸底也染 12% 这个色 ——
            // 只靠顶上那 3px 在缩略图上太细，整张纸带一点色才是一眼分得出。
            Color kind = isText ? AccentColor()
                                : FileKindColor(it.Name != null && it.Name.Length > 0 ? it.Name : it.FilePath);
            Color bgCol = isText ? PaperBg : Tint(PaperBg, kind, 0.12f);
            float pad = 9f;
            RectangleF inner = new RectangleF(rr.X + pad, rr.Y + pad,
                                              Math.Max(4f, rr.Width - pad * 2f), Math.Max(4f, rr.Height - pad * 2f));
            try
            {
                using (GraphicsPath card = Gfx.Round(rr, rad))
                {
                    using (SolidBrush bg = new SolidBrush(Gfx.A(bgCol, ia))) g.FillPath(bg, card);
                    g.SetClip(card);

                    // 顶部那一小条：文字格用主题色、文件格用它的种类色。两种都是纸，靠它一眼分开
                    using (SolidBrush band = new SolidBrush(Gfx.A(kind, ia)))
                        g.FillRectangle(band, inner.X, inner.Y, Math.Max(10f, rr.Width * 0.3f), 3f);

                    if (!isText)
                    {
                        // 文件格：右上角先写后缀。格子小的时候名字会被截断，后缀是最后一个能救回来的信息
                        string ext = "";
                        try { ext = Path.GetExtension(it.Name != null && it.Name.Length > 0 ? it.Name : (it.FilePath ?? "")); } catch { }
                        if (ext.Length > 0)
                        using (Font fe = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold))
                        {
                            float eh = fe.GetHeight(g);
                            StringFormat rf = new StringFormat(StringFormat.GenericTypographic);
                            rf.Alignment = StringAlignment.Far;
                            using (SolidBrush sb = new SolidBrush(Gfx.A(kind, ia)))
                                g.DrawString(ext.ToLowerInvariant(), fe, sb,
                                             new RectangleF(inner.X, inner.Y - 3f, inner.Width, eh + 1f), rf);
                            inner.Y += eh; inner.Height -= eh;
                        }
                    }
                    inner.Y += 5f; inner.Height -= 5f;

                    string body = isText ? it.Text
                                         : (string.IsNullOrEmpty(it.Name) ? (it.FilePath ?? "") : it.Name);
                    using (Font f = new Font("Microsoft YaHei UI", 9f, isText ? FontStyle.Regular : FontStyle.Bold))
                    {
                        // 行高用 Font.GetHeight：量的高度 ≠ 画需要的高度（见 16-DrawKit.cs 里那条教训）
                        float lh = f.GetHeight(g);
                        int maxLines = Math.Max(1, (int)Math.Floor(inner.Height / Math.Max(1f, lh)));
                        List<string> lines = CellLines(g, f, body, inner.Width, maxLines);
                        using (SolidBrush ink = new SolidBrush(Gfx.A(PaperInk, ia)))
                            for (int i = 0; i < lines.Count; i++)
                                g.DrawString(lines[i], f, ink,
                                             new RectangleF(inner.X, inner.Y + i * lh - 1f, inner.Width + 2f, lh + 2f),
                                             StringFormat.GenericTypographic);
                    }
                    g.ResetClip();
                }
            }
            catch (Exception ex) { Err.Log("DrawPaperCell", ex); }
        }
    }
}

namespace SnapWheel
{
    partial class WheelForm
    {











        string _lastDragInfo = "";      // 上一次拖出去的结果（只给日志看：格式 / 目标有没有接收）








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





        void DrawWheel(Graphics g, int w, int h)
        {
            int a = (int)(255 * Math.Max(0f, Math.Min(1f, _show)));
            if (a <= 1) return;
            // 时间感：深夜整块自己暗下去一点（最多 15%）。放在最前面 —— 它是"这一帧整体多亮"，
            // 后面所有层的 alpha 都从它出发，不用每处各乘一次。
            float dayDim = DayDim();
            if (dayDim > 0.001f) { a = (int)(a * (1f - dayDim)); if (a <= 1) return; }
            DiagClear();     // 诊断模式：这一帧的元素清单从空开始
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

            // 空态提示的可见度 = 空态参数 × **展开进度** × **收起进度**。
            //
            // 后面两个因子是这次补上的：环和卡片都是跟着 _introT 缓缓长出来 / 缩回去的
            // （卡片用 EnterProgress，计数胶囊用 IntroP(0.72f)），而这条提示当初**两个都没乘**，
            // 只乘了 _show —— 可 StartIntro() 里 `_show = 1f` 是**立刻赋值**的，
            // 于是展开时整块场景在缓缓成形、中间这行字第一帧就满血出现；收起时它又整段不动、
            // 等 _collapsed 置位那一刻消失。用户反馈"很生硬"就是它。
            float hintVis = _emptyT * IntroP(0.55f) * CollapseCardP();
            if (hintVis > 0.02f)
            {
                using (Font f0 = new Font("Microsoft YaHei UI", 10f))
                using (SolidBrush b0 = new SolidBrush(Color.FromArgb((int)(200 * a / 255f * hintVis), 255, 255, 255)))
                {
                    string hint = Lang.T("截图后会出现在这里", "No screenshots yet");
                    SizeF hs = g.MeasureString(hint, f0);
                    PointF hp = HintPos(hs);
                    Diag("空态提示「截图后会出现在这里」", new RectangleF(hp.X, hp.Y, hs.Width, hs.Height));
                    g.DrawString(hint, f0, b0, hp.X, hp.Y);
                }
            }

            // 拖出去那道"向外"的短促拖痕画在卡片**下面**（先画痕迹、再画卡片，看起来才是从格子里出去的）
            DrawDragTrail(g, a);

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
                    // 入场一律从**弧的上端**滑下来（EnterSlide 见 60-WheelForm.cs）：
                    // 堆满时就是老样子 0.30 / 开启动画 0.62；没堆满时一路从 _phiMax 滑到自己的格子
                    float slide = EnterSlide(i, (_intro || _collapsing) ? 0.62f : 0.30f);
                    float phi = ItemPhi(i) + (1f - pr) * slide;      // slide along the arc
                    if (phi < _phiMin - 0.50f || phi > _phiMax + 0.50f) continue;
                    PointF pc = ItemCenterAtPhi(phi);
                    int ia = (int)(a * pr);

                    // 拖出去的两套反馈（见 60-WheelForm.cs 里 _dragLift 的说明）：
                    //   留一份：拖拽中「提起来」（放大一点点），松手后那一格**颤一下 + 短暂高亮**
                    //   移走  ：拖拽中一路缩小到看不见（下面那行 _dragOutProg）
                    // 合起来的意义是——**画出来的和实际发生的必须一致**，不能骗人。
                    float shake = 0f, pulse = 0f;
                    if (i == _dragPulseIdx && _dragPulseT < 1f)
                    {
                        float t = _dragPulseT;
                        pulse = 1f - t;                                     // 高亮：从满到无
                        shake = (float)Math.Sin(t * Math.PI * 5.0) * 3.2f * (1f - t);   // 颤：来回几下就停
                    }

                    float sc; if (!_scales.TryGetValue(i, out sc)) sc = 1f;
                    if (_store.Items[i] == _dragOutItem)
                    {
                        sc *= Math.Max(0f, 1f - _dragOutProg);
                        sc *= 1f + 0.06f * _dragLift;
                    }
                    if (_store.Items[i] == _deletingItem) { sc *= Math.Max(0f, 1f - _deleteProg); ia = (int)(ia * (1f - _deleteProg)); }
                    if (isEnl) sc *= 1f;                              // peek is a separate overlay
                    SizeF baseSz = CardSize(_store.Items[i]);
                    // 这张卡片现在是不是"尺寸正在动"（放大预览 / 悬停 / 删除 / 拖动 / 收起）。
                    // 静止时（sc==1）走精确尺寸 + 1:1 贴图那条快路；动画中才用 ScaledThumb 里
                    // 那套"量化 + 先做中转图"的救急措施。见 61a 里 ScaledThumb 的说明。
                    bool animating = Math.Abs(sc - 1f) > 0.005f || shake != 0f;
                    int iw = Math.Max(4, (int)Math.Round(baseSz.Width * sc));
                    int ih = Math.Max(4, (int)Math.Round(baseSz.Height * sc));
                    if (iw < 4 || ih < 4) continue;
                    RectangleF ir = new RectangleF((float)Math.Round(pc.X - iw / 2f) + shake, (float)Math.Round(pc.Y - ih / 2f), iw, ih);
                    RectangleF rr2 = new RectangleF(ir.X - CardPad, ir.Y - CardPad, iw + 2 * CardPad, ih + 2 * CardPad);
                    Diag("缩略图 #" + i + (isEnl ? "（长按放大中）" : ""), rr2);
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
                        // 毛玻璃底（真背景）+ 新拟态的上下明暗边。
                        // 只有图片格有这一层：纸格自己就是底，先模糊一遍背景再拿纸盖掉纯属白花时间。
                        if (_store.Items[i].Image != null)
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
                        else
                            using (Perf.Section("2c2b-纸格")) DrawPaperCell(g, _store.Items[i], rr2, rad, ia);
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
                                Bitmap thb = ScaledThumb(_store.Items[i], tw, th, animating);
                                RectangleF fr = new RectangleF(
                                    (float)Math.Round(pc.X - tw / 2f), (float)Math.Round(pc.Y - th / 2f), tw, th);
                                DrawWithAlpha(g, thb, fr, ia);
                            }
                            else
                            {
                                Bitmap th = ScaledThumb(_store.Items[i], iw, ih, animating);
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
                        // 拖出去之后那一格"颤一下 + 短暂高亮"里的高亮：
                        // 一圈往外扩、同时淡掉的主题色边框。抖动在 ir 那边（shake），这里只管亮。
                        // 画在贴片缓存**外面** —— 它每帧都在变，进缓存等于每帧都在生成新贴片。
                        if (pulse > 0.01f)
                        using (Perf.Section("2c5-拖出反馈"))
                        {
                            float e = 1f - pulse;                       // 0 -> 1
                            float grow = 7f * e;
                            int pa2 = (int)(215 * pulse * ia / 255f);
                            Color ac2 = AccentColor();
                            RectangleF pr2 = new RectangleF(rr2.X - grow, rr2.Y - grow, rr2.Width + grow * 2, rr2.Height + grow * 2);
                            using (GraphicsPath pp = Gfx.Round(pr2, rad + grow * 0.5f))
                            using (Pen pen = new Pen(Color.FromArgb(pa2, ac2.R, ac2.G, ac2.B), 3.2f * (1f - e * 0.55f)))
                                g.DrawPath(pen, pp);
                        }
                        // 新来的那一格有微光（v1.0）：亮 2.2 秒、再用 2.6 秒冷下去。
                        // 目的在于"抬眼就知道哪张是刚截的"——不用去数、不用去看计数胶囊。
                        float fresh = FreshGlow(i);
                        if (fresh > 0.01f && pulse <= 0.01f)
                        using (Perf.Section("2c6-新图微光"))
                        {
                            Color fac = DayTint(_accentCur);
                            int fa = (int)(150 * fresh * ia / 255f);
                            using (GraphicsPath fp2 = Gfx.Round(rr2, rad))
                            using (Pen pen = new Pen(Color.FromArgb(fa, fac.R, fac.G, fac.B), 2.6f))
                                g.DrawPath(pen, fp2);
                            int fb2 = (int)(54 * fresh * ia / 255f);
                            using (GraphicsPath fp3 = Gfx.Round(new RectangleF(rr2.X - 4f, rr2.Y - 4f, rr2.Width + 8f, rr2.Height + 8f), rad + 4f))
                            using (Pen pen = new Pen(Color.FromArgb(fb2, fac.R, fac.G, fac.B), 5f))
                                g.DrawPath(pen, fp3);
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

            // ============================ 图层顺序（v1.0 统一整理） ============================
            // 起因：用户实机发现「松手把 3 张图加入「项目1」」这条提示被**计数胶囊**盖住了字。
            // 根因不是坐标凑巧撞上，是**顺序写死的** —— 提示条当时画在 DrawControls 中间，
            // 而计数胶囊画在整层之后，于是胶囊天经地义地压在提示上面。
            //
            // 所以这里不再按"谁先写的"排，改成按**角色**排。从下到上：
            //
            //   ① 装饰 / 背景    环、环的影子、拖痕         —— 层0
            //   ② 内容          缩略图卡片                —— DrawWheel 里直接画
            //   ③ 常驻控件       关闭 / 设置 / 万能键 / 名字药丸 / 把手 —— 层1（DrawControls）
            //   ④ 常驻指示       计数胶囊「几 / 几」         —— 跟滚动位置绑，不进缓存层
            //   ⑤ 瞬时氛围       涟漪                      —— "东西进来了"那一下的回应
            //   ⑥ 瞬时反馈       提示条（Toast）            —— **必须压住 ③④⑤**：它是回应用户刚做的一个动作，
            //                                              一出现就得看得见，被常驻的东西挡住等于没反馈
            //   ⑦ 诊断           元素名 + 边框              —— 诊断模式是给人查问题用的，压在所有东西上面
            //
            //  规矩：**加新元素时先问它属于哪一类**，按类插进这条链，别再按"顺手写在哪"排。
            //
            //  ⚠️ 还有一条同样重要的：**新元素必须 Diag 登记**。
            //  「拖放提示」原来连名字都没有，于是任何几何检查都看不见它 ——
            //  我第一轮去改"提示条"（同类的另一个元素）却毫无察觉，就是栽在这上面。
            //  没有名字的元素 = 没有任何检查能拦住它。
            DrawCountPill(g, a);       // ④
            DrawRipple(g, a);          // ⑤
            // ⑥ 瞬时反馈：三条共用状态区那一格，**同一时刻只显示最紧急的一条**。
            //    优先级：拖放提示（正卡着用户一个动作）> 长按提示 > 操作回执。
            //    这样它们既不会互相压住，也不用各自找位置（各自找位置就是之前那些重叠的来源）。
            bool statusTaken = false;
            // ⚠️ 这里**不能**再写 `_dropActive &&` —— 调用处一卡这个 bool，
            //    "出现"那一下内部还有机会淡入（_dropVis 在涨），
            //    "消失"时 _dropActive 已经翻了 false，调用处直接跳过 → **硬切**。
            //    用户报的正是"消失没有过渡，环是有的"（环那边读的是 _dropVis，不受影响）。
            //    要不要画、画多淡，全交给 DrawDropHint 自己按 _dropVis 判断。
            //    它也不占状态区那一格 —— 它的位置在轮盘上端，和那三条不在一块。
            DrawDropHint(g, a);
            if (_closeHoldP > 0.10f) { DrawCloseHoldHint(g, a); statusTaken = true; }
            if (!statusTaken) { DrawToast(g, a); statusTaken = true; }
            DrawDiag(g);               // ⑦ （没有开诊断就什么都不画）
        }






        // 拖出去那道"向外"的短促拖痕：从格子出发朝**松手方向**的一小截，越往外越细、越快淡掉。
        // 刻意不做成一条贯穿两点的直线 —— 那看起来像"连线"，不像"送出去"。
        // 用户原话是"留下一道短促的拖痕"，重点在**短促**和**向外**。
        void DrawDragTrail(Graphics g, int a)
        {
            if (_dragTrailT >= 1f) return;
            float t = _dragTrailT;
            float dx = _dragTrailB.X - _dragTrailA.X, dy = _dragTrailB.Y - _dragTrailA.Y;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist < 6f) return;
            dx /= dist; dy /= dist;
            float len = Math.Min(dist * 0.72f, 210f);
            if (len < 12f) return;
            int alpha = (int)(205 * (1f - t) * (1f - t) * a / 255f);
            if (alpha < 3) return;
            float w0 = 13f * (1f - t * 0.45f), w1 = 2.2f;
            float px = -dy, py = dx;
            PointF A = _dragTrailA;
            PointF tip = new PointF(A.X + dx * len, A.Y + dy * len);
            Color ac = AccentColor();
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddPolygon(new PointF[] {
                    new PointF(A.X + px * w0, A.Y + py * w0),
                    new PointF(tip.X + px * w1, tip.Y + py * w1),
                    new PointF(tip.X - px * w1, tip.Y - py * w1),
                    new PointF(A.X - px * w0, A.Y - py * w0)
                });
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, ac.R, ac.G, ac.B)))
                    g.FillPath(b, p);
            }
            // 末端一个小亮点 = "送出去"的那一下
            float dotR = 4.6f * (1f - t);
            if (dotR > 0.6f)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, alpha * 2), ac.R, ac.G, ac.B)))
                    g.FillEllipse(b, tip.X - dotR, tip.Y - dotR, dotR * 2, dotR * 2);
        }

        void DrawToast(Graphics g, int a)
        {            if (_toast.Length == 0) return;
            float age = (float)(DateTime.Now - _toastAt).TotalSeconds;
            float dur = _toastDur <= 0.1f ? 2.6f : _toastDur;
            if (age > dur) return;
            float t = 1f;
            if (age < 0.18f) t = age / 0.18f;
            else if (age > dur - 0.5f) t = Math.Max(0f, (dur - age) / 0.5f);
            int ta = (int)(235 * t * a / 255f);
            if (ta <= 2) return;
            using (Font f = new Font("Microsoft YaHei UI", 10f))
            {
                // 先按"屏幕容得下的宽度"折行量一次：整条提示绝不能宽过屏幕（用户实测左边出屏）。
                // 也不能铺成一条横幅 —— 太宽会盖住环，所以最宽只取逻辑宽度的一部分（有下限，
                // 窗口很小时不至于挤成一列字）。物理像素和逻辑像素在缩放里差一个倍数，
                // 所以这里用逻辑宽度量、再交给 StatusArea。
                SizeF ls = LogicalSize();
                float avail = Math.Max(160f, Math.Min(ls.Width - 86f, ls.Width * 0.66f));
                SizeF one = g.MeasureString(_toast, f);
                SizeF sz = one.Width <= avail ? one : g.MeasureString(_toast, f, new SizeF(avail, 9999f));
                if (sz.Width > avail) sz.Width = avail;
                RectangleF slot = StatusArea(sz, 17f, 8f);
                float w = slot.Width, h = slot.Height;
                float k2 = (1f - t) * 14f;                 // 从画面外往里滑进来
                float x = slot.X + (Sx() > 0 ? k2 : -k2);
                float y = slot.Y + (Sy() > 0 ? k2 : -k2);
                Diag("提示条（Toast）", new RectangleF(x, y, w, h));
                using (GraphicsPath pp = Gfx.Round(new RectangleF(x, y, w, h), h / 2f))
                {
                    BackdropClip(g, pp, ta);
                    Gfx.GlassPanel(g, pp, new RectangleF(x, y, w, h), Gfx.A(GlassBase(), GlassA((int)(ta * 0.86f))),
                        (int)(ta * 0.16f), (int)(ta * 0.14f), !StyleFlatOnly());
                    using (Pen p = new Pen(Gfx.A(Gfx.Shade(_accentCur, 0.15f), (int)(ta * 0.42f)), 1.2f))
                        g.DrawPath(p, pp);
                }
                // 折行画进框里（框已经夹在屏幕内；内容太长时末行收省略号）
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(ta, 255, 255, 255)))
                using (StringFormat sf = new StringFormat(StringFormatFlags.LineLimit))
                {
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    g.DrawString(_toast, f, tb, new RectangleF(x + 17f, y + 8f, w - 34f, h - 16f), sf);
                }
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
            Diag("环（四分之一圆环）", new RectangleF(c.X - rr, c.Y - rr, rr * 2f, rr * 2f));
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddArc(c.X - rr, c.Y - rr, rr * 2f, rr * 2f, st, sweep);
                // 拖放反馈：环上那圈光晕 + 亮轨道（外部文件=偏绿，自己的图=偏蓝）。
                // **乘 _dropVis 淡入淡出** —— 用户反馈"环随之变绿复原没有任何过渡"，
                // 就是这里原来直接按 bool 画满。它和那条提示共用同一个进度，所以两边一起走。
                float dv = _dropVis;
                if (dv > 0.01f)
                {
                    Color dc = _dropExternal ? Color.FromArgb(86, 214, 138) : Color.FromArgb(96, 170, 255);
                    Color dch = Color.FromArgb(255, Math.Min(255, dc.R + 24), Math.Min(255, dc.G + 24), Math.Min(255, dc.B + 24));
                    int da1 = (int)(110 * dv * ringA / 255f);
                    int da2 = (int)(235 * dv * ringA / 255f);
                    float dth = 40f + 10f * (1f - dv);          // 顺手让光晕"涨"进来，不只是变亮
                    if (da1 > 1)
                        using (Pen dg = new Pen(Color.FromArgb(da1, dc.R, dc.G, dc.B), dth))
                        { dg.StartCap = LineCap.Round; dg.EndCap = LineCap.Round; g.DrawPath(dg, gp); }
                    if (da2 > 1)
                        using (Pen dm = new Pen(Color.FromArgb(da2, dch.R, dch.G, dch.B), 6f))
                        { dm.StartCap = LineCap.Round; dm.EndCap = LineCap.Round; g.DrawPath(dm, gp); }
                }
                // 环的影子：先给轨道垫一层柔和的暗色（光从左上来，影子往右下走），环就"浮"起来了
                DrawRingShadow(g, gp, rr, ringA);
                // 环：扁平化处理 —— 一条细亮线为主，新拟态风格再垫一层柔和的光晕
                // 厚度跟着内容走（v1.0）：空环最细、堆满最粗，一眼能看出"这里装了多少东西出"
                float thick = RingThick();
                if (StyleNeu() && _settings.ShadowPercent > 8)
                    using (Pen glow = new Pen(Gfx.A(DayTint(_accentCur), (int)(34 * ringA / 255f)), 22f * thick))
                    { glow.StartCap = LineCap.Round; glow.EndCap = LineCap.Round; g.DrawPath(glow, gp); }
                using (Pen mid = new Pen(Color.FromArgb((int)((StyleFlatOnly() ? 78 : 92) * ringA / 255f), 255, 255, 255), 2.2f * thick))
                { mid.StartCap = LineCap.Round; mid.EndCap = LineCap.Round; g.DrawPath(mid, gp); }
                using (Pen hair = new Pen(Color.FromArgb((int)((StyleFlatOnly() ? 210 : 235) * ringA / 255f), 255, 255, 255), 1.3f + 0.5f * (thick - 1f)))
                { g.DrawPath(hair, gp); }
                if (_switchFlash > 0.01f)     // 切换 Wheel 时的一圈扩散闪光
                {
                    Color fc = DayTint(_accentCur);
                    using (Pen fp = new Pen(Color.FromArgb((int)(_switchFlash * 130f), fc.R, fc.G, fc.B), 12f * _switchFlash + 2f))
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
            // 0.6.0：每次唤出都重新抢一次最顶层 —— 双击打开图片、图片查看器关掉之后，
            // 轮盘会被压在别的置顶窗口下面（用户反馈：关掉窗口后轮盘回不到最顶层）。
            try { Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); } catch { }
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
            AnimTickCountForTest++;
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
                // 悬停变化这一帧**必须画**（省电模式下也不许延到下一 tick）：置 _forceDraw
                if (hh != _hover) { _hover = hh; need = true; _forceDraw = true; }
                if (gh != _gearHover) { _gearHover = gh; need = true; _forceDraw = true; }
                if (ch != _closeHover) { _closeHover = ch; need = true; _forceDraw = true; }
                if (sH != _shootHover) { _shootHover = sH; need = true; _forceDraw = true; }
                if (kh != _keyHover) { _keyHover = kh; need = true; _forceDraw = true; }
                if (nh != _nameHover) { _nameHover = nh; need = true; _forceDraw = true; }
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

            // 空态提示 ↔ 计数胶囊：交叉淡入淡出。
            // 原来两边都是"count==0 就画 / 否则不画"的硬开关 —— 第一张图进来那一刻，
            // 提示瞬间消失、胶囊瞬间出现，中间什么都没有（用户反馈："都是突然消失出现"）。
            // 合成一个参数之后，一个淡出的同时另一个淡入，天然是交叉的。
            {
                float etTgt = (_store.Items.Count == 0) ? 1f : 0f;
                if (!_emptySynced) { _emptyT = etTgt; _emptySynced = true; need = true; }
                else if (Math.Abs(_emptyT - etTgt) > 0.002f)
                {
                    float er = Math.Max(0.05f, Math.Min(0.5f, 0.20f / AnimK()));
                    _emptyT += (etTgt - _emptyT) * er;
                    need = true;
                }
                else if (_emptyT != etTgt) { _emptyT = etTgt; need = true; }
            }

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
                // 0.6.0：删除后后面每张图的下标前移 1，位置本来会瞬间下移一格。
                // 用户要的是看着它们滑下来：先给一个 +一个步距的补偿（把图拉回旧位置），
                // AnimTick 再每帧衰减回 0，于是从旧位置平滑滑到新位置。
                _phiShift = StepRad();
                _delShiftFrom = _delIdx;
                // 0.6.0：删除后，后面每张图的下标都前移 1 —— 视口也必须跟着挪一格，
                // 否则"上面的缩略图会瞬间下移一格"（用户反馈的瞬移、没有过渡）。
                // 判据：删的是视口最下面那张或更靠上的，才需要 -1（可见内容位置保持不变）；
                // 删的是视口下方的图，可见范围本来就不受影响。用 _targetOffset 而不是 _offset，
                // 这样位移是**动画过渡**过去的，不是跳过去。
                if (_delIdx >= 0 && _delIdx <= (int)Math.Round(_targetOffset))
                    _targetOffset = Math.Max(MinOffset(), _targetOffset - 1f);
                _delIdx = -1;
                    _deleteProg = 0f;
                    RemoveItem(victim, true);
                }
            }

            // keep animating while a freshly captured image is still sliding in / a delete is running
            // v1.0：窗口从 0.5 秒放宽到"微光冷完"那一刻（FreshHoldSec + FreshCoolSec）——
            // 否则滑入动画走完之后微光就冻住不冷了（它还需要约 4.8 秒）。这是**真的有东西在变**，
            // 不是空转：跑完就停，和上面那几条纪律一致。
            foreach (KeyValuePair<StoreItem, DateTime> kv in _enterT0)
                if ((DateTime.Now - kv.Value).TotalSeconds < 0.5) { need = true; break; }
            // 微光的窗口更长（亮 2.2 秒 + 冷 2.6 秒），单独走一份表 —— 见 _freshT0 的说明
            foreach (KeyValuePair<StoreItem, DateTime> kv in _freshT0)
                if ((DateTime.Now - kv.Value).TotalSeconds < FreshHoldSec + FreshCoolSec + 0.05) { need = true; break; }
            // 切轮盘时名字药丸"翻一下"
            if (_nameSwapT < 1f)
            {
                _nameSwapT += (float)((DateTime.Now - _nameSwapAt).TotalSeconds / 0.55f);
                if (_nameSwapT >= 1f) _nameSwapT = 1f;
                _nameSwapAt = DateTime.Now;
                need = true;
            }
            // 涟漪：从新来那一格扩散出去的一圈光
            if (RippleTick()) need = true;
            // 小按钮的发光淡入淡出（0 或 1 之间平滑走，和别处的趋近写法一致）
            {
                float g1 = _closeGlow + ((_closeHover ? 1f : 0f) - _closeGlow) * 0.22f;
                float g2 = _gearGlow + ((_gearHover ? 1f : 0f) - _gearGlow) * 0.22f;
                float g3 = _shootGlow + ((_shootHover ? 1f : 0f) - _shootGlow) * 0.22f;
                if (g1 < 0.004f) g1 = 0f; if (g2 < 0.004f) g2 = 0f; if (g3 < 0.004f) g3 = 0f;
                if (g1 != _closeGlow || g2 != _gearGlow || g3 != _shootGlow) { _closeGlow = g1; _gearGlow = g2; _shootGlow = g3; need = true; }
            }
            if (_deletingItem != null) need = true;
            // 持久置顶：图片查看器之类的窗口自己也是置顶的，它一关，Windows 就把我们排到
            // 非置顶带里去了 —— 只在唤出那一刻抢一次不够（用户反馈：一关图片窗口轮盘就沉下去）。
            // 这里每 2 秒校验一次，被排下去了就重新抢回来。窗口本来就在顶上时这次调用几乎无成本。
            // 用**抑制计数**而不是改 TopMost 属性：多处嵌套调用时（截图时设 false、期间又打开设置）
            // 恢复出来的会是 false，状态就永久丢了（用户报的"snapwheel 和设置窗口莫名不在顶层"）。
            // 安全阀：正常使用不可能抑制置顶超过 60 秒。超时就说明有地方 ++ 之后没能 --,
            // （比如 ShowDialog 抛异常跳过了收尾），强制清零自愈，不然轮盘会永远不再置顶。
            if (SuppressTopMost > 0)
            {
                if (_suppressSeen == 0) _suppressSeen = Environment.TickCount;
                else if (Environment.TickCount - _suppressSeen > 60000) { SuppressTopMost = 0; _suppressSeen = 0; }
            }
            else _suppressSeen = 0;
            // 安全阀用的时间戳（第一次看到抑制计数 > 0 的时刻）
            if (Visible && _settings.AlwaysOnTop && SuppressTopMost == 0 && (DateTime.Now - _topMostAt).TotalSeconds > 2.0)
            {
                _topMostAt = DateTime.Now;
            try { Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); } catch { }
            }
            if (_phiShift != 0f)
            {
                _phiShift *= 0.80f;
                if (Math.Abs(_phiShift) < 0.002f) { _phiShift = 0f; _delShiftFrom = -1; }
                need = true;
            }

            // 拖出去的反馈（v1.0）：向外那道拖痕，和那一格"颤一下 + 高亮"。
            // 两者都是**有始有终**的标量动画，跑完就停在 1，绝不留"永远差一点点"的状态
            // （空转满帧那两次事故都是这个形状）。
            if (_dragTrailT < 1f)
            {
                _dragTrailT += (float)((DateTime.Now - _dragTrailAt).TotalSeconds / 0.45f);
                if (_dragTrailT >= 1f) _dragTrailT = 1f;
                _dragTrailAt = DateTime.Now;
                need = true;
            }
            if (_dragPulseT < 1f)
            {
                _dragPulseT += (float)((DateTime.Now - _dragPulseAt).TotalSeconds / 0.55f);
                if (_dragPulseT >= 1f) { _dragPulseT = 1f; _dragPulseIdx = -1; }
                _dragPulseAt = DateTime.Now;
                need = true;
            }
            // 拖拽结束后"提起来"要放回去（拖回轮盘、或者拖出去之后）
            if (_dragLift > 0f && _dragOutItem == null)
            {
                _dragLift += (0f - _dragLift) * 0.22f;
                if (_dragLift < 0.01f) _dragLift = 0f;
                need = true;
            }

            // 拖放态淡入淡出（用户反馈：绿提示和环变绿都是硬切，一点过渡都没有）。
            // 一个标量同时驱动**环的光晕**和**那条提示**，两边自然同步。
            // 有始有终：走到 0 或 1 就吸附，绝不留"永远差一点点"的状态。
            {
                float want2 = _dropActive ? 1f : 0f;
                float cur2 = _dropVis;
                if (Math.Abs(cur2 - want2) > 0.006f)
                {
                    _dropVis = cur2 + (want2 - cur2) * 0.24f;
                    if (Math.Abs(_dropVis - want2) < 0.006f) _dropVis = want2;
                    need = true;
                }
                else if (cur2 != want2) { _dropVis = want2; need = true; }
                // "这次是不是外部拖放"要**锁存**到完全淡出为止（见字段说明）。
                // ⚠️ 清除必须加"已经不在拖放中"这个条件：拖放刚开始那一帧 _dropVis 还是 0，
                //    先锁存、后按 _dropVis 清掉的话，等于刚锁上就没了。
                if (_dropActive && _dropExternal) _dropExternalShown = true;
                if (!_dropActive && _dropVis <= 0.001f) _dropExternalShown = false;
            }

            // 提示条（"已加入 N 张图片"）淡入淡出
            if (_toast.Length > 0)
            {
                if ((DateTime.Now - _toastAt).TotalSeconds > _toastDur) _toast = "";
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
            // 这里**故意不写** if (x > 0.01f) need = true; 那种「悬停期间保持刷新」。
            // 上面两行已经把「正在变化」的每一帧都请求了；值一旦到位（else if 里会吸附），
            // 光晕就不该再要求任何一帧 —— 它是**纯标量**，不跟光标位置走。
            // 实测：留着那一行，只要它一直亮着就每帧重画（空转 46fps），而画面一个像素都没变。

                // 把手用途提示（"点我展开/收起"）：悬停时亮；首次运行的头 14 秒也自动亮一次
                bool wantHint = wantOut || wantIn || DateTime.Now < _firstRunHintUntil;
                float wantHT = wantHint ? 1f : 0f;
                if (Math.Abs(_nubHintT - wantHT) > 0.006f) { _nubHintT += (wantHT - _nubHintT) * 0.18f; need = true; }
                else if (_nubHintT != wantHT) { _nubHintT = wantHT; need = true; }

            }

            // 后台抓好的玻璃底：在 UI 线程这里换上。
            // 但动画期间不换（尤其是展开/收起那趟）—— 换底会强制整窗重绘，正好卡在动画中间，看着就"顿"。
            // 悬停/交互期间也不换：换底会强制整窗重绘，正好把交互那一下顶慢（"慢半拍"）
            // 后台抓取完成后立即换底；换底本身在后台准备，不应因鼠标停在轮盘上而永久延迟。
            if (!_intro)
                ApplyPendingBackdrop();

            // 背景交叉淡入（换背景时玻璃颜色渐变，不跳）
            if (_backdropOld != null && _backdropFade < 1f)
            {
                _backdropFade += (float)((DateTime.Now - _backdropFadeAt).TotalSeconds / 0.65f);
                if (_backdropFade >= 1f) { _backdropFade = 1f; try { _backdropOld.Dispose(); } catch { } _backdropOld = null; }
                _backdropFadeAt = DateTime.Now;
                need = true;
            }

            // 玻璃底定时重抓：轮盘一直挂着也不会"糊的是半小时前的桌面"
            // （窗口已设置 WDA_EXCLUDEFROMCAPTURE，抓屏不会把轮盘自己拍进去，所以显示中也能抓）
            // 省电模式（电池上）：**只**停"每 1 秒的定时重抓"这一档 —— 轮盘刚显示 / 切盘 / 拖放 / 隐藏时
            // 那几处 RequestBackdropAsync() 照旧（否则玻璃底色会缺）。见 12-Power.cs。
            // 演示模式（能被录到）下**必须停掉定时刷新**：这时轮盘对捕获不隐身了，
            // 再定时抓背景就会把自己的影子糊进自己的玻璃里。
            if (_settings.GlassRefresh && !_settings.Recordable && Visible && _show > 0.99f && !_intro && !PowerSaveOn())
            {
                if ((DateTime.Now - _backdropAt).TotalSeconds > 1.0)
                {
                    _backdropAt = DateTime.Now;
                    // 只要没有正在拖放、拖出或删除，就持续请求后台抓取；悬停不能阻止背景更新。
                    if (!_dropActive && _dragOutItem == null && _deletingItem == null)
                        RequestBackdropAsync();
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
                Color next = Color.FromArgb(
                    (int)Math.Round(_accentCur.R + (want.R - _accentCur.R) * 0.22f),
                    (int)Math.Round(_accentCur.G + (want.G - _accentCur.G) * 0.22f),
                    (int)Math.Round(_accentCur.B + (want.B - _accentCur.B) * 0.22f));
                // ⚠️ 收敛判定，**这一行是必须的**：
                // 上面是"每帧靠近 22%"，而插值结果又被 Round 成整数 ——
                // 当某个通道只差 1 时，cur + 0.22 取整回来还是 cur，于是它**永远停在差 1 的地方**，
                // 于是 `_accentCur != want` 恒成立、`need` 每个 tick 都为真 ——
                // **空转时也在满帧重画**（实测 45fps，10 秒 450 帧）。
                // 每通道都只差 1 以内就吸附过去，让它真的能停下来。
                bool close = Math.Abs(next.R - want.R) <= 1 && Math.Abs(next.G - want.G) <= 1
                             && Math.Abs(next.B - want.B) <= 1;
                _accentCur = close ? want : next;
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

            // 安全阀：正常使用不会抑制超过 2 分钟，超时说明有人忘了减一，强制清零自愈
            if (SuppressAutoHide > 0)
            {
                if (_suppressHideSeen == 0) _suppressHideSeen = Environment.TickCount;
                else if (Environment.TickCount - _suppressHideSeen > 120000) { SuppressAutoHide = 0; _suppressHideSeen = 0; }
            }
            else _suppressHideSeen = 0;
            if (SuppressAutoHide == 0 && _settings.AutoHide && !_collapsed && _targetShow > 0.5f && _show > 0.99f && !_intro)
                if ((DateTime.Now - _lastActive).TotalSeconds > _settings.AutoHideSeconds) DismissWheel();

            if (_show <= 0.002f && _targetShow <= 0.002f)
            {
                if (Visible) { Hide(); RequestBackdropAsync(); }   // 隐藏后再抓一次，下次显示时玻璃底是新的（后台抓，别卡 UI）
                return;
            }
            // 省电模式（电池上）：**隔一帧才画一次**，但三条硬约束不许破 ——
            //   ① 定时器间隔绝不动（15ms 那个节奏是动画的时基：_deleteProg += 0.055f、_chipsT 平滑都按帧推进，
            //      改间隔它们就整体变慢）；这里只决定"这一帧要不要真去 Render"。
            //   ② **绝不连续两帧不画**：上一帧跳过了，这一帧无论如何都画（_powerSkipped）。
            //   ③ **输入那一帧必画**：悬停/按下/滚轮改变的那一下要立刻看见（_forceDraw），不许延到下一 tick。
            if (PowerSaveOn() && !_powerSkipped && !_forceDraw)
            {
                _powerSkipped = true;                 // 这一帧省掉
                SkipCountForTest++;
            }
            else
            {
                _powerSkipped = false;
                if (need || !_rendered) Render();     // render ONLY when something changed (smooth + cheap)
            }
            _forceDraw = false;
        }


        // 现在是不是"省电生效"：设置开着 **且** 在电池上（Power.OnBattery 内部缓存 5 秒，不会每帧问系统）
        bool PowerSaveOn()
        {
            try { return _settings != null && _settings.PowerSave && Power.OnBattery(); }
            catch { return false; }
        }


        // only a NEWLY captured image slides in; everything else is already in place
        public void MarkNew(StoreItem it)
        {
            if (it == null) return;
            _enterT0[it] = DateTime.Now;
            _freshT0[it] = DateTime.Now;      // 微光只认这一份（切轮盘的"依次滑入"不算新）
            // 涟漪从"这一格"扩散出去（见 61d-WheelForm.Atmos.cs）：东西进来了，这件事要有形状。
            // 位置用"这张图在弧上的格子中心"——它可能刚滑进来还没到位，那就从它该到的位置开始，
            // 看着才是"从它那儿散开的"。
            try
            {
                int idx = _store.Items.IndexOf(it);
                if (idx >= 0) StartRipple(ItemCenterAtPhi(ItemPhi(idx)));
            }
            catch { }
            // 视口要跟到最新那张 —— 否则"刚截的图"可能落在可见弧之外（收起态下 offset 被归零，
            // 第 7 张的 ItemPhi 已经是 1.747，而可见弧上界 _phiMax+0.5 只有 1.766）：
            // 滑入动画其实在弧外跑，等它擦着边跨进来才"啪"地闪现一下 —— 用户报的
            // "缩略图滑进 wheel 突然闪现、动画和位置合不上"就是这个。
            // 剪贴板导入那条路一直是这么做的（OnClipboardChanged / ImportFiles 里都有这一句），
            // 截图这条路以前漏了。
            //
            // 0.5.3：目标值从 Count-1 改成 Count-Slots（最新那张顶在弧**上端**，见 OffsetForNewest），
            // 而且这一步可以在设置里关掉（关 = 保持用户当前滚动位置，不把他正在看的地方拽走）。
            FollowNewest();
        }


        // 收进 / 截进一张新图之后，视口要不要回到"最新那张"。
        // 设置项 `ResetScrollOnCapture`（默认开）关掉时：什么都不做 —— 视口就停在用户当前的位置，
        // 新图照样按 EnterProgress 从上面滑进来，只是不把画面拽走。
        void FollowNewest()
        {
            if (_settings != null && !_settings.ResetScrollOnCapture) return;
            _targetOffset = OffsetForNewest();
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

        int _backdropGen;                    // 玻璃底换到第几代（分层缓存的签名要用它，见 66-Layers）


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
                        {
                            // BitBlt 直接读取当前桌面 DC，避免 CopyFromScreen 在分层窗口/捕获排除策略下回退到旧合成帧。
                            IntPtr dc = g.GetHdc();
                            IntPtr screen = Native.GetDC(IntPtr.Zero);
                            try
                            {
                                if (screen == IntPtr.Zero || !Native.BitBlt(dc, 0, 0, got.Width, got.Height, screen, got.Left, got.Top, Native.SRCCOPY))
                                    throw new InvalidOperationException("Glass screen capture failed");
                            }
                            finally { if (screen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screen); g.ReleaseHdc(dc); }
                        }
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
        // 测试用：因为"新底和旧底一样"而跳掉了几次交叉淡入
        public static int GlassSkipForTest = 0;

        // 两片底图是不是**一模一样**（尺寸、偏移、像素）。
        // 用 LockBits 逐字节比，别用 GetPixel：658x658 就是 43 万次调用，那才是真的慢。
        // 逐字节全比，不做"抽样比几个点"——抽样会漏掉局部变化，而那正是要淡入的东西。
        // 这个函数 3.5 秒才跑一次，一次约 1ms，完全可以接受。
        static bool SameBackdrop(Bitmap a, Bitmap b, Point offA, Point offB)
        {
            if (a == null || b == null) return false;
            if (a.Width != b.Width || a.Height != b.Height) return false;
            if (offA.X != offB.X || offA.Y != offB.Y) return false;
            try
            {
                Rectangle r = new Rectangle(0, 0, a.Width, a.Height);
                BitmapData da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    BitmapData db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        int len = da.Stride * a.Height;
                        if (db.Stride * b.Height != len) return false;
                        byte[] ba = new byte[len], bb = new byte[len];
                        System.Runtime.InteropServices.Marshal.Copy(da.Scan0, ba, 0, len);
                        System.Runtime.InteropServices.Marshal.Copy(db.Scan0, bb, 0, len);
                        for (int i = 0; i < len; i++) if (ba[i] != bb[i]) return false;
                        return true;
                    }
                    finally { b.UnlockBits(db); }
                }
                finally { a.UnlockBits(da); }
            }
            catch { return false; }     // 比不了就当"变了"，保守：宁可白淡一次，也不要少淡一次
        }

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
            // 相同底图直接替换并跳过淡入；只有像素或位置真的变化时才交叉淡入。
            if (_backdropBlur != null && Visible && SameBackdrop(fresh, _backdropBlur, off, _backdropOffset))
            {
                try { _backdropBlur.Dispose(); } catch { }
                _backdropBlur = fresh;
                _backdropOffset = off;
                _backdropValid = true;
                _backdropAt = DateTime.Now;
                BackdropChanged();
                _rendered = false;
                GlassSkipForTest++;
                return;
            }
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
            BackdropChanged();
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
                BackdropChanged();
            }
            catch { FreeBackdrop(); }
        }


        // 换上一张新玻璃底时统一收尾：代次 +1（让分层缓存的签名失效）、丢掉上一轮的整帧混图。
        // 混图必须丢：_backdropMixFrame 记的是"渲染帧号"，而帧号只在真的出图时才 +1，
        // 所以换底那一帧的帧号很可能只比上一轮最后一张混图大 1 —— 不丢就会把
        // **上一轮淡入结束时那张几乎全是新底的混图**当成第一帧贴出去，开头闪一下新底。
        void BackdropChanged()
        {
            _backdropGen++;
            _backdropMixFrame = -1;
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
            // 一帧只混一次，同一帧里所有卡片共用（这才是"整帧只混一次"的原意）。
            // 以前这里写的是 _frameNo - _backdropMixFrame < 2（隔两帧才重建），但帧号每帧都 +1、
            // 淡入进度也是每帧都推进，于是差值恒为 1 —— 等于**永远**在用上一帧那张混图：
            // 淡入被量化成 2 帧一步（实测 0.38 秒只有 9 档，一步约 8%，看着一格一格地跳）。
            if (_backdropMix != null && _backdropMixFrame == _frameNo) return _backdropMix;
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
                    // 不要再加 Math.Max(0.06f, …) 那种下限：淡入第一帧 fade 就是 0，
                    // 强制按 6% 新底画 = 换底那一瞬间凭空跳 6%，正是"突兀"的来源之一。
                    float fade = _backdropFade < 0f ? 0f : (_backdropFade > 1f ? 1f : _backdropFade);
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

        // 把一张"整窗口尺寸"的玻璃底裁进当前形状 —— 只取形状真正会用到的那一小块源图。
        // 为什么必须这么干：一张 822x822 的底，裁进 6 张卡片 + 十来个控件（万能键盘/圆按钮/药丸/把手），
        // 每次都让 GDI+ 把**整张图**过一遍采样，一帧就是十几倍全窗口的开销（实测卡片底 2.5ms x 6 张）。
        // 这里按形状的包围盒把源矩形缩到实际需要的几十像素见方，画出来完全一样、只是不再白算。
        // 只在交叉淡入分支用：稳态那一路的画法（乃至它的采样细节）一个字都不动，别去碰用户天天看的那张毛玻璃。
        void DrawGlassCrop(Graphics g, Bitmap bmp, GraphicsPath path, int al)
        {
            try
            {
                // 形状在**设备坐标**下的包围盒：当前变换里既有 UiK 缩放、也可能有控件层的位移
                RectangleF pb = path.GetBounds(g.Transform);
                if (pb.Width < 1f || pb.Height < 1f) return;
                // 往外放 3px：抗锯齿的边缘 + 后面换算的取整，不能露出一条没画到的缝
                float pad = 3f;
                int sx = (int)Math.Floor(pb.X - pad) - _backdropOffset.X;
                int sy = (int)Math.Floor(pb.Y - pad) - _backdropOffset.Y;
                int sw = (int)Math.Ceiling(pb.Width + pad * 2f + 2f);
                int sh = (int)Math.Ceiling(pb.Height + pad * 2f + 2f);
                if (sx < 0) { sw += sx; sx = 0; }
                if (sy < 0) { sh += sy; sy = 0; }
                if (sx + sw > bmp.Width) sw = bmp.Width - sx;
                if (sy + sh > bmp.Height) sh = bmp.Height - sy;
                if (sw < 1 || sh < 1) return;
                // 目标直接用**设备像素整数矩形**：临时把画布变换复位（裁剪区是按设备坐标记住的，
                // 复位不影响它），于是这是一次 1:1 贴图，既不缩放也不重采样 ——
                // 而且落点和稳态那条"整张图画进 dest"的路完全同一个像素位置，换路时不会跳。
                System.Drawing.Drawing2D.Matrix m = g.Transform;
                try
                {
                    g.ResetTransform();
                    Rectangle dd = new Rectangle(sx + _backdropOffset.X, sy + _backdropOffset.Y, sw, sh);
                    if (al >= 250) g.DrawImage(bmp, dd, sx, sy, sw, sh, GraphicsUnit.Pixel);   // 满 alpha 就别走 ColorMatrix（慢路径）
                    else
                    {
                        ColorMatrix cmo = new ColorMatrix(); cmo.Matrix33 = al / 255f;
                        _iaBack.SetColorMatrix(cmo);
                        g.DrawImage(bmp, dd, sx, sy, sw, sh, GraphicsUnit.Pixel, _iaBack);
                    }
                }
                finally { try { g.Transform = m; } catch { } }
            }
            catch { }
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
                    if (mix != null) DrawGlassCrop(g, mix, path, al);
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
    // 输入：拖放与导入（OLE 拖放回调、导入图片/文件、剪贴板）（从 64-WheelForm.Input.cs 拆出，纯搬移，行为不变）。
    partial class WheelForm
    {
        void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            Cursor.Current = Cursors.Arrow;            // keep the normal pointer (no odd drag cursor)
            if (_dragOutItem != null)
            {
                // 两种模式在拖拽中的画法**必须不同**，否则至少有一种在骗人：
                //   留一份（默认）：图没走 → 画成"提起来"（放大一点、不缩小）
                //   移走          ：图会走 → 照旧一路缩小到看不见
                if (_settings.KeepAfterDragOut)
                {
                    if (_dragLift < 1f) { _dragLift = Math.Min(1f, _dragLift + 0.16f); Render(); }
                }
                else if (_dragOutProg < 1f)
                {
                    _dragOutProg = Math.Min(1f, _dragOutProg + 0.16f);   // pull-out collapse during the drag
                    Render();
                }
            }
            if (_proxy.Visible) _proxy.MoveTo(OffsetPt());
        }

        // 拖进来的到底是啥：文件（含文件夹）还是直接一张位图（网页/其它程序里拖出来的图）
        static bool HasBitmapData(IDataObject data)
        {
            if (data == null) return false;
            try { return data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib); }
            catch { return false; }
        }

        // 拖进来的是一段文字（网页里选中一段、记事本里选中一段）。1.3.0 起它变成**文字格**。
        static bool HasTextData(IDataObject data)
        {
            if (data == null) return false;
            try
            {
                if (!data.GetDataPresent(DataFormats.UnicodeText) && !data.GetDataPresent(DataFormats.Text)) return false;
                // 只有文字才算：Windows 的"虚拟文件"和资源管理器也会顺手塞一段文本，
                // 那种情况必须优先按文件走（见 OnDragDropWheel 的判定顺序）
                return !data.GetDataPresent(DataFormats.FileDrop) && !HasBitmapData(data);
            }
            catch { return false; }
        }

        // 这个环收不收这种格子（1.3.0 的 takes）。不收的时候必须**说一句**：
        // 静默丢掉是最让人困惑的结果 —— 用户只会以为拖放坏了。
        bool Refuses(CellKind k, out string why)
        {
            why = "";
            Wheel w = _mgr.ActiveWheel;
            if (w == null || w.Accepts(k)) return false;
            why = Lang.T("这个环只收", "This ring only takes ") + w.TakesLabel();
            return true;
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
                else if (HasTextData(e.Data)) { ext = true; n = 1; }   // 1.3.0：拖进来的一段字也是一格
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
                else if (HasTextData(e.Data))
                {
                    e.Effect = DragDropEffects.Copy;
                    ImportText(e.Data);
                }
                else e.Effect = DragDropEffects.None;
            }
            ClearDropCache();
            Render();
        }

        // 直接拖过来的一张位图（不是文件）：网页、看图软件、聊天窗口里拖出来的图都走这里
        public void ImportBitmap(IDataObject data)
        {
            string why;
            if (Refuses(CellKind.Image, out why)) { ShowToast(why); Render(); return; }   // 这个环不收图片
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
            if (bmp == null) { ShowToast(Lang.T("这张图读不出来", "This image could not be read")); Render(); return; }
            bmp = ImageIO.Fit(bmp, ImageIO.MaxDim);
            StoreItem it = _store.AddCore(bmp, ImageIO.ExtFor(bmp));
            _enterT0[it] = DateTime.Now;
            FollowNewest();                       // 视口跟到最新那张（设置里可关）
            _hover = -1; _enlarged = -1;
            ShowToast(Lang.T("已加入 1 张图片", "Added 1 image"));
            Usage.Ev("DropIn", "拖进来 1 张");
            Render();
        }

        // 剪贴板里出现图片就自动收进轮盘（可关）；**自己写的图不收**（见 SelfClipboard），并做去重
        void OnClipboardChanged()
        {
            if (!_settings.ClipboardImport) return;
            if (!_mgr.ActiveWheel.Accepts(CellKind.Image)) return;              // 这个环不收图片，别自动往里塞
            if (_dragOutItem != null) return;                                   // 正在拖出，别掺和

            // 第一道（便宜、精确）：剪贴板序号还是我们自己写完那一下 —— 就是我们自己刚写进去的那张，
            // 直接跳过。**连图都不读**：读一张 1600x1000 实测 ~10ms，正好落在"缩略图滑入"的帧上。
            // 跳过时把手里的指纹记进 _lastClipFp：系统对"写一次剪贴板"可能通知不止一次，
            // 第二条通知来的时候登记已经清掉了，靠这条去重才不会又收一张。
            try
            {
                string mine;
                if (SelfClipboard.TakeBySequence(out mine)) { if (mine != null) _lastClipFp = mine; return; }
            }
            catch { }

            Bitmap copy = null;
            string fp = "";
            try
            {
                if (!Clipboard.ContainsImage()) return;
                using (Image im = Clipboard.GetImage())
                {
                    if (im == null || im.Width < 2 || im.Height < 2) return;
                    fp = SelfClipboard.Fingerprint(im);
                    // 第二道（兜底）：序号对不上时（剪贴板被别的程序动过，或者序号读不到）
                    // 再按"图长什么样"比一次 —— 这一步在下面"导入外部图"那条路上本来就要读图，不额外花钱。
                    if (SelfClipboard.IsOurs(fp)) { _lastClipFp = fp; return; }
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
                    FollowNewest();               // 视口跟到最新那张（设置里可关）
                    _hover = -1; _enlarged = -1;
                    if (_collapsed && _settings.ShowBalloon) Err.Notify(Lang.T("已从剪贴板收进 1 张图", "Collected 1 image from the clipboard"));
                    else ShowToast(Lang.T("已从剪贴板收进 1 张图", "Collected 1 image from the clipboard"));
                    Usage.Ev("ClipboardIn");
                    if (Visible) Render();
                }
            }
            catch { }
        }

        // 把外部文件收进当前 wheel（1.3.0：能当图的当图，读不出来的就是**文件格**；单条失败不打断其它）
        public void ImportFiles(List<string> files)
        {
            if (files == null || files.Count == 0) return;
            int ok = 0, img = 0, file = 0, bad = 0, refused = 0;
            string note = "";
            for (int i = 0; i < files.Count; i++)
            {
                // 这个环声明不收这一种就先拦下（按扩展名预判）：别先把文件搬进来（"移进来"还会动原文件）再反悔
                CellKind guess = ImageIO.IsImageExt(files[i]) ? CellKind.Image : CellKind.File;
                string no;
                if (Refuses(guess, out no)) { refused++; continue; }
                StoreItem it = null;
                string n = "";
                try { it = _store.ImportDropped(files[i], _settings.MoveInOnDrop, out n); } catch { }
                if (it == null) { bad++; continue; }
                if (it.Kind == CellKind.Image) img++; else file++;
                if (!string.IsNullOrEmpty(n)) note = n;               // 最后一条：多是"没能移进来"这种要明说的
                _enterT0[it] = DateTime.Now.AddSeconds(ok * 0.07);    // 依次滑入
                ok++;
            }
            FollowNewest();                                           // 视口跟到最后一张（设置里可关）
            _hover = -1; _enlarged = -1;
            if (ok > 0)
            {
                // 混着文件时不能说"张" —— 用户拖进来一个 PDF，回一句"已加入 1 张图片"是在骗他
                string msg = (file == 0)
                    ? Lang.T("已加入 ", "Added ") + ok + Lang.T(" 张图片", " image(s)")
                    : Lang.T("已加入 ", "Added ") + ok + Lang.T(" 格（图片 ", " cell(s) (image ") + img +
                      Lang.T(" / 文件 ", " / file ") + file + "）";
                if (bad > 0) msg += "（" + bad + Lang.T(" 个没能收进来）", " could not be added)");
                if (refused > 0) msg += "（" + refused + Lang.T(" 个不是这个环收的）", " not what this ring takes)");
                // 文件的结论必须说出来（用户实测反馈：拖进来的文件"移进来"没反应，其实是他不知道有这个开关）：
                // 要么说清"原件进了回收站"，要么说清"留了一份、原件还在 + 去哪儿打开移进来"。
                if (!string.IsNullOrEmpty(note)) msg += " · " + note;
                ShowToast(msg);
            }
            else if (refused > 0)
                ShowToast(Lang.T("这个环只收", "This ring only takes ") + _mgr.ActiveWheel.TakesLabel() +
                          "（" + refused + Lang.T(" 个没收）", " skipped)"));
            else ShowToast(Lang.T("这些文件收不进来", "None of these files could be added"));
            Usage.Ev("DropIn", "拖进来 " + ok + "（图 " + img + " / 文件 " + file + " / 失败 " + bad + " / 不收 " + refused + "）");
            Render();
        }

        // 拖进来的一段文字（网页里选中的、记事本里选中的）→ 文字格。
        // 1.3.0 只收"用户自己拖进来的"；剪贴板里的文字留给 1.8 文字环。
        public void ImportText(IDataObject data)
        {
            string why;
            if (Refuses(CellKind.Text, out why)) { ShowToast(why); Render(); return; }    // 这个环不收文字
            string t = null;
            try { if (data.GetDataPresent(DataFormats.UnicodeText)) t = data.GetData(DataFormats.UnicodeText) as string; } catch { }
            if (string.IsNullOrEmpty(t)) { try { t = data.GetData(DataFormats.Text) as string; } catch { } }
            if (string.IsNullOrEmpty(t))
            {
                ShowToast(Lang.T("这段字读不出来", "That text could not be read"));
                Render(); return;
            }
            bool tooLong = (t.Length > _store.MaxTextLen);
            StoreItem it = null;
            try { it = _store.AddText(t); } catch { }
            if (it == null)
            {
                ShowToast(Lang.T("这段字是空的", "That text was empty"));
                Render(); return;
            }
            _enterT0[it] = DateTime.Now;
            FollowNewest();
            _hover = -1; _enlarged = -1;
            ShowToast(Lang.T("已加入 1 段文字", "Added 1 piece of text") +
                      (tooLong ? Lang.T("（太长，只留了前 5000 字）", " (too long, kept the first 5000)") : ""));
            Usage.Ev("DropIn", "拖进来 1 段文字");
            Render();
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
            _delIdx = _store.Items.IndexOf(it);   // 记住删的是第几张（见 AnimTick 里的视口跟进）
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


        // 三个小按钮（截图 / 设置 / 收起）：**紧贴万能键的左上方、沿一小段弧排布**（0.6.0 用户要求）。
        // 上一版理解错了 —— 我把它当成"绕屏幕角的同心弧"，结果排到了缩略图弧那一侧，
        // 看着就是"靠着 wheel"而不是"靠着万能键"。
        // 正解是**以万能键自己为圆心**的一段小弧：
        //   · 半径 = 万能键半径 + 按钮半径 + 6 → 紧贴万能键外缘；
        //   · 方位固定取屏幕的**左上方**（屏幕坐标 225°：x 负、y 负），
        //     三个按钮在它两侧各偏 40°，于是"围绕万能键的左上方"排成一小段弧。
        Rectangle BtnRect(int order)
        {
#if NO_KEY
            // 无万能键变体：没有可"围绕"的键，退回"贴屏幕角一竖列"（0.2 线冻的是行为，不是这行字）
            PointF c = Center();
            int bx = (int)((Sx() > 0) ? c.X + 10 : c.X - 40);
            float dist = 150f + order * 40f;
            float by = c.Y + Sy() * dist;
            if (Sy() > 0) by -= 30f;
            return new Rectangle(bx, (int)by, 30, 30);
#else
            const int s = 30;
            Rectangle k = KeyRect();
            if (k.Width < 8 || k.Height < 8) return Rectangle.Empty;      // 保险：拿不到键就不排
            float kcx = k.X + k.Width / 2f, kcy = k.Y + k.Height / 2f;
            float rad = k.Width / 2f + s / 2f + 22f;      // 留出万能键那圈外发光的位置（用户反馈：贴太近把光盖住了）
            double baseA = Math.PI * 1.11;                               // 约 200°：比正左上更靠左一点，整体往下挪（用户反馈太靠上）
            double stepA = (s + 8f) / rad;   // 角度间隔按弧长反推：按钮直径 + 8px 间隙
                                              // （原来写死 40°，半径 83px 时弧长就有 107px，三个按钮散得脱节）
            double a = baseA + stepA * (order - 1);
            int x = (int)Math.Round(kcx + rad * Math.Cos(a));
            int y = (int)Math.Round(kcy + rad * Math.Sin(a));
            return new Rectangle(x - s / 2, y - s / 2, s, s);
#endif
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
                    r = ImageIO.CollectAll(paths, 50);
                }
            }
            catch { }
            _dropCacheKey = data; _dropCacheFiles = r; _dropCacheAt = DateTime.Now;
            return r;
        }


        void ClearDropCache() { _dropCacheKey = null; _dropCacheFiles = null; _dropCacheAt = DateTime.MinValue; }











        // 便宜的指纹挪到 SelfClipboard（写剪贴板那边也要用同一个，才能比出"是不是自己写的那张"）
        static string Fingerprint(Image im) { return SelfClipboard.Fingerprint(im); }




        protected override void OnMouseMove(MouseEventArgs e)
        {
            _forceDraw = true;      // 省电模式下，鼠标动这一下也要立刻重绘
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
            _forceDraw = true;      // 按下要立刻有反馈
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
                    try { PinRequested(it.Image, PointToScreen(e.Location)); ShowToast(Lang.T("已贴到屏幕上（双击它或按 Esc 关掉）", "Pinned on screen (double-click it or press Esc to close)")); }
                    catch (Exception ex) { Err.Log("PinRequested", ex); }
                }
            }
        }


        protected override void OnMouseUp(MouseEventArgs e)
        {
            _forceDraw = true;
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
                if (wasLong) { ShowToast(Lang.T("正在退出 SnapWheel…", "Exiting SnapWheel…")); Render(); if (ExitRequested != null) ExitRequested(this, EventArgs.Empty); return; }
                HideWheel();          // 短按：直接关掉轮盘（不是收起）
                return;
            }
            _gearHold = _shootHold = false;
            // 左键单击 = 复制到剪贴板。
            //
            // ⚠️ 这个行为**以前根本不存在** —— 引导里一直写着"直接点一下缩略图则是把这张图复制到剪贴板"，
            // 而代码里从头到尾只有"拖出去 / 右键删 / 双击打开"三条路，单击什么都不做。
            // 又一个"文档在说谎"（和【新】标记、"自动更新 ✅" 同一个形状）。现在把它补上。
            //
            // 判定"这是单击"的依据：`_maybeDrag` 还活着。
            // 一旦移动超过 10px，MouseMove 里就会把它清掉并转成拖出去 —— 所以到这里还在，
            // 就说明按下去之后没拖动过。`_enlarged >= 0` 则说明是长按放大，也不算单击。
            if (e.Button == MouseButtons.Left && _maybeDrag && _dragIndex >= 0 && _enlarged < 0)
            {
                int ci = _dragIndex;
                // 1.3.0：文字格 / 文件格也能点 —— 复制的分别是"这段字"和"文件本身"（见 CopyItemAt）
                if (ci >= 0 && ci < _store.Items.Count) CopyItemAt(ci);
            }
            _maybeDrag = false; _dragIndex = -1; _holdIndex = -1;
            if (_enlarged >= 0) { _enlarged = -1; Render(); }
        }


        // 复制某一张到剪贴板 —— 带反馈。
        //
        // 为什么反馈这么重要：这是"最短路径用法"（不想拖、只想复制的人就走这条），
        // 而它以前**什么都没说** —— 用户点完不知道成了没有，得去别处粘贴一次才知道。
        // 按统计口径这类"做完没有任何回应"的动作是最伤手感的。
        void CopyItemAt(int i)
        {
            bool said = false;
            try
            {
                StoreItem item = _store.Items[i];
                if (item.Image != null)
                {
                    // 登记一下"这张剪贴板是我们自己写的"，别让剪贴板监听把它当成"用户复制的新图"又收一遍
                    SelfClipboard.Note(item.Image);
                    Clipboard.SetImage(item.Image);
                    SelfClipboard.NoteSequence();
                }
                else if (item.Kind == CellKind.Text)
                {
                    // 文字格：复制的应该是**这段字**，不是一张图。
                    // 剪贴板里的文字不会被收进轮盘（那是 1.8 文字环的事），所以不用登记。
                    Clipboard.SetText(item.Text != null ? item.Text : "");
                    said = true;
                }
                else
                {
                    // 文件格：复制的是**文件本身**（粘到资源管理器里就是一份）；拿不到就退回复制它的路径
                    string f = _store.EnsureFile(item);
                    if (!string.IsNullOrEmpty(f) && System.IO.File.Exists(f))
                    {
                        System.Collections.Specialized.StringCollection sc = new System.Collections.Specialized.StringCollection();
                        sc.Add(f);
                        Clipboard.SetFileDropList(sc);
                    }
                    else if (!string.IsNullOrEmpty(item.FilePath)) Clipboard.SetText(item.FilePath);
                    said = true;
                }
            }
            catch { }

            // 反馈一：那一格"弹"一下。
            // 不去新写一套动画 —— 直接把它的缩放顶起来，已有的缩放动画会自己把它收回去，
            // 所以这一下是免费的（约 300ms 的弹性回落）。
            _scales[i] = 1.18f;
            // 反馈二：一句话说清楚
            ShowToast(said ? Lang.T("已复制这段内容", "Copied to clipboard") : Lang.T("已复制到剪贴板", "Copied to clipboard"));
            Render();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            e = LogicalArgs(e);
            // 双击 = 打开原图，它**不该顺带复制**。
            // 双击在 Windows 上是两次完整的按下-松开，所以上面那段单击复制会先跑一次；
            // 这里把拖动状态清掉，让**跟着来的那次 MouseUp** 认不出"单击"，于是不会再复制第二遍。
            // （不清的话：双击 = 复制两次 + 打开一次 + 两条提示，很闹。）
            _maybeDrag = false; _dragIndex = -1; _holdIndex = -1;
            int hh = HitTest(e.Location);
            // 1.3.0：文字格双击 = 用记事本打开那段字；文件格双击 = 打开那个文件（都走 EnsureFile / FilePath）
            if (hh >= 0)
            {
                // 0.6.0：双击缩略图 = 用系统默认看图程序**打开原图**（用户要求）。
                // 图可能只在内存里（没开存盘），先 EnsureFile 落一份到磁盘再打开。
                try
                {
                    string fpath = _store.EnsureFile(_store.Items[hh]);
                    if (!string.IsNullOrEmpty(fpath) && System.IO.File.Exists(fpath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fpath) { UseShellExecute = true });
                        return;      // 打开了就别再顺手复制一次
                    }
                }
                catch { }
                // 拿不到文件（或打开失败）就退回"复制"：直接复用单击那条路，别在这儿再写一遍
                // —— 文字格复制的是字、文件格复制的是文件本身（1.3.0），这段逻辑只该有一个地方。
                CopyItemAt(hh);
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
                ShowToast(Lang.T("管理员模式：拖拽被 Windows 拦着（托盘右键 → 管理员模式说明）", "Administrator mode: Windows blocks dragging (tray menu -> admin mode)"));
                return;
            }
            _adminTipShown = true;
            ShowToast(Lang.T("管理员模式：拖不出去，是 Windows 拦的", "Administrator mode: cannot drag out - Windows blocks it"));
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
            //（文字格 / 文件格没有位图，只能给文件格式 —— 别塞一个空的 Bitmap 格式进去误导接收方）
            if (it.Image != null)
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
            _dragLift = 0f;
            if (taken)
            {
                // 拖出去是 Copy 语义：默认**留一份**在环上（随时能再拖一次，或拖给第二个窗口）。
                // 想要"拖出去即从环上移走"的话，设置第 1 页那个开关关掉即可。
                Usage.Ev("DragOut", _settings.KeepAfterDragOut ? "留一份" : "移走");
                // 先摆反馈（用删除/合拢之前的下标算位置），再决定格子去留
                BeginDragOutFeedback(index, it);
                if (_settings.KeepAfterDragOut)
                {
                    ShowToast(Lang.T("已拖出（环上还留着一份）", "Dragged out (a copy stays in the ring)"));
                }
                else
                {
                    _store.Items.Remove(it);
                    _thumbCache.Remove(it);
                    _enterT0.Remove(it);
                    _scales.Clear();
                    // 空位**慢慢合拢**，不是"啪"地一跳：复用删除那条动画（ItemPhi 会把后面每格往前推 _phiShift）
                    if (index >= 0)
                    {
                        _phiShift = StepRad();
                        _delShiftFrom = index;
                    }
                    if (_targetOffset > MaxOffset()) _targetOffset = MaxOffset();
                    if (_targetOffset < MinOffset()) _targetOffset = MinOffset();
                    if (_offset > _targetOffset) _offset = _targetOffset;
                }
            }
            else if (returned)
            {
                // 拖回轮盘：和刚截完图一样，重新播一次缩略图滑入动画
                _scales.Remove(_store.Items.IndexOf(it));
                MarkNew(it);
                FollowNewest();
                ShowToast(Lang.T("已放回「", "Put back into \"") + _mgr.ActiveWheel.Name + "」");
            }
            _hover = -1;
            Render();
        }

        // 松手之后那一下反馈：一道**向外**的短促拖痕；留一份模式下那一格还会**颤一下 + 短暂高亮**。
        //   · 方向是"格子 → 松手那一刻的指针"，所以拖到哪儿痕迹就朝哪儿 —— 一眼看出"送出去了"。
        //   · 留一份模式下**格子不合拢**（图本来就没走），这就是它和"移走"最要紧的区别。
        //   · 必须在移除 StoreItem **之前**调用（下标还要用）。
        void BeginDragOutFeedback(int index, StoreItem it)
        {
            try
            {
                PointF a = (index >= 0 && index < _store.Items.Count) ? ItemCenter(index) : ItemCenterAtPhi((_phiMin + _phiMax) / 2f);
                PointF b = ToLogicalPt(PointToClient(Cursor.Position));
                _dragTrailA = a; _dragTrailB = b;
                _dragTrailT = 0f; _dragTrailAt = DateTime.Now;
                if (_settings.KeepAfterDragOut)
                {
                    _dragPulseIdx = index; _dragPulseT = 0f; _dragPulseAt = DateTime.Now;
                }
                else
                {
                    _dragPulseIdx = -1; _dragPulseT = 1f;      // 格子都没了，没什么可颤的
                }
            }
            catch { _dragTrailT = 1f; _dragPulseT = 1f; _dragPulseIdx = -1; }
        }


        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _forceDraw = true;      // 滚轮翻图要立刻动，别等下一 tick
            _lastActive = DateTime.Now;
            _targetOffset -= e.Delta / 120;
            if (_targetOffset < MinOffset()) _targetOffset = MinOffset();
            if (_targetOffset > MaxOffset()) _targetOffset = MaxOffset();
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
            // 诊断模式一律不用缓存：元素的"名字 + 矩形"是它们画自己的时候记下来的，
            // 贴缓存就等于那一层根本没画，于是那层里的元素在诊断图上会整个消失 ——
            // 而诊断图恰恰是要拿来看"有哪些元素"的。
            if (_settings.DiagMode) return false;
            if (_show < 0.999f || _intro || _collapsing || _showAnimating) return false;
            if (_deletingItem != null || _dragOutItem != null || _dropActive) return false;
            // 拖放态正在淡入淡出时也不能贴缓存 —— 那时 _dropActive 可能已经是 false，
            // 但环上的光晕还在往回退（用户报的"复原没有过渡"就是它）。
            if (_dropVis > 0.001f) return false;
            // 拖出去的反馈（v1.0）：那道向外拖痕和"颤一下 + 高亮"都画在卡片层里，
            // 每帧都不一样 —— 不排掉就会冻在缓存位图里，看起来像"闪一下就不动了"。
            if (_dragTrailT < 1f || _dragPulseT < 1f || _dragLift > 0f) return false;
            // v1.0 的气氛层：微光在冷却、涟漪在扩散的时候，两层都不能用缓存
            // （它们都是"每帧都在变"的东西，冻在缓存位图里看起来就是"闪一下就不动了"）。
            if (which == 0 && (FreshActive() || _rippleT < 1f)) return false;
            if (_switchFlash > 0.01f) return false;
            // 玻璃底正在交叉淡入（换底后的那 0.38 秒）时，控件层绝不能用缓存：
            // 万能键玻璃盘 / 两个圆按钮 / 名字药丸 / 把手 的模糊底是**烤进这一层位图**里的，
            // 贴缓存 = 这几处玻璃整整 0.38 秒一动不动，等下一次签名变化（用户一悬停）再整块跳过去
            // —— 实测 22978 个"受换底影响的像素"里有 18342 个（80%）就没跟着淡，一戳签名直接跳到 0.91。
            // 宁可这 0.38 秒每帧老实重画（这一档的耗时本来就在 perf 基准里单独计），也不要那种"啪"的一跳。
            if (which == 1 && _backdropOld != null && _backdropFade < 0.999f) return false;
            if (which == 0) return true;
            // 控件层：任何"动着的 / 按下的 / 弹着的"状态都不用缓存
            // （提示条**不在这一层里**了 —— v1.0 起它画在所有常驻元素之上，见 61-WheelForm.Draw.cs
            //   的图层说明。所以它不再影响这一层能不能用缓存，这里也就不必再为它禁用缓存。）
            if (_delConfirm || _menuOpen || _menuT > 0.001f) return false;
            if (_closeDown > 0.01f || _closeHover || _closePend || _closeHoldP > 0.001f || _closeLong) return false;
            if (_gearDown > 0.01f || _gearHover || _gearPend) return false;
            if (_shootDown > 0.01f || _shootHover || _shootPend) return false;
            if (_keyDown || _keyHover) return false;
            if (_nameHover) return false;
            if (_nubAppearT < 0.999f || _nubHov > 0.01f || _nubOutHover || _nubInHover) return false;
            // 把手旁边那句"点我展开/收起"也画在这一层里，所以它正在淡入淡出的时候同样不能用缓存 ——
            // 漏掉这一条的效果是：提示要么整段冻在层里不动，要么等层签名变化时"啪"地跳出来。
            // 用户反馈的"开始、展开和收起的时候提示都没有过渡"里，另一半原因就是它。
            if (_nubHintT > 0.001f && _nubHintT < 0.999f) return false;
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
            // 玻璃底的"第几代"也要进签名：换了一张完全不同的桌面之后，这一层里烤的旧底
            // 就没用了 —— 只靠"淡入期间禁缓存"能保证过程对，但淡入一结束签名又会重新匹配上，
            // 于是把**旧底那一版**整块贴回来（等于换底白换了）。代次一变，层必须重画。
            h = Mix(h, _backdropGen);
            // 悬停/按下的**进度值**也必须进签名：按钮的反馈是"渐变"出来的（_keyHov/_closeDown…），
            // 只看那几个 bool 的话，过渡期间会一直贴旧层 —— 表现就是"鼠标放上去没反应"。
            h = Mix(h, (double)_keyHov); h = Mix(h, (double)_keyT);
            h = Mix(h, (double)_closeDown); h = Mix(h, (double)_gearDown); h = Mix(h, (double)_shootDown);
            h = Mix(h, (double)_closeHoldP); h = Mix(h, (double)_nubHov);
            h = Mix(h, (double)_nubAppearT); h = Mix(h, (double)_nubHintT);
            h = Mix(h, (double)_nubOutDist); h = Mix(h, (double)_nubInDist);
            // 小按钮的发光进度：不进签名的话会一直贴旧层，光就动不起来
            h = Mix(h, (double)_closeGlow); h = Mix(h, (double)_gearGlow); h = Mix(h, (double)_shootGlow);
            if (which == 0)
            {
                h = Mix(h, (double)EffR());
                h = Mix(h, _store.Items.Count);
                // 拖出去的反馈都画在这一层里：拖痕、那一格的抖动/高亮、以及"提起来"的缩放。
                // 和上面 _keyHov 那组同理 —— 只进 bool 不进进度值的话，过渡期间会一直贴旧层。
                h = Mix(h, (double)_dragTrailT); h = Mix(h, (double)_dragPulseT);
                h = Mix(h, (double)_dragLift); h = Mix(h, _dragPulseIdx);
                h = Mix(h, (long)Math.Round(_dragTrailA.X)); h = Mix(h, (long)Math.Round(_dragTrailA.Y));
                h = Mix(h, (long)Math.Round(_dragTrailB.X)); h = Mix(h, (long)Math.Round(_dragTrailB.Y));
                // v1.0：环的厚度跟着数量走（已经由 Items.Count 覆盖），涟漪和微光各自一份进度
                h = Mix(h, (double)_rippleT);
                h = Mix(h, (double)FreshGlowSum());
                h = Mix(h, (double)_dropVis);      // 环上那圈拖放光晕的淡入淡出
                return h;
            }
            h = Mix(h, _store.Items.Count);                       // 计数胶囊
            // 滚动位置也必须进签名：计数胶囊显示的是「当前视口最上面那张 / 总数」，
            // 只放数量的话，滚轮滚动时这一层不会重建 —— 数字就一直停在原处，
            // 只有数量变了才整块刷新（用户反馈的正是这个：滚轮滚 wheel 数字不变）。
            // 量化到 0.25 格：滚动时以 4 帧的分辨率更新，够跟手又不会每像素都重建。
            h = Mix(h, (long)Math.Round(_offset * 4));
            h = Mix(h, (long)Math.Round(_phiShift * 1000));   // 删除后的滑动过渡必须进签名，否则贴的是旧位置的缓存层
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
    // 诊断模式：轮盘上每个元素都画出**自己的名字和边框**。
    //
    // 为什么要有这个东西：
    //   用户报「某个地方很生硬」的时候，我这边拿到的只有一句文字描述，
    //   而界面上有十几个长得差不多的元素 —— 三个圆按钮、名字药丸、计数胶囊、
    //   提示条、空态提示、两个把手、缩略图、万能键、环……
    //   于是我只能在代码里找一个"看起来像是问题"的硬切，改完发版，来回三次。
    //
    //   打开它、截一张图发过来，就不用再猜了。
    //
    // 做法上有一条是刻意的：**名字和矩形是元素在画自己的时候顺手记下来的**，
    // 而不是我另写一份几何去推算。后者迟早会和真实绘制对不上 ——
    // 那正是这个项目反复踩过的「度量与绘制不同源」（反例 #1）。
    partial class WheelForm
    {
        readonly List<KeyValuePair<string, RectangleF>> _diag =
            new List<KeyValuePair<string, RectangleF>>();

        Font _diagFont;

        // 元素在画自己的时候调一下（不占任何开销：关着的时候第一行就返回）
        void Diag(string name, RectangleF r)
        {
            if (_settings == null || !_settings.DiagMode) return;
            if (r.Width <= 0.5f || r.Height <= 0.5f) return;
            _diag.Add(new KeyValuePair<string, RectangleF>(name, r));
        }

        void DiagClear()
        {
            if (_settings != null && _settings.DiagMode) _diag.Clear();
        }

        // 画在最后（主通道，不进缓存层）
        void DrawDiag(Graphics g)
        {
            if (_settings == null || !_settings.DiagMode || _diag.Count == 0) return;

            if (_diagFont == null) _diagFont = new Font("Microsoft YaHei UI", 7f, FontStyle.Bold);
            Font f = _diagFont;

            // 鼠标下的元素：高亮 + 把名字顶在最上面
            Point mp = ToLogicalPt(PointToClient(Cursor.Position));
            int hot = -1;
            for (int i = _diag.Count - 1; i >= 0; i--)        // 从后往前 = 从上层往下层找
                if (_diag[i].Value.Contains(mp)) { hot = i; break; }

            SmoothingMode oldSm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                for (int i = 0; i < _diag.Count; i++)
                {
                    if (i == hot) continue;
                    RectangleF r = _diag[i].Value;
                    using (Pen p = new Pen(Color.FromArgb(150, 0, 220, 220), 1f))
                        g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                    DrawTag(g, f, _diag[i].Key, r, Color.FromArgb(190, 0, 90, 100));
                }

                if (hot >= 0)
                {
                    RectangleF r = _diag[hot].Value;
                    using (Pen p = new Pen(Color.FromArgb(255, 255, 200, 0), 2f))
                        g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                    DrawTag(g, f, "★ " + _diag[hot].Key, r, Color.FromArgb(235, 190, 120, 0));
                }

                // 左上角一行小字：怎么用
                using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 255, 230, 120)))
                    g.DrawString(Lang.T("诊断模式：鼠标停在哪，那个元素就高亮并报出自己的名字。截图发我即可。",
                                        "Diagnostics: hover an element to highlight it and show its name. Send me a screenshot."),
                                 f, b, 14f, 12f);
            }
            finally { g.SmoothingMode = oldSm; }
        }

        // 名字标签：优先贴在框的右边，贴不下就放里面 —— 别跑出窗口
        void DrawTag(Graphics g, Font f, string text, RectangleF r, Color bg)
        {
            SizeF ts = g.MeasureString(text, f);
            float w = ts.Width + 6f, h = ts.Height + 2f;
            float x = r.Right + 3f, y = r.Top;
            if (x + w > LogicalSize().Width - 2f) x = Math.Max(2f, r.X - w - 3f);
            if (y + h > LogicalSize().Height - 2f) y = Math.Max(2f, r.Bottom - h);

            using (SolidBrush b = new SolidBrush(bg))
                g.FillRectangle(b, x, y, w, h);
            using (SolidBrush tb = new SolidBrush(Color.White))
                g.DrawString(text, f, tb, x + 3f, y + 1f);
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
    // ==================== 弧线设计语言（0.6.0） ====================
    // 用户的要求：轮盘上的东西别再"横平竖直"地摆 ——
    //   · 胶囊（wheel 名、"3 / 8" 计数）要**随弧弯成圆弧**、靠着 wheel 摆，文字也要沿弧排；
    //   · 截图 / 设置 / 收起 三个小按钮要**围绕万能键的左上方、沿一段弧**排布。
    //
    // 坐标换算和 WheelForm.ItemCenterAtPhiRadius 是同一套：以屏幕角为圆心，phi 是极角，
    // Sx()/Sy() 决定"角在哪一边"（右下角时两者都是 -1），所以工具函数都收 sx/sy，
    // 画出来的东西自动跟着四个角走。
    static class ArcUi
    {
        // 极坐标 → 屏幕坐标
        public static PointF Polar(PointF c, float sx, float sy, float phi, float r)
        {
            return new PointF((float)(c.X + r * Math.Cos(phi) * sx),
                              (float)(c.Y + r * Math.Sin(phi) * sy));
        }

        // 该点的**向外单位法线**（屏幕坐标）
        public static PointF Outward(PointF c, float sx, float sy, float phi)
        {
            PointF a = Polar(c, sx, sy, phi, 1f);
            float dx = a.X - c.X, dy = a.Y - c.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6f) return new PointF(0, -1);
            return new PointF(dx / len, dy / len);
        }

        // 该点处"phi 增大"方向的单位切线
        static void Tangent(PointF c, float sx, float sy, float phi, float r, out float tx, out float ty)
        {
            PointF p1 = Polar(c, sx, sy, phi + 0.004f, r), p0 = Polar(c, sx, sy, phi - 0.004f, r);
            tx = p1.X - p0.X; ty = p1.Y - p0.Y;
            float l = (float)Math.Sqrt(tx * tx + ty * ty);
            if (l > 1e-6f) { tx /= l; ty /= l; }
            else { tx = 0; ty = 0; }
        }

        // 弧形胶囊的轮廓：沿半径 r、从 a0 扫到 a1（弧度）、厚 h，两端半圆头。
        // 多边形近似（外/内弧每 ~2° 一点、端帽每 ~10° 一点）——比拿 GraphicsPath 拼两段弧加两个半圆稳得多。
        public static GraphicsPath Capsule(PointF c, float sx, float sy, float r, float h, float a0, float a1)
        {
            List<PointF> pts = new List<PointF>();
            float rOut = r + h / 2f, rIn = r - h / 2f;
            int seg = Math.Max(6, (int)(Math.Abs(a1 - a0) * 180.0 / Math.PI / 1.0));   // 每 1 度一个点：更圆润

            for (int i = 0; i <= seg; i++)                       // 外弧 a0 -> a1
                pts.Add(Polar(c, sx, sy, a0 + (a1 - a0) * i / seg, rOut));
            AddCap(pts, c, sx, sy, a1, r, h, true);              // a1 端帽：外点 -> 切线 -> 内点
            for (int i = seg; i >= 0; i--)                       // 内弧 a1 -> a0
                pts.Add(Polar(c, sx, sy, a0 + (a1 - a0) * i / seg, rIn));
            AddCap(pts, c, sx, sy, a0, r, h, false);             // a0 端帽：内点 -> 切线 -> 外点

            GraphicsPath p = new GraphicsPath();
            if (pts.Count >= 3) p.AddPolygon(pts.ToArray());
            return p;
        }

        // 端帽半圆：以"弧中线上那个点"为圆心、h/2 为半径，从外点经切线点转到内点（atEnd=true 时）；
        // 另一头则反过来。两端点本身已经由弧给出，这里只补中间的过渡点。
        static void AddCap(List<PointF> pts, PointF c, float sx, float sy, float a, float r, float h, bool atEnd)
        {
            PointF mid = Polar(c, sx, sy, a, r);
            PointF outP = Polar(c, sx, sy, a, r + h / 2f);
            float ux = (outP.X - mid.X) / (h / 2f), uy = (outP.Y - mid.Y) / (h / 2f);   // 单位半径方向
            float tx, ty;
            Tangent(c, sx, sy, a, r, out tx, out ty);
            if (tx == 0 && ty == 0) { tx = -uy; ty = ux; }

            const int steps = 14;
            for (int i = 1; i < steps; i++)
            {
                // 参数 t：0 = 外点、π/2 = 切线外推点、π = 内点
                double t = atEnd ? (Math.PI * i / steps) : (Math.PI * (steps - i) / steps);
                float ct = (float)Math.Cos(t), st = (float)Math.Sin(t);
                // 端帽必须凸向弧继续往外的那一侧：a1 端（外弧终点）继续往前 = +切线；
                // a0 端（内弧终点）要往回走 = -切线。这里原来两端都用了 +切线 —— a0 端于是
                // 凸向了弧带内部、和弧带自交，填充出来就是一个缺口（用户反馈的胶囊显示异常）。
                float dir = atEnd ? 1f : -1f;
                pts.Add(new PointF(mid.X + (h / 2f) * (ux * ct + dir * tx * st),
                                   mid.Y + (h / 2f) * (uy * ct + dir * ty * st)));
            }
        }

        // 沿弧排一行字：每个字各自旋转，让字"立"在弧上（字顶朝外）。
        // charStep：每个字占的角度（弧度），文字以 midPhi 为中心左右摊开。
        public static void ArcText(Graphics g, string text, Font f, Brush br, PointF c, float sx, float sy,
                                   float r, float midPhi, float charStep)
        {
            ArcText(g, text, f, br, c, sx, sy, r, midPhi, charStep, 1f);
        }

        // tilt = 每个字按比例跟随弧线倾斜：1 = 完全贴着弧（字会躺倒），0.45 左右 = 有弧度感但仍好读。
        // 用户反馈：计数胶囊里的数字在 45 度方向上被转得太狠、看着就是显示异常。
        public static void ArcText(Graphics g, string text, Font f, Brush br, PointF c, float sx, float sy,
                                   float r, float midPhi, float charStep, float tilt)
        {
            if (string.IsNullOrEmpty(text)) return;
            int n = text.Length;
            float start = midPhi + charStep * (n - 1) / 2f;   // 起点在 phi 大侧，配合递减步进读起来才是正序
            for (int i = 0; i < n; i++)
            {
                string ch = text.Substring(i, 1);
                SizeF cs = g.MeasureString(ch, f);
                float a = start - charStep * i;   // 倒着排：第一个字落在 phi 大的一侧（屏幕左上），读起来才是正序
                PointF p = Polar(c, sx, sy, a, r);
                PointF d = Outward(c, sx, sy, a);
                // 让"字的上方向"对齐向外法线：GDI+ 里字的上方向是 -Y，旋转 θ 后指向 (sinθ, -cosθ)，
                // 令它等于 d 即得 θ = atan2(dx, -dy)
                float deg = (float)(Math.Atan2(d.X, -d.Y) * 180.0 / Math.PI) * tilt;
                GraphicsState st = g.Save();
                try
                {
                    g.TranslateTransform(p.X, p.Y);
                    g.RotateTransform(deg);
                    g.DrawString(ch, f, br, -cs.Width / 2f, -cs.Height / 2f);
                }
                finally { g.Restore(st); }
            }
        }

        // 一行字沿弧排开时每个字该占多少角度（按实测字宽 + 间距算，避免挤在一起）
        public static float StepFor(Graphics g, string text, Font f, float r, float gap)
        {
            if (string.IsNullOrEmpty(text) || r <= 1f) return 0.1f;
            float w = g.MeasureString(text, f).Width / text.Length + gap;
            return w / r;
        }
    }
}

namespace SnapWheel
{
    // 多轮盘管理窗口：左边一列子轮盘，右边是「名称 / 颜色 / 新建 / 删除 / 完成」。
    //
    // ⚠️ DPI（本次修订）：这个窗口原来**坐标、尺寸、行高全是写死的像素**，而程序是 per-monitor DPI aware 的 ——
    // 150% 缩放下字体由 GDI+ 按 DPI 放大 1.5 倍渲染，格子却还是原来那么大，于是：
    //   · 列表行高写死 26，装不下放大后的字 → 每一行的名字上下都被切（这就是用户报的「显示不全」）；
    //   · 右列排在写死的 x=412 处，窗口却是写死的 430 宽 → 「新建 / 删除 / 完成」被挤出可视区；
    //   · 底部那句提示是 AutoSize=false 的死格子 (16,276,260,22) → 一行放不下就只剩半句。
    // 现在的规矩（和 75-SettingsForm / 80-Dialogs 一模一样，**别再发明第二套**）：
    //   · 长度（坐标 / 宽 / 高 / 行高 / 边距 / 按钮尺寸）一律过 Ui.S() 乘 DPI 系数；
    //   · **字体磅值一个都不乘** —— GDI+ 已经按 DPI 渲染过一遍，再乘就是双倍放大；
    //   · 说明性文字的 Label 交给 Ui.Wrap()：自己折行、自己报 PreferredHeight，比"把高度算准"可靠；
    //   · 窗口 ClientSize **最后**按内容重算一次，内容多高窗口就多高，任何 DPI 下都不裁。
    // 版面写法保持原样：还是手写坐标 + 常量，**没有**引入 TableLayoutPanel / FlowLayoutPanel
    // （这个项目在布局重构上翻过车，见 0.4.x 那次）。这次只动"数字怎么来的"，不动"谁排在哪"。
    class WheelsForm : Form
    {
        // ---- 版面常量（逻辑像素；用的时候一律过 Ui.S() 乘 K）----
        // 这几个数字就是原来写死在代码里的那些值，原样搬过来当"逻辑像素"用 ——
        // 这样 100% 缩放下算出来的结果和改之前一模一样，改动只在高 DPI 下才生效，出问题好对账。
        const int WinW = 430, WinH = 330;   // 原 ClientSize；乘 K 之后当窗口下限
        const int PadL = 16, PadT = 16;     // 内容左上边距（原 (16,16)）
        const int PadR = 18, PadB = 32;     // 内容右下边距（原右列右沿 412 → 430-412=18；提示底 298 → 330-298=32）
        const int ListW = 240, ListH = 250; // 左侧列表（250 / 26 ≈ 9.6 行，可见行数和以前一致）
        const int ColX = 272;               // 右列起点（原写死 272）
        const int ColW = 140;               // 右列控件宽（272+140 = 412，正好顶到右边距）
        const int BoxH = 24;                // 输入框 / 下拉框的版面高（原写死 24）
        const int RowH = 26;                // 列表行高 —— **必须**过 Ui.S()，写死 26 就是"行里文字被切"的元凶
        const int BtnW = 66, BtnH = 30;     // 「新建 / 删除」小按钮（原写死 66×30）
        const int CloseH = 34;              // 「完成」按钮高（原写死 34）
        const int Lab1Y = 18, Box1Y = 40;   // 「名称」标签 / 输入框的 y（原写死）
        const int Lab2Y = 74, Box2Y = 96;   // 「颜色」标签 / 下拉框的 y（原写死）
        const int BtnY = 136;               // 「新建 / 删除」那一行的 y（原写死）
        const int CloseY = 178;             // 「完成」的 y（原写死）
        const int GapTip = 10;              // 列表底 → 提示文字的间距（原 266 → 276）

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
            Text = Lang.T("管理 Wheel", "Manage wheels");
            Icon = Brand.Get();
            Font = new Font("Microsoft YaHei UI", 9.5f);        // 磅值不动：GDI+ 已按 DPI 渲染过一遍
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = Ui.Sz(WinW, WinH);                     // 先按原尺寸占位，构造完最后按内容再算一次

            int mL = Ui.S(PadL), mT = Ui.S(PadT), mR = Ui.S(PadR), mB = Ui.S(PadB);
            int right = Ui.S(WinW) - mR;                        // 右列右沿（原 412），右列控件全部贴它对齐

            _list = new ListBox();
            _list.Bounds = new Rectangle(mL, mT, Ui.S(ListW), Ui.S(ListH));
            _list.IntegralHeight = false;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = Ui.S(RowH);                      // ← 跟着 K 走：不然 150% 下每行的字上下都被切
            _list.DrawItem += new DrawItemEventHandler(OnDrawItem);
            _list.SelectedIndexChanged += new EventHandler(OnSelect);
            Controls.Add(_list);

            // 「名称」「颜色」是单行短标签：原来给的是写死的 60×22 死格子，靠"字比格子小"侥幸没出事。
            // 改成 AutoSize 之后宽度/高度都由字自己报，换一档缩放、换一种字体都不用再回来调数字。
            Label l1 = new Label();
            l1.Text = Lang.T("名称", "Name");
            l1.Location = Ui.Pt(ColX, Lab1Y);
            Ui.OneLine(l1);
            Controls.Add(l1);

            _name = new TextBox();
            _name.Bounds = new Rectangle(Ui.S(ColX), Ui.S(Box1Y), Ui.S(ColW), Ui.S(BoxH));
            _name.TextChanged += new EventHandler(OnNameChanged);
            Controls.Add(_name);

            Label l2 = new Label();
            l2.Text = Lang.T("颜色", "Colour");
            l2.Location = Ui.Pt(ColX, Lab2Y);
            Ui.OneLine(l2);
            Controls.Add(l2);

            _color = new ComboBox();
            _color.DropDownStyle = ComboBoxStyle.DropDownList;
            _color.Bounds = new Rectangle(Ui.S(ColX), Ui.S(Box2Y), Ui.S(ColW), Ui.S(BoxH));
            for (int i = 0; i < Palette.Names.Length; i++) _color.Items.Add(Palette.Names[i]);
            _color.SelectedIndexChanged += new EventHandler(OnColorChanged);
            Controls.Add(_color);

            RoundButton add = new RoundButton();
            add.Text = Lang.T("新建", "New"); add.Size = Ui.Sz(BtnW, BtnH);
            add.Fill = Color.FromArgb(0, 122, 204); add.FillHover = Color.FromArgb(0, 140, 232);
            add.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);   // 磅值不动
            add.Location = Ui.Pt(ColX, BtnY);
            add.Click += new EventHandler(delegate(object o, EventArgs e2) { _mgr.New(); _mgr.Save(); Reload(_mgr.Wheels.Count - 1); });
            Controls.Add(add);

            RoundButton del = new RoundButton();
            del.Text = Lang.T("删除", "Delete"); del.Size = Ui.Sz(BtnW, BtnH);
            del.Fill = Color.FromArgb(214, 70, 84); del.FillHover = Color.FromArgb(230, 90, 104);
            del.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);   // 磅值不动
            // 右对齐到列尾：原来写死 346（= 272+140-66）也对，但那样是"数值凑巧对"，
            // 用 ColX+ColW-BtnW 表达之后，改列宽/改按钮宽都不会再跑偏。
            del.Location = Ui.Pt(ColX + ColW - BtnW, BtnY);
            del.Click += new EventHandler(delegate(object o, EventArgs e2) {
                if (_list.SelectedIndex < 0) return;
                if (MessageBox.Show(Lang.T("确定删除 Wheel「", "Delete wheel \"") + _mgr.Wheels[_list.SelectedIndex].Name + Lang.T("」及其截图？", "\" and its screenshots?"),
                        Lang.T("删除 Wheel", "Delete wheel"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                _mgr.Remove(_list.SelectedIndex); _mgr.Save(); Reload(Math.Min(_list.SelectedIndex, _mgr.Wheels.Count - 1));
            });
            Controls.Add(del);

            RoundButton close = new RoundButton();
            close.Text = Lang.T("完成", "Done"); close.Size = Ui.Sz(ColW, CloseH);   // 宽度 = 整列宽（原写死 140，正好等于 ColW）
            close.Fill = Color.FromArgb(233, 234, 238); close.FillHover = Color.FromArgb(222, 224, 230);
            close.TextColor = Color.FromArgb(58, 60, 66);
            close.Font = new Font("Microsoft YaHei UI", 9.5f);      // 磅值不动
            close.Location = Ui.Pt(ColX, CloseY);
            close.Click += new EventHandler(delegate(object o, EventArgs e2) { _mgr.Save(); DialogResult = DialogResult.OK; Close(); });
            Controls.Add(close);

            Label tip = new Label();
            tip.Text = Lang.T("点一下左侧即可切换为当前 Wheel", "Click one on the left to make it the active wheel");
            tip.ForeColor = Color.FromArgb(150, 150, 158);
            // 原来是 AutoSize=false 的死格子 (16,276,260,22)：一行放不下就只剩半句。现在交给 Wrap()，
            // 让它自己折行、自己报高度。竖直位置也不再写死 276，而是吊在"列表和右列谁更低"的下面 ——
            // 原来 276 是靠"右列肯定比列表矮"这个巧合才没被压住，右列被 DPI 撑高之后这个巧合就没了。
            tip.Location = new Point(mL, Math.Max(_list.Bottom, close.Bottom) + Ui.S(GapTip));
            Ui.Wrap(tip, right - mL);                           // 可用宽 = 窗口内容宽（已乘过 K 的物理像素）
            Controls.Add(tip);

            // 窗口尺寸**最后**按内容重算一次（原来是写死的 430×330）：内容多高窗口就多高，绝不裁。
            // 再和"原尺寸 × K"取大值 —— 这是下限，保证 100% 缩放下和改之前完全一样，不会看着变小。
            int needW = Math.Max(_list.Right, Math.Max(close.Right, tip.Right)) + mR;
            int needH = tip.Bottom + mB;
            ClientSize = new Size(Math.Max(Ui.S(WinW), needW), Math.Max(Ui.S(WinH), needH));

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

        // 自绘列表的每一行：色点 + 名字。
        // 自绘里的常量**同样**要过 Ui.S()：行高已经按 DPI 放大了，色点 12 / 偏移 6 / 文字 26 却写死的话，
        // 150% 下色点会缩在行左上角、文字也比行高大 —— 上下被切。
        // 竖直位置干脆不再写死魔数，改成"在行高里居中"：行高、字体、缩放任一个变了都自动跟着走。
        void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            Wheel w = _mgr.Wheels[e.Index];
            int d = Ui.S(12);                                   // 色点直径（原写死 12）
            int ey = e.Bounds.Top + Math.Max(0, (e.Bounds.Height - d) / 2);        // 原写死 +7
            using (SolidBrush b = new SolidBrush(w.Accent))
                e.Graphics.FillEllipse(b, e.Bounds.Left + Ui.S(6), ey, d, d);      // 原写死 +6
            int ty = e.Bounds.Top + Math.Max(0, (e.Bounds.Height - e.Font.Height) / 2);   // 原写死 +5
            using (SolidBrush t = new SolidBrush(e.ForeColor))
                e.Graphics.DrawString(_list.Items[e.Index].ToString(), e.Font, t, e.Bounds.Left + Ui.S(26), ty);  // 原写死 +26
        }
    }

    // 改名字的小窗口（点轮盘上的名字药丸弹出来的那个）。
    //
    // ⚠️ DPI（本次修订）：原来 ClientSize 写死 360×128、输入框写死 250 宽、两个按钮写死 104/90×34、
    // 按钮位置还用 ClientSize 现算（width-24-104 / height-46）—— 150% 下字体放大 1.5 倍之后：
    //   · 上面那行说明文字会顶出窗口右边缘（AutoSize 只保证"不裁字"，不保证"不出界"，得给它折行上限）；
    //   · 输入框和按钮挤在一起、按钮被窗口下沿裁掉半截。
    // 规矩和 WheelsForm 完全一致：长度过 Ui.S()、字体磅值不动、文字用 Wrap()、窗口尺寸最后按内容重算。
    class RenameForm : Form
    {
        public const int MaxName = 12;      // 名字最长 12 个字（药丸宽度可控）
        public string Value = "";
        public int Takes = Wheel.TakesAny;  // 这个环收什么（0 什么都收 / 1 只收图片 / 2 只收文字）
        TextBox _box;
        ComboBox _cmbTakes;

        // ---- 版面常量（逻辑像素；用的时候一律过 Ui.S() 乘 K）----
        const int WinW = 360, WinH = 128;   // 原 ClientSize；乘 K 之后当窗口下限
        const int PadL = 22, PadR = 24, PadT = 18;
        const int LabDX = 2;                // 输入框相对说明文字右挪 2px（原来 22 → 24，视觉上更好对齐标题）
        const int BoxW = 250;               // 输入框宽（原写死 250）
        const int GapBox = 9;               // 说明文字下沿 → 输入框（原来 18+17 的文字底 → 44）
        const int GapBtn = 14;              // 输入框下沿 → 按钮行（原来 68 → 82）
        const int GapEnd = 12;              // 按钮下沿 → 窗口下沿（原来 116 → 128）
        const int OkW = 104, CancelW = 90;  // 两个按钮宽（原写死）
        const int BtnH = 34;                // 按钮高（原写死）
        const int BtnGap = 10;              // 两个按钮之间（原写死 10）

        public RenameForm(string cur, int takes)
        {
            Text = Lang.T("给这个 Wheel 起个名", "Name this wheel");
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);        // 磅值不动：GDI+ 已按 DPI 渲染过一遍
            BackColor = Color.FromArgb(250, 250, 252);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = Ui.Sz(WinW, WinH);                     // 先按原尺寸占位，最后按内容再算一次

            int mL = Ui.S(PadL), mR = Ui.S(PadR), mT = Ui.S(PadT);
            int winW = Ui.S(WinW);
            int right = winW - mR;                              // 内容右沿，按钮全部贴它对齐

            Label l = new Label();
            l.Text = Lang.T("名字（最多 ", "Name (up to ") + MaxName + Lang.T(" 个字，会显示在轮盘上）", " characters, shown on the ring)");
            l.ForeColor = Color.FromArgb(110, 114, 124);
            l.Location = new Point(mL, mT);
            Ui.Wrap(l, right - mL);      // 给折行上限：光 AutoSize 的话，字比窗口宽就直接伸出窗口被裁掉
            Controls.Add(l);

            _box = new TextBox();
            _box.Text = cur;
            _box.Font = new Font("Microsoft YaHei UI", 11f);    // 磅值不动
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.Width = Ui.S(BoxW);                            // 高度是字体驱动的，不用管也别写死
            _box.MaxLength = MaxName;      // 直接限制输入长度，避免打到超长
            // 位置按"说明文字的真实下沿"往下推，不写死 44：文字因缩放换了一档、或者多折了一行，
            // 都不会压到输入框上（写死 44 时这两件事任意一件发生就会重叠）。
            _box.Location = new Point(mL + Ui.S(LabDX), l.Bottom + Ui.S(GapBox));
            Controls.Add(_box);

            // 这个环收什么（§4.2 那份声明里的 takes）。跟名字挤在同一个窗口里是有意的：
            // "这个环是干什么的"和"它收什么"是同一件事的两半，分成两处设置就没人找得到。
            Label lt = new Label();
            lt.Text = Lang.T("这个环收什么", "What does this ring take?");
            lt.ForeColor = Color.FromArgb(110, 114, 124);
            lt.Location = new Point(mL, _box.Bottom + Ui.S(GapBtn));
            Controls.Add(lt);

            _cmbTakes = new ComboBox();
            _cmbTakes.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbTakes.Font = new Font("Microsoft YaHei UI", 10f);   // 磅值不动
            _cmbTakes.Width = Ui.S(BoxW);
            _cmbTakes.Items.Add(Lang.T("什么都收", "Anything"));
            _cmbTakes.Items.Add(Lang.T("只收图片", "Images only"));
            _cmbTakes.Items.Add(Lang.T("只收文字", "Text only"));
            // ⚠️ 选项顺序**就是** takes 的取值顺序（0/1/2），SelectedIndex 直接当 takes 用；
            // 以后要加档位，必须同步 src/30-Wheel.cs 的 TakesAny/TakesImage/TakesText 和 Accepts()。
            _cmbTakes.SelectedIndex = (takes >= Wheel.TakesAny && takes <= Wheel.TakesText) ? takes : Wheel.TakesAny;
            _cmbTakes.Location = new Point(mL + Ui.S(LabDX), lt.Bottom + Ui.S(GapBox));
            Controls.Add(_cmbTakes);

            int yBtn = _cmbTakes.Bottom + Ui.S(GapBtn);              // 同理：按钮行吊在最后一行控件下面，不写死

            RoundButton ok = new RoundButton();
            ok.Text = Lang.T("改好了", "Renamed");
            ok.Size = Ui.Sz(OkW, BtnH);
            ok.Fill = Color.FromArgb(0, 122, 204);
            ok.FillHover = Color.FromArgb(0, 140, 232);
            ok.TextColor = Color.White;
            ok.Primary = true;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);   // 磅值不动
            // 右对齐到内容右沿：原来那版用的是"当前 ClientSize - 24 - 104"，
            // 而 ClientSize 这时还没按内容重算，等于拿旧尺寸定位 —— 改成用目标窗口宽算，稳。
            ok.Location = new Point(right - ok.Width, yBtn);
            ok.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                Value = _box.Text.Trim();
                if (Value == null || Value.Length == 0) Value = cur;
                if (Value.Length > MaxName) Value = Value.Substring(0, MaxName);   // 名字太长会把药丸撑宽、挡住旁边
                Takes = _cmbTakes.SelectedIndex;
                DialogResult = DialogResult.OK;
                Close();
            });
            Controls.Add(ok);

            RoundButton cancel = new RoundButton();
            cancel.Text = Lang.T("取消", "Cancel");
            cancel.Size = Ui.Sz(CancelW, BtnH);
            cancel.Fill = Color.FromArgb(234, 235, 240);
            cancel.FillHover = Color.FromArgb(222, 224, 230);
            cancel.TextColor = Color.FromArgb(58, 60, 66);
            cancel.Font = new Font("Microsoft YaHei UI", 10f);  // 磅值不动
            cancel.Location = new Point(ok.Left - Ui.S(BtnGap) - cancel.Width, yBtn);
            cancel.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            // 窗口尺寸**最后**按内容重算一次（原来是写死的 360×128）：内容多宽多高，窗口就多大，绝不裁。
            // 同样和"原尺寸 × K"取大值当下限 —— 100% 缩放下和改之前一致，高 DPI 下只可能变大、不会裁。
            // 说明文字不用参与：它的宽度已经被上面的 Wrap(right-mL) 卡死在内容区里，不可能顶出窗口
            int needW = Math.Max(_box.Right, Math.Max(ok.Right, cancel.Right)) + mR;
            ClientSize = new Size(Math.Max(winW, needW), Math.Max(Ui.S(WinH), ok.Bottom + Ui.S(GapEnd)));
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
    // 设置窗口的四个页面构建（从 75-SettingsForm.cs 拆出来，纯搬移，行为不变）。
    // 拆的理由：主文件原本 1607 行，页面构建占了 462 行，改一处要翻很久。
    // partial class 让一个类写在多个文件里，编译时合并。
    partial class SettingsForm
    {
        // ---- 第 1 页：行为与快捷键 ----
        void BuildPage1()
        {
            TableLayoutPanel g = _pages[0];
            SetupRows(g, 11);   // 0.6.0：多了Lang.T("拖出后保留一份", "Keep a copy after drag-out")；1.3.0：多了"移进来"
            Settings s = _s;

            g.Controls.Add(Section(Lang.T("行为", "Behaviour")), 0, 0);
            g.Controls.Add(Section(Lang.T("快捷键与操作", "Shortcuts")), 1, 0);

            _chkDisk = new CheckBox();
            _chkDisk.AutoSize = true;
            _chkDisk.Text = Lang.T("保存到硬盘（否则只存内存，退出即清）", "Save to disk (otherwise memory only, cleared on exit)");
            _chkDisk.Checked = s.SaveToDisk;
            _chkDisk.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkDisk, 0, 1);

            _cmbHotkey = new ComboBox();
            _cmbHotkey.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbHotkey.Width = S(170);
            _cmbHotkey.Margin = new Padding(0, 6, 0, 0);
            _cmbHotkey.Items.AddRange(HotkeyUtil.Names);
            _cmbHotkey.SelectedItem = s.Hotkey;
            if (_cmbHotkey.SelectedIndex < 0) _cmbHotkey.SelectedIndex = 0;
            g.Controls.Add(Row(MkLabel(Lang.T("截图热键", "Capture hotkey")), _cmbHotkey), 1, 1);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.AutoSize = true;
            _chkAutoStart.Text = Lang.T("开机自动启动（登录后自动在后台运行）", "Start with Windows\n(runs in background after sign-in)");
            _chkAutoStart.Checked = AutoRun.IsEnabled();
            _chkAutoStart.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkAutoStart, 0, 2);

            _cmbCorner = new ComboBox();
            _cmbCorner.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCorner.Width = S(170);
            _cmbCorner.Margin = new Padding(0, 6, 0, 0);
            _cmbCorner.Items.AddRange(new object[] { Lang.T("左下角", "Bottom left"), Lang.T("右下角", "Bottom right"), Lang.T("左上角", "Top left"), Lang.T("右上角", "Top right") });
            _cmbCorner.SelectedIndex = CornerIndex(s.Corner);
            g.Controls.Add(Row(MkLabel(Lang.T("圆环位置", "Ring position")), _cmbCorner), 1, 2);

            _chkAuto = new CheckBox();
            _chkAuto.AutoSize = true;
            _chkAuto.Text = Lang.T("空闲后自动收起轮盘", "Auto-collapse the ring when idle");
            _chkAuto.Checked = s.AutoHide;
            _chkAuto.Margin = new Padding(0, 4, 0, 4);
            _numSec = Num(2, 600, s.AutoHideSeconds);
            g.Controls.Add(Row(_chkAuto, Gap(16), MkLabel(Lang.T("空闲秒数", "Idle seconds")), _numSec), 0, 3);

            _cmbDel = new ComboBox();
            _cmbDel.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDel.Width = S(170);
            _cmbDel.Margin = new Padding(0, 6, 0, 0);
            _cmbDel.Items.AddRange(new object[] { Lang.T("双击右键删除", "Double right-click to delete"), Lang.T("单击右键删除", "Right-click to delete") });
            _cmbDel.SelectedIndex = (s.DeleteMode == "single") ? 1 : 0;
            g.Controls.Add(Row(MkLabel(Lang.T("删除方式", "Delete gesture")), _cmbDel), 1, 3);

            // 轮盘贴哪条边。
            // 为什么要有这个：Windows 的"工作区"**总是**扣掉任务栏那一条 ——
            // 哪怕任务栏是自动隐藏的，也照样预留 48 像素。
            // 于是自动隐藏的用户会看到轮盘底下悬着一条看不见的空隙，像没靠到底。
            _cmbEdge = new ComboBox();
            _cmbEdge.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbEdge.Width = S(170);
            _cmbEdge.Margin = new Padding(0, 6, 0, 0);
            _cmbEdge.Items.AddRange(new object[] {
                Lang.T("自动（任务栏隐藏时贴屏幕边）", "Auto (screen edge when the taskbar auto-hides)"),
                Lang.T("贴屏幕边（会被任务栏压住一角）", "Screen edge (may sit under the taskbar)"),
                Lang.T("贴工作区边（永远避让任务栏）", "Work-area edge (always avoids the taskbar)") });
            _cmbEdge.SelectedIndex = (s.EdgeAnchor == "screen") ? 1 : (s.EdgeAnchor == "work") ? 2 : 0;
            // 放在第 8 行第 1 列：第 3、4 行都满了，第 5、6 行被"保存目录""剪贴板"横跨两列占掉。
            g.Controls.Add(Row(MkLabel(Lang.T("轮盘靠边方式", "Which edge the ring hugs")), _cmbEdge), 1, 8);

            _chkTop = new CheckBox();
            _chkTop.AutoSize = true;
            _chkTop.Text = Lang.T("总在最前（始终置顶显示）", "Always on top");
            _chkTop.Checked = s.AlwaysOnTop;
            _chkTop.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkTop, 0, 4);

            _cmbSwitch = new ComboBox();
            _cmbSwitch.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbSwitch.Width = S(170);
            _cmbSwitch.Margin = new Padding(0, 6, 0, 0);
            _cmbSwitch.Items.AddRange(new object[] { Lang.T("长按万能键弹圆盘", "Long-press the universal key for the dial"), Lang.T("长按后左右滑动", "Long-press then slide left/right") });
            _cmbSwitch.SelectedIndex = (s.SwitchMode == "swipe") ? 1 : 0;
            g.Controls.Add(Row(MkLabel(Lang.T("Wheel 切换", "Wheel switching")), _cmbSwitch), 1, 4);

            // 保存目录这一行本来就宽，横跨两列（否则两列加起来会顶破窗口宽度）
            _txtDir = new TextBox();
            _txtDir.Text = s.Dir;
            _txtDir.Width = S(300);
            _txtDir.Margin = new Padding(0, 5, 8, 0);
            Button browse = new Button();
            browse.Text = Lang.T("浏览", "Browsing");
            browse.AutoSize = true;
            browse.MinimumSize = new Size(S(60), S(26));
            browse.Margin = new Padding(0, 4, 0, 0);
            browse.Click += new EventHandler(delegate(object o, EventArgs e2) {
                FolderBrowserDialog d = new FolderBrowserDialog();
                if (d.ShowDialog() == DialogResult.OK) _txtDir.Text = d.SelectedPath;
            });
            Control dirRow = Row(MkLabel(Lang.T("保存目录", "Save folder")), _txtDir, browse);
            g.Controls.Add(dirRow, 0, 5);
            g.SetColumnSpan(dirRow, 2);

            _chkClip = new CheckBox();
            _chkClip.AutoSize = true;
            _chkClip.Text = Lang.T("复制图片后自动收进轮盘", "Collect copied images automatically");
            _chkClip.Checked = s.ClipboardImport;
            // 新勾选框：这一行的排版是量出来的，别随手改。
            // 完整提示（"要立刻粘贴时直接 Ctrl+V"）量出来是 342px，塞回左列会把第 1 页顶到 825px
            // （两列最小宽度 317 + 360 = 677，页面只有 720）—— 所以这一行改成**横跨两列**：
            // 跨列行不进任何一列的最小宽度，整行 167+6+342+6+128 = 658 ≤ 720 ✓。
            // 代价只有一个：原来在右列的气泡勾选框跟着流到本行第三个，说明括号去掉
            // （留着的话整行 762 > 720，右列会挨着裁）—— Lang.T("显示托盘气泡提示", "Show tray balloon tips")这个名字本身已经说明它是什么。
            _chkCopy = new CheckBox();
            _chkCopy.AutoSize = true;
            _chkCopy.Text = Lang.T("截图后同时复制到剪贴板（要立刻粘贴时直接 Ctrl+V）", "Also copy to the clipboard on capture\n(so Ctrl+V just works)");
            _chkCopy.Checked = s.CopyOnCapture;
            _chkCopy.Margin = new Padding(S(6), 3, 0, 3);

            _chkBalloon = new CheckBox();
            _chkBalloon.AutoSize = true;
            _chkBalloon.Text = Lang.T("显示托盘气泡提示", "Show tray balloon tips");
            _chkBalloon.Checked = s.ShowBalloon;
            _chkBalloon.Margin = new Padding(S(6), 3, 0, 3);

            Control clipRow = Row(_chkClip, _chkCopy, _chkBalloon);
            g.Controls.Add(clipRow, 0, 6);
            g.SetColumnSpan(clipRow, 2);

            // 1.3.0：拖进来的文件是「移进来」（从原地移走，可在回收站恢复）还是留下原件。
            // 跟"保存目录"那行一样横跨两列 —— 完整说明放进左列会把列宽顶破（见 :131 那段）。
            _chkMoveIn = new CheckBox();
            _chkMoveIn.AutoSize = true;
            _chkMoveIn.Text = Lang.T("拖进来的文件「移进来」（从原地移走，可在回收站恢复；不勾就只留原件）",
                                     "Move dropped files in\n(removed from where they were; recoverable from the Recycle Bin)");
            _chkMoveIn.Checked = s.MoveInOnDrop;
            _chkMoveIn.Margin = new Padding(0, 4, 0, 4);
            Control moveRow = Row(_chkMoveIn);
            g.Controls.Add(moveRow, 0, 10);
            g.SetColumnSpan(moveRow, 2);

            _chkUpdate = new CheckBox();
            _chkUpdate.AutoSize = true;
            _chkUpdate.Text = Lang.T("启动时检查有没有新版本（只提示，不自动安装）", "Check for updates on start\n(notify only, never auto-install)");
            _chkUpdate.Checked = s.CheckUpdate;
            g.Controls.Add(Row(_chkUpdate), 0, 7);

            // 拖出之后要不要在环上留一份（默认留）：拖出是 Copy 语义，留着才能再拖给别的窗口
            _chkKeep = new CheckBox();
            _chkKeep.AutoSize = true;
            _chkKeep.Text = Lang.T("缩略图拖出去后，环上保留一份（关掉就是拖出去即从环上移走）", "Keep a copy in the ring after\ndragging a thumbnail out");
            _chkKeep.Checked = s.KeepAfterDragOut;
            g.Controls.Add(Row(_chkKeep), 0, 8);

            // 界面语言（0.6.0 第一轮 i18n）：启动时生效，切换后要重启
            // 界面语言（0.6.0 第一轮 i18n）：启动时生效，切换后要重启
            // 用 FlowLayoutPanel 自动排：原来手工算 x 坐标（lbLang.PreferredWidth + 10），
            // PreferredWidth 一旦不准，标签就会压住下拉框（用户反馈的"有遮挡"）。
            Label lbLang = new Label();
            lbLang.AutoSize = true;
            lbLang.Text = Lang.T("界面语言（切换后重启生效）", "Language (restart to apply)");
            lbLang.ForeColor = Color.FromArgb(60, 64, 74);
            lbLang.Margin = new Padding(0, 6, 10, 0);
            _cbLang = new ComboBox();
            _cbLang.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbLang.Items.AddRange(new object[] { Lang.T("跟随系统", "Follow system"), Lang.T("中文", "Chinese"), "English" });
            _cbLang.SelectedIndex = (s.UiLanguage == "en") ? 2 : (s.UiLanguage == "zh" ? 1 : 0);
            _cbLang.Width = S(160);
            _cbLang.Margin = new Padding(0);
            FlowLayoutPanel langRow = new FlowLayoutPanel();
            langRow.AutoSize = true;
            langRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            langRow.FlowDirection = FlowDirection.LeftToRight;
            langRow.WrapContents = false;
            langRow.Margin = new Padding(0);
            langRow.Padding = new Padding(0);
            langRow.Controls.Add(lbLang);
            langRow.Controls.Add(_cbLang);
            g.Controls.Add(Row(langRow), 0, 9);

            _chkDragFile = new CheckBox();
            _chkDragFile.AutoSize = true;
            _chkDragFile.Text = Lang.T("拖出时同时带上\"文件\"（拖到桌面/文件夹会落地成文件）", "Attach a real file when dragging out (dropping onto the desktop / a folder writes a file)");
            _chkDragFile.Checked = s.DragOutAsFile;
            g.Controls.Add(Row(_chkDragFile), 1, 7);
        }

        // ---- 第 2 页：轮盘与外观 ----
        void BuildPage2()
        {
            TableLayoutPanel g = _pages[1];
            SetupRows(g, 8);
            Settings s = _s;

            g.Controls.Add(Section(Lang.T("外观", "Appearance")), 0, 0);

            _numMax = Num(1, 999, s.MaxCount);
            _numThumb = Num(40, 260, s.ThumbSize);
            g.Controls.Add(Row(MkLabel(Lang.T("最多保留张数", "Max items kept")), _numMax, Gap(24), MkLabel(Lang.T("缩略图大小", "Thumbnail size")), _numThumb), 0, 1);

            _chkCollapse = new CheckBox();
            _chkCollapse.AutoSize = true;
            _chkCollapse.Text = Lang.T("收起状态：缩到屏幕边上留个小把手", "Collapse into a small pull-tab\nat the screen edge");
            _chkCollapse.Checked = s.CollapseMode;
            g.Controls.Add(Row(_chkCollapse), 1, 1);

            _numPeek = Num(120, 500, s.PeekPercent);
            // 顺手把 0.5.3 的"截图后重置滚动位置"放在这一行的空处：这一行只有标签 + 数值框，
            // 右边空着一大片。**刻意不单独占一行** —— 第 2 页再加一行要多 31px，
            // 760×574（窗口允许缩到的最小尺寸）下页面格只有 346px、内容已经要 324px，加一行就顶出去被裁了。
            _chkScrollReset = new CheckBox();
            _chkScrollReset.AutoSize = true;
            _chkScrollReset.Text = Lang.T("截图后把滚动位置重置到最新那张（好让滑入动画看得见）", "Reset scroll to the newest item\nafter capture");
            _chkScrollReset.Checked = s.ResetScrollOnCapture;
            _chkScrollReset.Margin = new Padding(S(30), 4, 0, 4);
            Control peekRow = Row(MkLabel(Lang.T("长按放大(%)", "Hold-to-zoom (%)")), _numPeek, _chkScrollReset);
            g.Controls.Add(peekRow, 0, 2);
            g.SetColumnSpan(peekRow, 2);

            // 下面这几行本身就宽（标签 + 下拉 + 说明），横跨两列 —— 两列并排会顶破窗口宽度
            _cmbScale = new ComboBox();
            _cmbScale.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbScale.FlatStyle = FlatStyle.Flat;
            _cmbScale.Width = S(170);
            _cmbScale.Margin = new Padding(0, 6, 0, 0);
            _cmbScale.Items.Add(Lang.T("自动（按显示器 DPI）", "Auto (by monitor DPI)"));
            for (int i = 1; i < scVals.Length; i++) _cmbScale.Items.Add(scVals[i] + "%");
            _cmbScale.SelectedIndex = 0;
            for (int i = 0; i < scVals.Length; i++) if (scVals[i] == s.UiScale) _cmbScale.SelectedIndex = i;
            Label hint = new Label();
            hint.AutoSize = true;
            hint.Text = Lang.T("（整块轮盘等比放大，含文字和图标）", "(scales the whole ring, text and icons included)");
            hint.ForeColor = Color.FromArgb(150, 152, 160);
            hint.Margin = new Padding(0, 10, 0, 0);
            Control scaleRow = Row(MkLabel(Lang.T("界面缩放", "UI scale")), _cmbScale, Gap(12), hint);
            g.Controls.Add(scaleRow, 0, 3);
            g.SetColumnSpan(scaleRow, 2);

            // 收起 / 展开的动画速度（独立于Lang.T("动画速度", "Animation speed")）
            _cmbRing = new ComboBox();
            _cmbRing.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRing.FlatStyle = FlatStyle.Flat;
            _cmbRing.Width = S(130);
            _cmbRing.Margin = new Padding(0, 6, 0, 0);
            for (int i = 0; i < ringNames.Length; i++) _cmbRing.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            _cmbRing.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.ExpandSpeed) _cmbRing.SelectedIndex = i;
            Label hintRing = new Label();
            hintRing.AutoSize = true;
            hintRing.Text = Lang.T("（只管收起 / 展开；百分比越大越快）", "(collapse / expand only; higher = faster)");
            hintRing.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing.Margin = new Padding(0, 10, 0, 0);
            Control ringRow = Row(MkLabel(Lang.T("展开速度", "Expand speed")), _cmbRing, Gap(10), hintRing);
            g.Controls.Add(ringRow, 0, 4);
            g.SetColumnSpan(ringRow, 2);

            _cmbRing2 = new ComboBox();
            _cmbRing2.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRing2.FlatStyle = FlatStyle.Flat;
            _cmbRing2.Width = S(130);
            _cmbRing2.Margin = new Padding(0, 6, 0, 0);
            for (int i = 0; i < ringNames.Length; i++) _cmbRing2.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            _cmbRing2.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.CollapseSpeed) _cmbRing2.SelectedIndex = i;
            Label hintRing2 = new Label();
            hintRing2.AutoSize = true;
            hintRing2.Text = Lang.T("（默认比展开快一档，收起要干脆）", "(one notch faster than expand by default)");
            hintRing2.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing2.Margin = new Padding(0, 10, 0, 0);
            Control ring2Row = Row(MkLabel(Lang.T("收起速度", "Collapse speed")), _cmbRing2, Gap(10), hintRing2);
            g.Controls.Add(ring2Row, 0, 5);
            g.SetColumnSpan(ring2Row, 2);

            _numRad = Num(120, 700, s.Radius);
            _numSlots = Num(2, 12, s.Slots);
            _numLabel = Num(9, 40, s.LabelSize);
            Control radRow = Row(MkLabel(Lang.T("环半径", "Ring radius")), _numRad, Gap(24), MkLabel(Lang.T("弧上张数", "Items on the arc")), _numSlots,
                                 Gap(24), MkLabel(Lang.T("序号字号", "Index font size")), _numLabel);
            g.Controls.Add(radRow, 0, 6);
            g.SetColumnSpan(radRow, 2);

            _chkSingle = new CheckBox();
            _chkSingle.AutoSize = true;
            _chkSingle.Text = Lang.T("只用一个把手：左边那个点一下展开、再点一下收起（任务栏自动隐藏时更省事）", "Single handle: click to expand,\nclick again to collapse");
            _chkSingle.Checked = s.NubSingle;
            Control singleRow = Row(_chkSingle);
            g.Controls.Add(singleRow, 0, 7);
            g.SetColumnSpan(singleRow, 2);
        }

        // ---- 第 3 页：风格 ----
        void BuildPage3()
        {
            TableLayoutPanel g = _pages[2];
            SetupRows(g, 6);   // v1.0：多了「有生命感」那三个开关 + 演示模式
            Settings s = _s;

            g.Controls.Add(Section(Lang.T("风格", "Style")), 0, 0);

            _cmbStyle = new ComboBox();
            _cmbStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbStyle.FlatStyle = FlatStyle.Flat;
            _cmbStyle.Width = S(170);
            _cmbStyle.Margin = new Padding(0, 6, 0, 0);
            _cmbStyle.Items.AddRange(new object[] { Lang.T("新拟态 + 毛玻璃", "Neumorphic + frosted glass"), Lang.T("纯扁平", "Flat"), Lang.T("高对比（不透明）", "High contrast (opaque)") });
            _cmbStyle.SelectedIndex = (s.UiStyle == "flat") ? 1 : (s.UiStyle == "solid" ? 2 : 0);

            _cmbAccent = new ComboBox();
            _cmbAccent.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAccent.FlatStyle = FlatStyle.Flat;
            _cmbAccent.Width = S(170);
            _cmbAccent.Margin = new Padding(0, 6, 0, 0);
            _cmbAccent.Items.Add(Lang.T("跟随 Wheel 颜色", "Follow wheel colour"));
            for (int i = 0; i < Palette.Names.Length; i++) _cmbAccent.Items.Add("统一：" + Palette.Names[i]);
            _cmbAccent.SelectedIndex = (s.AccentIndex >= 0 && s.AccentIndex < Palette.Names.Length) ? s.AccentIndex + 1 : 0;
            Control styleRow = Row(MkLabel(Lang.T("界面风格", "UI style")), _cmbStyle, Gap(24), MkLabel(Lang.T("主题色", "Accent colour")), _cmbAccent);
            g.Controls.Add(styleRow, 0, 1);
            g.SetColumnSpan(styleRow, 2);

            _cmbAnim = new ComboBox();
            _cmbAnim.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAnim.FlatStyle = FlatStyle.Flat;
            _cmbAnim.Width = S(170);
            _cmbAnim.Margin = new Padding(0, 6, 0, 0);
            _cmbAnim.Items.AddRange(new object[] { Lang.T("慢", "Slow"), Lang.T("标准", "Normal"), Lang.T("快", "Fast") });
            _cmbAnim.SelectedIndex = (s.AnimSpeed <= 85) ? 0 : (s.AnimSpeed >= 120 ? 2 : 1);

            _chkName = new CheckBox();
            _chkName.AutoSize = true;
            _chkName.Text = Lang.T("显示名称标签", "Show name label");
            _chkName.Checked = s.ShowNameLabel;
            _chkName.Margin = new Padding(0, 10, 0, 0);
            _chkCount = new CheckBox();
            _chkCount.AutoSize = true;
            _chkCount.Text = Lang.T("显示计数标签", "Show counter label");
            _chkCount.Checked = s.ShowCountLabel;
            _chkCount.Margin = new Padding(S(20), 10, 0, 0);
            Control animRow = Row(MkLabel(Lang.T("动画速度", "Animation speed")), _cmbAnim, Gap(24), _chkName, _chkCount);
            g.Controls.Add(animRow, 0, 2);
            g.SetColumnSpan(animRow, 2);

            _chkGlassRefresh = new CheckBox();
            _chkGlassRefresh.AutoSize = true;
            _chkGlassRefresh.Text = Lang.T("毛玻璃定时刷新（轮盘挂久了背景也是新的）", "Refresh frosted glass periodically\n(background stays current)");
            _chkGlassRefresh.Checked = s.GlassRefresh;
            g.Controls.Add(Row(_chkGlassRefresh), 0, 3);

            _chkIntroAnim = new CheckBox();
            _chkIntroAnim.AutoSize = true;
            _chkIntroAnim.Text = Lang.T("启动时播放开启动画", "Play the startup animation");
            _chkIntroAnim.Checked = s.IntroAnim;
            g.Controls.Add(Row(_chkIntroAnim), 1, 3);

            // ---- v1.0「有生命感」的三个开关 ----
            // 放在这一页（风格）最后一行：它们不影响功能，只影响"看起来怎么样"，
            // 和上面那一行的"显示名称/计数标签"是同一类东西。
            _chkRipple = new CheckBox();
            _chkRipple.AutoSize = true;
            _chkRipple.Text = Lang.T("涟漪（新图进来时扩散一圈）", "Ripple (a ring spreads out when an image arrives)");
            _chkRipple.Checked = s.Ripple;
            _chkRipple.Margin = new Padding(0, 6, 0, 0);

            _chkRingShadow = new CheckBox();
            _chkRingShadow.AutoSize = true;
            _chkRingShadow.Text = Lang.T("环有影子", "Shadow under the ring");
            _chkRingShadow.Checked = s.RingShadow;
            _chkRingShadow.Margin = new Padding(S(20), 6, 0, 0);

            _chkDayMood = new CheckBox();
            _chkDayMood.AutoSize = true;
            _chkDayMood.Text = Lang.T("时间感（早上偏暖、深夜变暗）", "Time of day (warmer in the morning, dimmer at night)");
            _chkDayMood.Checked = s.DayMood;
            _chkDayMood.Margin = new Padding(S(20), 6, 0, 0);

            Control moodRow = Row(_chkRipple, _chkRingShadow, _chkDayMood);
            g.Controls.Add(moodRow, 0, 4);
            g.SetColumnSpan(moodRow, 2);

            // 演示模式（v1.0）：录屏录不到轮盘 —— 因为它默认对屏幕捕获隐身。
            // 打开这一项才能把轮盘录进视频里；代价是自己截图时轮盘会进图，
            // 所以这里同时把毛玻璃的定时刷新停掉（见 WheelForm.ApplyCaptureVisibility）。
            _chkRecordable = new CheckBox();
            _chkRecordable.AutoSize = true;
            _chkRecordable.Text = Lang.T("录屏时能拍到轮盘（演示用；打开后自己截图也会带上它）",
                                        "Let screen recorders capture the ring\n(for demos; your own screenshots will include it too)");
            _chkRecordable.Checked = s.Recordable;
            _chkRecordable.Margin = new Padding(0, 8, 0, 0);
            Control recRow = Row(_chkRecordable);
            g.Controls.Add(recRow, 0, 5);
            g.SetColumnSpan(recRow, 2);
        }

        // ---- 第 4 页：万能键与高级 ----
        void BuildPage4()
        {
            TableLayoutPanel g = _pages[3];
            SetupRows(g, 11);            // 0.6.0：多了两行翻译接口（URL/模型 一行、API Key 一行）
            Settings s = _s;

            g.Controls.Add(Section(Lang.T("万能键", "Universal key")), 0, 0);

            // 四个分区各绑一个动作（以前是写死的）。选中就立刻写进设置：
            // 不依赖Lang.T("确定", "OK")里那段保存循环（之前那里没生效）。
            string[] keyDir = { Lang.T("上", "Up"), Lang.T("右", "Right"), Lang.T("下", "Down"), Lang.T("左", "Left") };
            for (int i = 0; i < 4; i++)
            {
                ComboBox kb = new ComboBox();
                kb.DropDownStyle = ComboBoxStyle.DropDownList;
                kb.Width = S(150);
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

            Label keyHint = MkLabel(Lang.T("按住万能键弹出圆盘，往哪个方向松手就执行哪个动作", "Hold the universal key and the dial appears; release towards a direction to run that action"));
            keyHint.ForeColor = Color.FromArgb(140, 146, 158);
            Control keyHintRow = Row(keyHint);
            g.Controls.Add(keyHintRow, 0, 3);
            g.SetColumnSpan(keyHintRow, 2);

            // ---------- 高级（外观微调）：默认折叠，需要时勾一下 ----------
            // 这几项对大多数人是噪音（第一次用不懂该选什么），所以默认藏起来。
            g.Controls.Add(Section(Lang.T("高级", "Advanced")), 0, 4);

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
            _advR.Controls.Add(Row(MkLabel(Lang.T("玻璃不透明度", "Glass opacity")), _numGlass, Gap(16), MkLabel(Lang.T("圆角(%)", "Corner radius (%)")), _numRadius,
                                   Gap(16), MkLabel(Lang.T("阴影强度", "Shadow strength")), _numShadow), 0, 0);

            CheckBox chkAdv = new CheckBox();
            chkAdv.AutoSize = true;
            chkAdv.Text = Lang.T("显示高级选项（外观微调：玻璃 / 圆角 / 阴影）", "Show advanced options\n(fine-tune glass / corners / shadow)");
            chkAdv.Margin = new Padding(0, 10, 0, 0);
            chkAdv.CheckedChanged += new EventHandler(delegate(object o, EventArgs e2) {
                _advR.Visible = chkAdv.Checked;
                _advR.PerformLayout();
                PerformLayout();
            });
            g.Controls.Add(chkAdv, 0, 5);

            // 省电模式（0.5.3）：归在Lang.T("高级", "Advanced")这组里 —— 它是电源相关的行为开关，不是外观微调。
            // 排在 _advR **之前**：展开"高级选项"时往下顶的是这一行，微调行仍紧贴它自己的开关。
            // 第 4 页由此从 7 行变 8 行（和其余页持平）；万一它成了最高的一页，
            // EnsureFit 会在翻到它时把窗口补够（只长大不裁切），不会切掉这一行。
            _chkPower = new CheckBox();
            _chkPower.AutoSize = true;
            _chkPower.Text = Lang.T("省电模式：用电池时停掉定时毛玻璃刷新、重绘减半（插电自动恢复）", "Power saving: on battery, stop glass refresh\nand halve redraws (auto-restores on AC)");
            _chkPower.Margin = new Padding(0, 10, 0, 0);
            _chkPower.Checked = s.PowerSave;
            g.Controls.Add(_chkPower, 0, 6);
            g.Controls.Add(_advR, 0, 7);

            // ---- 翻译接口（0.6.0）----
            // 为什么放这一页：它是Lang.T("高级", "Advanced")配置，普通用户留空即可（内置免费引擎链），
            // 愿意填 key 的人自己会翻到这里。**不放进 _advR** —— 那个组默认是隐藏的。
            _txtLlmUrl = new TextBox();
            _txtLlmUrl.Text = s.LlmUrl;
            _txtLlmUrl.Width = S(300);
            _txtLlmUrl.Margin = new Padding(0, 5, 0, 0);
            _txtLlmModel = new TextBox();
            _txtLlmModel.Text = s.LlmModel;
            _txtLlmModel.Width = S(150);
            _txtLlmModel.Margin = new Padding(0, 5, 0, 0);
            Control llmRow = Row(MkLabel(Lang.T("翻译接口", "Translation API")), _txtLlmUrl, Gap(10), MkLabel(Lang.T("模型", "Model")), _txtLlmModel);
            g.Controls.Add(llmRow, 0, 8);
            g.SetColumnSpan(llmRow, 2);

            _txtLlmKey = new TextBox();
            _txtLlmKey.UseSystemPasswordChar = true;      // 别在屏幕上明着显示 key
            _txtLlmKey.Text = s.LlmKey;
            _txtLlmKey.Width = S(300);
            _txtLlmKey.Margin = new Padding(0, 5, 0, 0);
            Label llmKeyHint = new Label();
            llmKeyHint.AutoSize = true;
            llmKeyHint.Text = Lang.T("（留空就用内置免费接口；key 只存在本机配置文件里）", "(leave empty to use the built-in free endpoint; the key is stored only in your local config file)");
            llmKeyHint.ForeColor = Color.FromArgb(150, 152, 160);
            llmKeyHint.Margin = new Padding(0, 10, 0, 0);
            Control keyRow = Row(MkLabel("API Key"), _txtLlmKey, Gap(10), llmKeyHint);
            g.Controls.Add(keyRow, 0, 9);
            g.SetColumnSpan(keyRow, 2);

            // 反馈入口（0.6.0）：一键提 issue（预填环境信息）+ 复制诊断信息。
            // 放在Lang.T("高级", "Advanced")页最下面：不占常用路径，但用户真遇到问题时找得到。
            RoundButton fb = new RoundButton();
            fb.Text = Lang.T("反馈 / 报告问题…", "Feedback / report a problem…");
            fb.Font = new Font("Microsoft YaHei UI", 10f);
            fb.Fill = Color.FromArgb(238, 240, 245);
            fb.FillHover = Color.FromArgb(226, 230, 238);
            fb.TextColor = Color.FromArgb(60, 64, 74);
            fb.Size = new Size(S(170), S(34));
            fb.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                FeedbackForm ff = new FeedbackForm();
                try { ff.ShowDialog(this); } catch { }
                try { ff.Dispose(); } catch { }
            });
            Control fbRow = Row(fb);
            g.Controls.Add(fbRow, 0, 10);
            g.SetColumnSpan(fbRow, 2);
        }
    }
}

namespace SnapWheel
{
    // 设置窗口的"保存"逻辑（从 75-SettingsForm.cs 拆出来，纯搬移，行为不变）。
    // 它把界面上所有控件的当前值写回 Settings —— 是整个窗口里最需要保持与 UI 一致的一段，
    // 单独放一个文件，改界面和改保存逻辑时不容易互相影响。
    partial class SettingsForm
    {
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
                s.MoveInOnDrop = _chkMoveIn.Checked;   // 1.3.0：拖进来的文件是移进来还是留一份
                s.AutoHide = _chkAuto.Checked;
                s.AutoHideSeconds = (int)_numSec.Value;
                s.AlwaysOnTop = _chkTop.Checked;
                s.Corner = IndexCorner(_cmbCorner.SelectedIndex);
                s.AutoStart = _chkAutoStart.Checked;
                s.DeleteMode = (_cmbDel.SelectedIndex == 1) ? "single" : "double";
                s.SwitchMode = (_cmbSwitch.SelectedIndex == 1) ? "swipe" : "radial";
                s.EdgeAnchor = (_cmbEdge.SelectedIndex == 1) ? "screen" : (_cmbEdge.SelectedIndex == 2 ? "work" : "auto");
                s.ClipboardImport = _chkClip.Checked;
                s.CopyOnCapture = _chkCopy.Checked;
                s.ShowBalloon = _chkBalloon.Checked;
                s.CheckUpdate = _chkUpdate.Checked;
                s.DragOutAsFile = _chkDragFile.Checked;
                s.KeepAfterDragOut = _chkKeep.Checked;   // 0.6.0：拖出后是否留一份
                s.UiLanguage = (_cbLang.SelectedIndex == 2) ? "en" : (_cbLang.SelectedIndex == 1 ? "zh" : "");   // ""=跟随系统
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
                s.ResetScrollOnCapture = _chkScrollReset.Checked;   // 0.5.3：截图后要不要把滚动位置重置到最新那张
            }
            if (_built[2])
            {
                s.UiStyle = (_cmbStyle.SelectedIndex == 1) ? "flat" : (_cmbStyle.SelectedIndex == 2 ? "solid" : "neu");
                s.AccentIndex = _cmbAccent.SelectedIndex - 1;
                s.AnimSpeed = (_cmbAnim.SelectedIndex == 0) ? 70 : (_cmbAnim.SelectedIndex == 2 ? 140 : 100);
                s.ShowNameLabel = _chkName.Checked;
                s.ShowCountLabel = _chkCount.Checked;
                s.GlassRefresh = _chkGlassRefresh.Checked;
            s.Recordable = _chkRecordable.Checked;
                s.IntroAnim = _chkIntroAnim.Checked;     // 注意：这个控件在第 3 页（"动画细节"），别放进上一块
                // v1.0「有生命感」三个开关
                s.Ripple = _chkRipple.Checked;
                s.RingShadow = _chkRingShadow.Checked;
                s.DayMood = _chkDayMood.Checked;
            }
            if (_built[3])
            {
                s.GlassPercent = (int)_numGlass.Value;
                s.CardRadius = (int)_numRadius.Value;
                s.ShadowPercent = (int)_numShadow.Value;
                s.PowerSave = _chkPower.Checked;     // 0.5.3：省电模式（电池上才实际生效，见 12-Power.cs）
                // 0.6.0：翻译接口（留空 = 用内置免费引擎链；填了 = 走你自己的 OpenAI 兼容接口）
                s.LlmUrl = _txtLlmUrl.Text.Trim();
                s.LlmKey = _txtLlmKey.Text.Trim();
                s.LlmModel = _txtLlmModel.Text.Trim();
                if (s.LlmModel.Length == 0) s.LlmModel = "deepseek-chat";   // 模型名空着会直接 400
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
    }
}

namespace SnapWheel
{
    // 设置窗口的翻页（滑动切换）逻辑（从 75-SettingsForm.cs 拆出来，纯搬移，行为不变）。
    // 这一组是"翻页时把两页各截成位图来滑动"的实现 —— 它和界面构建、和值保存是三件不同的事，
    // 拆开后改动画不会碰到控件构建，改控件也不会碰坏动画。
    partial class SettingsForm
    {
        Bitmap ShotPage(Control pg)
        {
            Bitmap b = new Bitmap(pg.Width, pg.Height, PixelFormat.Format32bppPArgb);
            pg.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
            return b;
        }

        void FreeSlide()
        {
            if (_slideA != null) { try { _slideA.Dispose(); } catch { } _slideA = null; }
            if (_slideB != null) { try { _slideB.Dispose(); } catch { } _slideB = null; }
        }

        void BodyPaint(object o, PaintEventArgs pe)
        {
            if (_slideA != null) pe.Graphics.DrawImageUnscaled(_slideA, _slideAx, 0);
            if (_slideB != null) pe.Graphics.DrawImageUnscaled(_slideB, _slideBx, 0);
        }

        // 用户输入路径（点扇区 / 滚轮）走这里：动画期间直接忽略 —— 这就是"防连点"，
        // 连点不会叠加动画、不会重叠、不会跳变。（把防连点放在输入层，而不是塞进 ShowPage：
        // 塞进 ShowPage 会让"程序性切页"被悄悄吞掉 —— 没消息泵时动画永远走不完，
        // 后面几次切页就全丢了，工具/测试里踩到过。）
        bool TryGoto(int i)
        {
            if (Animating) return false;
            ShowPage(i);
            return true;
        }

        // 程序性切页（首次显示 / 工具 / 测试）：一定切过去；上一段动画没收尾就先精确收尾，绝不卡住
        void ShowPage(int i, bool animate)
        {
            if (i < 0) i = 0;
            if (i > _pages.Length - 1) i = _pages.Length - 1;
            if (Animating) FinishNow();
            if (i == _cur) return;          // 已经在这一页：不重播
            BuildPage(i);
            int from = _cur;
            int dialFrom = _dial != null ? _dial.Current : -1;   // 分页器高亮的"起点"要按它自己的高亮算
            _cur = i;
            if (_dial != null) _dial.Current = i;
            if (from < 0 || !animate || !IsHandleCreated || _body == null
                || _body.ClientSize.Width <= 0 || _body.ClientSize.Height <= 0)
            {
                SnapTo(i);                  // 首次显示 / 窗口还没出来：直接摆好（老行为）
                return;
            }
            StartSlide(from, i, dialFrom);
        }

        // 把正在走的动画立刻收尾到目标页（精确落位，等同动画最后一帧）
        void FinishNow()
        {
            if (_ptimer != null) _ptimer.Stop();
            if (_pwatch != null) { try { _pwatch.Stop(); } catch { } _pwatch = null; }
            int to = _animTo;
            _animT = 1f;
            _animFrom = -1;
            if (to >= 0) SnapTo(to);
            else FreeSlide();
        }

        void BuildPage(int i)
        {
            if (_built[i]) return;
            _built[i] = true;
            _pages[i].SuspendLayout();
            _builders[i]();
            _pages[i].ResumeLayout(true);
            _pages[i].PerformLayout();
            EnsureFit(i);        // 建完就量：这一页的内容要是不够放，窗口当场长大（翻到哪页都不会裁）
        }

        // 精确落位：Dock=Fill 由布局引擎给出整格矩形，动画结束绝不留下 1px 偏移
        void SnapTo(int i)
        {
            // 先把真控件摆回来，再扔贴图 —— 任何一帧都不许出现"没内容"的空档
            for (int k = 0; k < _pages.Length; k++)
            {
                _pages[k].Dock = DockStyle.Fill;
                _pages[k].Visible = (k == i);
            }
            FreeSlide();
            if (_dial != null)
            {
                _dial.AnimFrom = i; _dial.AnimTo = i; _dial.AnimT = 1f;
                _dial.Current = i; _dial.Invalidate();
            }
            if (_body != null) { _body.PerformLayout(); _body.Invalidate(); }
        }

        void StartSlide(int from, int to, int dialFrom)
        {
            BuildPage(from);
            _animFrom = from; _animTo = to; _animT = 0f;
            _dialFrom = dialFrom < 0 ? from : dialFrom;
            int W = _body.ClientSize.Width, H = _body.ClientSize.Height;
            // 先把两页摆好（Dock=Fill）并排一次版，才能拍到正确的图
            for (int k = 0; k < _pages.Length; k++)
            {
                _pages[k].Dock = DockStyle.Fill;
                _pages[k].Visible = (k == from || k == to);
            }
            _body.PerformLayout();
            _pages[from].PerformLayout();
            _pages[to].PerformLayout();
            // 拍两张图：旧页滑出、新页滑入（各 4~9ms，一次切页只拍一次）
            FreeSlide();
            _slideA = ShotPage(_pages[from]);
            _slideB = ShotPage(_pages[to]);
            // 真控件全藏起来 —— 滑动期间 body 只贴这两张图，不挪窗口、不重排、不重画文字
            for (int k = 0; k < _pages.Length; k++) _pages[k].Visible = false;
            int dir = (to > from) ? 1 : -1;              // 往后翻：新页从右边进来
            _slideAx = 0;
            _slideBx = dir * W;
            _animDir = dir;
            _pwatch = System.Diagnostics.Stopwatch.StartNew();
            if (_ptimer == null)
            {
                _ptimer = new System.Windows.Forms.Timer();
                _ptimer.Interval = 10;                   // 和轮盘动画同一个节拍（~66fps）
                _ptimer.Tick += delegate(object o, EventArgs e2) { AnimTick(); };
            }
            _ptimer.Start();
            ApplySlide(0f);                              // 第 0 帧：新页整页在窗口外 —— 一帧都不许重叠
        }

        void ApplySlide(float t)
        {
            int W = _body == null ? 0 : _body.ClientSize.Width;
            if (W <= 0 || _body.ClientSize.Height <= 0) return;
            float e = Gfx.EaseOut(t);                // 先快后慢、收尾稳（跟轮盘同一套缓动）
            _slideBx = (int)Math.Round(_animDir * W * (1f - e));    // ±W -> 0
            _slideAx = (int)Math.Round(-_animDir * W * e);          // 0 -> ∓W
            if (_dial != null)
            {
                _dial.AnimFrom = _dialFrom; _dial.AnimTo = _animTo; _dial.AnimT = e;
                _dial.Invalidate();                  // 扇区高亮/凸起跟着一起走过去
            }
            if (_body != null) _body.Invalidate();   // 双缓冲面板：一帧只画一次，贴图不出闪
        }

        void AnimTick()
        {
            if (_animFrom < 0 || _animTo < 0) { if (_ptimer != null) _ptimer.Stop(); return; }
            // 进度看真实时间：这一帧画得慢（负载重/重绘多）时不会把整段动画拖长，总时长始终是 160ms 左右
            _animT = _pwatch == null ? 1f : (float)(_pwatch.Elapsed.TotalMilliseconds / PageAnimMs);
            if (_animT >= 1f)
            {
                _animT = 1f;                        // 收尾精确到 1，不留 1.03 这种余量
                ApplySlide(1f);                     // 最后一帧：位置精确等于目标（0 偏移）
                int to = _animTo;
                _animFrom = -1;
                if (_ptimer != null) _ptimer.Stop();
                if (_pwatch != null) { _pwatch.Stop(); _pwatch = null; }
                SnapTo(to);                         // 再交回布局引擎（Dock=Fill），保证和静态布局逐像素一致
                _animTo = to;
                return;
            }
            ApplySlide(_animT);
        }

    }
}

// 注：本文件的"翻页动画"已拆到 75c-SettingsForm.Slide.cs，"页面构建"在 75a，"保存"在 75b
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
    partial class SettingsForm : Form, IMessageFilter
    {
        // ============================ 布局总则（务必先读） ============================
        // 1. 这个窗口的布局一律"坐标明确"：每张 TableLayoutPanel 都写死 RowCount/ColumnCount，
        //    每个控件都用 Add(控件, 列, 行) 指明格子 —— **绝不靠添加顺序排行**。
        //    v0.5.2 提速时就是因为挪了添加顺序，标题跑到最底下、按钮跑到最上面（"头和屁股长反了"）。
        // 2. 四页内容 = 四张页面格，同一时间只显示一张；每页都是"两列 + 行"的明确坐标。
        // 3. 每页的控件**第一次翻到那页才建**（懒建）：构造量降到 1/4，这是打开设置变快的主因。
        //    没建过的页 = 没被看过 = 没被改过，所以Lang.T("确定", "OK")时跳过它（值保持原样，不会被写回默认值）。
        // ==========================================================================
        TableLayoutPanel _root;
        Panel _body;
        PageDial _dial;
        readonly TableLayoutPanel[] _pages = new TableLayoutPanel[4];
        readonly Action[] _builders = new Action[4];
        readonly bool[] _built = new bool[4];
        int _cur = -1;
        int _openW, _openH;                  // 打开时的客户区尺寸（关闭时对比，用户拖过才写回设置）
        public Size ContentNeed;             // 四页里最大的"内容首选尺寸"（工具/测试看用）
        Settings _s;
        bool _filterAdded;

        // ---- 第 1 页「行为与快捷键」 ----
        CheckBox _chkDisk, _chkAutoStart, _chkAuto, _chkTop, _chkClip, _chkCopy, _chkBalloon, _chkUpdate, _chkDragFile, _chkMoveIn;
        CheckBox _chkKeep;                   // 拖出后是否在环上留一份（0.6.0）
        ComboBox _cbLang;                    // 界面语言（0.6.0）
        TextBox _txtDir;
        NumericUpDown _numSec;
        ComboBox _cmbHotkey, _cmbCorner, _cmbDel, _cmbSwitch, _cmbEdge;
        // ---- 第 2 页「轮盘与外观」 ----
        NumericUpDown _numMax, _numThumb, _numRad, _numSlots, _numLabel, _numPeek;
        ComboBox _cmbScale, _cmbRing, _cmbRing2;
        CheckBox _chkCollapse, _chkSingle, _chkIntroAnim, _chkScrollReset;
        // ---- 第 3 页「风格」 ----
        ComboBox _cmbStyle, _cmbAccent, _cmbAnim;
        CheckBox _chkName, _chkCount, _chkGlassRefresh, _chkRecordable;
        // v1.0「有生命感」的三个开关（默认开、可关，见 61d-WheelForm.Atmos.cs）
        CheckBox _chkRipple, _chkRingShadow, _chkDayMood;
        // ---- 第 4 页「万能键与高级」 ----
        readonly ComboBox[] _keyBox = new ComboBox[4];
        NumericUpDown _numGlass, _numRadius, _numShadow;
        TableLayoutPanel _advR;
        CheckBox _chkPower;                  // 省电模式（0.5.3）：只在电池供电时生效，见 12-Power.cs
        // 翻译接口（0.6.0）：留空就走内置免费引擎链（有道 → MyMemory 保底）；
        // 填上就是 OpenAI 兼容接口（DeepSeek / 豆包 / 通义 / 本地 Ollama），译文质量最好。
        TextBox _txtLlmUrl, _txtLlmKey, _txtLlmModel;

        // 窗口出厂尺寸 = 允许缩到的最小尺寸（**逻辑像素**，实际会乘 DPI 系数 K）。
        // 这个数不能随手改小：四页内容是按 720px 宽（760 - 40 边距）排的。
        const int MinClientW = 760, MinClientH = 574;

        // 测试用：强制指定 DPI 缩放系数（0 = 按真实 DPI 判断）。
        // 和 Elev.ForceForTest 一个道理 —— 不然"150% 屏幕下会不会被裁"这件事永远只能在那种屏上手测。
        public static float ForceKForTest = 0f;

        float K = 1f;                          // DPI 缩放系数（长度类尺寸都乘它，字体点数不乘）

        int S(float v) { return (int)Math.Round(v * K); }
        int S(int v) { return (int)Math.Round(v * K); }
        Padding Pad(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }

        static readonly int[] scVals = { 0, 80, 90, 100, 110, 125, 150, 175, 200, 250 };
        static readonly int[] ringVals = { 220, 150, 100, 80, 60, 45 };
        static readonly string[] ringNames = { Lang.T("极快", "Very fast"), Lang.T("快", "Fast"), Lang.T("标准", "Normal"), Lang.T("慢", "Slow"), Lang.T("很慢", "Very slow"), Lang.T("最慢", "Slowest") };

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

        // 双层缓冲 + 整窗合成：翻页滑动时子控件（文字）不会闪、不会重影。
        // WS_EX_COMPOSITED 是这里的关键 —— WinForms 的 DoubleBuffered 只管控件自己那一块，
        // 子控件（标签/勾选框/下拉框各自都是独立窗口）挪动时的重画它管不着，只有整窗合成能压住。
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000;      // WS_EX_COMPOSITED
                return cp;
            }
        }

        // 滚轮：一格 = 一页。向上滚 = 往前一页（和主界面轮盘一致）；环跟着转一格，停手后吸附回正角度。
        // 拖动中忽略；动画中允许接管（ShowPage 会先把上一段精确收尾，再从当前进度滑向新页，不跳回）。
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e.Delta != 0) { WheelStep(e.Delta > 0 ? -1 : 1); return; }   // 自己吃掉，不再往上冒（免得翻两次）
            base.OnMouseWheel(e);
        }

        // 滚一格：step = -1 往前一页、+1 往后一页
        bool WheelStep(int step)
        {
            if (step == 0) return false;
            if (_dial != null && _dial.Dragging) return false;        // 拖着转环的时候滚轮不参与
            int want = _cur + step;
            if (want < 0) want = 0;
            if (want > _pages.Length - 1) want = _pages.Length - 1;
            if (want == _cur) return false;                           // 顶到头了：什么都不做
            if (_dial != null) _dial.Nudge(want > _cur ? 1 : -1);     // 环先跟着转一格（有动画，不是硬跳）
            ShowPage(want);                                           // 内容页切过去（动画中直接接管）
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 自己的定时器必须自己停（v0.5.1 的教训：窗口关了定时器还在跑，白烧 CPU）
                if (_fadeTimer != null) { try { _fadeTimer.Stop(); _fadeTimer.Dispose(); } catch { } _fadeTimer = null; }
                if (_ptimer != null) { try { _ptimer.Stop(); _ptimer.Dispose(); } catch { } _ptimer = null; }
                if (_pwatch != null) { try { _pwatch.Stop(); } catch { } _pwatch = null; }
                FreeSlide();
                if (_filterAdded)
                {
                    try { Application.RemoveMessageFilter(this); } catch { }
                    _filterAdded = false;
                }
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
                if (delta != 0) { WheelStep(delta > 0 ? -1 : 1); return true; }
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


        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 拖边框改大小：翻页滑动用的两张位图是按旧尺寸拍的，先精确收尾（不然会贴一张旧尺寸的图）；
            // 再把当前页按新尺寸重新排一次。页面是 Dock=Fill + 行高 AutoSize，
            // 所以"多出来的高度"自然全给了 root 第 2 行（当前页那格 Percent(100)），
            // 标题（第 0 行）、分页器（第 1 行）、按钮行（第 3 行）、页脚（第 4 行）都是固定高，不会跟着错位。
            if (_body == null || _cur < 0) return;      // 构造函数里设 ClientSize 时还没有页面
            if (Animating) FinishNow();
            SnapTo(_cur);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);          // 补一次整窗重绘，按钮四角不会闪白块
            StartFadeIn();
        }

        // ---- 打开时的淡入 ----
        // 用户反馈：点设置后窗口出现较慢，而且**期间没有任何东西缓解"加载感/卡顿感"**。
        // 实测 `new SettingsForm()` 约 250ms（构造**体内**的标记加起来只有 ~15ms，
        // 大头还没定位到，见下面注释），那 250ms 里屏幕上什么都不发生；
        // 等到窗口出现又是"啪"地全亮 —— 一头一尾都是硬切。
        // 这一下至少把**出现**变成渐显：眼睛看到的是"它在长出来"，不是"突然多了一坨"。
        //
        // ⚠️ 还没解决的是那 250ms 本身。之所以没顺手去"优化"它：
        //    构造体内的分段计时加起来只有 15ms，说明时间不在我改得到的那段代码里 ——
        //    没定位到就改，只会把别的地方弄坏（这个项目在"猜一个改法试一次"上摔过很多次）。
        System.Windows.Forms.Timer _fadeTimer;
        float _fadeT = 0f;
        bool _fadeDone;

        void StartFadeIn()
        {
            if (_fadeDone) return;          // 只为"打开"那一次，切页不重放
            _fadeDone = true;
            try
            {
                Opacity = 0.34;
                _fadeTimer = new System.Windows.Forms.Timer();
                _fadeTimer.Interval = 15;
                _fadeTimer.Tick += new EventHandler(delegate(object o, EventArgs e2)
                {
                    try
                    {
                        _fadeT += 0.17f;
                        if (_fadeT >= 1f) { _fadeT = 1f; _fadeTimer.Stop(); _fadeTimer.Dispose(); _fadeTimer = null; }
                        Opacity = 0.34 + 0.66 * _fadeT;
                    }
                    catch { try { Opacity = 1.0; } catch { } }
                });
                _fadeTimer.Start();
            }
            catch { try { Opacity = 1.0; } catch { } }
        }

        public SettingsForm(Settings s)
        {
            _s = s;
            Text = AppInfo.Name + Lang.T(" 设置", " Settings");
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(250, 250, 252);
            // ---- DPI 缩放系数（0.5.3 补）----
            // 这个窗口的布局全是"写死的物理像素"。在 150% 缩放（144DPI）的屏幕上，**字会按 DPI 放大 1.5 倍，
            // 窗口却一动不动** —— 于是内容顶出可视区：用户报的"有些选项的字显示不全""下面还有部分被遮住"
            // （底部按钮行和页脚被切）就是这个。修法：**所有Lang.T("长度", "Length")尺寸都乘 K**；
            // **字体的点数一个都不动**（点数本来就跟着 DPI 走，再乘一次会变成 2.25 倍）。
            try { K = ForceKForTest > 0f ? ForceKForTest : Native.DpiScaleOf(IntPtr.Zero); } catch { K = 1f; }
            if (!(K >= 1f)) K = 1f;               // 小于 100% 不缩（缩了字反而更小、更看不清）
            if (K > 3f) K = 3f;
            // 有限度地自由调整大小（用户报"有些选项的字显示不全"）：可以拖边框放大 / 缩小，
            //   下限 = 出厂尺寸（760×574 逻辑像素 × K，四页内容在这个尺寸下都排得下）
            //   上限 = 屏幕工作区的 92%（再大就没意义，也不该长到屏幕外面去）
            // 只动窗口大小：**四页的布局一个字没动**（坐标明确 + 每页懒建 + 保存只写建过的页）。
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;                  // 上限已经被 MaximumSize 夹住了，最大化键没有意义
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = false;                     // 不随内容长高长胖：大小由用户拖（翻页仍然代替滚动）
            DoubleBuffered = true;                // 滑动时整窗不闪（配合 CreateParams 里的 WS_EX_COMPOSITED）
            ClientSize = new Size(S(MinClientW), S(MinClientH));
            MinimumSize = SizeFromClientSize(new Size(S(MinClientW), S(MinClientH)));
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                if (wa.Width > 100 && wa.Height > 100)
                    MaximumSize = new Size(Math.Max(MinimumSize.Width, (int)(wa.Width * 0.92f)),
                                           Math.Max(MinimumSize.Height, (int)(wa.Height * 0.92f)));
            }
            catch { }
            // 底部原来只留 12px：页脚 "by exper7" 那行的真实文字格比字体行高高，末几行像素会被窗口底边切掉
            Padding = Pad(20, 14, 20, 18);

            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.AutoSize = false;
            root.Dock = DockStyle.Fill;
            root.Margin = new Padding(0);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(28)));    // 0 标题
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(96)));    // 1 轮盘式分页器
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));      // 2 当前页
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));    // 3 按钮行（右下）
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(32)));    // 4 版本行（22 太小：标签真实高度+边距放不下，末几行像素会被裁）
            _root = root;

            Label head = new Label();
            // 别信 AutoSize：标签的高度是按"字体行高"算的（13pt 粗体只给 22px），
            // 但文字真正要占的格子是 25px —— 底下一排会被削掉（用户报的"标题被遮挡了一点"，
            // 和上一轮 ComboBox"报 23px 实高 27px"是同一个坑）。这里改成自己量出真实宽高。
            head.AutoSize = false;
            head.Text = AppInfo.Name + Lang.T(" 设置", " Settings");
            head.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(32, 34, 38);
            head.TextAlign = ContentAlignment.MiddleLeft;
            Size hsz = TextRenderer.MeasureText(head.Text, head.Font);
            head.Size = new Size(hsz.Width + 2, hsz.Height + 1);        // 真实文字格 + 1px 余量
            head.Margin = new Padding(0, 0, 0, 0);                      // 不靠 Margin 占位，行高 28 自己留白
            root.Controls.Add(head, 0, 0);

            // 分页器：顶部小圆弧，四个扇区 = 四页（点扇区 / 滚轮翻页），新拟态凸起 + 当前页高亮
            _dial = new PageDial();
            _dial.Names = new string[] { Lang.T("行为与快捷键", "Behaviour & shortcuts"), Lang.T("轮盘与外观", "Ring & appearance"), Lang.T("风格", "Style"), Lang.T("万能键与高级", "Universal key & advanced") };
            _dial.BackColor = Color.FromArgb(250, 250, 252);
            _dial.Dock = DockStyle.Fill;
            _dial.Margin = new Padding(0);
            _dial.K = K;                       // 分页器的弧线几何也跟着 DPI 走
            _dial.PagePicked += new EventHandler(delegate(object o, EventArgs e2) { TryGoto(_dial.Picked); });
            // 拖动松手：内容页跟着高亮走。这里用 ShowPage（程序性切页）而不是 TryGoto ——
            // 拖动允许直接接管上一次还没走完的过渡（从当前进度收尾后再滑向新页），不许被防连点吞掉。
            _dial.PageDropped += new EventHandler(delegate(object o, EventArgs e2) { ShowPage(_dial.Current); });
            root.Controls.Add(_dial, 0, 1);

            // 四张页面格：先建好挂上（空白），内容懒建；非当前页 Visible=false
            Panel body = new BufferedPanel();     // 双缓冲：滑动时这一块整块重画
            body.Paint += new PaintEventHandler(BodyPaint);   // 滑动期间由它贴两张页位图
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
            ok.Text = Lang.T("确定", "OK");
            ok.Size = new Size(S(104), S(36));
            ok.Fill = Color.FromArgb(0, 122, 204);
            ok.FillHover = Color.FromArgb(0, 140, 232);
            ok.TextColor = Color.White;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            ok.Margin = Pad(10, 2, 0, 0);
            ok.Click += new EventHandler(delegate(object o, EventArgs e2) {
                SaveFromUi();
                DialogResult = DialogResult.OK;
                Close();
            });
            RoundButton cancel = new RoundButton();
            cancel.Text = "取消";
            cancel.Size = new Size(S(104), S(36));
            cancel.Fill = Color.FromArgb(234, 235, 240);
            cancel.FillHover = Color.FromArgb(222, 224, 230);
            cancel.TextColor = Color.FromArgb(58, 60, 66);
            cancel.Font = new Font("Microsoft YaHei UI", 10f);
            cancel.Margin = Pad(10, 2, 0, 0);
            cancel.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });

            RoundButton guide = new RoundButton();
            guide.Text = Lang.T("新手引导", "Getting started");
            guide.Size = new Size(S(104), S(36));
            guide.Fill = Color.FromArgb(236, 240, 246);
            guide.FillHover = Color.FromArgb(226, 233, 243);
            guide.TextColor = Color.FromArgb(40, 90, 150);
            guide.Font = new Font("Microsoft YaHei UI", 10f);
            guide.Margin = Pad(0, 2, 0, 0);
            guide.Click += new EventHandler(delegate(object o, EventArgs e2)
            { GuideForm gf = new GuideForm(); gf.ShowDialog(this); });

            // 还原默认设置：只重置设置项，不动你的图片和 Wheel 内容
            RoundButton reset = new RoundButton();
            reset.Text = Lang.T("还原默认", "Restore defaults");
            reset.Size = new Size(S(104), S(36));
            reset.Fill = Color.FromArgb(252, 238, 236);
            reset.FillHover = Color.FromArgb(248, 224, 220);
            reset.TextColor = Color.FromArgb(178, 66, 52);
            reset.Font = new Font("Microsoft YaHei UI", 10f);
            reset.Margin = Pad(10, 2, 0, 0);
            reset.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                DialogResult r2 = MessageBox.Show(this,
                    "把所有设置恢复成默认值？\r\n\r\n（不会动你的图片和 Wheel 内容，只重置外观/行为等设置项）",
                    Lang.T("还原默认设置", "Restore defaults"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r2 != DialogResult.OK) return;
                Settings def = new Settings();
                Settings.CopyInto(def, s);
                AutoRun.Apply(s.AutoStart);
                s.Save();
                DialogResult = DialogResult.OK;
                Close();
            });

            // 打赏：收款码弹窗（刻意不写进说明、不显眼，见 84-Reward.cs）
            RoundButton tip = new RoundButton();
            tip.Text = Lang.T("打赏", "Tip the author");
            tip.Size = new Size(S(104), S(36));
            tip.Fill = Color.FromArgb(252, 246, 234);
            tip.FillHover = Color.FromArgb(248, 236, 216);
            tip.TextColor = Color.FromArgb(160, 116, 30);
            tip.Font = new Font("Microsoft YaHei UI", 10f);
            tip.Margin = Pad(10, 2, 0, 0);
            tip.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                try { using (RewardForm rf = new RewardForm()) rf.ShowDialog(this); }
                catch (Exception rex) { Err.Log("RewardForm", rex); }
            });

            // 按钮行：用六列表格把确定/取消靠右对齐（引导/还原/打赏在左）。
            TableLayoutPanel btnRow = new TableLayoutPanel();
            btnRow.ColumnCount = 6;
            btnRow.RowCount = 1;
            btnRow.AutoSize = false;
            btnRow.Dock = DockStyle.Fill;
            btnRow.Margin = new Padding(0);
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            btnRow.Controls.Add(guide, 0, 0);
            btnRow.Controls.Add(reset, 1, 0);
            btnRow.Controls.Add(tip, 2, 0);           // 「还原默认」右边，样式和其它按钮一致
            // 中间只放个"撑宽"的空位（把确定/取消推到右边）。这里必须给它一个小尺寸：
            // Panel 的默认尺寸是 200×100，放进 40px 高的按钮行里会顶出行高、被裁（渲染工具会报"被裁"）。
            Panel btnSpacer = new Panel();
            btnSpacer.Size = new Size(1, 1);
            btnSpacer.Margin = new Padding(0);
            btnRow.Controls.Add(btnSpacer, 3, 0);
            btnRow.Controls.Add(ok, 4, 0);
            btnRow.Controls.Add(cancel, 5, 0);
            root.Controls.Add(btnRow, 0, 3);

            Label about = new Label();
            about.AutoSize = true;
            about.Text = AppInfo.Name + "   v" + AppInfo.Version + "   ·   by " + AppInfo.Author;
            about.ForeColor = Color.FromArgb(150, 150, 160);
            about.Margin = Pad(0, 2, 0, 0);
            root.Controls.Add(about, 0, 4);

            // ⚠️⚠️ 这两行的**顺序是有讲究的，千万别顺手换回去**。
            //
            // 原来是"先 ShowPage(0) 建页、再 Controls.Add(root) 挂树"，注释还写着
            // "全部建完才挂上去：整棵树只排一次"。听着合理，实测是**灾难**：
            //   · 先建页再挂树 → `new SettingsForm()` **255ms**
            //   · 先挂树再建页 → **29ms**（快 9 倍）
            // 而且**画出来一模一样**：831×653 的窗口逐像素比，13.5 万个采样点**零差异**。
            //
            // 为什么：页面挂在窗体上之后，TableLayoutPanel 的布局/文字测量能走系统已经建好的
            // 那一套上下文；在一个**还没挂到窗体**的树上布局，每个控件都要各自去建一次
            // （实测布局耗时随控件数近似平方增长：9 个控件 101ms、17 个 278ms）。
            // 换句话说"只排一次"省下的那点，远远抵不过"脱离窗体去排"贵出来的那些。
            Controls.Add(root);      // 先挂树
            ShowPage(0);             // 再建第 1 页（只建这一页，其余懒建）
            PerformLayout();
            SizeToContent();         // 再按"内容首选尺寸 × DPI"定默认尺寸 / 最小尺寸（见方法里的说明）

        }

        // ============================ 默认尺寸 / 记住用户拖过的尺寸 ============================
        // 用户报"一打开显示不全、还得自己拖"：所以默认尺寸不再写死，改成**按内容反推**：
        //     需要的最小客户区 = max(出厂尺寸, 那一页的"内容首选尺寸" + 固定行高 + 内边距)
        // 全部都是**物理像素**（页面里的字号已经按真实 DPI 渲染，所以量出来的首选尺寸天然含 DPI 系数，
        // 不需要再乘一次 K —— 和"长度乘 K、字体点数不乘"是同一条规矩）。
        //   · 量哪一页？**先量马上要显示的那一页**（第 1 页，构造时就建好了），其余各页等第一次翻到时
        //     在 BuildPage 里量（EnsureFit）。这样打开不比原来慢多少（"每页懒建"的初衷保住了：
        //     实测四页全量要 +130ms），但**任何一页被显示出来时都已经按内容补足过尺寸**，不会裁。
        //   · MinimumSize 用同一个值（"最小尺寸不小于内容"），上限仍是屏幕工作区 92%。
        //   · 用户拖过之后在关闭时写回 settings（WinW/WinH），下次打开就用他的尺寸；
        //     存下来的值一律夹进 [最小, 最大]（换到别的缩放比屏幕上也绝不会小于内容）。
        void SizeToContent()
        {
            // 出厂下限先当最小尺寸（量出来的只会比它大）
            MinimumSize = SizeFromClientSize(new Size(S(MinClientW), S(MinClientH)));
            ApplyMaxSize();
            ClientSize = new Size(S(MinClientW), S(MinClientH));
            if (_s != null && _s.WinW > 0 && _s.WinH > 0) { ClientSize = new Size(_s.WinW, _s.WinH); ClampClient(); }
            EnsureFit(0);                                   // 第 1 页按内容（不够就把窗口长大）
            ClampClient();
            _openW = ClientSize.Width; _openH = ClientSize.Height;
        }

        void ApplyMaxSize()
        {
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                if (wa.Width > 100 && wa.Height > 100)
                    MaximumSize = new Size(Math.Max(MinimumSize.Width, (int)(wa.Width * 0.92f)),
                                           Math.Max(MinimumSize.Height, (int)(wa.Height * 0.92f)));
            }
            catch { }
        }

        // 把第 i 页量一次；不够就把 MinimumSize 和窗口一起长大（只长大不缩小 —— 用户拖过的尺寸不会被某一页打回）
        void EnsureFit(int i)
        {
            if (i < 0 || i >= _pages.Length) return;
            try
            {
                Size pref = _pages[i].GetPreferredSize(new Size(Math.Max(1, S(MinClientW) - Padding.Horizontal), 0));
                if (pref.Width > ContentNeed.Width || pref.Height > ContentNeed.Height)
                    ContentNeed = new Size(Math.Max(ContentNeed.Width, pref.Width), Math.Max(ContentNeed.Height, pref.Height));
                int w = Math.Max(S(MinClientW), ContentNeed.Width + Padding.Horizontal);
                int h = Math.Max(S(MinClientH), ContentNeed.Height + S(28) + S(96) + S(40) + S(32) + Padding.Vertical);
                MinimumSize = SizeFromClientSize(new Size(w, h));
                ApplyMaxSize();
                if (ClientSize.Width < w || ClientSize.Height < h)
                {
                    ClientSize = new Size(Math.Max(ClientSize.Width, w), Math.Max(ClientSize.Height, h));
                    ClampClient();
                }
            }
            catch (Exception ex) { Err.Log("SettingsEnsureFit", ex); }
        }

        // 客户区夹进 [最小, 最大]（范围是以"窗口外框"记的，所以换算出客户区的边界再比）
        void ClampClient()
        {
            try
            {
                int minW = MinimumSize.Width - (Width - ClientSize.Width);
                int minH = MinimumSize.Height - (Height - ClientSize.Height);
                int maxW = MaximumSize.Width - (Width - ClientSize.Width);
                int maxH = MaximumSize.Height - (Height - ClientSize.Height);
                int w = ClientSize.Width, h = ClientSize.Height;
                if (w < minW) w = minW; if (h < minH) h = minH;
                if (maxW > 0 && w > maxW) w = maxW;
                if (maxH > 0 && h > maxH) h = maxH;
                if (w != ClientSize.Width || h != ClientSize.Height) ClientSize = new Size(w, h);
            }
            catch { }
        }

        // 关窗口时把用户拖出来的尺寸记进设置（和打开时不一样才写，免得每次都动配置文件）
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try
            {
                ClampClient();
                if (_s != null && (ClientSize.Width != _openW || ClientSize.Height != _openH))
                {
                    _s.WinW = ClientSize.Width; _s.WinH = ClientSize.Height;
                    _s.Save();
                }
            }
            catch (Exception ex) { Err.Log("SettingsWinSize", ex); }
        }

        // ============================ 四页的内容 ============================
        // 每页一张"两列 + 行"的格子，行号写死；一格里放一个控件（成组的行用 Row(...) 包一层）。
        // 行号从 0 开始，写 RowCount 时要 ≥ 最大行号 + 1，否则那行不显示。

        // 四个页面构建函数已拆到 75a-SettingsForm.Pages.cs（partial class，编译时合并）

        // ============================ 翻页 ============================
        // 第一次翻到某页才建那页的控件；没建过的页 = 没看过 = 没改过。
        // 翻页带 160ms 位移动画（15ms 一帧 ≈ 11 帧，实测约 165ms）：新页从一侧滑进来、
        // 旧页朝反方向滑出去（Panel 没有透明度，所以只用位移 + 分页器高亮同步过渡，不跳变）。
        // 两页在动画期间**永远刚好拼满可视区** —— 一个在 [x, x+W]、另一个在 [x±W, x±W+W]
        // —— 所以既不重叠也不留缝。
        const int PageAnimMs = 240;      // 0.6.0：160 -> 240ms（用户反馈翻页过渡帧率偏低，拉长时间让帧数更多、更顺）      // 140~200ms 档；15ms 一帧 ≈ 11 帧
        System.Windows.Forms.Timer _ptimer;
        System.Diagnostics.Stopwatch _pwatch;       // 进度按"真实过去了多少毫秒"算，不按帧数累加
        int _animFrom = -1, _animTo = -1;
        int _dialFrom = -1;             // 分页器高亮过渡的起点（拖动时它和 _animFrom 可能不是同一页）
        int _animDir = 1;               // 翻页方向：+1 = 新页从右边进来
        float _animT = 1f;

        // ---------- 翻页用位图滑动（不是"挪真控件"）----------
        // 页里的标签/勾选框/下拉框各自都是独立窗口，每帧挪一次就要重画一次 —— 屏幕上就是"字在闪/抖"
        // （文字每帧被重新光栅化尤其明显，实测一次翻页里体内容器被重排 23 次、页容器 25 次）。
        // 现在：切页时两页各拍一张位图，真控件全部藏起来，body 自己按整数偏移贴这两张图。
        // 双缓冲面板贴图 = 一帧只画一次，文字是同一份光栅，不重排、不重画、不抖。
        Bitmap _slideA, _slideB;        // A = 旧页（滑出）B = 新页（滑入）
        int _slideAx, _slideBx;




        // "正在翻页"以动画状态为准，不看定时器 —— 定时器被别的东西停掉时防连点也不能失效
        bool Animating { get { return _animFrom >= 0 && _animTo >= 0 && _animT < 1f; } }

        void ShowPage(int i) { ShowPage(i, true); }









        // 界面上所有控件的值写回 Settings 的逻辑已拆到 75b-SettingsForm.Save.cs

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
            BufferedGrid g = new BufferedGrid();     // 双缓冲页容器：整页滑进滑出不会闪
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

        Label MkLabel(string t)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = t;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = Pad(0, 10, 12, 0);
            return l;
        }

        Label Section(string t)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = t;
            l.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(0, 122, 204);
            l.Margin = Pad(0, 14, 0, 2);
            return l;
        }

        Control Gap(int w)
        {
            Control c = new Control();
            c.Width = S(w); c.Height = 1;
            c.Margin = new Padding(0);
            return c;
        }

        NumericUpDown Num(int mn, int mx, int val)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = mn; n.Maximum = mx; n.Value = val;
            n.Width = S(72);
            n.Height = S(26);
            n.Margin = Pad(0, 7, 10, 0);
            return n;
        }

        FlowLayoutPanel Row(params Control[] cs)
        {
            RowPanel f = new RowPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = false;
            f.Margin = Pad(0, 5, 0, 5);   // 行距：设置项变多了，压紧一点免得窗口太高（跟着 DPI 走）
            for (int i = 0; i < cs.Length; i++) f.Controls.Add(cs[i]);
            return f;
        }
    }

    // ============================ 双缓冲容器 ============================
    // 翻页是把整页容器在窗口里挪位置，单缓冲的话每次挪动都要"擦底再画"，
    // 里面的文字看起来就在闪。Panel/TableLayoutPanel 默认都是单缓冲，这里统一开成双缓冲。
    class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
            DoubleBuffered = true;
        }
    }

    class BufferedGrid : TableLayoutPanel
    {
        public BufferedGrid()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
            DoubleBuffered = true;
        }
    }

    // ============================ 一行控件（行容器） ============================
    // 高度用**子控件的真实底边**兜底，不能只信 FlowLayoutPanel 自己算出来的数。
    // 起因（v0.5.2 用户报的"控件被裁"）：ComboBox 继承窗口字体（9.5pt 雅黑）之后真实高度是 27px，
    // 但它对外报的"首选高度"是 23px —— 于是 AutoSize 的行只有 29px 高，组合框的底边和下拉箭头
    // 被整整裁掉 4px。数值框（24px）则是刚好贴边。四页 12 个组合框全中。
    // 这里只在"算出来的比子控件实际需要的矮"时补高，其余行一个字都不动。
    class RowPanel : FlowLayoutPanel
    {
        public RowPanel()
        {
            // 行容器也会跟着页容器一起挪，同样要双缓冲（用户报的"字在闪"）
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
            DoubleBuffered = true;
        }

        public override Size GetPreferredSize(Size proposed)
        {
            Size s = base.GetPreferredSize(proposed);
            int need = 0;
            foreach (Control c in Controls)
                if (c.Visible) need = Math.Max(need, c.Bottom + c.Margin.Bottom);
            need += Padding.Bottom;
            if (need > s.Height) s.Height = need;     // 谁大听谁的：底边永远不被裁
            return s;
        }
    }

    // ============================ 轮盘式分页器 ============================
    // 顶部一个小圆弧，四个扇区 = 四页：与主界面同一套视觉语言（新拟态的"上亮下暗"凸起感），
    // 当前页高亮成主题色、数字变白。点扇区翻页，滚轮由 SettingsForm 的消息过滤器接管。
    class PageDial : Control
    {
        public string[] Names = new string[0];
        public float K = 1f;                // DPI 缩放系数（由 SettingsForm 传进来；只作用在Lang.T("长度", "Length")上）
        public int Current;
        // 刚被点中的扇区（交给 SettingsForm 决定要不要翻：动画期间它会忽略，所以这里不自己改 Current）
        public int Picked { get; set; }
        // 高亮过渡：AnimT=0 时高亮全在 AnimFrom、=1 时全在 AnimTo（由 SettingsForm 的翻页动画推）
        public int AnimFrom { get; set; }
        public int AnimTo { get; set; }
        public float AnimT { get; set; }
        public event EventHandler PagePicked;
        int _hover = -1;

        static readonly Color Accent = Color.FromArgb(0, 122, 204);
        static readonly Color Surface = Color.FromArgb(238, 240, 245);
        static readonly Color SurfaceHot = Color.FromArgb(246, 249, 253);
        static readonly Color NumIdle = Color.FromArgb(112, 120, 134);
        static readonly Font NumFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        static readonly Font TitleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);

        const float SweepTotal = 150f;      // 整个圆弧张开的度数（其余留白，看起来才像"顶部一小段弧"）
        const float BandW = 24f;            // 弧的厚度（乘以 K 才是实际像素）
        const float LiftPx = 2.2f;          // 当前页那一瓣往外凸出去多少（凸起也是平滑过渡的，乘以 K）

        // ---- 按住拖动转环（v0.5.2 追加：和主界面轮盘"能转"的手感对齐）----
        // 环整圈 = 四瓣 = 150°（SweepTotal），所以"转回正位"的周期就是 150°：
        // 角度 ≡ 0 (mod 150) 时每一页都恰好落在自己那一瓣的位上 = 和静态布局逐像素相同的样子。
        // 拖动：整环跟着指针连续转（增量累加，快速来回/转好几圈都不丢），转到指位上的那一瓣高亮；
        // 松手：ease-out 回到"离松手角度最近的正角度"，最多回弹 75°。
        public const int DragSlop = 4;      // 按下后位移 < 4px 算点击（点扇区切页），≥ 4px 算拖动
        public const int SnapMs = 160;      // 吸附时长：和翻页过渡一个量级（140~200ms）
        public float Angle { get; private set; }        // 环当前旋转角（度）
        public float TargetAngle { get; private set; }  // 这次吸附的目标角（静止时 ≡ 0 mod 150）
        public bool Dragging { get { return _drag == 2; } }
        public event EventHandler PageDropped;          // 拖动松手：Current 才是用户选的那一页
        int _drag;                 // 0=没按 1=按着（还没过阈值）2=正在拖
        Point _downPt;
        float _downAngle;          // 按下那一刻的指针角
        float _lastPtAngle;        // 上一次的指针角（按增量累加，绕圈/快速来回都不跳）
        int _grabPage;             // 按下时指针在哪一瓣上
        System.Windows.Forms.Timer _snapTimer;
        System.Diagnostics.Stopwatch _snapWatch;
        float _snapFrom;
        int _wheelSteps;           // 滚轮从上次静止起累计转过的格数（停手后吸附回正角度时清零）

        public PageDial()
        {
            AnimT = 1f;                       // 默认"过渡已完成"：高亮就停在 AnimTo（也就是 Current）上
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        // 自己的定时器自己停（AGENT-NOTES：#8 窗口关了定时器还在跑）
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_snapTimer != null) { try { _snapTimer.Stop(); _snapTimer.Dispose(); } catch { } _snapTimer = null; }
                if (_snapWatch != null) { try { _snapWatch.Stop(); } catch { } _snapWatch = null; }
            }
            base.Dispose(disposing);
        }

        // 这一瓣的"高亮权重" 0..1：翻页时旧页 1->0、新页 0->1，两边同时走
        float Weight(int i)
        {
            float w = 0f;
            if (i == AnimTo) w += AnimT;
            if (i == AnimFrom) w += 1f - AnimT;
            return w > 1f ? 1f : (w < 0f ? 0f : w);
        }

        // 颜色按权重插值（不是改透明度：GDI 画字不吃 alpha，插颜色才真的平滑）
        static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(a.A + (int)Math.Round((b.A - a.A) * t),
                                  a.R + (int)Math.Round((b.R - a.R) * t),
                                  a.G + (int)Math.Round((b.G - a.G) * t),
                                  a.B + (int)Math.Round((b.B - a.B) * t));
        }

        int Count { get { return Names == null ? 0 : Names.Length; } }
        float Cx { get { return Width / 2f; } }
        float Cy { get { return Height - 4f; } }                    // 圆心落在控件底边上：只露出上半圆
        float Ro { get { return Math.Min(92f * K, Height - 8f); } }
        float Ri { get { return Ro - BandW * K; } }

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

        // 第 i 瓣在"环转了 rot 度"之后的位置
        void SectorAt(int i, float rot, out float start, out float sweep, out PointF mid)
        {
            int n = Math.Max(1, Count);
            sweep = SweepTotal / n;
            start = 270f - SweepTotal / 2f + sweep * i + rot;
            double a = (start + sweep / 2f) * Math.PI / 180.0;
            float mr = (Ri + Ro) / 2f;
            mid = new PointF(Cx + (float)(Math.Cos(a) * mr), Cy + (float)(Math.Sin(a) * mr));
        }

        // 角度归一化：环每 150° 重复一次，所以画/算都只用最靠近 0 的那一份（视觉完全一样，数字不会越滚越大）
        static float Norm150(float a)
        {
            a = (float)(a - 150.0 * Math.Floor((a + 75.0) / 150.0));   // 落到 (-75, 75]
            return a;
        }

        // 指针相对圆心的角度（度，0=正右，顺时针为正）
        float PointerAngle(Point p)
        {
            double a = Math.Atan2(p.Y - Cy, p.X - Cx) * 180.0 / Math.PI;
            return (float)a;
        }

        // 两个指针角的差（归到 (-180,180]，这样绕着圆心转也不会跳）
        static float DeltaDeg(float now, float prev)
        {
            float d = (float)((now - prev + 540.0) % 360.0 - 180.0);
            return d;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            int n = Count;
            if (n <= 0) return;
            float rot = Angle;      // 环的旋转

            // 只画"窗口"那一段：把窗口环带当裁剪区，每瓣再按 ±150° 各画一份，
            // 于是转出去的那部分会从另一头转进来 —— 圆弧永远是一段完整的四瓣，不会缺角、也不会歪。
            using (GraphicsPath win = Band(Cx, Cy, Ri, Ro, 270f - SweepTotal / 2f, SweepTotal))
            {
                GraphicsState st = g.Save();
                g.SetClip(win, CombineMode.Replace);
                for (int k = -1; k <= 1; k++)
                {
                    float r2 = rot + k * SweepTotal;
                    for (int i = 0; i < n; i++)
                    {
                        float start, sweep; PointF mid;
                        SectorAt(i, r2, out start, out sweep, out mid);
                        float w = Weight(i);
                        bool hot = (i == _hover) && w < 0.5f && _drag == 0;
                        // 高亮 = 从"常态底"往主题色插值；同时整瓣沿半径往外凸一点（凸起跟着高亮一起走）
                        double midA = (start + sweep / 2f) * Math.PI / 180.0;
                        float lift = LiftPx * K * w;
                        float ox = (float)(Math.Cos(midA) * lift), oy = (float)(Math.Sin(midA) * lift);
                        using (GraphicsPath p = Band(Cx + ox, Cy + oy, Ri, Ro, start + 1.2f, sweep - 2.4f))
                        {
                            RectangleF box = p.GetBounds();
                            // 凸起感：顶上一条高光、底下一条暗边（GlassPanel 一上一下，跟轮盘控件同一套路）
                            Color fill = Mix(hot ? SurfaceHot : Surface, Accent, w);
                            int hi = (int)Math.Round(190 + (120 - 190) * w);
                            int shade = (int)Math.Round(46 + (80 - 46) * w);
                            Gfx.GlassPanel(g, p, box, fill, hi, shade, true);
                            if (w > 0.01f)
                                using (Pen pen = new Pen(Color.FromArgb((int)Math.Round(120 * w), 255, 255, 255), 1.2f))
                                { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawPath(pen, p); }
                        }
                    }
                }
                g.Restore(st);
            }

            // 扇区里的序号：只画"整块都在窗口里"的那些（贴边的半瓣不画，免得半个数字挂在弧外）
            for (int k = -1; k <= 1; k++)
            {
                float r2 = rot + k * SweepTotal;
                for (int i = 0; i < n; i++)
                {
                    float start, sweep; PointF mid;
                    SectorAt(i, r2, out start, out sweep, out mid);
                    if (mid.X < 8 * K || mid.X > Width - 8 * K) continue;
                    // 中心角必须落在窗口内（留一点余量给数字本身）
                    double rel = (start + sweep / 2f) - (270.0 - SweepTotal / 2.0);
                    if (rel < 0) rel += 360.0;
                    if (rel < 13 || rel > SweepTotal - 13) continue;
                    Rectangle numRc = new Rectangle((int)mid.X - (int)(12 * K), (int)mid.Y - (int)(9 * K), (int)(24 * K), (int)(18 * K));
                    TextRenderer.DrawText(g, (i + 1).ToString(), NumFont, numRc,
                        Mix(NumIdle, Color.White, Weight(i)),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }

            // 圆环内圈里写页名：翻页时走到一半换字（不叠字、不跳页）
            int show = (AnimT < 0.5f && AnimFrom >= 0 && AnimFrom < n) ? AnimFrom : Current;
            if (show < 0 || show >= n) show = AnimTo >= 0 && AnimTo < n ? AnimTo : 0;
            string t = (show >= 0 && show < n) ? Names[show] : "";
            int tw = TextRenderer.MeasureText(t, TitleFont).Width + (int)(12 * K);
            Rectangle rc = new Rectangle((int)(Cx - tw / 2f), (int)(Cy - Ri + 34f * K), tw, (int)(26 * K));
            TextRenderer.DrawText(g, t, TitleFont, rc, Color.FromArgb(64, 70, 82),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // 命中的扇区；没命中返回 -1（环带内外各放宽 6px，好点一点）。带上环的旋转。
        int HitTest(Point p)
        {
            int n = Count;
            if (n <= 0) return -1;
            float dx = p.X - Cx, dy = p.Y - Cy;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            if (d > Ro + 6f || d < Ri - 6f) return -1;
            double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (ang < 0) ang += 360.0;
            double rel = ang - (270.0 - SweepTotal / 2.0) - Angle;   // 减掉环的旋转
            while (rel < 0) rel += 360.0;
            while (rel >= 360.0) rel -= 360.0;
            if (rel > SweepTotal) return -1;
            int idx = (int)(rel / (SweepTotal / n));
            return idx < n ? idx : n - 1;
        }

        // 拖动中"转到指位上的那一瓣"变成当前页：环转过的瓣数直接把选中页往回推
        void UpdateDragPage()
        {
            int n = Math.Max(1, Count);
            int steps = (int)Math.Round(Angle / (SweepTotal / n));
            int p = ((_grabPage - steps) % n + n) % n;
            if (p != Current)
            {
                Current = p;
                AnimFrom = p; AnimTo = p; AnimT = 1f;   // 拖动中不做补间：指到哪亮到哪（页名也跟着）
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            CancelSnap();                       // 上一次吸附没走完就直接接管：从当前角度接着拖，不先跳回去
            _drag = 1;
            _downPt = e.Location;
            _grabPage = HitTest(e.Location);
            if (_grabPage < 0) _grabPage = Current < 0 ? 0 : Current;   // 按在弧外也允许拖
            _downAngle = PointerAngle(e.Location);
            _lastPtAngle = _downAngle;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag == 0)
            {
                int i = HitTest(e.Location);
                if (i != _hover) { _hover = i; Invalidate(); }
                return;
            }
            if (_drag == 1)
            {
                int dx = e.X - _downPt.X, dy = e.Y - _downPt.Y;
                if (dx * dx + dy * dy < DragSlop * DragSlop) return;    // 还没过阈值：仍按"可能是点击"处理
                _drag = 2;
                Capture = true;
                // 基准取"按下那一刻"的指针角：环从按下点开始跟手，位移一点都不丢
                //（阈值只有 4px ≈ 3°，所以过阈值那一下最多也就 3°，看不出来）
                _lastPtAngle = _downAngle;
            }
            // 正在拖：环跟着指针连续转（增量累加，绕圈、快速来回都不丢）
            float r = (float)Math.Sqrt((e.X - Cx) * (e.X - Cx) + (e.Y - Cy) * (e.Y - Cy));
            float a = PointerAngle(e.Location);
            if (r < 8f) { _lastPtAngle = a; return; }   // 指针贴着圆心：角度没意义，只更新基准，离开时不跳
            Angle = Norm150(Angle + DeltaDeg(a, _lastPtAngle));
            _lastPtAngle = a;
            UpdateDragPage();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag == 0) return;
            int st = _drag;
            _drag = 0;
            if (Capture) Capture = false;
            if (st == 2) { FinishDrag(); return; }
            // 没过阈值 = 点击：点哪一瓣翻哪一页（老行为，只是改到松手时判定，才能和拖动区分）
            int i = HitTest(e.Location);
            if (i >= 0 && i != Current)
            {
                Picked = i;
                if (PagePicked != null) PagePicked(this, EventArgs.Empty);
            }
        }

        // 捕获被抢走（松手在窗口外、切窗口…）也照样收尾，绝不留半路状态
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_drag != 0 && !Capture) { _drag = 0; FinishDrag(); return; }
            if (_drag == 1) _drag = 0;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_drag != 0) return;                 // 拖着的时候移出窗口不算离开（有捕获，继续拖）
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        // 滚轮用：环往"翻到下一页/上一页"的方向转一格（有动画），停手后 SnapTick 会接着吸附回正角度。
        // 方向和拖动一致：往后翻一页 = 环往负方向转（东西从右边转进来）。
        public void Nudge(int pageDir)
        {
            if (_drag != 0) return;                                  // 拖着的时候滚轮不参与
            _wheelSteps += pageDir;
            AnimateRingTo(-(SweepTotal / Math.Max(1, Count)) * _wheelSteps);
        }

        // 让环从当前角度 ease-out 转到目标角度（拖动收尾和滚轮共用一套）
        void AnimateRingTo(float target)
        {
            TargetAngle = target;
            _snapFrom = Angle;
            _snapWatch = System.Diagnostics.Stopwatch.StartNew();
            if (_snapTimer == null)
            {
                _snapTimer = new System.Windows.Forms.Timer();
                _snapTimer.Interval = 15;           // 和翻页过渡同一个节拍
                _snapTimer.Tick += delegate(object o, EventArgs e2) { SnapTick(); };
            }
            _snapTimer.Start();
            Invalidate();
        }

        // 松手：吸附到离当前角度最近的正角度（≡0 mod 150°，也就是每页都回到自己扇区位的那个角度）
        void FinishDrag()
        {
            _wheelSteps = 0;
            AnimateRingTo((float)(Math.Round(Angle / SweepTotal) * SweepTotal));
            if (PageDropped != null) PageDropped(this, EventArgs.Empty);
        }

        // 直接接管（用户按下时上一次吸附还没走完）：角度保持不动，从当前进度接着拖
        void CancelSnap()
        {
            if (_snapTimer != null && _snapTimer.Enabled) _snapTimer.Stop();
            if (_snapWatch != null) { try { _snapWatch.Stop(); } catch { } _snapWatch = null; }
        }

        void SnapTick()
        {
            float t = _snapWatch == null ? 1f : (float)(_snapWatch.Elapsed.TotalMilliseconds / SnapMs);
            if (t >= 1f)
            {
                Angle = TargetAngle;                // 精确落到目标角，不留偏差
                CancelSnap();
                Invalidate();
                float home = (float)(Math.Round(Angle / SweepTotal) * SweepTotal);
                if (Math.Abs(Angle - home) > 0.0001f) AnimateRingTo(home);   // 滚轮转出去的那一格：接着吸附回正角度
                else _wheelSteps = 0;
                return;
            }
            Angle = _snapFrom + (TargetAngle - _snapFrom) * Gfx.EaseOut(t);
            Invalidate();
        }
    }
}

namespace SnapWheel
{
    // ============ 内容比窗口高时的"整块滚动"（给说明类窗口用） ============
    // 为什么需要它：说明窗口的内容是按 DPI 放大后**按内容长高**的（150% 下引导窗口内容约 1500px），
    // 再高就超过屏幕工作区 —— 那时候**不是字被裁，而是整个按钮被顶到屏幕外面**，同样叫"显示不全"。
    // 所以：窗口高度夹到工作区以内，装不下的部分用滚轮上下翻。
    //
    // 刻意**不用** Panel/AutoScroll：那会盖住窗口的半透明毛玻璃底（Panel 要不透明才不闪），
    // 而这里只需要把子控件整块上下挪 —— 挪出窗口的部分由窗口自己裁掉，背景一动不动。
    class UiScroll
    {
        readonly List<Control> _cs = new List<Control>();
        readonly List<int> _top0 = new List<int>();
        int _scroll, _contentH, _viewH;

        public bool Active { get { return _contentH > _viewH; } }
        public int Scroll { get { return _scroll; } }

        // 把一个内容控件登记进来（登记时它已经在最终位置上了，这里记下原始 Top）
        public void Add(Control c)
        {
            _cs.Add(c);
            _top0.Add(c.Top);
        }

        // 清空登记（重排用：宽度变了要把旧的标签全丢掉重新排）
        public void Reset() { _cs.Clear(); _top0.Clear(); _scroll = 0; }

        // 排完版调用：contentH = 内容理想高度，viewH = 实际能看到的区域高度
        public void Finish(int contentH, int viewH)
        {
            _contentH = contentH;
            _viewH = viewH;
            _scroll = 0;
            Apply();
        }

        // 滚轮：一格的位移按 DPI 走（96dpi 下一格 48px）
        public bool Wheel(int delta)
        {
            if (!Active) return false;
            return ScrollBy(-(delta / 120) * Ui.S(48));
        }

        public bool ScrollBy(int dy)
        {
            if (!Active) return false;
            int max = _contentH - _viewH;
            int was = _scroll;
            _scroll += dy;
            if (_scroll < 0) _scroll = 0;
            if (_scroll > max) _scroll = max;
            if (_scroll == was) return false;
            Apply();
            return true;
        }

        void Apply()
        {
            for (int i = 0; i < _cs.Count; i++)
                _cs[i].Top = _top0[i] - _scroll;
        }
    }

    // 新手上路 / 升级说明窗口。
    //
    // ⚠️ DPI（0.5.3 修订）：这个窗口原来**每个坐标都是写死的像素**，而程序是 per-monitor DPI aware 的 ——
    // 150% 缩放下字体按 DPI 放大 1.5 倍、格子却还是原来那么大，于是长句子右半截被裁、
    // 说明文字只看得见第一行（用户报的「引导界面 / 新手引导显示不全」就是这个）。
    // 现在的规矩：
    //   · 长度（坐标 / 宽 / 高 / 行距 / 按钮）一律过 Ui.S() 乘 DPI 系数；
    //   · 说明文字交给 Ui.Wrap()：自己折行、自己报高度 —— 比"把高度算准"可靠；
    //   · 窗口 ClientSize 最后按内容算一次；**再夹到屏幕工作区以内**，装不下的部分滚轮翻（见 UiScroll）；
    //   · 底部按钮固定贴在窗口底边 —— 永远看得见，滚到哪儿都点得到。
    class GuideForm : Form
    {
        // ---- 版面常量（逻辑像素；用的时候都乘 K）----
        const int WinW = 540;
        const int PadL = 28, PadR = 28, PadT = 24;
        const int Gutter = 16;      // 正文相对条目标题的右缩进
        const int RowGap = 14;      // 相邻两条之间的空隙
        const int BtnH = 38;

        int _contentW;              // 已乘 K 的内容宽（= 窗口宽 - 左右边距）
        int _tipCount;              // 已经加了几条说明（首次安装时只显示前 3 条，见 AddTip）
        string _seenVer;            // 用户上次看过引导时的版本号；比它新的说明才标【新】
        bool _brief;                // true = 首次安装的欢迎引导：**只显示前 3 条**，别一上来丢长文
        readonly UiScroll _sc = new UiScroll();
        Label _scrollHint;
        // 可缩放重排要用的：标题、按钮行高、按钮本身
        string _title, _subtitle;
        int _btnRowH;
        RoundButton _go;

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

        // 滚轮上下翻内容（窗口本身不滚动，只是整块挪）
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _sc.Wheel(e.Delta);
            base.OnMouseWheel(e);
        }

        // 设置里调出来的那次：传当前版本当"看过的版本" —— 用户是主动来翻手册的，
        // 不该满屏【新】（那不是"这次更新"，是参考手册）。
        public GuideForm() : this(AppInfo.Name + " 快照轮环 · 使用说明", Lang.T("轮盘平时就待在屏幕角落里，用鼠标滚轮就能翻图。", "The ring sits in a screen corner; scroll the mouse wheel over it to browse."), AppInfo.Version, false) { }

        // firstEver=true：全新安装的欢迎引导（**只给三步上手**，不丢长文）；
        // false：升级后自动弹的"这次多了什么"（展示全部；比 seenVer 新的那几条带【新】）
        //
        // seenVer 必须由调用方**先存下来再传进来**：App 在弹窗之前就把
        // GuideSeenVersion 写成当前版本了，这里再去读配置只会读到新值，什么都标不出来。
        public GuideForm(bool firstEver, string seenVer) : this(
            firstEver ? Lang.T("欢迎用 SnapWheel 快照轮环", "Welcome to SnapWheel") : (Lang.T("SnapWheel 更新到 v", "SnapWheel updated to v") + AppInfo.Version),
            firstEver ? Lang.T("三件事就能用起来：下面这三条。更多细节在「设置 → 新手引导」里。", "Three things and you are set - see below. More detail is in Settings > Getting started.")
                      : Lang.T("这次加了新东西 —— 下面标了「新」的几条就是，一分钟看完就能用上。", "Something new in this version - the items marked [NEW] below; a minute to read and you are using them."),
            seenVer, firstEver) { }

        GuideForm(string title, string subtitle, string seenVer, bool brief)
        {
            _seenVer = seenVer == null ? "" : seenVer;
            _brief = brief;
            Text = AppInfo.Name + " 新手上路";
            _title = title; _subtitle = subtitle;
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            // 可缩放：和设置界面一样。**不再把高度夹在工作区的 72%** —— 那条上限正是
            // "小屏笔记本上字被下边缘切掉、又没法把窗口拖大"的直接原因。
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Ui.S(WinW), Ui.S(200));     // 先占位，最后按内容重算
            SuspendLayout();

            Rectangle wa = WorkArea();
            int winW = Ui.S(WinW);
            int maxW = (int)(wa.Width * 0.92) - Ui.S(16);     // 别顶满屏幕，留点边
            if (winW > maxW) winW = Math.Max(Ui.S(320), maxW);
            int mL = Ui.S(PadL), mR = Ui.S(PadR);
            _contentW = winW - mL - mR;

            int y = BuildTips(mL, title, subtitle);   // 内容整套排一遍（宽度变了会再排，见 OnResize）

        // ---- 底部按钮行（不参与滚动，永远贴窗口底边）----
            int btnRowH = Ui.S(10) + Ui.S(BtnH) + Ui.S(22);     // 底部按钮行占的高度（含上下留白）
            // 内容总高要把**按钮行**也算进去：按钮是贴底固定的、不参与滚动，
            // 不算的话滚到最后几行会被按钮压住（用户反馈"字被确认按钮遮住"）。
            _btnRowH = btnRowH;
            int contentH = y + btnRowH;

            int btnY = contentH + Ui.S(10);                    // 按钮的"内容坐标"
            RoundButton go = new RoundButton();
            _go = go;
            go.Text = Lang.T("开始使用", "Get started");
            go.Size = Ui.Sz(124, BtnH);
            go.Fill = Color.FromArgb(0, 122, 204);
            go.FillHover = Color.FromArgb(0, 140, 232);
            go.TextColor = Color.White;
            go.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            go.Location = new Point(winW - mR - go.Width, btnY);
            go.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Add2Raw(go);                    // 贴底固定：**不**参与滚动，滚到哪儿都看得见
            AcceptButton = go;

            _scrollHint = new Label();
            _scrollHint.Text = Lang.T("（内容较多，鼠标滚轮可上下翻看）", "(long page - use the mouse wheel to scroll)");
            _scrollHint.ForeColor = Color.FromArgb(150, 152, 160);
            _scrollHint.AutoSize = true;
            _scrollHint.Location = new Point(mL, btnY + (go.Height - _scrollHint.Font.Height) / 2);
            Add2Raw(_scrollHint);

            // ---- 定尺寸：按内容长高，但绝不超过屏幕的 72% ----
            // 上限不能贴着屏幕高度（原来是 wa.Height - 24）：内容一多窗口就顶满整个屏幕，
            // 看着非常压迫（用户反馈"新手引导的窗口也太长了"）。内容超出时靠滚轮翻看就够了。
            int idealH = contentH;                             // contentH 里已经含了按钮行，别再重复加
            // 初始高度仍按工作区的 72%：这是用户反馈"新手引导窗口也太长了"之后定下来的。
            // 小屏上内容放不下**不再靠"把上限调大"解决**，而是靠窗口**可缩放**（见 OnResize）——
            // 用户想宽自己拖一下，拖了会按新宽度整套重排。
            int maxH = (int)(wa.Height * 0.72);
            if (maxH < Ui.S(260)) maxH = Ui.S(260);
            int winH = Math.Min(idealH, maxH);
            ClientSize = new Size(winW, winH);

            int viewH = winH - btnRowH;                        // 内容区能露出来的高度
            if (_scrollHost != null) _scrollHost.Size = new Size(winW, viewH);   // 宿主的可视高度：超出部分会被裁掉
            _sc.Finish(contentH, viewH);
            _scrollHint.Visible = _sc.Active;

            // 按钮/提示固定在底边（不随内容滚动）
            go.Top = winH - Ui.S(22) - go.Height;
            _scrollHint.Top = go.Top + (go.Height - _scrollHint.Font.Height) / 2;

            ResumeLayout();
        }

        // 滚动内容的宿主 Panel。
        //
        // 为什么必须有个 Panel：滚动是靠移动子控件的 Location 实现的，而 **WinForms 的窗体
        // 不会裁剪子控件** —— 超出窗口的内容照样画出来。用户反馈的"字把开始使用按钮遮住"
        // 就是这么来的：某个标签的 Y=773 压在了按钮的 Y=769 上。
        // Panel 会裁剪自己的子控件，放进来的内容滚到边界就被切断，永远到不了按钮那一层。
        Panel _scrollHost;

        // 登记一个控件：加进滚动宿主（会被裁剪），也告诉滚动器它的原始位置
        // 把"大标题 + 副标题 + 全部说明"整套排一遍，返回内容总高。
        //
        // 为什么抽成方法：窗口现在是**可缩放**的（和设置界面一样），宽度一变，
        // 每一段文字都要重新折行、每段的高度也跟着变 —— 必须整套重排，只改坐标是错的。
        // 重排前先把滚动宿主里的旧标签丢掉、并清掉滚动器的登记，否则旧的会留在原地叠着。
        // 拖动窗口时**绝不能每动一下就整套重排**。
        // BuildTips 要重建几十个标签、还要逐段量文字，一次几十毫秒 ——
        // 拖动中 OnResize 每动一下就触发，全跑一遍就是"拖起来一顿一顿、不连贯"的来源。
        //
        // 所以拆成两半：
        //   · 便宜的（按钮和提示条贴底、可视高度）**立刻**做 —— 手一动就跟着走；
        //   · 贵的（重新折行、重算高度）**等手停下来**再做（下面那个 140ms 的定时器）。
        System.Windows.Forms.Timer _relayout;

        void ScheduleRelayout()
        {
            if (_relayout == null)
            {
                _relayout = new System.Windows.Forms.Timer();
                _relayout.Interval = 140;
                _relayout.Tick += delegate(object o, EventArgs e2) { _relayout.Stop(); DoRelayout(); };
            }
            _relayout.Stop();
            _relayout.Start();
        }

        void DoRelayout()
        {
            if (_go == null || _title == null) return;
            int mL = Ui.S(PadL), mR = Ui.S(PadR);
            _contentW = Math.Max(Ui.S(200), ClientSize.Width - mL - mR);
            int contentH = BuildTips(mL, _title, _subtitle) + _btnRowH;
            FitScroll(contentH);
            Gfx.RepaintAll(this);
        }

        // 按钮和提示条永远贴着窗口**下沿**。
        // ⚠️ 提示条是**跟着按钮走的**（见构造函数里那句），拖窗口时必须一起挪 ——
        //    我上一版只挪了按钮、漏了它，于是"内容较多的小提示不随窗口下沿动"。
        void StickBottom()
        {
            if (_go == null) return;
            _go.Top = ClientSize.Height - Ui.S(22) - _go.Height;
            if (_scrollHint != null)
                _scrollHint.Top = _go.Top + (_go.Height - _scrollHint.Font.Height) / 2;
        }

        void FitScroll(int contentH)
        {
            int viewH = ClientSize.Height - _btnRowH;
            if (viewH < Ui.S(80)) return;
            if (_scrollHost != null) _scrollHost.Size = new Size(ClientSize.Width, viewH);
            _sc.Finish(contentH, viewH);
            if (_scrollHint != null) _scrollHint.Visible = _sc.Active;
            StickBottom();
        }

        // 窗口被拖动：便宜的那半立刻做，贵的那半排程。
        // 这就是"和设置界面一样"的那一步 —— 宽度一变，每段文字重新折行、高度重算，
        // 而不是只把窗口拉大、内容还按老宽度切着。
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_go == null || _title == null) return;          // 布局还没建完
            StickBottom();                                       // 按钮 + 提示条：立刻跟着下沿走
            int viewH = ClientSize.Height - _btnRowH;
            if (viewH >= Ui.S(80) && _scrollHost != null)
                _scrollHost.Size = new Size(ClientSize.Width, viewH);
            ScheduleRelayout();                                  // 重新折行：等手停下来
        }

        int BuildTips(int x, string title, string subtitle)
        {
            _tipCount = 0;
            if (_scrollHost != null) { _scrollHost.Controls.Clear(); _sc.Reset(); }
            Label head = new Label();
            head.Text = title;
            head.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.Location = new Point(x, Ui.S(PadT));
            W(head, _contentW);
            Add2(head);

            int y = head.Top + head.PreferredSize.Height + Ui.S(8);

            Label sub = new Label();
            sub.Text = subtitle;
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.Location = new Point(x + Ui.S(3), y);
            W(sub, _contentW - Ui.S(3));
            Add2(sub);

            y += TxtH(sub, _contentW - Ui.S(3)) + Ui.S(16);

            AddTip(x, ref y, "0.1.0", Lang.T("第 1 步：截一张", "Step 1: capture"), Lang.T("按 ", "Press ") + Settings.Load().Hotkey + Lang.T(" 拖框选区域，四角缩放、拖旋转键转角度，双击/回车确认。", " drag to select; corners resize, the dial rotates, double-click / Enter confirms."));
            AddTip(x, ref y, "0.1.0", Lang.T("截完直接标注", "Annotate right after capturing"), Lang.T("浮层上有条工具条：箭头 / 方框 / 马赛克 / 文字，四个颜色可选，Ctrl+Z 撤销、Ctrl+Y 重做。确认之后标注就跟着图一起进轮盘 —— 圈重点不用再去别的软件。", "The overlay has a toolbar: arrow / box / mosaic / text, four colours, Ctrl+Z to undo and Ctrl+Y to redo. Annotations are baked into the image that lands in the ring - no separate editor needed."));
            AddTip(x, ref y, "0.1.0", Lang.T("第 2 步：拖出去（最常用）", "Step 2: drag it out (the everyday use)"), Lang.T("把环上的缩略图直接拖进微信 / QQ / 文档 / 文件夹，松开就发出去 —— 不用先保存、再选文件。这一下就是它的全部意义。", "Drag a thumbnail straight into WeChat / Word / a folder and release - no saving, no picking files. That is the whole point."));
            AddTip(x, ref y, "0.9.0", Lang.T("不用鼠标也能把图送出去：传递模式", "Send an image without the mouse: carry mode"),
                Lang.T("先滚轮选好要发的那张，按 ", "Pick the image with the wheel first, then press ")
                + AppCtx.CarryHotkeyName
                + Lang.T(" 进入传递模式：屏幕上会出现一个「假光标」，右下角吸附着那张缩略图。"
                       + "你自己 Alt+Tab 切到微信 / 文档，用方向键（或 WASD）把假光标移过去，按空格放下 —— 它会真的替你完成一次鼠标拖放，"
                       + "所以任何支持拖放的窗口都适用。[ ] 换一张、Shift 加速、C 只复制不粘贴、Esc 取消。",
                         " to enter carry mode: a fake cursor appears with that thumbnail attached to it. "
                       + "Alt+Tab to your target window yourself, steer the cursor with the arrow keys (or WASD) and press Space to drop - "
                       + "it performs a real mouse drag for you, so it works with any window that accepts drops. "
                       + "[ ] switch image, Shift = faster, C = copy only (no paste), Esc = cancel."));
            // ⚠️ 这一条原来是**两条**：「要对照着看：贴到屏幕上」(0.5.0) 和
            // 「截完不用等，直接贴到屏幕上」(1.0.0) —— 标题几乎一样、讲的都是贴图，
            // 只是方法不同（中键 / 浮层那颗钉子），却在列表里隔了好几条。
            // 读的人会以为是重复的。合成一条：先说最顺手的（框完直接点钉子），再说中键那条老路。
            AddTip(x, ref y, "1.0.0", Lang.T("贴到屏幕上：钉着对照看", "Pin it on screen"),
                Lang.T("**截图浮层工具条最右边那颗钉子**：框完点它，图立刻钉在你框的那块位置，"
                       + "**并且照常存进轮环** —— 不用再等轮盘拉出来。那颗钉子的底色是**常亮**的"
                       + "（这条栏上别的一律是深底细线条），一眼就能找到。"
                       + "也可以缩略图上按一下鼠标中键：滚轮缩放、拖着挪位置、双击或 Esc 关掉，"
                       + "托盘能一键收掉全部贴图。"
                       + "钉出来的图和轮环里是**各自独立**的：关掉贴图不会影响环上那张。",
                         "Click the **nail at the right end of the capture toolbar**: the shot is pinned where you framed it and it still goes into the ring, so there is no waiting for the wheel. The nail has a permanently lit background, so it is easy to find. You can also middle-click a thumbnail: scroll to zoom, drag to move, double-click or Esc to close, and the tray can close them all at once. The pinned copy and the ring copy are independent."));
            AddTip(x, ref y, "0.9.5", Lang.T("截图里少了某个窗口？（是那个程序自己不让人截）", "A window missing from the capture? (that app is blocking it)"),
                Lang.T("最典型的是微信：按下截图热键后，浮层上微信的位置直接是它背后的桌面 —— 看起来像「微信突然消失了」。"
                       + "这不是 SnapWheel 的问题：微信给 Windows 设了「把我排除在截屏之外」（WDA_EXCLUDEFROMCAPTURE），"
                       + "系统级的机制，任何用同一种方式抓屏的工具都一样拍不到它，包括 Windows 自带的截图。"
                       + "想确认的话按 Win+Shift+S 框住微信 —— 同样不在里面，那就是它在反截屏，不是谁坏了。"
                       + "顺手一提：SnapWheel 自己也给自己设了这个，否则轮盘会拍进你截的每一张图里。",
                         "The classic case is WeChat: after you press the capture hotkey, the frozen overlay shows whatever is "
                       + "behind WeChat where its window was - it looks like WeChat vanished. This is not a SnapWheel problem: "
                       + "WeChat tells Windows to exclude it from screen capture (WDA_EXCLUDEFROMCAPTURE). It is a system-level "
                       + "switch, so every tool that captures the same way misses it - including the built-in Windows snip. "
                       + "To confirm: press Win+Shift+S over WeChat and it will be missing there too. For the record, SnapWheel "
                       + "sets the same flag on itself, otherwise the ring would appear in every screenshot you take."));
            AddTip(x, ref y, "0.1.0", Lang.T("反过来：拖回来", "Or the other way: drag it back"), Lang.T("从桌面、网页、聊天窗口里把图片拖到环带上松手，就收进轮盘了，随时能再拖出去。", "Drop an image from the desktop, a web page or a chat window onto the ring to keep it - drag it out again whenever you need it."));
            AddTip(x, ref y, "0.4.7", Lang.T("连拖都不用：复制即收纳", "Do not even drag: copy and it is collected"), Lang.T("在任何地方「复制」一张图（截图工具、网页右键、微信里都行），它会自动滑进轮盘。不想要可以在设置里关掉。", "Copy an image anywhere (a screenshot tool, a web page, WeChat) and it slides into the ring. Turn this off in settings if you do not want it."));
            AddTip(x, ref y, "0.1.0", Lang.T("按住看大图", "Hold to zoom"), Lang.T("缩略图按住约 0.3 秒放大预览，放大倍数在设置里可调。", "Hold a thumbnail for ~0.3 s to preview it enlarged; the zoom factor is adjustable in settings."));
            AddTip(x, ref y, "0.3.0", Lang.T("万能键（可以改成你要的）", "Universal key (rebindable)"), Lang.T("长按环内侧那个圆盘会弹出四个方向，往哪个方向松手就执行哪个动作。默认：上=新建轮盘，右=下一个，下=删除，左=上一个 —— 四个动作都能在设置里换。", "Long-press the dial inside the ring and four directions appear; release towards one to run that action. Defaults: up = new wheel, right = next, down = delete, left = previous - all rebindable in settings."));
            AddTip(x, ref y, "0.1.1", Lang.T("收起态（默认关）", "Collapsed mode (off by default)"), Lang.T("打开后不用时会缩成屏幕边上的小把手，点一下用彩虹动画拉出来。想让桌面更干净再开。", "When idle it shrinks into a small pull-tab at the screen edge; click it and the ring slides back out. Turn on for a tidier desktop."));
            AddTip(x, ref y, "0.1.0", Lang.T("删掉 / 撤回 / 退出", "Delete / undo / quit"),
                Lang.T("缩略图上点鼠标右键就能删（默认是单击即删，设置里可以改成「双击右键才删」，防手滑）。删错了不要紧："
                       + "托盘右键 →「撤销上一次删除」能找回来。另外环上那个关闭按钮，按住 0.65 秒松手 = 直接退出程序。",
                         "Right-click a thumbnail to delete it (single click by default; settings can require a double "
                       + "right-click to avoid slips). Deleted by mistake? Tray > Undo last delete brings it back. "
                       + "The close button on the ring, held for 0.65 s and released, quits the app outright."));
            AddTip(x, ref y, "0.1.0", Lang.T("托盘", "Tray"), Lang.T("托盘右键还有：导入图片、新手引导、重播开启动画、设置、退出。", "The tray menu also has: import images, getting started, replay startup animation, settings, exit."));
            AddTip(x, ref y, "0.5.0", Lang.T("取字：把图里的文字抠出来", "OCR: pull the text out of an image"),
                Lang.T("在截图浮层上选「取字」工具（或按 O），框住要识别的文字 —— 框得越紧越准。识别完可以直接一键翻译成中文或英文。"
                       + "不走截图也行：托盘右键 →「取字：识别剪贴板里的图」。用的是 Windows 自带的识别引擎，不需要联网。",
                         "In the capture overlay pick the OCR tool (or press O) and box the text - the tighter the box, the better. "
                       + "The result window can translate to Chinese or English in one click. Without capturing: tray > OCR the clipboard image. "
                       + "It uses the Windows built-in recognition engine, no network needed."));
            AddTip(x, ref y, "0.6.0", Lang.T("滚动长截图：一整页长图", "Scrolling capture: one long image"),
                Lang.T("截图浮层上点「长图」，然后自己慢慢往下滚页面，它会自动把各屏拼成一张完整的长图。"
                       + "**框选时只框会跟着滚的那块内容** —— 左边栏、浏览器标签栏、悬浮窗这些**不跟着滚**的东西，"
                       + "会被当成新内容一屏一屏地重复拼进去（这是长截图这种做法的边界，同类工具都一样）。"
                       + "任务栏那一条已经自动排除了，不用自己躲。",
                         "In the capture overlay click the long-shot button, then scroll the page yourself; it stitches the screens into one long image. "
                       + "Frame ONLY the part that scrolls: sidebars, browser toolbars and floating panels do not scroll, so they get stitched in again "
                       + "on every screen (that is the limit of this technique, the same for every tool). The taskbar is excluded automatically."));
            AddTip(x, ref y, "0.7.0", Lang.T("另存为 / 直接复制", "Save as / copy"),
                Lang.T("缩略图上按 Ctrl+S 可以「另存为」（选路径和格式）；直接点一下缩略图则是把这张图复制到剪贴板。",
                         "Ctrl+S on a thumbnail opens Save-as (path and format); a plain click copies that image to the clipboard."));
            AddTip(x, ref y, "0.7.0", Lang.T("符号标注：箭头 / 马赛克之外还有符号", "Symbols besides arrow / mosaic"),
                Lang.T("标注工具条里还有一组「符号」，可以从三排符号里挑一个盖在图上（标记、箭头、编号），"
                       + "符号的颜色和大小都能调，拖动可以挪位置。",
                         "The annotate toolbar also has a Symbols panel: pick from three rows (markers, arrows, numbers) and stamp it "
                       + "on the image. Colour and size are adjustable, and you can drag it around."));
            AddTip(x, ref y, "0.6.0", Lang.T("界面语言：中文 / English", "Interface language: Chinese / English"),
                Lang.T("设置 → 第一页 →「界面语言」可以选跟随系统 / 中文 / English。选完重启一次生效。",
                         "Settings > page 1 > Interface language: follow system / Chinese / English. Restart once to apply."));
            AddTip(x, ref y, "0.8.1", Lang.T("自动更新（免费、不用装任何东西）", "Auto update (free, nothing to install)"),
                Lang.T("托盘右键 →「检查更新」会去读项目的发行版页面，有新版本会问你要不要下载并安装 —— 程序自己重启一次就换好了，"
                       + "轮盘和设置都不会丢。设置里可以关掉「启动时检查」。",
                         "Tray > Check for updates reads the project's release page; if there is a newer version it asks whether to download "
                       + "and install. The app restarts once and everything is kept. You can turn off the startup check in settings."));

AddTip(x, ref y, "1.0.0", Lang.T("环现在会「有反应」了", "The ring reacts now"),
                Lang.T("这一版除了上面那颗钉子，补的主要是反馈：**拖出去**的时候那一格会颤一下、并向拖的方向留下一道短促的拖痕"
                       + "（默认是「留一份」，所以格子**不合拢** —— 图并没有走）；**新截的那张**会亮一下再慢慢冷下去，"
                       + "一眼就知道哪张是刚截的；图进来的时候环上会**扩散一圈涟漪**；**切轮盘**时名字药丸会翻一下"
                       + "（药丸和上面的字会**一起**放大缩小）。"
                       + "另外环会**随内容变粗**，早上偏暖、深夜自己暗一点，环下面也多了一层影子。"
                       + "涟漪 / 影子 / 时间感都能在「设置 → 风格」里单独关掉。",
                         "Besides the nail above, this release is mostly about feedback: dragging an image out makes its cell flinch "
                       + "and leaves a short trail in the drag direction (the default keeps a copy, so the cell does NOT close up - "
                       + "nothing actually left); a freshly captured image glows then cools, so you can see at a glance which one is "
                       + "new; a ripple spreads out when an image arrives; switching wheels flips the name pill (the pill and its "
                       + "text scale together). The ring also thickens with content, warms up in the morning, dims at night, and now "
                       + "casts a soft shadow. Ripple / shadow / time-of-day can each be turned off in Settings > Style."));

            int tipW = _contentW - Ui.S(3);
            Label tip = new Label();
            tip.Text = Lang.T("小提示：如果拖图片拖不进去，检查是不是用「以管理员身份运行」启动的（Windows 会拦掉跨权限的拖拽）。", "Tip: if you cannot drag images in, check whether SnapWheel was started as administrator (Windows blocks cross-privilege dragging).");
            tip.ForeColor = Color.FromArgb(168, 122, 36);
            tip.Location = new Point(x + Ui.S(3), y);
            W(tip, tipW);
            Add2(tip);
            y += TxtH(tip, tipW);

            return y;
        }

        void Add2(Control c)
        {
            if (_scrollHost == null)
            {
                _scrollHost = new Panel();
                _scrollHost.Location = new Point(0, 0);
                _scrollHost.Size = new Size(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
                _scrollHost.BackColor = Color.FromArgb(252, 252, 254);   // 和窗体同色（不用 Transparent，避免重绘问题）
                Controls.Add(_scrollHost);
            }
            _scrollHost.Controls.Add(c);
            _sc.Add(c);
        }

        // 固定贴底的东西（按钮、滚动提示）：加进窗口但**不**参与滚动
        void Add2Raw(Control c) { Controls.Add(c); }

        // 会折行的标签
        static void W(Label l, int maxW) { Ui.Wrap(l, maxW); }

        // 一条说明：粗体小标题 + 一段正文，两行都自己折行、自己报高度
        //
        // about `since`：**每条说明必须写清它是哪个版本加的**，【新】标记由它算出来。
        // 以前这里是一个写死的 markNew 布尔量贴在某几条上，于是每次升级那几条都重新标一遍【新】——
        // 升到 0.9.4 还在说「传递模式」是新的（那是 0.9.0 的东西）。**标错的【新】比不标更糟**：
        // 用户会以为功能是刚加的，然后去找一个早就存在、早就该会的东西。
        //
        // 关于 _brief 这个上限：**只有"全新安装的欢迎引导"才只显示前 3 条**（三步上手），
        // 其余情况（设置里调出的说明、升级后的"这次多了什么"）都完整显示。
        // 注意别拿别的标志来判：之前就是拿 markNew 当条件，结果设置里的说明反而被截成了 3 条。
        void AddTip(int x, ref int y, string since, string title, string body)
        {
            _tipCount++;
            if (_brief && _tipCount > 3) return;         // 首次安装：只给头三步

            // 只有这条说明的加入版本比用户上次看过的那版新，才标【新】
            string nw = (since != null && AppInfo.IsNewer(since, _seenVer)) ? Lang.T("【新】", "[NEW]") : "";

            int mkW = _contentW - Ui.S(3);
            Label t = new Label();
            t.Text = "• " + nw + title;
            t.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            t.ForeColor = Color.FromArgb(0, 110, 190);
            t.Location = new Point(x + Ui.S(3), y);
            W(t, mkW);
            Add2(t);
            y += TxtH(t, mkW) + Ui.S(4);

            int bw = _contentW - Ui.S(Gutter);
            Label b = new Label();
            b.Text = body;
            b.ForeColor = Color.FromArgb(70, 74, 84);
            b.Location = new Point(x + Ui.S(Gutter), y);
            W(b, bw);
            Add2(b);
            y += TxtH(b, bw) + Ui.S(RowGap);
        }

        // 折行后的真实高度：两种量法取大的那个（宁可多留一点空白，也不切字）
        static int TxtH(Label l, int maxW)
        {
            int h = l.PreferredSize.Height;
            int h2 = Ui.TextH(l, l.Text, maxW);
            return Math.Max(Math.Max(h, h2), l.Font.Height);
        }

        // 窗口该待在哪个屏幕上：跟着鼠标走（多屏时不会跑到另一块屏的边角上）
        internal static Rectangle WorkArea()
        {
            try { return Screen.FromPoint(Cursor.Position).WorkingArea; }
            catch
            {
                try { return Screen.PrimaryScreen.WorkingArea; }
                catch { return new Rectangle(0, 0, 1024, 768); }
            }
        }
    }

    // 管理员模式提示窗口（拖拽被 UIPI 拦掉时弹的那个）。
    // ⚠️ DPI：同上 —— 原来那段说明是写死 506×40 的 Label，150% 下会被裁掉一半。
    class AdminForm : Form
    {
        const int WinW = 560;
        const int PadL = 28, PadR = 28, PadT = 24;
        const int Gutter = 16;
        const int RowGap = 14;
        const int BtnH = 38;

        int _contentW;
        readonly UiScroll _sc = new UiScroll();

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

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _sc.Wheel(e.Delta);
            base.OnMouseWheel(e);
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
            ClientSize = new Size(Ui.S(WinW), Ui.S(200));     // 先占位，最后按内容重算
            SuspendLayout();

            Rectangle wa = GuideForm.WorkArea();
            int winW = Ui.S(WinW);
            int maxW = (int)(wa.Width * 0.92) - Ui.S(16);
            if (winW > maxW) winW = Math.Max(Ui.S(320), maxW);
            int mL = Ui.S(PadL), mR = Ui.S(PadR);
            _contentW = winW - mL - mR;

            Label head = new Label();
            head.Text = Lang.T("管理员模式下，拖拽会被 Windows 拦住", "In administrator mode Windows blocks dragging");
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.Location = new Point(mL, Ui.S(PadT));
            Add2(head, _contentW);

            int y = head.Top + head.PreferredSize.Height + Ui.S(8);

            Label sub = new Label();
            sub.Text = Lang.T("不是 SnapWheel 的毛病，是系统的安全限制（UIPI）：管理员进程和普通程序（资源管理器、微信、浏览器）之间不允许互相拖拽。", "This is not SnapWheel's fault - it is a Windows security rule (UIPI): dragging between an elevated process and normal programs (Explorer, WeChat, browsers) is blocked.");
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.Location = new Point(mL + Ui.S(3), y);
            Add2(sub, _contentW - Ui.S(3));

            y += TxtH(sub, _contentW - Ui.S(3)) + Ui.S(16);

            AddTip(mL, ref y, Lang.T("想拖拽 → 换普通权限", "Want dragging? Switch to normal permissions"), Lang.T("点下面那个按钮：SnapWheel 会先退出，再由资源管理器用普通权限重新启动。设置、轮盘、存的图片都不受影响。", "Click the button below: SnapWheel exits, then Explorer restarts it with normal permissions. Settings, wheels and saved images are untouched."));
            AddTip(mL, ref y, Lang.T("不换权限也能用", "Works without changing permissions"), Lang.T("托盘右键「导入图片…」能直接选文件收进轮盘；在任何地方「复制」一张图，它也会自动滑进来 —— 这两个都不受权限影响。", "Tray -> Import images... puts files straight into the ring; and copying an image anywhere slides it in automatically. Neither is affected by permissions."));
            AddTip(mL, ref y, Lang.T("什么时候才需要管理员", "When do you actually need administrator?"), Lang.T("只有要截「管理员窗口」（任务管理器、某些安装程序）时才需要；平时用普通权限最省事，拖拽也正常。", "Only needed to capture elevated windows (Task Manager, some installers). Normal permissions are simpler day to day and dragging works."));

            int contentH = y;

        // ---- 底部按钮行（不参与滚动，永远贴窗口底边）----
            int btnRowH = Ui.S(10) + Ui.S(BtnH) + Ui.S(22);
            int right = winW - mR;
            int btnY = contentH + Ui.S(10);

            RoundButton go = new RoundButton();
            go.Text = Lang.T("以普通权限重启", "Restart with normal permissions");
            go.Size = Ui.Sz(150, BtnH);
            go.Fill = Color.FromArgb(0, 122, 204);
            go.FillHover = Color.FromArgb(0, 140, 232);
            go.TextColor = Color.White;
            go.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            go.Location = new Point(right - go.Width, btnY);
            go.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Add2Raw(go);
            AcceptButton = go;

            RoundButton no = new RoundButton();
            no.Text = Lang.T("知道了", "Got it");
            no.Size = Ui.Sz(104, BtnH);
            no.Fill = Color.FromArgb(238, 240, 245);
            no.FillHover = Color.FromArgb(226, 230, 238);
            no.TextColor = Color.FromArgb(60, 64, 74);
            no.Font = new Font("Microsoft YaHei UI", 10f);
            no.Location = new Point(go.Left - Ui.S(12) - no.Width, btnY);
            no.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });
            Add2Raw(no);
            CancelButton = no;

            int idealH = contentH + btnRowH;
            int maxH = wa.Height - Ui.S(24);
            if (maxH < Ui.S(260)) maxH = Ui.S(260);
            int winH = Math.Min(idealH, maxH);
            ClientSize = new Size(winW, winH);

            _sc.Finish(contentH, winH - btnRowH);

            go.Top = winH - Ui.S(22) - go.Height;
            no.Top = go.Top;
            ResumeLayout();
        }

        // 折行标签：加进窗口 + 登记滚动
        void Add2(Label l, int maxW)
        {
            Ui.Wrap(l, maxW);
            Controls.Add(l);
            _sc.Add(l);
        }

        // 固定贴底的东西（按钮）：加进窗口但**不**参与滚动
        void Add2Raw(Control c) { Controls.Add(c); }

        void AddTip(int x, ref int y, string title, string body)
        {
            int mkW = _contentW - Ui.S(3);
            Label t = new Label();
            t.Text = "• " + title;
            t.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            t.ForeColor = Color.FromArgb(0, 110, 190);
            t.Location = new Point(x + Ui.S(3), y);
            Add2(t, mkW);
            y += TxtH(t, mkW) + Ui.S(4);

            int bw = _contentW - Ui.S(Gutter);
            Label b = new Label();
            b.Text = body;
            b.ForeColor = Color.FromArgb(70, 74, 84);
            b.Location = new Point(x + Ui.S(Gutter), y);
            Add2(b, bw);
            y += TxtH(b, bw) + Ui.S(RowGap);
        }

        static int TxtH(Label l, int maxW)
        {
            int h = l.PreferredSize.Height;
            int h2 = Ui.TextH(l, l.Text, maxW);
            return Math.Max(Math.Max(h, h2), l.Font.Height);
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
        // 换引擎之后重新认同一张图：调用方把"这张图"封进来（剪贴板那张 Bitmap、或选区的像素）
        public delegate string Redo(out string error);

        readonly TextBox _src;
        readonly TextBox _dst;
        readonly RoundButton _tr;
        readonly Label _trState;
        readonly Label _head;
        readonly ComboBox _cmbEngine;
        readonly Label _engNote;
        readonly Redo _redo;
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

        public OcrForm(string text) : this(text, null) { }

        public OcrForm(string text, Redo redo)
        {
            _redo = redo;
            if (text == null) text = "";
            bool copied = false;
            try { Clipboard.SetText(text); copied = true; } catch { }

            int chars = text.Replace("\r", "").Replace("\n", "").Length;
            Text = AppInfo.Name + Lang.T(" 取字", " OCR");
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;

            // ===================== 尺寸基准（DPI 修订） =====================
            // 原来这一整块是 96dpi 下量出来的死数字（24 / 46 / 74 / 94 / 170 / 274 / 316 / 560…）。
            // 本程序是 per-monitor DPI aware：150% 下字体被 GDI+ 按 DPI 放大渲染，死数字却一个不动，
            // 于是「认出来 N 个字」压住副标题、说明文字整句被切、按钮被挤出窗口右下角 ——
            // 用户报的「凡是涉及界面的都显示不全」就是这个根因。
            // 约定：**长度一律过 Ui.S()（乘 K），字体磅值一个都不乘**（乘了就是双倍放大）。
            int pad = Ui.S(24);        // 左右外边距（原来满篇写死 24）
            int top = Ui.S(16);        // 顶部外边距
            // 下面这几个间距就是设计稿里"标题底 → 46 → 74 → 94"之间的空档（写死的 y 减去标签自身高度）。
            // 现在标签高度是量出来的，所以这几个数是"真的空档"，不会被字变高顶掉 —— 100% 下看起来和原来一样。
            int gHeadSub = 0;          // 标题 → 副标题：原来 46 = 16 + 标题高，中间本来就没留空档
            int gSubL1 = Ui.S(7);      // 副标题 → 「原文」小标签（原来 = 74 - 46 - 副标题高）
            int gL1Box = 0;            // 小标签 → 原文框：原来 94 = 74 + 标签高，也是紧贴着放
            int gBoxRow = Ui.S(10);    // 原文框 → 按钮行（原来 = 274 - 94 - 170）
            int gRowBox = Ui.S(8);     // 按钮行 → 译文框（原来 = 316 - 274 - 34）
            int gBoxBot = Ui.S(12);    // 译文框 → 底部那排按钮
            int botPad = Ui.S(14);     // 底部外边距（原来 560 - 48 - 34 = 14）
            int boxH = Ui.S(170);      // 两个多行框的设计高度（一屏放不下时会在下面被压矮）
            SuspendLayout();

            Label head = new Label();
            head.Text = chars > 0 ? (Lang.T("认出来 ", "Recognised ") + chars + Lang.T(" 个字", " characters")) : Lang.T("没认出文字", "No text found");
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            Controls.Add(head);
            _head = head;

            // ---- 窗口宽度：设计稿 620 × K，但不能被标题或屏幕挤到放不下 ----
            int clientW = Ui.S(620);
            int headNeed = head.PreferredSize.Width + pad * 2;   // 标题单行要占的宽度（含左右边距）
            if (headNeed > clientW) clientW = headNeed;
            int scrW = 0, scrH = 0;
            try
            {
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                scrW = wa.Width; scrH = wa.Height;
            }
            catch { }
            // 300% 时 620 × 3 = 1860，比屏幕还宽就没意义了（屏幕为准，文字反正能折行）
            if (scrW > 0 && clientW > scrW - Ui.S(24)) clientW = scrW - Ui.S(24);
            int contentW = clientW - pad * 2;                    // 会折行的标签最多占这么宽
            Ui.Wrap(head, contentW);                             // 标题兜底：真放不下宁可折行，也不许被窗口切掉

            Label sub = new Label();
            sub.Text = chars > 0
                ? (copied ? Lang.T("原文已复制到剪贴板；要用译文点下面的「翻译」", "Original copied to the clipboard; click Translate below for the translation")
                          : Lang.T("下面就是识别结果，可以改完再复制", "The recognised text is below - edit it if needed, then copy"))
                : Lang.T("换一块更清晰、字更大的区域再试试；倾斜或花哨的字体识别率会低一些", "Try a clearer area with larger text; slanted or decorative fonts are recognised less reliably");
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            // 这句最长（没认出字那版有 33 个字）：原来 AutoSize 不封顶，150% 下它比窗口还宽，右半边整句被切掉。
            // Wrap = AutoSize + MaximumSize(内容宽, 0)：放得下就是一行，放不下自己折行、自己报出真实高度。
            Ui.Wrap(sub, contentW);
            Controls.Add(sub);

            // ---- 取字引擎：给用户自己选，并把两个的短处写清楚（不然他不知道"哪个更好"）----
            // 两个引擎的毛病不一样：系统那个快但短标题/小字容易整行漏（实测「验证」整行消失），
            // 本地组件漏字少但整屏要 3~4 秒。这不是"我们没调好"，是两套引擎各自的取舍，
            // 所以把这个选择交给用户，而不是替他定死。
            Label lEng = new Label();
            lEng.Text = Lang.T("取字引擎", "OCR engine");
            lEng.ForeColor = Color.FromArgb(120, 124, 134);
            Ui.OneLine(lEng);
            Controls.Add(lEng);

            string[] engItems = new string[]
            {
                Lang.T("自动（推荐）", "Auto (recommended)"),
                Lang.T("系统自带（快）", "System (fast)"),
                Lang.T("随包本地组件（慢、漏字少）", "Bundled component (slower, fewer misses)")
            };
            _cmbEngine = new ComboBox();
            _cmbEngine.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbEngine.Font = new Font("Microsoft YaHei UI", 9.5f);
            _cmbEngine.Items.AddRange(engItems);
            int cbW = Ui.S(170);
            try
            {
                foreach (string it in engItems)
                {
                    Size t = TextRenderer.MeasureText(it, _cmbEngine.Font);
                    if (t.Width + Ui.S(40) > cbW) cbW = t.Width + Ui.S(40);   // 40 = 下拉箭头 + 内边距
                }
            }
            catch { }
            int cbMax = contentW - lEng.PreferredSize.Width - Ui.S(10);
            if (cbMax > Ui.S(100) && cbW > cbMax) cbW = cbMax;               // 再长也不许把标签挤出去
            _cmbEngine.Size = new Size(cbW, Ui.S(24));
            Controls.Add(_cmbEngine);
            // 先把初值定好再接事件 —— 否则构造时那一次 SelectedIndex 赋值会当场触发"重新识别"
            _cmbEngine.SelectedIndex = EngineIndex(Ocr.Engine);
            _cmbEngine.SelectedIndexChanged += new EventHandler(OnEngineChanged);

            _engNote = new Label();
            _engNote.Text = EngineNote();
            _engNote.ForeColor = Color.FromArgb(120, 124, 134);
            Ui.Wrap(_engNote, contentW);
            Controls.Add(_engNote);

            Label l1 = new Label();
            l1.Text = Lang.T("原文", "Original");
            l1.ForeColor = Color.FromArgb(120, 124, 134);
            Ui.OneLine(l1);                                      // 短标签：AutoSize 就够，行高别再写死
            Controls.Add(l1);

            // ---- 先把每块占多高量清楚，再决定各块的 y 和窗口得多高 ----
            // 高度一律问标签自己要（AutoSize 标签报的 Height / PreferredHeight **就是**它随后要占的高度），
            // 不再猜"9.5pt 一行大概 17px" —— 那种猜法正是 150% 下裁字的根源。
            int headH = head.PreferredHeight;
            int subH = sub.PreferredHeight;
            int l1H = l1.PreferredHeight;
            int engRowH = Math.Max(lEng.PreferredHeight, _cmbEngine.Height);   // 标签和下拉框取高的那个
            int engNoteH = _engNote.PreferredHeight;
            int gEng = Ui.S(6);                                                // 下拉框那行 → 说明文字

            // 圆角按钮上的字是 TextRenderer 直接画在按钮矩形里的（见 40-RoundButton.OnPaint）：
            // 宽或高不够就**硬裁**，按钮不会自己缩字号、也不会自己变宽。所以宽高取「设计值 × K」和
            // 「文字实测 + 内边距」里的大者。字体先在这里建出来，是为了在摆控件之前就能算准按钮高度。
            Font fBtn = new Font("Microsoft YaHei UI", 10f);
            Font fBtnB = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            string trText = chars > 0 ? (Lang.T("翻译成", "Translate to") + Translate.TargetLabel(text)) : Lang.T("翻译", "Translate");
            int trW = BtnW(fBtnB, trText, 132);
            int trH = BtnH(fBtnB, trText, 34);
            string copySrcText = Lang.T("复制原文", "Copy original");
            int copySrcW = BtnW(fBtn, copySrcText, 102);
            int copySrcH = BtnH(fBtn, copySrcText, 34);
            int rowH = Math.Max(trH, copySrcH);                  // 按钮行的行高：按这一行里最高的按钮算
            string copyDstText = Lang.T("复制译文", "Copy translation");
            int copyDstW = BtnW(fBtn, copyDstText, 102);
            int copyDstH = BtnH(fBtn, copyDstText, 34);
            string closeText = Lang.T("关闭", "Close");
            int closeW = BtnW(fBtnB, closeText, 96);
            int closeH = BtnH(fBtnB, closeText, 36);
            int bottomH = Math.Max(copyDstH, closeH);            // 底部那排的行高

            // 除两个多行框之外的固定开销（用来判断"一屏放不放得下"）
            int fixedH = top + headH + gHeadSub + subH + gSubL1 + engRowH + gEng + engNoteH + gSubL1
                       + l1H + gL1Box
                       + gBoxRow + rowH + gRowBox + gBoxBot + bottomH + botPad;
            if (scrH > 0)
            {
                int avail = scrH - Ui.S(72);                     // 给标题栏和任务栏留点余量
                if (fixedH + boxH * 2 > avail)
                {
                    // 一屏放不下时**优先压两个多行框**：它们带滚动条，压矮了内容还能滚；
                    // 标题 / 说明 / 按钮压下去就是真的裁字，所以那些一个都不动。
                    int half = (avail - fixedH) / 2;
                    if (half < boxH) boxH = half;
                    if (boxH < Ui.S(80)) boxH = Ui.S(80);        // 再挤也留这么高，不然两个框没法用
                }
            }

            // ---- 各块的 y：一律"上一块的底边 + 间距"，不再写死 46 / 74 / 94 / 274 / 316 ----
            int y = top;
            head.Location = new Point(pad, y);
            y += headH + gHeadSub;
            sub.Location = new Point(Ui.S(27), y);               // x 的 27 是设计稿里相对标题(24)的错位，保持原样
            y += subH + gSubL1;
            lEng.Location = new Point(pad, y);
            _cmbEngine.Location = new Point(lEng.Right + Ui.S(10), y + Math.Max(0, (engRowH - _cmbEngine.Height) / 2));
            y += engRowH + gEng;
            _engNote.Location = new Point(pad, y);
            y += engNoteH + gSubL1;
            l1.Location = new Point(pad, y);
            y += l1H + gL1Box;
            int srcY = y;
            y += boxH + gBoxRow;
            int rowY = y;
            y += rowH + gRowBox;
            int dstY = y;
            y += boxH + gBoxBot;                                 // y 到这里 = 底部那排按钮的顶边

            _src = new TextBox();
            _src.Multiline = true;
            _src.ScrollBars = ScrollBars.Both;
            _src.WordWrap = true;
            _src.Font = new Font("Microsoft YaHei UI", 11f);
            _src.BorderStyle = BorderStyle.FixedSingle;
            _src.BackColor = Color.White;
            _src.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _src.Location = new Point(pad, srcY);
            // 宽度：原来写死 ClientSize.Width - 48，可那个 48 不跟着 DPI 放大，150% 下右边会被吃掉一块
            _src.Size = new Size(contentW, boxH);
            _src.Text = text;
            Controls.Add(_src);

            // 翻译
            _tr = new RoundButton();
            _tr.Text = trText;
            _tr.Size = new Size(trW, trH);
            _tr.Fill = Color.FromArgb(0, 122, 204);
            _tr.FillHover = Color.FromArgb(0, 140, 232);
            _tr.TextColor = Color.White;
            _tr.Font = fBtnB;
            _tr.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _tr.Location = new Point(pad, rowY);
            _tr.Click += new EventHandler(delegate(object o, EventArgs e2) { DoTranslate(); });
            Controls.Add(_tr);

            RoundButton copySrc = new RoundButton();
            copySrc.Text = copySrcText;
            copySrc.Size = new Size(copySrcW, copySrcH);
            copySrc.Fill = Color.FromArgb(238, 240, 245);
            copySrc.FillHover = Color.FromArgb(226, 230, 238);
            copySrc.TextColor = Color.FromArgb(60, 64, 74);
            copySrc.Font = fBtn;
            copySrc.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            copySrc.Location = new Point(_tr.Right + Ui.S(8), rowY);   // 原来写死 164 = 24 + 132 + 8
            copySrc.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                try { Clipboard.SetText(_src.Text); _trState.Text = Lang.T("原文已复制", "Original copied"); } catch { }
            });
            Controls.Add(copySrc);

            _trState = new Label();
            _trState.Text = Lang.T("译文", "Translation");
            _trState.ForeColor = Color.FromArgb(120, 124, 134);
            _trState.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(_trState);
            int stateX = copySrc.Right + Ui.S(14);                      // 原来写死 280 = 164 + 102 + 14
            int stateW = Math.Max(Ui.S(80), clientW - pad - stateX);     // 右边不许越过窗口边距
            Ui.Wrap(_trState, stateW);
            // 这行字运行中会变长（Lang.T("翻译中…（用 MyMemory 免费接口，要联网）", "Translating… (free MyMemory endpoint, needs internet)")、失败原因），所以
            // ①宽度封顶让它能折行；②这一行的**行高按已知最长的那句预留**（Ui.TextH 量），
            // 免得它突然折成两行、压在下面的译文框上。文字本身不动，只是把位置算出来。
            int stateH = Ui.TextH(_trState, Lang.T("翻译中…（用 MyMemory 免费接口，要联网）", "Translating… (free MyMemory endpoint, needs internet)"), stateW);
            int stateY = rowY + Math.Max(0, (rowH - stateH) / 2);        // 原来 284：按钮行(274 高 34)里垂直居中
            _trState.Location = new Point(stateX, stateY);

            _dst = new TextBox();
            _dst.Multiline = true;
            _dst.ScrollBars = ScrollBars.Both;
            _dst.WordWrap = true;
            _dst.ReadOnly = true;
            _dst.Font = new Font("Microsoft YaHei UI", 11f);
            _dst.BorderStyle = BorderStyle.FixedSingle;
            _dst.BackColor = Color.FromArgb(248, 249, 252);
            _dst.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            // 译文框的 y：状态行万一真折成两行也要让开它（取更靠下的那个），原来写死 316
            if (stateY + stateH + Ui.S(4) > dstY) dstY = stateY + stateH + Ui.S(4);
            _dst.Location = new Point(pad, dstY);
            _dst.Size = new Size(contentW, boxH);
            Controls.Add(_dst);
            // 内容真正需要的高度：窗口至少得这么高（最后一步按它定 ClientSize）
            int needH = dstY + boxH + gBoxBot + bottomH + botPad;

            RoundButton copyDst = new RoundButton();
            copyDst.Text = copyDstText;
            copyDst.Size = new Size(copyDstW, copyDstH);
            copyDst.Fill = Color.FromArgb(238, 240, 245);
            copyDst.FillHover = Color.FromArgb(226, 230, 238);
            copyDst.TextColor = Color.FromArgb(60, 64, 74);
            copyDst.Font = fBtn;
            copyDst.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            // 位置在最后统一排：这排按钮贴的是窗口**底边**，而 ClientSize 到最后一刻才定
            copyDst.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                if (_translated.Length == 0) { _trState.Text = Lang.T("还没翻译呢", "Nothing translated yet"); return; }
                try { Clipboard.SetText(_translated); _trState.Text = Lang.T("译文已复制", "Translation copied"); } catch { }
            });
            Controls.Add(copyDst);

            RoundButton close = new RoundButton();
            close.Text = closeText;
            close.Size = new Size(closeW, closeH);
            close.Fill = Color.FromArgb(0, 122, 204);
            close.FillHover = Color.FromArgb(0, 140, 232);
            close.TextColor = Color.White;
            close.Font = fBtnB;
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;

            // ===================== 最后一步：按内容把窗口尺寸定下来 =====================
            // 上面的 y 全是"上一块的底边 + 间距"推出来的，两个多行框又是 170 × K，
            // 所以 150% 下内容自然比原来的 560 高。窗口不跟着长，底部那排按钮就被切在窗外 ——
            // 折行 / AutoSize 省下来的高度必须在**这里**兑现成窗口尺寸，否则等于白改。
            int clientH = Math.Max(needH, Ui.S(560));   // 不小于设计稿观感
            if (scrH > 0 && clientH > scrH - Ui.S(24) && needH <= scrH - Ui.S(24))
            {
                // 只是"设计稿最小观感"顶出了屏幕，可以缩回来；内容本身放得下（放不下就宁可窗口高一点，也不裁内容）
                clientH = scrH - Ui.S(24);
            }
            MinimumSize = new Size(Math.Min(Ui.S(420), clientW), Math.Min(Ui.S(380), clientH));
            ClientSize = new Size(clientW, clientH);
            ResumeLayout();

            // ClientSize 变了，贴边的那几个控件再放一遍：它们的 Anchor 基准是"入伙时"的旧尺寸，
            // 显式给一遍才能保证"离右边 24 / 离底边 14"就是设计稿的边距（Anchor 之后照样对用户拖窗口生效）。
            int wide = clientW - pad * 2;
            _src.Width = wide;
            _dst.Width = wide;
            int bottomLine = clientH - botPad;                              // 底部那排共用的底边（原来 = 560 - 48 + 36 的底）
            copyDst.Location = new Point(pad, bottomLine - copyDst.Height);
            close.Location = new Point(clientW - pad - close.Width, bottomLine - close.Height);
        }

        // ---- 引擎那行的映射：顺序和中英文文案、和 35-Settings.cs 的取值三处一一对应，改一处就得改三处 ----
        static int EngineIndex(string v)
        {
            if (string.Equals(v, "system", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(v, "native", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }

        static string EngineValue(int i)
        {
            if (i == 1) return "system";
            if (i == 2) return "native";
            return "auto";
        }

        static string EngineNote()
        {
            return Lang.T(
                "「系统」快（整屏不到 1 秒），但短标题、小字容易整行漏掉；「本地组件」慢（整屏 3~4 秒）漏字少，需要程序旁边有 ocr 目录（32 位 Win7 上只有它）。「自动」= 有系统取字就用系统，Win7 自动改用本地组件。",
                "System is fast (under a second for a full screen) but often drops short headings and small text; the bundled component is slower (3-4 s) with fewer misses, and needs an 'ocr' folder next to the program (on 32-bit Windows 7 it is the only choice). Auto = the system OCR when present, the bundled one otherwise.");
        }

        // 换了引擎：存下来 → 让 Ocr 重探一次 → 拿同一张图重新认一遍。
        // 认不出来就**保留原来的文字**：用户手上的结果不能被一次实验性切换弄丢。
        void OnEngineChanged(object sender, EventArgs e)
        {
            string val = EngineValue(_cmbEngine.SelectedIndex);
            Settings.SaveOcrEngine(val);
            Ocr.Engine = val;
            Ocr.Reconfigure();
            Usage.Ev("OcrEngine", val);
            if (_redo == null) return;          // 极少数拿不到原图的调用 → 存下来，下次取字生效

            _cmbEngine.Enabled = false;
            _engNote.Text = Lang.T("正在用新的引擎重新识别这张图…", "Re-recognising this image with the new engine…");
            Redo redo = _redo;
            Thread th = new Thread(new ThreadStart(delegate()
            {
                string err = null, txt = null;
                try { txt = redo(out err); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        _cmbEngine.Enabled = true;
                        _engNote.Text = EngineNote();
                        if (txt == null)
                        {
                            _engNote.Text = (err ?? Lang.T("重新识别失败", "Re-recognition failed")) + Lang.T("（原文没动）", " (the text above was kept)");
                            return;
                        }
                        _src.Text = txt;
                        int n = txt.Replace("\r", "").Replace("\n", "").Length;
                        _head.Text = n > 0 ? (Lang.T("认出来 ", "Recognised ") + n + Lang.T(" 个字", " characters")) : Lang.T("没认出文字", "No text found");
                        try { Clipboard.SetText(txt); } catch { }
                    }));
                }
                catch { }
            }));
            th.IsBackground = true;
            th.Start();
        }

        // 圆角按钮上的字是 TextRenderer 直接画在按钮矩形里（见 40-RoundButton.OnPaint）：
        // 宽或高不够就硬裁，按钮既不会缩字号也不会自己变宽。所以尺寸取「设计稿像素 × K」和
        // 「文字实测 + 内边距」里的大者 —— 换目标语言、换字体回退、更高的 DPI，都不会让按钮上的字只剩一半。
        static int BtnW(Font f, string text, int designW)
        {
            int w = Ui.S(designW);
            try
            {
                Size t = TextRenderer.MeasureText(text, f);
                int need = t.Width + Ui.S(24);
                if (need > w) w = need;
            }
            catch { }
            return w;
        }

        static int BtnH(Font f, string text, int designH)
        {
            int h = Ui.S(designH);
            try
            {
                Size t = TextRenderer.MeasureText(text, f);
                int need = t.Height + Ui.S(12);
                if (need > h) h = need;
            }
            catch { }
            return h;
        }

        // 翻译丢到后台线程去做：网络慢的时候窗口不能卡死（这就是"别做成鸡肋"的意思）
        void DoTranslate()
        {
            if (_busy) return;
            string text = _src.Text;
            if (text.Trim().Length == 0) { _trState.Text = Lang.T("没有要翻译的文字", "Nothing to translate"); return; }
            Usage.Ev("Translate", "字数=" + text.Trim().Length);
            _busy = true;
            _tr.Enabled = false;
            _trState.Text = Lang.T("翻译中…（用 MyMemory 免费接口，要联网）", "Translating… (free MyMemory endpoint, needs internet)");
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
                            _trState.Text = err ?? Lang.T("翻译失败", "Translation failed");
                            _dst.Text = Lang.T("（翻译失败：", "(translation failed: ") + (_trState.Text) + "）";
                        }
                        else
                        {
                            _translated = result;
                            _dst.Text = result;
                            _trState.Text = Lang.T("译文（已可复制）", "Translation (ready to copy)");
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
    // ============================ 打赏（收款码） ============================
    // 设置窗口底部「还原默认」右边那个按钮弹出来的东西。刻意不写进引导、不写进说明
    // （用户原话："悄咪咪的不做说明"）。
    //
    // ⚠️ 体积（这里踩过坑，别再犯）：内嵌的图**必须**小。
    //    v0.5.3 第一版把 docs\reward.png（828×1124、250,799 字节）整张按 **base64** 内嵌，
    //    结果 exe 从 222,208 字节涨到 559,616 —— 多出来的 333,312 字节几乎全是它。两个原因：
    //      ① base64 本身 4/3 膨胀；
    //      ② **base64 串在 exe 里每个字符要占约 4 字节**（const 字符串进 #US 堆是 UTF-16：
    //         实测 81,952 个字符 → +333,312 字节）。用字符串内嵌二进制就是这么贵。
    //    现在三层一起压：
    //      · 只嵌**二维码那一块**（394×362；二维码本体 334px，四边各留 ~28px≈4 个模块的静区），
    //        昵称/ID 那一行整个在裁切区之外 —— 这张图里根本没有它（仓库里那份打码全图仍是 docs\reward.png）；
    //      · 二维码**二值化成纯黑白**（头像+微信支付小标那一小块保持原样，它在码中间不能动）：
    //        扫描器内部也是按阈值判的，实测"按阈值 128 判定"与原图 **0 处不同** —— 图案一个模块都没变，
    //        只是把 JPEG 噪点清掉了，反而更干净；
    //      · 用 **byte[] 字面量**而不是字符串：数组数据在 PE 里就是原始字节（1 字节占 1 字节），
    //        不走 #US 堆的 UTF-16。整张 17,260 字节，exe 只涨约 17KB。
    //    想换图：替换 docs\reward.png（记得打码），按同样办法裁 → 二值化 → 8 位调色板量化，
    //    再把 PNG 的字节原样填进下面的数组。
    //
    // ⚠️ 不联网、不接任何支付 API、不打开任何链接：只是把这张图显示出来。
    static class Reward
    {
        static Image _img;
        static bool _tried;

        // 懒解码一次，之后复用（设置窗口反复打开也只解一次）
        public static Image Get()
        {
            if (_tried) return _img;
            _tried = true;
            try
            {
                using (MemoryStream ms = new MemoryStream(Png, false))
                using (Image im = Image.FromStream(ms))
                    _img = new Bitmap(im);          // 拷出来：流一关原图就不能用了
            }
            catch (Exception ex) { Err.Log("Reward.Get", ex); _img = null; }
            return _img;
        }

        // docs\reward.png 里"二维码 + 静区"那一块的 8 位调色板 PNG（17,260 字节，二值化后量化）
        static readonly byte[] Png = new byte[] {             137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,1,138,0,0,1,106,8,3,0,0,0,51,228,237,
            160,0,0,0,1,115,82,71,66,0,174,206,28,233,0,0,0,4,103,65,77,65,0,0,177,143,11,252,97,5,0,0,
            3,0,80,76,84,69,0,0,0,0,0,51,0,0,102,0,0,153,0,0,204,0,0,255,0,43,0,0,43,51,0,43,
            102,0,43,153,0,43,204,0,43,255,0,85,0,0,85,51,0,85,102,0,85,153,0,85,204,0,85,255,0,128,0,0,
            128,51,0,128,102,0,128,153,0,128,204,0,128,255,0,170,0,0,170,51,0,170,102,0,170,153,0,170,204,0,170,255,
            0,213,0,0,213,51,0,213,102,0,213,153,0,213,204,0,213,255,0,255,0,0,255,51,0,255,102,0,255,153,0,255,
            204,0,255,255,51,0,0,51,0,51,51,0,102,51,0,153,51,0,204,51,0,255,51,43,0,51,43,51,51,43,102,51,
            43,153,51,43,204,51,43,255,51,85,0,51,85,51,51,85,102,51,85,153,51,85,204,51,85,255,51,128,0,51,128,51,
            51,128,102,51,128,153,51,128,204,51,128,255,51,170,0,51,170,51,51,170,102,51,170,153,51,170,204,51,170,255,51,213,
            0,51,213,51,51,213,102,51,213,153,51,213,204,51,213,255,51,255,0,51,255,51,51,255,102,51,255,153,51,255,204,51,
            255,255,102,0,0,102,0,51,102,0,102,102,0,153,102,0,204,102,0,255,102,43,0,102,43,51,102,43,102,102,43,153,
            102,43,204,102,43,255,102,85,0,102,85,51,102,85,102,102,85,153,102,85,204,102,85,255,102,128,0,102,128,51,102,128,
            102,102,128,153,102,128,204,102,128,255,102,170,0,102,170,51,102,170,102,102,170,153,102,170,204,102,170,255,102,213,0,102,
            213,51,102,213,102,102,213,153,102,213,204,102,213,255,102,255,0,102,255,51,102,255,102,102,255,153,102,255,204,102,255,255,
            153,0,0,153,0,51,153,0,102,153,0,153,153,0,204,153,0,255,153,43,0,153,43,51,153,43,102,153,43,153,153,43,
            204,153,43,255,153,85,0,153,85,51,153,85,102,153,85,153,153,85,204,153,85,255,153,128,0,153,128,51,153,128,102,153,
            128,153,153,128,204,153,128,255,153,170,0,153,170,51,153,170,102,153,170,153,153,170,204,153,170,255,153,213,0,153,213,51,
            153,213,102,153,213,153,153,213,204,153,213,255,153,255,0,153,255,51,153,255,102,153,255,153,153,255,204,153,255,255,204,0,
            0,204,0,51,204,0,102,204,0,153,204,0,204,204,0,255,204,43,0,204,43,51,204,43,102,204,43,153,204,43,204,204,
            43,255,204,85,0,204,85,51,204,85,102,204,85,153,204,85,204,204,85,255,204,128,0,204,128,51,204,128,102,204,128,153,
            204,128,204,204,128,255,204,170,0,204,170,51,204,170,102,204,170,153,204,170,204,204,170,255,204,213,0,204,213,51,204,213,
            102,204,213,153,204,213,204,204,213,255,204,255,0,204,255,51,204,255,102,204,255,153,204,255,204,204,255,255,255,0,0,255,
            0,51,255,0,102,255,0,153,255,0,204,255,0,255,255,43,0,255,43,51,255,43,102,255,43,153,255,43,204,255,43,255,
            255,85,0,255,85,51,255,85,102,255,85,153,255,85,204,255,85,255,255,128,0,255,128,51,255,128,102,255,128,153,255,128,
            204,255,128,255,255,170,0,255,170,51,255,170,102,255,170,153,255,170,204,255,170,255,255,213,0,255,213,51,255,213,102,255,
            213,153,255,213,204,255,213,255,255,255,0,255,255,51,255,255,102,255,255,153,255,255,204,255,255,255,0,0,0,0,0,0,
            0,0,0,0,0,0,217,246,242,40,0,0,0,253,116,82,78,83,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,0,246,79,52,3,0,0,0,9,112,72,89,115,0,0,14,195,0,
            0,14,195,1,199,111,168,100,0,0,62,236,73,68,65,84,120,94,237,157,63,123,28,73,146,222,155,14,113,14,224,236,
            56,11,103,104,3,14,18,134,170,100,44,215,16,206,16,245,13,212,116,170,100,168,225,44,100,236,156,115,250,6,75,25,
            106,56,44,56,67,103,169,47,129,113,186,96,44,139,14,250,75,172,5,158,49,108,26,215,112,244,68,86,101,116,197,27,
            25,153,213,152,163,30,234,30,254,192,225,48,34,222,120,51,187,11,245,183,171,187,103,143,54,179,39,243,100,39,213,32,
            125,118,152,117,44,176,19,34,219,70,228,132,88,159,14,58,73,82,101,116,154,206,147,157,84,131,244,217,97,214,177,192,
            78,136,108,219,129,58,37,196,250,116,208,73,146,42,163,211,116,158,236,132,13,210,102,132,41,192,66,136,21,178,109,7,
            234,148,16,235,211,65,39,73,170,140,78,211,121,178,19,54,72,155,17,166,0,11,33,86,200,182,17,57,33,214,167,131,
            78,146,84,25,157,166,243,100,39,108,144,54,35,76,1,22,66,172,144,109,59,80,167,132,88,159,14,58,73,82,101,116,
            154,206,147,157,176,65,218,140,48,5,88,8,177,66,182,237,64,157,18,98,125,58,232,36,73,149,209,105,58,79,118,194,
            6,105,51,194,20,96,33,196,10,217,182,3,117,74,136,245,233,160,147,36,85,158,230,48,66,141,137,49,230,85,65,129,
            66,238,132,188,18,34,186,195,128,133,86,131,89,176,152,216,144,42,79,115,24,161,198,196,24,243,170,160,64,33,119,66,
            94,9,17,221,97,192,66,171,193,44,152,76,107,72,149,167,57,140,80,147,196,24,243,170,128,40,33,39,172,60,183,2,
            89,65,128,133,86,131,89,48,153,214,144,42,79,115,24,161,38,137,49,230,85,1,81,66,78,64,222,28,42,160,59,12,
            88,104,53,152,5,139,137,13,169,242,52,135,17,106,76,140,49,175,10,136,18,114,2,242,230,80,1,221,97,192,66,171,
            193,44,152,76,107,72,149,167,57,140,80,147,196,24,243,170,128,40,33,39,32,111,14,21,208,29,6,44,180,26,204,130,
            201,180,134,84,121,154,195,8,53,73,140,49,175,10,10,20,114,39,228,149,16,209,29,6,44,180,26,204,130,201,180,134,
            84,121,154,195,8,53,73,140,49,175,10,10,20,114,167,149,231,78,32,43,8,176,208,106,48,11,38,211,26,82,101,229,
            48,211,243,244,96,131,52,157,249,194,35,253,137,231,163,115,24,74,49,71,10,70,165,224,77,126,99,89,80,247,197,157,
            24,25,44,68,56,86,14,129,22,132,132,241,172,232,58,39,162,164,202,202,129,7,1,84,157,59,160,128,121,179,192,78,
            83,5,88,103,80,168,148,152,87,66,43,214,9,32,212,149,67,156,84,89,57,240,32,128,170,115,7,20,48,111,22,216,
            233,201,130,0,234,148,16,243,74,104,197,58,1,132,186,114,136,147,42,43,7,30,4,80,117,238,128,2,230,205,2,59,
            61,89,16,64,157,18,98,94,9,173,88,39,128,80,87,14,113,82,101,229,192,131,0,170,206,29,80,192,188,89,96,167,
            39,11,2,168,83,66,204,43,161,21,235,4,16,234,202,33,78,170,140,14,60,6,162,4,108,1,5,204,155,5,118,154,
            42,192,58,131,66,165,196,188,18,90,177,78,0,161,174,28,226,164,202,232,192,99,32,74,192,22,80,192,188,89,96,167,
            169,2,172,51,40,84,74,204,43,161,21,235,4,16,234,202,33,78,170,140,14,60,6,162,4,108,1,5,204,155,5,118,
            122,178,32,128,58,37,196,188,18,90,177,78,0,161,174,28,226,164,202,232,192,99,32,74,192,22,80,192,188,89,96,167,
            39,11,2,168,83,66,149,199,132,21,235,4,16,234,202,33,78,170,140,14,60,6,162,4,152,8,49,230,167,179,183,3,
            10,101,251,136,172,0,49,135,64,84,7,39,162,164,202,232,192,99,32,74,128,137,16,99,126,58,123,59,160,80,182,143,
            200,10,16,115,8,68,117,112,34,74,170,140,14,60,6,162,4,152,8,49,230,167,179,183,3,10,101,251,136,172,0,49,
            135,64,84,7,39,162,164,202,232,192,99,32,74,128,137,16,99,126,58,123,59,160,80,182,143,200,10,16,115,8,68,117,
            112,34,74,170,140,14,60,6,162,4,152,8,49,230,167,179,183,3,10,101,251,136,172,0,49,135,64,84,7,39,162,164,
            202,232,192,99,32,74,128,137,16,99,126,58,123,59,160,80,182,143,200,10,16,115,8,68,117,112,34,74,170,140,14,60,
            6,162,4,152,8,49,230,167,179,183,3,10,101,251,136,172,0,49,135,64,84,7,39,162,164,202,232,192,99,32,74,128,
            137,16,99,126,58,123,59,160,80,182,143,200,10,16,115,8,68,117,112,34,74,170,140,14,60,6,162,4,42,145,3,27,
            66,204,72,121,132,201,13,89,1,34,125,247,120,148,170,131,19,81,82,101,116,224,49,16,37,80,137,28,216,16,98,70,
            202,35,76,110,200,10,16,233,187,199,163,84,29,156,136,146,42,163,3,143,129,40,129,74,228,192,134,16,51,82,30,97,
            114,67,86,128,72,223,61,30,165,234,224,68,148,84,25,29,120,12,68,9,84,34,7,54,132,152,145,242,8,147,27,178,
            2,68,250,238,241,40,85,7,39,162,164,202,202,129,7,1,84,93,37,114,96,67,136,25,41,215,160,222,110,200,10,16,
            233,187,199,163,84,29,156,136,146,42,163,3,143,129,40,129,74,228,192,134,16,51,82,30,97,114,67,86,128,72,223,61,
            30,165,234,224,68,148,84,89,57,240,32,128,170,171,68,14,108,8,49,35,229,17,38,55,100,5,136,244,221,227,81,170,
            14,78,68,73,149,149,3,15,2,168,186,74,228,192,134,16,51,82,174,65,189,221,144,21,32,210,119,143,71,169,58,56,
            17,37,85,158,230,48,66,79,2,96,129,5,54,168,14,204,99,172,19,22,40,12,177,2,5,202,33,203,180,134,84,121,
            154,195,136,236,36,89,96,145,109,192,2,198,58,97,129,194,16,43,80,160,28,178,76,107,72,149,167,57,140,200,78,146,
            5,22,216,160,58,48,175,132,24,155,160,144,157,16,20,40,135,44,211,26,82,229,105,14,35,178,147,100,129,69,182,1,
            11,24,235,132,5,10,67,172,64,129,114,200,50,173,33,85,158,230,48,34,59,73,22,88,96,131,234,192,60,198,58,97,
            129,194,16,43,80,160,28,178,76,107,72,149,167,57,140,200,78,146,5,22,216,160,58,48,175,132,24,155,160,144,157,16,
            20,40,135,44,211,26,82,229,105,14,35,178,147,100,129,5,54,168,14,204,43,33,198,38,40,100,39,4,5,202,33,199,
            196,134,84,121,154,195,136,236,152,44,176,192,6,213,129,121,140,117,194,2,133,33,86,160,64,57,228,152,216,144,42,179,
            197,222,160,131,21,171,4,198,42,97,197,42,97,197,42,129,177,74,88,241,254,240,16,81,82,101,116,154,14,58,88,177,
            74,96,172,18,86,172,18,86,172,18,86,172,18,24,239,79,112,136,147,42,163,211,116,208,193,138,85,2,99,149,176,98,
            149,176,98,149,176,98,149,192,120,127,130,67,156,84,25,157,166,131,14,86,172,18,24,171,132,21,171,132,21,171,132,21,
            171,4,198,251,19,28,226,164,202,232,52,25,229,96,197,42,97,197,42,129,177,74,88,177,74,88,177,74,96,188,63,193,
            33,78,170,140,78,147,81,14,86,172,18,86,172,18,24,171,132,21,171,132,21,171,4,198,251,19,28,226,164,202,232,52,
            29,116,176,98,149,176,98,149,192,88,37,172,88,37,172,88,37,48,222,159,224,16,39,85,70,167,201,40,7,43,86,9,
            43,86,9,140,85,194,138,85,194,138,85,2,227,253,9,14,113,210,101,244,154,136,234,167,113,134,55,178,251,92,95,244,
            117,255,31,142,184,155,214,240,230,120,31,247,13,99,79,251,137,11,33,143,225,155,19,13,42,14,179,148,161,76,77,7,
            30,102,140,92,253,183,161,38,132,121,41,223,161,234,220,97,33,218,35,160,94,205,5,99,5,91,125,37,190,238,0,214,
            163,81,9,68,9,56,97,33,251,53,168,87,206,24,43,216,234,43,241,117,7,176,30,141,74,32,170,206,29,22,162,61,
            2,234,213,92,48,86,176,213,87,226,235,14,96,61,26,149,64,148,128,19,22,178,95,131,122,229,140,177,130,173,190,18,
            95,119,0,235,209,168,4,162,234,220,97,33,218,35,160,94,205,5,99,5,91,125,37,190,238,0,214,163,81,9,68,213,
            185,195,66,180,71,64,189,154,11,198,10,182,250,74,124,221,1,172,71,163,18,136,170,115,135,133,104,143,128,122,53,23,
            140,21,108,245,149,248,186,3,88,143,70,37,16,85,231,14,11,209,30,1,245,106,46,24,43,216,234,43,49,101,0,53,
            23,57,197,8,57,97,214,137,5,40,196,152,145,242,29,168,219,31,211,201,42,96,62,196,25,166,232,204,49,76,114,66,
            109,13,176,0,133,24,51,82,190,3,117,251,99,57,153,67,168,60,43,147,76,145,41,71,30,195,34,39,204,58,177,0,
            133,24,51,82,190,3,117,251,99,58,89,5,204,135,56,195,20,157,178,228,65,13,178,194,233,2,20,98,204,72,249,14,
            212,237,143,229,100,14,161,242,172,76,50,69,166,28,121,12,139,156,48,235,196,2,20,98,140,121,5,10,247,199,116,178,
            10,152,15,113,134,41,58,101,201,131,26,100,133,211,5,40,196,152,145,242,29,168,219,31,211,201,42,168,124,72,164,153,
            34,83,150,156,176,200,9,181,53,192,2,20,98,140,121,5,10,247,199,116,178,10,152,15,113,134,41,58,115,12,147,156,
            80,91,3,44,64,33,198,152,87,160,112,127,76,39,171,160,242,33,145,102,138,76,140,19,3,27,212,19,103,34,125,34,
            143,2,11,147,49,13,204,2,98,9,100,123,2,213,33,108,20,153,178,71,14,16,1,27,38,142,29,177,182,242,121,39,
            196,52,48,11,136,37,144,237,9,84,131,244,65,50,101,143,240,143,97,54,96,65,33,108,98,179,199,194,100,76,3,179,
            128,88,2,217,158,64,53,72,31,36,83,246,8,255,24,216,96,62,10,133,244,137,204,30,11,147,49,13,204,2,98,9,
            100,123,2,213,32,125,144,76,217,35,252,99,96,131,249,40,20,210,39,50,123,44,76,198,52,48,11,136,37,144,237,9,
            84,131,244,65,50,101,143,240,143,97,54,96,65,33,108,98,179,199,194,100,76,3,179,128,88,2,217,158,64,53,72,31,
            36,83,246,8,255,24,216,96,62,10,133,244,137,204,30,11,147,49,13,204,2,98,9,100,123,2,213,33,108,20,153,178,
            71,14,16,193,108,192,130,66,216,252,123,93,20,42,17,39,83,142,35,6,220,103,76,85,231,14,40,96,94,97,10,217,
            58,135,217,128,5,182,134,188,34,43,72,243,164,62,57,181,200,164,165,124,135,170,115,7,20,48,175,48,133,108,157,195,
            108,192,2,91,67,94,145,21,164,121,82,159,156,154,158,180,84,143,80,2,182,128,2,230,21,166,144,173,115,88,13,202,
            137,19,144,87,100,5,105,158,212,39,167,166,39,45,213,35,148,128,45,160,128,121,133,41,100,235,28,102,3,22,216,26,
            242,138,172,32,205,147,250,228,212,244,164,165,122,132,18,176,5,20,48,175,48,133,108,157,195,106,80,78,156,128,188,34,
            43,72,243,164,62,57,181,200,164,165,124,135,170,115,7,20,48,175,48,133,108,157,195,106,80,78,156,128,188,34,43,72,
            243,164,62,57,53,61,105,169,30,161,4,108,1,5,204,43,76,33,91,231,48,27,176,192,214,144,87,100,5,105,158,212,
            39,167,22,153,180,148,239,80,117,238,128,2,230,21,166,144,173,115,88,13,202,137,19,144,87,100,5,105,82,125,202,26,
            19,33,198,252,116,97,14,213,129,177,137,41,180,10,33,175,64,65,182,131,5,251,145,234,67,107,53,22,39,32,111,119,
            66,62,15,118,96,108,98,10,173,66,200,43,80,144,237,96,193,126,164,250,208,90,141,197,9,43,175,18,144,207,162,58,
            48,54,49,133,86,33,228,21,40,200,118,176,96,63,82,125,104,173,198,226,132,149,87,9,200,231,193,14,140,77,76,161,
            85,8,121,5,10,178,29,44,216,143,84,31,90,171,177,56,1,121,187,19,242,89,84,7,198,38,166,208,42,132,188,2,
            5,217,14,22,236,71,170,15,173,213,88,156,128,188,221,9,249,60,216,129,177,137,41,180,10,33,175,64,65,182,131,5,
            251,145,234,83,214,152,224,193,173,188,74,64,62,139,234,192,216,196,20,90,133,144,87,160,0,99,5,91,238,71,170,79,
            89,99,130,7,135,252,116,97,22,236,192,216,196,20,90,133,144,87,160,32,219,193,130,253,72,245,161,181,26,139,19,86,
            94,37,32,159,69,117,96,108,98,10,173,66,200,43,80,128,177,130,45,247,99,74,31,14,197,60,89,200,160,64,182,141,
            144,109,186,209,100,178,80,193,99,131,3,230,25,217,30,17,162,64,146,41,123,208,145,121,178,144,65,129,108,27,33,219,
            116,163,201,100,161,130,199,6,7,204,51,178,61,34,68,129,36,83,246,160,35,243,100,33,131,2,217,54,66,182,233,70,
            147,201,66,5,143,13,14,152,103,100,123,68,136,2,73,166,236,65,71,230,201,66,6,5,178,109,132,108,211,141,38,147,
            133,10,30,27,28,48,207,200,246,136,16,5,146,76,217,131,142,204,147,133,12,10,100,219,8,217,166,27,77,38,11,21,
            60,54,56,96,158,145,237,17,33,10,36,153,178,7,29,153,39,11,25,20,200,182,17,178,77,55,154,76,22,42,120,108,
            112,192,60,35,219,35,66,20,72,50,101,15,58,50,79,22,50,40,144,109,35,100,155,110,52,153,44,84,240,216,224,128,
            121,70,182,71,132,40,144,100,202,30,116,100,158,44,100,80,32,219,70,200,54,221,104,50,89,168,224,177,193,1,243,140,
            108,143,8,81,32,201,148,61,232,56,165,167,199,106,80,78,86,172,18,33,198,188,41,196,88,63,26,11,238,176,192,134,
            44,104,32,201,148,61,232,56,165,167,199,106,80,78,86,172,18,33,198,188,41,196,88,63,26,11,238,176,192,134,44,104,
            32,201,148,61,232,56,165,167,199,106,80,78,185,88,39,32,111,118,98,172,31,141,5,119,88,96,67,22,52,144,100,202,
            30,116,156,210,211,99,53,40,39,43,86,137,16,155,121,76,96,172,31,141,5,119,88,96,67,22,52,144,100,202,30,116,
            156,210,211,99,53,40,39,43,86,137,16,99,222,20,98,172,31,141,5,119,24,160,62,15,58,72,50,101,15,58,78,233,
            233,177,26,148,147,21,171,68,136,205,60,38,48,214,143,198,130,59,44,176,33,11,26,72,50,101,15,58,78,233,233,177,
            26,148,19,198,95,151,251,187,133,227,41,152,96,151,2,27,178,160,129,36,83,246,160,227,148,158,30,171,65,57,97,252,
            245,185,230,57,88,96,135,2,27,178,160,129,36,83,22,160,179,234,197,186,34,43,148,126,95,149,14,199,206,129,6,38,
            216,200,160,80,146,41,75,114,214,88,87,228,132,141,244,251,186,52,56,122,6,236,55,193,70,6,133,146,76,89,128,206,
            170,23,235,138,156,80,218,125,109,38,236,47,198,96,187,9,54,50,40,148,100,202,2,116,86,189,88,87,100,132,215,210,
            238,201,172,49,17,103,129,227,167,193,118,19,108,100,80,40,201,148,5,232,172,122,177,174,200,8,239,66,125,203,150,95,
            131,47,195,255,239,112,252,52,224,98,131,141,12,10,37,153,178,36,103,141,117,69,70,120,63,54,91,15,63,253,159,221,
            95,195,31,17,136,108,168,113,83,159,15,237,187,5,189,231,142,123,60,185,36,216,200,160,80,146,41,11,208,89,245,98,
            93,145,17,134,95,87,122,234,186,166,44,143,138,163,242,240,168,60,236,255,65,127,247,127,198,25,249,247,81,113,84,28,
            29,150,225,231,232,176,164,68,241,15,135,84,42,95,254,199,171,110,88,88,169,105,24,236,250,50,96,35,131,66,73,166,
            44,64,103,213,139,117,69,70,24,202,235,199,245,101,121,20,126,142,142,252,95,37,253,217,197,145,127,247,127,194,191,253,
            15,253,161,165,52,132,69,89,94,242,108,205,105,24,140,26,211,96,35,131,66,73,170,140,78,140,37,196,88,33,219,34,
            157,195,150,124,125,73,79,226,225,240,92,143,150,74,255,36,15,79,53,167,199,11,130,151,134,55,40,92,225,41,253,31,
            79,189,219,17,221,127,145,63,221,104,26,154,200,92,101,1,81,117,217,134,164,202,194,119,140,37,196,88,33,219,34,157,
            61,235,230,200,111,135,134,197,176,91,6,180,112,202,35,191,136,124,161,223,128,237,150,201,15,65,222,47,177,178,40,234,
            122,89,47,234,170,170,171,162,174,232,255,127,89,62,245,228,37,62,215,196,195,85,2,209,165,72,149,165,241,8,75,136,
            177,66,182,69,58,123,62,22,180,85,41,143,142,94,30,245,207,238,15,197,145,223,29,28,249,253,197,176,26,208,178,226,
            197,208,139,168,236,27,252,126,166,40,234,213,234,97,245,240,233,161,125,88,181,15,237,234,161,125,120,104,31,218,205,118,
            180,83,154,78,124,174,137,135,171,4,162,75,145,42,75,227,17,150,80,37,16,209,53,22,202,236,149,127,114,127,240,203,
            163,60,250,97,88,40,47,143,202,31,142,202,31,202,163,151,71,135,244,191,97,83,116,84,254,206,103,121,189,32,33,173,
            18,101,85,175,62,61,60,60,60,172,30,30,252,255,233,47,90,52,159,135,35,169,61,23,72,124,174,83,30,173,74,68,
            73,149,165,241,8,75,168,18,136,232,26,11,101,246,37,29,250,12,207,237,33,45,140,225,55,127,216,246,208,51,127,72,
            127,31,149,63,20,180,144,40,75,11,108,248,161,131,174,226,101,211,109,62,124,90,249,5,241,240,119,90,2,183,15,171,
            191,83,176,106,229,104,83,137,207,117,202,163,85,137,40,169,178,52,30,97,9,49,86,200,182,72,103,127,204,95,250,167,
            185,223,222,15,11,225,15,71,71,229,31,252,70,234,101,191,62,188,244,7,182,195,6,234,119,126,195,245,242,232,232,208,
            175,17,69,185,184,105,234,101,179,249,68,139,96,117,219,174,254,78,75,129,150,197,45,253,61,158,193,116,212,92,177,128,
            40,129,232,82,164,202,210,120,132,37,84,9,68,116,141,133,50,251,146,246,197,63,12,91,164,126,47,225,23,8,109,155,
            134,93,183,255,233,183,82,187,29,117,191,228,138,178,108,154,106,190,92,254,165,253,188,106,87,171,182,93,53,237,138,150,
            196,195,223,87,180,72,134,69,177,239,41,125,124,174,83,30,173,74,68,73,149,165,241,8,75,168,18,136,232,26,11,57,
            225,207,140,249,233,165,127,244,123,137,240,188,15,255,242,153,254,223,135,188,159,232,119,43,197,209,226,230,210,205,151,191,
            172,222,188,221,180,237,178,89,210,17,212,130,22,193,176,157,186,21,83,24,200,239,56,212,92,177,128,40,129,232,82,164,
            202,224,201,225,168,167,255,98,199,93,133,190,21,17,227,254,203,26,251,136,251,250,255,124,102,60,133,254,216,230,208,111,
            125,252,102,170,95,28,253,238,184,127,238,253,254,195,239,48,134,5,230,247,14,47,15,105,241,120,69,125,179,152,87,111,
            86,203,95,150,85,215,212,254,116,251,168,44,150,180,105,122,120,248,59,173,26,97,125,168,23,187,159,69,250,164,34,254,
            132,246,211,239,51,252,32,153,84,103,132,84,89,57,96,34,196,38,40,196,216,24,221,111,105,252,238,152,126,235,135,77,
            212,209,31,250,103,191,63,68,58,124,73,199,173,69,191,178,244,135,76,126,177,21,229,85,83,186,249,124,121,123,251,118,
            89,117,215,253,217,201,81,89,188,241,155,168,91,191,243,240,131,124,129,249,227,36,20,74,136,9,182,130,188,18,198,73,
            149,149,3,38,66,108,130,66,140,141,209,139,97,219,239,247,25,126,141,232,151,75,88,9,142,250,181,165,79,249,5,208,
            255,208,18,107,154,178,114,149,187,109,111,111,175,171,155,69,191,78,209,106,241,214,239,54,218,213,106,213,134,141,17,79,
            131,216,127,173,192,4,91,89,121,238,140,146,42,43,7,76,240,24,22,40,196,216,24,253,232,168,12,59,131,112,28,203,
            127,209,106,208,175,12,97,5,161,117,163,188,186,121,183,160,101,114,211,156,207,139,215,110,126,123,187,92,45,139,155,203,
            97,153,29,150,229,178,125,219,46,151,205,109,211,190,13,227,240,52,136,236,206,66,205,25,19,108,5,121,37,140,147,42,
            43,7,76,132,216,4,133,24,27,163,211,90,65,91,22,122,190,253,193,20,45,1,127,90,55,44,129,176,22,12,123,144,
            242,170,105,202,230,230,168,40,155,155,186,120,93,84,174,186,189,189,189,253,101,126,117,73,86,229,239,124,87,73,87,6,
            253,213,168,48,14,79,131,248,34,46,209,71,80,115,198,4,91,65,94,9,227,164,202,232,160,198,224,132,5,10,49,142,
            143,190,166,231,183,191,156,68,43,7,237,39,252,166,42,92,38,167,39,181,190,124,57,92,115,61,44,47,155,203,210,185,
            203,166,40,111,58,231,92,237,230,238,47,191,172,110,111,223,22,203,63,30,149,254,148,132,126,250,107,41,212,29,6,226,
            105,16,255,95,109,160,148,37,39,44,80,136,177,49,186,255,85,30,206,32,248,56,233,135,146,22,134,127,246,95,54,31,
            187,245,122,221,244,87,70,154,155,218,205,107,215,92,22,55,157,59,175,94,23,85,125,86,223,222,46,111,127,153,95,94,
            246,87,11,105,237,25,22,72,121,116,116,24,198,225,105,16,95,111,81,40,97,156,84,25,29,212,24,156,176,64,33,198,
            198,232,254,89,27,142,150,104,91,213,191,234,112,88,190,172,95,150,101,81,94,190,107,22,117,221,124,124,188,47,139,98,
            209,93,58,87,213,174,248,120,221,124,116,7,243,178,152,159,87,167,127,105,111,111,219,101,221,92,249,131,42,90,149,194,
            206,199,92,43,228,20,34,40,33,38,216,10,242,74,24,39,85,70,7,53,6,39,44,80,136,177,49,122,65,187,98,191,
            94,208,62,218,47,147,195,178,188,126,215,173,63,222,223,92,53,87,245,185,115,206,157,253,183,238,241,178,185,41,93,241,
            186,112,207,222,116,221,77,49,163,124,81,185,249,242,150,142,102,235,119,151,195,186,48,252,244,219,168,34,188,144,231,167,
            80,188,46,106,87,187,226,174,185,187,75,94,63,87,115,198,68,136,205,60,119,70,201,148,61,194,63,6,54,48,147,5,
            66,230,247,21,225,140,250,7,90,12,135,101,217,220,119,205,117,93,215,245,245,194,185,170,172,74,231,158,159,150,205,181,
            115,174,114,110,254,220,185,19,247,204,205,220,236,148,18,75,218,64,93,215,221,31,251,181,169,223,73,28,210,41,35,45,
            141,241,232,167,33,120,110,205,21,102,167,11,150,0,235,74,32,201,148,61,232,168,192,6,102,178,64,202,194,47,113,248,
            109,62,44,95,222,52,165,59,165,85,225,96,230,234,234,188,58,159,87,175,43,191,14,184,234,156,254,113,226,255,237,220,
            236,160,162,221,246,155,255,58,111,111,235,155,5,157,81,252,201,95,69,28,237,118,196,90,17,130,83,152,4,131,179,83,
            5,75,128,117,37,144,100,202,30,116,84,96,3,51,89,32,101,244,155,235,175,6,14,47,217,149,229,205,130,126,217,107,
            255,228,87,116,230,80,149,180,16,220,188,152,59,231,230,101,225,234,130,22,197,140,54,81,243,98,94,189,169,254,178,188,
            174,110,232,133,188,162,241,39,122,116,244,245,67,255,42,136,24,61,4,223,215,10,225,51,92,28,10,155,117,127,24,75,
            255,163,237,80,241,154,118,200,206,185,243,202,213,174,114,117,237,206,93,113,234,138,185,155,185,121,181,172,251,234,201,137,
            155,157,156,93,47,171,235,213,155,226,238,229,97,241,31,150,237,91,127,30,206,59,110,185,175,8,23,164,194,189,130,97,
            106,12,207,14,9,5,75,128,117,37,144,100,202,30,116,84,96,3,51,89,32,100,235,254,165,210,240,98,208,203,242,178,
            59,160,223,117,58,119,115,5,173,18,175,171,243,234,53,173,33,110,217,109,62,110,90,231,138,235,165,95,45,156,59,89,
            94,23,167,103,39,111,154,165,171,110,94,22,69,253,183,85,83,248,163,0,62,140,18,163,227,84,66,204,100,11,150,0,
            235,74,32,201,148,61,232,168,192,6,102,178,64,202,134,77,122,127,5,234,136,174,43,157,86,180,75,152,159,23,175,105,
            229,40,105,165,240,207,251,178,185,190,94,54,205,245,236,180,185,123,93,56,55,95,186,235,199,205,246,110,121,114,182,124,
            115,86,118,101,89,173,218,85,251,54,236,125,250,5,44,70,15,23,60,190,175,21,49,31,127,106,221,255,30,251,243,130,
            155,75,58,137,166,253,193,107,87,156,211,230,169,172,233,247,255,249,233,153,91,212,103,207,206,220,108,86,55,142,54,77,
            229,188,189,223,220,181,93,61,43,156,171,187,178,88,208,85,192,166,63,136,26,142,144,197,90,241,28,167,18,98,38,91,
            176,4,88,87,2,73,166,236,65,71,5,54,48,147,5,82,214,223,232,52,220,219,113,84,30,54,141,223,71,211,229,37,
            218,33,244,251,138,19,55,59,61,59,152,127,232,22,238,236,224,108,54,115,245,153,171,235,249,115,183,172,139,186,174,78,
            102,238,172,252,88,22,197,219,213,170,253,83,56,26,243,119,133,200,35,168,205,199,237,199,237,151,251,111,123,173,64,7,
            233,27,177,198,122,94,16,216,73,214,219,199,181,191,90,193,103,219,101,121,217,208,74,80,187,121,113,94,185,243,234,220,
            31,54,205,232,104,233,204,53,119,173,115,39,7,179,147,197,166,184,110,239,55,205,108,230,170,182,245,229,197,250,168,44,
            170,246,195,159,232,124,253,135,162,95,53,240,8,10,225,153,168,4,130,2,140,49,175,10,146,84,25,29,216,18,11,79,
            23,4,164,140,150,1,223,46,75,231,119,29,157,182,85,69,237,170,115,58,161,123,237,202,217,233,129,95,22,207,220,25,
            157,75,156,204,156,187,174,170,122,121,237,223,101,119,221,213,238,57,253,143,238,135,106,150,195,237,132,135,139,225,136,44,
            57,29,158,133,74,0,170,3,99,198,44,8,82,101,116,8,49,35,229,79,17,4,164,236,192,175,15,253,90,225,143,106,
            223,213,206,213,116,58,65,187,108,218,61,159,158,29,204,78,102,254,84,192,29,156,204,102,7,206,213,31,238,218,85,179,
            164,125,200,204,181,215,180,164,154,142,46,96,93,210,125,83,71,197,97,81,55,254,62,231,178,12,183,48,227,44,60,60,
            11,149,64,80,128,49,99,22,4,169,50,58,132,152,145,242,167,8,2,82,54,92,12,31,142,121,104,103,113,71,7,179,
            245,188,156,151,115,58,145,112,51,119,50,155,61,155,209,194,112,174,56,56,153,157,184,170,219,108,54,155,142,54,101,180,
            227,152,205,220,236,242,93,216,235,208,75,170,238,109,83,246,247,235,136,125,5,194,179,80,9,64,117,96,204,152,5,65,
            170,140,14,33,102,164,252,41,130,128,148,13,247,215,244,27,40,255,2,197,186,118,229,235,226,156,174,113,20,231,180,36,
            232,153,126,126,242,236,228,153,191,2,56,123,238,206,234,205,230,126,211,181,149,163,213,228,204,75,22,119,225,68,145,94,
            49,250,223,171,246,191,251,168,72,79,7,171,60,45,4,5,24,99,94,21,36,169,50,58,176,37,22,158,46,8,72,153,
            95,18,254,104,54,188,30,122,217,21,174,156,211,21,84,231,220,115,191,78,208,47,127,191,133,242,79,254,201,114,179,249,
            188,217,52,231,133,115,7,180,170,204,102,207,138,187,63,210,106,240,59,122,197,239,63,208,9,198,162,63,121,76,78,135,
            103,161,18,128,234,192,152,49,11,130,84,25,29,66,204,72,249,83,4,1,41,243,239,81,241,107,69,184,69,249,240,234,
            198,249,101,81,212,254,120,105,118,64,23,156,158,249,94,58,215,155,205,234,237,166,219,110,186,37,237,212,103,39,7,206,
            209,126,253,224,202,223,146,64,23,162,254,178,122,88,209,162,192,43,179,10,158,133,74,32,40,192,152,49,11,130,84,25,
            29,66,204,72,249,83,4,1,41,27,206,182,135,83,238,254,191,203,198,209,194,240,215,94,253,174,129,214,133,103,103,207,
            102,39,207,40,87,125,240,43,197,246,141,95,48,142,150,15,45,144,155,151,126,83,87,212,203,213,106,245,208,190,245,59,
            139,127,163,35,40,37,192,24,243,170,32,201,148,5,108,137,214,24,51,88,224,206,120,33,68,7,253,50,24,238,189,233,
            207,5,14,175,26,58,162,173,253,30,155,214,137,243,202,205,206,158,157,60,127,118,118,250,252,196,53,155,205,102,219,109,
            58,127,237,246,25,29,219,186,217,51,186,242,225,247,217,197,91,127,35,243,170,173,252,209,64,24,39,14,206,17,65,189,
            98,178,80,178,143,94,206,72,61,145,145,177,177,192,157,241,66,136,250,91,11,250,163,157,126,253,56,42,75,119,121,87,
            156,210,111,188,155,157,209,249,5,93,252,59,157,61,59,153,205,206,158,57,247,102,75,7,80,219,59,90,20,126,131,69,
            219,49,215,252,227,112,146,88,189,125,160,183,90,180,141,95,182,201,55,19,227,20,21,216,160,152,44,148,236,165,151,83,
            194,39,50,50,54,22,184,51,94,24,2,127,101,150,254,208,29,54,225,118,131,178,168,223,214,103,39,207,105,181,160,167,
            123,94,186,243,194,157,204,158,159,82,224,150,116,36,187,217,248,165,69,175,34,249,29,123,125,69,175,220,209,45,55,238,
            127,211,174,226,111,237,207,254,62,244,48,234,112,143,46,220,1,133,115,68,164,58,194,100,161,100,31,189,156,145,122,34,
            35,99,99,129,59,227,133,16,245,219,39,191,64,248,206,241,178,188,92,118,155,198,157,210,209,209,57,173,21,21,157,203,
            57,119,70,9,87,119,180,125,218,124,112,103,39,116,49,202,31,225,158,221,92,13,167,136,71,69,69,139,162,253,211,194,
            191,199,79,173,21,226,22,40,156,35,50,214,70,153,44,148,236,163,151,51,82,79,100,100,108,44,112,103,188,224,255,185,
            125,124,60,232,183,75,225,253,46,5,189,179,116,113,185,188,223,220,111,234,153,115,231,180,243,174,157,123,54,59,107,55,
            155,187,234,180,120,237,222,108,182,155,207,155,237,157,155,185,15,219,230,244,140,214,141,89,115,21,54,114,69,177,90,181,
            31,250,3,168,221,121,5,177,25,7,4,78,81,129,13,138,201,66,201,62,122,57,35,245,68,70,198,198,2,119,198,11,
            33,242,27,165,176,113,162,255,47,110,186,102,209,117,155,237,221,201,153,223,23,212,206,21,167,213,201,179,118,219,109,105,
            87,93,45,252,169,246,166,117,179,179,187,205,99,227,183,81,139,107,186,119,243,168,248,161,60,114,197,219,15,43,58,52,
            246,91,190,48,78,20,156,162,2,27,20,147,133,146,189,244,114,74,248,68,70,198,198,2,119,198,11,67,176,222,189,181,
            165,63,120,186,188,94,92,215,85,181,236,54,238,172,107,221,252,188,160,101,113,186,156,157,209,129,211,151,165,171,230,45,
            157,85,108,54,205,201,172,186,219,250,151,245,92,185,89,215,253,125,232,116,182,189,108,22,253,123,1,142,138,244,162,208,
            147,4,80,175,152,44,148,236,163,151,51,82,79,100,100,108,44,112,103,188,16,162,243,195,163,195,254,182,204,254,164,187,
            174,187,246,195,93,91,221,45,103,203,205,35,189,88,49,167,85,163,59,57,89,110,105,85,113,69,221,250,237,211,230,250,
            172,186,163,99,218,179,83,87,186,230,3,221,210,57,188,254,212,92,247,75,133,214,180,48,78,28,156,35,130,122,197,100,
            161,100,95,189,71,78,13,159,208,41,5,132,173,61,195,43,169,195,205,6,197,162,108,183,254,64,245,250,236,164,219,110,
            150,207,138,215,197,249,220,85,219,186,234,55,75,110,94,111,58,250,199,102,49,171,238,62,108,54,119,207,157,115,167,205,
            199,182,163,115,60,127,167,194,85,191,32,232,93,52,226,188,130,238,178,221,97,207,105,199,88,35,132,86,126,226,178,201,
            148,227,200,17,237,185,216,5,132,173,61,254,56,150,62,150,195,63,121,101,179,236,54,219,205,166,173,91,87,109,186,45,
            93,135,170,92,113,82,63,110,186,205,135,237,102,187,241,219,174,45,173,21,155,165,63,214,45,252,177,149,91,92,215,69,
            121,229,55,119,47,253,75,31,253,203,81,124,199,135,63,138,13,55,221,40,228,156,118,160,206,124,148,170,131,19,81,50,
            229,56,114,68,123,46,118,1,97,107,98,237,194,155,79,253,179,184,104,174,63,188,105,186,246,205,178,125,211,109,54,219,
            226,180,58,119,243,226,180,222,208,115,79,235,66,93,180,254,31,221,166,123,108,79,138,186,156,205,78,159,251,139,233,238,
            89,243,174,127,127,113,191,239,233,255,242,163,12,7,176,95,112,42,140,152,211,8,212,153,143,82,229,217,34,74,166,28,
            71,12,24,27,51,91,64,216,218,51,156,96,251,203,79,165,95,41,218,106,249,151,218,63,223,219,187,225,4,239,204,209,
            58,65,215,157,54,117,213,110,62,211,98,217,110,58,87,92,251,151,95,221,204,213,117,237,138,245,165,63,205,163,165,241,
            195,112,251,205,110,95,65,171,7,78,133,17,83,26,129,58,243,81,170,14,78,68,201,148,227,200,17,237,185,216,5,132,
            173,61,7,126,251,52,156,222,93,94,182,155,237,166,173,218,118,243,193,63,239,103,53,189,144,55,119,179,118,187,233,232,
            53,138,102,190,164,243,59,191,48,218,19,186,232,113,112,250,220,61,123,78,87,113,111,154,254,236,228,165,255,123,188,86,
            4,112,42,140,80,141,64,157,249,40,85,7,39,162,100,202,113,228,136,246,92,236,2,194,214,158,225,196,172,95,43,154,
            5,109,149,186,249,170,107,253,70,136,94,207,115,181,59,175,102,103,254,0,246,75,235,252,250,226,55,81,219,230,236,217,
            217,233,193,201,193,41,93,157,157,185,155,110,120,119,43,45,1,218,83,248,247,41,133,125,133,191,49,16,167,194,200,57,
            237,64,157,249,40,85,158,45,162,100,202,113,196,128,177,49,179,5,132,173,137,53,159,225,21,71,71,238,221,117,243,161,
            109,223,84,119,159,253,22,104,121,224,138,215,174,156,211,53,191,170,219,116,221,210,85,203,205,253,102,67,235,204,102,179,
            41,78,14,104,173,56,240,87,69,138,238,93,216,63,208,214,105,184,76,27,206,43,252,94,219,254,168,52,49,167,17,168,
            51,31,165,234,224,68,148,76,57,142,28,209,158,139,93,64,216,218,227,198,7,179,87,93,221,86,85,213,110,218,238,51,
            93,5,63,165,183,83,132,91,161,174,151,244,142,35,255,170,246,118,179,249,215,77,183,185,59,235,95,109,117,207,103,117,
            229,234,199,133,127,250,251,59,52,253,255,224,194,199,255,131,35,40,149,136,147,41,15,208,219,196,71,74,31,142,222,25,
            239,7,9,239,136,15,138,144,28,154,251,30,139,157,55,65,219,38,122,97,219,159,155,149,221,162,219,220,109,54,31,62,
            249,147,11,218,35,191,46,232,101,211,210,31,35,205,139,170,165,243,188,126,157,232,15,103,79,220,217,51,186,74,88,187,
            131,155,143,180,12,104,159,211,127,44,139,223,101,132,113,234,197,226,127,45,174,175,23,187,159,80,17,15,119,132,127,60,
            8,63,214,241,83,228,63,92,32,60,236,225,163,6,210,100,5,163,177,49,175,38,133,117,213,41,229,35,100,91,120,25,
            213,239,189,255,225,106,73,187,130,15,255,242,225,115,183,217,46,78,252,73,197,220,189,166,255,232,102,230,55,221,214,95,
            148,245,75,131,36,155,122,54,123,126,226,174,221,156,46,143,172,175,250,183,22,31,14,183,6,210,31,49,29,57,116,2,
            156,179,194,108,192,66,156,41,50,211,145,199,202,9,48,86,136,174,117,120,51,170,127,222,138,230,146,126,237,63,252,11,
            253,222,119,244,162,106,73,87,161,230,174,46,230,197,156,174,120,108,186,229,27,146,12,103,121,155,109,123,189,108,54,221,
            89,93,185,234,89,119,227,159,255,225,243,211,252,27,203,228,29,31,98,232,20,56,103,133,213,128,121,131,41,58,211,146,
            39,145,19,96,172,144,109,253,129,167,63,195,59,44,143,46,27,186,232,231,247,202,219,246,212,223,29,72,59,139,210,205,
            93,249,230,3,29,55,45,103,5,45,145,1,90,38,180,41,155,211,91,49,14,22,247,244,210,108,120,53,176,95,43,196,
            125,80,114,232,4,56,103,133,217,128,133,56,83,100,166,35,143,149,19,96,172,144,109,255,48,188,63,219,63,129,174,188,
            163,195,217,254,181,235,5,189,227,229,117,225,94,187,185,155,215,245,167,251,205,182,219,110,170,186,170,187,77,183,253,188,
            185,247,235,133,95,67,26,122,79,146,59,45,215,195,109,154,191,227,143,167,248,127,189,86,168,66,156,41,50,211,145,199,
            202,9,48,86,12,245,225,29,64,97,65,248,251,102,143,202,166,94,210,243,75,191,240,85,61,47,251,55,66,22,213,130,
            78,253,58,186,32,91,93,111,223,84,254,132,111,227,239,251,240,11,227,77,93,185,187,166,44,222,209,202,21,62,135,226,
            15,126,233,138,233,132,32,11,206,89,97,53,96,222,96,138,46,59,150,85,87,147,145,242,17,178,205,175,15,126,99,66,
            183,66,149,109,211,250,237,83,183,109,79,233,67,35,202,249,121,85,215,75,191,112,252,89,223,155,235,182,91,86,215,116,
            101,144,78,191,253,182,236,67,189,105,234,197,199,155,197,255,24,150,130,255,187,63,193,16,211,145,67,39,192,57,43,204,
            6,44,196,153,34,51,29,121,172,156,0,99,133,108,235,95,207,166,75,179,180,56,94,222,53,116,81,163,219,108,30,223,
            60,175,29,221,246,228,170,250,218,159,123,111,62,248,91,2,171,197,242,186,58,163,147,238,254,74,20,29,70,189,221,60,
            182,151,63,95,94,94,245,87,179,254,176,123,71,153,124,127,133,28,58,1,206,89,97,54,96,33,206,20,153,233,200,99,
            229,4,24,43,68,23,189,138,231,15,160,252,39,73,184,134,86,138,206,159,191,21,111,110,28,189,143,165,172,27,127,50,
            49,60,237,31,230,205,166,109,218,229,162,241,87,169,186,205,166,89,46,187,182,105,58,186,44,27,78,237,118,23,201,197,
            116,66,208,117,95,238,147,31,46,129,115,86,88,13,152,55,72,233,76,43,44,132,152,177,10,86,30,134,24,142,116,134,
            255,117,203,214,191,88,186,217,222,21,221,194,209,178,168,239,218,59,255,98,209,103,255,223,151,213,156,222,90,209,44,231,
            117,189,88,94,55,117,229,150,93,219,182,205,31,195,139,120,126,111,225,239,12,60,194,79,190,9,129,250,54,11,161,138,
            196,38,65,184,39,169,62,211,26,11,60,9,200,155,5,204,195,16,7,126,251,212,175,24,69,221,209,45,4,254,69,186,
            182,238,238,218,75,186,11,179,56,112,139,118,73,251,132,127,245,91,164,15,237,135,118,73,159,117,80,213,69,221,220,52,
            119,205,178,217,248,55,166,250,139,227,253,42,225,95,196,43,138,114,119,78,61,105,78,86,108,18,132,123,146,234,51,173,
            177,192,147,128,188,89,192,252,174,195,111,198,119,191,203,229,209,193,213,221,146,174,192,210,150,232,122,113,183,120,124,220,
            186,3,186,75,179,112,174,241,151,1,63,183,237,191,124,222,108,174,235,178,162,219,116,238,186,238,230,93,119,223,182,109,
            91,135,15,210,46,105,211,68,123,158,162,168,22,187,207,153,21,67,255,251,92,43,204,130,149,151,67,172,15,253,249,157,
            191,114,84,148,205,178,161,205,19,29,163,54,203,199,143,77,93,210,187,85,203,186,168,28,29,227,126,110,219,213,237,219,
            219,246,182,112,243,210,21,117,113,213,92,21,101,89,46,63,132,155,149,135,237,28,157,106,23,69,253,246,243,167,15,225,
            93,243,211,230,100,197,38,65,184,39,169,62,211,26,11,60,137,92,193,202,195,16,254,59,14,250,181,226,176,188,235,47,
            188,210,139,17,215,245,101,113,122,64,175,226,149,181,171,203,217,210,159,73,208,39,63,221,254,178,156,59,186,117,179,116,
            174,188,108,202,35,250,216,198,182,14,139,129,126,232,3,55,139,229,234,243,195,223,62,137,69,17,238,12,252,247,185,86,
            40,33,22,48,63,20,194,83,244,15,253,155,131,252,77,179,55,111,233,119,255,222,191,112,218,208,253,104,101,93,184,178,
            46,202,170,60,173,105,119,254,185,189,93,221,222,182,43,119,230,11,174,40,139,155,155,162,88,62,180,43,191,32,232,162,
            71,255,249,179,69,245,246,211,175,159,30,30,190,175,21,137,60,12,65,27,150,225,215,185,124,55,188,68,183,253,76,247,
            254,21,53,45,5,122,186,235,243,162,240,119,164,109,63,181,183,191,220,46,127,241,123,15,90,45,106,87,118,87,238,246,
            211,45,109,160,232,211,203,253,39,123,209,205,154,45,125,66,252,175,159,30,196,52,240,211,12,212,156,172,216,36,8,247,
            36,213,103,90,99,129,39,1,121,179,128,121,57,132,127,223,246,240,77,58,215,180,82,244,91,168,205,246,198,149,197,121,
            81,190,118,37,45,16,218,105,208,253,103,155,214,239,43,232,222,130,131,162,118,231,180,241,234,170,219,213,3,189,33,117,
            184,170,232,239,68,95,125,122,248,245,225,215,79,191,198,119,219,56,37,85,192,216,36,8,247,36,213,135,214,106,44,140,
            179,176,67,156,32,27,62,190,151,22,200,77,125,71,251,236,173,191,137,224,174,152,151,117,89,249,143,28,42,207,233,211,
            185,42,90,101,62,183,183,183,203,118,73,119,152,187,126,159,126,208,52,183,225,61,70,253,30,135,62,214,224,211,223,252,
            58,241,171,220,87,132,65,213,90,129,4,161,34,43,152,70,170,31,135,80,147,194,56,11,59,196,9,50,186,227,163,63,
            246,252,111,205,242,158,142,99,253,233,220,182,163,35,164,242,156,222,163,74,171,69,65,47,228,209,238,162,93,253,178,106,
            151,244,62,61,191,241,162,79,160,107,55,237,138,63,122,217,31,197,174,218,207,15,159,254,246,176,250,245,211,175,98,58,
            19,231,150,120,148,89,193,52,82,253,56,132,154,20,198,89,216,33,78,144,249,79,95,246,87,3,253,107,21,254,37,58,
            127,193,117,225,138,243,186,168,105,33,148,101,125,238,232,131,150,239,232,94,254,213,47,215,213,117,85,30,184,115,87,211,
            103,201,150,85,247,169,105,10,250,14,5,127,159,71,81,44,219,95,251,205,211,131,177,129,250,190,86,140,25,84,107,190,
            221,163,108,22,244,154,16,93,222,160,107,75,219,107,71,219,167,186,56,167,53,192,159,69,184,69,77,215,157,254,165,173,
            22,29,173,21,7,165,95,88,174,124,179,109,47,71,239,94,170,62,208,55,237,248,37,241,16,223,64,225,100,20,65,168,
            200,10,166,145,234,199,33,212,164,48,206,194,14,113,130,204,133,203,129,135,215,13,237,42,232,229,161,97,173,40,233,227,
            111,104,135,237,143,105,139,210,45,219,122,209,182,237,226,77,183,169,93,241,140,110,64,40,253,1,239,205,134,94,213,30,
            62,41,184,120,251,129,214,137,135,213,195,223,172,221,246,247,181,98,76,144,13,111,137,244,31,49,238,239,214,247,119,104,
            110,238,59,250,36,40,250,96,40,127,38,87,156,23,117,237,234,251,110,89,87,245,170,219,108,233,227,3,203,234,156,238,
            147,162,21,164,187,233,247,252,229,97,81,252,165,245,91,167,79,159,63,127,250,240,249,243,247,181,34,251,112,131,108,88,
            16,180,129,186,94,250,165,208,175,22,119,174,162,187,110,156,255,152,89,218,62,157,23,133,107,191,248,235,226,219,205,199,
            37,45,160,146,118,36,180,148,92,189,94,208,49,172,255,80,137,85,251,235,195,167,7,255,242,248,118,247,109,46,126,208,
            112,97,252,251,90,49,102,80,173,207,195,237,129,135,55,21,221,43,219,95,129,218,110,62,132,183,65,20,254,211,234,202,
            130,238,254,88,60,210,203,218,155,143,221,227,181,43,139,154,246,31,116,33,221,21,238,230,93,89,28,22,85,189,92,46,
            219,135,246,243,231,209,66,232,105,232,67,89,66,128,147,81,200,222,17,89,193,52,82,253,114,38,145,69,96,97,59,32,
            82,24,162,176,86,148,71,77,61,171,222,182,159,183,116,11,1,173,21,254,141,120,244,92,135,191,93,81,117,116,132,213,
            109,54,247,254,100,188,164,83,11,255,70,122,231,62,54,133,91,46,223,174,86,180,101,242,151,85,252,194,240,87,127,195,
            43,121,157,123,54,59,165,219,9,59,250,194,200,48,131,20,248,24,24,75,136,121,131,148,78,14,244,244,103,56,129,20,
            134,104,119,224,115,85,59,247,166,109,135,221,197,91,250,208,223,158,131,226,156,246,206,116,169,195,191,227,107,187,233,190,
            180,180,117,170,203,202,95,48,164,45,88,125,95,191,109,86,183,171,219,246,51,126,203,212,46,170,96,42,89,198,243,23,
            152,66,44,196,73,201,196,56,251,44,138,201,66,99,81,12,23,181,233,142,254,133,115,39,21,189,246,64,188,57,43,232,
            45,217,254,109,217,254,48,201,239,57,154,47,116,154,71,239,219,46,138,215,180,44,232,56,138,182,97,133,107,187,246,250,
            109,115,185,236,198,11,98,11,111,154,111,250,189,68,242,197,212,49,242,17,140,48,133,88,136,147,146,137,113,126,195,51,
            156,64,10,135,96,248,156,89,186,96,81,95,249,207,184,94,182,183,237,245,91,250,252,241,176,133,42,171,114,78,135,172,
            174,112,203,123,90,16,143,244,238,35,87,250,15,123,239,215,155,178,114,174,109,155,186,90,124,244,174,225,75,183,119,95,
            193,77,203,100,253,248,216,21,227,169,100,129,135,176,195,18,98,222,32,165,147,3,169,39,206,198,118,64,164,48,68,254,
            56,182,63,162,189,114,238,108,238,206,170,55,244,101,180,126,117,56,243,127,232,3,226,233,144,150,62,200,96,120,75,100,
            247,101,65,215,8,139,210,29,56,87,60,163,91,107,93,221,45,138,197,248,195,11,214,247,247,239,223,191,127,127,127,191,
            30,237,23,214,180,145,250,247,185,86,76,22,198,23,197,122,216,58,209,95,87,244,116,211,179,127,66,31,170,226,87,136,
            126,251,116,78,159,46,209,31,44,117,219,142,222,33,185,237,174,29,29,64,213,37,189,53,210,191,137,222,21,151,205,101,
            216,75,175,31,239,239,175,174,46,46,46,46,142,47,46,254,241,234,231,247,163,165,225,166,62,95,169,7,101,10,177,16,
            39,37,19,227,140,45,49,175,120,162,48,68,187,47,18,41,46,135,205,13,61,173,103,253,82,152,185,231,126,3,229,63,
            233,163,112,231,133,95,41,232,158,168,214,21,165,63,5,60,112,103,244,145,191,244,54,213,155,96,250,120,255,254,234,248,
            213,241,197,241,241,240,223,197,79,239,238,121,133,113,95,113,173,192,188,65,74,39,7,122,250,47,123,2,41,244,255,92,
            247,183,47,15,159,108,250,178,161,227,161,97,85,232,143,96,159,187,83,127,124,212,111,160,92,229,223,52,79,59,238,187,
            214,159,86,148,181,59,125,126,82,221,125,238,150,207,79,11,218,67,211,62,123,125,127,117,124,252,226,248,248,197,139,139,
            23,63,190,120,241,226,248,197,241,239,47,126,226,101,113,199,47,172,102,193,199,192,152,66,44,196,153,34,19,227,197,192,
            6,147,137,13,195,214,201,127,255,249,77,77,111,135,167,115,186,211,231,244,177,178,7,238,156,54,85,116,252,84,251,221,
            246,121,209,125,217,124,164,181,162,109,220,57,29,65,149,229,172,127,123,222,99,235,250,239,105,217,62,174,127,190,56,254,
            253,43,250,67,11,225,248,248,213,241,143,23,47,142,143,47,126,14,91,41,250,16,182,248,91,232,173,216,196,20,134,66,
            156,76,217,131,142,10,108,48,153,214,176,30,222,189,232,87,142,178,243,203,194,61,163,79,219,42,23,87,77,119,227,79,
            238,234,146,46,55,209,82,154,251,211,138,13,125,95,170,255,176,114,250,198,151,194,159,157,127,222,116,195,183,178,174,127,
            166,245,225,248,199,87,199,47,126,255,226,248,71,90,59,250,101,114,241,115,88,47,212,133,143,48,29,43,54,49,133,161,
            16,39,83,246,160,163,2,27,76,38,54,244,7,179,253,45,248,197,31,223,93,22,7,244,138,80,243,174,123,119,83,94,
            150,139,250,128,206,236,232,181,35,255,62,176,226,142,174,65,117,155,182,189,174,206,233,85,239,178,116,103,119,116,135,212,
            102,19,78,39,254,233,226,248,213,113,191,70,208,207,43,90,26,47,46,126,60,126,117,252,242,221,176,90,220,201,199,164,
            159,81,140,77,76,97,40,196,201,148,61,232,168,192,6,147,137,13,253,165,64,255,146,143,63,183,184,233,222,189,235,214,
            55,87,244,110,248,195,35,255,41,253,116,59,1,125,177,11,189,5,172,95,43,186,219,101,67,87,8,75,250,74,5,71,
            111,242,190,223,12,31,246,68,91,167,126,125,240,63,23,126,105,252,158,214,137,139,87,239,239,195,69,16,92,45,194,108,
            172,216,196,20,134,66,156,76,217,131,142,10,108,48,201,53,12,191,196,244,189,206,253,59,142,250,51,189,242,229,75,127,
            195,32,221,170,118,228,111,47,8,151,161,232,165,213,121,247,72,43,64,219,222,254,82,209,202,66,175,45,205,150,203,37,
            189,223,165,119,93,95,244,203,224,197,143,23,253,114,120,113,252,163,223,58,253,249,253,253,122,29,86,139,70,62,40,245,
            140,98,108,98,10,67,33,78,166,236,65,71,5,54,152,76,105,216,134,47,77,24,118,221,195,75,113,195,103,68,149,135,
            229,162,153,57,127,42,71,199,173,116,203,1,109,157,182,119,171,219,101,51,247,31,168,86,249,139,37,221,182,11,43,197,
            79,195,243,223,47,144,223,191,56,254,207,199,47,46,142,47,254,249,253,122,253,120,255,211,197,85,47,195,119,112,135,9,
            89,177,137,41,12,133,56,153,178,7,29,21,216,96,50,177,193,191,239,189,255,136,52,190,79,160,95,30,71,71,229,1,
            125,250,47,237,155,233,165,85,90,26,181,191,161,214,223,12,85,207,253,61,6,117,211,220,249,55,41,245,118,247,23,47,
            142,255,147,95,17,46,252,223,180,141,58,190,248,249,126,77,187,115,58,225,27,142,162,96,11,21,102,99,197,38,166,48,
            20,226,100,202,30,116,84,96,131,201,196,134,143,135,195,119,20,14,111,182,30,246,28,126,91,69,223,102,241,156,174,134,
            211,159,243,170,172,252,11,22,155,15,237,237,242,182,169,252,169,159,11,239,145,220,250,117,108,253,238,194,31,61,253,151,
            87,23,116,16,75,139,225,226,213,95,215,235,245,227,250,253,159,104,77,185,120,223,15,187,136,63,42,43,54,49,133,161,
            16,39,83,246,160,163,2,27,76,242,13,254,202,221,99,227,63,188,105,183,86,244,171,197,240,89,4,239,234,83,218,101,
            251,63,116,224,234,223,191,74,75,226,246,182,238,111,108,246,183,78,181,159,186,97,87,241,211,177,223,42,189,90,255,245,
            194,111,169,46,94,253,236,47,9,222,255,116,65,11,233,197,241,85,255,137,68,112,12,21,166,100,197,38,166,48,20,226,
            164,202,89,7,20,168,49,57,1,152,14,158,245,227,227,165,223,73,15,27,38,127,101,112,248,198,199,162,44,187,226,160,
            160,39,156,246,23,244,241,18,237,99,215,47,137,235,85,77,247,13,210,225,211,166,189,109,111,253,237,4,95,30,215,127,
            58,126,65,199,177,23,255,103,253,158,174,64,253,153,118,17,126,39,241,123,90,91,142,95,92,252,185,63,132,26,118,22,
            97,26,56,183,16,103,9,13,170,17,11,146,84,57,235,128,2,53,38,39,0,211,97,96,253,120,69,159,250,183,251,180,
            154,151,71,37,29,83,209,86,171,236,158,185,215,117,73,47,77,208,242,240,151,160,104,73,252,178,186,253,165,166,111,43,
            172,93,213,44,151,203,229,47,77,191,86,172,95,245,187,136,227,159,182,143,235,127,250,153,22,196,227,227,227,251,127,60,
            166,85,196,31,224,94,12,199,80,233,185,133,56,11,59,96,35,22,36,169,114,214,1,5,106,76,78,0,166,3,179,254,
            216,188,172,233,251,132,135,63,37,125,206,59,125,31,170,187,186,121,94,20,244,178,4,125,130,96,85,86,116,97,182,189,
            94,253,178,188,94,54,116,123,32,221,222,60,59,57,59,169,126,233,111,178,89,223,255,227,49,157,203,189,184,184,88,211,
            23,41,209,126,124,125,255,39,191,219,232,207,251,46,46,134,51,110,57,23,156,91,136,179,132,6,213,136,5,73,170,156,
            117,64,129,26,147,19,128,233,32,25,191,208,240,116,214,127,246,191,255,199,47,142,195,69,142,117,191,147,240,127,232,24,
            247,79,253,138,210,111,160,184,15,231,22,226,44,236,128,141,88,144,164,202,89,7,20,96,108,206,94,9,56,241,36,134,
            221,51,196,3,235,199,171,225,36,251,248,85,255,170,197,187,139,139,254,210,172,223,108,189,184,56,254,233,235,238,43,148,
            83,156,84,57,235,128,130,16,235,4,96,58,140,137,174,19,209,100,154,245,95,195,101,143,139,191,174,239,31,223,255,228,
            163,227,23,191,167,83,239,11,90,26,239,250,101,119,45,231,130,115,11,113,150,208,192,152,5,65,170,156,117,64,65,136,
            117,2,48,29,34,159,17,254,91,217,62,222,211,114,160,253,194,241,43,58,128,61,246,199,182,47,142,95,245,155,167,221,
            94,219,191,196,109,207,45,196,89,216,1,27,177,32,73,149,179,14,40,80,99,114,2,48,29,254,141,241,119,17,60,62,
            254,116,124,225,79,44,142,143,95,209,201,245,139,240,170,197,171,227,31,143,127,124,113,49,108,159,30,253,215,186,217,115,
            227,217,231,96,7,108,196,130,36,83,246,40,39,246,6,76,1,23,166,18,182,246,254,73,122,194,70,137,224,182,245,253,
            197,112,57,150,126,232,242,56,189,110,228,15,101,253,241,211,112,178,61,128,115,159,62,121,108,100,80,24,103,138,76,89,
            138,113,70,152,117,182,250,74,192,151,129,140,241,165,159,46,104,15,253,123,90,11,194,18,161,5,209,175,27,63,201,175,
            175,80,179,23,197,20,216,200,160,48,206,20,153,114,148,3,237,48,235,220,57,25,127,172,227,217,221,91,185,141,174,30,
            254,154,223,58,92,50,145,247,0,246,220,63,174,105,19,213,175,23,254,175,126,47,254,234,248,197,143,199,175,134,83,109,
            6,231,62,125,242,216,200,160,48,206,20,153,114,148,3,237,48,235,220,185,47,225,10,29,223,100,140,132,171,169,244,97,
            17,176,24,198,225,253,159,233,37,11,127,249,195,255,9,47,32,189,56,190,82,203,119,52,111,15,214,77,176,145,65,97,
            156,41,50,101,41,198,25,161,59,176,176,47,203,193,64,126,30,233,104,123,20,22,197,114,92,239,241,42,218,246,208,26,
            70,235,197,143,199,47,254,83,191,101,242,187,11,186,19,231,226,167,97,227,180,230,69,55,154,118,207,216,51,9,54,6,
            80,103,48,69,167,44,229,72,59,204,58,119,78,37,60,217,151,131,65,191,86,68,118,9,253,1,104,191,86,120,188,102,
            244,155,62,188,102,177,190,255,169,95,4,254,117,163,227,87,254,32,234,248,226,189,90,39,244,236,177,110,130,141,12,10,
            227,76,145,41,75,49,206,8,179,206,86,251,18,214,138,176,129,162,151,31,196,118,40,172,21,215,20,132,221,47,41,194,
            115,92,15,255,95,251,51,187,99,191,117,242,235,198,197,197,171,127,238,27,6,233,176,160,71,211,238,25,12,242,96,99,
            0,117,6,83,116,202,83,12,52,194,172,179,85,142,245,102,248,241,223,12,255,133,247,21,114,3,69,207,219,189,255,217,
            173,21,125,248,241,177,123,236,30,251,239,149,255,178,253,184,253,248,232,250,101,177,190,167,51,138,171,63,191,242,55,106,
            30,95,92,208,189,129,187,181,103,251,133,111,14,28,77,187,39,20,178,96,35,131,194,56,83,100,232,172,200,10,81,192,
            214,136,108,219,161,118,219,40,176,241,75,113,251,248,254,226,226,191,252,117,189,190,127,255,63,127,250,231,159,254,250,238,
            61,189,138,199,208,18,87,206,187,178,1,10,173,56,239,228,153,34,99,75,139,172,16,5,108,141,208,233,110,140,223,176,
            40,94,247,13,239,46,252,29,178,253,243,79,47,29,241,118,110,251,88,147,174,27,22,13,55,134,186,9,10,173,56,239,
            228,153,34,99,75,139,172,16,5,108,141,200,182,29,106,3,133,2,155,161,117,61,218,28,141,143,116,215,143,93,233,117,
            33,193,141,172,53,80,66,43,206,58,245,76,145,177,165,69,86,136,2,182,70,250,175,109,214,168,181,2,239,31,179,57,
            133,78,60,102,106,134,53,49,236,44,184,17,116,26,20,90,113,222,201,51,69,198,150,22,89,33,10,216,26,145,109,59,
            212,151,1,163,32,65,234,110,253,47,195,187,191,70,31,70,196,125,32,85,40,161,21,103,157,122,166,200,216,210,34,43,
            68,1,91,35,214,151,73,168,181,2,5,9,124,47,156,146,244,71,177,221,117,56,0,123,202,90,161,132,86,156,117,234,
            153,34,99,75,139,172,16,5,108,141,200,182,29,213,84,97,12,127,206,129,172,239,22,149,216,28,134,189,7,39,160,65,
            161,132,86,156,117,234,153,34,99,75,139,172,16,5,108,141,156,202,62,70,173,21,103,168,72,81,223,143,94,115,29,158,
            242,13,138,250,203,137,251,60,129,40,180,226,188,147,39,37,203,58,89,2,158,131,37,80,12,58,71,39,104,116,170,6,
            253,250,8,138,206,229,232,170,6,238,190,221,227,230,241,227,246,75,111,179,222,60,210,219,226,187,225,37,36,250,111,183,
            161,162,243,64,114,25,206,7,59,255,181,60,227,185,130,179,253,12,231,226,137,164,244,89,75,83,16,10,166,0,65,161,
            126,134,13,70,27,251,30,20,76,134,135,12,9,233,171,243,220,137,9,140,39,146,210,103,45,77,65,40,152,2,4,133,
            178,61,225,128,58,83,152,69,25,72,223,200,220,48,97,197,19,73,233,179,150,166,32,20,76,1,130,66,217,158,112,64,
            157,41,204,162,28,132,109,36,175,58,173,120,34,41,125,214,210,20,132,130,41,64,80,40,219,19,14,168,51,133,89,148,
            131,176,141,228,85,167,21,79,36,165,207,90,90,130,144,103,80,160,64,161,108,79,56,160,206,20,102,81,6,210,55,50,
            55,76,88,241,68,82,250,172,165,37,8,121,6,5,10,20,202,246,132,3,234,76,97,22,101,32,125,35,115,195,132,21,
            79,36,165,207,90,154,130,80,48,5,8,10,101,123,194,1,117,166,48,139,50,144,190,145,185,97,194,138,39,146,210,243,
            152,218,154,34,159,240,223,130,56,146,80,52,154,236,212,249,140,29,164,33,204,176,255,18,198,4,113,161,112,16,95,116,
            41,242,209,68,164,48,76,178,255,55,63,27,187,111,133,140,60,108,157,65,82,138,196,92,100,66,202,70,112,71,14,108,
            12,160,206,20,50,150,208,116,192,124,86,168,234,152,144,178,136,48,78,170,204,22,104,101,197,10,182,202,129,141,204,100,
            97,192,18,154,14,152,207,10,149,192,138,25,179,32,72,149,131,3,131,5,140,21,108,149,3,27,3,168,51,133,140,37,
            52,29,48,159,21,170,58,38,164,44,85,16,164,202,60,22,90,89,177,130,173,114,96,35,51,89,24,176,132,166,3,230,
            179,66,37,176,98,198,44,8,82,229,224,160,156,48,33,101,35,184,35,7,54,50,147,133,1,75,104,58,96,62,43,84,
            2,43,198,188,42,72,82,101,182,64,39,76,72,217,8,238,200,129,141,1,212,153,66,198,18,154,14,152,207,10,85,29,
            19,82,22,17,198,73,149,217,2,157,48,33,101,35,184,35,7,54,6,80,103,10,25,75,104,58,168,188,74,64,158,193,
            2,198,140,89,16,164,202,193,129,193,2,198,10,182,202,129,141,1,212,153,66,198,18,154,14,152,207,10,85,29,19,82,
            22,17,198,73,149,217,2,65,129,108,139,144,21,162,0,99,198,42,132,60,179,119,193,202,219,5,36,43,12,130,56,169,
            50,58,49,40,144,109,17,178,66,20,96,204,100,11,40,192,188,89,216,223,9,201,10,121,136,40,169,50,58,49,40,144,
            109,17,178,66,20,96,204,100,11,40,192,188,89,176,242,118,1,201,10,131,32,78,170,140,78,12,10,100,91,132,172,16,
            5,24,51,217,2,10,48,111,22,172,188,93,64,178,194,32,136,147,42,163,19,131,2,217,22,33,43,68,1,198,76,182,
            128,2,204,155,5,43,111,23,144,172,48,8,226,164,202,232,196,160,64,182,69,200,10,81,128,49,99,21,66,158,217,187,
            96,229,237,2,146,21,6,65,156,84,25,157,24,20,200,182,8,89,33,10,48,102,178,5,20,96,222,44,88,121,187,128,
            100,133,65,16,39,85,70,39,6,5,178,45,66,86,136,2,140,153,108,1,5,152,55,11,251,59,33,89,33,15,17,37,
            85,206,58,97,65,41,57,1,121,5,10,3,168,203,99,117,74,223,17,40,84,96,67,22,213,40,253,44,82,50,225,31,
            115,196,130,82,114,2,242,10,20,6,80,151,199,234,148,190,35,80,168,192,134,44,170,81,250,89,164,100,194,63,102,137,
            121,140,149,3,23,16,20,6,80,151,199,234,148,190,35,80,168,192,134,44,170,81,250,89,164,100,194,63,230,136,5,165,
            228,4,228,21,40,12,160,46,143,213,41,125,71,160,80,129,13,89,84,163,244,179,72,201,132,127,204,17,11,24,231,29,
            2,40,12,160,46,143,213,41,125,71,160,80,129,13,89,84,163,244,179,72,201,132,127,204,17,11,74,201,9,200,43,80,
            24,64,93,30,171,83,250,142,64,161,2,27,178,96,163,180,51,73,233,228,0,17,75,44,96,156,119,8,160,144,65,97,
            22,171,81,218,142,64,161,2,27,178,96,163,180,51,73,233,228,0,17,75,44,40,37,39,32,175,64,97,0,117,121,172,
            78,233,59,2,133,10,108,200,130,141,210,206,100,170,238,105,200,41,70,38,101,21,100,219,20,129,69,182,65,250,38,148,
            168,99,80,96,197,182,131,39,83,254,141,240,36,172,185,88,5,217,54,69,96,145,109,144,190,123,8,25,20,96,172,19,
            81,50,229,223,72,152,131,57,23,171,32,219,166,8,44,178,13,210,119,15,33,131,2,140,117,34,74,166,252,27,9,115,
            48,231,98,21,100,219,20,129,69,182,65,250,238,33,100,80,128,177,78,68,201,148,127,35,97,14,230,92,172,130,108,155,
            34,176,200,54,72,223,61,132,12,10,48,214,137,40,153,242,111,36,204,193,156,139,85,144,109,83,4,22,217,6,233,155,
            80,162,142,65,1,198,58,17,37,83,254,141,132,57,152,115,177,10,178,109,138,192,34,219,32,125,247,16,50,40,192,88,
            39,162,100,202,191,145,48,7,115,46,86,65,182,77,17,88,100,27,164,239,30,66,6,5,86,108,59,120,82,101,182,216,
            23,52,82,147,83,160,128,173,114,72,155,17,166,208,44,32,150,16,99,83,184,39,169,62,28,98,58,150,19,230,25,20,
            176,83,14,105,51,194,20,154,5,196,18,218,9,200,239,73,170,15,135,152,12,26,169,103,90,129,2,182,202,33,109,70,
            152,66,179,128,88,66,140,77,225,158,164,250,112,136,233,88,78,152,103,80,192,78,57,164,205,8,83,104,22,16,75,104,
            39,32,191,39,169,62,28,98,58,150,19,230,25,20,176,83,14,105,51,194,20,154,5,196,18,218,9,200,239,73,170,15,
            135,152,12,26,169,103,90,129,2,182,202,33,109,70,152,66,179,128,88,66,59,1,249,61,73,245,225,16,211,177,156,48,
            207,160,128,157,114,72,155,17,166,208,44,32,150,16,99,83,184,39,169,62,28,98,58,150,19,230,25,20,176,83,14,105,
            51,194,20,154,5,196,18,218,9,200,239,73,170,15,135,72,34,222,217,62,242,160,63,195,251,234,163,131,249,156,110,236,
            45,19,88,126,225,33,133,183,248,179,158,203,194,97,60,85,97,55,8,98,14,190,60,254,55,48,42,237,67,170,111,111,
            107,53,25,140,49,175,64,33,179,183,208,138,117,2,242,12,22,164,124,132,108,75,8,211,164,250,246,182,86,147,193,24,
            243,10,20,50,123,11,173,88,39,32,207,96,65,202,71,200,182,132,48,77,170,111,111,107,53,25,140,49,175,64,33,179,
            183,208,138,117,2,242,12,22,164,124,132,108,75,8,211,164,250,246,182,86,147,193,24,243,10,20,50,123,11,173,88,39,
            32,207,96,65,202,71,200,182,132,48,77,170,111,111,107,53,25,140,49,175,64,33,179,183,208,138,117,2,242,12,22,164,
            124,132,108,75,8,211,164,250,246,182,86,147,193,24,243,10,20,50,123,11,173,88,39,32,207,96,65,202,71,200,182,132,
            48,77,170,111,111,107,53,25,140,49,175,64,33,179,183,208,138,117,2,242,12,22,164,124,132,108,75,8,211,164,250,246,
            182,86,147,193,24,243,10,20,50,123,11,173,88,39,32,207,96,65,202,71,200,182,132,48,77,170,15,173,229,128,35,148,
            0,19,33,86,160,0,99,38,20,80,128,113,30,233,179,3,117,166,48,139,105,32,253,145,84,89,57,72,227,29,170,142,
            9,118,64,80,128,49,19,10,40,192,56,143,244,217,129,58,83,152,197,52,144,254,72,170,140,14,210,119,132,18,96,34,
            196,10,20,96,204,132,2,10,48,206,35,125,118,160,206,20,102,49,13,164,63,146,42,163,131,244,29,161,4,152,8,177,
            2,5,24,51,161,128,2,140,243,72,159,29,168,51,133,89,76,3,233,143,164,202,202,65,26,239,80,117,76,176,3,130,
            2,140,153,80,64,1,198,121,164,207,14,212,153,194,44,166,129,244,71,82,101,116,144,190,35,148,0,19,33,86,160,0,
            99,38,20,80,160,18,89,132,205,8,212,153,194,44,166,129,244,71,82,101,116,144,190,35,148,0,19,33,86,160,0,99,
            38,20,80,128,113,30,233,179,3,117,166,48,139,105,32,253,145,84,25,29,164,239,8,37,192,68,136,21,40,192,152,9,
            5,20,96,156,71,250,236,64,157,41,204,98,26,72,127,36,85,70,7,233,59,66,9,84,34,7,54,96,172,8,2,6,
            5,38,79,110,12,168,78,97,151,200,103,72,233,209,50,196,10,37,80,137,28,216,128,177,34,8,24,20,152,60,185,49,
            160,58,133,93,34,159,33,165,71,203,16,43,148,64,37,114,96,67,136,77,7,22,228,132,138,39,55,6,84,167,176,75,
            228,51,164,244,104,25,98,133,18,168,68,14,108,192,88,17,4,89,161,226,233,157,3,170,83,216,37,242,25,82,122,180,
            12,177,66,9,84,34,7,54,96,172,8,2,6,5,38,79,110,12,168,70,233,167,31,196,68,82,122,180,228,177,17,37,
            80,137,28,216,128,177,34,8,178,66,197,211,59,7,84,167,176,75,228,51,164,244,104,25,98,133,18,168,68,14,108,192,
            88,17,4,12,10,76,158,220,24,80,141,210,79,63,136,137,164,244,104,201,99,35,74,160,18,57,176,1,99,69,16,100,
            133,138,167,119,14,168,78,97,151,200,103,72,233,209,50,196,10,37,96,11,44,24,117,37,96,80,32,219,34,100,133,108,
            109,8,85,65,168,35,13,106,72,140,117,34,74,170,140,14,33,86,40,1,91,96,193,168,43,1,131,2,217,22,33,43,
            100,107,67,168,10,66,29,105,80,67,98,172,19,81,82,101,116,8,177,66,9,216,2,11,70,93,9,2,74,32,186,98,
            100,133,236,109,8,85,65,168,35,13,106,72,140,117,34,74,170,140,14,33,86,40,1,91,96,193,168,43,65,64,9,68,
            87,140,172,144,189,13,161,42,8,117,164,65,13,105,197,186,83,144,42,163,3,91,34,74,192,22,88,48,234,74,16,80,
            2,209,21,35,43,100,111,67,168,10,66,29,105,80,67,98,172,19,81,82,101,116,8,177,66,9,216,2,11,70,93,9,
            2,74,32,186,98,100,133,236,109,8,85,65,168,35,13,106,72,140,117,34,74,170,140,14,33,86,40,1,91,96,193,168,
            43,65,64,9,68,87,140,172,144,189,13,161,42,8,117,164,65,13,137,177,78,68,73,149,209,33,196,10,37,96,11,44,
            24,117,37,96,80,32,219,34,100,133,108,109,9,177,32,229,123,52,152,130,56,169,242,52,135,17,161,65,77,202,68,246,
            71,134,196,4,119,2,220,128,152,2,217,31,17,4,178,66,20,4,148,64,116,41,82,229,105,14,35,212,36,56,97,33,
            251,245,144,74,200,9,128,29,16,83,32,251,35,130,64,86,136,130,128,18,136,46,69,170,60,205,97,132,154,4,39,44,
            100,127,100,72,76,112,39,194,29,128,89,151,237,17,65,32,43,68,65,64,9,68,151,34,85,158,230,48,66,77,130,19,
            22,178,95,15,169,132,156,64,216,2,48,235,178,61,34,8,100,133,40,8,40,129,232,82,164,202,211,28,70,168,73,112,
            194,66,246,71,134,196,4,119,34,220,1,152,117,217,30,17,4,178,66,20,4,148,64,116,41,82,229,105,14,35,212,36,
            56,97,33,251,245,144,74,200,9,132,45,0,179,46,219,35,130,64,86,136,130,128,18,136,46,69,170,60,205,97,132,154,
            4,39,44,100,127,100,72,76,112,39,192,13,136,41,144,253,17,65,32,43,68,65,64,9,68,151,34,85,158,230,48,66,
            77,130,19,22,178,95,15,169,132,156,0,216,1,49,5,178,63,34,8,100,133,40,8,168,186,108,67,82,229,157,5,255,
            203,68,74,208,105,247,54,122,128,235,33,166,12,191,227,222,11,252,155,243,209,74,62,60,81,54,24,73,80,24,60,145,
            190,8,22,253,215,207,239,146,163,127,143,76,188,231,200,9,19,49,82,245,209,120,123,130,78,1,212,233,69,97,196,10,
            83,16,10,8,234,108,135,128,236,215,115,83,200,246,189,73,245,227,80,211,65,167,0,234,212,195,179,98,133,41,8,5,
            4,117,182,67,64,246,235,185,41,100,251,222,164,250,113,168,233,160,83,0,117,234,225,89,177,194,20,132,2,130,58,219,
            33,32,251,245,220,20,178,125,111,82,253,56,212,116,208,41,128,58,245,240,172,88,97,10,66,1,65,157,237,16,144,253,
            122,110,10,217,190,55,169,126,28,106,58,232,20,64,157,122,120,86,172,48,5,161,128,160,206,118,8,200,126,61,55,133,
            108,223,155,84,63,14,53,25,52,98,80,168,30,158,21,43,76,65,40,32,168,179,29,2,178,95,207,77,33,219,247,38,
            213,143,67,77,6,141,24,20,170,135,103,197,10,83,16,10,8,234,108,135,128,236,215,115,83,200,246,189,73,245,227,80,
            147,65,35,6,133,234,225,89,177,194,20,132,2,130,58,219,33,32,251,245,220,20,178,125,111,126,107,255,119,254,205,248,
            190,40,190,25,190,47,138,111,134,239,139,226,155,225,251,162,248,102,248,190,40,190,25,190,47,138,111,134,239,139,226,155,
            225,251,162,248,102,248,190,40,190,25,190,47,138,111,134,239,139,226,155,225,251,162,248,102,248,190,40,190,25,190,47,138,
            111,134,239,139,226,155,225,251,162,248,102,248,190,40,190,25,190,47,138,111,134,239,139,226,155,225,251,162,248,102,248,190,
            40,190,25,190,47,138,111,134,239,139,226,155,225,251,162,248,102,248,190,40,190,25,254,47,115,153,27,35,52,105,5,120,
            0,0,0,0,73,69,78,68,174,66,96,130 };
    }

    // ============================ 收款码小窗口 ============================
    // 一张图 + 一句Lang.T("请作者喝杯咖啡", "Buy the author a coffee")。Esc、点窗口外面、标题栏 × 都能关掉。
    //
    // ⚠️ DPI：这个窗口原来自己算了一遍缩放系数（构造函数里的本地 k + 字段 _k），只把"间距"乘了系数，
    // 行距还是写死的数字 —— 150% 下 14pt 标题的真实高度是 40px 上下，写死 38px 的行距就跟副标题叠在一起
    // （用户报的"显示不全"）。现在统一用 13-Ui.cs 那一套（和 75-SettingsForm / 80-Dialogs 完全一样，
    // 别再在本文件里发明第二套换算）：
    //   · 坐标 / 边距 / 行距 / 图的上下留白一律过 Ui.S() 乘 DPI 系数；
    //   · **字体磅值不乘** —— GDI+ 已经按 DPI 渲染过一遍，再乘就是双倍放大；
    //   · 行高不写死：用 Label 自己报的 PreferredSize.Height（AutoSize 之后它就知道自己多高）+ 乘过 K 的空隙，
    //     换字体、换缩放都不会叠字；
    //   · 二维码本身是**图的像素**，1:1 显示、不乘 K（乘了就是拿 1.5 倍插值糊一遍，反而不清楚）；
    //   · ClientSize 放在**最后**按内容算一次：内容多高窗口就多高，绝不多切一个像素。
    class RewardForm : Form
    {
        bool _activated;          // 已经拿到过焦点：之后失去焦点才算"点了外面"
        PictureBox _pic;

        public RewardForm()
        {
            Text = AppInfo.Name + Lang.T(" 打赏", " Tip");
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            // 边距 / 起始行高按 DPI 走。系数统一用 Ui.K（13-Ui.cs），本文件不再自己算一遍 ——
            // 之前那份本地 k 就是"第二套换算"的开头，行距忘了乘，于是 150% 下标题跟副标题叠字。
            int pad = Ui.S(24), y = Ui.S(18);

            // 文字最多能占多宽（已乘 K）= 屏幕工作区宽 - 左右边距。
            // 这**只**是"别让窗口长出屏幕"的上限：正常屏幕上标题 / 副标题 / 提示都是一行放得下的，
            // 走不到折行那一支，版面看起来跟以前一样；只有极端 DPI / 小屏才兜底 ——
            // 宁可文字折行，也不能让窗口比屏幕还宽（比屏幕宽就等于右边那截根本看不见）。
            int maxTextW;
            try { maxTextW = Math.Max(Ui.S(200), Screen.PrimaryScreen.WorkingArea.Width - pad * 2); }
            catch { maxTextW = Ui.S(420); }

            Image img = Reward.Get();

            // 图太大就等比缩到工作区的 84%（小屏幕 / 低分辨率下窗口不会长出屏幕）
            // 380 / 140 是"图读不出来时那个白框"的占位尺寸：和真二维码一样属**图片像素**这一路，不乘 K ——
            // 真图是按原始像素 1:1 贴的，两条路得用同一把尺子；横向由下面 max(图宽, 文字宽) 兜着，不会裁字
            int iw = img != null ? img.Width : 380;
            int ih = img != null ? img.Height : 140;
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                float sk = Math.Min(1f, Math.Min(wa.Width * 0.84f / iw, wa.Height * 0.84f / ih));
                if (sk < 0.999f)
                {
                    iw = Math.Max(60, (int)Math.Round(iw * sk));
                    ih = Math.Max(60, (int)Math.Round(ih * sk));
                }
            }
            catch { }

            Label head = new Label();
            head.Text = Lang.T("请作者喝杯咖啡", "Buy the author a coffee");
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.Location = new Point(pad, y);
            Ui.Wrap(head, maxTextW);          // AutoSize + MaximumSize：自己折行、自己报宽高，不给它写死格子
            Controls.Add(head);
            // 行高不再写死（原来的 38*k 是"猜"标题有多高，36 那一版就猜少了、标题跟副标题叠在一起）：
            // 标题自己报的 PreferredSize.Height + 乘过 K 的空隙 —— 换字体、换缩放都不会叠字，
            // 这比"把高度算准"可靠得多（80-Dialogs 里那套路数一样）
            y += head.PreferredSize.Height + Ui.S(13);

            Label sub = new Label();
            sub.Text = img != null ? Lang.T("扫码打赏，随心意就好 —— 不打赏也完全不影响使用。", "Scan to tip - entirely optional; not tipping changes nothing.") : Lang.T("收款码没读出来（图片数据坏了），重装一次应该就好。", "The payment QR could not be read (corrupt image data); reinstalling should fix it.");
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.Location = new Point(pad + Ui.S(2), y);   // 副标题相对标题缩进 2px（跟标题左边对齐得更自然）
            Ui.Wrap(sub, maxTextW - Ui.S(2));
            Controls.Add(sub);
            y += sub.PreferredSize.Height + Ui.S(12);

            // 图 1:1 显示（Zoom 装在同尺寸的框里 = 不缩放）：二维码一个像素都不重采样，才最清楚
            _pic = new PictureBox();
            _pic.Image = img;
            _pic.SizeMode = PictureBoxSizeMode.Zoom;
            _pic.BackColor = Color.White;
            // iw / ih 是**图的像素尺寸**，不乘 K：二维码必须一个像素都不重采样才清楚，
            // 乘了就是拿 1.5 倍的插值糊一遍。要跟着 DPI 缩的只有图周围那些间距。
            _pic.Size = new Size(iw, ih);
            _pic.Location = new Point(pad, y);
            Controls.Add(_pic);
            y += ih + Ui.S(10);

            Label hint = new Label();
            hint.Text = Lang.T("按 Esc、点窗口外面，或点右上角 × 关掉", "Press Esc, click outside, or use the × in the top-right corner to close");
            hint.ForeColor = Color.FromArgb(158, 162, 172);
            hint.Location = new Point(pad, y);
            Ui.Wrap(hint, maxTextW);
            Controls.Add(hint);
            y += hint.PreferredSize.Height + Ui.S(5);

            // 宽度取"图"和"三行文字"里最宽的那个：内嵌图换成窄的二维码之后比副标题还窄，
            // 只按图宽开窗会把副标题右边切掉（和设置窗口那种"字显示不全"是同一类坑）。
            // 这里也是**最后**一次定尺寸：文字都排完了、图也摆好了，窗口跟着内容长，
            // 而不是把内容硬塞进一个写死的格子 —— 底部那点留白同样过 K。
            int textW = Math.Max(head.PreferredWidth, Math.Max(sub.PreferredWidth, hint.PreferredWidth));
            ClientSize = new Size(Math.Max(iw, textW) + pad * 2, y + Ui.S(8));
            CancelButton = null;
        }

        // 二维码外面套一圈微信绿（图本身是白底，套一圈才不像"贴了张白纸"）—— 纯绘制，不占体积
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_pic == null) return;
            Rectangle r = _pic.Bounds;
            int grow = Ui.S(7);                  // 绿边的厚度也按 DPI 走（要缩的只有这圈装饰，图本身还是 1:1）
            r.Inflate(grow, grow);
            try
            {
                using (GraphicsPath p = Gfx.Round(r, Ui.S(10)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(7, 193, 96)))
                    e.Graphics.FillPath(b, p);
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _activated = true;
        }

        // 点了窗口外面（别的窗口拿到焦点）= 关掉。没拿到过焦点的那次不算。
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_activated) Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _pic != null) { _pic.Image = null; _pic = null; }   // 图是公共缓存，这里不能 Dispose
            base.Dispose(disposing);
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
        public static string CarryHotkeyName = "Ctrl+Alt+C";   // 实际注册成功的传递热键（显示在托盘菜单上）
        ToolStripMenuItem _carryItem;               // 托盘里的「传递模式」项，拉开菜单时更新它的文案
        HotkeyForm _hotkey;
        HotkeyForm _carryKey;      // 传递模式的全局热键（轮盘是「不激活」窗口，收不到键盘，只能靠热键）
        WheelForm _wheel;
        // 贴在屏幕上的那些图钉（中键点缩略图产生），退出时一起收掉
        readonly System.Collections.Generic.List<PinForm> _pins = new System.Collections.Generic.List<PinForm>();

        public AppCtx()
        {
            _settings = Settings.Load();
            Ocr.Engine = _settings.OcrEngine;   // 取字引擎（auto/system/native，取字框里可改）
            Usage.On = _settings.UsageLog;      // 本地使用统计（默认关）
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
            _tray.Text = Lang.T("SnapWheel 快照轮环", "SnapWheel");
            _tray.Visible = true;
            Err.Notify = delegate(string msg)             // 出问题时托盘冒个泡，程序继续跑
            {
                if (!_settings.ShowBalloon) return;        // 设置里可以关掉右下角通知
                try { _tray.ShowBalloonTip(4000, Lang.T("SnapWheel 快照轮环遇到一个问题（已记录）", "SnapWheel hit a problem (logged)"), msg, ToolTipIcon.Warning); }
                catch { }
            };
            Lang.Init(string.IsNullOrEmpty(_settings.UiLanguage) ? Lang.Guess() : _settings.UiLanguage);   // 界面语言：没选过就按系统语言，切换后重启生效

            ContextMenuStrip menu = new ContextMenuStrip();

            // 菜单最上面放一行「版本 + 这个 exe 的编译时间」，灰色不可点。
            // 目的很直接：一眼确认"现在跑的到底是哪一版"。
            // 之前反复出现"我改了但你看不出变化"，双方各说各话 —— 有这一行就不会了。
            ToolStripMenuItem verItem = new ToolStripMenuItem(
                AppInfo.Name + "  v" + AppInfo.Version + "   ·   " + BuiltAt());
            verItem.Enabled = false;
            menu.Items.Add(verItem);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Lang.T("截图", "Screenshot"), null, new EventHandler(OnHotkey));
            menu.Items.Add(Lang.T("导入图片…", "Import images…"), null, new EventHandler(OnImport));
            menu.Items.Add(Lang.T("新手引导", "Getting started"), null, new EventHandler(OnGuide));
            menu.Items.Add(Lang.T("重播开启动画", "Replay startup animation"), null, new EventHandler(delegate(object o, EventArgs e) { _wheel.StartIntro(); }));
            if (Elev.Is)
                menu.Items.Add(Lang.T("管理员模式说明…（拖拽为什么不动）", "Admin mode… (why dragging does not work)"), null, new EventHandler(OnAdminHelp));
            menu.Items.Add(Lang.T("显示/隐藏轮盘", "Show / hide the ring"), null, new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); }));
            menu.Items.Add(Lang.T("关掉所有贴图", "Close all pinned images"), null, new EventHandler(delegate(object o, EventArgs e) { CloseAllPins(); }));
            menu.Items.Add(Lang.T("取字：识别剪贴板里的图", "OCR the clipboard image"), null, new EventHandler(OnOcrClipboard));
            menu.Items.Add(Lang.T("撤销上一次删除", "Undo last delete"), null, new EventHandler(OnUndoDelete));
            menu.Items.Add(Lang.T("管理 Wheel…", "Manage wheels…"), null, new EventHandler(OnWheels));
            menu.Items.Add(Lang.T("反馈 / 报告问题…", "Feedback / report a problem…"), null, new EventHandler(OnFeedback));
            // 诊断模式：轮盘上每个元素都标出名字。用户报"某个地方不对"时，截一张图就够了，
            // 不用再描述"那个小字"、"那个圆点" —— 这个项目为此来回过三次。
            ToolStripMenuItem diagItem = new ToolStripMenuItem(Lang.T("诊断模式（显示元素名）", "Diagnostics (name every element)"));
            diagItem.CheckOnClick = true;
            diagItem.Checked = _settings.DiagMode;
            diagItem.Click += new EventHandler(delegate(object o, EventArgs e)
            {
                _settings.DiagMode = diagItem.Checked;
                _settings.Save();
                try { _wheel.Render(); } catch { }
            });
            menu.Items.Add(diagItem);
            // 本地使用统计：默认关。打开后只在本机记"某件事发生了一次"，不记内容、不联网。
            // 存在的理由：这个项目几个"往哪走"的方向都是靠想定的，然后被真实数据否掉。
            ToolStripMenuItem usageItem = new ToolStripMenuItem(Lang.T("记录本地使用统计（只在本机）", "Log local usage stats (this machine only)"));
            usageItem.CheckOnClick = true;
            usageItem.Checked = _settings.UsageLog;
            usageItem.Click += new EventHandler(delegate(object o, EventArgs e)
            {
                _settings.UsageLog = usageItem.Checked;
                _settings.Save();
                Usage.On = usageItem.Checked;
                if (usageItem.Checked) Usage.Ev("Usage.On", "用户在托盘里打开了统计");
                try { _wheel.ShowToast(Lang.T("统计已打开：只记「哪件事发生了一次」，不记内容、不联网",
                                              "Stats on: only *what happened*, never content, never uploaded")); } catch { }
            });
            menu.Items.Add(usageItem);
            menu.Items.Add(Lang.T("打开统计文件…", "Open the stats file…"), null, new EventHandler(delegate(object o, EventArgs e)
            {
                try { System.Diagnostics.Process.Start(Usage.Path); }
                catch { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + Usage.Path + "\""); } catch { } }
            }));
            menu.Items.Add(Lang.T("设置…", "Settings…"), null, new EventHandler(OnSettings));
            _carryItem = new ToolStripMenuItem(Lang.T("传递模式（键盘搬图）", "Carry mode (keyboard)"));
            _carryItem.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                try { StartCarry(); } catch (Exception ex) { Err.Log("Carry", ex); }
            });
            menu.Items.Add(_carryItem);
            // 每次拉开菜单时把"当前真正生效的热键"写上去：热键可能因为被别的程序占用
            // 而自动换成了备选，而用户除了这里没有别的线索（RegisterHotKey 失败不报错）。
            menu.Opening += new System.ComponentModel.CancelEventHandler(delegate(object o, System.ComponentModel.CancelEventArgs e2)
            {
                try
                {
                    string b = Lang.T("传递模式（键盘搬图）", "Carry mode (keyboard)");
                    _carryItem.Text = CarryHotkeyName.Length > 0
                        ? b + "   [" + CarryHotkeyName + "]"
                        : b + Lang.T("   [热键被占用，用本菜单]", "   [hotkey taken, use this menu]");
                }
                catch { }
            });
            menu.Items.Add(Lang.T("检查更新", "Check for updates"), null, new EventHandler(delegate(object o, EventArgs e2)             {                 try { CheckUpdate(true); } catch { }             }));             menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Lang.T("打开项目主页", "Open project page"), null, new EventHandler(delegate(object o, EventArgs e) {
                try { System.Diagnostics.Process.Start("https://github.com/" + AppInfo.Repo); } catch { }
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Lang.T("退出", "Exit"), null, new EventHandler(delegate(object o, EventArgs e) { Quit(); }));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += new EventHandler(delegate(object o, EventArgs e) { _wheel.ToggleWheel(); });

            RegisterHotkeyAndNotify();
            RegisterCarryHotkey();

            // 启动后到后台检查有没有新版本（不挡启动；设置里可以关）
            if (_settings.CheckUpdate)
            {
                System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate() { CheckUpdate(false); }));
                th.IsBackground = true;
                th.Start();
            }

            if (Elev.Is && _settings.ShowBalloon)
                try
                {
                    _tray.ShowBalloonTip(6000, Lang.T("SnapWheel 快照轮环以管理员身份运行", "SnapWheel is running as administrator"),
                        Lang.T("Windows 会拦掉管理员进程和桌面/资源管理器之间的拖拽。想在轮盘上拖进拖出图片，请用普通权限运行（托盘右键 → 管理员模式说明）。", "Windows blocks dragging between an elevated process and the desktop / Explorer. To drag images in and out of the ring, run it with normal permissions (tray menu -> admin mode)."),
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
                _wheel.ShowToast(Lang.T("新功能：缩略图上按鼠标中键 = 把图钉在屏幕上", "New: middle-click a thumbnail to pin that image on screen"));
            }
            else if (Elev.Is)
                _wheel.ShowToast(Lang.T("管理员模式：拖拽会被 Windows 拦（托盘右键看说明）", "Administrator mode: Windows blocks dragging (see the tray menu)"));

            // 第一次打开、或者换到没见过的版本：都自动弹一次引导（"看过就不再弹"只对同一版本成立）。
            // 需要自己去托盘里找的引导留不住人，所以升级后也主动亮一次。
            bool firstEver = !_settings.IntroSeen;
            bool newVersion = (_settings.GuideSeenVersion != AppInfo.Version);
            if (firstEver || newVersion)
            {
                // 必须在改写配置**之前**把它读出来：下面马上就要把 GuideSeenVersion 刷成当前版本，
                // 之后再读只会读到新值，于是"哪些是这次新加的"就永远算不出来（引导会一条【新】都不标）。
                //
                // 全新安装时传当前版本当"看过的版本"：对第一次来的人，**每一条都是新的**，
                // 标满【新】等于没标，反而把"这次更新了什么"这个信息的价值毁掉。
                string prevVer = firstEver ? AppInfo.Version : _settings.GuideSeenVersion;
                _settings.IntroSeen = true;
                _settings.GuideSeenVersion = AppInfo.Version;
                _settings.Save();
                Timer g = new Timer();
                g.Interval = 900;
                g.Tick += new EventHandler(delegate(object o, EventArgs e2)
                {
                    g.Stop(); g.Dispose();
                    try { GuideForm gf = new GuideForm(firstEver, prevVer); gf.ShowDialog(); } catch { }
                });
                g.Start();
            }
        }

        // 托盘入口：一键提 issue（预填环境信息）+ 复制诊断信息（0.6.0）
        void OnFeedback(object sender, EventArgs e)
        {
            FeedbackForm ff = new FeedbackForm();
            try { ff.ShowDialog(_wheel); } catch { }
            try { ff.Dispose(); } catch { }
        }

        // 弹框/截图期间让轮盘退到后面。要点两个：
        //   ① 用**计数**：多处嵌套（截图时又打开设置）时，谁也不会把对方的状态冲掉；
        //   ② 必须**真的把 TopMost 关掉** —— 只抑制"周期置顶"是不够的：轮盘本来就已经在顶层，
        //      不关掉它照样压在设置窗口/截图浮层上面（用户报的"设置跑到下面、以为没打开"）。
        int _noTop = 0;
        bool _noTopWasOn = false;

        void PushNoTopMost()
        {
            if (_noTop == 0) { try { _noTopWasOn = _wheel.TopMost; _wheel.TopMost = false; } catch { } }
            _noTop++;
        }

        void PopNoTopMost()
        {
            _noTop = Math.Max(0, _noTop - 1);
            if (_noTop == 0) { try { _wheel.TopMost = _noTopWasOn; } catch { } }
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
                Usage.Ev("Pin", "钉到屏幕上");
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
                    MessageBox.Show("没有可撤销的删除。\n\n（只记得住本次运行中最近 " + Undo.MaxBatches + Lang.T(" 次删除，退出程序就清空 —— 需要长期保存的图请拖到文件夹里存好。）", " deletions; cleared when the app exits - drag images to a folder to keep them.)"),
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string desc = Undo.LastDesc;
                string wheel = "";
                int n = Undo.UndoLast(_wheels, out wheel);
                Usage.Ev("UndoDelete", n.ToString());
                if (n <= 0)
                {
                    MessageBox.Show(Lang.T("没能放回去（原轮盘可能已经被删掉了）。", "Could not put it back (the original wheel may have been deleted)."), AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _wheel.RefreshWheel();
                _wheel.ShowToast(Lang.T("已放回 ", "Restored ") + n + Lang.T(" 张到「", " item(s) to \"") + wheel + "」" + (string.IsNullOrEmpty(desc) ? "" : "（" + desc + "）"));
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
                    MessageBox.Show(Lang.T("剪贴板里没有图片。先复制一张图（或截图），再来点这里。", "No image in the clipboard. Copy one (or take a screenshot) and try again."),
                        AppInfo.Name + " 取字", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
                return;
            }
            string err = null, txt = null;
            Cursor prev = null;
            try { prev = Cursor.Current; Cursor.Current = Cursors.WaitCursor; } catch { }
            try
            {
                try { txt = Ocr.Recognize(img, out err); }
                catch (Exception ex) { err = ex.Message; }
                finally { try { Cursor.Current = prev; } catch { } }

                if (txt == null)
                {
                    try { MessageBox.Show(err ?? "识别失败了", AppInfo.Name + " 取字", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
                    return;
                }
                // 这张图要活到结果框关掉为止：框里换引擎时会拿它重新认一遍。
                // （原来是在 finally 里立刻 Dispose —— 那样"重新识别"根本无从谈起）
                Bitmap keep = img;
                try
                {
                    using (OcrForm of = new OcrForm(txt, delegate(out string e2) { return Ocr.Recognize(keep, out e2); })) { of.ShowDialog(); }
                }
                catch (Exception ex) { Err.Log("OcrForm", ex); }
            }
            finally { try { img.Dispose(); } catch { } }
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
                    PushNoTopMost();
                    af.TopMost = true;
                    restart = (af.ShowDialog() == DialogResult.OK);
                    PopNoTopMost();
                }
            }
            catch { }

            if (!restart) return;
            if (Elev.RelaunchNormal()) Quit();
            else
            {
                try
                {
                    MessageBox.Show(Lang.T("没能自动重启。请关掉 SnapWheel，再右键 SnapWheel.exe →「以普通权限运行」。", "Could not restart automatically. Close SnapWheel, then right-click SnapWheel.exe and choose \"Run as a normal user\"."),
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            }
        }

        // 托盘「导入图片…」：不想拖的时候也能从任意位置选图加进当前 wheel
        // 检查 GitHub Releases 有没有新版本：只提示，绝不自动下载/替换
        // 检查更新。
        // manual=true：用户点了托盘菜单 —— 必须有反馈（已是最新 / 发现新版 / 下载 / 失败）。
        // manual=false：启动时的静默检查 —— 只在真有新版时提一句，其余一律不打扰。
        // 检查、下载都在后台线程，界面操作统一切回 UI 线程（Ui()）。
        // 传递模式：把轮盘上"当前这张"用键盘搬到别的窗口去。
        // 流程：取图 → 生成吸附用的小图 → 开假光标窗口 → 用户自己切屏、WASD 移动、Enter 放下。
        void StartCarry()
        {
            // 先记一条"确实被调用了"：用来区分"托盘菜单/热键根本没触发"和"触发了但后面出问题"。
            // （用户反馈"连托盘启动都不行"，但日志里又有放下的记录 —— 必须先分清是哪一种。）
            Err.Log("Carry.Start", new Exception("进入传递模式：被调用"));
            Usage.Ev("Carry.Start");
            Store st = _wheels.ActiveStore;
            int idx = _wheel.CurrentIndex;
            Err.Log("Carry.Start", new Exception("轮盘项数=" + (st == null ? -1 : st.Items.Count) + " 当前索引=" + idx));
            if (st == null || idx < 0 || idx >= st.Items.Count)
            {
                MessageBox.Show(_wheel,
                    Lang.T("轮盘上没有可以传递的图。先截一张，或者用滚轮选一张。",
                           "There is nothing on the ring to carry. Capture something, or pick one with the wheel."),
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StoreItem item = st.Items[idx];
            // 1.3.0：环上多了文字格 / 文件格，但传递模式是"模拟把一张图拖出去"，搬不了它们。
            // 这里必须说人话 —— 否则文字格会被那句"这一张图取不到内容"误导成"图坏了"。
            if (item.Image == null)
            {
                MessageBox.Show(_wheel,
                    Lang.T("传递模式现在只搬图片。这一格装的是文字或文件 —— 单击它就能复制，再去粘贴吧。",
                           "Carry mode only moves images for now. This cell holds text or a file - click it to copy, then paste."),
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Bitmap thumb = MakeCarryThumb(item.Image, 132, 99);
            if (thumb == null)
            {
                // 图拿不到（Image 已被释放、或这张本来就不是有效的图）——
                // 必须明确说出来。原来是静默 return，用户看到的就是"按了传递模式，什么都没发生"，
                // 完全不知道发生了什么（这条是用户"重新截一张图就能用了"反馈出来的）。
                MessageBox.Show(_wheel,
                    Lang.T("这一张图取不到内容，没法传递。换一张（用滚轮换），或者重新截一张。",
                           "This image has no usable content, so it cannot be carried. Pick another one (scroll) or capture again."),
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 起点：轮盘上那张缩略图大致所在的位置。模拟拖放时要从这里"按下"，
            // 目标程序才会认为图是从轮盘里拖出来的。
            // 起点：那张缩略图在屏幕上的真实位置。模拟拖放要从这里「按下」——
            // 有些程序要求按下点确实落在图上，用窗口中心当起点它们不认。
            //
            // ⚠️ 必须把矩形**和屏幕求交集**再取中心：
            // 轮盘贴在屏幕底部，窗口常有一部分在屏幕外（实测起点算出来是 Y=1199，而虚拟屏幕只有 1152 高）。
            // 直接用中心的话，SetCursorPos 会把光标夹到屏幕边缘，"按下"就落在空白处 ——
            // 鼠标看着动了，但拖放永远不会启动。取"可见部分"的中心就不会出屏幕。
            Rectangle itemRect = _wheel.ItemScreenRect(idx);
            Point origin;
            if (itemRect.Width > 2)
            {
                Rectangle vis = Rectangle.Intersect(itemRect, SystemInformation.VirtualScreen);
                origin = (vis.Width > 2 && vis.Height > 2)
                    ? new Point(vis.X + vis.Width / 2, vis.Y + vis.Height / 2)
                    : new Point(itemRect.X, itemRect.Y);
            }
            else
            {
                origin = new Point(_wheel.Left + _wheel.Width / 2, _wheel.Top + _wheel.Height / 2);   // 拿不到就退回中心
            }

            CarryForm cf = new CarryForm(thumb, origin);
            // 把轮盘句柄交给传递模式：模拟拖放前它要先把轮盘拉到前台，
            // 否则轮盘作为"不激活窗口"会把第一次模拟点击用来激活自己、应用收不到（拖放永远不启动）。
            CarryForm.WheelHandle = _wheel.Handle;

            // 传递模式里按 , . 可以换一张（不用退出去重新选）。
            // 换图要动轮盘上的选中项、重算起点和缩略图，所以放在 App 这边做，
            // 做完再把新的缩略图和起点交给 CarryForm。
            cf.SwitchRequested += new Action<int>(delegate(int delta)
            {
                try
                {
                    int cnt = st.Items.Count;
                    if (cnt <= 0) return;
                    int cur = _wheel.CurrentIndex;
                    if (cur < 0) cur = 0;
                    int nx = ((cur + delta) % cnt + cnt) % cnt;

                    _wheel.SelectIndex(nx);                       // 轮盘跟着滚过去（保留原地不动会让人困惑）

                    if (st.Items[nx].Image == null)
                    {
                        // 1.3.0：环上有文字格 / 文件格了，它们不进传递模式 —— 这句话要说清是"不是图片"，
                        // 而不是"这张图坏了"。
                        cf.SetHintText(Lang.T("这一格不是图片（传递模式只搬图片），再用 [ ] 换一张",
                                              "That cell is not an image - use Q E to pick another"));
                        return;
                    }
                    Bitmap nt = MakeCarryThumb(st.Items[nx].Image, 132, 99);
                    if (nt == null) return;

                    Rectangle ir2 = _wheel.ItemScreenRect(nx);
                    Point org2 = (ir2.Width > 2) ? new Point(ir2.X, ir2.Y) : origin;
                    cf.SetThumb(nt, org2);
                    cf.SetHintText(Lang.T("第 " + (nx + 1) + " / " + cnt + " 张　·　空格 放下　·　[ ] 换一张　·　Esc 取消",
                                          "Image " + (nx + 1) + " / " + cnt + "  ·  Space drop  ·  Q E switch  ·  Esc cancel"));
                }
                catch (Exception ex) { Err.Log("Carry.Switch", ex); }
            });
            // 传递期间不能让轮盘自动收起：用户要自己切屏过去，常常超过那 8 秒；
            // 轮盘一藏，"按下"的起点就变成空桌面，目标程序什么都不会发生。
            WheelForm.SuppressAutoHide++;
            // 用 Show() 而**不是** ShowDialog()：模态窗口会一直占着"活动窗口"的位置，
            // 用户 Alt+Tab 切到目标程序时会觉得"切不过去"（用户实测的现象）。
            // 传递模式本来也不需要模态 —— 假光标和按键全靠全局轮询，不依赖键盘焦点。
            cf.FormClosed += new FormClosedEventHandler(delegate(object o, FormClosedEventArgs e2)
            {
                WheelForm.SuppressAutoHide = Math.Max(0, WheelForm.SuppressAutoHide - 1);
                try
                {
                    if (cf.UseClipboard)
                    {
                        // 用户选的是「复制到剪贴板」：不走模拟拖放，直接把图写进剪贴板。
                        // 先登记"这张剪贴板是我们自己写的"，否则"复制即收纳"会把刚复制的图又收一遍。
                        SelfClipboard.Note(item.Image);
                        Clipboard.SetImage(item.Image);
                        SelfClipboard.NoteSequence();
                        if (_settings.ShowBalloon) _tray.ShowBalloonTip(6000,
                            Lang.T("已复制到剪贴板", "Copied to clipboard"),
                            Lang.T("切到目标窗口按 Ctrl+V 就能粘贴。", "Switch to the target window and press Ctrl+V."),
                            ToolTipIcon.Info);
                    }
                }
                catch (Exception ex) { Err.Log("Carry.Clipboard", ex); }
                try { thumb.Dispose(); } catch { }
            });
            cf.Show();
        }

        // 生成"吸附在假光标上"的小图：按比例填满目标框、居中裁切，不变形
        static Bitmap MakeCarryThumb(Bitmap src, int w, int h)
        {
            if (src == null) return null;
            try
            {
                Bitmap dst = new Bitmap(w, h);
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    float k = Math.Max((float)w / src.Width, (float)h / src.Height);
                    int sw = (int)Math.Min(src.Width, Math.Ceiling(w / k));
                    int sh = (int)Math.Min(src.Height, Math.Ceiling(h / k));
                    int sx = (src.Width - sw) / 2, sy = (src.Height - sh) / 2;
                    g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(sx, sy, sw, sh), GraphicsUnit.Pixel);
                }
                return dst;
            }
            catch { return null; }
        }

        void CheckUpdate(bool manual)
        {
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate(object st)
                {
                    Update.Found f = Update.Check();
                    if (f == null)
                    {
                        if (manual) Ui(delegate
                        {
                            MessageBox.Show(_wheel,
                                Lang.T("已经是最新版本（v", "You are up to date (v") + AppInfo.Version + Lang.T("）。", ")."),
                                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        });
                        return;
                    }

                    // 后台静默检查：只提醒，把决定权留给用户
                    if (!manual)
                    {
                        Ui(delegate { BalloonUpdate(f.Version); });
                        return;
                    }

                    // 手动检查：问要不要现在下载
                    bool go = false;
                    Ui(delegate
                    {
                        go = MessageBox.Show(_wheel,
                            Lang.T("发现新版本 v", "New version found: v") + f.Version +
                            Lang.T("（当前 v", " (current v") + AppInfo.Version + Lang.T("）。", ").") + "\\r\\n\\r\\n" +
                            Lang.T("现在下载并安装吗？程序会自动重启一次，你的轮盘和设置都不会丢。",
                                   "Download and install now? The app restarts once; your ring and settings are kept."),
                            AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    });
                    if (!go) return;

                    string dir = Update.Download(f, null);
                    if (dir == null)
                    {
                        Ui(delegate
                        {
                            MessageBox.Show(_wheel,
                                Lang.T("下载失败。可能是网络问题，也可以右键托盘 →「打开项目主页」手动下载。",
                                       "Download failed. This may be a network issue; you can also use tray > Open project page to download manually."),
                                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        });
                        return;
                    }

                    bool apply = false;
                    Ui(delegate
                    {
                        apply = MessageBox.Show(_wheel,
                            Lang.T("下载完成，现在重启并安装 v", "Downloaded. Restart and install v") + f.Version + Lang.T("？", "?"),
                            AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    });
                    if (!apply) return;

                    // 启动"更新器模式"的自己，然后把当前进程关掉 —— 由它覆盖文件并重新启动
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath,
                            "--apply-update \"" + dir + "\" " + Process.GetCurrentProcess().Id);
                        psi.UseShellExecute = false;
                        Process.Start(psi);
                    }
                    catch { }
                    Ui(delegate { Application.Exit(); });
                });
            }
            catch { }   // 没网 / 超时 / 被拦，一律静默，绝不打扰使用
        }

        // 把一段界面操作切回 UI 线程执行（后台线程不能直接碰控件）
        void Ui(MethodInvoker a)
        {
            try { _wheel.Invoke(a); } catch { }
        }

        // 有新版时的提示气泡
        void BalloonUpdate(string ver)
        {
            try
            {
                if (!_settings.ShowBalloon) return;
                string msg = Lang.T("有新版本 v", "New version available: v") + ver +
                             Lang.T("。右键托盘图标 →「检查更新」可以下载并安装（程序会重启一次，轮盘和设置都保留）。",
                                    ". Right-click the tray icon and choose \"Check for updates\" to download and install (the app restarts once; your ring and settings are kept).");
                _tray.ShowBalloonTip(9000, Lang.T("SnapWheel 快照轮环 有新版本", "SnapWheel has an update"), msg, ToolTipIcon.Info);
            }
            catch { }
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
            using (System.Windows.Forms.OpenFileDialog d = new System.Windows.Forms.OpenFileDialog())
            {
                d.Title = Lang.T("把图片加入轮盘", "Add images to the ring");
                d.Multiselect = true;
                d.Filter = ImageIO.DialogFilter();
                d.RestoreDirectory = true;
                if (d.ShowDialog() != DialogResult.OK) return;
                Usage.Ev("Import", d.FileNames.Length.ToString());
                List<string> files = ImageIO.Collect(d.FileNames, 50);
                _wheel.ShowWheel();
                _wheel.ImportFiles(files);
            }
        }

        // 传递模式的全局热键：Ctrl+Alt+C。
        // 为什么必须有它：轮盘窗口是用 SWP_NOACTIVATE 显示的（为了不抢走别的程序的焦点），
        // 所以它**收不到键盘输入** —— 想在轮盘上用键盘选图、进传递模式，只能靠全局热键。
        void RegisterCarryHotkey()
        {
            try
            {
                if (_carryKey == null)
                {
                    _carryKey = new HotkeyForm();
                    _carryKey.Hotkey += new EventHandler(delegate(object o, EventArgs e)
                    {
                    // 不要在这里直接调 StartCarry：这段代码是在 HotkeyForm.WndProc 里跑的，
                    // 而 StartCarry 最后会开一个模态窗口（ShowDialog）。在消息处理中间去开模态窗口，
                    // 等于套一层嵌套的消息循环，新窗口往往根本出不来 —— 这就是"托盘菜单能进、
                    // 热键进不去"的原因（菜单走的是普通 Click 事件，不在 WndProc 里）。
                    // 用 BeginInvoke 把它推迟到当前消息处理完之后再执行，就没有这个问题。
                    _carryKey.BeginInvoke((MethodInvoker)delegate()
                    {
                        try { StartCarry(); } catch (Exception ex) { Err.Log("Carry", ex); }
                    });
                    });
                }
                // 依次尝试几个候选热键，用第一个注册成功的。
                // 依次尝试几个候选热键，用第一个注册成功的。
                // 为什么要这样：RegisterHotKey 失败不会有任何报错（只返回 false），用户看到的就是
                // "按了没反应"。而 Ctrl+Alt+C 特别容易被占用（QQ、微信、输入法、录屏都用它）。
                uint[][] cands = new uint[][]
                {
                    new uint[] { Native.MOD_CONTROL | Native.MOD_ALT,   (uint)Keys.C },
                    new uint[] { Native.MOD_CONTROL | Native.MOD_ALT,   (uint)Keys.W },
                    new uint[] { Native.MOD_CONTROL | Native.MOD_ALT,   (uint)Keys.F9 },
                    new uint[] { Native.MOD_CONTROL | Native.MOD_SHIFT, (uint)Keys.F9 },
                    new uint[] { Native.MOD_CONTROL | Native.MOD_ALT,   (uint)Keys.F10 },
                };
                string[] names = new string[] { "Ctrl+Alt+C", "Ctrl+Alt+W", "Ctrl+Alt+F9", "Ctrl+Shift+F9", "Ctrl+Alt+F10" };

                CarryHotkeyName = "";
                for (int ci = 0; ci < cands.Length; ci++)
                {
                    if (_carryKey.Register(cands[ci][0], cands[ci][1])) { CarryHotkeyName = names[ci]; break; }
                }
                Err.Log("Carry.Register", new Exception("传递热键注册结果: [" + CarryHotkeyName + "]"));   // 必定落盘
                if (CarryHotkeyName.Length == 0)
                {
                    // 一个都没注册上：主动告诉用户去用托盘菜单，别让他对着键盘干等
                    try
                    {
                        if (_settings.ShowBalloon) _tray.ShowBalloonTip(8000,
                            Lang.T("传递模式的热键被占用了", "Carry-mode hotkey unavailable"),
                            Lang.T("Ctrl+Alt+C、Ctrl+Alt+W 等组合都被别的程序占着。请用托盘菜单里的「传递模式」。",
                                   "Ctrl+Alt+C, Ctrl+Alt+W and the other candidates are all taken. Use the tray menu item instead."),
                            ToolTipIcon.Warning);
                    }
                    catch { }
                }
            }
            catch { }
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
            string tip = ok ? (Lang.T("已就绪，热键 ", "Ready, hotkey ") + _settings.Hotkey) : Lang.T("热键注册失败，请在设置里换一个", "Hotkey registration failed - pick another one in settings");
            try
            {
                _tray.Text = Lang.T("SnapWheel 快照轮环 (", "SnapWheel (") + _settings.Hotkey + ")";
                // 热键提示只在"第一次运行"或"注册失败"时弹，平时开机不打扰
                if ((_settings.ShowBalloon && !_settings.IntroSeen) || !ok)
                    _tray.ShowBalloonTip(3000, Lang.T("SnapWheel 快照轮环", "SnapWheel"), tip, ToolTipIcon.Info);
            }
            catch { }
        }

        /// <summary>
        /// 这个 exe 是什么时候编译的（用来确认"现在跑的到底是哪一版"）。
        /// 显示在托盘菜单最上面那一行。
        /// </summary>
        static string BuiltAt()
        {
            try
            {
                return System.IO.File.GetLastWriteTime(Application.ExecutablePath).ToString("MM-dd HH:mm");
            }
            catch { return "?"; }
        }

        void OnWheels(object sender, EventArgs e)
        {
            PushNoTopMost();
            WheelsForm f = new WheelsForm(_wheels);
            f.ShowDialog();
            _wheels.ApplySettings();
            PopNoTopMost();
            _wheel.RefreshWheel();
        }

        void OnSettings(object sender, EventArgs e)
        {
            bool wasTop = _wheel.TopMost;
            PushNoTopMost();
            // 取字引擎在取字框里随时能改（改完它自己写盘）。设置窗口保存的是内存里这一份 _settings，
            // 所以存之前先把当前值同步过来 —— 否则这里一按确定，就把刚选好的引擎写回去了。
            _settings.OcrEngine = Ocr.Engine;
            SettingsForm f = new SettingsForm(_settings);
            // 用户报"设置界面偶尔会出现在下层而不是顶层"。
            // 原因：`ShowDialog()` 没传 owner，而这个进程的窗口都带 WS_EX_NOACTIVATE、
            // 切到设置前还会把轮盘的置顶临时关掉（PushNoTopMost）—— 于是对话框既没有属主、
            // 又不在置顶组里，什么时候被别的窗口盖住全看运气。
            // 设置是**用户刚点出来的模态框**，开着的时候就该在最上面，这里明确置顶。
            try { f.TopMost = true; } catch { }
            DialogResult r = f.ShowDialog();
            if (r == DialogResult.OK)
            {
                _wheels.ApplySettings();
                RegisterHotkeyAndNotify();
            RegisterCarryHotkey();
                // 界面侧收尾都在这里：以前这句里还夹着一句 HideWheel()，
                // 结果每次点设置里的确定，轮盘都当场消失（详见 WheelForm.AfterSettingsApplied）
                _wheel.AfterSettingsApplied();
            }
            else
            {
                PopNoTopMost();
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

            // 第一次用截图浮层：让工具条旁边亮一次"能标注"的提示（只亮这一次）
            if (!_settings.AnnotHintDone)
            {
                _settings.AnnotHintDone = true;
                _settings.Save();
            }
            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap shot = new Bitmap(vs.Width, vs.Height);
            try
            {
                using (Graphics g = Graphics.FromImage(shot))
                    g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
            }
            catch { shot.Dispose(); if (wasExpanded) _wheel.ExpandWheel(); return; }

            OverlayForm ov = new OverlayForm(vs, shot, _settings);
            // 浮层必须是前台：轮盘每 2 秒的周期置顶会把它压下去（用户报的"截图时页面不在最顶层"），
            // 所以先让轮盘退出置顶，截完再恢复。
            // 截图/弹框期间抑制轮盘的周期置顶（见 WheelForm.SuppressTopMost）
            PushNoTopMost();
            try { ov.ShowDialog(); }
            finally { PopNoTopMost(); }
            if (ov.WantLongShot)
            {
                // 0.6.0：在截图浮层里点了「长图」—— 带着他框的那块区域去跑滚动长截图
                Rectangle reg = ov.LongShotRegion;
                try { ov.Dispose(); } catch { }
                RunLongShot(reg, wasExpanded);
                return;
            }
            if (ov.Result != null)
            {
                Store st = _wheels.ActiveStore;
                StoreItem ni = st.Add(ov.Result);
                // 本地统计：截了多少次、带几个标注、截完环上有几张
                // （"环上几乎总是一张"这个判断就是靠这批数据，以前只能靠 Frame 日志猜采样）
                Usage.Ev("Shot", "标注=" + ov.ShapeCount());
                Usage.Ev("RingItems", st.Items.Count.ToString());
                // 关键：浮层关掉之后重抓一次背景。
                // 之前是拿着"截图浮层还在时抓的"背景去显示玻璃，所以截图完轮盘是暗的，
                // 过一会儿定时刷新才突然变亮 —— 现在这里立刻换新背景（带淡入过渡）。
                try { _wheel.RequestBackdropAsync(); } catch { }
                // 截完播拉出动画（收起态拉出来最自然；原来是展开的就直接显示）
                if (_settings.CollapseMode) _wheel.ExpandWheel(true);   // 截图流程：拉出也快一点
                else _wheel.ShowWheel();
                // MarkNew 必须放在"拉出 / 显示"**之后**：收起态那条路走的是 ExpandWheel → StartIntro，
                // 而 StartIntro 会把 _enterT0 清空、重排成"0.45 + i*0.13 秒"的错峰出场表（开机彩虹扫出用的就是它）。
                // 反过来的话，刚截这张的"现在就滑进来"会被那张错峰表覆盖 —— 要等 1 秒多才动，
                // 而且这期间视口还没跟过去（见 MarkNew），用户看到的就是"动画和位置合不上、突然闪现"。
                _wheel.MarkNew(ni);              // only the brand-new shot plays the slide-in

                // 1.0.0：在浮层里点了「贴图」——
                // **先照常进轮环**（上面这一整套：入环、统计、重抓背景、拉出动画、MarkNew 全部照跑），
                // 再额外钉一张到屏幕上。用户要的就是"框选完就能贴图，而且自动保存到轮环上"，
                // 两件事都要，所以不是二选一的分支。
                if (ov.WantPin)
                {
                    try
                    {
                        // 必须**克隆**：ov.Result 那张位图的所有权在轮环（StoreItem）手上，
                        // 直接把同一个对象交给 PinForm，关掉贴图时 Dispose 会把环上那张也弄没。
                        using (Bitmap pinImg = new Bitmap(ov.Result))
                            OnPin(new Bitmap(pinImg), ov.PinAt);
                        Usage.Ev("Pin.FromShot", "截图时直接贴图");
                    }
                    catch (Exception ex) { Err.Log("Pin.FromShot", ex); }
                }
            }
            else if (wasExpanded)
            {
                Usage.Ev("Shot.Cancel");         // 本地统计：截图被取消（高的话说明这一步有摩擦）
                _wheel.ExpandWheel(true);        // 取消了截图，也把轮盘拉回来
            }
        }

        // ==================== 滚动长截图（0.6.0） ====================
        // 托盘 / 菜单进来的入口。用户拍板的交互是"他自己滚，程序跟着无缝拼接" ——
        // 所以这里只做三件事：把轮盘让开、抓好第一屏、把 LongShotForm 摆上去。
        // 拼接本身在 58-LongShot.cs（引擎）和 59-LongShotForm.cs（提示条 + 定时抓帧）里。
        // 入口在截图浮层的工具条上（用户要求：长截图从截图页面选，而不是托盘）

        void RunLongShot(Rectangle region, bool wasExpanded)
        {
            Usage.Ev("LongShot.Start", region.Width + "x" + region.Height);
            // 轮盘在进浮层时已经让开了；这里只负责长图本身
            if (_settings.CollapseMode && _wheel.Visible)
            {
                _wheel.CollapseWheel(true);
                for (int i = 0; i < 90 && !_wheel.IsCollapsed; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(8); }
            }
            else if (_wheel.Visible) _wheel.Hide();

            LongShotForm lf = new LongShotForm(region);
            lf.ShowDialog();
            Bitmap result = lf.Result;
            try { lf.Dispose(); } catch { }



            if (result != null)
            {
                Store st = _wheels.ActiveStore;
                StoreItem ni = st.Add(result);
                try { _wheel.RequestBackdropAsync(); } catch { }
                if (_settings.CollapseMode) _wheel.ExpandWheel(true);
                else _wheel.ShowWheel();
                _wheel.MarkNew(ni);      // 和普通截图一样：刚出的这张要有Lang.T("滑进来", "slides in")的动画
            }
            else if (wasExpanded)
            {
                _wheel.ExpandWheel(true);        // 取消了长截图，也把轮盘拉回来
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
        static void Main(string[] args) 
        {
            // 更新器模式：由「下载更新」启动的第二个自己。等主进程退出后覆盖文件、再把人重新拉起来。
            // 走这条路就完全不碰界面和轮盘，做完就退出。
            //
            // 注意：下面这行在 v0.8.1 到 v0.9.3 之间被挤进了上一行的注释里，整个更新器模式是死代码：
            // 能编译、0 警告、测试全绿，但「下载并安装更新」永远不会生效。守卫见 tools/check-swallowed.ps1。
            if (Update.IsApplyMode(args)) { Update.RunApply(args); return; }

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

namespace SnapWheel
{
    // ==================== 诊断信息（0.6.0 反馈闭环） ====================
    // 用户提 issue 时最怕"说不清环境"：版本、系统、DPI、设置组合一多，作者就要来回问。
    // 这里把该问的东西一次收齐，分成两档：
    //   · Brief() —— 短，塞进 GitHub issue 的 URL 里（URL 有长度上限，日志不能放）；
    //   · Full()  —— 长，带错误日志尾部，给Lang.T("复制诊断信息", "Copy diagnostics")按钮用（用户自己找地方贴）。
    static class Diag
    {
        static string Bits() { try { return Environment.Is64BitOperatingSystem ? " 64 位" : " 32 位"; } catch { return ""; } }

        // 系统版本**必须读注册表**：这个程序没有声明"支持 Windows 10"，于是
        // Environment.OSVersion 会一直报 6.2（= Windows 8）—— 用户提 issue 时这一栏就是错的。
        // 另外 Win11 在注册表里常常仍写着 "Windows 10"，要按 build 号纠正。
        static string Os()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        string name = k.GetValue("ProductName") as string;
                        string disp = k.GetValue("DisplayVersion") as string;
                        string build = k.GetValue("CurrentBuildNumber") as string;
                        string ubr = k.GetValue("UBR") as string;
                        int b = 0; if (!string.IsNullOrEmpty(build)) int.TryParse(build, out b);
                        if (b >= 22000 && !string.IsNullOrEmpty(name) && name.IndexOf("Windows 10") >= 0)
                            name = name.Replace("Windows 10", "Windows 11");
                        if (!string.IsNullOrEmpty(name))
                            return name + (string.IsNullOrEmpty(disp) ? "" : " " + disp)
                                 + " (build " + build + (string.IsNullOrEmpty(ubr) ? "" : "." + ubr) + ")" + Bits();
                    }
                }
            }
            catch { }
            return Environment.OSVersion.VersionString + Bits();
        }

        static string Screen()
        {
            try
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                return vs.Width + "x" + vs.Height + "（虚拟屏）";
            }
            catch { return "未知"; }
        }

        static string SettingsBrief()
        {
            try
            {
                Settings s = Settings.Load();
                if (s == null) return "（读不到设置）";
                StringBuilder b = new StringBuilder();
                b.Append("风格 ").Append(s.UiStyle);
                b.Append(" / 界面缩放 ").Append(s.UiScale == 0 ? "自动" : s.UiScale + "%");
                b.Append(" / 热键 ").Append(s.Hotkey);
                b.Append(" / 收起态 ").Append(s.CollapseMode ? "开" : "关");
                b.Append(" / 玻璃定时刷新 ").Append(s.GlassRefresh ? "开" : "关");
                b.Append(" / 省电 ").Append(s.PowerSave ? "开" : "关");
                b.Append(" / 最多 ").Append(s.MaxCount).Append(" 张");
                b.Append(" / 贴角 ").Append(s.Corner);
                return b.ToString();
            }
            catch { return "（读不到设置）"; }
        }

        public static string Brief()
        {
            StringBuilder b = new StringBuilder();
            b.Append("版本：v").Append(AppInfo.Version).Append("（").Append(AppInfo.Name).Append(" ").Append(AppInfo.CnName).Append("）\r\n");
            b.Append("系统：").Append(Os()).Append("\r\n");
            double dpi = 1.0;
            try { dpi = Native.DpiScaleOf(IntPtr.Zero); } catch { }
            b.Append("DPI：").Append((int)Math.Round(dpi * 100)).Append("%\r\n");
            b.Append("屏幕：").Append(Screen()).Append("\r\n");
            b.Append("权限：").Append(Elev.Is ? "管理员" : "普通用户").Append("\r\n");
            b.Append("设置：").Append(SettingsBrief()).Append("\r\n");
            return b.ToString();
        }

        // 带日志里那台机器最近发生了什么（只取尾部若干行，别把整份日志贴出去）
        public static string Full()
        {
            StringBuilder b = new StringBuilder();
            b.Append(Brief());
            try
            {
                string f = Err.LogPath();
                if (!string.IsNullOrEmpty(f) && File.Exists(f))
                {
                    string[] all = File.ReadAllLines(f);
                    int take = Math.Min(20, all.Length);
                    b.Append("\r\n最近日志（最后 ").Append(take).Append(" 行）：\r\n");
                    for (int i = all.Length - take; i < all.Length; i++) b.Append(all[i]).Append("\r\n");
                }
            }
            catch { }
            return b.ToString();
        }
    }

    // 反馈窗口：一键提 issue（预填好）+ 复制诊断信息。
    // ⚠️ 刻意**不内置任何 token**：客户端直传 issue 需要服务端中转，而"打开预填好的新建页"
    //    不需要任何凭据、也不碰用户的账号 —— 点一下浏览器打开、他自己点提交就行。
    // ⚠️ 国内网络打不开 github.com 是常态：所以文案里明说、并给Lang.T("复制诊断信息", "Copy diagnostics")这条兜底路，
    //    跳转失败也要当场说清原因，绝不静默失败。
    class FeedbackForm : Form
    {
        TextBox _box;
        Label _state;

        public FeedbackForm()
        {
            Text = AppInfo.Name + Lang.T(" 反馈", " Feedback");
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;

            Rectangle wa = GuideForm.WorkArea();
            int winW = Ui.S(600);
            int maxW = (int)(wa.Width * 0.92) - Ui.S(16);
            if (winW > maxW) winW = Math.Max(Ui.S(360), maxW);
            int mL = Ui.S(26), mR = Ui.S(26);
            int contentW = winW - mL - mR;
            SuspendLayout();

            int y = Ui.S(22);

            Label head = new Label();
            head.Text = Lang.T("遇到问题了？", "Something wrong?");
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.Location = new Point(mL, y);
            Ui.Wrap(head, contentW);
            Controls.Add(head);
            y += head.PreferredSize.Height + Ui.S(8);

            Label sub = new Label();
            sub.Text = "点下面的按钮会在浏览器里打开 GitHub 的「新建 issue」页面，标题和环境信息已经帮你填好了，你只要补一句现象、点提交。\r\n"
                     + Lang.T("⚠️ 国内网络经常打不开 github.com —— 打不开是正常的，不是程序坏了：把下面那段诊断信息复制出来，", "⚠️ github.com is often unreachable from mainland China - that is normal, not a bug: copy the diagnostics below and")
                     + Lang.T("直接发给作者（QQ / 微信 / 邮件都行）效果完全一样。", "send them to the author directly (QQ / WeChat / email all work the same).");
            sub.ForeColor = Color.FromArgb(110, 114, 126);
            sub.Location = new Point(mL, y);
            Ui.Wrap(sub, contentW);
            Controls.Add(sub);
            y += sub.PreferredSize.Height + Ui.S(12);

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.BackColor = Color.FromArgb(244, 246, 250);
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.Font = new Font("Consolas", 9f);
            _box.Text = Diag.Full().Replace("\n", "\r\n");
            _box.Location = new Point(mL, y);
            _box.Size = new Size(contentW, Ui.S(190));
            Controls.Add(_box);
            y += _box.Height + Ui.S(12);

            int bh = Ui.S(36);
            RoundButton go = new RoundButton();
            go.Text = Lang.T("在浏览器里提 issue", "Open a GitHub issue");
            go.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            go.Fill = Color.FromArgb(0, 122, 204);
            go.FillHover = Color.FromArgb(0, 140, 232);
            go.TextColor = Color.White;
            go.Size = new Size(Ui.S(190), bh);
            go.Location = new Point(mL, y);
            go.Click += new EventHandler(OnOpen);
            Controls.Add(go);

            RoundButton cp = new RoundButton();
            cp.Text = Lang.T("复制诊断信息", "Copy diagnostics");
            cp.Font = new Font("Microsoft YaHei UI", 10f);
            cp.Fill = Color.FromArgb(238, 240, 245);
            cp.FillHover = Color.FromArgb(226, 230, 238);
            cp.TextColor = Color.FromArgb(60, 64, 74);
            cp.Size = new Size(Ui.S(140), bh);
            cp.Location = new Point(go.Right + Ui.S(10), y);
            cp.Click += new EventHandler(OnCopy);
            Controls.Add(cp);

            RoundButton no = new RoundButton();
            no.Text = Lang.T("关闭", "Close");
            no.Font = new Font("Microsoft YaHei UI", 10f);
            no.Fill = Color.FromArgb(238, 240, 245);
            no.FillHover = Color.FromArgb(226, 230, 238);
            no.TextColor = Color.FromArgb(60, 64, 74);
            no.Size = new Size(Ui.S(96), bh);
            no.Location = new Point(winW - mR - no.Width, y);
            no.Click += new EventHandler(delegate(object o, EventArgs e2) { Close(); });
            Controls.Add(no);
            CancelButton = no;
            y += bh + Ui.S(8);

            _state = new Label();
            _state.Text = "";
            _state.ForeColor = Color.FromArgb(150, 152, 160);
            _state.Location = new Point(mL, y);
            Ui.Wrap(_state, contentW);
            Controls.Add(_state);
            y += Ui.S(28);

            ClientSize = new Size(winW, Math.Min(y, wa.Height - Ui.S(24)));
            ResumeLayout();
        }

        void OnOpen(object o, EventArgs e)
        {
            string title = Lang.T("反馈：", "Feedback: ") + AppInfo.Name + " v" + AppInfo.Version;
            string body = Diag.Brief() + "\r\n【我遇到的情况】\r\n（在这里写一句：做什么的时候、出现了什么）\r\n";
            string url = "https://github.com/" + AppInfo.Repo + "/issues/new?title=" +
                         Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body);
            try
            {
                Process.Start(url);
                _state.ForeColor = Color.FromArgb(0, 130, 90);
                _state.Text = Lang.T("已在浏览器里打开。填完点提交就行 —— 如果页面一直转圈打不开，就改用「复制诊断信息」发给作者。", "Opened in your browser. Just submit when you are done - if the page keeps spinning, use \"Copy diagnostics\" instead.");
            }
            catch (Exception ex)
            {
                _state.ForeColor = Color.FromArgb(190, 90, 40);
                _state.Text = Lang.T("没能打开浏览器（", "Could not open the browser (") + ex.Message + Lang.T("）—— 请点「复制诊断信息」，把它发给作者；仓库地址：", ") - click \"Copy diagnostics\" and send it to the author; repository: ") + url;
            }
        }

        void OnCopy(object o, EventArgs e)
        {
            try
            {
                Clipboard.SetText(Diag.Full());
                _state.ForeColor = Color.FromArgb(0, 130, 90);
                _state.Text = Lang.T("诊断信息已复制。可以直接发给作者，或粘到任何你能打开的地方。", "Diagnostics copied. Send them to the author, or paste them anywhere you can.");
            }
            catch (Exception ex)
            {
                _state.ForeColor = Color.FromArgb(190, 90, 40);
                _state.Text = Lang.T("复制失败（", "Copy failed (") + ex.Message + Lang.T("）—— 可以在上面的框里手动选中复制。", ") - select the text in the box above and copy it manually.");
            }
        }
    }
}

namespace SnapWheel
{
    // ==================== 大图分块识别（0.6.0 阶段 2 的第三条） ====================
    // 问题：Windows 的 OCR 引擎对超大图会先**降采样**再识别，字一小就整段糊掉 ——
    //       而 0.6.0 新加的滚动长截图，产出的正是又高又窄的长图（比如 1200×9000），
    //       整张丢进去等于白给。
    // 做法：把长图**沿竖直方向切成若干横条**，每条都保持原始分辨率、各自走一遍识别，
    //       最后按顺序拼成文本。
    //
    // ⚠️ 切缝不能随便切：切在一行字的中间会把那行字劈成两半（两半都认不出来）。
    //    所以切缝要在目标位置附近**找一条"最空的行"**（行内相邻像素差最小 = 最接近纯色背景），
    //    那里几乎不可能有字，切下去就不伤内容。这样也省掉了"重叠区文字去重"那套麻烦事。
    static class OcrTall
    {
        const int StripH = 1400;      // 每条的目标高度（原分辨率，够引擎吃）
        const int SearchRows = 80;    // 在目标切缝上下各找这么多行，挑最空的一条
        const int MinStrip = 60;      // 比这还矮就不切了（避免最后剩一条细缝反复识别）

        public static bool ShouldSplit(int w, int h)
        {
            // 又高又长才切：高度超过两条、且明显是"长条形"（长截图就是这样的）
            return h > StripH * 2 && (double)h / Math.Max(1, w) > 1.6;
        }

        // 分块识别。任何一条失败就整体失败（返回 null + 原因），不假装成功。
        public static string Recognize(byte[] bgra, int w, int h, out string error)
        {
            error = null;
            StringBuilder all = new StringBuilder();
            int y = 0;
            int guard = 0;
            while (y < h && guard++ < 64)
            {
                int cut = Math.Min(h, y + StripH);
                if (cut < h) cut = BlankRowNear(bgra, w, h, cut);
                int ch = cut - y;
                if (ch < MinStrip) break;

                byte[] strip = Crop(bgra, w, h, y, ch);
                string t = Ocr.RecognizePixels(strip, w, ch, out error);
                if (t == null) return null;
                t = t.Trim();
                if (t.Length > 0)
                {
                    if (all.Length > 0) all.Append('\n');
                    all.Append(t);
                }
                y = cut;
            }
            return all.ToString();
        }

        // 在目标行附近挑一条"最空"的行当切缝：空 = 这一行里相邻像素差别最小（接近纯色）
        static int BlankRowNear(byte[] px, int w, int h, int target)
        {
            int lo = Math.Max(1, target - SearchRows);
            int hi = Math.Min(h - 2, target + SearchRows);
            int best = target;
            long bestScore = long.MaxValue;
            for (int y = lo; y <= hi; y++)
            {
                long s = 0;
                int o = y * w * 4;
                // 横向抽样比对（每 8 个像素取一个），够判断"是不是一片均匀）
                for (int x = 0; x + 32 < w * 4; x += 32)
                {
                    int a = px[o + x], b = px[o + x + 32];
                    s += Math.Abs(a - b);
                }
                if (s < bestScore) { bestScore = s; best = y; }
                if (bestScore == 0) break;      // 纯色行，不用再找
            }
            return best;
        }

        // 按行裁一段出来（32bpp，一行 w*4 字节）
        static byte[] Crop(byte[] px, int w, int h, int y0, int ch)
        {
            byte[] outp = new byte[w * ch * 4];
            Buffer.BlockCopy(px, y0 * w * 4, outp, 0, outp.Length);
            return outp;
        }
    }
}

namespace SnapWheel
{
    // ==================== 自动更新（0.8.1） ====================
    //
    // 设计目标：**不花一分钱**，也不需要代码签名或自己的服务器。
    //   ① 检查：匿名 GET GitHub 的 releases/latest 接口（限流 60 次/小时/IP，够用）
    //   ② 下载：把 zip 存到 %TEMP%\SnapWheel-update\
    //   ③ 安装：用 `SnapWheel.exe --apply-update <目录>` 启动"更新器模式"的自己 ——
    //      等主进程退出 → 覆盖文件 → 重新启动主程序 → 自己退出
    //
    // 为什么这样能在没有签名的情况下工作：**升级是"覆盖已有文件"，不触发 SmartScreen**；
    // 只有首次下载的全新文件才会提示，而那是用户主动下载的。
    //
    // 失败一律**静默**：国内经常连不上 GitHub，检查更新绝不能变成打扰。
    // 设置里有开关（默认开），托盘菜单里也有手动的"检查更新"。

    static class Update
    {
        public const string ApiUrl = "https://api.github.com/repos/ExpertKT/SnapWheel/releases/latest";
        public const string ReleasesPage = "https://github.com/ExpertKT/SnapWheel/releases/latest";

        public static string UpdateDir { get { return Path.Combine(Path.GetTempPath(), "SnapWheel-update"); } }

        // 找到的更新（null 表示没有新版、或检查失败）
        public class Found
        {
            public string Version;     // 例如 "0.8.1"
            public string ZipUrl;      // 对应 exe 的 zip 下载地址
        }

        // ---------- 比较版本号 ----------
        // 只比较数字部分，容忍 "v" 前缀和 "-beta" 之类的后缀。
        // 实现搬到了 AppInfo —— 引导窗口也要用同一套判断（「这条说明比你看过的那版新吗」），
        // 两处各写一份迟早会不一致。这里保留一个转发：调用点写法不变，逻辑永远只有一份。
        public static bool IsNewer(string remote, string local)
        {
            return AppInfo.IsNewer(remote, local);
        }

        // ---------- 检查 ----------
        // 同步执行（在后台线程里调）。任何异常都返回 null，绝不打扰用户。
        public static Found Check()
        {
            try
            {
                string json = Http(ApiUrl, 15000);
                if (string.IsNullOrEmpty(json)) return null;

                string tag = JsonString(json, "tag_name");
                if (string.IsNullOrEmpty(tag)) return null;
                if (!IsNewer(tag, AppInfo.Version)) return null;

                // 找 assets 里文件名包含 "full" 的 zip（完整版）；没有就退回第一个 zip
                string url = null, fallback = null;
                int idx = 0;
                while (true)
                {
                    int p = json.IndexOf("browser_download_url", idx, StringComparison.Ordinal);
                    if (p < 0) break;
                    string u = JsonStringAt(json, p);
                    idx = p + 20;
                    if (string.IsNullOrEmpty(u)) continue;
                    if (u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fallback == null) fallback = u;
                        if (u.IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0) { url = u; break; }
                    }
                }
                if (url == null) url = fallback;
                if (url == null) return null;

                Found f = new Found();
                f.Version = tag.TrimStart('v', 'V');
                f.ZipUrl = url;
                return f;
            }
            catch { return null; }
        }

        // ---------- 下载并解压 ----------
        // 返回解压后的目录；失败返回 null。
        public static string Download(Found f, Action<int> progress)
        {
            try
            {
                if (Directory.Exists(UpdateDir)) { try { Directory.Delete(UpdateDir, true); } catch { } }
                Directory.CreateDirectory(UpdateDir);
                string zip = Path.Combine(UpdateDir, "update.zip");

                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "SnapWheel/" + AppInfo.Version);
                    if (progress != null)
                    {
                        wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                        {
                            try { progress(e.ProgressPercentage); } catch { }
                        };
                    }
                    wc.DownloadFile(new Uri(f.ZipUrl), zip);
                }

                string outDir = Path.Combine(UpdateDir, "files");
                Directory.CreateDirectory(outDir);
                ZipExtract(zip, outDir);
                try { File.Delete(zip); } catch { }

                // zip 里可能有一层目录，往下找到含 exe 的那一层
                string exeDir = FindExeDir(outDir, 0);
                return exeDir ?? outDir;
            }
            catch { return null; }
        }

        static string FindExeDir(string dir, int depth)
        {
            if (depth > 3 || !Directory.Exists(dir)) return null;
            if (Directory.GetFiles(dir, "SnapWheel*.exe").Length > 0) return dir;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string r = FindExeDir(sub, depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        // ---------- 更新器模式 ----------
        // 以 `--apply-update <目录> <等待的进程id>` 启动自己：主进程退出后覆盖文件并重启。
        public static bool IsApplyMode(string[] args)
        {
            return args != null && args.Length >= 2 && args[0] == "--apply-update";
        }

        public static void RunApply(string[] args)
        {
            string src = args[1];
            int waitPid = 0;
            if (args.Length >= 3) int.TryParse(args[2], out waitPid);

            // 等主进程退出（最多 30 秒）
            if (waitPid > 0)
            {
                try
                {
                    Process p = Process.GetProcessById(waitPid);
                    p.WaitForExit(30000);
                }
                catch { }
            }
            else
            {
                Thread.Sleep(1200);
            }
            Thread.Sleep(400);      // 再给文件系统一点时间

            string me = Assembly.GetEntryAssembly().Location;
            string myDir = Path.GetDirectoryName(me);
            string myName = Path.GetFileName(me);
            int copied = 0;

            try
            {
                foreach (string f in Directory.GetFiles(src))
                {
                    string name = Path.GetFileName(f);
                    string dst = Path.Combine(myDir, name);
                    // 正在运行的自己是锁住的，跳过（更新器自己就是它）
                    if (string.Equals(name, myName, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Copy(f, dst, true); copied++; } catch { }
                }
            }
            catch { }

            // 重启主程序
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(me);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = myDir;
                Process.Start(psi);
            }
            catch { }

            Environment.Exit(copied > 0 ? 0 : 1);
        }

        // ---------- 内置的极简 HTTP + JSON + ZIP ----------
        // 只有几百行的小项目，不值得为此引入第三方库；够用就行。
        public static string Http(string url, int timeoutMs)
        {
            try
            {
                // .NET 4.0 默认只开 TLS 1.0，GitHub 会直接拒绝连接 —— 必须显式开 TLS 1.2。
                // （这条是从项目里原有的检查更新代码学来的，漏了它检查会一直失败。）
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "SnapWheel/" + AppInfo.Version;
                req.Accept = "application/vnd.github+json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch { return null; }
        }

        // 从 json 里取 "key": "value"
        static string JsonString(string json, string key)
        {
            int p = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (p < 0) return null;
            return JsonStringAt(json, p);
        }

        // 从 p（某个 key 或字段名出现的位置）往后找第一个字符串值
        static string JsonStringAt(string json, int p)
        {
            int colon = json.IndexOf(':', p);
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
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'u' && i + 5 < json.Length)
                    {
                        int cp = 0;
                        if (int.TryParse(json.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                            sb.Append((char)cp);
                        i += 4;
                    }
                    else sb.Append(n);
                    i += 2;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // 解压：先试系统自带的 ZipFile（.NET 4.5+，用反射调用所以不需要编译期引用）；
        // 拿不到就用自己的解析器 —— 这一点是测试逼出来的：
        // 项目编译目标是 .NET 4.0，反射 Type.GetType 找不到 System.IO.Compression.FileSystem，
        // 如果没有兜底，自动更新到了用户机器上会直接失败。
        static void ZipExtract(string zip, string outDir)
        {
            try
            {
                Type t = Type.GetType("System.IO.Compression.ZipFile, System.IO.Compression.FileSystem");
                if (t != null)
                {
                    System.Reflection.MethodInfo m = t.GetMethod("ExtractToDirectory", new Type[] { typeof(string), typeof(string) });
                    if (m != null) { m.Invoke(null, new object[] { zip, outDir }); return; }
                }
            }
            catch { }
            ZipExtractManual(zip, outDir);
        }

        // 自己解析 ZIP：读"中央目录"拿到每个条目的准确信息，再用 DeflateStream 解压。
        // 只做"解压"这一个用途，所以不需要支持加密、ZIP64、多卷这些特性 —— 够用就好。
        static void ZipExtractManual(string zip, string outDir)
        {
            byte[] d = File.ReadAllBytes(zip);
            // 从尾部往前找 EOCD（End Of Central Directory）签名 PK\x05\x06
            int eocd = -1;
            int low = Math.Max(0, d.Length - 65558);
            for (int i = d.Length - 22; i >= low; i--)
            {
                if (d[i] == 0x50 && d[i+1] == 0x4b && d[i+2] == 0x05 && d[i+3] == 0x06) { eocd = i; break; }
            }
            if (eocd < 0) throw new Exception("不是有效的 zip 文件");

            int count = U16(d, eocd + 10);
            int cd = (int)U32(d, eocd + 16);
            int p = cd;

            for (int i = 0; i < count; i++)
            {
                if (p + 46 > d.Length) break;
                if (!(d[p] == 0x50 && d[p+1] == 0x4b && d[p+2] == 0x01 && d[p+3] == 0x02)) break;   // PK\x01\x02
                int method  = U16(d, p + 10);
                long csize  = U32(d, p + 20);
                int nlen    = U16(d, p + 28);
                int elen    = U16(d, p + 30);
                int clen    = U16(d, p + 32);
                long lho    = U32(d, p + 42);
                string name = Encoding.UTF8.GetString(d, p + 46, nlen);

                p += 46 + nlen + elen + clen;

                if (string.IsNullOrEmpty(name) || name.EndsWith("/") || name.EndsWith("\\")) continue;

                // 本地文件头里的名字/扩展区长度可能和中央目录不同，必须重新读一遍
                int lnlen = U16(d, (int)lho + 26);
                int lelen = U16(d, (int)lho + 28);
                int ds = (int)lho + 30 + lnlen + lelen;
                if (ds < 0 || ds > d.Length) continue;

                // 防目录穿越：把 .. 和绝对路径都挡掉
                string safe = name.Replace('\\', '/');
                if (safe.StartsWith("/") || safe.Contains("..")) continue;
                string dst = Path.Combine(outDir, safe.Replace('/', Path.DirectorySeparatorChar));
                string dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);

                if (method == 0)      // 存储（未压缩）
                {
                    using (FileStream o = File.Create(dst)) o.Write(d, ds, (int)csize);
                }
                else if (method == 8) // deflate（最常用）
                {
                    using (MemoryStream ms = new MemoryStream(d, ds, (int)csize, false))
                    using (System.IO.Compression.DeflateStream z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
                    using (FileStream o = File.Create(dst))
                        z.CopyTo(o);
                }
                else continue;   // 其它压缩方式不支持（我们的发行包不会用到）
            }
        }

        static int U16(byte[] d, int i)
        {
            if (i < 0 || i + 2 > d.Length) return 0;
            return d[i] | (d[i+1] << 8);
        }
        static uint U32(byte[] d, int i)
        {
            if (i < 0 || i + 4 > d.Length) return 0;
            return (uint)(d[i] | (d[i+1] << 8) | (d[i+2] << 16) | (d[i+3] << 24));
        }
    }
}

namespace SnapWheel
{
    // ==================== 传递模式（0.9.0） ====================
    //
    // 目标：**不用鼠标也能把一张图"拖"进别的程序**。
    //
    // 设计（用户提的方案）：
    //   ① 在轮盘上用键盘选中一张缩略图
    //   ② 进入传递模式：缩略图被"提起来"，跟着一个**假光标**走
    //   ③ 用户自己切到目标窗口（Alt+Tab 或鼠标点都行，随便）
    //   ④ 用 WASD（或方向键）操控假光标移动，Shift 加速
    //   ⑤ 按 Enter/空格 = 放下；Esc = 取消
    //
    // 为什么"放下"要真的去动鼠标：目标程序只认**真实的拖放**（OLE 拖放 / WM_DROPFILES），
    // 它没法理解"我这边有个假光标"。所以放下的瞬间，我们做的事是：
    //   把真实光标挪到假光标的位置 → 左键按下 → 分几步移动过去（模拟人手）→ 左键松开。
    // 这样目标程序收到的就是一次普普通通的拖放，兼容性最好。
    //
    // 关于"怎么知道用户按了 WASD"：**不用低级键盘钩子**，而是定时器轮询 GetAsyncKeyState。
    // 钩子会插进系统输入链、容易被安全软件盯上、还得保证一定卸载；轮询简单得多，效果一样。

    class CarryForm : Form
    {
        const int TickMs = 15;
        const float SpeedSlow = 6f;      // 像素/帧
        const float SpeedFast = 20f;     // 按住 Shift
        const int ThumbW = 132, ThumbH = 99;   // 吸附的缩略图尺寸
        const int CursorW = 26, CursorH = 26;  // 假光标尺寸

        Bitmap _thumb;                   // 吸附在假光标上的缩略图（传递中可以换）
        Point _origin;                   // 起点（轮盘上那张缩略图的位置）—— 模拟拖放时从这里按下
        Point _pos;                      // 假光标的屏幕坐标
        float _fx, _fy;                  // 亚像素累积（不然慢速移动会卡顿或跳格）
        System.Windows.Forms.Timer _tick;
        CarryHintForm _hint;            // 屏幕底部的操作提示（独立小窗，不抢焦点）
        DateTime _started;
        bool _busy;                      // 正在执行"放下"的模拟，别让定时器再插一脚
        // 一次性动作键要用"边沿"判断：只在"刚才没按、现在按下"那一刻触发。
        // 不这么做会出事 —— 传递热键是 Ctrl+Alt+C，用户按完 C 键还按着不放，
        // 窗口一出现就检测到 C 是按下状态，立刻当成"复制到剪贴板"并关闭，
        // 表现就是"闪了一下就没了"（用户实测出来的）。
        bool _prevEnter, _prevEsc, _prevC, _prevSpace;

        // ---- "飞回"动画：按 Esc 取消时，不是啪一下消失，而是让缩略图飞回它在轮盘上的位置 ----
        // 直接消失会让人不知道刚才那张去哪了；飞回去明确表达"放回原处了"。
        // 不需要额外的窗口 —— 假光标窗口本身就是透明置顶的，让它自己移动+淡出即可。
        bool _flyBack;
        Point _flyFrom, _flyTo;
        DateTime _flyStart;
        float _flyScale = 1f;            // 飞行途中缩略图一起缩小（1 → 0.4）
        const int FlyMs = 320;

        /// <summary>用户确认放下了（Enter/空格）。</summary>
        public bool Confirmed;
        /// <summary>放下的位置（屏幕坐标）。</summary>
        public Point DropPoint;

        // true = 用的是「复制到剪贴板」，而不是模拟拖放（对键盘用户更顺，也更可靠）
        public bool UseClipboard;

        /// <summary>
        /// 用户在传递模式里要求换一张图（按 , 或 .）。
        /// 参数是相对位移：-1 上一张、+1 下一张。
        /// 由 App 订阅去换图，然后回调 SetThumb 把新的缩略图换上来。
        /// </summary>
        public event Action<int> SwitchRequested;

        /// <summary>换掉假光标上吸附的那张图（App 换好之后回调进来）。</summary>
        public void SetThumb(Bitmap thumb, Point origin)
        {
            try { if (_thumb != null) _thumb.Dispose(); } catch { }
            _thumb = thumb;
            _origin = origin;
            try { Invalidate(); } catch { }
        }

        /// <summary>换提示条上的文字（提示条是独立小窗，转交给它）。</summary>
        public void SetHintText(string text)
        {
            try { if (_hint != null) _hint.SetText(text); } catch { }
        }

        bool _prevPrev, _prevNext;       // 换图键的边沿状态

        public CarryForm(Bitmap thumb, Point origin)
        {
            _thumb = thumb;
            _origin = origin;
            _pos = new Point(origin.X, origin.Y);
            _fx = origin.X; _fy = origin.Y;
            _started = DateTime.Now;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Magenta;          // 透明键：这个颜色会被系统抠掉，只留下光标和缩略图
            TransparencyKey = Color.Magenta;
            ClientSize = new Size(ThumbW + CursorW + 24, ThumbH + CursorH + 24);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            DoubleBuffered = true;
            KeyPreview = true;

            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = TickMs;
            _tick.Tick += new EventHandler(OnTick);
            _tick.Start();

            // 操作提示条：第一次用传递模式的人不可能知道按什么键，所以必须写在屏幕上。
            try { _hint = new CarryHintForm(); _hint.Show(); } catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                TopMost = true;
                BringToFront();
                // 这里**故意不** Activate()：假光标靠全局轮询 GetAsyncKeyState 读按键，
                // 不需要键盘焦点；抢焦点会让用户 Alt+Tab 切到别的程序时"切不过去"（用户实测）。
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            catch { }
            ApplyPos();
            // ⚠️ 键盘钩子先停用：装上去之后"一进传递模式就卡死"（用户实测）。
            // 低级键盘钩子一旦处理不当会卡住整个系统的输入链，风险太高 —— 先回到可用状态，
            // 再换一种不插进系统输入链的做法来处理"WASD 漏到前台窗口"的问题。
            // InstallHook();
        }

        static bool Down(Keys k)
        {
            try { return (Native.GetAsyncKeyState((int)k) & 0x8000) != 0; }
            catch { return false; }
        }

        /// <summary>只在"刚才没按、现在按下"的那一刻返回 true（上升沿）。</summary>
        static bool Rising(ref bool prev, Keys k)
        {
            bool now = Down(k);
            bool edge = now && !prev;
            prev = now;
            return edge;
        }

        void OnTick(object sender, EventArgs e)
        {
            if (_busy) return;

            // 飞行分支：按 Esc 之后走这条，把缩略图送回轮盘上的位置
            if (_flyBack)
            {
                try
                {
                    double t = (DateTime.Now - _flyStart).TotalMilliseconds / (double)FlyMs;
                    if (t >= 1.0) { try { _tick.Stop(); } catch { } try { Close(); } catch { } return; }
                    float eased = 1f - (float)Math.Pow(1.0 - t, 3);    // ease-out：起步快、收尾慢
                    _fx = _flyFrom.X + (_flyTo.X - _flyFrom.X) * eased;
                    _fy = _flyFrom.Y + (_flyTo.Y - _flyFrom.Y) * eased;
                    _pos = new Point((int)Math.Round(_fx), (int)Math.Round(_fy));
                    _flyScale = 1f - eased * 0.6f;                     // 缩到 40%
                    try { Opacity = Math.Max(0.05, 1.0 - t * 0.85); } catch { }
                    ApplyPos();
                }
                catch { try { Close(); } catch { } }
                return;
            }

            try
            {
                // 超时保护：两分钟没有任何确认就自己取消，免得假光标一直赖在屏幕上
                if ((DateTime.Now - _started).TotalSeconds > 120) { Cancel(); return; }

                float v = Down(Keys.ShiftKey) || Down(Keys.LShiftKey) || Down(Keys.RShiftKey) ? SpeedFast : SpeedSlow;
                float dx = 0, dy = 0;

                // WASD 和方向键都支持（有人习惯方向键）
                if (Down(Keys.W) || Down(Keys.Up))    dy -= v;
                if (Down(Keys.S) || Down(Keys.Down))  dy += v;
                if (Down(Keys.A) || Down(Keys.Left))  dx -= v;
                if (Down(Keys.D) || Down(Keys.Right)) dx += v;

                if (dx != 0 && dy != 0) { dx *= 0.7071f; dy *= 0.7071f; }   // 斜着走不要更快

                if (dx != 0 || dy != 0)
                {
                    _fx += dx; _fy += dy;
                    // 夹在虚拟屏幕范围内，别让假光标跑出屏幕
                    Rectangle vs = SystemInformation.VirtualScreen;
                    if (_fx < vs.Left) _fx = vs.Left;
                    if (_fy < vs.Top) _fy = vs.Top;
                    if (_fx > vs.Right - CursorW) _fx = vs.Right - CursorW;
                    if (_fy > vs.Bottom - CursorH) _fy = vs.Bottom - CursorH;
                    _pos = new Point((int)Math.Round(_fx), (int)Math.Round(_fy));
                    ApplyPos();
                }

                // 一次性动作：必须用上升沿（见 _prev* 字段的注释）。
                // 另外刚显示后的 350ms 里不响应按键 —— 用户刚按完热键，
                // 手指还压在键上，那段窗口期不该被当成"用户操作"。
                bool warmed = (DateTime.Now - _started).TotalMilliseconds > 350;
                if (warmed)
                {
                    // 空格和 Enter 都能"放下"（空格更顺手，Enter 保留）。
                    // 注意两个键各用一个 _prev 变量：共用一个的话，先按 Enter 再按空格时
                    // 第二个键会被当成"一直按着"，上升沿不成立。
                    if (Rising(ref _prevSpace, Keys.Space) || Rising(ref _prevEnter, Keys.Enter)) { DoDrop(); return; }
                    if (Rising(ref _prevC, Keys.C)) { UseClipboard = true; DoDrop(); return; }   // 复制到剪贴板
                    if (Rising(ref _prevEsc, Keys.Escape)) { Cancel(); return; }

                    // 换一张图（不想传这张了，不用退出去重来）。
                    // 用 [ ] 而不是 Q/E：Q E 是会"打字"的字母键，按下去目标窗口里会留下字母、
                    // 还可能把输入法叫出来；方括号不产生文字，也就没有这个问题。
                    // （这也是为什么移动键推荐方向键 —— 同理不产生文字。）
                    if (Rising(ref _prevPrev, Keys.OemOpenBrackets)) { RaiseSwitch(-1); return; }
                    if (Rising(ref _prevNext, Keys.OemCloseBrackets)) { RaiseSwitch(1); return; }
                }
            }
            catch { }
        }

        void ApplyPos()
        {
            try { Location = _pos; Invalidate(); } catch { }
        }

        void Cancel()
        {
            // 不直接消失：让缩略图"飞回"它在轮盘上的位置（缓动 + 淡出 + 缩小，约 320ms）。
            // 真正关闭在 OnTick 的飞行分支里做。
            if (_flyBack) { try { Close(); } catch { } return; }
            _flyBack = true;
            Confirmed = false;
            _flyFrom = _pos;
            _flyTo = _origin;
            _flyStart = DateTime.Now;
            try { if (_hint != null) _hint.Close(); } catch { }   // 提示条先收掉
            try { _tick.Start(); } catch { }                       // 定时器继续跑，改走飞行分支
        }

        // ---------- 放下：把假光标的位置"演"成一次真实的鼠标拖放 ----------
        void DoDrop()
        {
            _busy = true;
            try { _tick.Stop(); } catch { }
            Confirmed = true;
            DropPoint = _pos;

            // 先在屏幕上把假光标藏起来，接下来的动作交给真实光标
            try { if (_hint != null) _hint.Close(); } catch { }
            try { Opacity = 0; } catch { }
            try { Hide(); } catch { }
            Application.DoEvents();

            Thread t = new Thread(delegate()
            {
                SimulateDrag(_origin, DropPoint);
                try { BeginInvoke((MethodInvoker)delegate { try { Close(); } catch { } }); } catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>通知 App "用户要换图了"（-1 上一张 / +1 下一张）。</summary>
        void RaiseSwitch(int delta)
        {
            try { if (SwitchRequested != null) SwitchRequested(delta); } catch { }
        }

        // ---------- 键盘钩子：把属于传递模式的按键"吃掉" ----------
        //
        // 不做这件事的话，用户按 WASD 时那些字母会照常送进前台窗口：
        // 输入法弹出来、聊天框里被打进一堆字母（用户实测就是这个）。
        // 轮询只能"读"按键，要"拦"就必须在系统输入链上装钩子。
        IntPtr _hook = IntPtr.Zero;
        Native.LowLevelKeyboardProc _hookProc;      // 必须保留引用：被 GC 回收的话钩子会失效甚至崩

        void InstallHook()
        {
            try
            {
                if (_hook != IntPtr.Zero) return;
                _hookProc = new Native.LowLevelKeyboardProc(HookCallback);
                _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc,
                                                Native.GetModuleHandle(null), 0);
                Err.Log("Carry.Hook", new Exception("键盘钩子已安装：" + (_hook != IntPtr.Zero)));
            }
            catch (Exception ex) { Err.Log("Carry.Hook", ex); }
        }

        void UninstallHook()
        {
            try
            {
                if (_hook != IntPtr.Zero)
                {
                    Native.UnhookWindowsHookEx(_hook);
                    Err.Log("Carry.Hook", new Exception("键盘钩子已卸载"));
                }
            }
            catch { }
            _hook = IntPtr.Zero;
            _hookProc = null;
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
                    if (IsCarryKey(vk)) return (IntPtr)1;   // 返回 1 = 吞掉，不让它继续传下去
                }
            }
            catch { }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>传递模式自己要用到的键（这些键在传递期间不该传给别人）。</summary>
        static bool IsCarryKey(int vk)
        {
            switch (vk)
            {
                case 0x57:   // W
                case 0x41:   // A
                case 0x53:   // S
                case 0x44:   // D
                case 0x51:   // Q  上一张
                case 0x45:   // E  下一张
                case 0x20:   // 空格 放下
                case 0x0D:   // Enter 放下
                case 0x1B:   // Esc 取消
                case 0x10:   // Shift 加速
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 轮盘窗口的句柄，由 App 在进入传递模式时设置。
        /// 模拟拖放前必须先把它拉到前台 —— 见下面 SimulateDrag 里的说明。
        /// </summary>
        public static IntPtr WheelHandle = IntPtr.Zero;

        /// <summary>
        /// 把光标移到某个点，**并且生成真实的鼠标移动消息**。
        ///
        /// 为什么不能只用 SetCursorPos：它只是把光标"瞬移"过去，**不产生鼠标移动消息**。
        /// 而轮盘的拖出是靠"按下 + 鼠标移动"启动的（OLE 拖放的启动条件），收不到移动消息
        /// 就永远不会开始拖 —— 用户实测的现象正是"真鼠标指针从起点移到了终点，然后什么都没发生"。
        /// 补一个 MOUSEEVENTF_MOVE 就是在告诉系统"鼠标真的动了"（位移 0，只为了让消息发出去）。
        ///
        /// ⚠️ 这几行在代码回退时丢过一次，结果"放下"又变成同一个失败现象。
        /// 改动这段时务必保留 —— 它看起来多余，其实是拖放能启动的关键。
        /// </summary>
        static void MoveTo(int x, int y)
        {
            Native.SetCursorPos(x, y);
            Native.mouse_event(Native.MOUSEEVENTF_MOVE, 0, 0, 0, IntPtr.Zero);
        }

        /// <summary>
        /// 模拟一次真实的拖放：光标移到起点 → 按下 → 分步移到终点 → 松开。
        /// 分步移动很重要：一步跳过去的话，多数程序不会把它当成拖放。
        /// </summary>
        public static void SimulateDrag(Point from, Point to)
        {
            Usage.Ev("Carry.Drop", from.X + "," + from.Y + " → " + to.X + "," + to.Y);
            try
            {
                // 关键的第一步：把轮盘拉到前台。
                //
                // 轮盘是"不激活窗口"（当初为了不抢焦点、让用户能 Alt+Tab 切过去），
                // 而 Windows 的规则是：**非活动窗口的第一次点击会被系统用来激活它，应用收不到**。
                // 我们的模拟点击正好就是那第一次点击 —— 被系统吃掉，轮盘没收到"按下"，
                // 也就不会有 DoDragDrop。所以这里先显式把它带到前台，让后面的点击真正送达。
                if (WheelHandle != IntPtr.Zero)
                {
                    bool fg = Native.SetForegroundWindow(WheelHandle);
                    // 记下来：这一步成不成，直接决定后面的模拟点击能不能送到轮盘手上
                    Err.Log("Carry.Drag", new Exception("放下开始：拿前台=" + fg + " 起点=" + from.X + "," + from.Y + " 终点=" + to.X + "," + to.Y));
                    Thread.Sleep(180);
                }
                else
                {
                    Err.Log("Carry.Drag", new Exception("放下开始：WheelHandle 没拿到！起点=" + from.X + "," + from.Y));
                }

                MoveTo(from.X, from.Y);
                Thread.Sleep(100);

                // 核对坐标：我们**以为**移到了起点，实际落在哪？
                try
                {
                    Point actual = Cursor.Position;
                    Rectangle vs = SystemInformation.VirtualScreen;
                    Err.Log("Carry.Drag", new Exception(
                        "移到起点 " + from.X + "," + from.Y + " → 实际光标 " + actual.X + "," + actual.Y +
                        "　虚拟屏幕=" + vs.Width + "x" + vs.Height));
                }
                catch { }

                Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);

                int steps = 22;      // 步数多一些、每步慢一些，更像人手（一步跳过去多数程序不认）
                for (int i = 1; i <= steps; i++)
                {
                    Point p = StepPoint(from, to, i, steps);
                    MoveTo(p.X, p.Y);
                    Thread.Sleep(20);
                }
                Thread.Sleep(120);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(40);
            }
            catch { }
        }

        // ---------- 绘制：缩略图（带阴影）+ 假光标 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // 缩略图吸附在假光标右下角
            int tx = CursorW / 2 + 6;
            int ty = CursorH / 2 + 6;

            try
            {
                // 阴影（跟着缩略图一起缩）
                int shw = (int)(ThumbW * _flyScale), shh = (int)(ThumbH * _flyScale);
                for (int i = 4; i >= 1; i--)
                {
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(26, 0, 0, 0)))
                        g.FillRectangle(sb, tx - i + 2, ty - i + 3, shw + i * 2, shh + i * 2);
                }
                // 图（按 _flyScale 缩放：飞行途中一起缩小，回到环上时正好是缩略图大小）
                int tw = (int)(ThumbW * _flyScale), th = (int)(ThumbH * _flyScale);
                int cx = tx + ThumbW / 2, cy = ty + ThumbH / 2;
                Rectangle box = new Rectangle(cx - tw / 2, cy - th / 2, tw, th);
                if (_thumb != null) g.DrawImage(_thumb, box);
                // 白边（提到"被拿起来"的感觉）
                using (Pen p = new Pen(Color.FromArgb(230, 255, 255, 255), 2f))
                    g.DrawRectangle(p, box);
            }
            catch { }

            // 假光标：画一个和系统箭头形状接近的白色箭头 + 黑描边
            try
            {
                PointF[] arrow = new PointF[]
                {
                    new PointF(2f, 2f),
                    new PointF(2f, 20f),
                    new PointF(7f, 15.5f),
                    new PointF(10.5f, 23f),
                    new PointF(14f, 21.5f),
                    new PointF(10.5f, 14f),
                    new PointF(17f, 13.5f),
                };
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddPolygon(arrow);
                    using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, gp);
                    using (Pen p = new Pen(Color.FromArgb(240, 20, 20, 20), 1.6f)) g.DrawPath(p, gp);
                }
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            UninstallHook();     // 一定要卸：钩子挂着不卸会影响全局键盘输入
            try { _tick.Stop(); _tick.Dispose(); } catch { }
            try { if (_hint != null) { _hint.Close(); _hint.Dispose(); _hint = null; } } catch { }
            base.OnFormClosed(e);
        }

        // 让窗口不抢焦点也能收到 Esc（保险）
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Cancel(); return true; }
            if (keyData == Keys.Enter || keyData == Keys.Space) { DoDrop(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        /// <summary>
        /// 拖放过程的第 i 步落在哪：在起点和终点之间按比例插值。
        /// 抽成独立方法有两个原因：① 语义清楚（这正是"分步移动"的核心）；
        /// ② **可测** —— 分步是否符合预期是能在离线环境验证的，不必真去动鼠标。
        /// </summary>
        public static Point StepPoint(Point from, Point to, int i, int steps)
        {
            if (steps < 1) steps = 1;
            if (i < 0) i = 0;
            if (i > steps) i = steps;
            return new Point(from.X + (to.X - from.X) * i / steps,
                             from.Y + (to.Y - from.Y) * i / steps);
        }

    }

    /// <summary>
    /// 传递模式的操作提示条：贴在屏幕底部居中，只说明按什么键，不接受任何操作。
    /// 单独一个小窗而不是画在假光标窗口里 —— 假光标窗口只有光标那么大，
    /// 而且它跟着光标到处跑，提示条会一直晃。
    /// </summary>
    class CarryHintForm : Form
    {
        string _text;      // 当前显示的说明文字（换图/放下之后会换成别的提示）

        /// <summary>
        /// 换上新的说明文字。文字长短不同，窗口要**重新量一次**并重新贴到底部居中，
        /// 不然新文字会被裁掉。
        /// </summary>
        public void SetText(string t)
        {
            if (string.IsNullOrEmpty(t)) return;
            _text = t;
            try
            {
                using (Font f = HintFont())
                using (Bitmap b = new Bitmap(1, 1))
                using (Graphics g = Graphics.FromImage(b))
                {
                    SizeF sz = g.MeasureString(t, f, new PointF(0, 0), StringFormat.GenericTypographic);
                    ClientSize = new Size((int)Math.Ceiling((double)sz.Width) + PadX * 2,
                                          (int)Math.Ceiling((double)sz.Height) + PadY * 2);
                }
                Rectangle scr = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(scr.Left + (scr.Width - Width) / 2, scr.Bottom - Height - 40);
                Invalidate();
            }
            catch { }
        }

        const int PadX = 22, PadY = 12;

        public CarryHintForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(198, 20, 22, 26);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            DoubleBuffered = true;

            string s = _text ?? Lang.T("方向键 移动　·　Shift 加速　·　空格 放下　·　[ ] 换一张　·　Esc 取消　（WASD 也能用，但会在目标窗口里留下字母）",
                                       "Arrow keys move  ·  Shift faster  ·  Space drop  ·  [ ] switch  ·  Esc cancel   (WASD also works, but it types letters into the target window)");
            using (Font f = HintFont())
            {
                SizeF sz;
                using (Bitmap b = new Bitmap(1, 1))
                using (Graphics g = Graphics.FromImage(b))
                    sz = g.MeasureString(s, f, new PointF(0, 0), StringFormat.GenericTypographic);
                ClientSize = new Size((int)Math.Ceiling((double)sz.Width) + PadX * 2, (int)Math.Ceiling((double)sz.Height) + PadY * 2);
            }

            // 贴在"光标所在那块屏幕"的底部居中
            Rectangle scr = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(scr.Left + (scr.Width - Width) / 2, scr.Bottom - Height - 40);
        }

        static Font HintFont()
        {
            try { return new Font(DrawKit.UI, 10.5f); }
            catch { return new Font(FontFamily.GenericSansSerif, 10.5f); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            string s = _text ?? Lang.T("方向键 移动　·　Shift 加速　·　空格 放下　·　[ ] 换一张　·　Esc 取消　（WASD 也能用，但会在目标窗口里留下字母）",
                                       "Arrow keys move  ·  Shift faster  ·  Space drop  ·  [ ] switch  ·  Esc cancel   (WASD also works, but it types letters into the target window)");
            using (Font f = HintFont())
            using (SolidBrush b = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
                DrawKit.DrawFitted(g, s, new RectangleF(PadX - 6, PadY - 4, ClientSize.Width - PadX * 2 + 12, ClientSize.Height - PadY * 2 + 8),
                                   b.Color, 11, ClientSize.Width - PadX * 2 + 12, DrawKit.UI, FontStyle.Regular, Align.Center);
        }

        // 不抢焦点：不然用户按 Alt+Tab 切窗口时提示条会把焦点抢回来
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                TopMost = true;
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            catch { }
    }
}
}
