using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnapWheel
{
    // 截图标注：箭头 / 方框 / 马赛克 / 文字（roadmap 里"加标注"那条 ——
    // 有了它，截完在轮盘里就能直接圈重点，不用再拖去别的软件）。
    //
    // 坐标：全部用浮层的客户坐标（= 截图位图自己的坐标，shot 画在 0,0），
    // 所以预览和"确认时合成进图片"可以共用同一套画法 —— 所见即所得。
    //
    // 交互：
    //   工具条在选区左下角（放不下就翻到上方）：选择 / 箭头 / 方框 / 马赛克 / 文字 | 颜色 ×4 | 文字底 | A- A+ | 撤销
    //   快捷键：Esc 取消截图（选中图元时先取消选中）、Enter 确认、Ctrl+Z 撤销、A 箭头、R 方框、
    //           M 马赛克、T 文字、V 选择、1~4 颜色、B 文字底、Del 删除选中、[ ] 改字号
    //   选中一个图元后：拖动 = 移动，滚轮 = 改字号（文字）/ 粗细（其它），Del = 删除
    partial class OverlayForm
    {
        enum AnnotKind { Select = 0, Arrow = 1, Rect = 2, Mosaic = 3, Text = 4, Ocr = 5 }

        class Shape
        {
            public AnnotKind Kind;
            public PointF A, B;          // 箭头/方框/马赛克 = 起止点；文字 = A 是位置
            public string Text;
            public Color Color;
            public float W = 3f;
            public float Size = 20f;     // 文字字号（会再乘 _k）
            public Bitmap Cache;         // 马赛克结果缓存（每帧重算太贵）
            public Rectangle CacheRect;
        }

        static readonly Color[] AnnotColors = {
            Color.FromArgb(238, 70, 90),    // 红（默认，圈重点最常用）
            Color.FromArgb(250, 176, 42),   // 黄
            Color.FromArgb(0, 150, 240),    // 蓝
            Color.FromArgb(26, 28, 34)      // 黑
        };

        readonly List<Shape> _shapes = new List<Shape>();
        Shape _drawing = null;               // 正在拖的那一个（松手才进 _shapes）
        Shape _sel = null;                   // 当前选中的图元（可拖动/改字号/删除）
        Shape _dragShape = null;             // 正在拖动的图元
        PointF _dragFromShape;
        AnnotKind _tool = AnnotKind.Select;
        Color _annotColor = AnnotColors[0];
        TextBox _textBox = null;
        Rectangle _toolRect = Rectangle.Empty;      // 工具条整体
        Rectangle[] _toolBtns = new Rectangle[0];   // 每个按钮的位置（含颜色点、撤销）
        int _toolHover = -1;
        Rectangle _introRect = Rectangle.Empty;     // 首次教程面板

        const int BtnW = 34;                 // 都会被 _k 缩放
        const int BtnH = 30;
        const int Gap = 6;
        const int IdxBg = 6 + 4;             // 工具 6 个（选择/箭头/方框/马赛克/文字/取字），颜色点占 6..9
        const int IdxSizeDown = IdxBg + 1;
        const int IdxSizeUp = IdxBg + 2;
        const int IdxUndo = IdxBg + 3;
        const int BtnCount = IdxBg + 4;

        // ---------- 几何 / 命中 ----------
        static RectangleF RectOf(PointF a, PointF b)
        {
            return new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        }

        static Rectangle ToRect(RectangleF r)
        {
            return new Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), Math.Max(1, (int)Math.Round(r.Width)), Math.Max(1, (int)Math.Round(r.Height)));
        }

        SizeF TextSize(Shape s)
        {
            string t = string.IsNullOrEmpty(s.Text) ? " " : s.Text;
            using (Font f = new Font("Microsoft YaHei UI", Math.Max(6f, s.Size * _k), FontStyle.Bold))
            {
                Size sz = TextRenderer.MeasureText(t, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                return new SizeF(sz.Width, sz.Height);
            }
        }

        // 图元的外框（文字按实际排版量；其它按起止点）
        RectangleF ShapeBounds(Shape s)
        {
            if (s == null) return RectangleF.Empty;
            if (s.Kind != AnnotKind.Text) return RectOf(s.A, s.B);
            SizeF sz = TextSize(s);
            return new RectangleF(s.A.X - 3 * _k, s.A.Y - 2 * _k, sz.Width + 7 * _k, sz.Height + 5 * _k);
        }

        // 命中图元：从后往前找（后画的在上层）
        Shape HitShape(PointF p)
        {
            for (int i = _shapes.Count - 1; i >= 0; i--)
            {
                Shape s = _shapes[i];
                RectangleF r = ShapeBounds(s);
                if (s.Kind != AnnotKind.Text)
                {
                    float pad = Math.Max(6f, s.W * _k + 3f);
                    r.Inflate(pad, pad);
                }
                if (r.Contains(p)) return s;
            }
            return null;
        }

        void SelectShape(Shape s)
        {
            if (_sel == s) return;
            _sel = s;
            Invalidate();
        }

        void MoveShape(Shape s, float dx, float dy)
        {
            s.A = new PointF(s.A.X + dx, s.A.Y + dy);
            if (s.Kind != AnnotKind.Text) s.B = new PointF(s.B.X + dx, s.B.Y + dy);
            if (s.Cache != null) { try { s.Cache.Dispose(); } catch { } s.Cache = null; }   // 马赛克跟着挪，得重算
        }

        // 滚轮/按钮调大小：文字改字号，其它改线条粗细
        void ResizeShape(Shape s, float delta)
        {
            if (s == null) return;
            if (s.Kind == AnnotKind.Text)
            {
                s.Size = Math.Max(9f, Math.Min(160f, s.Size + delta * 2f));
                _textSize = s.Size;          // 下一个新文字也用这个大小
            }
            else
                s.W = Math.Max(1f, Math.Min(24f, s.W + delta * 0.4f));
            Invalidate();
        }

        // ---------- 工具条布局 ----------
        // 原则：**优先放在选区外面**，别压住用户正要截的内容。
        // 以前只有"下面放不下就翻到上面"，再放不下就被夹到边距里 —— 选区一大就压在截图上了。
        int _toolAlpha = 255;        // 鼠标不在附近时自动变淡（不挡内容），靠近就完全不透明

        void PlaceToolbar()
        {
            int bw = (int)(BtnW * _k), bh = (int)(BtnH * _k), gp = (int)(Gap * _k);
            int n = BtnCount;
            int total = n * bw + (n - 1) * gp + gp * 2;
            int h = bh + gp * 2;
            RectangleF sb = SelBounds();

            // 按"当前这块屏幕"来算（多屏时别摆到别的屏去）
            Rectangle scr = ScreenFor(_vs, _hasSel ? new Point((int)_c.X, (int)_c.Y) : Point.Empty, _hasSel);
            int cl = scr.Left - _vs.Left, ct = scr.Top - _vs.Top;
            int cr = scr.Right - _vs.Left, cb = scr.Bottom - _vs.Top;

            int x = (int)Math.Max(cl + 8, sb.Left);
            if (x + total > cr - 8) x = Math.Max(cl + 8, cr - 8 - total);

            int below = (int)sb.Bottom + 12;          // 选区下方（外面）
            int above = (int)sb.Top - h - 12;         // 选区上方（外面）
            int y;
            if (below + h <= cb - 8) y = below;
            else if (above >= ct + 8) y = above;
            else
            {
                // 上下都放不下（选区几乎占满屏幕）：挑"外面空一点"的那一侧，
                // 实在没地方才允许压一点，并且配合下面的自动变淡，不至于挡住看不清
                int roomAbove = (int)sb.Top - ct - 8;
                int roomBelow = cb - 8 - (int)sb.Bottom;
                y = (roomBelow >= roomAbove) ? (cb - 8 - h) : (ct + 8);
            }
            if (y < ct + 8) y = ct + 8;
            if (y + h > cb - 8) y = cb - 8 - h;

            // 别压住左下角那个「比例」按钮（选区别在左下角时正好会撞上）
            Rectangle avoid = _toggleRect;
            if (_chipsOpen && _chips != null && _chips.Length > 0) avoid = Rectangle.Union(avoid, _chips[0].Rect);
            if (new Rectangle(x, y, total, h).IntersectsWith(avoid))
            {
                int up = avoid.Top - h - 6;
                y = (up >= ct + 8) ? up : Math.Min(cb - 8 - h, avoid.Bottom + 6);
                if (y < ct + 8) y = ct + 8;
            }

            _toolRect = new Rectangle(x, y, total, h);
            _toolBtns = new Rectangle[n];
            int cx = x + gp, cy = y + gp;
            for (int i = 0; i < n; i++) { _toolBtns[i] = new Rectangle(cx, cy, bw, bh); cx += bw + gp; }
        }

        bool ToolbarVisible() { return _hasSel && _sz.Width > 20 && _sz.Height > 20; }

        // 鼠标离工具条远就变淡（不挡截图），靠近就恢复不透明。
        // 只做"远/近"两档、阈值给足余量，不做连续渐变 —— 免得看着晃。
        void UpdateToolAlpha()
        {
            if (_toolRect.Width == 0) { _toolAlpha = 255; return; }
            try
            {
                Point cp = PointToClient(Cursor.Position);
                int dx = 0, dy = 0;
                if (cp.X < _toolRect.Left) dx = _toolRect.Left - cp.X;
                else if (cp.X > _toolRect.Right) dx = cp.X - _toolRect.Right;
                if (cp.Y < _toolRect.Top) dy = _toolRect.Top - cp.Y;
                else if (cp.Y > _toolRect.Bottom) dy = cp.Y - _toolRect.Bottom;
                int d = (int)Math.Sqrt(dx * dx + dy * dy);
                int want = (d < 90) ? 255 : 165;
                if (want != _toolAlpha) _toolAlpha = want;
            }
            catch { _toolAlpha = 255; }
        }

        // ---------- 画标注内容（预览与合成共用） ----------
        void DrawAnnotationShapes(Graphics g)
        {
            for (int i = 0; i < _shapes.Count; i++) DrawOne(g, _shapes[i]);
            if (_drawing != null) DrawOne(g, _drawing);
        }

        void DrawOne(Graphics g, Shape s)
        {
            if (s == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            switch (s.Kind)
            {
                case AnnotKind.Arrow:
                    {
                        using (Pen p = new Pen(s.Color, s.W * _k))
                        {
                            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                            g.DrawLine(p, s.A, s.B);
                        }
                        DrawArrowHead(g, s.A, s.B, s.Color, s.W * _k);
                        break;
                    }
                case AnnotKind.Rect:
                    {
                        RectangleF r = RectOf(s.A, s.B);
                        using (Pen p = new Pen(s.Color, s.W * _k))
                        {
                            p.Alignment = PenAlignment.Inset;
                            using (GraphicsPath path = Gfx.Round(r, Math.Min(6f, r.Height / 3f)))
                                g.DrawPath(p, path);
                        }
                        break;
                    }
                case AnnotKind.Mosaic:
                    {
                        Rectangle r = ToRect(RectOf(s.A, s.B));
                        if (r.Width < 2 || r.Height < 2) break;
                        if (s.Cache == null || s.CacheRect != r)
                        {
                            if (s.Cache != null) { try { s.Cache.Dispose(); } catch { } s.Cache = null; }
                            s.Cache = MosaicOf(_shot, r);
                            s.CacheRect = r;
                        }
                        if (s.Cache == null) break;
                        g.DrawImageUnscaled(s.Cache, r.Left, r.Top);
                        break;
                    }
                case AnnotKind.Ocr:
                    {
                        // 取字时拖出来的框：只是"要认哪一块"的示意，不会画进图里
                        RectangleF r = RectOf(s.A, s.B);
                        using (Pen p = new Pen(Color.FromArgb(245, 166, 35), 1.8f * _k))
                        {
                            p.DashStyle = DashStyle.Dash;
                            g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                        }
                        break;
                    }
                case AnnotKind.Text:
                    {
                        if (string.IsNullOrEmpty(s.Text)) break;
                        float fs = Math.Max(6f, s.Size * _k);
                        if (_textBg)
                        {
                            using (Font f = new Font("Microsoft YaHei UI", fs, FontStyle.Bold))
                            using (SolidBrush bg = new SolidBrush(Color.FromArgb(165, 255, 255, 255)))
                            {
                                SizeF sz = g.MeasureString(s.Text, f);
                                g.FillRectangle(bg, s.A.X - 3 * _k, s.A.Y - 2 * _k, sz.Width + 6 * _k, sz.Height + 2 * _k);
                            }
                        }
                        using (Font f = new Font("Microsoft YaHei UI", fs, FontStyle.Bold))
                        using (SolidBrush b = new SolidBrush(s.Color))
                            g.DrawString(s.Text, f, b, s.A);
                        break;
                    }
            }
        }

        void DrawArrowHead(Graphics g, PointF from, PointF to, Color c, float w)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 2f) return;
            float ux = dx / len, uy = dy / len;
            float head = Math.Max(9f * _k, w * 3.2f);
            float half = head * 0.5f;
            PointF p1 = new PointF(to.X - ux * head - uy * half, to.Y - uy * head + ux * half);
            PointF p2 = new PointF(to.X - ux * head + uy * half, to.Y - uy * head - ux * half);
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, new PointF[] { to, p1, p2 });
        }

        // 马赛克：先把这块缩小，再放大回原来的大小（放大用最近邻 → 变成色块）。
        // 返回的位图尺寸 == 请求的矩形，所以画的时候直接贴在 r 的左上角就行。
        static Bitmap MosaicOf(Bitmap src, Rectangle r)
        {
            if (src == null || r.Width < 2 || r.Height < 2) return null;
            Rectangle clip = Rectangle.Intersect(r, new Rectangle(0, 0, src.Width, src.Height));
            if (clip.Width < 2 || clip.Height < 2) return null;
            int block = Math.Max(6, (int)Math.Round(Math.Min(clip.Width, clip.Height) / 12f));
            int sw = Math.Max(1, clip.Width / block), sh = Math.Max(1, clip.Height / block);
            try
            {
                Bitmap big = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
                using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, sw, sh), clip, GraphicsUnit.Pixel);
                    }
                    using (Graphics g = Graphics.FromImage(big))
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.SmoothingMode = SmoothingMode.None;
                        g.DrawImage(small, new Rectangle(0, 0, r.Width, r.Height));
                    }
                }
                return big;
            }
            catch { return null; }
        }

        // 关掉浮层时把马赛克缓存放掉（不然每画一次就漏一块内存）
        void DisposeAnnotationCaches()
        {
            for (int i = 0; i < _shapes.Count; i++)
                if (_shapes[i].Cache != null) { try { _shapes[i].Cache.Dispose(); } catch { } _shapes[i].Cache = null; }
            if (_drawing != null && _drawing.Cache != null) { try { _drawing.Cache.Dispose(); } catch { } _drawing.Cache = null; }
        }

        // ---------- 画工具条 ----------
        void PaintToolbar(Graphics g, int alpha)
        {
            if (!ToolbarVisible() || _toolBtns.Length == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath bgp = Gfx.Round(_toolRect, 10f * _k))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(238 * alpha / 255f), 22, 24, 28)))
                    g.FillPath(b, bgp);
                using (Pen p = new Pen(Color.FromArgb((int)(60 * alpha / 255f), 255, 255, 255), 1f))
                    g.DrawPath(p, bgp);
            }

            for (int i = 0; i < _toolBtns.Length; i++)
            {
                Rectangle r = _toolBtns[i];
                bool isTool = i < 6;
                bool sel = isTool && ((AnnotKind)i == _tool);
                if (sel || i == _toolHover)
                {
                    using (GraphicsPath bp = Gfx.Round(r, 7f * _k))
                    using (SolidBrush b = new SolidBrush(sel ? Color.FromArgb(235, 0, 122, 204) : Color.FromArgb(90, 255, 255, 255)))
                        g.FillPath(b, bp);
                }

                Color ic = Color.White;
                if (i >= 6 && i < 6 + AnnotColors.Length)
                {
                    // 颜色点：当前色描粗白边；其余也描一圈细边 —— 黑点在深色工具条上不然看不见
                    int ci = i - 6;
                    bool cur = (_annotColor.ToArgb() == AnnotColors[ci].ToArgb());
                    int d = (int)(15 * _k);
                    Rectangle cr = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
                    using (SolidBrush b = new SolidBrush(AnnotColors[ci])) g.FillEllipse(b, cr);
                    using (Pen ring = new Pen(Color.FromArgb(cur ? 255 : 140, 255, 255, 255), cur ? 2.2f : 1.2f))
                        g.DrawEllipse(ring, cr);
                    continue;
                }

                RectangleF d2 = Inset(r, 9f * _k);
                switch (i)
                {
                    case 0:      // 选择（指针）
                        {
                            using (SolidBrush b = new SolidBrush(ic))
                                g.FillPolygon(b, new PointF[] {
                                    new PointF(d2.Left + d2.Width * 0.25f, d2.Top),
                                    new PointF(d2.Left + d2.Width * 0.25f, d2.Bottom),
                                    new PointF(d2.Left + d2.Width * 0.62f, d2.Bottom - d2.Height * 0.30f),
                                    new PointF(d2.Right, d2.Top + d2.Height * 0.12f) });
                            break;
                        }
                    case 1:      // 箭头
                        {
                            using (Pen p = new Pen(ic, 2f * _k))
                            {
                                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                                g.DrawLine(p, d2.Left, d2.Bottom, d2.Right * 0.92f + d2.Left * 0.08f, d2.Top + d2.Height * 0.08f);
                            }
                            using (SolidBrush b = new SolidBrush(ic))
                            {
                                g.FillPolygon(b, new PointF[] {
                                    new PointF(d2.Right, d2.Top),
                                    new PointF(d2.Right - d2.Width * 0.42f, d2.Top + d2.Height * 0.10f),
                                    new PointF(d2.Right - d2.Width * 0.10f, d2.Top + d2.Height * 0.42f) });
                            }
                            break;
                        }
                    case 2:      // 方框
                        using (Pen p = new Pen(ic, 2f * _k)) g.DrawRectangle(p, d2.Left, d2.Top, d2.Width, d2.Height);
                        break;
                    case 3:      // 马赛克：田字格
                        {
                            float hw = d2.Width / 2f, hh = d2.Height / 2f;
                            using (SolidBrush b = new SolidBrush(ic))
                            {
                                g.FillRectangle(b, d2.Left, d2.Top, hw - 1, hh - 1);
                                g.FillRectangle(b, d2.Left + hw + 1, d2.Top, hw - 1, hh - 1);
                                g.FillRectangle(b, d2.Left, d2.Top + hh + 1, hw - 1, hh - 1);
                                g.FillRectangle(b, d2.Left + hw + 1, d2.Top + hh + 1, hw - 1, hh - 1);
                            }
                            break;
                        }
                    case 4:      // 文字
                        using (Font f = new Font("Microsoft YaHei UI", 12f * _k, FontStyle.Bold))
                        using (SolidBrush b = new SolidBrush(ic))
                        {
                            StringFormat sf = new StringFormat();
                            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                            g.DrawString("T", f, b, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                        }
                        break;
                    case IdxBg:  // 文字底：方块填实=带白底，只描边=不带底
                        {
                            RectangleF sq = new RectangleF(d2.Left, d2.Top + d2.Height * 0.12f, d2.Width, d2.Height * 0.88f);
                            if (_textBg)
                                using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, sq);
                            using (Pen p = new Pen(Color.White, 1.4f * _k)) g.DrawRectangle(p, sq.X, sq.Y, sq.Width, sq.Height);
                            using (Font f = new Font("Microsoft YaHei UI", 8.5f * _k, FontStyle.Bold))
                            using (SolidBrush b = new SolidBrush(_textBg ? Color.FromArgb(22, 24, 28) : Color.White))
                            {
                                StringFormat sf = new StringFormat();
                                sf.Alignment = StringAlignment.Center;
                                sf.LineAlignment = StringAlignment.Center;
                                g.DrawString("T", f, b, sq, sf);
                            }
                            break;
                        }
                    case IdxSizeDown:   // A-
                    case IdxSizeUp:     // A+
                        {
                            using (Font f = new Font("Microsoft YaHei UI", 13f * _k, FontStyle.Bold))
                            using (SolidBrush b = new SolidBrush(ic))
                                g.DrawString("A", f, b, d2.Left - 1 * _k, d2.Top - 1 * _k);
                            using (Pen p = new Pen(ic, 1.8f * _k))
                            {
                                float mx = d2.Right - 2 * _k, my = d2.Top + d2.Height * 0.34f;
                                g.DrawLine(p, mx - 5 * _k, my, mx, my);
                                if (i == IdxSizeUp) g.DrawLine(p, mx - 2.5f * _k, my - 2.5f * _k, mx - 2.5f * _k, my + 2.5f * _k);
                            }
                            break;
                        }
                    case 5:             // 取字工具：一个"字"比任何图标都好认
                        using (Font f = new Font("Microsoft YaHei UI", 13f * _k, FontStyle.Bold))
                        using (SolidBrush b = new SolidBrush(Ocr.Available ? ic : Color.FromArgb(120, 255, 255, 255)))
                        {
                            StringFormat sf = new StringFormat();
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            g.DrawString("字", f, b, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                        }
                        break;
                    default:     // 撤销
                        using (Pen p = new Pen(_shapes.Count > 0 ? ic : Color.FromArgb(110, 255, 255, 255), 2f * _k))
                        {
                            g.DrawArc(p, d2.Left, d2.Top + d2.Height * 0.15f, d2.Width, d2.Height * 0.9f, 30, 250);
                            g.DrawLine(p, d2.Left + d2.Width * 0.02f, d2.Top + d2.Height * 0.42f, d2.Left + d2.Width * 0.28f, d2.Top + d2.Height * 0.10f);
                            g.DrawLine(p, d2.Left + d2.Width * 0.02f, d2.Top + d2.Height * 0.42f, d2.Left + d2.Width * 0.32f, d2.Top + d2.Height * 0.55f);
                        }
                        break;
                }
            }
        }

        static RectangleF Inset(Rectangle r, float pad)
        {
            return new RectangleF(r.X + pad, r.Y + pad, Math.Max(2, r.Width - pad * 2), Math.Max(2, r.Height - pad * 2));
        }

        // 选中的图元：虚线框 + 一句"能怎么改"（拖动/滚轮/删除 全靠它被看见）
        void PaintShapeSelection(Graphics g)
        {
            if (_sel == null || _shapes.Count == 0) return;
            RectangleF r = ShapeBounds(_sel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Color.FromArgb(235, 0, 174, 255), 1.6f))
            {
                p.DashStyle = DashStyle.Dash;
                g.DrawRectangle(p, r.X - 3 * _k, r.Y - 3 * _k, r.Width + 6 * _k, r.Height + 6 * _k);
            }
            string hint = (_sel.Kind == AnnotKind.Text) ? "拖动移动　·　滚轮 / A+/A- 改字号　·　Del 删除"
                                                        : "拖动移动　·　滚轮改粗细　·　Del 删除";
            using (Font f = new Font("Microsoft YaHei UI", 9f * _k, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(hint, f);
                int pad = (int)(7 * _k);
                int w = (int)sz.Width + pad * 2, h = (int)sz.Height + pad;
                int x = (int)(r.Left);
                int y = (int)(r.Top - h - 8 * _k);
                if (y < 6) y = (int)(r.Bottom + 8 * _k);
                if (x + w > _vs.Width - 6) x = _vs.Width - 6 - w;
                if (x < 6) x = 6;
                using (GraphicsPath bp = Gfx.Round(new Rectangle(x, y, w, h), 7f * _k))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(238, 22, 24, 28)))
                    g.FillPath(b, bp);
                using (SolidBrush tb = new SolidBrush(Color.White))
                    g.DrawString(hint, f, tb, x + pad, y + pad / 2f);
            }
        }

        // 第一次进截图界面：中间来一块正经的教程面板（不是角落里一句小提示）
        void PaintIntroPanel(Graphics g)
        {
            if (!_annotHint) return;
            int w = (int)(440 * _k), h = (int)(292 * _k);
            RectangleF sb = _hasSel ? SelBounds() : new RectangleF(_vs.Width / 2f - 200 * _k, _vs.Height / 2f - 130 * _k, 400 * _k, 260 * _k);
            int x = (int)(sb.Left + (sb.Width - w) / 2f);
            int y = (int)(sb.Top + Math.Max(20 * _k, (sb.Height - h) / 2f));
            if (x < 12) x = 12;
            if (x + w > _vs.Width - 12) x = _vs.Width - 12 - w;
            if (y < 12) y = 12;
            if (y + h > _vs.Height - 12) y = _vs.Height - 12 - h;
            _introRect = new Rectangle(x, y, w, h);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath bp = Gfx.Round(_introRect, 16f * _k))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(245, 22, 24, 30))) g.FillPath(b, bp);
                using (Pen pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1.4f)) g.DrawPath(pen, bp);
            }

            int pad = (int)(22 * _k);
            using (Font ft = new Font("Microsoft YaHei UI", 15f * _k, FontStyle.Bold))
            using (SolidBrush bt = new SolidBrush(Color.White))
                g.DrawString("截图浮层：三步搞定", ft, bt, x + pad, y + pad - 4 * _k);

            string[] lines = {
                "①  按住左键拖出要截的区域（四角缩放、圆点旋转、中间拖动）",
                "②  用下面的工具条标注：箭头 A · 方框 R · 马赛克 M · 文字 T",
                "      颜色 1~4 · 文字底 B · 字号 A+/A- 或滚轮 · Ctrl+Z 撤销",
                "      画完的文字/方框可以直接拖动、滚轮改大小，Del 删掉",
                "③  选「字」工具（或按 O）拖一个框圈住文字 = 取字，框越小越准；",
                "      取字窗口里还能一键翻译成中文/英文",
                "④  双击选区或按回车 = 确认（Esc 取消），图直接进轮盘"
            };
            int ly = y + pad + (int)(34 * _k);
            using (Font fl = new Font("Microsoft YaHei UI", 10f * _k))
            using (SolidBrush bl = new SolidBrush(Color.FromArgb(226, 232, 240)))
                for (int i = 0; i < lines.Length; i++)
                    g.DrawString(lines[i], fl, bl, x + pad, ly + i * (int)(24 * _k));

            Rectangle btn = new Rectangle(x + w - pad - (int)(124 * _k), y + h - pad - (int)(34 * _k), (int)(124 * _k), (int)(34 * _k));
            using (GraphicsPath bp = Gfx.Round(btn, 9f * _k))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 122, 204)))
                g.FillPath(b, bp);
            using (Font fb = new Font("Microsoft YaHei UI", 10.5f * _k, FontStyle.Bold))
            using (SolidBrush bb = new SolidBrush(Color.White))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString("开始用", fb, bb, btn, sf);
            }
            using (Font fh = new Font("Microsoft YaHei UI", 8.5f * _k))
            using (SolidBrush bh = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                g.DrawString("点一下面板或按任意键就开始（只提示这一次）", fh, bh, x + pad, y + h - pad - (int)(18 * _k));
        }

        // ---------- 鼠标 / 键盘钩子（由 OverlayForm 主文件调进来） ----------
        bool AnnotMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return false;

            if (_annotHint)
            {
                bool inPanel = _introRect.Contains(e.Location);
                _annotHint = false;
                Invalidate();
                if (inPanel) return true;        // 点面板本身：就当作"我知道了"
            }

            if (ToolbarVisible())
            {
                for (int i = 0; i < _toolBtns.Length; i++)
                {
                    if (!_toolBtns[i].Contains(e.Location)) continue;
                    if (i < 6) { EndText(true); _tool = (AnnotKind)i; }
                    else if (i < 6 + AnnotColors.Length) { _annotColor = AnnotColors[i - 6]; }
                    else if (i == IdxBg) { _textBg = !_textBg; SaveTextBg(); }
                    else if (i == IdxSizeDown) { if (_sel != null) ResizeShape(_sel, -1f); else SetNextTextSize(_textSize - 2f); }
                    else if (i == IdxSizeUp) { if (_sel != null) ResizeShape(_sel, 1f); else SetNextTextSize(_textSize + 2f); }

                    else Undo();
                    Invalidate();
                    return true;
                }
                if (_toolRect.Contains(e.Location)) return true;   // 点在工具条空白处：别当成长按选图
            }

            // 点到已有的图元上：选中它并准备拖动（选择工具、文字工具都支持）
            Shape hit = HitShape(e.Location);
            if (hit != null && (_tool == AnnotKind.Select || _tool == AnnotKind.Text))
            {
                EndText(true);
                SelectShape(hit);
                _dragShape = hit;
                _dragFromShape = e.Location;
                return true;
            }
            if (hit != null && hit.Kind == AnnotKind.Text && _tool != AnnotKind.Select)
            {
                EndText(true);
                SelectShape(hit);
                _dragShape = hit;
                _dragFromShape = e.Location;
                return true;
            }

            if (_tool == AnnotKind.Select)
            {
                SelectShape(null);
                return false;                  // 交回给原来的框选/移动逻辑
            }
            if (!_hasSel) return false;
            if (!InsideSel(e.Location))
            {
                // 选了标注工具还点到选区外：什么都不做。
                // 否则会落回"新建选区"，把刚画的标注全丢掉（画错一笔就白干，太气人）。
                // 想重新框选按 V（或 Esc 重来）。
                return true;
            }
            if (_textBox != null) EndText(true);

            if (_tool == AnnotKind.Text) { BeginText(e.Location); return true; }

            if (_tool == AnnotKind.Ocr)
            {
                // 取字工具：拖一个框圈住要认的文字（框小=只是想认整块选区）
                _drawing = new Shape();
                _drawing.Kind = AnnotKind.Ocr;
                _drawing.A = e.Location;
                _drawing.B = e.Location;
                SelectShape(null);
                Invalidate();
                return true;
            }

            _drawing = new Shape();
            _drawing.Kind = _tool;
            _drawing.A = e.Location;
            _drawing.B = e.Location;
            _drawing.Color = _annotColor;
            _drawing.W = 3f;
            SelectShape(null);
            Invalidate();
            return true;
        }

        bool AnnotMouseMove(MouseEventArgs e)
        {
            if (_dragShape != null)
            {
                MoveShape(_dragShape, e.Location.X - _dragFromShape.X, e.Location.Y - _dragFromShape.Y);
                _dragFromShape = e.Location;
                Invalidate();
                return true;
            }

            int h = -1;
            if (ToolbarVisible())
                for (int i = 0; i < _toolBtns.Length; i++) if (_toolBtns[i].Contains(e.Location)) { h = i; break; }
            if (h != _toolHover) { _toolHover = h; Invalidate(); }

            if (_drawing == null) return false;
            _drawing.B = e.Location;
            Invalidate();
            return true;
        }

        bool AnnotMouseUp(MouseEventArgs e)
        {
            if (_dragShape != null) { _dragShape = null; Invalidate(); return true; }
            if (_drawing == null) return false;
            Shape s = _drawing;
            _drawing = null;
            if (s.Kind == AnnotKind.Ocr)
            {
                // 取字：不去动 _shapes（它不是标注，不该被画进成品图）
                RectangleF rc = RectOf(s.A, s.B);
                Invalidate();
                DoOcrRegion(rc);
                return true;
            }
            RectangleF r = RectOf(s.A, s.B);
            bool ok = (s.Kind == AnnotKind.Arrow) || (r.Width >= 4 && r.Height >= 4);
            if (ok) { _shapes.Add(s); _annotHint = false; }
            Invalidate();
            return true;
        }

        // 滚轮：选中了图元就改大小（文字改字号），没选中就还给主逻辑
        internal bool AnnotWheel(MouseEventArgs e)
        {
            if (_sel == null) return false;
            ResizeShape(_sel, e.Delta > 0 ? 1f : -1f);
            return true;
        }

        // 返回 true = 这个键已经被标注逻辑用掉了
        bool AnnotKey(KeyEventArgs e)
        {
            if (_textBox != null) return false;        // 正在打字：键都归输入框

            bool ctrl = (e.Modifiers & Keys.Control) == Keys.Control;
            if (ctrl && e.KeyCode == Keys.Z) { Undo(); return true; }
            if (ctrl) return false;

            switch (e.KeyCode)
            {
                case Keys.V: _tool = AnnotKind.Select; break;
                case Keys.A: _tool = AnnotKind.Arrow; break;
                case Keys.R: _tool = AnnotKind.Rect; break;
                case Keys.M: _tool = AnnotKind.Mosaic; break;
                case Keys.T: _tool = AnnotKind.Text; break;
                case Keys.O: _tool = AnnotKind.Ocr; break;      // O = 取字（OCR）
                case Keys.B: _textBg = !_textBg; SaveTextBg(); break;
                case Keys.OemOpenBrackets: ResizeShape(_sel, -1f); return true;
                case Keys.OemCloseBrackets: ResizeShape(_sel, 1f); return true;
                case Keys.Delete:
                case Keys.Back:
                    if (_sel != null)
                    {
                        _shapes.Remove(_sel);
                        if (_sel.Cache != null) { try { _sel.Cache.Dispose(); } catch { } }
                        _sel = null;
                        Invalidate();
                        return true;
                    }
                    return false;
                case Keys.Escape:
                    if (_sel != null) { _sel = null; Invalidate(); return true; }   // 先取消选中，再按一次才是取消截图
                    return false;
                case Keys.D1: case Keys.NumPad1: _annotColor = AnnotColors[0]; break;
                case Keys.D2: case Keys.NumPad2: _annotColor = AnnotColors[1]; break;
                case Keys.D3: case Keys.NumPad3: _annotColor = AnnotColors[2]; break;
                case Keys.D4: case Keys.NumPad4: _annotColor = AnnotColors[3]; break;
                default: return false;
            }
            Invalidate();
            return true;
        }

        // "取字中…"的小提示：识别在后台跑，但得让用户看见"它在干活"（不显示的话还是像卡住）
        void PaintOcrBusy(Graphics g)
        {
            if (!_ocrBusy) return;
            string txt = "取字中…";
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Font f = new Font("Microsoft YaHei UI", 11f * _k, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(txt, f);
                int pad = (int)(14 * _k);
                int w = (int)sz.Width + pad * 2, h = (int)sz.Height + pad;
                Rectangle box = new Rectangle(_vs.Width / 2 - w / 2, 24, w, h);
                using (GraphicsPath bp = Gfx.Round(box, 9f * _k))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(238, 22, 24, 28)))
                    g.FillPath(b, bp);
                using (SolidBrush tb = new SolidBrush(Color.White))
                    g.DrawString(txt, f, tb, box.X + pad, box.Y + pad / 2f);
            }
        }

        void SaveTextBg()
        {
            if (_set == null) return;
            try { _set.TextBg = _textBg; _set.Save(); } catch { }
        }

        // 取字（OCR）：识别整块选区里的文字。
        // 更准的用法是选「字」工具拖一个框（DoOcrRegion）—— 框小一点、只圈文字，识别率明显更好。
        internal void DoOcr()
        {
            if (!_hasSel || _shot == null || _sz.Width < 4 || _sz.Height < 4) return;
            EndText(true);
            Bitmap crop = null;
            try { crop = CropSelection(false); } catch { }
            if (crop != null) StartOcrAsync(crop);
        }

        // 拖出来的框里取字：从**原图**（不带标注）裁这一块去认，框越贴合文字越准
        internal void DoOcrRegion(RectangleF rect)
        {
            if (!_hasSel || _shot == null) return;
            Rectangle rc = ToRect(rect);
            if (rc.Width < 10 || rc.Height < 10) { DoOcr(); return; }      // 只是点了一下：认整块选区
            rc = Rectangle.Intersect(rc, new Rectangle(0, 0, _shot.Width, _shot.Height));
            if (rc.Width < 4 || rc.Height < 4) return;
            EndText(true);
            try
            {
                Bitmap crop = new Bitmap(rc.Width, rc.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(crop)) g.DrawImageUnscaled(_shot, -rc.Left, -rc.Top);
                StartOcrAsync(crop);
            }
            catch (Exception ex) { Err.Log("OcrCrop", ex); }
        }

        bool _ocrBusy = false;

        // 取字丢到后台线程去做 —— 以前是同步跑的：界面整整卡 100~300ms、鼠标变等待圈、
        // 还没有任何反馈，用户当然觉得"性能垃圾"。现在轮盘照常能用，识别完结果框自己弹出来。
        void StartOcrAsync(Bitmap crop)
        {
            if (crop == null) return;
            if (_ocrBusy) { try { crop.Dispose(); } catch { } return; }     // 上一次还没完，直接忽略这一次
            _ocrBusy = true;

            // 先在 UI 线程把像素拷出来：后台线程就完全不碰 GDI 位图了
            byte[] px = null; int pw = 0, ph = 0;
            try { px = Ocr.PixelsOf(crop, out pw, out ph); } catch { px = null; }
            if (px == null) { try { crop.Dispose(); } catch { } _ocrBusy = false; return; }
            try { crop.Dispose(); } catch { }        // 像素到手，位图就可以扔了

            Invalidate();                            // 让"取字中…"立刻显示出来
            byte[] data = px; int w = pw, h = ph;
            System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate()
            {
                string err = null, txt = null;
                try { txt = Ocr.RecognizePixels(data, w, h, out err); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        _ocrBusy = false;
                        ShowOcrResult(txt, err);
                    }));
                }
                catch { _ocrBusy = false; }
            }));
            th.IsBackground = true;
            th.Start();
        }

        void ShowOcrResult(string txt, string err)
        {
            if (txt == null)
            {
                try
                {
                    MessageBox.Show(this, err ?? "识别失败了", "取字", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
                return;
            }
            try
            {
                bool wasTop = TopMost;
                TopMost = false;
                using (OcrForm of = new OcrForm(txt))
                {
                    of.TopMost = true;
                    of.ShowDialog(this);
                }
                TopMost = wasTop;
            }
            catch (Exception ex) { Err.Log("OcrForm", ex); }
        }

        void Undo()
        {
            if (_shapes.Count == 0) return;
            Shape last = _shapes[_shapes.Count - 1];
            _shapes.RemoveAt(_shapes.Count - 1);
            if (last.Cache != null) { try { last.Cache.Dispose(); } catch { } }
            if (_sel == last) _sel = null;
            Invalidate();
        }

        // ---------- 文字工具 ----------
        void BeginText(Point at)
        {
            EndText(true);
            _textBox = new TextBox();
            _textBox.Font = new Font("Microsoft YaHei UI", Math.Max(9f, _textSize * _k), FontStyle.Bold);
            _textBox.ForeColor = _annotColor;
            _textBox.BackColor = Color.White;      // 不能给带透明度的颜色，WinForms 控件不支持
            _textBox.BorderStyle = BorderStyle.FixedSingle;
            _textBox.Location = new Point(at.X, Math.Max(0, at.Y));
            _textBox.Width = (int)(170 * _k);
            _textBox.KeyDown += new KeyEventHandler(delegate(object o, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; EndText(true); }
                else if (ke.KeyCode == Keys.Escape) { ke.SuppressKeyPress = true; EndText(false); }
            });
            // 点到别处（比如去点工具条）也要把字落下，不然打好的字会莫名其妙丢掉
            _textBox.Leave += new EventHandler(delegate(object o, EventArgs e2) { EndText(true); });
            Controls.Add(_textBox);
            _textBox.Focus();
        }

        // 新文字用的字号：跟着上一个文字走（改过一次就不用每次再调）
        float _textSize = 20f;

        // commit=true 且非空 -> 落成一个文字标注，并自动选中它（接着就能拖动/改字号）
        void EndText(bool commit)
        {
            if (_textBox == null) return;
            TextBox tb = _textBox;
            _textBox = null;
            string txt = tb.Text;
            Point at = tb.Location;
            try { Controls.Remove(tb); tb.Dispose(); } catch { }
            if (commit && !string.IsNullOrEmpty(txt))
            {
                Shape s = new Shape();
                s.Kind = AnnotKind.Text;
                s.A = new PointF(at.X, at.Y);
                s.Text = txt;
                s.Color = _annotColor;
                s.Size = _textSize;
                _shapes.Add(s);
                _sel = s;                       // 画完就选中：可以直接拖 / 滚轮改大小
                _annotHint = false;
            }
            Invalidate();
        }

        // 选了字号后，下一个新文字也用它
        internal void SetNextTextSize(float size)
        {
            _textSize = Math.Max(9f, Math.Min(160f, size));
            if (_sel != null && _sel.Kind == AnnotKind.Text)
            {
                _sel.Size = _textSize;
                Invalidate();
            }
        }
    }
}
