using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
namespace SnapWheel
{
    class RoundButton : Button
    {
        public Color Fill = Color.FromArgb(0, 122, 204);
        public Color FillHover = Color.FromArgb(0, 138, 228);
        public Color TextColor = Color.White;
        public bool Primary = false;
        public bool Ghost = false;

        public RoundButton()
        {
            // 圆角外的部分交给父容器去画：SupportsTransparentBackColor + OnPaintBackground。
            // 角落永远是"父容器真实的背景"，不用自己猜颜色 ——
            // 之前自己填色，半透明窗体上取不到色就退回白/黑，四角才会出现"鼠标移上去才好"的脏块。
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            // 先让父容器把背景铺到我们这块区域（含窗体底色）
            try { base.OnPaintBackground(pevent); } catch { }

            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0, 0, Width, Height);

            bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
            bool down = MouseButtons == MouseButtons.Left && hot;
            Color c = hot ? FillHover : Fill;
            if (Ghost) c = Color.FromArgb(hot ? 240 : 200, c.R, c.G, c.B);

            // 圆角按高度算（28% 高度）：写死 10f 的话，高 DPI 下按钮被放大 1.5 倍、圆角却不变，看着就不搭了
            using (GraphicsPath p = Gfx.Round(r, Math.Max(2f, r.Height * 0.28f)))
            {
                if (down)
                {
                    // 按下：内凹（暗边在上，亮边在下）
                    using (SolidBrush b = new SolidBrush(Gfx.Shade(c, -0.10f))) g.FillPath(b, p);
                    using (Pen sh = new Pen(Color.FromArgb(70, 0, 0, 0), 1.6f)) g.DrawPath(sh, p);
                }
                else
                {
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                        new RectangleF(r.X, r.Y - 1f, r.Width, r.Height + 2f),
                        Primary ? Gfx.Shade(c, 0.16f) : Gfx.Shade(c, 0.55f),
                        Primary ? Gfx.Shade(c, -0.12f) : Gfx.Shade(c, -0.04f),
                        LinearGradientMode.Vertical))
                        g.FillPath(lg, p);
                    using (Pen hi = new Pen(Color.FromArgb(Primary ? 60 : 200, 255, 255, 255), 1.1f)) g.DrawPath(hi, p);
                    using (Pen sh = new Pen(Color.FromArgb(28, 0, 0, 0), 1f)) g.DrawPath(sh, p);
                }
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height - 1), TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
