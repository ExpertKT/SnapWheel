using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnapWheel
{
    // 标注：工具条布局（摆位、可见性、悬停变淡）（从 51-OverlayForm.Annotate.cs 拆出，纯搬移，行为不变）。
    partial class OverlayForm
    {
        void PlaceToolbar()
        {
            int bw = (int)(BtnW * _k), bh = (int)(BtnH * _k), gp = (int)(Gap * _k);
            int n = BtnCount;
            RectangleF sb = SelBounds();

            // 按"当前这块屏幕"来算（多屏时别摆到别的屏去）
            Rectangle scr = ScreenFor(_vs, _hasSel ? new Point((int)_c.X, (int)_c.Y) : Point.Empty, _hasSel);
            Rectangle scrLocal = new Rectangle(scr.Left - _vs.Left, scr.Top - _vs.Top, scr.Width, scr.Height);

            // 左下角那个「比例」按钮（以及展开后的面板）别被压住
            Rectangle avoid = _toggleRect;
            if (_chipsOpen && _chips != null && _chips.Length > 0) avoid = Rectangle.Union(avoid, _chips[0].Rect);

            bool vertical, overlap;
            Rectangle me = ToolbarRect(scrLocal, sb, n, bw, bh, gp, gp, avoid, out vertical, out overlap);

            // 高 DPI 小屏（比如 1080p 开 150%）：竖排长度可能比屏幕还高 —— 那就把按钮间距压紧再试一次，
            // 宁可排得挤一点，也别去压住用户要截的地方。
            int gapUse = gp;
            if (vertical && me.Height > scrLocal.Height - 16 && gp > 3)
            {
                int cg = Math.Max(2, gp / 3);
                bool v2, o2;
                Rectangle m2 = ToolbarRect(scrLocal, sb, n, bw, bh, cg, cg, avoid, out v2, out o2);
                if (v2 && m2.Height <= scrLocal.Height - 16) { me = m2; vertical = v2; overlap = o2; gapUse = cg; }
            }

            _toolVertical = vertical;
            _toolOverlap = overlap;
            _toolRect = me;
            _toolBtns = new Rectangle[n];
            int cx = me.X + gapUse, cy = me.Y + gapUse;
            for (int i = 0; i < n; i++)
            {
                _toolBtns[i] = new Rectangle(cx, cy, bw, bh);
                if (vertical) cy += bh + gapUse; else cx += bw + gapUse;
            }
        }

        // 工具条到底摆哪（纯计算，离线可测）：**绝不压住选区**是硬规则。
        // 四个方向依次试，全试不到才允许压一点：
        //   1 选区下方  2 选区上方  3 选区右侧（竖排）  4 选区左侧（竖排）
        // 以前只试上下两个方向，选区一高（比如竖着截一整条）就只能压在截图上 ——
        // 用户看到的就是"工具栏挡住了截图区域"。
        internal static Rectangle ToolbarRect(Rectangle screen, RectangleF sel, int n, int bw, int bh, int gapBetween, int outer,
                                              Rectangle avoid, out bool vertical, out bool overlap)
        {
            vertical = false; overlap = false;
            int rowLen = n * bw + (n - 1) * gapBetween + outer * 2;   // 排成一排/一列时的总长
            int thick = bh + outer * 2;                               // 另一边的厚度
            int cl = screen.Left + 8, ct = screen.Top + 8, cr = screen.Right - 8, cb = screen.Bottom - 8;

            const int gap = 12;
            int sx0 = (int)sel.Left, sy0 = (int)sel.Top;
            int sx1 = (int)Math.Ceiling(sel.Right), sy1 = (int)Math.Ceiling(sel.Bottom);
            int rightX = sx1 + gap, leftX = sx0 - thick - gap;
            int belowY = sy1 + gap, aboveY = sy0 - thick - gap;

            int x = 0, y = 0;

            int hx = (int)sel.Left;                        // 横排：跟选区左对齐，再夹进屏幕
            if (hx + rowLen > cr) hx = cr - rowLen;
            if (hx < cl) hx = cl;

            int vy = (int)sel.Top;                         // 竖排：跟选区上对齐，再夹进屏幕
            if (vy + rowLen > cb) vy = cb - rowLen;
            if (vy < ct) vy = ct;

            if (belowY + thick <= cb) { x = hx; y = belowY; }                                    // 1 下方
            else if (aboveY >= ct) { x = hx; y = aboveY; }                                       // 2 上方
            else if (rowLen <= cb - ct && rightX + thick <= cr) { vertical = true; x = rightX; y = vy; }   // 3 右侧竖排
            else if (rowLen <= cb - ct && leftX >= cl) { vertical = true; x = leftX; y = vy; }             // 4 左侧竖排
            else
            {
                // 5 四处都没空（选区几乎铺满整屏）：压到"外面更空"的那一侧，并标记"压住了"——
                //   画的时候会压得更透（配合"鼠标不在附近就变淡"），至少不糊住看不清
                overlap = true;
                int roomAbove = sy0 - ct, roomBelow = cb - sy1;
                x = hx;
                y = (roomBelow >= roomAbove) ? Math.Min(cb - thick, belowY) : Math.Max(ct, aboveY);
                if (y < ct) y = ct;
                if (y + thick > cb) y = cb - thick;
            }
            if (x < cl) x = cl;

            int w = vertical ? thick : rowLen;
            int h = vertical ? rowLen : thick;
            Rectangle me = new Rectangle(x, y, w, h);

            if (me.IntersectsWith(avoid))
            {
                // 先试试横着躲开，再试竖着躲开；只有两样都不行才认命（并且更透）
                int altY = (y <= avoid.Top) ? avoid.Bottom + 6 : avoid.Top - h - 6;
                int altX = (x <= avoid.Left) ? avoid.Right + 6 : avoid.Left - w - 6;
                Rectangle candX = new Rectangle(altX, y, w, h);
                Rectangle candY = new Rectangle(x, altY, w, h);
                if (altX >= cl && altX + w <= cr && !candX.IntersectsWith(avoid) && !HitsSel(candX, sel))
                    me = candX;
                else if (altY >= ct && altY + h <= cb && !candY.IntersectsWith(avoid) && !HitsSel(candY, sel))
                    me = candY;
                else
                {
                    if (altY >= ct && altY + h <= cb) me = candY;
                    if (me.Y < ct) me.Y = ct;
                    if (me.Y + h > cb) me.Y = cb - h;
                    if (me.X < cl) me.X = cl;
                    overlap = true;
                }
            }
            return me;
        }

        static bool HitsSel(Rectangle r, RectangleF sb)
        {
            return r.IntersectsWith(Rectangle.Round(sb));
        }

        bool ToolbarVisible()
        {
            // 拖框选的过程中先不显示：那会儿工具条会追着鼠标、正好压在你要选的地方
            if (_dragging) return false;
            return _hasSel && _sz.Width > 20 && _sz.Height > 20;
        }

        // 鼠标离工具条远就变淡（不挡截图），靠近就恢复不透明。
        // 只做"远/近"两档、阈值给足余量，不做连续渐变 —— 免得看着晃。
        // 压住选区时（_toolOverlap）基础透明度更低，尽量别挡住底下那张图。
        // 返回"透明度变了没有"：浮层不是每帧重绘，变了得主动请求重绘，否则永远看不到变化。
        bool UpdateToolAlpha()
        {
            if (_toolRect.Width == 0) { _toolAlpha = 255; return false; }
            int want;
            try
            {
                Point cp = PointToClient(Cursor.Position);
                int dx = 0, dy = 0;
                if (cp.X < _toolRect.Left) dx = _toolRect.Left - cp.X;
                else if (cp.X > _toolRect.Right) dx = cp.X - _toolRect.Right;
                if (cp.Y < _toolRect.Top) dy = _toolRect.Top - cp.Y;
                else if (cp.Y > _toolRect.Bottom) dy = cp.Y - _toolRect.Bottom;
                int d = (int)Math.Sqrt(dx * dx + dy * dy);
                int idle = _toolOverlap ? 108 : 165;
                want = (d < 90) ? 255 : idle;
            }
            catch { want = 255; }
            if (want == _toolAlpha) return false;
            _toolAlpha = want;
            return true;
        }

    }
}
