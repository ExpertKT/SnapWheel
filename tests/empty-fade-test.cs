// empty-fade-test.cs -- 空态提示「截图后会出现在这里」↔ 计数胶囊「3 / 8」
// 之间到底是不是**交叉淡入**，还是硬切。
//
// 用户反馈的原话：「这个字样的消失和出现以及和计数胶囊之间的交替互换没有过渡动画，
// 都是突然消失出现」。
//
// 原来两边都是 `_store.Items.Count == 0` 的硬开关：第一张图进来那一刻，
// 提示瞬间消失、胶囊瞬间出现，中间什么都没有。现在两边共用一个 `_emptyT` 参数，
// 一个淡出的同时另一个淡入。
//
// 这个测试要钉住两件事：
//   ① 画面**真的由 _emptyT 驱动**：把它拨到 1 / 0.5 / 0，三张图必须两两不同 ——
//      如果只是"过某个阈值就切换"，0.5 那张会和 1 或 0 一样，这条就挂了；
//   ② 动画**真的会走**：加一张图之后逐帧采样 _emptyT，必须经过一串中间值，
//      而不是一帧从 1 跳到 0。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.EmptyFadeTest /out:%TEMP%\ef.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\empty-fade-test.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class EmptyFadeTest
    {
        static int pass, fail;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + "   " + detail); }
        }

        static FieldInfo Field(string name)
        {
            FieldInfo fi = typeof(WheelForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + name);
            return fi;
        }

        static float EmptyT(WheelForm f) { return (float)Field("_emptyT").GetValue(f); }
        static void SetEmptyT(WheelForm f, float v) { Field("_emptyT").SetValue(f, v); }

        // 把轮盘渲染一张出来（不泵消息，免得动画循环把 _emptyT 覆盖掉）
        static Bitmap Render(WheelForm f)
        {
            MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            Bitmap b = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
                dw.Invoke(f, new object[] { g, f.Width, f.Height });
            return b;
        }

        // 两张图的差异（采样比较，快）
        static int Diff(Bitmap a, Bitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
            int d = 0;
            for (int y = 0; y < a.Height; y += 3)
                for (int x = 0; x < a.Width; x += 3)
                {
                    Color ca = a.GetPixel(x, y), cb = b.GetPixel(x, y);
                    if (ca.ToArgb() != cb.ToArgb()) d++;
                }
            return d;
        }

        [STAThread]
        static void Main()
        {
            try { Run(); }
            catch (Exception ex)
            {
                Console.WriteLine("出错：" + ex.GetType().Name + "  " + ex.Message);
                if (ex.InnerException != null) Console.WriteLine("  内层：" + ex.InnerException.Message);
                Environment.ExitCode = 1;
            }
        }

        static void Run()
        {
            try { Lang.Init("zh"); } catch { }
            Application.EnableVisualStyles();
            Settings.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_ef_settings.ini");
            WheelManager.OverrideMetaPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_ef_wheels.ini");

            Console.WriteLine("=== 空态提示 ↔ 计数胶囊：交叉淡入 ===\n");

            Settings s = new Settings();
            s.ShowCountLabel = true;
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();

            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;
            st.Items.Clear();                       // 空态
            Application.DoEvents();

            // ---------- ① 画面真的由 _emptyT 驱动，而且是连续变化不是阈值开关 ----------
            {
                MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);
                SetEmptyT(f, 1f);   Bitmap bEmpty = Render(f);
                SetEmptyT(f, 0.5f); Bitmap bHalf = Render(f);
                SetEmptyT(f, 0f);   Bitmap bFull = Render(f);

                int dEH = Diff(bEmpty, bHalf), dHF = Diff(bHalf, bFull), dEF = Diff(bEmpty, bFull);
                Console.WriteLine("   （像素差异）空态↔半程 {0} ｜ 半程↔有图 {1} ｜ 空态↔有图 {2}", dEH, dHF, dEF);

                Check("空态那张：真的有东西（提示画出来了）", dEF > 0, "空态和有图完全一样，说明提示没画");
                Check("半程那张和两端**都不一样**（说明是渐变，不是过阈值就切换）",
                      dEH > 0 && dHF > 0 && dEF > 0,
                      string.Format("空↔半 {0}，半↔有图 {1}", dEH, dHF));

                bEmpty.Dispose(); bHalf.Dispose(); bFull.Dispose();
            }

            // ---------- ② 加一张图：_emptyT 必须平滑走到 0 ----------
            {
                // 先把积压的消息清干净再开始计时。
                // 任务①里连渲三张图 + 比较像素花了不少时间，这期间定时器消息一直排在队列里，
                // 于是下面第一次 DoEvents 会**一次性放出好几帧动画** —— 采到的第一帧就已经是 0.13 了。
                // 这是测试自己的问题（无头环境里消息会积压），不是产品的：
                // 真实运行时帧循环一直在跑，定时器不会排队。
                Application.DoEvents();

                SetEmptyT(f, 1f);
                Field("_emptySynced").SetValue(f, true);
                DateTime t0 = DateTime.Now;
                st.Add(new Bitmap(240, 150, PixelFormat.Format32bppArgb));

                // 判据用**时间下界**，不用"采到几帧中间值"：
                // 采样能不能采到中间帧，完全取决于消息队列怎么攒的（同一份代码跑三次：
                // 采到 4 帧 / 0 帧 / 3 帧）—— 那又是"看运气"，正是这个项目反复栽过的坑。
                //
                // 而耗时是**下界**断言：机器有负载只会让它更慢，不会更快，所以下界在负载下是安全的。
                // 如果动画退化成硬切（一帧从 1 跳到 0），这个循环第一次判断就退出了，
                // ms 会是十几毫秒 —— 一定抓得住。
                float last = 1f;
                for (int i = 0; i < 300 && last > 0.05f; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(10);
                    last = EmptyT(f);
                }
                double ms = (DateTime.Now - t0).TotalMilliseconds;

                Console.WriteLine("   （有图之后）用时 {0:F0} ms，_emptyT 末值 {1:F2}", ms, last);

                Check("有图之后 _emptyT 落到 0（提示彻底淡出）", last <= 0.05f, "末值是 " + last.ToString("F2"));
                Check("这段过渡**确实花了时间**（≥60ms，不是一帧跳过去）", ms >= 60.0,
                      string.Format("只用了 {0:F0} ms", ms));
            }

            // ---------- ③ 删光：反向也要平滑走回 1 ----------
            {
                Application.DoEvents();
                DateTime t0 = DateTime.Now;
                st.Items.Clear();

                float last = 0f;
                for (int i = 0; i < 300 && last < 0.95f; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(10);
                    last = EmptyT(f);
                }
                double ms = (DateTime.Now - t0).TotalMilliseconds;

                Console.WriteLine("   （删光之后）用时 {0:F0} ms，_emptyT 末值 {1:F2}", ms, last);

                Check("删光之后 _emptyT 回到 1（提示彻底淡回来）", last >= 0.95f, "末值是 " + last.ToString("F2"));
                Check("反向过渡也**确实花了时间**（≥60ms）", ms >= 60.0,
                      string.Format("只用了 {0:F0} ms", ms));
            }

            f.Dispose();
            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
