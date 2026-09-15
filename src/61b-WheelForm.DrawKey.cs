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
    // 轮盘绘制：万能键圆盘、四方向摇杆、小按钮发光（从 61-WheelForm.Draw.cs 拆出来，纯搬移，行为不变）。
    partial class WheelForm
    {
        // 万能键：新拟态玻璃圆盘 —— 玻璃底 + 上亮下暗 + 主题色核心，按下时核心点亮并轻微放大
        // 小按钮悬停时的外发光：和万能键同款（PathGradientBrush 中心亮、外围透明）。
        // 抽成方法而不是内联三份：内联会和各自作用域里的局部变量重名（编译期才发现，很烦）。
        void DrawBtnGlow(Graphics g, Rectangle r, float hot)
        {
            if (hot <= 0.01f || !StyleNeu() || _settings.ShadowPercent <= 8) return;
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(r.Left - 11f, r.Top - 11f, r.Width + 22f, r.Height + 22f);
                using (PathGradientBrush halo = new PathGradientBrush(gp))
                {
                    Color acc = _accentCur;   // 光色跟随当前 wheel 的主题色（切 wheel 会变）
                    halo.CenterPoint = new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
                    halo.CenterColor = Gfx.A(acc, (int)(128 * hot));
                    halo.SurroundColors = new Color[] { Gfx.A(acc, 0) };
                    g.FillPath(halo, gp);
                }
            }
        }

        void DrawKeyDisc(Graphics g, int a, Color acc, Rectangle kr, float kcx, float kcy, float krr)
        {
            float kt = Gfx.Clamp01(_keyT);
            float hv = Gfx.Clamp01(_keyHov);
            // 悬停时轻微放大 + 高光变亮；按下时再强一点
            float sc = 1f + 0.045f * hv + 0.05f * kt;
            float rr = krr * sc;
            RectangleF disc = new RectangleF(kcx - rr, kcy - rr, rr * 2f, rr * 2f);
            bool neu = StyleNeu();
            if (hv > 0.01f) a = Math.Min(255, (int)(a * (1f + 0.12f * hv)));

            // 1) 外发光：悬停时明显变亮变大
            if (neu && _settings.ShadowPercent > 8)
            {
                using (GraphicsPath gp = new GraphicsPath())
                {
                    float ho = rr + 12f + 10f * kt + 12f * hv;
                    gp.AddEllipse(kcx - ho, kcy - ho, ho * 2f, ho * 2f);
                    using (PathGradientBrush halo = new PathGradientBrush(gp))
                    {
                        halo.CenterPoint = new PointF(kcx, kcy);
                        halo.CenterColor = Gfx.A(acc, (int)((48 + 90 * kt + 70 * hv) * a / 255f));
                        halo.SurroundColors = new Color[] { Gfx.A(acc, 0) };
                        g.FillPath(halo, gp);
                    }
                }
            }

            // 2) 玻璃盘身
            using (GraphicsPath body = new GraphicsPath())
            {
                body.AddEllipse(disc);
                BackdropClip(g, body, a);
                Gfx.GlassPanel(g, body, disc,
                    Gfx.A(GlassBase(), GlassA((int)((178 + 28 * kt) * a / 255f))),
                    (int)((neu ? 46 : 22) * a / 255f),
                    (int)((neu ? 52 : 0) * a / 255f),
                    !StyleFlatOnly());
                // 3) 主题色内芯（按下的进度决定点亮的程度）
                float cr = rr * (0.52f + 0.06f * kt);
                using (GraphicsPath core = new GraphicsPath())
                {
                    core.AddEllipse(kcx - cr, kcy - cr, cr * 2f, cr * 2f);
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                        new RectangleF(kcx - cr, kcy - cr - 1f, cr * 2f, cr * 2f + 2f),
                        Gfx.A(Gfx.Shade(acc, 0.22f + 0.10f * hv), (int)(((StyleFlatOnly() ? 200 : 118) + 60 * kt + 55 * hv) * a / 255f)),
                        Gfx.A(Gfx.Shade(acc, -0.28f), (int)(((StyleFlatOnly() ? 170 : 88) + 62 * kt + 50 * hv) * a / 255f)),
                        LinearGradientMode.Vertical))
                        g.FillPath(lg, core);
                    using (Pen cp = new Pen(Gfx.A(Gfx.Shade(acc, 0.35f), (int)((110 + 90 * kt + 60 * hv) * a / 255f)), 1.2f))
                        g.DrawPath(cp, core);
                }
            }

            // 4) 摇杆点：中心点 + 圆盘展开时四个方向标出各自的动作名（当前指向的高亮）
            float dsz = 6.2f + 1.4f * kt;
            using (SolidBrush db = new SolidBrush(Color.FromArgb((int)((238 + 17 * hv) * a / 255f), 255, 255, 255)))
            {
                float cs = 7.4f + 2.2f * kt;
                g.FillEllipse(db, kcx - cs / 2f, kcy - cs / 2f, cs, cs);

                if (_menuT > 0.05f)
                {
                    float[] dx4 = { 0f, 1f, 0f, -1f };
                    float[] dy4 = { -1f, 0f, 1f, 0f };
                    float lr = rr * 0.60f;
                    using (Font kf = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold))
                    {
                        for (int q = 0; q < 4; q++)
                        {
                            string txt = KeyActionShort(_settings.KeyActionAt(q));
                            if (txt.Length == 0) continue;              // "不设置"就不画
                            float px = kcx + dx4[q] * lr, py = kcy + dy4[q] * lr;
                            bool act = (_sector == q);
                            float ka = _menuT * (_sector < 0 ? 0.78f : (act ? 1f : 0.42f));
                            SizeF ts = g.MeasureString(txt, kf);
                            if (act)                                     // 指向的那个：白底 + 深字
                            {
                                float hr = Math.Max(ts.Width, ts.Height) * 0.5f + 5f;
                                using (SolidBrush hb = new SolidBrush(Color.FromArgb((int)(215 * ka * a / 255f), 255, 255, 255)))
                                    g.FillEllipse(hb, px - hr, py - hr, hr * 2f, hr * 2f);
                            }
                            Color tc = act ? Color.FromArgb(26, 28, 34) : Color.FromArgb(255, 255, 255);
                            using (SolidBrush tb = new SolidBrush(Color.FromArgb((int)(250 * ka * a / 255f), tc.R, tc.G, tc.B)))
                                g.DrawString(txt, kf, tb, px - ts.Width / 2f, py - ts.Height / 2f);
                        }
                    }
                }
            }
        }

        // ---- 贴边小把手：收起态画"拉出"、展开态画Lang.T("收起", "Collapse") ----
        // 两个把手按环的进度交叉淡入淡出（并各自从屏幕边滑出来），不会"啪"地换一个
        void DrawNubs(Graphics g, int a)
        {
            float k = _collapsed ? 0f : (_intro ? _introT : 1f);    // 0=完全收起，1=完全展开
            float ap = _nubAppearT >= 1f ? 1f : 1f - (1f - _nubAppearT) * (1f - _nubAppearT);   // 出现用 easeOut
            if (NubSingleMode())
            {
                DrawNubOne(g, a, true, ap);                          // 只有一个把手，始终可见
                return;
            }
            if (k < 0.995f) DrawNubOne(g, a, true, (1f - k) * ap);
            if (k > 0.005f) DrawNubOne(g, a, false, k);
        }

        void DrawNubOne(Graphics g, int a, bool outMode, float vis)
        {
            if (vis <= 0.004f) return;
            RectangleF r = outMode ? NubOutRect() : NubInRect();
            bool hov = vis > 0.98f && (outMode ? _nubOutHover : _nubInHover);
            float k = vis > 0.98f ? _nubHov : 0f;
            Color acc = _accentCur;
            int alpha = (int)(a * vis * vis * (0.62f + 0.38f * k));   // vis 平方：淡出更干脆
            // 出场/退场时贴着屏幕边滑一下（像从边里抽出来）
            float slide = (1f - vis) * 16f;

            // 悬停时稍微长一点、厚一点，像"被拉出来一点"
            float grow = 10f * k;
            bool vertical = r.Height > r.Width;
            RectangleF rr = vertical
                ? new RectangleF(r.X, r.Y - grow / 2f, r.Width, r.Height + grow)
                : new RectangleF(r.X - grow / 2f, r.Y, r.Width + grow, r.Height);
            if (vertical) rr = new RectangleF(rr.X - (Sx() > 0 ? slide : -slide), rr.Y, rr.Width, rr.Height);
            else rr = new RectangleF(rr.X, rr.Y + (Sy() > 0 ? slide : -slide), rr.Width, rr.Height);

            using (GraphicsPath p = Gfx.Round(rr, Math.Min(rr.Width, rr.Height) / 2f))
            {
                BackdropClip(g, p, alpha);
                using (SolidBrush b = new SolidBrush(Gfx.A(GlassBase(), (int)((hov ? 214 : 178) * alpha / 255f))))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Gfx.A(acc, (int)((hov ? 210 : 130) * alpha / 255f)), 1.4f))
                    g.DrawPath(pen, p);
            }

            // 三个小点（抓手感）+ 一个指向"将要动的方向"的小三角
            float cx = rr.X + rr.Width / 2f, cy = rr.Y + rr.Height / 2f;
            using (SolidBrush db = new SolidBrush(Gfx.A(acc, (int)((hov ? 255 : 200) * alpha / 255f))))
            {
                float ds = 3.4f + 1.0f * k;
                float gap = 9f;
                for (int i = -1; i <= 1; i++)
                {
                    if (vertical) g.FillEllipse(db, cx - ds / 2f, cy + i * gap - ds / 2f, ds, ds);
                    else g.FillEllipse(db, cx + i * gap - ds / 2f, cy - ds / 2f, ds, ds);
                }
            }
            // 方向提示三角：拉出 = 指向屏幕里；收起 = 指向贴着的那条屏幕边
            //  竖着的把手（在竖直的屏幕边上）：箭头朝 左右
            //  横着的把手（在水平的屏幕边上）：箭头朝 上下
            // 箭头指向"点一下会发生什么"：拉出->朝屏幕里；收起->朝屏幕边外
            bool willExpand = outMode;
            if (NubSingleMode()) willExpand = _collapsed;
            float ax2 = 0f, ay2 = 0f;
            if (vertical) ax2 = willExpand ? Sx() : -Sx();
            else ay2 = willExpand ? Sy() : -Sy();
            // 三角放在"远离角落"的那一端旁边，避开中间的三个点
            float along = NubLong * 0.30f;
            float px0 = vertical ? cx : cx + along * (Sx() > 0 ? 1f : -1f);
            float py0 = vertical ? cy + along * (Sy() > 0 ? 1f : -1f) : cy;
            using (GraphicsPath ar = new GraphicsPath())
            {
                float s2 = 4.8f + 1.4f * k;
                if (vertical)
                {
                    ar.AddPolygon(new PointF[] {
                        new PointF(px0 - ax2 * s2 * 0.55f, py0 - s2 * 0.85f),
                        new PointF(px0 - ax2 * s2 * 0.55f, py0 + s2 * 0.85f),
                        new PointF(px0 + ax2 * s2 * 0.75f, py0)
                    });
                }
                else
                {
                    ar.AddPolygon(new PointF[] {
                        new PointF(px0 - s2 * 0.85f, py0 - ay2 * s2 * 0.55f),
                        new PointF(px0 + s2 * 0.85f, py0 - ay2 * s2 * 0.55f),
                        new PointF(px0, py0 + ay2 * s2 * 0.75f)
                    });
                }
                using (SolidBrush ab3 = new SolidBrush(Gfx.A(acc, (int)((hov ? 255 : 215) * alpha / 255f))))
                    g.FillPath(ab3, ar);
            }

            // ---- 用途提示：首次运行自动亮一次，之后悬停才显示 ----
            // 以前把手只有三个点和一个小三角，新用户根本不知道它是干嘛的。
            float hintA = vis > 0.98f ? _nubHintT : 0f;
            if (hintA > 0.02f)
            {
                string ht = willExpand ? Lang.T("点我展开", "Click to expand") : Lang.T("点我收起", "Click to collapse");
                using (Font hf = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                {
                    SizeF ts = g.MeasureString(ht, hf);
                    float pw = ts.Width + 16f, ph = ts.Height + 8f;
                    float hx, hy;
                    if (vertical)   // 竖直边上的把手：提示放到屏幕里侧（右边）
                    {
                        hx = rr.Right + 8f;
                        hy = rr.Y + rr.Height / 2f - ph / 2f;
                    }
                    else            // 水平边上的把手：提示放到屏幕里侧（上边）
                    {
                        hx = rr.X + rr.Width / 2f - pw / 2f;
                        hy = rr.Y - ph - 8f;
                    }
                    RectangleF pr3 = new RectangleF(hx, hy, pw, ph);
                    int ha = (int)(hintA * 245);
                    using (GraphicsPath hp2 = Gfx.Round(pr3, ph / 2f))
                    {
                        BackdropClip(g, hp2, ha);
                        using (SolidBrush hb = new SolidBrush(Color.FromArgb((int)(ha * 0.62f), 22, 24, 30)))
                            g.FillPath(hb, hp2);
                        using (Pen hpn = new Pen(Gfx.A(acc, (int)(ha * 0.55f)), 1.3f))
                            g.DrawPath(hpn, hp2);
                    }
                    using (SolidBrush htx = new SolidBrush(Color.FromArgb(ha, 255, 255, 255)))
                        g.DrawString(ht, hf, htx, hx + 8f, hy + 4f);
                }
            }
        }

    }
}
