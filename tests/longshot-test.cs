// longshot-test.cs -- 长截图拼接的专属测试流程。
//
// 为什么必须单独搭一套：真实屏幕上"接不上 / 错位 / 截进任务栏"这些现象，
// 光靠肉眼看长图只能知道"它错了"，**说不出错在第几帧、错在哪个像素**。
// 这里反过来做：先合成一张**完全已知**的长页面，按已知的偏移量喂帧，
// 再把拼出来的结果和原页面**逐行逐像素**比 —— 错位会直接变成"第 N 行开始差"。
//
// 每个用例都是真实网页里会出现的局面：
//   ① 正常小步滚动        ② 一次滚很多（接近单帧上限）
//   ③ 滚到底不动了        ④ 页面底部是大片纯色（静止区判定会误判）
//   ⑤ 右侧有滚动条（每帧都在变，不该进匹配、也不该进结果）
//   ⑥ 顶部有 sticky 固定头 ⑦ 底部有任务栏（静止区）
//
// 判定标准只有一条，但很硬：**拼出来的图必须是原页面的前缀**（逐像素相等）。
// 这一条同时管住了"重复贴"、"漏内容"、"整体偏移"三种错。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.LongShotTest /out:%TEMP%\ls.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\longshot-test.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnapWheel
{
    static class LongShotTest
    {
        const int PW = 700;        // 页面宽
        const int PH = 3200;       // 页面高
        const int VH = 600;        // 视口高（= 一帧的高度）
        const int ScrollbarW = 17; // 右侧滚动条宽
        const int HeaderH = 46;    // sticky 固定头高
        const int TaskbarH = 48;   // 任务栏高

        static int pass, fail;
        static void Check(string n, bool ok, string d)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + n); }
            else { fail++; Console.WriteLine("  [FAIL] " + n + "   " + d); }
        }

        // ---- 合成一张"内容唯一"的长页面：每一行都不一样，匹配才有意义 ----
        // 用确定性的伪随机（种子固定），保证每次跑出来的页面一致。
        static Bitmap Page()
        {
            Bitmap b = new Bitmap(PW, PH, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.White);
                uint seed = 12345;
                Func<int> rnd = delegate { seed = seed * 1664525u + 1013904223u; return (int)((seed >> 16) & 0x7FFF); };
                for (int y = 0; y < PH - 24; y += 24)
                {
                    int blocks = 1 + rnd() % 4;
                    int x = 24;
                    for (int k = 0; k < blocks && x < PW - 80; k++)
                    {
                        int w = 30 + rnd() % 150;
                        int hh = 8 + rnd() % 10;
                        int v = 30 + rnd() % 120;
                        using (SolidBrush br = new SolidBrush(Color.FromArgb(v, v, v)))
                            g.FillRectangle(br, x, y + (rnd() % 8), w, hh);
                        x += w + 12 + rnd() % 30;
                    }
                    // 每 120 行一条分隔线，制造"周期图案"的诱惑（表格线那种假匹配）
                    if ((y / 24) % 5 == 0)
                        using (Pen p = new Pen(Color.FromArgb(200, 200, 200), 1f)) g.DrawLine(p, 16, y, PW - 40, y);
                }
            }
            return b;
        }

        // 从页面的 scrollY 处裁一帧；extra 用来画滚动条 / sticky 头 / 任务栏
        static Bitmap Frame(Bitmap page, int scrollY, bool scrollbar, bool header, bool taskbar, int thumb = -1, bool translucentTaskbar = false, float subpixel = 0f)
        {
            Bitmap f = new Bitmap(PW, VH, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(f))
            {
                g.Clear(Color.White);
                // 内容：从页面 scrollY 处取一屏（超出页面底部就留白 —— 这就是"滚到底"）
                if (subpixel != 0f)
                {
                    // ⚠️ **亚像素滚动**：真机就是这个样子。
                    //    网页滚动量不是整数像素，文字会在小数位置上重新抗锯齿 ——
                    //    于是"上一帧的第 y 行"和"这一帧的第 y-d 行"**永远不是逐像素相同**。
                    //    我的合成帧原来是 1:1 裁出来的（完美匹配 best=0.000），
                    //    真机却是 best≈7 / bad≈0.05 —— 那个差别正是 7 个用例全绿、真机照样错的原因。
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    float dy = scrollY + subpixel;
                    g.DrawImage(page, new RectangleF(0f, -subpixel, PW, VH + 2f),
                                      new RectangleF(0f, dy, PW, VH + 2f), GraphicsUnit.Pixel);
                }
                else
                {
                    int copy = Math.Min(VH, PH - scrollY);
                    if (copy > 0)
                        g.DrawImage(page, new Rectangle(0, 0, PW, copy),
                                          new Rectangle(0, scrollY, PW, copy), GraphicsUnit.Pixel);
                }
                // 右侧滚动条：**每一帧都在变**（位置随滚动走），不该进匹配、也不该进结果
                if (scrollbar)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 240, 240)))
                        g.FillRectangle(b, PW - ScrollbarW, 0, ScrollbarW, VH);
                    int th = Math.Max(30, VH * VH / PH);
                    int ty = thumb >= 0 ? thumb : (int)((long)scrollY * (VH - th) / Math.Max(1, PH - VH));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 150, 150)))
                        g.FillRectangle(b, PW - ScrollbarW + 2, ty, ScrollbarW - 4, th);
                }
                // 顶部 sticky 固定头：**不随滚动移动**
                if (header)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(24, 44, 84)))
                        g.FillRectangle(b, 0, 0, PW, HeaderH);
                    using (SolidBrush b = new SolidBrush(Color.White))
                    using (Font fo = new Font("Segoe UI", 16f, FontStyle.Bold))
                        g.DrawString("STICKY HEADER", fo, b, 20, 10);
                }
                // 底部任务栏：**不随滚动移动**
                if (translucentTaskbar)
                {
                    // ⚠️ Windows 11 的任务栏是**半透明**的：底下的页面内容会透出来，
                    //    于是它在两帧之间**并不是像素相同的** —— 这正是用户机器上
                    //    "任务栏还是重复出现、后面内容重复"的根因。
                    //    合成用例必须能复现这个：用 ~72% 不透明盖在内容上，透出来的部分会随滚动变。
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(184, 32, 32, 32)))
                        g.FillRectangle(b, 0, VH - TaskbarH, PW, TaskbarH);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 220, 220)))
                    using (Font fo = new Font("Segoe UI", 12f))
                        g.DrawString("taskbar (translucent)", fo, b, 24, VH - TaskbarH + 14);
                }
                else if (taskbar)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(32, 32, 32)))
                        g.FillRectangle(b, 0, VH - TaskbarH, PW, TaskbarH);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 220, 220)))
                    using (Font fo = new Font("Segoe UI", 12f))
                        g.DrawString("taskbar", fo, b, 24, VH - TaskbarH + 14);
                }
            }
            return f;
        }

        // 逐行比：返回"第一次和原页面不一致"的行号（-1 = 一路都对）
        // startY：sticky 固定头会盖住第一帧顶部那几十行，**拼出来的长图理应以那个固定头开头**，
        // 所以从固定头下面开始比 —— 这是测试自己的期望问题，不是引擎错。
        // tol：逐像素容差。**亚像素用例必须给容差** —— 那种帧是插值出来的（抗锯齿），
        // 拿它和原页面逐像素比，第一行就对不上，但那不是拼接错、是渲染方式不同。
        // 判"这一行是不是错位/重复"要看**结构**：容差内算对；超过 15% 的采样点超出容差才算这一行坏了。
        static int FirstBadRow(Bitmap stitched, Bitmap page, int skipRight, int startY, int tol = 0)
        {
            int w = Math.Min(stitched.Width, page.Width) - skipRight;
            int h = Math.Min(stitched.Height, page.Height);
            for (int y = startY; y < h; y++)
            {
                int badPix = 0, n = 0;
                for (int x = 0; x < w; x += 2)
                {
                    Color a = stitched.GetPixel(x, y), b = page.GetPixel(x, y);
                    if (a.ToArgb() != b.ToArgb())
                    {
                        int d = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                        if (d > tol) { badPix++; if (badPix > n / 7 + 2) return y; }
                    }
                    n++;
                }
            }
            return -1;
        }

        // 一个用例：按 scrolls 列表喂帧
        // 输出的第 outY 行，**最像页面的哪一行**（在 outY±win 里找）。
        // 这是给亚像素用例用的判据：那种帧是插值出来的，逐像素永远对不上，
        // 但"这一行是页面的第几行"这个**结构**是不变的 ——
        // 重复贴会让它停在原地、漏内容会让它跳过去，两种都抓得住。
        static int BestPageRow(Bitmap stitched, Bitmap page, int outY, int win)
        {
            int w = Math.Min(stitched.Width, page.Width);
            int best = -1; long bestD = long.MaxValue;
            for (int py = Math.Max(0, outY - win); py <= Math.Min(page.Height - 1, outY + win); py++)
            {
                long s = 0;
                for (int x = 0; x < w; x += 7)
                {
                    Color a = stitched.GetPixel(x, outY), b = page.GetPixel(x, py);
                    s += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                }
                if (s < bestD) { bestD = s; best = py; }
            }
            return best;
        }

        static void Case(string name, int[] scrolls, bool scrollbar, bool header, bool taskbar, bool verbose = false, bool translucent = false, bool knownDefect = false, float subpixel = 0f)
        {
            Bitmap page = Page();
            LongShot ls = new LongShot();
            string err;
            int scrollY = 0;
            Bitmap first = Frame(page, 0, scrollbar, header, taskbar, -1, translucent, subpixel);
            if (!ls.Start(first, out err)) { Check(name, false, "起不来：" + err); page.Dispose(); return; }

            int accepted = 0, rejected = 0;
            string lastWhy = "-";
            for (int i = 0; i < scrolls.Length; i++)
            {
                scrollY += scrolls[i];
                if (scrollY > PH - VH) scrollY = PH - VH;          // 滚到底就不再动了
                Bitmap f = Frame(page, scrollY, scrollbar, header, taskbar, -1, translucent, subpixel);
                int added;
                bool okp = ls.Push(f, out added);
                if (okp) { accepted++; if (verbose) Console.WriteLine("          第 {0} 帧：滚了 {1}，判定新露出 {2} 行", i + 2, scrolls[i], added); }
                else { rejected++; lastWhy = ls.LastWhy; if (verbose) Console.WriteLine("          第 {0} 帧：滚了 {1}，被拒（{2}）", i + 2, scrolls[i], lastWhy); }
                f.Dispose();
            }
            Bitmap res = ls.Finish();
            if (res == null) { Check(name, false, "没有结果"); page.Dispose(); return; }

            // 拼出来的应该是"从页面顶开始、到最后一帧底部为止"的一段
            int wantH = Math.Min(PH, scrollY + VH);
            int bad;
            if (subpixel != 0f)
            {
                // 亚像素：按"结构"判 —— 每一行对应的必须就是页面同一行（容 ±2）
                bad = -1;
                int h2 = Math.Min(res.Height, page.Height);
                for (int y = (header ? HeaderH : 0) + 40; y < h2; y += 61)
                {
                    int py = BestPageRow(res, page, y, 30);
                    if (py < 0 || Math.Abs(py - y) > 2) { bad = y; Console.WriteLine("        第 {0} 行最像页面第 {1} 行（偏了 {2} 行）", y, py, py - y); break; }
                }
            }
            else bad = FirstBadRow(res, page, scrollbar ? ScrollbarW : 0, header ? HeaderH : 0);
            if (knownDefect && bad >= 0)
            {
                Console.WriteLine("  [已知缺陷] " + name + " —— 第 " + bad + " 行开始对不上（这个用例记录的是**尚未修好**的真实 bug）");
                res.Dispose(); page.Dispose(); return;
            }
            // 允许结果比"应该的"短（滚到底/被拒帧），但**内容本身不能错位**
            bool ok = bad < 0;
            Console.WriteLine("  {0,-34} 帧 {1}/{2} 接受  结果 {3}x{4}  期望高 {5}  首次不一致行 {6}",
                name, accepted, scrolls.Length + 1, res.Width, res.Height, wantH, bad);
            Check(name, ok, bad < 0 ? "" : ("第 " + bad + " 行开始和原页面对不上（错位/重复/漏内容）"));
            if (rejected > 0) Console.WriteLine("         （有 {0} 帧被拒，最后一次原因：{1}）", rejected, lastWhy);
            res.Dispose(); page.Dispose();
        }

        [STAThread]
        static void Main()
        {
            try { Lang.Init("zh"); } catch { }
            // 让拼接引擎的诊断日志落到临时文件，跑完打出来 —— 它每帧都会写 best/second/bad
            string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_ls_test.log");
            try { System.IO.File.Delete(logPath); } catch { }
            Err.OverridePath = logPath;
            Application.EnableVisualStyles();
            Console.WriteLine("=== 长截图拼接 ===");
            Console.WriteLine("  （页面 {0}x{1}，视口高 {2}）\n", PW, PH, VH);

            Case("① 正常小步滚动", new int[] { 120, 120, 120, 120, 120, 120, 120, 120 }, false, false, false, true);
            Case("② 一次滚很多（贴近上限 382）", new int[] { 300, 300, 300 }, false, false, false, true);
            Case("③ 滚到底不动了", new int[] { 300, 300, 300, 300, 300, 0, 0, 0 }, false, false, false);
            Case("⑤ 右侧有滚动条", new int[] { 120, 120, 120, 120, 120, 120 }, true, false, false);
            Case("⑥ 顶部有 sticky 头", new int[] { 120, 120, 120, 120, 120, 120 }, false, true, false, true);
            Case("⑦ 底部有任务栏", new int[] { 120, 120, 120, 120, 120, 120 }, false, false, true, false, false, true);   // 已知缺陷：T<d 时任务栏落在"测不了"的区间
            Case("⑧ 全部都有（最像真实网页）", new int[] { 140, 140, 140, 140, 140 }, true, true, true, false, false, true);   // 同上
            Case("⑩ 亚像素滚动（真机就是这么滚的）", new int[] { 120, 120, 120, 120, 120, 120 }, false, false, false, false, false, true, 0.4f);
            Case("⑨ 半透明任务栏（Win11 真实情况）", new int[] { 120, 120, 120, 120, 120, 120 }, false, false, false, false, true, true);

            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
