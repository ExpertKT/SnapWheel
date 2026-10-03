using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnapWheel
{
    // 标注：标注的数据模型（图元类型、命中框、外框计算）（从 51-OverlayForm.Annotate.cs 拆出，纯搬移，行为不变）。
    partial class OverlayForm
    {
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
            // 度量用的字体必须和绘制用的**同一个**，否则框和内容对不上（符号画的时候用 Segoe UI Symbol）
            string ff2 = (s.Kind == AnnotKind.Emoji) ? "Segoe UI Symbol" : "Microsoft YaHei UI";
            using (Font f = new Font(ff2, Math.Max(6f, s.Size * _k), (s.Kind == AnnotKind.Emoji) ? FontStyle.Regular : FontStyle.Bold))
            {
                Size sz = TextRenderer.MeasureText(t, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                return new SizeF(sz.Width, sz.Height);
            }
        }

        // 图元的外框（文字按实际排版量；其它按起止点）
        RectangleF ShapeBounds(Shape s)
        {
            if (s == null) return RectangleF.Empty;
            if (!IsTextLike(s)) return RectOf(s.A, s.B);
            if (s.Kind == AnnotKind.Emoji)   // 符号：A 是**中心**（与绘制一致）；文字用的是左上角锚点
            {
                SizeF esz = TextSize(s);
                return new RectangleF(s.A.X - esz.Width / 2f, s.A.Y - esz.Height / 2f, esz.Width, esz.Height);
            }
            SizeF sz = TextSize(s);
            return new RectangleF(s.A.X - 3 * _k, s.A.Y - 2 * _k, sz.Width + 7 * _k, sz.Height + 5 * _k);
        }

    }
}
