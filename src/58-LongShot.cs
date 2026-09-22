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
        public const int BandRows = 100;        // 模板带高度（薄一点 → 能检测的单帧滚动量更大）
        // ★ 模板带**放在屏幕高度的 62% 处**，绝不贴屏幕底边 —— 原因见 Find() 里的说明
        //   （屏幕最底下通常是任务栏，它在截图里是静止的，拿它当模板永远匹配不上）。
        public const float BandCenterFrac = 0.72f;
        // 第二段验证带（放在屏幕 28% 处）：同一个偏移 d 必须在**两段互不相邻的画面**上都对得上才算数。
        // 这是防"假匹配"的关键 —— 网页里到处是周期（表格行、列表项、等距的卡片），单段匹配时
        // 一个错误的 d 也可能把线条对齐（实测滚过头时会挑出 300 这种错偏移，接缝整条错位）；
        // 两段隔得远，要同时骗过两边的概率低得多。
        public const float Band2CenterFrac = 0.28f;
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
        // 判据阈值：合成图测试里像素完全一致（真匹配代价 ≈ 0、差异比例 ≈ 0），但**真实屏幕不是** ——
        // 浏览器平滑滚动会让内容做子像素重采样、光标在闪、还有视频/动画，前后帧不可能逐像素相同。
        // 所以这里按"真实场景"放宽（0.6.0 实测：贴屏幕底边的模板带 + 过严的阈值，会让真实使用里
        // 一帧都接不上）；防止误匹配主要靠下面那条"best×1.8 必须小于 second"的置信度判据。
        public const double MatchTol = 32.0;     // 代价上限（真实屏幕实测 best 20~24；配合"小步滚动"重叠区大，这个值够用）
        public const double MaxBadRatio = 0.13;  // 差异像素比例上限（真实屏幕实测 6%~8%，原来 5% 把每一帧都拒了）
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
        bool _stillTrimmed;          // 第一帧那条静止区（任务栏）裁掉了没有

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
            if (first == null) { error = Lang.T("没有拿到第一屏", "Did not get the first screen"); return false; }
            _w = first.Width; _h = first.Height;
            if (_h < BandRows * 2 + MinNewRows) { error = Lang.T("这一屏太矮，滚动长图用不了", "This area is too short for a scrolling capture"); return false; }

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
            if (_canvas == null || frame == null) { _why = Lang.T("还没开始长图", "Not started yet"); return false; }
            if (frame.Width != _w || frame.Height != _h) { _why = Lang.T("画面尺寸变了（换了窗口/显示器？），这张长图到此为止", "The screen size changed (another window or monitor?), so this capture stops here"); return false; }
            if (Full) { _why = Lang.T("长图已经到最大高度了", "The image reached its maximum height"); return false; }

            byte[] cur = Sample(frame);
            Match m = Find(_prev, cur, _w, _h, _sw, _sh);
            // 诊断：把每次判定的依据写进日志（真实屏幕上"接不上"时，这是唯一能看出卡在哪的东西）
            try
            {
                Err.Log("LongShot", new Exception("帧 " + _shotCount + " " + _w + "x" + _h + " -> "
                    + (m.Ok ? ("接上 " + m.NewRows + Lang.T(" 行", " rows")) : "拒绝")
                    + " best=" + m.Score.ToString("0.00") + " second=" + m.Second.ToString("0.00")
                    + " bad=" + m.BadRatio.ToString("0.000") + " why=" + (m.Why == null ? "-" : m.Why)));
            }
            catch { }
            if (!m.Ok)
            {
                _why = m.Why;
                _prev = cur;   // 关键：失败也把基准推到当前帧，否则下一拍还在跟起点帧比，越滚越对不上
                return false;
            }

            addedRows = m.NewRows;
            int room = MaxCanvasH - _canvasH;
            if (addedRows > room) addedRows = room;
            if (addedRows <= 0) { _why = Lang.T("长图已经到最大高度了", "The image reached its maximum height"); return false; }

            // 把新屏的**最后 addedRows 行**贴到画布下方：这就是新露出来的内容
            // ⚠️ 取"新露出的内容"必须避开屏幕底部的**静止区**（典型就是任务栏）：它不随页面滚动移动，
            // 直接取屏幕最底部的 addedRows 行，等于每一帧都把任务栏又贴进长图一次 ——
            // 结果就是"长图里全是堆叠的任务栏、几乎没有内容"（用户实测）。
            //
            // ⚠️⚠️ 但"静止"**不能只看"这一行两帧一模一样"**。
            //    空白行在两帧里当然也一样 —— 于是一张有大片留白的**普通网页**，
            //    底部会被判成"一大片静止区"，srcY 被抬高，**每一帧都重复贴一段已经贴过的内容**。
            //    这就是用户报的"错位"，而且它对"正常页面"也会发作（合成长页测试一直没暴露它，
            //    因为合成图里全是密排的文字、没有留白）。
            //    正确判据要**同时看两个假设**，顺序不能反：
            //      · 滚动假设：cur[y] ≈ prev[y-d] → 这一行跟着页面滚了 → 到底了，停
            //      · 静止假设：cur[y] ≈ prev[y]   → 这一行没动 → 才可能是任务栏
            //    先看滚动假设：**空白行在滚动假设下也成立**（两边都白）→ 直接停、still=0。
            //    这正是我们要的保守默认 —— 宁可当成"会滚"，也不要凭空抬高 srcY。
            //    只有"滚动假设不成立、静止假设成立"的行才算静止区。
            int still = 0;
            int bandBot2 = (int)(_h * BandCenterFrac) + BandRows / 2;   // 和 Find 里那条模板带同一条
            int stillCap = _h - bandBot2 - 2;
            for (int y = _h - 1; y > bandBot2 && still < stillCap; y--)
            {
                if (RowDiffOffset(_prev, cur, _sw, y, addedRows) <= 3.0) break;   // 跟着滚了
                if (RowDiff(_prev, cur, _sw, y) > 3.0) break;                     // 既不像滚也不像静止：收手
                still++;
            }
            // ⚠️ take 必须就是"实际画了几行"。
            //    原来是 take/srcY/_canvasH 三个量分开算的，srcY<0 时 take 会变成 _h-still、
            //    比真正画上去的 addedRows 大，于是画布上留下空行 —— **之后每一帧的落点整体偏移**。
            // ⚠️ 第一帧是**整屏**贴进画布的（见 Start），但它底部的 still 行是**静止区**、不是页面内容。
            //    不裁掉的话，画布开头就带着一条任务栏，之后所有内容都跟着错位
            //    （实测：任务栏 48px 的用例正好在第 552 行 = 600-48 处开始对不上）。
            if (!_stillTrimmed && still > 0)
            {
                _stillTrimmed = true;
                _canvasH -= still;
                if (_canvasH < 1) _canvasH = 1;
            }
            int take = addedRows;
            int srcY = _h - still - take;
            if (srcY < 0) { srcY = 0; take = _h - still; }
            if (take <= 0) { _why = "没有可拼的新内容"; return false; }
            using (Graphics g = Graphics.FromImage(_canvas))
            {
                g.DrawImage(frame, new Rectangle(0, _canvasH, _w, take),
                                   new Rectangle(0, srcY, _w, take), GraphicsUnit.Pixel);
            }
            _canvasH += take;
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
        static double Score(byte[] prev, byte[] cur, int sw, int bandTop, int bandBot, int bandTop2, int bandBot2,
                            int d, bool[] sameRow, out double badRatio)
        {
            long sad = 0; int n = 0, bad = 0;
            AddBand(prev, cur, sw, bandTop, bandBot, d, sameRow, ref sad, ref n, ref bad);
            AddBand(prev, cur, sw, bandTop2, bandBot2, d, sameRow, ref sad, ref n, ref bad);
            if (n < 200) { badRatio = 1; return double.MaxValue; }
            badRatio = (double)bad / n;
            return badRatio * 100.0 + (double)sad / n;
        }

        // 把一段横带上的"模板行 y 对新屏行 y-d"累加进统计
        static void AddBand(byte[] prev, byte[] cur, int sw, int bandTop, int bandBot, int d, bool[] sameRow,
                            ref long sad, ref int n, ref int bad)
        {
            for (int y = bandTop; y < bandBot; y += 3)
            {
                int y2 = y - d;
                if (y2 < 0) continue;
                // ⚠️ 跳过"两帧里**同一行**本来就一样"的行 —— 那要么是 sticky 固定顶栏，要么是空白行，
                // 两种都不携带"滚了多少"的信息，拿来比只会把真匹配污染掉。
                //
                // 不做这一步的话，长图**在真实网页上根本接不上**：网页几乎都有 sticky 顶栏，
                // 而偏移 d 下的参照行 y2 = y-d 会整段落进那一块固定头里。
                // 实测（合成 sticky 头用例）：带2 的 48% 样本在拿"页面内容"比"固定头"，
                // bad 从 0.000 涨到 0.21，于是每一帧都被拒、长图停在第一屏。
                if (sameRow != null && y2 < sameRow.Length && sameRow[y2]) continue;
                int o1 = y * sw, o2 = y2 * sw;
                for (int x = 0; x < sw; x += 4)
                {
                    int a = prev[o1 + x], b = cur[o2 + x];
                    int diff = (a > b) ? (a - b) : (b - a);
                    sad += diff;
                    if (diff > 12) bad++;          // 12 个灰度级以上就算"这个像素不一样"
                    n++;
                }
            }
        }

        internal static Match Find(byte[] prev, byte[] cur, int w, int h, int sw, int sh)
        {
            Match m = new Match();
            // 模板带的**位置**是这套算法最容易踩的坑：不能贴屏幕底边。
            // 屏幕最底下通常是**任务栏** —— 它在抓屏里是静止的、不跟着页面滚动走，
            // 拿它当模板的话"怎么对都对得上"（甚至对它自己 SAD≈0），匹配必然失败或挑到假偏移。
            // 0.6.0 实测：真实屏幕上"压根接不上"就是这个原因，而合成长页测试没暴露它
            // （合成图里没有任务栏）。
            // 现在取"屏幕高度 62% 处"为中心的一条带：稳稳落在内容区，上不碰标题栏、下不碰任务栏。
            int bandBot = (int)(h * BandCenterFrac) + BandRows / 2;
            if (bandBot > h - SkipBottom) bandBot = h - SkipBottom;
            int bandTop = bandBot - BandRows;
            if (bandTop < 0) bandTop = 0;

            int maxD = bandTop;                               // d 最大到"模板带顶行"：再大模板就顶出屏幕了
            if (maxD > 500) maxD = 500;                       // 上限：再大的单帧滚动本来也难保证拼对，还极费时间
            if (maxD > h - 16) maxD = h - 16;                 // 保险（矮屏）

            // 第二段验证带（屏幕 28% 处）：和主带隔得远，专门用来拆穿"周期图案对齐"的假匹配
            int band2Bot = (int)(h * Band2CenterFrac) + BandRows / 2;
            if (band2Bot > h - SkipBottom) band2Bot = h - SkipBottom;
            int band2Top = band2Bot - BandRows;
            if (band2Top < 0) band2Top = 0;
            // 先算一遍"两帧里同一行是不是本来就一样"（每帧一次，别放进 d 循环里 —— 那会把匹配器的开销翻倍）
            bool[] sameRow = new bool[h];
            for (int y = 0; y < h; y++) sameRow[y] = RowDiff(prev, cur, sw, y) <= 3.0;
            double best = double.MaxValue, second = double.MaxValue;
            int bestD = 0;
            double bestBad = 1;

            // 第一遍：找代价最小的 d
            for (int d = MinNewRows; d <= maxD; d++)
            {
                double bad;
                double cost = Score(prev, cur, sw, bandTop, bandBot, band2Top, band2Bot, d, sameRow, out bad);
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
                    double cost = Score(prev, cur, sw, bandTop, bandBot, band2Top, band2Bot, d, sameRow, out bad);
                    if (cost < second) second = cost;
                }
            }

            m.Score = best == double.MaxValue ? -1 : best;
            m.Second = second == double.MaxValue ? -1 : second;
            m.BadRatio = bestBad;
            if (bestD == 0)
            {
                m.Why = Lang.T("找不到重叠区（这一屏和上一屏对不上），再滚一下试试", "No overlap found (this screen does not match the previous one) - try scrolling again");
                return m;
            }
            // ① 主判据：差异像素比例。周期图案（表格线/列表项）对齐时线条能对上，
            //    但格子里的字对不上 —— 那一片片"不一样的像素"就是靠这个挡下来的，
            //    实测（用例 2：一次滚过头）平均差只有 4 点几，单看平均差会放它过去。
            if (bestBad > MaxBadRatio)
            {
                m.Why = Lang.T("这一屏对不上（滚过头了，或者画面里在动），慢一点再滚一下", "No match (scrolled too far, or something is moving) - scroll more slowly");
                return m;
            }
            if (best > MatchTol)
            {
                m.Why = Lang.T("这一屏没对上（画面变化太大或滚过头了），再滚一下试试", "No match (the content changed too much, or scrolled too far) - try again");
                return m;
            }
            // 置信度：次优不能和最优一样好 —— 否则说明"怎么对都对得上"（多半是纯色/重复内容），宁可让用户再滚
            // 0.6.0：原来这里还有一条"次优必须明显更差"的相对置信度判据，已删除 ——
            // 真实屏幕实测 best 与 second 天然只差 1（内容相似度本就是连续渐变的），
            // 那条判据只会一路拒。现在由上面的绝对判据（代价上限 + 差异像素比例）把关。
            m.NewRows = bestD;
            return m;
        }

        // 某一行在两帧之间的平均灰度差，但按**滚动偏移**对齐：cur[y] 对 prev[y-d]。
        // 空白行在"同位置"和"按偏移"两种假设下都成立（两边都白），
        // 而跟着滚动的实内容只在"按偏移"下成立 —— 这两条一比就能把空白和静止区分开。
        static double RowDiffOffset(byte[] a, byte[] b, int sw, int y, int d)
        {
            if (a == null || b == null) return 999;
            int y2 = y - d;
            if (y2 < 0) return 999;
            long s = 0; int n = 0;
            for (int x = 0; x < sw; x += 3) { int v = a[y2 * sw + x] - b[y * sw + x]; s += v < 0 ? -v : v; n++; }
            return n == 0 ? 999 : (double)s / n;
        }

        // 某一行在两帧之间的平均灰度差（用于找屏幕底部的静止区）
        static double RowDiff(byte[] a, byte[] b, int sw, int y)
        {
            if (a == null || b == null) return 999;
            int o = y * sw; long s = 0; int n = 0;
            for (int x = 0; x < sw; x += 3) { int d = a[o + x] - b[o + x]; s += d < 0 ? -d : d; n++; }
            return n == 0 ? 999 : (double)s / n;
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
