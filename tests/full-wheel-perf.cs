// full-wheel-perf.cs -- 环上塞满 50 张图的时候，一帧到底要多久。
//
// 为什么要单独量这个：ROADMAP 里那 931 条真实帧耗时样本，**全是环上 1~3 张图**的状态
// （那是用户当时的实际用量）。而 MaxCount 默认是 50。满环是什么样，从来没量过 ——
// 这不是已知短板，是**未知**，未知比短板更该先清掉。
//
// 测的是真实绘制路径：反复调 DrawWheel（= RenderCore 里那一步，含分层缓存的 TryBlitLayer/StoreLayer），
// 输出到一张位图，只量绘制本身。UpdateLayeredWindow 那一下是常数开销，不在这里重复计入。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.FullWheelPerf /out:%TEMP%\fw.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\full-wheel-perf.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class FullWheelPerf
    {
        const int Items = 50;        // = 设置里的 MaxCount 默认值
        const int Frames = 120;      // 每种状态量这么多帧（后 80 帧计入，前 40 帧当预热）
        const int Warm = 40;

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

        static double Measure(WheelForm f, MethodInfo dw, string label, Action setup)
        {
            List<double> all = new List<double>();
            using (Bitmap buf = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppPArgb))
            {
                for (int i = 0; i < Frames; i++)
                {
                    if (setup != null) setup();
                    Stopwatch sw = Stopwatch.StartNew();
                    using (Graphics g = Graphics.FromImage(buf))
                        dw.Invoke(f, new object[] { g, f.Width, f.Height });
                    sw.Stop();
                    all.Add(sw.Elapsed.TotalMilliseconds);
                    Application.DoEvents();
                }
            }
            List<double> hot = all.GetRange(Warm, all.Count - Warm);
            hot.Sort();
            double sum = 0; foreach (double d in hot) sum += d;
            double avg = sum / hot.Count;
            double p50 = hot[hot.Count / 2];
            double p95 = hot[(int)(hot.Count * 0.95)];
            double max = hot[hot.Count - 1];
            Console.WriteLine("  {0,-22} 平均 {1,6:F2}ms   p50 {2,6:F2}   p95 {3,6:F2}   最慢 {4,7:F2}",
                label, avg, p50, p95, max);
            if (avg > 20.0) Console.WriteLine("      ^^ 超 20ms 预算（约 50fps 以下）");
            return avg;
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
            Settings.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_fw_settings.ini");
            WheelManager.OverrideMetaPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_fw_wheels.ini");

            Console.WriteLine("=== 满环性能（" + Items + " 张图，每态 " + Frames + " 帧，取后 " + (Frames - Warm) + " 帧）===\n");

            Settings s = new Settings();
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();

            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;

            double steady = 0;

            // 一张 1920x1080 的"截图"给 50 条记录共用：
            // 真实用户是 50 张不同的图，但**绘制的开销取决于尺寸和条数，不取决于内容**；
            // 共用一张能把内存从 400MB 压下来，量出来的数一样。
            using (Bitmap big = new Bitmap(1920, 1080, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.Clear(Color.FromArgb(32, 40, 56));
                    for (int i = 0; i < 60; i++)
                        using (Pen p = new Pen(Color.FromArgb(90, 255, 255, 255), 2f))
                            g.DrawLine(p, 0, i * 18, 1920, i * 18 + 30);
                }
                for (int i = 0; i < Items; i++) st.Add(big);
                Console.WriteLine("  已放入 " + st.Items.Count + " 张（" + big.Width + "x" + big.Height + "）\n");

                MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);

                // 预热：第一遍要把 50 张缩略图全部现生成出来 —— 那是冷启动的一次性成本，
                // 单独量出来，因为用户能感觉到它（开机第一帧 / 截图后第一帧）。
                S(f, "_show", 1f); S(f, "_intro", false); S(f, "_collapsed", false);
                double cold;
                using (Bitmap buf = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppPArgb))
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    using (Graphics g = Graphics.FromImage(buf))
                        dw.Invoke(f, new object[] { g, f.Width, f.Height });
                    sw.Stop();
                    cold = sw.Elapsed.TotalMilliseconds;
                }
                Console.WriteLine("  冷启动第一帧（要现生成 50 张缩略图）：{0:F0} ms\n", cold);

                // 先滚一整轮把每张的缩略图都逼出来，再量 —— 这样可以区分两种可能：
                //   · 尖峰来自"新图滚进视野时才现生成缩略图"（预热后就该消失）；
                //   · 尖峰来自滚动这条路本身（预热后照样在）。
                // 不下这一步就只能靠猜。
                {
                    S(f, "_hover", -1); S(f, "_enlarged", -1);
                    using (Bitmap buf = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppPArgb))
                    {
                        for (int k = 0; k <= 20; k++)
                        {
                            S(f, "_targetOffset", (float)k);
                            S(f, "_offset", (float)k);
                            using (Graphics g = Graphics.FromImage(buf))
                                dw.Invoke(f, new object[] { g, f.Width, f.Height });
                        }
                        // 回到底部
                        S(f, "_targetOffset", 0f); S(f, "_offset", 0f);
                        using (Graphics g = Graphics.FromImage(buf))
                            dw.Invoke(f, new object[] { g, f.Width, f.Height });
                    }
                    Console.WriteLine("  （已先滚一整轮预热，把 50 张的缩略图全部逼出来）\n");
                }

                steady = Measure(f, dw, "稳态（无悬停）", delegate { S(f, "_hover", -1); S(f, "_enlarged", -1); });
                Measure(f, dw, "悬停一张", delegate { S(f, "_hover", 25); S(f, "_enlarged", -1); });
                Measure(f, dw, "长按放大（peek）", delegate { S(f, "_hover", 25); S(f, "_enlarged", 25); });

                // 滚动中：每帧改一次偏移。注意 _offset 是"当前"，_targetOffset 是"目标"，
                // 真实动画里 _offset 会追过去；这里直接调 DrawWheel 不走 AnimTick，
                // 所以两个都得推，否则画面根本不动，量的是假的。
                int dir = 1;
                Measure(f, dw, "滚动中", delegate
                {
                    float cur = Convert.ToSingle(G(f, "_targetOffset"));
                    cur += dir * 0.25f;
                    if (cur > 20f) dir = -1;
                    if (cur < 0f) dir = 1;
                    S(f, "_targetOffset", cur);
                    S(f, "_offset", cur);
                });
            }

            f.Dispose();
            Console.WriteLine();
            Console.WriteLine("（参考：项目里的预算线是**每帧 ≤20ms**，即 50fps 以上；60fps 的预算是 16.7ms）");

            // 判定留 4 倍余量（实测稳态 ~3ms）：
            // 绝对毫秒在负载下会抖，但 12ms 这个门槛既不会被噪声碰响，
            // 真退化了（比如不小心把贴片缓存全废掉）一定抓得住。
            Console.WriteLine();
            if (steady <= 12.0)
            {
                Console.WriteLine("  [OK]   满环稳态平均 {0:F2}ms ≤ 12ms（预算 20ms）", steady);
                Console.WriteLine("通过 1 / 失败 0");
            }
            else
            {
                Console.WriteLine("  [FAIL] 满环稳态平均 {0:F2}ms 超过 12ms 门槛（预算 20ms）", steady);
                Console.WriteLine("通过 0 / 失败 1");
                Environment.ExitCode = 1;
            }
        }
    }
}
