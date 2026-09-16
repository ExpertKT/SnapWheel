using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnapWheel
{
    // 标注：标注的绘制（各图元、工具条、引导面板、取字中提示）（从 51-OverlayForm.Annotate.cs 拆出，纯搬移，行为不变）。
    partial class OverlayForm
    {
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
                    case AnnotKind.Emoji:
                    {
                        // ⚠️ 必须用 Graphics.DrawString（GDI+），**不能**用 TextRenderer：
                        //   合成最终图时这个 Graphics 上有 Translate/Rotate 变换（按选区裁剪+旋转），
                        //   而 TextRenderer 走 GDI，完全不响应 GDI+ 的变换 —— 于是预览看着正常、
                        //   一合成符号就跑到图外（用户反馈"贴上去了出图没有"）。文字标注一直用 DrawString，
                        //   所以从来没这个问题。
                        float sf2 = Math.Max(10f, s.Size * _k);
                        using (Font f = new Font("Segoe UI Symbol", sf2))
                        using (SolidBrush b = new SolidBrush(s.Color))
                        {
                            StringFormat fmt = new StringFormat();
                            fmt.Alignment = StringAlignment.Center;
                            fmt.LineAlignment = StringAlignment.Center;
                            g.DrawString(s.Text, f, b,
                                new RectangleF(s.A.X - sf2, s.A.Y - sf2 * 0.85f, sf2 * 2f, sf2 * 1.7f), fmt);
                        }
                        break;
                    }
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

            int a = Math.Max(0, Math.Min(255, alpha));
            for (int i = 0; i < _toolBtns.Length; i++)
            {
                Rectangle r = _toolBtns[i];
                bool isTool = i < 6;
                bool sel = isTool && ((AnnotKind)i == _tool);
                if (sel || i == _toolHover)
                {
                    using (GraphicsPath bp = Gfx.Round(r, 7f * _k))
                    using (SolidBrush b = new SolidBrush(sel ? Color.FromArgb((int)(235 * a / 255f), 0, 122, 204)
                                                              : Color.FromArgb((int)(90 * a / 255f), 255, 255, 255)))
                        g.FillPath(b, bp);
                }

                Color ic = Color.FromArgb(a, 255, 255, 255);   // 图标跟着一起淡，不然底淡了图标还刺眼
                if (i >= 6 && i < 6 + AnnotColors.Length)
                {
                    // 颜色点：当前色描粗白边；其余也描一圈细边 —— 黑点在深色工具条上不然看不见
                    int ci = i - 6;
                    bool cur = (_annotColor.ToArgb() == AnnotColors[ci].ToArgb());
                    int d = (int)(15 * _k);
                    Rectangle cr = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
                    using (SolidBrush b = new SolidBrush(AnnotColors[ci])) g.FillEllipse(b, cr);
                    using (Pen ring = new Pen(Color.FromArgb((int)((cur ? 255 : 140) * a / 255f), 255, 255, 255), cur ? 2.2f : 1.2f))
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
                                using (SolidBrush b = new SolidBrush(ic)) g.FillRectangle(b, sq);
                            using (Pen p = new Pen(ic, 1.4f * _k)) g.DrawRectangle(p, sq.X, sq.Y, sq.Width, sq.Height);
                            using (Font f = new Font("Microsoft YaHei UI", 8.5f * _k, FontStyle.Bold))
                            using (SolidBrush b = new SolidBrush(_textBg ? Color.FromArgb(a, 22, 24, 28) : ic))
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
                    case IdxLong:       // 长图：一页纸 + 上下箭头
                    float lx = d2.Left + d2.Width / 2f, ly = d2.Top + d2.Height / 2f;
                    using (Pen pl = new Pen(Color.FromArgb(226, 232, 240), 1.6f))
                    {
                        g.DrawRectangle(pl, lx - 5f * _k, ly - 8f * _k, 10f * _k, 16f * _k);
                        g.DrawLine(pl, lx, ly - 9f * _k, lx, ly - 14f * _k);
                        g.DrawLine(pl, lx - 2.4f * _k, ly - 11.5f * _k, lx, ly - 14f * _k);
                        g.DrawLine(pl, lx + 2.4f * _k, ly - 11.5f * _k, lx, ly - 14f * _k);
                        g.DrawLine(pl, lx, ly + 9f * _k, lx, ly + 14f * _k);
                        g.DrawLine(pl, lx - 2.4f * _k, ly + 11.5f * _k, lx, ly + 14f * _k);
                        g.DrawLine(pl, lx + 2.4f * _k, ly + 11.5f * _k, lx, ly + 14f * _k);
                    }
                    break;
                    case IdxSave:       // 另存为：向下箭头 + 底线（存盘）
                    {
                        float sx = d2.Left + d2.Width / 2f, sy = d2.Top + d2.Height / 2f;
                        using (Pen ps = new Pen(Color.FromArgb(226, 232, 240), 1.6f))
                        {
                            g.DrawLine(ps, sx, sy - 8f * _k, sx, sy + 3f * _k);
                            g.DrawLine(ps, sx - 4f * _k, sy - 1f * _k, sx, sy + 3f * _k);
                            g.DrawLine(ps, sx + 4f * _k, sy - 1f * _k, sx, sy + 3f * _k);
                            g.DrawLine(ps, sx - 7f * _k, sy + 8f * _k, sx + 7f * _k, sy + 8f * _k);
                        }
                    }
                    break;
                    case IdxEmoji:      // 贴 emoji：画一张笑脸（比任何图标都好认）
                    {
                        float ex = d2.Left + d2.Width / 2f, ey = d2.Top + d2.Height / 2f, er = 8f * _k;
                        using (Pen pe = new Pen(Color.FromArgb(238, 200, 90), 1.5f))
                        {
                            g.DrawEllipse(pe, ex - er, ey - er, er * 2f, er * 2f);
                            using (SolidBrush be = new SolidBrush(Color.FromArgb(238, 200, 90)))
                            {
                                g.FillEllipse(be, ex - 3.4f * _k, ey - 3.2f * _k, 2f * _k, 2f * _k);
                                g.FillEllipse(be, ex + 1.4f * _k, ey - 3.2f * _k, 2f * _k, 2f * _k);
                            }
                            g.DrawArc(pe, ex - 4.4f * _k, ey - 1.6f * _k, 8.8f * _k, 6f * _k, 20, 140);
                        }
                    }
                    break;
                    case 5:             // 取字工具：一个Lang.T("字", "Aa")比任何图标都好认
                        using (Font f = new Font("Microsoft YaHei UI", 13f * _k, FontStyle.Bold))
                        using (SolidBrush b = new SolidBrush(Ocr.Available ? ic : Color.FromArgb((int)(120 * a / 255f), 255, 255, 255)))
                        {
                            StringFormat sf = new StringFormat();
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            g.DrawString(Lang.T("字", "Aa"), f, b, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                        }
                        break;
                    default:     // 撤销
                        // 原来的画法有问题（用户反馈"图标偏移、异常"）：
                        //   弧的矩形下移了 15%、高度只给 90%，而箭头的两根线是**硬编码在左上角**的 ——
                        //   两者对不上，于是箭头飘在弧外面、弧本身又整体偏下。
                        // 现在按几何来：先算弧的端点，箭头就画在端点上，保证永远接得住。
                        using (Pen p = new Pen(_shapes.Count > 0 ? ic : Color.FromArgb((int)(110 * a / 255f), 255, 255, 255), 2f * _k))
                        {
                            // 弧铺满图标区，缺口留在左上（撤销箭头的经典形状）
                            float cx = d2.Left + d2.Width / 2f, cy = d2.Top + d2.Height / 2f;
                            float rx = d2.Width / 2f, ry = d2.Height / 2f;
                            const float startDeg = -55f, sweepDeg = 265f;
                            g.DrawArc(p, d2.Left, d2.Top, d2.Width, d2.Height, startDeg, sweepDeg);

                            // 箭头画在弧的起点上（用同样的角度算出来，两端自然对齐）
                            double rad = startDeg * Math.PI / 180.0;
                            float ax = cx + (float)(rx * Math.Cos(rad));
                            float ay = cy + (float)(ry * Math.Sin(rad));
                            g.DrawLine(p, ax, ay, ax - 2.0f * _k, ay - 5.5f * _k);
                            g.DrawLine(p, ax, ay, ax + 4.5f * _k, ay - 2.0f * _k);
                        }
                        break;
                }
            }
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
            string hint = (_sel.Kind == AnnotKind.Text) ? Lang.T("拖动移动　·　滚轮 / A+/A- 改字号　·　Del 删除", "Drag · wheel or A+/A- resize · Del delete")
                                                        : Lang.T("拖动移动　·　滚轮改粗细　·　Del 删除", "Drag · wheel thickness · Del delete");
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
                g.DrawString(Lang.T("截图浮层：三步搞定", "The capture overlay in three steps"), ft, bt, x + pad, y + pad - 4 * _k);

            string[] lines = {
                Lang.T("①  按住左键拖出要截的区域（四角缩放、圆点旋转、中间拖动）", "1. Drag with the left button to pick an area (corners resize, the dot rotates, the middle moves it)"),
                Lang.T("②  用下面的工具条标注：箭头 A · 方框 R · 马赛克 M · 文字 T", "2. Annotate with the toolbar below: arrow A · box R · mosaic M · text T"),
                Lang.T("      颜色 1~4 · 文字底 B · 字号 A+/A- 或滚轮 · Ctrl+Z 撤销", "      colours 1-4 · text background B · size A+/A- or wheel · Ctrl+Z to undo"),
                Lang.T("      画完的文字/方框可以直接拖动、滚轮改大小，Del 删掉", "      drawn text and boxes can be dragged, resized with the wheel, deleted with Del"),
                Lang.T("③  选「字」工具（或按 O）拖一个框圈住文字 = 取字，框越小越准；", "3. Pick the OCR tool (or press O) and drag a box around text; the tighter the box, the better"),
                Lang.T("      取字窗口里还能一键翻译成中文/英文", "      the OCR window can also translate to Chinese or English in one click"),
                Lang.T("④  双击选区或按回车 = 确认（Esc 取消），图直接进轮盘", "4. Double-click the selection or press Enter to confirm (Esc cancels); the image goes into the ring")
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
                g.DrawString(Lang.T("开始用", "Start using it"), fb, bb, btn, sf);
            }
            using (Font fh = new Font("Microsoft YaHei UI", 8.5f * _k))
            using (SolidBrush bh = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                g.DrawString(Lang.T("点一下面板或按任意键就开始（只提示这一次）", "Click the panel or press any key to begin (shown once)"), fh, bh, x + pad, y + h - pad - (int)(18 * _k));
        }

        // Lang.T("取字中…", "Recognising…")的小提示：识别在后台跑，但得让用户看见"它在干活"（不显示的话还是像卡住）
        void PaintOcrBusy(Graphics g)
        {
            if (!_ocrBusy) return;
            string txt = Lang.T("取字中…", "Recognising…");
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

    }
}
