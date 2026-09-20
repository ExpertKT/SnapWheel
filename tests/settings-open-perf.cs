// settings-open-perf.cs -- 设置窗口打开要多久（用户报"点设置后出现较慢"）。
//
// 为什么值得单独一套守着：这个 250ms 的根因**不是"某段代码慢"，而是构造里两行的先后顺序**
// （先建页再挂树 / 先挂树再建页），而且两种写法的**画面完全一样**（逐像素比零差异）——
// 也就是说，谁顺手把顺序换回去，**任何渲染测试、任何探针都看不出来**，只有用户能感觉到"卡了一下"。
// 越是"看不出来"的性能回归，越需要一条会红的断言盯着。
//
// 实测（2026-09-20，本机）：
//   先建页再挂树 = 255~280ms      ← 旧写法
//   先挂树再建页 =  28~33ms       ← 现在
// 门槛取 120ms：是实测 30ms 的 4 倍，既不会被噪声碰响，真退回去了（>250ms）一定抓得住。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.SettingsOpenPerf /out:%TEMP%\sop.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\settings-open-perf.cs
using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace SnapWheel
{
    static class SettingsOpenPerf
    {
        static int pass, fail;
        static void Check(string n, bool ok, string d)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + n); }
            else { fail++; Console.WriteLine("  [FAIL] " + n + "   " + d); }
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
            Application.SetCompatibleTextRenderingDefault(false);
            Settings.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_sop_settings.ini");

            Console.WriteLine("=== 设置窗口打开速度 ===\n");

            Settings s = new Settings();
            using (SettingsForm warm = new SettingsForm(s)) { }     // 预热：去掉 JIT 的影响

            long best = long.MaxValue, worst = 0, sum = 0;
            const int N = 7;
            for (int i = 0; i < N; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Stopwatch sw = Stopwatch.StartNew();
                SettingsForm f = new SettingsForm(s);
                long ms = sw.ElapsedMilliseconds;
                if (ms < best) best = ms;
                if (ms > worst) worst = ms;
                sum += ms;
                f.Dispose();
            }
            long avg = sum / N;
            Console.WriteLine("  {0} 次：最快 {1}ms  最慢 {2}ms  平均 {3}ms", N, best, worst, avg);
            Console.WriteLine("  （旧写法实测 255~280ms；现在是 28~33ms）\n");

            // 用**最慢的一次**判，不用平均：用户感知的是"卡的那一下"
            Check("设置窗口构造 ≤120ms（快 9 倍那条不能退回去）", worst <= 120,
                  "最慢 " + worst + "ms —— 是不是把 Controls.Add(root) 挪到 ShowPage(0) 后面了？");

            // 顺带守住"窗口尺寸是按内容算出来的"这件事没被顺序改动带偏
            using (SettingsForm f2 = new SettingsForm(s))
            {
                bool sane = f2.Width > 600 && f2.Height > 400 && f2.Width < 4000 && f2.Height < 4000;
                Check("窗口尺寸仍然合理（按内容算出来的，不是退回了出厂尺寸）", sane,
                      f2.Width + "x" + f2.Height);
            }

            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
