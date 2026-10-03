using System;
using System.Text;

namespace SnapWheel
{
    // ==================== 大图分块识别（0.6.0 阶段 2 的第三条） ====================
    // 问题：Windows 的 OCR 引擎对超大图会先**降采样**再识别，字一小就整段糊掉 ——
    //       而 0.6.0 新加的滚动长截图，产出的正是又高又窄的长图（比如 1200×9000），
    //       整张丢进去等于白给。
    // 做法：把长图**沿竖直方向切成若干横条**，每条都保持原始分辨率、各自走一遍识别，
    //       最后按顺序拼成文本。
    //
    // ⚠️ 切缝不能随便切：切在一行字的中间会把那行字劈成两半（两半都认不出来）。
    //    所以切缝要在目标位置附近**找一条"最空的行"**（行内相邻像素差最小 = 最接近纯色背景），
    //    那里几乎不可能有字，切下去就不伤内容。这样也省掉了"重叠区文字去重"那套麻烦事。
    static class OcrTall
    {
        const int StripH = 1400;      // 每条的目标高度（原分辨率，够引擎吃）
        const int SearchRows = 80;    // 在目标切缝上下各找这么多行，挑最空的一条
        const int MinStrip = 60;      // 比这还矮就不切了（避免最后剩一条细缝反复识别）

        public static bool ShouldSplit(int w, int h)
        {
            // 又高又长才切：高度超过两条、且明显是"长条形"（长截图就是这样的）
            return h > StripH * 2 && (double)h / Math.Max(1, w) > 1.6;
        }

        // 分块识别。任何一条失败就整体失败（返回 null + 原因），不假装成功。
        public static string Recognize(byte[] bgra, int w, int h, out string error)
        {
            error = null;
            StringBuilder all = new StringBuilder();
            int y = 0;
            int guard = 0;
            while (y < h && guard++ < 64)
            {
                int cut = Math.Min(h, y + StripH);
                if (cut < h) cut = BlankRowNear(bgra, w, h, cut);
                int ch = cut - y;
                if (ch < MinStrip) break;

                byte[] strip = Crop(bgra, w, h, y, ch);
                string t = Ocr.RecognizePixels(strip, w, ch, out error);
                if (t == null) return null;
                t = t.Trim();
                if (t.Length > 0)
                {
                    if (all.Length > 0) all.Append('\n');
                    all.Append(t);
                }
                y = cut;
            }
            return all.ToString();
        }

        // 在目标行附近挑一条"最空"的行当切缝：空 = 这一行里相邻像素差别最小（接近纯色）
        static int BlankRowNear(byte[] px, int w, int h, int target)
        {
            int lo = Math.Max(1, target - SearchRows);
            int hi = Math.Min(h - 2, target + SearchRows);
            int best = target;
            long bestScore = long.MaxValue;
            for (int y = lo; y <= hi; y++)
            {
                long s = 0;
                int o = y * w * 4;
                // 横向抽样比对（每 8 个像素取一个），够判断"是不是一片均匀）
                for (int x = 0; x + 32 < w * 4; x += 32)
                {
                    int a = px[o + x], b = px[o + x + 32];
                    s += Math.Abs(a - b);
                }
                if (s < bestScore) { bestScore = s; best = y; }
                if (bestScore == 0) break;      // 纯色行，不用再找
            }
            return best;
        }

        // 按行裁一段出来（32bpp，一行 w*4 字节）
        static byte[] Crop(byte[] px, int w, int h, int y0, int ch)
        {
            byte[] outp = new byte[w * ch * 4];
            Buffer.BlockCopy(px, y0 * w * 4, outp, 0, outp.Length);
            return outp;
        }
    }
}
