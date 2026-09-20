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
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int GetDpiForSystem();
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        // 0.6.0 滚动长截图：给目标窗口发合成的滚轮消息（这是产品功能，不是测试里的模拟输入）
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        public const uint WM_MOUSEWHEEL = 0x020A;
        // 置顶但**不激活**：TopMost=false/true 或 BringToFront() 会抢一次前台焦点，
        // 那样托盘右键菜单、打赏窗口这些"一失焦就自动关闭"的界面点出来就会过一会儿自己没了。
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
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
