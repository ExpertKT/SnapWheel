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
    // 轮盘绘制：三个小按钮与计数胶囊（从 61-WheelForm.Draw.cs 拆出来，纯搬移，行为不变）。
    partial class WheelForm
    {
        // ---- 控件层：关闭键 / 设置键 / 万能键 / 名字药丸 / 提示条 / 把手 ----
        void DrawControls(Graphics g, int a)
        {
            // 按下反馈：缩小一点 + 描边更亮，让"按下去"看得见
            PointF c = Center();
            Rectangle cbr = Shrink(CloseButtonRect(), _closeDown);
            Diag("关闭键（短按收起 / 长按 0.65s 退出）", cbr);
            DrawBtnGlow(g, cbr, _closeGlow);
            Color acc = _accentCur;
            float pb0 = IntroP(0.30f), pb1 = IntroP(0.40f), pb2 = IntroP(0.50f);
            PointF sh0 = IntroShift(pb0), sh1 = IntroShift(pb1), sh2 = IntroShift(pb2);
            int ab0 = (int)(a * pb0), ab1 = (int)(a * pb1), ab2 = (int)(a * pb2);

            if (pb0 > 0.01f)
            {
                System.Drawing.Drawing2D.Matrix m0 = g.Transform;
                g.TranslateTransform(sh0.X, sh0.Y);
                Rectangle cbr0 = cbr;
                using (GraphicsPath cbp2 = new GraphicsPath()) { cbp2.AddEllipse(cbr0); BackdropClip(g, cbp2, ab0); cbp2.Dispose(); }
                // 长按时：底色由玻璃色渐变到红色（用 _closeHoldP 过渡，不是突然变），
                // 外边再画一圈红色进度环 —— 按下去就知道还差多久松手
                float hp = _closeHoldP;
                Color glassSurf = Gfx.A(GlassBase(), GlassA((int)((_closeHover ? UiFeel.SurfaceHover : (_closeDown > 0.5f ? UiFeel.SurfacePress : UiFeel.SurfaceIdle)) * ab0 / 255f)));
                Color redSurf = Gfx.A(Color.FromArgb(236, 74, 62), (int)(238 * ab0 / 255f));
                Color closeSurf = hp > 0.001f
                    ? Color.FromArgb(
                        (int)(glassSurf.A + (redSurf.A - glassSurf.A) * hp),
                        (int)(glassSurf.R + (redSurf.R - glassSurf.R) * hp),
                        (int)(glassSurf.G + (redSurf.G - glassSurf.G) * hp),
                        (int)(glassSurf.B + (redSurf.B - glassSurf.B) * hp))
                    : glassSurf;
                Color closeAcc = hp > 0.001f
                    ? Color.FromArgb((int)(236 * hp + 255 * (1 - hp)), (int)(74 + 181 * (1 - hp)), (int)(62 + 193 * (1 - hp)))
                    : acc;
                Gfx.NeuCircle(g, cbr0, closeSurf, Gfx.A(closeAcc, (int)(200 * ab0 / 255f)), hp > 0.5f, false,
                    (int)((StyleNeu() ? 60 : 24) * ab0 / 255f), (int)((StyleNeu() ? 60 : 0) * ab0 / 255f));
                if (hp > 0.01f)
                {
                    using (Pen pr = new Pen(Color.FromArgb((int)(240 * ab0 / 255f), 236, 74, 62), 3.2f))
                    {
                        pr.StartCap = LineCap.Round; pr.EndCap = LineCap.Round;
                        g.DrawArc(pr, cbr0.X - 3f, cbr0.Y - 3f, cbr0.Width + 6f, cbr0.Height + 6f, -90f, 360f * hp);
                    }
                }
                // （长按提示条挪到最后统一画：这里画会被后面的万能键盖住）
                using (Pen cbp = new Pen(Color.FromArgb((int)((238 + 17 * _closeDown) * ab0 / 255f), 255, 255, 255), 1.8f + 1.4f * _closeDown + 0.8f * (_closeLong ? 1f : 0f)))
                {
                    float pad = 10 + 2f * _closeDown;
                    g.DrawLine(cbp, cbr0.Left + pad, cbr0.Top + pad, cbr0.Right - pad, cbr0.Bottom - pad);
                    g.DrawLine(cbp, cbr0.Right - pad, cbr0.Top + pad, cbr0.Left + pad, cbr0.Bottom - pad);
                }
                g.Transform = m0;
            }

            // gear (settings) button
            if (pb1 > 0.01f)
            {
                System.Drawing.Drawing2D.Matrix m1 = g.Transform;
                g.TranslateTransform(sh1.X, sh1.Y);
                Rectangle gbr = Shrink(GearButtonRect(), _gearDown);
            Diag("设置键", gbr);
            DrawBtnGlow(g, gbr, _gearGlow);
                using (GraphicsPath gbp2 = new GraphicsPath()) { gbp2.AddEllipse(gbr); BackdropClip(g, gbp2, ab1); gbp2.Dispose(); }
                Gfx.NeuCircle(g, gbr, Gfx.A(GlassBase(), GlassA((int)((_gearHover ? UiFeel.SurfaceHover : (_gearDown > 0.5f ? UiFeel.SurfacePress : UiFeel.SurfaceIdle)) * ab1 / 255f))),
                    Gfx.A(acc, (int)(200 * ab1 / 255f)), false, false,
                    (int)((StyleNeu() ? 60 : 24) * ab1 / 255f), (int)((StyleNeu() ? 60 : 0) * ab1 / 255f));
                float gcx = gbr.X + gbr.Width / 2f, gcy = gbr.Y + gbr.Height / 2f;
                float gro = gbr.Width * 0.28f;
                using (Pen gp2 = new Pen(Color.FromArgb((int)(238 * ab1 / 255f), 255, 255, 255), 1.8f))
                {
                    g.DrawEllipse(gp2, gcx - gro * 0.62f, gcy - gro * 0.62f, gro * 1.24f, gro * 1.24f);
                    for (int k = 0; k < 8; k++)
                    {
                        double th = k * Math.PI / 4.0;
                        float x1 = (float)(gcx + Math.Cos(th) * gro * 0.7f), y1 = (float)(gcy + Math.Sin(th) * gro * 0.7f);
                        float x2 = (float)(gcx + Math.Cos(th) * gro * 1.28f), y2 = (float)(gcy + Math.Sin(th) * gro * 1.28f);
                        g.DrawLine(gp2, x1, y1, x2, y2);
                    }
                }
                g.Transform = m1;
            }

            // 万能键（弧线内侧中点的摇杆式大圆盘）
            // NO_KEY 变体里 KeyRect() 是空的，这里必须直接跳过，否则会拿 0 尺寸去建画刷
            Rectangle kr = KeyRect();
            if (kr.Width > 8 && kr.Height > 8) Diag("万能键（圆盘）", kr);
            float kcx = kr.X + kr.Width / 2f, kcy = kr.Y + kr.Height / 2f;
            float krr = kr.Width / 2f;
            float pk = IntroP(0.16f);
            PointF sk = IntroShift(pk);
            bool hasKey = kr.Width > 8 && kr.Height > 8;
            if (hasKey && pk > 0.01f) { g.TranslateTransform(sk.X, sk.Y); }

            if (_delConfirm && hasKey)
            {
                // 左半 = 取消（灰绿），右半 = 确认删除（红），鼠标所在半更亮
                using (GraphicsPath lp = new GraphicsPath())
                {
                    lp.AddArc(kr, 90f, 180f);
                    lp.CloseFigure();
                    int la = (_delHalf == 0) ? 250 : 205;
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb((int)(la * a / 255f), 62, 178, 112)))
                        g.FillPath(lb, lp);
                }
                using (GraphicsPath rp = new GraphicsPath())
                {
                    rp.AddArc(kr, 270f, 180f);
                    rp.CloseFigure();
                    int ra = (_delHalf == 1) ? 255 : 215;
                    using (SolidBrush rb = new SolidBrush(Color.FromArgb((int)(ra * a / 255f), 232, 64, 80)))
                        g.FillPath(rb, rp);
                }
                using (Pen kp2 = new Pen(Color.FromArgb((int)(235 * a / 255f), 255, 255, 255), 1.6f))
                    g.DrawEllipse(kp2, kr);
                using (Pen lp2 = new Pen(Color.FromArgb((int)(235 * a / 255f), 255, 255, 255), 1.4f))
                    g.DrawLine(lp2, kcx, kr.Y + 6f, kcx, kr.Bottom - 6f);
                using (Font fh = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
                {
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb((int)(245 * a / 255f), 255, 255, 255)))
                    {
                        SizeF s1 = g.MeasureString(Lang.T("取消", "Cancel"), fh);
                        g.DrawString(Lang.T("取消", "Cancel"), fh, tb, kcx - krr / 2f - s1.Width / 2f, kcy - s1.Height / 2f);
                        SizeF s2b = g.MeasureString(Lang.T("确认", "Confirm"), fh);
                        g.DrawString(Lang.T("确认", "Confirm"), fh, tb, kcx + krr / 2f - s2b.Width / 2f, kcy - s2b.Height / 2f);
                    }
                }
                using (Font f3 = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush b3 = new SolidBrush(Color.FromArgb((int)(235 * a / 255f), 255, 210, 210)))
                {
                    string t3 = Lang.T("删除「", "Delete \"") + FitName(_mgr.ActiveWheel.Name, 12) + Lang.T("」？点左半取消 / 右半确认", "\"? Left half cancels / right half confirms");
                    SizeF s3 = g.MeasureString(t3, f3);
                    g.DrawString(t3, f3, b3, kcx - s3.Width / 2f, kr.Y - s3.Height - 4);
                }
            }
            else if (hasKey)
            {
            // 万能键：玻璃盘 + 主题色内芯（新拟态 + 扁平 + 毛玻璃）
            DrawKeyDisc(g, (int)(a * pk), acc, kr, kcx, kcy, krr);
            }
            if (hasKey && pk > 0.01f) g.TranslateTransform(-sk.X, -sk.Y);   // 恢复，别影响后面的元素

            // 当前 wheel 名：药丸底 + 主题色圆点（可在设置里关掉）
            float pn = IntroP(0.62f);
            if (hasKey && a > 60 && pn > 0.01f && _settings.ShowNameLabel)
            {
                PointF sn = IntroShift(pn);
                int an = (int)(a * pn);
                g.TranslateTransform(sn.X, sn.Y);
                using (Font fw = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
                {
                    string wn = FitName(_mgr.ActiveWheel.Name, 12);
                    // 0.6.0 修正：这一块**恢复成原来的样子**（位置、形状都没动）。
                    // 我上一版把它挪到弧上端外侧、还弯成了弧形 —— 用户明确说"项目名字不用变位置"。
                    // 要"随弧弯"的是「几 / 几」那个计数胶囊（见 DrawCountPill），不是这个。
                    SizeF ws = g.MeasureString(wn, fw);
                    float dot = 9f;
                    float pw2 = ws.Width + dot + 30f, ph2 = ws.Height + 8f;
                    // 切轮盘时"翻一下"（v1.0）：药丸弹一下 + 边框用主题色亮一下。
                    // 为什么是名字而不是别处：**"我现在在哪个轮盘上"是持续性信息** ——
                    // 原来只有环上闪一下，太轻，一眨眼就过去了，切完还得再确认一次。
                    float pop = _nameSwapT < 1f ? (float)Math.Sin(_nameSwapT * Math.PI) : 0f;   // 0→1→0
                    if (pop > 0.001f)
                    {
                        float grow2 = 5f * pop;
                        pw2 += grow2 * 2f; ph2 += grow2 * 2f;
                    }
                    float wx = kcx - pw2 / 2f;
                    float wy = kr.Y + kr.Height + 4f;
                    if (pop > 0.001f) wy -= 2.5f * pop;          // 稍微抬一点，弹跳感更像"翻了一下"
                    Diag("名字药丸（当前轮盘名）", new RectangleF(wx, wy, pw2, ph2));
                    RectangleF pill2 = new RectangleF(wx, wy, pw2, ph2);
                    _namePillRect = pill2;                       // 记下来给命中测试用（点它能改名）
                    using (GraphicsPath pg2 = Gfx.Round(pill2, ph2 / 2f))
                    {
                        BackdropClip(g, pg2, an);
                        Gfx.GlassPanel(g, pg2, pill2, Gfx.A(GlassBase(), GlassA((int)((_nameHover ? 210 : 176) * an / 255f))),
                            (int)((StyleNeu() ? 40 : 18) * an / 255f), (int)((StyleNeu() ? 34 : 0) * an / 255f), !StyleFlatOnly());
                        int bpA = (int)((_nameHover ? 235 : 120) * an / 255f);
                        Color bpC = Gfx.Shade(acc, 0.15f);
                        if (pop > 0.001f) { bpA = Math.Min(255, bpA + (int)(150 * pop)); bpC = acc; }
                        using (Pen bp2 = new Pen(Gfx.A(bpC, bpA), (_nameHover ? 1.6f : 1.1f) + 1.4f * pop))
                            g.DrawPath(bp2, pg2);
                    }
                    float dy2 = pill2.Y + ph2 / 2f;
                    using (SolidBrush db2 = new SolidBrush(Color.FromArgb((int)(250 * an / 255f), acc.R, acc.G, acc.B)))
                        g.FillEllipse(db2, pill2.X + 11f, dy2 - dot / 2f, dot, dot);
                    using (SolidBrush bw = new SolidBrush(Color.FromArgb((int)(245 * an / 255f), 255, 255, 255)))
                        g.DrawString(wn, fw, bw, pill2.X + 13f + dot, pill2.Y + (ph2 - ws.Height) / 2f + 1);
                    // 悬停时在右边补一句Lang.T("点一下改名", "Click to rename")
                    if (_nameHover && an > 80)
                    {
                        using (Font ft = new Font("Microsoft YaHei UI", 9f))
                        using (SolidBrush bt = new SolidBrush(Color.FromArgb((int)(220 * an / 255f), 235, 238, 245)))
                            g.DrawString(Lang.T("点一下改名", "Click to rename"), ft, bt, pill2.Right + 8f, dy2 - ft.Height / 2f + 1);
                    }
                }
                g.TranslateTransform(-sn.X, -sn.Y);
            }

            // 圆盘菜单
            if (_menuT > 0.01f)
            {
                PointF kc = KeyCenter();
                float R = 78f * (0.55f + 0.45f * _menuT);
                int alpha = (int)(_menuT * 235);
                string[] labels = { Lang.T("新建", "New"), Lang.T("下一个", "Next"), Lang.T("删除", "Delete"), Lang.T("上一个", "Previous") };
                for (int s2 = 0; s2 < 4; s2++)
                {
                    bool sel = (_sector == s2);
                    Color sc;
                    if (s2 == 2) sc = Color.FromArgb(sel ? 230 : 170, 214, 70, 84);        // 删除=红
                    else sc = sel ? Color.FromArgb(235, acc.R, acc.G, acc.B) : Color.FromArgb(170, 26, 28, 33);
                    using (GraphicsPath gp2 = new GraphicsPath())
                    {
                        gp2.AddArc(kc.X - R, kc.Y - R, R * 2, R * 2, s2 * 90 - 135, 88);
                        gp2.AddLine(kc.X, kc.Y, kc.X, kc.Y);
                        gp2.CloseFigure();
                        // 玻璃扇区 + 选中时主题色点亮（新拟态：外圈加一道高光）
                        BackdropClip(g, gp2, alpha);
                        Color scFill = sel ? Gfx.A(Gfx.Shade(acc, 0.05f), (int)(alpha * 0.92f))
                                           : (s2 == 2 ? Color.FromArgb((int)(alpha * 0.72f), 150, 46, 58)
                                                      : Gfx.A(GlassBase(), (int)(alpha * 0.86f)));
                        using (SolidBrush sb2 = new SolidBrush(scFill))
                            g.FillPath(sb2, gp2);
                        using (Pen sp2 = new Pen(Color.FromArgb((int)(alpha * (sel ? 0.75f : 0.42f)), 255, 255, 255), 1.2f))
                            g.DrawPath(sp2, gp2);
                    }
                    double mid = (-90 + s2 * 90) * Math.PI / 180.0;
                    float tx = (float)(kc.X + Math.Cos(mid) * R * 0.62f);
                    float ty = (float)(kc.Y + Math.Sin(mid) * R * 0.62f);
                    string lab = labels[s2];
                    using (Font f2b = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                    using (SolidBrush sb3 = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                    {
                        SizeF ls = g.MeasureString(lab, f2b);
                        g.DrawString(lab, f2b, sb3, tx - ls.Width / 2f, ty - ls.Height / 2f);
                    }
                }
            }
            Rectangle sbr = Shrink(ShootButtonRect(), _shootDown);
            Diag("截图键", sbr);
            DrawBtnGlow(g, sbr, _shootGlow);
            if (pb2 > 0.01f)
            {
                g.TranslateTransform(sh2.X, sh2.Y);
                using (GraphicsPath sbp2 = new GraphicsPath()) { sbp2.AddEllipse(sbr); BackdropClip(g, sbp2, ab2); sbp2.Dispose(); }
                using (SolidBrush sbbs = new SolidBrush(Color.FromArgb((int)((_shootHover ? UiFeel.SolidHover : UiFeel.SolidIdle) * ab2 / 255f), 0, 122, 204)))
                    g.FillEllipse(sbbs, sbr);
                using (Pen sp = new Pen(Color.FromArgb((int)(245 * ab2 / 255f), 255, 255, 255), 1.8f))
                {
                    float cx2 = sbr.X + sbr.Width / 2f, cy2 = sbr.Y + sbr.Height / 2f;
                    g.DrawRectangle(sp, cx2 - 8f, cy2 - 5f, 16f, 11f);
                    g.DrawEllipse(sp, cx2 - 3.4f, cy2 - 2.6f, 6.8f, 6.8f);
                    g.DrawLine(sp, cx2 - 4f, cy2 - 8f, cx2 + 4f, cy2 - 8f);
                }
                g.TranslateTransform(-sh2.X, -sh2.Y);
            }

            // 计数胶囊「3 / 8」跟滚动位置有关，单独draw（见 DrawCountPill）：
            // 留在这一层里的话，滚动时签名每帧都变，整层缓存就废了。

            // 外部文件拖到轮盘上方：提示松手加入
            if (_dropActive && _dropExternal && a > 90)
            {
                string tip = Lang.T("松手把 ", "Release to add ") + _dropCount + Lang.T(" 张图片加入「", " item(s) to \"") + FitName(_mgr.ActiveWheel.Name, 12) + "」";
                using (Font f = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
                {
                    SizeF sz = g.MeasureString(tip, f);
                    float px = c.X + Sx() * (EffR() * 0.78f) - sz.Width / 2f;
                    float py = c.Y + Sy() * (EffR() * 0.78f) - sz.Height / 2f;
                    RectangleF pill = new RectangleF(px - 14, py - 7, sz.Width + 28, sz.Height + 14);
                    using (GraphicsPath pg = Gfx.Round(pill, pill.Height / 2f))
                    {
                        using (SolidBrush pb = new SolidBrush(Color.FromArgb((int)(235 * a / 255f), 34, 120, 86)))
                            g.FillPath(pb, pg);
                        using (Pen pp2 = new Pen(Color.FromArgb((int)(220 * a / 255f), 150, 245, 190), 1.6f))
                            g.DrawPath(pp2, pg);
                    }
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb((int)(250 * a / 255f), 255, 255, 255)))
                        g.DrawString(tip, f, tb, pill.X + 14, pill.Y + 6);
                }
            }

            // 长按关闭键的提示条：位置放在关闭键正上方（避开万能键），并且最后画，不会被盖住
            if (_closeHoldP > 0.10f)
            {
                Rectangle cbr8 = Shrink(CloseButtonRect(), _closeDown);
                int ab8 = (int)(a * IntroP(0.30f));
                if (ab8 < 8) ab8 = 8;
                int ta = (int)(Math.Min(1f, (_closeHoldP - 0.10f) / 0.25f) * 240 * ab8 / 255f);
                string tip2 = _closeLong ? Lang.T("松手退出 · 移开取消", "Release to exit · move away to cancel") : Lang.T("按住不放 · 移开可取消", "Hold · move away to cancel");
                using (Font ft2 = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                using (SolidBrush tb2 = new SolidBrush(Color.FromArgb(ta, 255, 255, 255)))
                {
                    SizeF ts2 = g.MeasureString(tip2, ft2);
                    // 放在轮盘左下角那条提示带（和 toast 同一位置）：
                    // 按钮上方被万能键占着、旁边被缩略图占着，只有这里是干净的
                    SizeF ls3 = LogicalSize();
                    float px2 = 26f;
                    float py2 = ls3.Height - ts2.Height - 26f;   // 再往下让开计数胶囊
                    RectangleF pr2 = new RectangleF(px2 - 8f, py2 - 4f, ts2.Width + 16f, ts2.Height + 8f);
                    using (GraphicsPath clPath = Gfx.Round(pr2, pr2.Height / 2f))
                    {
                        BackdropClip(g, clPath, ta);
                        using (SolidBrush clBg = new SolidBrush(Color.FromArgb((int)(ta * 0.62f), 22, 24, 30))) g.FillPath(clBg, clPath);
                        using (Pen clPen = new Pen(Color.FromArgb((int)(ta * 0.55f), 236, 74, 62), 1.4f)) g.DrawPath(clPen, clPath);
                    }
                    g.DrawString(tip2, ft2, tb2, px2, py2);
                }
            }

            DrawToast(g, a);

            DrawNubs(g, a);      // 展开状态下也画一个Lang.T("收起", "Collapse")把手（贴着另一条屏幕边）
        }

        // 计数胶囊「当前 / 总数」：跟着滚动位置变，所以每帧单独画（不进缓存层）
        void DrawCountPill(Graphics g, int a)
        {
            Color acc = _accentCur;
            if (!_settings.ShowCountLabel) return;
            // 没图就没有「几 / 几」可显示 —— 别在淡出期间画出「1 / 0」这种数字
            if (_store.Items.Count == 0) return;
            // 和空态提示共用 _emptyT 做交叉淡入：有图时它淡入（同一时刻提示正在淡出）。
            // 再乘收起进度 —— 环和卡片缩回去的时候，胶囊也得跟着退，不能等 _collapsed 置位那一刻跳掉。
            // （展开那一路本来就有 IntroP，见下面的 pc2。）
            float vis = (1f - _emptyT) * CollapseCardP();
            if (vis <= 0.02f) return;
            a = (int)(a * vis);
                // 0.5.3：视口锚点改成"最新那张顶在弧上端"之后，`_offset` 是**弧下端那一格**的下标，
                // 所以可见区里最靠上（最新）的那张 = _offset + Slots。这么写，默认视口下就是 N/N
                // （最新那张在最上面），往上滚会依次变小 —— 和以前"跟着滚动位置变"的语义一致。
                int cur = (int)Math.Round(_targetOffset) + 1;   // 序号 = 弧下端那一张（1 起算）：滚轮滚到哪张就显示哪张，范围 1..总数
                if (cur < 1) cur = 1;
                if (cur > _store.Items.Count) cur = _store.Items.Count;
                string idx = cur + " / " + _store.Items.Count;
                float fs = Math.Max(9f, Math.Min(40f, _settings.LabelSize));
                float pc2 = IntroP(0.72f);
                if (pc2 > 0.01f)
                {
                PointF sc2 = IntroShift(pc2);
                int ac2 = (int)(a * pc2);
                g.TranslateTransform(sc2.X, sc2.Y);
                using (Font f = new Font("Microsoft YaHei UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    SizeF sz = g.MeasureString(idx, f);
                    // 0.6.0 弧线设计语言：计数胶囊也是**沿弧弯出来的弧带**、文字逐字沿弧转。
                    // 角度放在弧下端之外（phi 更小），与弧上端的名称胶囊左右对称，
                    // 中间那段"万能键左上方"留给三个小按钮。
                    PointF cc2 = Center();
                    float sx3 = Sx(), sy3 = Sy();
                    float ip = sz.Height * 0.72f;                    // 前置的小圆点
                    float h3 = sz.Height + 12f;
                    float r3 = EffR() + 78f;                       // 回到 45° 对角线外侧那一档（原来的位置）
                    float mid3 = (_phiMin + _phiMax) / 2f;         // 45° 对角线方向
                    // 弧长按内容算：圆点 + 间隔 + 文字 + 两端留白 —— 这样数字绝不会被胶囊边缘切到
                    float needLen = ip + sz.Width * 1.04f + 30f;   // 圆点(直径+端头留白) + 间隙 + 文字 + 尾端留白
                    float half3 = needLen / 2f / r3;
                    using (GraphicsPath pg = ArcUi.Capsule(cc2, sx3, sy3, r3, h3, mid3 - half3, mid3 + half3))
                    {
                        RectangleF bnd3 = pg.GetBounds();
                        Diag("计数胶囊「几 / 几」", bnd3);
                        BackdropClip(g, pg, ac2);
                        Gfx.GlassPanel(g, pg, bnd3, Gfx.A(GlassBase(), GlassA((int)(176 * ac2 / 255f))),
                            (int)((StyleNeu() ? 38 : 16) * ac2 / 255f), (int)((StyleNeu() ? 32 : 0) * ac2 / 255f), !StyleFlatOnly());
                        using (Pen pp2 = new Pen(Gfx.A(Gfx.Shade(acc, 0.15f), (int)(110 * ac2 / 255f)), 1.1f))
                            g.DrawPath(pp2, pg);
                    }
                    // 内容按水平直线摆（弧长很短时和弧的差别可忽略），左右严格留白 ——
                    // 用户反馈的计数胶囊数字显示问题就是这里排版太挤导致数字被切。
                    float dotAng = (ip + 12f) / r3;                    // 圆点直径 + 端头留白
                    PointF dp3 = ArcUi.Polar(cc2, sx3, sy3, mid3 + half3 - dotAng / 2f, r3);   // 圆点在弧的前一端（这个角下 phi 大的一侧才是屏幕左上方）
                    // （圆点与文字都按弧坐标摆，不再用屏幕直线坐标）
                    using (SolidBrush db = new SolidBrush(Color.FromArgb((int)(245 * ac2 / 255f), acc.R, acc.G, acc.B)))
                        g.FillEllipse(db, dp3.X - ip / 2f, dp3.Y - ip / 2f, ip, ip);
                    using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(246 * ac2 / 255f), 255, 255, 255)))
                    // 数字也沿弧排（用户要求跟胶囊同一条弧）：弧长按内容算足了，整串都在胶囊里
                    {
                    float step3 = (sz.Width * 1.04f / Math.Max(1, idx.Length)) / r3;   // 每字一个角：按实测字宽，紧凑
                    float textMid = mid3 + half3 - dotAng - (ip / 2f + 6f + sz.Width * 1.04f / 2f) / r3;   // 文字排在圆点之后，中间留 6px 缝（用户反馈：字和圆点会重叠）
                        ArcUi.ArcText(g, idx, f, br, cc2, sx3, sy3, r3, textMid, step3, 0.45f);
                    }
                    }
                g.TranslateTransform(-sc2.X, -sc2.Y);
                }
        }

    }
}
