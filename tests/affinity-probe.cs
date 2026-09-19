// affinity-probe.cs -- 列出所有「把自己排除在截屏之外」的顶层窗口。
//
// 为什么要有这个工具：
//   用户报"截图的时候微信会突然消失，别的程序不会"。排查方向有很多（钩子？键盘合成？
//   往别人窗口发消息？），但真正的原因只有一种可能：**那个程序自己告诉 Windows 别拍它**。
//   这件事是**可以量出来的**，不该靠猜 —— 一条 GetWindowDisplayAffinity 就能定案。
//
// 显示亲和性（display affinity）的取值：
//   0x00  WDA_NONE                可以被截屏
//   0x01  WDA_MONITOR             截屏里显示成黑块
//   0x11  WDA_EXCLUDEFROMCAPTURE  **整个从截屏里删掉** —— 抓屏会看到它后面的东西
//
// 所以看到 0x11 的窗口在截图里"消失"，是系统行为，不是截图工具的 bug。
//
// 编译与运行（和别的探针一样用 csc，不需要装任何东西）：
//   csc /nologo /target:exe /out:%TEMP%\aff.exe tests\affinity-probe.cs
//   %TEMP%\aff.exe
//
// 实测（2026-09-18）：微信 4.x 主窗口 class=Qt51514QWindowIcon，读出来是 0x11。
// 同一台机器上 SnapWheel 自己的轮盘窗口也是 0x11 —— 那是它故意设的，
// 不设的话轮盘会出现在你截的每一张图里。
using System;
using System.Text;
using System.Runtime.InteropServices;

class AffinityProbe
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr h, out uint aff);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    static int flagged;

    static void Main()
    {
        Console.WriteLine("affinity  pid     进程 / class / 标题");
        Console.WriteLine("--------------------------------------------------------------------------");
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            uint aff = 0;
            if (!GetWindowDisplayAffinity(h, out aff) || aff == 0) return true;   // 只看设了的

            StringBuilder t = new StringBuilder(300); GetWindowText(h, t, 300);
            StringBuilder c = new StringBuilder(300); GetClassName(h, c, 300);
            uint pid; GetWindowThreadProcessId(h, out pid);
            string pname = "";
            try { pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }

            flagged++;
            string why = aff == 0x11 ? "   <== 从截屏里整个删掉（看到的是它背后的东西）"
                       : aff == 0x01 ? "   <== 截屏里显示成黑块"
                       : "";
            Console.WriteLine("0x{0:X2}      {1,-8} {2} | {3} | {4}{5}", aff, pid, pname, c, t, why);
            return true;
        }, IntPtr.Zero);

        Console.WriteLine("--------------------------------------------------------------------------");
        Console.WriteLine("设了显示亲和性的可见窗口：" + flagged + " 个");
        Console.WriteLine();
        Console.WriteLine("上面任意一个窗口在你截图里「消失」，都是它自己的选择，不是截图工具的问题。");
        Console.WriteLine("对照验证：按 Win+Shift+S（Windows 自带截图）框住它，同样拍不到。");
    }
}
