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
    partial class WheelForm
    {











        string _lastDragInfo = "";      // 上一次拖出去的结果（只给日志看：格式 / 目标有没有接收）








        // 铺一层“看不见的接住区”：分层窗口是按 alpha 做命中测试的 —— alpha=0 的地方
        // 系统会当作不存在，拖到那儿鼠标消息/拖放都直接穿到底下的窗口去（这就是“拖上去没反应”的根因）。
        // alpha=1 肉眼完全看不出来，但系统会认为这里有东西，于是拖放能找上我们。
        // 范围 = 环带（含一点余量）+ 摇杆键，也就是“看上去是轮盘”的那一片。
        void DrawDropCatcher(Graphics g)
        {
            // 收起态不许画接住区：轮盘已经收成一个小把手，但这一片"看不见的 alpha=1 区域"
            // 仍然会让窗口吃住鼠标和拖放 —— 用户原话是"收起来了却好像还在这，挡着我点别的东西"。
            if (_collapsed) return;
            PointF c = Center();
            float R = EffR();
            float outer = R + _thumb * 1.15f;
            float inner = Math.Max(0f, R - _thumb * 1.15f);
            float st = ArcStart();
            using (GraphicsPath gp = new GraphicsPath(FillMode.Alternate))
            {
                gp.AddArc(c.X - outer, c.Y - outer, outer * 2f, outer * 2f, st, 90f);
                gp.AddLine(c.X, c.Y, c.X, c.Y);
                gp.CloseFigure();
                if (inner > 2f)
                {
                    gp.AddArc(c.X - inner, c.Y - inner, inner * 2f, inner * 2f, st, 90f);
                    gp.AddLine(c.X, c.Y, c.X, c.Y);
                    gp.CloseFigure();
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                    g.FillPath(b, gp);
            }
            Rectangle kr = KeyRect();
            using (SolidBrush kb = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                g.FillEllipse(kb, kr);
        }





        void DrawWheel(Graphics g, int w, int h)
        {
            int a = (int)(255 * Math.Max(0f, Math.Min(1f, _show)));
            if (a <= 1) return;
            // 统一缩放：后面所有绘制都按逻辑坐标来，字体/图标/间距自动跟着 DPI 走
            if (Math.Abs(UiK - 1f) > 0.001f) g.ScaleTransform(UiK, UiK);
            PointF c = Center();

            using (Perf.Section("2a-接住区")) DrawDropCatcher(g);

            if (_collapsed)              // 收起态：只留边上那个小把手
            {
                DrawNubs(g, a);
                return;
            }

            // 环与控件都可能是"缓存好的一层"（稳态下整块贴图），见 TryBlitLayer
            bool backLayer = TryBlitLayer(g, 0);
            if (!backLayer)
            {
                using (Perf.Section("2b-环")) DrawRing(g, a);
                StoreLayer(0, g, a, false);
            }

            if (_store.Items.Count == 0)
            {
                using (Font f0 = new Font("Microsoft YaHei UI", 10f))
                using (SolidBrush b0 = new SolidBrush(Color.FromArgb((int)(200 * a / 255f), 255, 255, 255)))
                {
                    string hint = Lang.T("截图后会出现在这里", "No screenshots yet");
                    SizeF hs = g.MeasureString(hint, f0);
                    PointF hp = HintPos(hs);
                    g.DrawString(hint, f0, b0, hp.X, hp.Y);
                }
            }

            for (int pass = 0; pass < 2; pass++)
            {
                using (Perf.Section("2c-缩略图"))
                for (int i = 0; i < _store.Items.Count; i++)
                {
                    bool isEnl = (i == _enlarged);
                    if ((pass == 0) == isEnl) continue;

                    // staggered slide-in: items queue up and glide along the arc with eased motion
                    float pr = EnterProgress(i);
                    if (!_collapsed) pr *= CollapseCardP();          // 收起时图片先淡出、沿弧退回角落
                    if (pr <= 0.001f) continue;
                    // 入场一律从**弧的上端**滑下来（EnterSlide 见 60-WheelForm.cs）：
                    // 堆满时就是老样子 0.30 / 开启动画 0.62；没堆满时一路从 _phiMax 滑到自己的格子
                    float slide = EnterSlide(i, (_intro || _collapsing) ? 0.62f : 0.30f);
                    float phi = ItemPhi(i) + (1f - pr) * slide;      // slide along the arc
                    if (phi < _phiMin - 0.50f || phi > _phiMax + 0.50f) continue;
                    PointF pc = ItemCenterAtPhi(phi);
                    int ia = (int)(a * pr);

                    float sc; if (!_scales.TryGetValue(i, out sc)) sc = 1f;
                    if (_store.Items[i] == _dragOutItem) sc *= Math.Max(0f, 1f - _dragOutProg);
                    if (_store.Items[i] == _deletingItem) { sc *= Math.Max(0f, 1f - _deleteProg); ia = (int)(ia * (1f - _deleteProg)); }
                    if (isEnl) sc *= 1f;                              // peek is a separate overlay
                    SizeF baseSz = CardSize(_store.Items[i]);
                    int iw = Math.Max(4, (int)Math.Round(baseSz.Width * sc));
                    int ih = Math.Max(4, (int)Math.Round(baseSz.Height * sc));
                    if (iw < 4 || ih < 4) continue;
                    RectangleF ir = new RectangleF((float)Math.Round(pc.X - iw / 2f), (float)Math.Round(pc.Y - ih / 2f), iw, ih);
                    RectangleF rr2 = new RectangleF(ir.X - CardPad, ir.Y - CardPad, iw + 2 * CardPad, ih + 2 * CardPad);
                    float rad = CardRadOf(rr2);
                    float shOff = (float)Math.Round(Math.Max(2f, rr2.Height * 0.04f));
                    // 贴片缓存只在"卡片尺寸不动"时用：尺寸每帧都在变的话（放大预览 / 删除 / 拖动 / 收起动画），
                    // 每帧都会生成新贴片，反而比直接画更贵 —— 实测过这一版更慢，所以加了这道门槛。
                    // 判定标准是"尺寸跟上一帧一样吗"，而不是"缩放是不是 1"——
                    // 放大预览停在某个倍数上时尺寸同样稳定，也该用上贴片。
                    long sizeKey = ((long)iw << 20) | (uint)ih;
                    long lastSize;
                    bool sizeStable = _cardSizeMemo.TryGetValue(_store.Items[i], out lastSize) && lastSize == sizeKey;
                    _cardSizeMemo[_store.Items[i]] = sizeKey;
                    bool usePlate = sizeStable && _deletingItem == null && _dragOutItem == null
                                    && !_collapsing && !_intro && !_showAnimating && _show >= 0.999f;

                    // 阴影：新拟态用柔和的漫射阴影，纯扁平就一层淡淡的投影
                    // 缓存成贴片（同一尺寸/样式的卡片每帧画出来一模一样）
                    int shA = ShadowA(95);
                    if (shA > 2)
                    using (Perf.Section("2c1-阴影"))
                    {
                        float pad = 14f;
                        RectangleF area = new RectangleF(rr2.X - pad, rr2.Y - pad, rr2.Width + pad * 2, rr2.Height + pad * 2);
                        string key = "shd|" + (StyleFlatOnly() ? "f" : "n") + "|" + (int)rr2.Width + "|" + (int)rr2.Height +
                                     "|" + (int)rad + "|" + shA + "|" + (int)shOff;
                        Bitmap plate = PlateIf(usePlate, key, area, delegate(Graphics pg)
                        {
                            if (StyleFlatOnly())
                            {
                                using (GraphicsPath sh = Gfx.Round(new RectangleF(rr2.X, rr2.Y + shOff, rr2.Width, rr2.Height), rad))
                                using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(shA / 2.2f), 0, 0, 0)))
                                    pg.FillPath(sb, sh);
                            }
                            else
                            {
                                using (GraphicsPath sh = Gfx.Round(new RectangleF(rr2.X + 1f, rr2.Y + shOff * 1.4f, rr2.Width, rr2.Height), rad))
                                    Gfx.SoftShadow(pg, sh, (int)(shA / 2.4f), 2.6f);
                            }
                        });
                        if (plate != null) DrawWithAlpha(g, plate, area, ia);
                    }

                    using (GraphicsPath card = Gfx.Round(rr2, rad))
                    {
                        // 毛玻璃底（真背景）+ 新拟态的上下明暗边
                        using (Perf.Section("2c2-卡片底"))
                        {
                            BackdropClip(g, card, ia);
                            int baseA = GlassA(188);
                            Color fill = Gfx.A(GlassBase(), baseA);
                            string pkey = "pan|" + _settings.UiStyle + "|" + _settings.GlassPercent + "|" +
                                          (int)rr2.Width + "|" + (int)rr2.Height + "|" + (int)rad + "|" + baseA;
                            Bitmap plate = PlateIf(usePlate, pkey, rr2, delegate(Graphics pg)
                            {
                                using (GraphicsPath p2 = Gfx.Round(rr2, rad))
                                    Gfx.GlassPanel(pg, p2, rr2, fill,
                                        (StyleNeu() ? 34 : 16), (StyleNeu() ? 40 : 0), !StyleFlatOnly());
                            });
                            if (plate != null) DrawWithAlpha(g, plate, rr2, ia);
                            else
                            {
                                Color fill2 = Gfx.A(GlassBase(), (int)(baseA * ia / 255f));
                                Gfx.GlassPanel(g, card, rr2, fill2,
                                    (int)((StyleNeu() ? 34 : 16) * ia / 255f),
                                    (int)((StyleNeu() ? 40 : 0) * ia / 255f),
                                    !StyleFlatOnly());
                            }
                        }
                        if (_store.Items[i].Image != null)
                        using (Perf.Section("2c3-图片"))
                        {
                            g.SetClip(card);
                            bool ex = IsExtreme(_store.Items[i]);
                            if (ex)
                            {
                                // letterbox the picture inside the special box (no distortion)
                                SizeF isz = FitInside(_store.Items[i].Image.Size, iw - 10, ih - 10);
                                int tw = Math.Max(3, (int)Math.Round(isz.Width));
                                int th = (int)Math.Round(isz.Height); if (th < 3) th = 3;
                                Bitmap thb = ScaledThumb(_store.Items[i], tw, th);
                                RectangleF fr = new RectangleF(
                                    (float)Math.Round(pc.X - tw / 2f), (float)Math.Round(pc.Y - th / 2f), tw, th);
                                DrawWithAlpha(g, thb, fr, ia);
                            }
                            else
                            {
                                Bitmap th = ScaledThumb(_store.Items[i], iw, ih);
                                DrawWithAlpha(g, th, ir, ia);
                            }
                            g.ResetClip();
                        }
                        bool hv = (i == _hover || isEnl);
                        bool spec = IsExtreme(_store.Items[i]);
                        using (Perf.Section("2c4-描边"))
                        {
                            Color bc;
                            if (hv) bc = Color.FromArgb(96, 170, 255);
                            else if (spec) bc = Color.FromArgb(245, 166, 35);     // amber = extreme aspect
                            else bc = Color.FromArgb(255, 255, 255);
                            float bw = hv ? 3f : (spec ? 2.2f : 1.4f);
                            int ba = hv ? 255 : (spec ? 240 : 170);
                            float bpad = bw + 3f;
                            RectangleF bArea = new RectangleF(rr2.X - bpad, rr2.Y - bpad, rr2.Width + bpad * 2, rr2.Height + bpad * 2);
                            string bkey = "brd|" + (int)rr2.Width + "|" + (int)rr2.Height + "|" + (int)rad + "|" +
                                          bc.ToArgb() + "|" + (int)(bw * 10) + "|" + ba;
                            Bitmap bplate = PlateIf(usePlate, bkey, bArea, delegate(Graphics pg)
                            {
                                using (GraphicsPath p3 = Gfx.Round(rr2, rad))
                                using (Pen bp = new Pen(Color.FromArgb(ba, bc.R, bc.G, bc.B), bw))
                                    pg.DrawPath(bp, p3);
                            });
                            if (bplate != null) DrawWithAlpha(g, bplate, bArea, ia);
                            else
                                using (Pen bp = new Pen(Color.FromArgb((int)(ba * ia / 255f), bc.R, bc.G, bc.B), bw))
                                    g.DrawPath(bp, card);
                        }
                    }
                }
            }

            // (the big preview is now just a larger card scale - no separate overlay, so no desync)

            bool frontLayer = TryBlitLayer(g, 1);
            if (!frontLayer)
            {
                using (Perf.Section("2d-控件")) DrawControls(g, a);
                StoreLayer(1, g, a, false);
            }
            DrawCountPill(g, a);       // 跟滚动位置绑定的那一个，不进缓存层
        }






        void DrawToast(Graphics g, int a)
        {
            if (_toast.Length == 0) return;
            float age = (float)(DateTime.Now - _toastAt).TotalSeconds;
            if (age > 2.6f) return;
            float t = 1f;
            if (age < 0.18f) t = age / 0.18f;
            else if (age > 2.1f) t = Math.Max(0f, (2.6f - age) / 0.5f);
            int ta = (int)(235 * t * a / 255f);
            if (ta <= 2) return;
            using (Font f = new Font("Microsoft YaHei UI", 10f))
            {
                SizeF sz = g.MeasureString(_toast, f);
                float w = sz.Width + 34f, h = sz.Height + 16f;
                SizeF ls2 = LogicalSize();
                float x = 26f + (1f - t) * 14f, y = ls2.Height - h - 26f;
                using (GraphicsPath pp = Gfx.Round(new RectangleF(x, y, w, h), h / 2f))
                {
                    BackdropClip(g, pp, ta);
                    Gfx.GlassPanel(g, pp, new RectangleF(x, y, w, h), Gfx.A(GlassBase(), GlassA((int)(ta * 0.86f))),
                        (int)(ta * 0.16f), (int)(ta * 0.14f), !StyleFlatOnly());
                    using (Pen p = new Pen(Gfx.A(Gfx.Shade(_accentCur, 0.15f), (int)(ta * 0.42f)), 1.2f))
                        g.DrawPath(p, pp);
                }
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(ta, 255, 255, 255)))
                    g.DrawString(_toast, f, tb, x + 17f, y + 8f);
            }
        }

        // ---- 环（含接住区之外的环带、两端小圆点）----
        // 抽成方法是为了能整块缓存成"静态层"：稳态下每帧只贴一张图，不再一笔一笔重画。
        void DrawRing(Graphics g, int a)
        {
            PointF c = Center();
            // ring track (quarter of the ring that lies inside the screen)
            float rr = EffR();
            float st = ArcStart();
            // 开启动画：环像彩虹一样从一端扫出来
            float sweepP = IntroP(0f);
            float sweep = 90f * (0.02f + 0.98f * Gfx.EaseInOut(sweepP));
            float ringA = (int)(a * Math.Min(1f, 0.35f + 0.65f * sweepP));
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddArc(c.X - rr, c.Y - rr, rr * 2f, rr * 2f, st, sweep);
                if (_dropActive)
                {
                    // drop-target feedback: blue glow + bright blue track（外部文件=偏绿，自己的图=偏蓝）
                    Color dc = _dropExternal ? Color.FromArgb(86, 214, 138) : Color.FromArgb(96, 170, 255);
                    Color dch = Color.FromArgb(255, Math.Min(255, dc.R + 24), Math.Min(255, dc.G + 24), Math.Min(255, dc.B + 24));
                    using (Pen dg = new Pen(Color.FromArgb((int)(110 * ringA / 255f), dc.R, dc.G, dc.B), 40f))
                    { dg.StartCap = LineCap.Round; dg.EndCap = LineCap.Round; g.DrawPath(dg, gp); }
                    using (Pen dm = new Pen(Color.FromArgb((int)(235 * ringA / 255f), dch.R, dch.G, dch.B), 6f))
                    { dm.StartCap = LineCap.Round; dm.EndCap = LineCap.Round; g.DrawPath(dm, gp); }
                }
                // 环：扁平化处理 —— 一条细亮线为主，新拟态风格再垫一层柔和的光晕
                if (StyleNeu() && _settings.ShadowPercent > 8)
                    using (Pen glow = new Pen(Gfx.A(_accentCur, (int)(34 * ringA / 255f)), 22f))
                    { glow.StartCap = LineCap.Round; glow.EndCap = LineCap.Round; g.DrawPath(glow, gp); }
                using (Pen mid = new Pen(Color.FromArgb((int)((StyleFlatOnly() ? 78 : 92) * ringA / 255f), 255, 255, 255), 2.2f))
                { mid.StartCap = LineCap.Round; mid.EndCap = LineCap.Round; g.DrawPath(mid, gp); }
                using (Pen hair = new Pen(Color.FromArgb((int)((StyleFlatOnly() ? 210 : 235) * ringA / 255f), 255, 255, 255), 1.3f))
                { g.DrawPath(hair, gp); }
                if (_switchFlash > 0.01f)     // 切换 Wheel 时的一圈扩散闪光
                {
                    using (Pen fp = new Pen(Color.FromArgb((int)(_switchFlash * 130f), _accentCur.R, _accentCur.G, _accentCur.B), 12f * _switchFlash + 2f))
                    { fp.StartCap = LineCap.Round; fp.EndCap = LineCap.Round; g.DrawPath(fp, gp); }
                }
            }
            // end dots on the two visible ends of the quarter
            for (int e2 = 0; e2 < 2; e2++)
            {
                if (sweep < (e2 == 0 ? 1f : 89f)) continue;         // 扫到哪儿亮到哪儿
                double ang = (st + e2 * 90) * Math.PI / 180.0;
                float ex = (float)(c.X + rr * Math.Cos(ang));
                float ey = (float)(c.Y + rr * Math.Sin(ang));
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(170 * ringA / 255f), 255, 255, 255)))
                    g.FillEllipse(b, ex - 3.5f, ey - 3.5f, 7, 7);
            }
        }



    }
}
