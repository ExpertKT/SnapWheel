// 动画性能基准：把轮盘的几种动画"跑一遍并计时"，用来给"掉帧卡顿"定量。
//
// 为什么不模拟鼠标：铁律里写着不许模拟输入。这里全部走内部方法（AnimTickCore / CollapseWheel …），
// 通过反射驱动，屏幕上看不到鼠标动，也不会抢用户的焦点。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.PerfBench /out:perf.exe src\*.cs tests\perf-bench.cs
// 运行（想看分段耗时就把 SNAPWHEEL_PERF 设成 1）：
//   $env:SNAPWHEEL_PERF=1 ; & .\perf.exe
//
// 验收口径（0.5.2 定的）：动画/交互全程每帧 ≤ 20ms，且不出现 > 40ms 的帧。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SnapWheel
{
    static class PerfBench
    {
        const double LimitMs = 20.0;      // 每帧目标
        const double HardMs = 40.0;       // 一帧超过这个数就是"看得出的一顿"

        static void F(object o, string n, object v)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            fi.SetValue(o, v);
        }
        static object G(object o, string n)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            return fi == null ? null : fi.GetValue(o);
        }
        static object Call(object o, string n, params object[] a)
        {
            // 同名重载（比如 StartIntro 有无参和带参两个）会 AmbiguousMatch，按参数个数挑一个
            MethodInfo[] all = o.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            MethodInfo m = null;
            for (int i = 0; i < all.Length; i++)
                if (all[i].Name == n && all[i].GetParameters().Length == a.Length) { m = all[i]; break; }
            if (m == null) throw new Exception("找不到方法 " + n + "（" + a.Length + " 个参数）");
            return m.Invoke(o, a);
        }

        static Bitmap Solid(int w, int h, Color c)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(c);
                using (Font f = new Font("Microsoft YaHei UI", Math.Max(8f, h / 8f), FontStyle.Bold))
                using (SolidBrush br = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    g.DrawString(w + "x" + h, f, br, 4, 4);
            }
            return b;
        }

        class Stats
        {
            public string Name;
            public List<double> Frames = new List<double>();
            public void Add(double ms) { Frames.Add(ms); }
            public double Avg() { double s = 0; for (int i = 0; i < Frames.Count; i++) s += Frames[i]; return Frames.Count == 0 ? 0 : s / Frames.Count; }
            public double Pct(double p)
            {
                if (Frames.Count == 0) return 0;
                List<double> c = new List<double>(Frames); c.Sort();
                int i = (int)Math.Min(c.Count - 1, Math.Round((c.Count - 1) * p));
                return c[i];
            }
            public double Max() { double m = 0; for (int i = 0; i < Frames.Count; i++) if (Frames[i] > m) m = Frames[i]; return m; }
            public int Over(double ms) { int n = 0; for (int i = 0; i < Frames.Count; i++) if (Frames[i] > ms) n++; return n; }
            public bool Ok { get { return Pct(0.95) <= LimitMs && Over(HardMs) == 0; } }
        }

        static readonly List<Stats> All = new List<Stats>();

        // 一帧 = 调一次 AnimTickCore（里面会按需 Render）。pace 是"假装 60fps"的间隔。
        static Stats Run(string name, WheelForm f, Func<bool> step, int maxFrames, int paceMs)
        {
            Stats st = new Stats();
            st.Name = name;
            for (int w = 0; w < 2; w++) { step(); if (paceMs > 0) Thread.Sleep(paceMs); }   // 预热两帧：不计入
            for (int i = 0; i < maxFrames; i++)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool more = step();
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                st.Add((t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                if (!more) break;
                if (paceMs > 0) Thread.Sleep(paceMs);
            }
            All.Add(st);
            return st;
        }

        static void Line(Stats s)
        {
            Console.WriteLine("  {0,-22} 帧 {1,4}  平均 {2,5:0.0}ms  p50 {3,5:0.0}  p95 {4,5:0.0}  最慢 {5,6:0.0}  >20ms {6,3}  >40ms {7,3}  {8}",
                s.Name, s.Frames.Count, s.Avg(), s.Pct(0.50), s.Pct(0.95), s.Max(), s.Over(LimitMs), s.Over(HardMs),
                s.Ok ? "OK" : "★超标");
        }

        [STAThread]
        static void Main(string[] args)
        {
            try { Bench(args); }
            catch (Exception ex)
            {
                // 反射里抛出来的异常有时连 ToString() 都会炸，这里手动拼一条能看的
                string msg;
                try { msg = ex.GetType().FullName + ": " + ex.Message; } catch { msg = "(异常信息都读不出来)"; }
                Console.WriteLine("基准跑挂了：" + msg);
                try { Console.WriteLine(ex.StackTrace); } catch { }
                Exception inner = null;
                try { inner = ex.InnerException; } catch { }
                if (inner != null)
                {
                    try { Console.WriteLine("内层：" + inner.GetType().FullName + ": " + inner.Message + "\r\n" + inner.StackTrace); } catch { }
                }
            }
        }

        static void Bench(string[] args)
        {
            Application.EnableVisualStyles();
            // 基准要的是"这台机器空载时能跑多快"，别被后台的杀毒/浏览器抖动污染：
            // 抬一点自己的优先级，并且每条阶段跑之前先空转两帧预热（首帧有 JIT 和缓存构建）。
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; } catch { }
            string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_perf");   // 固定目录，方便跑完翻 Perf 报告
            Directory.CreateDirectory(tmp);
            Settings.OverridePath = Path.Combine(tmp, "settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(tmp, "wheels.ini");
            Err.OverridePath = Path.Combine(tmp, "error.log");

            int n = 8;
            if (args.Length > 0) { int t; if (int.TryParse(args[0], out t)) n = t; }

            Settings s = new Settings();
            s.SaveToDisk = false;
            // 第二个参数 = 手动界面缩放（用来模拟高 DPI：他真实屏幕是 125%，窗口会大一圈、绘制更贵）
            if (args.Length > 1) { int sc; if (int.TryParse(args[1], out sc)) s.UiScale = sc; }
            WheelManager mgr = new WheelManager(s);
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;
            Random rnd = new Random(7);
            for (int i = 0; i < n; i++)
                st.Add(Solid(200 + rnd.Next(600), 150 + rnd.Next(400),
                    Color.FromArgb(255, 60 + rnd.Next(180), 60 + rnd.Next(180), 60 + rnd.Next(180))));

            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();
            Thread.Sleep(400);                 // 让开启动画先跑一段

            Console.WriteLine("--- 动画性能基准（{0} 张图，窗口 {1}x{2}，UiK={3:0.00}，DPI={4}%）---",
                n, f.Width, f.Height, G(f, "UiK"), (int)(100 * Convert.ToSingle(G(f, "UiK"))));
            Console.WriteLine("口径：每帧 ≤ {0:0}ms、且没有 > {1:0}ms 的帧", LimitMs, HardMs);
            Console.WriteLine();

            Perf.Reset();

            // 1) 开启动画（环扫出 + 图片排队滑落）
            Call(f, "StartIntro");
            Run("开启动画", f, delegate { Call(f, "AnimTickCore"); return Convert.ToSingle(G(f, "_introT")) < 0.999f; }, 400, 15);

            // 2) 收起 → 展开（都按 _show 这个"看得见多少"的量来驱动，别用 IsCollapsed 这类目标值）
            f.CollapseWheel(true);
            Run("收起", f, delegate { Call(f, "AnimTickCore"); return Convert.ToSingle(G(f, "_show")) > 0.02f; }, 400, 15);
            Call(f, "ExpandWheel");
            Run("展开", f, delegate { Call(f, "AnimTickCore"); return Convert.ToSingle(G(f, "_show")) < 0.995f; }, 400, 15);

            // 3) 切换 Wheel 的闪光 + 滚动
            Call(f, "ExpandWheel");
            for (int i = 0; i < 200 && Convert.ToSingle(G(f, "_show")) < 0.99f; i++) { Call(f, "AnimTickCore"); Thread.Sleep(8); }
            F(f, "_switchFlash", 1f);
            Run("切盘闪光", f, delegate { Call(f, "AnimTickCore"); return Convert.ToSingle(G(f, "_switchFlash")) > 0.01f; }, 200, 15);

            f.RefreshWheel();
            // RefreshWheel 会放一次切盘闪光，等它散掉再测（否则静态层缓存一直被它挡着，数据不真实）
            for (int i = 0; i < 200 && Convert.ToSingle(G(f, "_switchFlash")) > 0.01f; i++) { Call(f, "AnimTickCore"); Thread.Sleep(8); }
            F(f, "_targetOffset", (float)(st.Items.Count - 1));
            Run("滚动翻图", f, delegate
            {
                Call(f, "AnimTickCore");
                return Math.Abs(Convert.ToSingle(G(f, "_offset")) - Convert.ToSingle(G(f, "_targetOffset"))) > 0.02f;
            }, 400, 15);

            // 4) 悬停 / 放大预览（放大那张要额外画一次，是最费的一档）
            f.RefreshWheel();
            F(f, "_hover", 2);
            F(f, "_enlarged", 2);
            Perf.Reset();
            Run("悬停+放大预览", f, delegate { Call(f, "AnimTickCore"); return true; }, 60, 15);
            Perf.Report("阶段：悬停+放大预览");

            // 5) 毛玻璃换底：抓屏在后台线程，但换上来的那 0.38 秒交叉淡入是每帧整窗重绘
            f.RefreshWheel();
            for (int i = 0; i < 200 && Convert.ToSingle(G(f, "_switchFlash")) > 0.01f; i++) { Call(f, "AnimTickCore"); Thread.Sleep(8); }
            Call(f, "RequestBackdropAsync");
            for (int i = 0; i < 60 && G(f, "_backdropOld") == null; i++) { Call(f, "AnimTickCore"); Thread.Sleep(10); }
            Perf.Reset();
            Run("玻璃换底+交叉淡入", f, delegate
            {
                Call(f, "AnimTickCore");
                return G(f, "_backdropOld") != null;
            }, 300, 15);
            Perf.Report("阶段：玻璃换底交叉淡入");

            // 6) 实时节奏：不手动驱动，让真实的 15ms 定时器跑 12 秒，然后看日志里的 [Frame] 行
            Line(All[All.Count - 1]);
            Console.WriteLine();
            Console.WriteLine("实时节奏（真实定时器驱动 12 秒，读 error.log 里 FrameStats 的统计）：");
            long mark = Err.LogPath() != null && File.Exists(Err.LogPath()) ? new FileInfo(Err.LogPath()).Length : 0;
            f.RefreshWheel();
            F(f, "_hover", 1);
            DateTime until = DateTime.Now.AddSeconds(12);
            while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(5); }
            try
            {
                string[] lines = File.ReadAllLines(Err.LogPath());
                for (int i = lines.Length - 1; i >= 0 && i > lines.Length - 6; i--)
                    if (lines[i].IndexOf("[Frame]") >= 0) Console.WriteLine("  " + lines[i].Trim());
            }
            catch { }

            Console.WriteLine();
            Console.WriteLine("汇总（每帧 ≤{0:0}ms 且无 >{1:0}ms 帧 = 达标）：", LimitMs, HardMs);
            int bad = 0;
            for (int i = 0; i < All.Count; i++) { Line(All[i]); if (!All[i].Ok) bad++; }
            Console.WriteLine(bad == 0 ? "  全部达标" : "  有 " + bad + " 项超标");
            Perf.Report("基准 " + All.Count + " 项（" + n + " 张图）");
            // 不删临时目录：Perf 报告和 [Frame] 行都在里面，跑完要看
        }
    }
}
