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
        void DrawWithAlpha(Graphics g, Bitmap bmp, RectangleF dest, int alpha)
        {
            // 目标尺寸跟位图**正好 1:1** 时绕开重采样：
            // 画布上开着 HighQualityBicubic，即便一张图是 1:1 贴上去，GDI+ 也会老老实实走双三次插值
            // —— 实测每张约 0.5ms（一张卡片三块贴片 + 一张缩略图 ≈ 2ms，8 张卡片一帧就是 13ms）。
            // 关键：**不能取整坐标、也不能复位变换**。取整会让贴片/缩略图相对卡片边框跳 1 个像素
            // （卡片边框是按精确小数坐标画的），用户看到的就是"缩略图边框抽搐"。
            // 所以保留变换，只在 1:1 时把插值换成最近邻：既省掉重采样，位置也跟原来一模一样。
            if (Math.Abs(dest.Width * UiK - bmp.Width) < 0.6f && Math.Abs(dest.Height * UiK - bmp.Height) < 0.6f)
            {
                InterpolationMode oldIm = g.InterpolationMode;
                PixelOffsetMode oldPo = g.PixelOffsetMode;
                try
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    if (alpha >= 250) g.DrawImage(bmp, dest);
                    else
                    {
                        ColorMatrix cm = new ColorMatrix();
                        cm.Matrix33 = Math.Max(0f, Math.Min(1f, alpha / 255f));
                        _ia.SetColorMatrix(cm);
                        g.DrawImage(bmp, new Rectangle((int)dest.X, (int)dest.Y, (int)dest.Width, (int)dest.Height),
                            0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, _ia);
                    }
                }
                finally
                {
                    g.InterpolationMode = oldIm;
                    g.PixelOffsetMode = oldPo;
                }
                return;
            }
            if (alpha >= 250) { g.DrawImage(bmp, dest); return; }
            ColorMatrix cm2 = new ColorMatrix();
            cm2.Matrix33 = Math.Max(0f, Math.Min(1f, alpha / 255f));
            _ia.SetColorMatrix(cm2);
            g.DrawImage(bmp, new Rectangle((int)dest.X, (int)dest.Y, (int)dest.Width, (int)dest.Height),
                0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, _ia);
        }


        void PruneCaches()
        {
            if (_thumbCache.Count <= _store.Items.Count) return;
            List<StoreItem> dead = new List<StoreItem>();
            foreach (StoreItem k in _thumbCache.Keys) if (!_store.Items.Contains(k)) dead.Add(k);
            for (int i = 0; i < dead.Count; i++)
            {
                foreach (Bitmap b in _thumbCache[dead[i]].Values) { try { b.Dispose(); } catch { } }
                _thumbCache.Remove(dead[i]);
            }
        }


        Bitmap ScaledThumb(StoreItem it, int w, int h)
        {
            // w/h 是逻辑尺寸；实际按物理像素生成，缩放到高 DPI 屏上才不会发虚。
            // 注意：尺寸**不能量化**。量化会让"1:1 贴图"变成重采样贴图，实测反而更慢
            // （缩略图那一段 2.98 → 3.72ms），所以这里保持精确尺寸。
            int dw = Math.Max(1, (int)Math.Round(w * UiK));
            int dh = Math.Max(1, (int)Math.Round(h * UiK));
            Dictionary<long, Bitmap> d;
            if (!_thumbCache.TryGetValue(it, out d)) { d = new Dictionary<long, Bitmap>(); _thumbCache[it] = d; }
            long key = ((long)dw << 20) | (uint)dh;
            Bitmap b;
            if (d.TryGetValue(key, out b)) return b;

            // 没命中：优先拿"比目标略大的现成缩略图"当源。放大预览时尺寸每帧都在变，
            // 每帧都从原图重做一次高质量缩放会掉帧；从已经缩过的大图再缩下去便宜得多，
            // 画质几乎没差别（都是双三次，源本身也是高质量缩出来的）。
            Bitmap src = it.Image;
            long bestArea = 0;
            foreach (KeyValuePair<long, Bitmap> kv in d)
            {
                Bitmap cand = kv.Value;
                if (cand.Width >= dw && cand.Height >= dh)
                {
                    long area = (long)cand.Width * cand.Height;
                    if (bestArea == 0 || area < bestArea) { bestArea = area; src = cand; }
                }
            }

            if (d.Count > 64)
            {
                foreach (Bitmap v in d.Values) { try { v.Dispose(); } catch { } }
                d.Clear();
                src = it.Image;
            }
            b = new Bitmap(dw, dh, PixelFormat.Format32bppPArgb);
            using (Perf.Section("2c9-缩略图生成"))
            using (Graphics gg = Graphics.FromImage(b))
            {
                gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                gg.DrawImage(src, new Rectangle(0, 0, dw, dh));
            }
            d[key] = b;
            return b;
        }


        // exactly what DrawWheel draws for item i (so grabbing matches what you see)
        RectangleF DrawnRect(int i)
        {
            if (i < 0 || i >= _store.Items.Count) return RectangleF.Empty;
            float sc; if (!_scales.TryGetValue(i, out sc)) sc = 1f;
            if (_store.Items[i] == _dragOutItem) sc *= Math.Max(0f, 1f - _dragOutProg);
            if (_store.Items[i] == _deletingItem) sc *= Math.Max(0f, 1f - _deleteProg);
            SizeF b = CardSize(_store.Items[i]);
            int iw = Math.Max(4, (int)Math.Round(b.Width * sc));
            int ih = Math.Max(4, (int)Math.Round(b.Height * sc));
            PointF pc = ItemCenter(i);
            float x = (float)Math.Round(pc.X - iw / 2f), y = (float)Math.Round(pc.Y - ih / 2f);
            return new RectangleF(x - CardPad, y - CardPad, iw + 2 * CardPad, ih + 2 * CardPad);
        }


        void Render()
        {
            if (!IsHandleCreated || !Visible) return;
            RenderCountForTest++;               // 测试用（省电验收要数「到底画了几帧」）
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try { RenderCore(); }
            finally
            {
                sw.Stop();
                FrameStats.Sample(sw.Elapsed.TotalMilliseconds, FrameState());
            }
        }


        // 慢帧要能说清"当时界面是什么状态"，否则只知道慢、不知道因为什么慢
        string FrameState()
        {
            return "show=" + _show.ToString("0.00") + " target=" + _targetShow.ToString("0.00") +
                   " 图=" + _store.Items.Count + " 缩略图缓存=" + _thumbCache.Count +
                   " offset=" + _offset.ToString("0.0") + "/" + _targetOffset.ToString("0.0") +
                   " hover=" + _hover + " 放大=" + _enlarged + " peek=" + _peekIndex +
                   " 菜单=" + _menuOpen + " intro=" + _intro + " 收起中=" + _collapsing + " 已收起=" + _collapsed +
                   " 删除中=" + (_deletingItem != null) + " 展开动画=" + _showAnimating + " 提示=" + (_toast.Length > 0) +
                   " 拖出=" + (_dragOutItem != null ? "进行中" : "无") + (_lastDragInfo.Length > 0 ? " 上次【" + _lastDragInfo + "】" : "");
        }

        string _lastDragInfo = "";      // 上一次拖出去的结果（只给日志看：格式 / 目标有没有接收）


        void RenderCore()
        {
            PruneCaches();
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;

            _frameNo++;
            EnsureDib(w, h);
            using (Graphics g = Graphics.FromHdc(_memDc))
            {
                using (Perf.Section("1-清屏"))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.Clear(Color.Transparent);                 // zero the reused DIB (no allocation)
                    g.CompositingMode = CompositingMode.SourceOver;
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                try
                {
                    using (Perf.Section("2-绘制内容")) DrawWheel(g, w, h);
                }
                catch (Exception ex) { Err.Log("DrawWheel", ex); }      // 画错一帧总好过整个程序崩掉
            }

            using (Perf.Section("3-推送窗口"))
            {
                IntPtr screenDc = Native.GetDC(IntPtr.Zero);
                Native.SIZE size = new Native.SIZE(w, h);
                Native.POINT src = new Native.POINT(0, 0);
                Native.POINT dst = new Native.POINT(Left, Top);
                Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
                bf.BlendOp = Native.AC_SRC_OVER; bf.BlendFlags = 0; bf.SourceConstantAlpha = 255; bf.AlphaFormat = Native.AC_SRC_ALPHA;
                bool ok = Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, _memDc, ref src, 0, ref bf, Native.ULW_ALPHA);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                if (!ok)
                {
                    // safety fallback: render through a plain bitmap if the DIB path fails on this machine
                    Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    using (Graphics g2 = Graphics.FromImage(bmp))
                    {
                        g2.SmoothingMode = SmoothingMode.AntiAlias;
                        g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g2.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        g2.Clear(Color.Transparent);
                        try { DrawWheel(g2, w, h); }
                        catch (Exception ex) { Err.Log("DrawWheel-fallback", ex); }
                    }
                    Native.PushLayered(this, bmp);
                    bmp.Dispose();
                }
            }
            _rendered = true;
        }


        void EnsureDib(int w, int h)
        {
            if (_memDc != IntPtr.Zero && _dibW == w && _dibH == h) return;
            ReleaseDib();
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            _memDc = Native.CreateCompatibleDC(screenDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            Native.BITMAPINFO bi = new Native.BITMAPINFO();
            bi.bmiHeader.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h;                 // top-down
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = 0;             // BI_RGB
            _bits = IntPtr.Zero;
            _dib = Native.CreateDIBSection(_memDc, ref bi, 0, out _bits, IntPtr.Zero, 0);
            _oldBmp = Native.SelectObject(_memDc, _dib);
            _dibW = w; _dibH = h;
        }


        void ReleaseDib()
        {
            if (_memDc == IntPtr.Zero) return;
            try
            {
                if (_oldBmp != IntPtr.Zero) Native.SelectObject(_memDc, _oldBmp);
                if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
                Native.DeleteDC(_memDc);
            }
            catch { }
            _memDc = IntPtr.Zero; _dib = IntPtr.Zero; _oldBmp = IntPtr.Zero; _bits = IntPtr.Zero;
        }


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


        // 万能键：新拟态玻璃圆盘 —— 玻璃底 + 上亮下暗 + 主题色核心，按下时核心点亮并轻微放大
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
                    string hint = "截图后会出现在这里";
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


        // ---- 贴边小把手：收起态画"拉出"、展开态画"收起" ----
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
                string ht = willExpand ? "点我展开" : "点我收起";
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


        // ---- 控件层：关闭键 / 设置键 / 万能键 / 名字药丸 / 提示条 / 把手 ----
        void DrawControls(Graphics g, int a)
        {
            // 按下反馈：缩小一点 + 描边更亮，让"按下去"看得见
            PointF c = Center();
            Rectangle cbr = Shrink(CloseButtonRect(), _closeDown);
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
                        SizeF s1 = g.MeasureString("取消", fh);
                        g.DrawString("取消", fh, tb, kcx - krr / 2f - s1.Width / 2f, kcy - s1.Height / 2f);
                        SizeF s2b = g.MeasureString("确认", fh);
                        g.DrawString("确认", fh, tb, kcx + krr / 2f - s2b.Width / 2f, kcy - s2b.Height / 2f);
                    }
                }
                using (Font f3 = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush b3 = new SolidBrush(Color.FromArgb((int)(235 * a / 255f), 255, 210, 210)))
                {
                    string t3 = "删除「" + FitName(_mgr.ActiveWheel.Name, 12) + "」？点左半取消 / 右半确认";
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
                    float wx = kcx - pw2 / 2f;
                    float wy = kr.Y + kr.Height + 4f;
                    RectangleF pill2 = new RectangleF(wx, wy, pw2, ph2);
                    _namePillRect = pill2;                       // 记下来给命中测试用（点它能改名）
                    using (GraphicsPath pg2 = Gfx.Round(pill2, ph2 / 2f))
                    {
                        BackdropClip(g, pg2, an);
                        Gfx.GlassPanel(g, pg2, pill2, Gfx.A(GlassBase(), GlassA((int)((_nameHover ? 210 : 176) * an / 255f))),
                            (int)((StyleNeu() ? 40 : 18) * an / 255f), (int)((StyleNeu() ? 34 : 0) * an / 255f), !StyleFlatOnly());
                        using (Pen bp2 = new Pen(Gfx.A(Gfx.Shade(acc, 0.15f), (int)((_nameHover ? 235 : 120) * an / 255f)), _nameHover ? 1.6f : 1.1f))
                            g.DrawPath(bp2, pg2);
                    }
                    float dy2 = pill2.Y + ph2 / 2f;
                    using (SolidBrush db2 = new SolidBrush(Color.FromArgb((int)(250 * an / 255f), acc.R, acc.G, acc.B)))
                        g.FillEllipse(db2, pill2.X + 11f, dy2 - dot / 2f, dot, dot);
                    using (SolidBrush bw = new SolidBrush(Color.FromArgb((int)(245 * an / 255f), 255, 255, 255)))
                        g.DrawString(wn, fw, bw, pill2.X + 13f + dot, pill2.Y + (ph2 - ws.Height) / 2f + 1);
                    // 悬停时在右边补一句"点一下改名"
                    if (_nameHover && an > 80)
                    {
                        using (Font ft = new Font("Microsoft YaHei UI", 9f))
                        using (SolidBrush bt = new SolidBrush(Color.FromArgb((int)(220 * an / 255f), 235, 238, 245)))
                            g.DrawString("点一下改名", ft, bt, pill2.Right + 8f, dy2 - ft.Height / 2f + 1);
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
                string[] labels = { "新建", "下一个", "删除", "上一个" };
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
                string tip = "松手把 " + _dropCount + " 张图片加入「" + FitName(_mgr.ActiveWheel.Name, 12) + "」";
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
                string tip2 = _closeLong ? "松手退出 · 移开取消" : "按住不放 · 移开可取消";
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

            DrawNubs(g, a);      // 展开状态下也画一个"收起"把手（贴着另一条屏幕边）
        }
        // 计数胶囊「当前 / 总数」：跟着滚动位置变，所以每帧单独画（不进缓存层）
        void DrawCountPill(Graphics g, int a)
        {
            Color acc = _accentCur;
            if (_store.Items.Count == 0 || !_settings.ShowCountLabel) return;
                // 0.5.3：视口锚点改成"最新那张顶在弧上端"之后，`_offset` 是**弧下端那一格**的下标，
                // 所以可见区里最靠上（最新）的那张 = _offset + Slots。这么写，默认视口下就是 N/N
                // （最新那张在最上面），往上滚会依次变小 —— 和以前"跟着滚动位置变"的语义一致。
                int cur = (int)Math.Round(_offset) + _slots;
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
                    float mid3 = (_phiMin + _phiMax) / 2f;         // 45° 对角线方向（用户要求靠 45° 角，别再往下偏）
                    float st3 = ArcUi.StepFor(g, idx, f, r3, 3f);
                    float span3 = st3 * (idx.Length - 1);
                    float b0 = mid3 - span3 / 2f - st3 * 1.5f;        // 左端留一截给圆点
                    float b1 = mid3 + span3 / 2f + st3 * 0.6f;
                    using (GraphicsPath pg = ArcUi.Capsule(cc2, sx3, sy3, r3, h3, b0, b1))
                    {
                        RectangleF bnd3 = pg.GetBounds();
                        BackdropClip(g, pg, ac2);
                        Gfx.GlassPanel(g, pg, bnd3, Gfx.A(GlassBase(), GlassA((int)(176 * ac2 / 255f))),
                            (int)((StyleNeu() ? 38 : 16) * ac2 / 255f), (int)((StyleNeu() ? 32 : 0) * ac2 / 255f), !StyleFlatOnly());
                        using (Pen pp2 = new Pen(Gfx.A(Gfx.Shade(acc, 0.15f), (int)(110 * ac2 / 255f)), 1.1f))
                            g.DrawPath(pp2, pg);
                    }
                    PointF dp3 = ArcUi.Polar(cc2, sx3, sy3, b0 + st3 * 0.6f, r3);
                    using (SolidBrush db = new SolidBrush(Color.FromArgb((int)(245 * ac2 / 255f), acc.R, acc.G, acc.B)))
                        g.FillEllipse(db, dp3.X - ip / 2f, dp3.Y - ip / 2f, ip, ip);
                    using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(246 * ac2 / 255f), 255, 255, 255)))
                        // 文字**水平**放在胶囊正中（用户反馈：斜着排读不出来，像"显示 bug"）。
                        // 弧线感交给胶囊本体，计数这种短文字保持正立 —— 好读优先。
                    {
                        PointF tp3 = ArcUi.Polar(cc2, sx3, sy3, mid3, r3);
                        SizeF isz = g.MeasureString(idx, f);
                        g.DrawString(idx, f, br, tp3.X - isz.Width / 2f + ip * 0.55f, tp3.Y - isz.Height / 2f);
                    }
                }
                g.TranslateTransform(-sc2.X, -sc2.Y);
                }
        }

    }
}
