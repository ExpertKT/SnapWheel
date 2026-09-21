// capture-visible-test.cs -- 录屏能不能拍到轮盘（v1.0 演示模式）。
//
// 用户报："我想录视频演示功能，但是轮盘在录制中不出现，截图界面都有但是轮盘没有。"
//
// 根因：WheelForm 建句柄时调了 SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)，
// 让轮盘对屏幕捕获隐身（好处是自己截图时不会把轮盘截进去）。
// 但那个 API 在 Windows 10 2004+ 上对**所有**基于 Windows.Graphics.Capture 的捕获都生效 ——
// **录屏也算**。截图浮层是普通窗口、没设这个标记，所以能录进去 —— 现象完全对得上。
//
// 这条检查盯四件事：
//   ① 默认（演示模式关）→ 窗口确实是"对捕获隐身"的状态
//   ② 打开演示模式 → 真的变成可被捕获
//   ③ **改完立刻生效**（不用重启）—— 用户是在设置里点一下就要去录的
//   ④ 设置能存能读（重启之后还在）
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.CaptureVisibleTest /out:%TEMP%\cv.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\capture-visible-test.cs
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapWheel
{
    static class CaptureVisibleTest
    {
        [DllImport("user32.dll")]
        static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

        const uint WDA_NONE = 0x00, WDA_EXCLUDEFROMCAPTURE = 0x11;

        static int pass, fail;
        static void Check(string n, bool ok, string d)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + n); }
            else { fail++; Console.WriteLine("  [FAIL] " + n + "   " + d); }
        }
        static object G(object o, string n)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            return fi.GetValue(o);
        }
        static string Affinity(IntPtr h)
        {
            uint a;
            if (!GetWindowDisplayAffinity(h, out a)) return "读不到";
            if (a == WDA_NONE) return "可被捕获";
            if (a == WDA_EXCLUDEFROMCAPTURE) return "对捕获隐身";
            return "0x" + a.ToString("X");
        }

        [STAThread]
        static void Main()
        {
            try { Run(); }
            catch (Exception ex)
            {
                Console.WriteLine("出错：" + ex.GetType().Name + "  " + ex.Message);
                Environment.ExitCode = 1;
            }
        }

        static void Run()
        {
            try { Lang.Init("zh"); } catch { }
            Application.EnableVisualStyles();
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_cv");
            System.IO.Directory.CreateDirectory(tmp);
            Settings.OverridePath = System.IO.Path.Combine(tmp, "settings.ini");
            WheelManager.OverrideMetaPath = System.IO.Path.Combine(tmp, "wheels.ini");

            Console.WriteLine("=== 录屏能不能拍到轮盘 ===\n");

            Settings s = new Settings();
            s.Recordable = false;
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();

            string def = Affinity(f.Handle);
            Console.WriteLine("  默认状态：" + def);
            Check("① 默认对屏幕捕获隐身（所以自己截图不会带上轮盘）", def == "对捕获隐身", def);

            // ③ 改完立刻生效
            s.Recordable = true;
            f.ApplyCaptureVisibility();
            Application.DoEvents();
            string on = Affinity(f.Handle);
            Console.WriteLine("  演示模式：" + on);
            Check("② 打开演示模式 -> 真的能被捕获了（录屏才拍得到）", on == "可被捕获", on);
            Check("③ 改完立刻生效，不用重启", on == "可被捕获", on);

            // 关回去
            s.Recordable = false;
            f.ApplyCaptureVisibility();
            Application.DoEvents();
            Check("③ 关回去也能立刻恢复隐身", Affinity(f.Handle) == "对捕获隐身", Affinity(f.Handle));

            // ④ 存盘往返
            s.Recordable = true;
            s.Save();
            Settings back = Settings.Load();
            Check("④ 设置能存能读（重启后演示模式还在）", back.Recordable, "读回来是 " + back.Recordable);

            f.Dispose();
            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
