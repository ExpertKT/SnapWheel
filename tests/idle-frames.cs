// idle-frames.cs -- 空转的轮盘，10 秒到底画了几帧。
//
// 这是怎么查出来的（值得记下来，因为前两次都找错了地方）：
//
//   线索：error.log 里 49/92 条帧统计平均 >20ms，而状态是 图=0 / hover=-1 / 没在动画。
//   第一次猜：玻璃底每 3.5 秒重抓一次屏幕（代码注释自己写着"抓屏+模糊要几十毫秒"）。
//             于是写了个"先采几个小块判断桌面变了没有"的优化 —— **量出来发现是反的**：
//             CopyFromScreen 12x12 要 16.7ms，整屏要 44.6ms —— **抓屏是固定成本，跟大小无关**，
//             采四块比整屏还贵。这个方案当场作废（见 ROADMAP 里的记录）。
//   第二次猜：主题色过渡 `_accentCur != want` 收敛不了 —— 那确实是**真 bug**
//             （只差 1 的时候 Round 会让它永远停住），修了，但它不是主因。
//   第三次才对：往每个 `need = true` 旁边插了一行记录行号，直接问程序"谁在要帧" ——
//             答案是 `_nubHintT > 0.01f` 这类**"悬停期间保持刷新"**。
//             提示/光晕**已经完全显示、一个像素都不再变**了，却还在每帧要求重画。
//
//   实测：修之前空转 45fps（10 秒 450 帧），修之后 7.1fps（关掉玻璃定时刷新 4.5fps）。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.IdleFrames /out:%TEMP%\if.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\idle-frames.cs
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class IdleFrames
    {
        static int pass, fail;

        static object G(object o, string n)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            return fi.GetValue(o);
        }
        static void S(object o, string n, object v)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            fi.SetValue(o, v);
        }

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + "   " + detail); }
        }

        static double Measure(WheelForm f, FieldInfo rc, double seconds, string label)
        {
            // 先把设置余波放掉
            for (int i = 0; i < 30; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
            long before = Convert.ToInt64(rc.GetValue(null));
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(1);
            }
            long after = Convert.ToInt64(rc.GetValue(null));
            sw.Stop();
            double fps = (after - before) / sw.Elapsed.TotalSeconds;
            Console.WriteLine("  {0,-24} {1:F1} 秒 {2,4} 帧  →  {3,5:F1} fps", label, sw.Elapsed.TotalSeconds, after - before, fps);
            return fps;
        }

        [STAThread]
        static void Main()
        {
            try { Run(); }
            catch (Exception ex)
            {
                Console.WriteLine("出错：" + ex.GetType().Name + "  " + ex.Message);
                if (ex.InnerException != null) Console.WriteLine("  内层：" + ex.InnerException.Message);
                Console.WriteLine("通过 {0} / 失败 {1}", pass, fail + 1);
                Environment.ExitCode = 1;
            }
        }

        static void Run()
        {
            try { Lang.Init("zh"); } catch { }
            Application.EnableVisualStyles();
            Settings.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_if_settings.ini");
            WheelManager.OverrideMetaPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_if_wheels.ini");

            Console.WriteLine("=== 空转时到底画了几帧 ===\n");

            Settings s = new Settings();
            s.GlassRefresh = true;
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();

            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;
            st.Items.Clear();

            // 推到"完全稳态"：显示完成、没有入场动画、没收起
            S(f, "_show", 1f); S(f, "_targetShow", 1f); S(f, "_showAnimating", false);
            S(f, "_intro", false); S(f, "_collapsing", false); S(f, "_collapsed", false);
            S(f, "_nubAppearT", 1f);
            S(f, "_hover", -1); S(f, "_enlarged", -1); S(f, "_peekIndex", -1);
            Application.DoEvents();

            FieldInfo rc = typeof(WheelForm).GetField("RenderCountForTest",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (rc == null) { Console.WriteLine("找不到 RenderCountForTest"); Environment.ExitCode = 1; return; }
            if (!rc.IsStatic) { Console.WriteLine("RenderCountForTest 不是静态的"); Environment.ExitCode = 1; return; }

            // 注意：这里**故意不**把 _nubHintT 压到 0。
            // 首次运行时把手提示会亮 14 秒，而"提示亮着"正是当年那个 45fps 的现场 ——
            // 压掉它就把要测的东西藏起来了。
            double withGlass = Measure(f, rc, 10.0, "玻璃定时刷新开着");
            s.GlassRefresh = false;
            double withoutGlass = Measure(f, rc, 6.0, "玻璃定时刷新关掉");

            Console.WriteLine();
            Console.WriteLine("  （修之前：开着 45.0 fps / 关着 22.8 fps）");
            Console.WriteLine();

            Check("空转帧率正常（开着 ≤15fps；修之前是 45）", withGlass <= 15.0,
                  string.Format("{0:F1} fps", withGlass));
            Check("关掉玻璃刷新后基本不动（≤10fps；修之前是 22.8）", withoutGlass <= 10.0,
                  string.Format("{0:F1} fps", withoutGlass));

            f.Dispose();
            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
