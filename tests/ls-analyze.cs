// ls-analyze.cs -- 量一张长图里"哪一段被重复贴了、周期是多少"（临时分析工具）。
//
// 为什么必须量：用户报的"文本怪怪的"有两种完全不同的可能根因，修法也完全不同 ——
//   A. 底部静止区（任务栏）被当成新内容贴进去  → 重复周期 ≈ 视口高 − 任务栏高，而且只在底部
//   B. 匹配到的偏移比真实滚动小几行（亚像素滚动）→ 重复周期 = 那几行，全图到处都有
// 光看图分不出这两者，逐行哈希一比就出来了。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnapWheel
{
    static class LsAnalyze
    {
        static void Main(string[] a)
        {
            string path = a.Length > 0 ? a[0] : null;
            if (path == null) { Console.WriteLine("用法: ls-analyze.exe <图.png>"); return; }
            Bitmap b = new Bitmap(path);
            int w = b.Width, h = b.Height;
            Console.WriteLine("图：" + w + "x" + h);

            Rectangle r = new Rectangle(0, 0, w, h);
            BitmapData d = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            int stride = d.Stride;
            byte[] buf = new byte[stride * h];
            Marshal.Copy(d.Scan0, buf, 0, buf.Length);
            b.UnlockBits(d);

            // 每行一个哈希（跳着采，够快也够稳）
            uint[] hs = new uint[h];
            for (int y = 0; y < h; y++)
            {
                int o = y * stride; uint v = 2166136261u;
                for (int x = 0; x < stride; x += 8) v = (v ^ buf[o + x]) * 16777619u;
                hs[y] = v;
            }

            Console.WriteLine("\n==== 候选重复周期（占比最高的 8 个）====");
            List<int> cand = new List<int>();
            List<double> ratio = new List<double>();
            // ⚠️ 指标是"**最长连续重复段**"，不是"重复行占比"。
            //    占比对小周期天然虚高（大片纯色/空行相邻两行本来就一样，周期 2 能到 27%），
            //    真正的"整段被重复贴了一遍"表现为**上百行的连续重复**。
            for (int P = 20; P <= 1200 && P < h; P++)
            {
                int run = 0, bestRun = 0;
                for (int y = 0; y + P < h; y++)
                {
                    if (hs[y] == hs[y + P]) { run++; if (run > bestRun) bestRun = run; }
                    else run = 0;
                }
                if (bestRun >= 20) { cand.Add(P); ratio.Add(bestRun); }
            }
            for (int i = 0; i < cand.Count; i++)
            {
                for (int j = i + 1; j < cand.Count; j++)
                    if (ratio[j] > ratio[i])
                    {
                        double t = ratio[i]; ratio[i] = ratio[j]; ratio[j] = t;
                        int tt = cand[i]; cand[i] = cand[j]; cand[j] = tt;
                    }
            }
            for (int i = 0; i < 8 && i < cand.Count; i++)
                Console.WriteLine("  周期 {0,4} 行 -> 最长连续重复段 {1:0} 行", cand[i], ratio[i]);

            int best = cand.Count > 0 ? cand[0] : 0;
            Console.WriteLine("\n==== 用最佳周期 " + best + " 看：重复发生在哪些行 ====");
            if (best > 0)
            {
                int runStart = -1, runLen = 0, shown = 0;
                for (int y = 0; y + best < h; y++)
                {
                    bool same = hs[y] == hs[y + best];
                    if (same) { if (runStart < 0) runStart = y; runLen++; }
                    else
                    {
                        if (runLen >= 20 && shown < 12)
                        {
                            Console.WriteLine("  行 {0,6} .. {1,6}  长 {2,5} 行  （和图上方相隔 {3} 行的内容重复）",
                                runStart, runStart + runLen - 1, runLen, best);
                            shown++;
                        }
                        runStart = -1; runLen = 0;
                    }
                }
                if (runLen >= 20 && shown < 12)
                    Console.WriteLine("  行 {0,6} .. {1,6}  长 {2,5} 行", runStart, runStart + runLen - 1, runLen);

                // 整体重复比例
                int tot = 0, hit = 0;
                for (int y = 0; y + best < h; y++) { tot++; if (hs[y] == hs[y + best]) hit++; }
                Console.WriteLine("\n  整图 {0} 行里有 {1} 行和它上方相隔 {2} 行的内容**完全一样**（{3:0.0}%）",
                    tot, hit, best, 100.0 * hit / Math.Max(1, tot));
            }
            b.Dispose();
        }
    }
}
