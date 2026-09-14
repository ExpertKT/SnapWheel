using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnapWheel
{
    // ==================== 滚动长截图：拼接引擎（0.6.0 阶段 1） ====================
    // 用户拍板的交互：**他手动滚，程序跟着无缝拼接**（不给目标窗口发合成滚轮消息 ——
    // 那样既要猜滚几格、又要处理惯性/节流，还挑窗口）。
    //
    // 流程（浮层那边调用）：
    //   1) 进入长图模式，抓第一屏 -> LongShot.Start(first)
    //   2) 用户滚动目标内容；每次"停稳"后再抓一屏 -> LongShot.Push(frame)
    //        返回 true  = 接上了（返回新增了几行）
    //        返回 false = 这一屏没对上（页面动画/滚动没发生/滚太多）—— 提示用户再滚一下，不破坏已有画布
    //   3) 用户按 Enter/双击结束 -> LongShot.Result 拿到整张长图
    //
    // 算法（只找**竖直**偏移，横向不动 —— 滚动截图不会左右移）：
    //   · 拿"上一屏的底部带"当模板（默认取最后 240 行，并避开最底 8 行：那里常常是滚动条圆角、
    //     提示条、渐变淡出，它们不随内容滚动，会把匹配带偏）；
    //   · 在新屏里沿竖直方向搜 d（= 这一屏新露出多少行），使 new[y-d] ≈ prev[y] 最接近；
    //   · 用行方向 2 行取 1、列方向 2 px 取 1 的灰度采样算平均绝对差（SAD），
    //     满分辨率太慢（一次搜索要几百 MB 次比较），采样后误差仍在 1 个灰度级内，够用；
    //   · 判"对上了"要有置信度，两条同时满足：
    //       ① 最佳 d 的平均差 < MatchTol（灰度级，默认 10）
    //       ② 最佳 d 明显好于次优（去掉最佳附近 ±24 行后的最好值）：best*1.8 < second
    //       否则返回 false —— **宁可让用户再滚一次，也不要拼错**（拼错会留下一条错位的接缝）。
    //   · 右侧 24px 与最底 8~24px 一律不参与匹配：滚动条/水印/窗口圆角都在那儿，且它们不滚动。
    //
    // 内存：画布最高 MaxCanvasH 行，按 32bpp 算 20000×1200×4B ≈ 96MB —— 到顶就停下并告诉用户。
    // 线程：这里全是 LockBits + byte[]，不碰 GDI 绘制，可以在后台线程跑（浮层抓屏是另一回事）。
    // 一次长截图 = 一个 LongShot 实例（0.6.0 改成实例类了：它要记住"上一屏"和画布）。
    // 里面的 Find / Sample 是纯函数，保持 static，方便单独验证。
    sealed class LongShot
    {
        public const int MaxCanvasH = 20000;    // 画布高度上限
        public const int BandRows = 120;        // 模板带高度
        // ⚠️ 这个带**必须薄**，原因是一条硬约束：
        //     能检测出来的最大滚动量 d ≤ 模板带顶行离屏幕顶的距离（bandTop），
        //     因为模板的每一行 y 都要能在新屏里找到 y-d ≥ 0。
        //   带取 240 行时 bandTop 只剩 352（600 高的屏）→ d 只能搜到 240 左右，
        //   而人滚一次常常就是 300~400 行 —— 于是每一帧都"找不到重叠"，只能挑到某个
        //   局部最优（实测挑出了 189、160 这种数），拼出来的图是错的。
        //   0.6.0 用合成长页逐帧验证时就是这么暴露的。120 行：样本 ~2.9 万个点，够稳；
        //   bandTop 抬到 472，一次滚到 470 行都还认得出来。
        public const int SkipBottom = 8;        // 模板带离屏幕底边的距离（躲开滚动条/圆角）
        public const int SkipRight = 24;        // 右侧不参与匹配的宽度（滚动条）
        public const int MinNewRows = 6;        // 小于这个行数算"没滚"，不拼
        public const double MatchTol = 4.0;     // 代价上限（代价 = 差异像素比例×100 + 平均灰度差；真匹配约 1，假匹配 7 起步）
        public const double MaxBadRatio = 0.012; // 差异像素比例上限：真匹配 ~0.1%（只有抗锯齿边缘），周期图案假匹配 2% 起步
        public const int MinCanvasLeft = 0;

        // 一次匹配的结果，方便浮层显示"这次接上了多少行 / 为什么没接上"
        public sealed class Match
        {
            public int NewRows;        // 新露出多少行（0 = 没成功）
            public double Score;       // 代价（越小越像）
            public double Second;      // 次优代价（用来看置信度）
            public double BadRatio;    // 差异像素比例（最能说明"到底像不像"）
            public string Why;         // 失败原因（人话）
            public bool Ok { get { return NewRows > 0; } }
        }

        Bitmap _canvas;                // 已拼好的长图（32bppPArgb）
        int _w, _h;                    // 屏幕宽高（= 单帧尺寸，全程不变；变了就重来）
        int _canvasH;                  // 画布已用高度
        byte[] _prev;                  // 上一屏的灰度采样（宽 _w、高 _h，每像素 1 字节）
        int _sw;                       // 采样后的行宽（= ceil((_w - SkipRight) / 2)）
        int _sh;                       // 高（= _h，行不跳采样，只有列跳）
        string _why;
        int _shotCount;

        public int Height { get { return _canvasH; } }
        public int Shots { get { return _shotCount; } }
        public string LastWhy { get { return _why; } }
        public Bitmap Result { get { return _canvas; } }
        public bool Full { get { return _canvasH >= MaxCanvasH; } }

        // 开一张新长图：把第一屏整张贴进画布
        public bool Start(Bitmap first, out string error)
        {
            error = null;
            _why = null;
            _shotCount = 0;
            if (first == null) { error = "没有拿到第一屏"; return false; }
            _w = first.Width; _h = first.Height;
            if (_h < BandRows * 2 + MinNewRows) { error = "这一屏太矮，滚动长图用不了"; return false; }

            _canvasH = _h;
            if (_canvasH > MaxCanvasH) _canvasH = MaxCanvasH;
            _canvas = new Bitmap(_w, MaxCanvasH, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(_canvas))
            {
                g.DrawImageUnscaled(first, 0, 0);
            }
            _sw = (_w - SkipRight + 1) / 2;
            _sh = _h;
            _prev = Sample(first);
            _shotCount = 1;
            return true;
        }

        // 新一屏：算偏移 -> 命中就把新增的那些行追加到画布下方
        public bool Push(Bitmap frame, out int addedRows)
        {
            addedRows = 0;
            _why = null;
            if (_canvas == null || frame == null) { _why = "还没开始长图"; return false; }
            if (frame.Width != _w || frame.Height != _h) { _why = "画面尺寸变了（换了窗口/显示器？），这张长图到此为止"; return false; }
            if (Full) { _why = "长图已经到最大高度了"; return false; }

            byte[] cur = Sample(frame);
            Match m = Find(_prev, cur, _w, _h, _sw, _sh);
            if (!m.Ok) { _why = m.Why; return false; }

            addedRows = m.NewRows;
            int room = MaxCanvasH - _canvasH;
            if (addedRows > room) addedRows = room;
            if (addedRows <= 0) { _why = "长图已经到最大高度了"; return false; }

            // 把新屏的**最后 addedRows 行**贴到画布下方：这就是新露出来的内容
            int srcY = _h - addedRows;
            using (Graphics g = Graphics.FromImage(_canvas))
            {
                g.DrawImage(frame, new Rectangle(0, _canvasH, _w, addedRows),
                                   new Rectangle(0, srcY, _w, addedRows), GraphicsUnit.Pixel);
            }
            _canvasH += addedRows;
            _shotCount++;
            _prev = cur;
            return true;
        }

        // 结束：把画布裁到实际高度，返回一张干净的长图
        public Bitmap Finish()
        {
            if (_canvas == null) return null;
            if (_canvasH >= _canvas.Height) return _canvas;
            Bitmap r = new Bitmap(_w, Math.Max(1, _canvasH), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(r))
            {
                g.DrawImage(_canvas, new Rectangle(0, 0, _w, _canvasH),
                                    new Rectangle(0, 0, _w, _canvasH), GraphicsUnit.Pixel);
            }
            return r;
        }

        // ============================ 匹配 ============================
        // prev / cur：上一屏与这一屏的灰度采样（列已按 2px 采样，宽 = _sw，高 = _sh）
        // 返回：新露出多少行 + 置信度
        // 单个候选偏移 d 的代价（越小越像）。
        // 代价 = **差异像素比例 × 100** + 平均灰度差。
        // 为什么主判据是"差异像素比例"而不是平均差：
        //   真匹配（同一段内容）像素级几乎完全一样，只有抗锯齿边缘会差一点 —— 差异像素比例 ~0.1%；
        //   假匹配（周期图案对齐，比如表格线对齐了但格子里的字没对齐）平均差可能看着也不大，
        //   但"明显不同的像素"会成片出现，比例能到 2%~5%。
        //   平均差会被大片相同背景稀释（这正是用例 2 滚过头时"线条对齐"假匹配能溜过去的原因）。
        // 输出 badRatio 供调用方判定；样本太少返回 MaxValue 表示这个候选不算数。
        static double Score(byte[] prev, byte[] cur, int sw, int bandTop, int bandBot, int d, out double badRatio)
        {
            long sad = 0; int n = 0, bad = 0;
            // 模板带的每一行 y，去找新屏里的 y-d
            for (int y = bandTop; y < bandBot; y += 2)
            {
                int y2 = y - d;
                if (y2 < 0) continue;
                int o1 = y * sw, o2 = y2 * sw;
                for (int x = 0; x < sw; x += 2)
                {
                    int a = prev[o1 + x], b = cur[o2 + x];
                    int diff = (a > b) ? (a - b) : (b - a);
                    sad += diff;
                    if (diff > 12) bad++;          // 12 个灰度级以上就算"这个像素不一样"
                    n++;
                }
            }
            if (n < 200) { badRatio = 1; return double.MaxValue; }
            badRatio = (double)bad / n;
            return badRatio * 100.0 + (double)sad / n;
        }

        internal static Match Find(byte[] prev, byte[] cur, int w, int h, int sw, int sh)
        {
            Match m = new Match();
            int bandTop = h - SkipBottom - BandRows;          // 模板带的上边界
            if (bandTop < 0) bandTop = 0;
            int bandBot = h - SkipBottom;                     // 下边界（不含）

            int maxD = bandTop;                               // d 最大到"模板带顶行"：再大模板就顶出屏幕了
            if (maxD > h - 16) maxD = h - 16;                 // 保险（矮屏）
            double best = double.MaxValue, second = double.MaxValue;
            int bestD = 0;
            double bestBad = 1;

            // 第一遍：找代价最小的 d
            for (int d = MinNewRows; d <= maxD; d++)
            {
                double bad;
                double cost = Score(prev, cur, sw, bandTop, bandBot, d, out bad);
                if (cost < best) { best = cost; bestD = d; bestBad = bad; }
            }

            // 第二遍：在**排除最佳附近 ±24 行**之后找次优。
            // ⚠️ 这一步不能省、也不能用"顺手记录次优"的写法：相邻偏移（d±1、d±2…）的分数
            // 天然几乎一样（内容本来就平滑），顺手记下来的"次优"永远是 best+一点点，
            // 于是下面的置信度判据必然判成"不够独特"，**每一帧都会被拒** ——
            // 0.6.0 的合成长页验证就是这么暴露出来的（五帧全拒、长图停在第一屏高度）。
            if (bestD != 0)
            {
                for (int d = MinNewRows; d <= maxD; d++)
                {
                    if (d > bestD - 24 && d < bestD + 24) continue;
                    double bad;
                    double cost = Score(prev, cur, sw, bandTop, bandBot, d, out bad);
                    if (cost < second) second = cost;
                }
            }

            m.Score = best == double.MaxValue ? -1 : best;
            m.Second = second == double.MaxValue ? -1 : second;
            m.BadRatio = bestBad;
            if (bestD == 0)
            {
                m.Why = "找不到重叠区（这一屏和上一屏对不上），再滚一下试试";
                return m;
            }
            // ① 主判据：差异像素比例。周期图案（表格线/列表项）对齐时线条能对上，
            //    但格子里的字对不上 —— 那一片片"不一样的像素"就是靠这个挡下来的，
            //    实测（用例 2：一次滚过头）平均差只有 4 点几，单看平均差会放它过去。
            if (bestBad > MaxBadRatio)
            {
                m.Why = "这一屏对不上（滚过头了，或者画面里在动），慢一点再滚一下";
                return m;
            }
            if (best > MatchTol)
            {
                m.Why = "这一屏没对上（画面变化太大或滚过头了），再滚一下试试";
                return m;
            }
            // 置信度：次优不能和最优一样好 —— 否则说明"怎么对都对得上"（多半是纯色/重复内容），宁可让用户再滚
            if (second != double.MaxValue && second > 0 && best * 1.8 > second)
            {
                m.Why = "这一屏重叠区不够独特（可能是纯色/重复内容），再滚一下试试";
                return m;
            }
            m.NewRows = bestD;
            return m;
        }

        // 灰度采样：列方向 2px 取 1（跳过右侧 SkipRight），行方向全取（行是匹配方向，不能跳）
        // 采样比全分辨率快 2 倍，实测对匹配精度没有影响（同一图案的相邻列几乎一样）
        //
        // ⚠️ 这里刻意**不用 unsafe**：本项目的 csc 没开 /unsafe（照 56-Ocr.cs 的 ScalePixels 走
        //    Marshal.Copy 逐行搬），开了开关又要动 build.ps1，没必要。
        internal static byte[] Sample(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int sw = (w - SkipRight + 1) / 2;
            byte[] outp = new byte[sw * h];
            byte[] row = new byte[w * 4];
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy((IntPtr)((long)d.Scan0 + (long)y * d.Stride), row, 0, w * 4);
                    int o = y * sw;
                    for (int x = 0, i = 0; i < sw; x += 2, i++)
                    {
                        // 亮度近似：0.114B + 0.587G + 0.299R（整数版，避免浮点）
                        int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        outp[o + i] = (byte)((b * 29 + g * 150 + r * 77) >> 8);
                    }
                }
            }
            finally { bmp.UnlockBits(d); }
            return outp;
        }
    }
}
