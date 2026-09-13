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
    //   工具条在选区左下角（放不下就翻到上方）：选择 / 箭头 / 方框 / 马赛克 / 文字 | 颜色 ×4 | 撤销
    //   快捷键：Esc 取消截图、Enter 确认、Ctrl+Z 撤销、A 箭头、R 方框、M 马赛克、T 文字、1~4 颜色、V 回到选择
    //   只有"选择"工具下，双击选区才是确认（画的时候不会误触）
    partial class OverlayForm
    {
        enum AnnotKind { Select = 0, Arrow = 1, Rect = 2, Mosaic = 3, Text = 4 }

        class Shape
        {
            public AnnotKind Kind;
            public PointF A, B;          // 箭头/方框/马赛克 = 起止点；文字 = A 是位置
            public string Text;
            public Color Color;
            public float W = 3f;
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
        AnnotKind _tool = AnnotKind.Select;
        Color _annotColor = AnnotColors[0];
        TextBox _textBox = null;
        Rectangle _toolRect = Rectangle.Empty;      // 工具条整体
        Rectangle[] _toolBtns = new Rectangle[0];   // 每个按钮的位置（含颜色点、撤销）
        int _toolHover = -1;

        const int BtnW = 34;                 // 都会被 _k 缩放
        const int BtnH = 30;
        const int Gap = 6;
        const int IdxBg = 5 + 4;             // 工具条上"文字底"按钮的下标（颜色点占 5..8）
        const int IdxUndo = IdxBg + 1;

        // ---------- 工具条布局 ----------
        void PlaceToolbar()
        {
            int bw = (int)(BtnW * _k), bh = (int)(BtnH * _k), gp = (int)(Gap * _k);
            int n = 5 + AnnotColors.Length + 2;      // 5 个工具 + 4 个颜色 + 文字底 + 撤销
            int total = n * bw + (n - 1) * gp + gp * 2;
            int h = bh + gp * 2;
            RectangleF sb = SelBounds();
            int x = (int)Math.Max(8, sb.Left);
            int y = (int)(sb.Bottom + 12);
            if (y + h > _vs.Height - 8) y = (int)(sb.Top - h - 12);      // 下面放不下就翻到上面
            if (y < 8) y = 8;
            if (x + total > _vs.Width - 8) x = Math.Max(8, _vs.Width - 8 - total);

            // 别压住左下角那个「比例」按钮（选区别在左下角时正好会撞上）
            Rectangle avoid = _toggleRect;
            if (_chipsOpen && _chips != null && _chips.Length > 0) avoid = Rectangle.Union(avoid, _chips[0].Rect);
            if (new Rectangle(x, y, total, h).IntersectsWith(avoid))
            {
                int up = avoid.Top - h - 6;
                y = (up >= 8) ? up : Math.Min(_vs.Height - 8 - h, avoid.Bottom + 6);
                if (y < 8) y = 8;
            }

            _toolRect = new Rectangle(x, y, total, h);
            _toolBtns = new Rectangle[n];
            int cx = x + gp, cy = y + gp;
            for (int i = 0; i < n; i++) { _toolBtns[i] = new Rectangle(cx, cy, bw, bh); cx += bw + gp; }
        }

        bool ToolbarVisible() { return _hasSel && _sz.Width > 20 && _sz.Height > 20; }

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
                case AnnotKind.Text:
                    {
                        if (string.IsNullOrEmpty(s.Text)) break;
                        if (_textBg)
                        {
                            using (Font f = new Font("Microsoft YaHei UI", 12f * _k, FontStyle.Bold))
                            using (SolidBrush bg = new SolidBrush(Color.FromArgb(165, 255, 255, 255)))
                            {
                                SizeF sz = g.MeasureString(s.Text, f);
                                g.FillRectangle(bg, s.A.X - 3 * _k, s.A.Y - 2 * _k, sz.Width + 6 * _k, sz.Height + 2 * _k);
                            }
                        }
                        using (Font f = new Font("Microsoft YaHei UI", 12f * _k, FontStyle.Bold))
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

        static RectangleF RectOf(PointF a, PointF b)
        {
            return new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        }

        static Rectangle ToRect(RectangleF r)
        {
            return new Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), Math.Max(1, (int)Math.Round(r.Width)), Math.Max(1, (int)Math.Round(r.Height)));
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
                bool isTool = i < 5;
                bool sel = isTool && ((AnnotKind)i == _tool);
                if (sel || i == _toolHover)
                {
                    using (GraphicsPath bp = Gfx.Round(r, 7f * _k))
                    using (SolidBrush b = new SolidBrush(sel ? Color.FromArgb(235, 0, 122, 204) : Color.FromArgb(90, 255, 255, 255)))
                        g.FillPath(b, bp);
                }

                Color ic = Color.White;
                if (i >= 5 && i < 5 + AnnotColors.Length)
                {
                    // 颜色点：当前色描粗白边；其余也描一圈细边 —— 黑点在深色工具条上不然看不见
                    int ci = i - 5;
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

        // 第一次用的时候，在工具条上方亮一条说明 —— 新功能不能被埋在托盘菜单里，
        // 用户得"一眼看到"。第一次画了标注 / 过了 10 秒就收掉。
        void PaintAnnotCoach(Graphics g)
        {
            if (!_annotHint || !ToolbarVisible() || _drawing != null) return;
            if ((DateTime.Now - _annotHintAt).TotalSeconds > 10) { _annotHint = false; return; }
            if (_shapes.Count > 0) { _annotHint = false; return; }

            string txt = "截图可以直接标注：箭头 A · 方框 R · 马赛克 M · 文字 T　（Ctrl+Z 撤销）";
            using (Font f = new Font("Microsoft YaHei UI", 10f * _k, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(txt, f);
                int pad = (int)(12 * _k);
                int w = (int)sz.Width + pad * 2;
                int h = (int)sz.Height + pad;
                int x = _toolRect.X + _toolRect.Width / 2 - w / 2;
                int y = _toolRect.Y - h - (int)(10 * _k);
                if (y < 8) y = _toolRect.Bottom + (int)(10 * _k);
                if (x < 8) x = 8;
                if (x + w > _vs.Width - 8) x = _vs.Width - 8 - w;

                Rectangle box = new Rectangle(x, y, w, h);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath bp = Gfx.Round(box, 9f * _k))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(242, 255, 196, 60)))
                    g.FillPath(b, bp);
                // 小箭头：指着下面的工具条
                bool below = (y < _toolRect.Y);
                float ax = _toolRect.X + _toolRect.Width / 2f;
                PointF[] tri = below
                    ? new PointF[] { new PointF(ax - 8 * _k, box.Bottom - 1), new PointF(ax + 8 * _k, box.Bottom - 1), new PointF(ax, box.Bottom + 9 * _k) }
                    : new PointF[] { new PointF(ax - 8 * _k, box.Top + 1), new PointF(ax + 8 * _k, box.Top + 1), new PointF(ax, box.Top - 9 * _k) };
                using (SolidBrush b = new SolidBrush(Color.FromArgb(242, 255, 196, 60))) g.FillPolygon(b, tri);
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(30, 26, 16)))
                    g.DrawString(txt, f, tb, x + pad, y + pad / 2 - 1);
            }
        }

        // ---------- 鼠标 / 键盘钩子（由 OverlayForm 主文件调进来） ----------
        bool AnnotMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return false;

            if (ToolbarVisible())
            {
                for (int i = 0; i < _toolBtns.Length; i++)
                {
                    if (!_toolBtns[i].Contains(e.Location)) continue;
                    if (i < 5) { EndText(true); _tool = (AnnotKind)i; }
                    else if (i < 5 + AnnotColors.Length) _annotColor = AnnotColors[i - 5];
                    else if (i == IdxBg) { _textBg = !_textBg; if (_set != null) { try { _set.TextBg = _textBg; _set.Save(); } catch { } } }
                    else Undo();
                    Invalidate();
                    return true;
                }
                if (_toolRect.Contains(e.Location)) return true;   // 点在工具条空白处：别当成长按选图
            }

            if (_tool == AnnotKind.Select) return false;           // 选择工具下，交回给原来的框选逻辑
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

            _drawing = new Shape();
            _drawing.Kind = _tool;
            _drawing.A = e.Location;
            _drawing.B = e.Location;
            _drawing.Color = _annotColor;
            _drawing.W = 3f;
            Invalidate();
            return true;
        }

        bool AnnotMouseMove(MouseEventArgs e)
        {
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
            if (_drawing == null) return false;
            Shape s = _drawing;
            _drawing = null;
            RectangleF r = RectOf(s.A, s.B);
            bool ok = (s.Kind == AnnotKind.Arrow) || (r.Width >= 4 && r.Height >= 4);
            if (ok) { _shapes.Add(s); _annotHint = false; }     // 画了一笔，首次提示就收掉
            Invalidate();
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
                case Keys.B: _textBg = !_textBg;
                    if (_set != null) { try { _set.TextBg = _textBg; _set.Save(); } catch { } }
                    break;
                case Keys.D1: case Keys.NumPad1: _annotColor = AnnotColors[0]; break;
                case Keys.D2: case Keys.NumPad2: _annotColor = AnnotColors[1]; break;
                case Keys.D3: case Keys.NumPad3: _annotColor = AnnotColors[2]; break;
                case Keys.D4: case Keys.NumPad4: _annotColor = AnnotColors[3]; break;
                default: return false;
            }
            Invalidate();
            return true;
        }

        void Undo()
        {
            if (_shapes.Count == 0) return;
            _shapes.RemoveAt(_shapes.Count - 1);
            Invalidate();
        }

        // ---------- 文字工具 ----------
        void BeginText(Point at)
        {
            EndText(true);
            _textBox = new TextBox();
            _textBox.Font = new Font("Microsoft YaHei UI", 12f * _k, FontStyle.Bold);
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

        // commit=true 且非空 -> 落成一个文字标注
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
                _shapes.Add(s);
            }
            Invalidate();
        }
    }
}
