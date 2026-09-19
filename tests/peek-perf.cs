// peek-perf.cs -- 量一下"大图第一次长按放大"到底卡在哪，以及补的那张中转图有没有用。
//
// 背景（GitHub issue #2：大图的缩略图第一次长按放大时有可能出现动画掉帧）：
//   放大动画里每一帧的尺寸都不同，ScaledThumb 的缓存按尺寸存，
//   所以帧帧未命中。未命中时它会去找"缓存里比目标大的现成缩略图"当源，
//   找不到才退回原图 —— 大图上从 2560x1440 做一次高质量双三次就是几十毫秒。
//
//   而"找更大的现成图"这条路，原来挂在「目标是否超过原图」这个条件上，
//   大图放大后仍然小于原图宽（2560 → 346），条件不成立，于是每帧都退回原图。
//
// 这个探针直接对真实的 ScaledThumb 做 A/B：
//   冷缓存 → 模拟一段放大动画（尺寸从卡片尺寸长到 peek 尺寸）→ 计时
//   分别用 animate=false（旧行为）和 animate=true（新行为，会先做一张中转图）
//
// 编译（跟别的探针一样）：
//   csc /nologo /target:exe /main:SnapWheel.PeekPerf /out:%TEMP%\peek.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\peek-perf.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class PeekPerf
    {
        static object F(object o, string name)
        {
            FieldInfo fi = o.GetType().GetField(name,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + name);
            return fi.GetValue(o);
        }

        const int Frames = 40;      // 一段放大动画大约这么多帧

        // 冷缓存跑一遍"放大动画"：尺寸从卡片尺寸线性长到 peek 尺寸
        static double Run(WheelForm f, StoreItem it, MethodInfo scaled, int cardW, int cardH, int peekW, int peekH, bool animating)
        {
            // 每次从冷缓存开始，只保留卡片尺寸那张（真实第一次放大时的状态）
            Dictionary<StoreItem, Dictionary<long, Bitmap>> cache =
                (Dictionary<StoreItem, Dictionary<long, Bitmap>>)F(f, "_thumbCache");
            foreach (var kv in cache.Values) foreach (Bitmap b in kv.Values) { try { b.Dispose(); } catch { } }
            cache.Clear();

            int[] warm = new int[2];
            scaled.Invoke(f, new object[] { it, cardW, cardH, false });   // 先把卡片尺寸那张做出来

            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < Frames; i++)
            {
                float t = (float)i / (Frames - 1);
                int w = (int)Math.Round(cardW + (peekW - cardW) * t);
                int h = (int)Math.Round(cardH + (peekH - cardH) * t);
                scaled.Invoke(f, new object[] { it, w, h, animating });
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        [STAThread]
        static void Main()
        {
            try { Run(); }
            catch (Exception ex)
            {
                Console.WriteLine("探针出错：" + ex.GetType().Name + "  " + ex.Message);
                if (ex.InnerException != null)
                    Console.WriteLine("  内层：" + ex.InnerException.GetType().Name + "  " + ex.InnerException.Message);
                Environment.ExitCode = 1;
            }
        }

        static void Run()
        {
            try { Lang.Init("zh"); } catch { }
            Application.EnableVisualStyles();

            Settings.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_peek_settings.ini");
            WheelManager.OverrideMetaPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_peek_wheels.ini");

            Console.WriteLine("=== 大图放大的缩放开销（冷缓存，模拟 " + Frames + " 帧动画）===\n");

            Settings s = new Settings();
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();                       // 和 render-smoke 一样：不先 ShowWheel 窗体没初始化好
            Application.DoEvents();

            // 造一张 2560x1440 的"大图"（用户报的那种尺寸）
            using (Bitmap big = new Bitmap(2560, 1440, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.Clear(Color.DarkSlateBlue);
                    for (int i = 0; i < 40; i++)
                        using (Pen p = new Pen(Color.FromArgb(60, 255, 255, 255), 3f))
                            g.DrawLine(p, 0, i * 40, 2560, i * 40 + 40);
                }

                Store store = mgr.ActiveStore;
                store.SaveToDisk = false;
                StoreItem it = store.Add(big);
                MethodInfo scaled = typeof(WheelForm).GetMethod("ScaledThumb",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                MethodInfo cardSize = typeof(WheelForm).GetMethod("CardSize",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                SizeF cs = (SizeF)cardSize.Invoke(f, new object[] { it });
                float uik = (float)F(f, "UiK");
                int cardW = (int)Math.Round(cs.Width), cardH = (int)Math.Round(cs.Height);
                float peek = (float)typeof(WheelForm)
                    .GetProperty("PeekScale", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(f, null);
                int peekW = (int)Math.Round(cs.Width * peek), peekH = (int)Math.Round(cs.Height * peek);

                Console.WriteLine("  原图 2560x1440   卡片 {0}x{1}（逻辑）  peek x{2:F1} → {3}x{4}   UiK={5:F2}",
                    cardW, cardH, peek, peekW, peekH, uik);
                Console.WriteLine();

                double oldMs = Run(f, it, scaled, cardW, cardH, peekW, peekH, false);
                double newMs = Run(f, it, scaled, cardW, cardH, peekW, peekH, true);

                Console.WriteLine("  旧行为（animating=false，每帧退回原图）: {0,8:F1} ms   平均 {1:F1} ms/帧", oldMs, oldMs / Frames);
                Console.WriteLine("  新行为（animating=true，先做一张中转图）: {0,8:F1} ms   平均 {1:F1} ms/帧", newMs, newMs / Frames);
                Console.WriteLine();
                double gain = oldMs / Math.Max(0.001, newMs);
                Console.WriteLine("  提升：{0:F1} 倍（省掉 {1:F0}% 的时间）", gain, 100.0 * (oldMs - newMs) / oldMs);
                Console.WriteLine("  以 16.7ms/帧（60fps）为预算：旧行为{0}，新行为{1}。",
                    oldMs / Frames > 16.7 ? "**超预算，会掉帧**" : "在预算内",
                    newMs / Frames > 16.7 ? "**超预算，会掉帧**" : "在预算内");

                // 判定用**相对比值**，不用绝对毫秒 —— 绝对阈值在负载下会抖
                // （这个项目已经在"用墙钟卡死阈值"上栽过了，见 render-smoke 里那条耗时对称）。
                // 25 倍的余量下，要 3 倍才算过：机器再慢也稳过，真退化了又一定抓得住。
                Console.WriteLine();
                if (gain >= 3.0)
                {
                    Console.WriteLine("  [OK]   大图放大不再每帧从原图重做（{0:F1} 倍 ≥ 3 倍）", gain);
                    Console.WriteLine("通过 1 / 失败 0");
                }
                else
                {
                    Console.WriteLine("  [FAIL] 大图放大又退回每帧从原图重做了（只有 {0:F1} 倍）", gain);
                    Console.WriteLine("通过 0 / 失败 1");
                    Environment.ExitCode = 1;
                }
            }

            f.Dispose();
            Environment.ExitCode = 0;
        }
    }
}
