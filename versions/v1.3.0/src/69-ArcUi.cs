using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnapWheel
{
    // ==================== 弧线设计语言（0.6.0） ====================
    // 用户的要求：轮盘上的东西别再"横平竖直"地摆 ——
    //   · 胶囊（wheel 名、"3 / 8" 计数）要**随弧弯成圆弧**、靠着 wheel 摆，文字也要沿弧排；
    //   · 截图 / 设置 / 收起 三个小按钮要**围绕万能键的左上方、沿一段弧**排布。
    //
    // 坐标换算和 WheelForm.ItemCenterAtPhiRadius 是同一套：以屏幕角为圆心，phi 是极角，
    // Sx()/Sy() 决定"角在哪一边"（右下角时两者都是 -1），所以工具函数都收 sx/sy，
    // 画出来的东西自动跟着四个角走。
    static class ArcUi
    {
        // 极坐标 → 屏幕坐标
        public static PointF Polar(PointF c, float sx, float sy, float phi, float r)
        {
            return new PointF((float)(c.X + r * Math.Cos(phi) * sx),
                              (float)(c.Y + r * Math.Sin(phi) * sy));
        }

        // 该点的**向外单位法线**（屏幕坐标）
        public static PointF Outward(PointF c, float sx, float sy, float phi)
        {
            PointF a = Polar(c, sx, sy, phi, 1f);
            float dx = a.X - c.X, dy = a.Y - c.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6f) return new PointF(0, -1);
            return new PointF(dx / len, dy / len);
        }

        // 该点处"phi 增大"方向的单位切线
        static void Tangent(PointF c, float sx, float sy, float phi, float r, out float tx, out float ty)
        {
            PointF p1 = Polar(c, sx, sy, phi + 0.004f, r), p0 = Polar(c, sx, sy, phi - 0.004f, r);
            tx = p1.X - p0.X; ty = p1.Y - p0.Y;
            float l = (float)Math.Sqrt(tx * tx + ty * ty);
            if (l > 1e-6f) { tx /= l; ty /= l; }
            else { tx = 0; ty = 0; }
        }

        // 弧形胶囊的轮廓：沿半径 r、从 a0 扫到 a1（弧度）、厚 h，两端半圆头。
        // 多边形近似（外/内弧每 ~2° 一点、端帽每 ~10° 一点）——比拿 GraphicsPath 拼两段弧加两个半圆稳得多。
        public static GraphicsPath Capsule(PointF c, float sx, float sy, float r, float h, float a0, float a1)
        {
            List<PointF> pts = new List<PointF>();
            float rOut = r + h / 2f, rIn = r - h / 2f;
            int seg = Math.Max(6, (int)(Math.Abs(a1 - a0) * 180.0 / Math.PI / 1.0));   // 每 1 度一个点：更圆润

            for (int i = 0; i <= seg; i++)                       // 外弧 a0 -> a1
                pts.Add(Polar(c, sx, sy, a0 + (a1 - a0) * i / seg, rOut));
            AddCap(pts, c, sx, sy, a1, r, h, true);              // a1 端帽：外点 -> 切线 -> 内点
            for (int i = seg; i >= 0; i--)                       // 内弧 a1 -> a0
                pts.Add(Polar(c, sx, sy, a0 + (a1 - a0) * i / seg, rIn));
            AddCap(pts, c, sx, sy, a0, r, h, false);             // a0 端帽：内点 -> 切线 -> 外点

            GraphicsPath p = new GraphicsPath();
            if (pts.Count >= 3) p.AddPolygon(pts.ToArray());
            return p;
        }

        // 端帽半圆：以"弧中线上那个点"为圆心、h/2 为半径，从外点经切线点转到内点（atEnd=true 时）；
        // 另一头则反过来。两端点本身已经由弧给出，这里只补中间的过渡点。
        static void AddCap(List<PointF> pts, PointF c, float sx, float sy, float a, float r, float h, bool atEnd)
        {
            PointF mid = Polar(c, sx, sy, a, r);
            PointF outP = Polar(c, sx, sy, a, r + h / 2f);
            float ux = (outP.X - mid.X) / (h / 2f), uy = (outP.Y - mid.Y) / (h / 2f);   // 单位半径方向
            float tx, ty;
            Tangent(c, sx, sy, a, r, out tx, out ty);
            if (tx == 0 && ty == 0) { tx = -uy; ty = ux; }

            const int steps = 14;
            for (int i = 1; i < steps; i++)
            {
                // 参数 t：0 = 外点、π/2 = 切线外推点、π = 内点
                double t = atEnd ? (Math.PI * i / steps) : (Math.PI * (steps - i) / steps);
                float ct = (float)Math.Cos(t), st = (float)Math.Sin(t);
                // 端帽必须凸向弧继续往外的那一侧：a1 端（外弧终点）继续往前 = +切线；
                // a0 端（内弧终点）要往回走 = -切线。这里原来两端都用了 +切线 —— a0 端于是
                // 凸向了弧带内部、和弧带自交，填充出来就是一个缺口（用户反馈的胶囊显示异常）。
                float dir = atEnd ? 1f : -1f;
                pts.Add(new PointF(mid.X + (h / 2f) * (ux * ct + dir * tx * st),
                                   mid.Y + (h / 2f) * (uy * ct + dir * ty * st)));
            }
        }

        // 沿弧排一行字：每个字各自旋转，让字"立"在弧上（字顶朝外）。
        // charStep：每个字占的角度（弧度），文字以 midPhi 为中心左右摊开。
        public static void ArcText(Graphics g, string text, Font f, Brush br, PointF c, float sx, float sy,
                                   float r, float midPhi, float charStep)
        {
            ArcText(g, text, f, br, c, sx, sy, r, midPhi, charStep, 1f);
        }

        // tilt = 每个字按比例跟随弧线倾斜：1 = 完全贴着弧（字会躺倒），0.45 左右 = 有弧度感但仍好读。
        // 用户反馈：计数胶囊里的数字在 45 度方向上被转得太狠、看着就是显示异常。
        public static void ArcText(Graphics g, string text, Font f, Brush br, PointF c, float sx, float sy,
                                   float r, float midPhi, float charStep, float tilt)
        {
            if (string.IsNullOrEmpty(text)) return;
            int n = text.Length;
            float start = midPhi + charStep * (n - 1) / 2f;   // 起点在 phi 大侧，配合递减步进读起来才是正序
            for (int i = 0; i < n; i++)
            {
                string ch = text.Substring(i, 1);
                SizeF cs = g.MeasureString(ch, f);
                float a = start - charStep * i;   // 倒着排：第一个字落在 phi 大的一侧（屏幕左上），读起来才是正序
                PointF p = Polar(c, sx, sy, a, r);
                PointF d = Outward(c, sx, sy, a);
                // 让"字的上方向"对齐向外法线：GDI+ 里字的上方向是 -Y，旋转 θ 后指向 (sinθ, -cosθ)，
                // 令它等于 d 即得 θ = atan2(dx, -dy)
                float deg = (float)(Math.Atan2(d.X, -d.Y) * 180.0 / Math.PI) * tilt;
                GraphicsState st = g.Save();
                try
                {
                    g.TranslateTransform(p.X, p.Y);
                    g.RotateTransform(deg);
                    g.DrawString(ch, f, br, -cs.Width / 2f, -cs.Height / 2f);
                }
                finally { g.Restore(st); }
            }
        }

        // 一行字沿弧排开时每个字该占多少角度（按实测字宽 + 间距算，避免挤在一起）
        public static float StepFor(Graphics g, string text, Font f, float r, float gap)
        {
            if (string.IsNullOrEmpty(text) || r <= 1f) return 0.1f;
            float w = g.MeasureString(text, f).Width / text.Length + gap;
            return w / r;
        }
    }
}
