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

        int GlassA(int baseA)
        {
            float g = StyleSolid() ? 1f : (_settings.GlassPercent / 100f);
            int a = (int)(baseA * g);
            return a < 0 ? 0 : (a > 255 ? 255 : a);
        }


        // ---------- 轮盘上的真·毛玻璃 ----------
        // 分层窗口不能上系统 acrylic（会给整个窗口矩形蒙灰），所以自己来：
        // 显示之前把轮盘背后那块屏幕抓下来 → 缩到 1/6 做盒式模糊 → 放大回去，
        // 画面板时把这块模糊底裁进形状里，再叠玻璃色 —— 透过去的确实是真桌面。
        DateTime _backdropAt = DateTime.MinValue;

        Bitmap _backdropBlur;

        bool _backdropValid;

        Bitmap _backdropOld;                 // 上一张模糊背景（换背景时交叉淡入，避免玻璃颜色突然一跳）

        float _backdropFade = 1f;            // 1 = 新背景完全不透明

        DateTime _backdropFadeAt = DateTime.MinValue;


        void FreeBackdrop()
        {
            if (_backdropBlur != null) { try { _backdropBlur.Dispose(); } catch { } _backdropBlur = null; }
            if (_backdropOld != null) { try { _backdropOld.Dispose(); } catch { } _backdropOld = null; }
            _backdropFade = 1f;
            _backdropValid = false;
        }


        // ---------- 毛玻璃后台抓取 ----------
        // 抓屏 + 高斯模糊要几十毫秒，原来是在 UI 线程里做的，动画期间会卡一下。
        // 现在：后台线程负责抓+糊，UI 线程只在下一帧把结果换上（沿用已有的交叉淡入，视觉不变）。
        volatile bool _glassBusy = false;

        Bitmap _glassPending = null;

        Point _glassPendingOffset = Point.Empty;


        public void RequestBackdropAsync()
        {
            if (StyleFlatOnly()) { FreeBackdrop(); return; }
            if (_glassBusy) return;
            if (Width < 20 || Height < 20) return;
            Rectangle vs = SystemInformation.VirtualScreen;
            Rectangle want = new Rectangle(Left, Top, Width, Height);
            Rectangle got = Rectangle.Intersect(want, vs);
            if (got.Width < 8 || got.Height < 8) return;
            _glassBusy = true;
            Point off = new Point(got.Left - want.Left, got.Top - want.Top);
            System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate()
            {
                Bitmap fresh = null;
                try
                {
                    using (Bitmap full = new Bitmap(got.Width, got.Height, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(full))
                            g.CopyFromScreen(got.Left, got.Top, 0, 0, new Size(got.Width, got.Height), CopyPixelOperation.SourceCopy);
                        fresh = BlurBitmap(full, 6);
                    }
                }
                catch { fresh = null; }
                lock (_glassLock)
                {
                    if (_glassPending != null) { try { _glassPending.Dispose(); } catch { } }
                    _glassPending = fresh;
                    _glassPendingOffset = off;
                }
                _glassBusy = false;
            }));
            th.IsBackground = true;
            try { th.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }   // 别和 UI 抢 CPU
            th.Start();
        }


        // 由 AnimTick 在 UI 线程调用：把后台糊好的底换上去
        void ApplyPendingBackdrop()
        {
            Bitmap fresh = null;
            Point off = Point.Empty;
            lock (_glassLock)
            {
                if (_glassPending == null) return;
                fresh = _glassPending; off = _glassPendingOffset; _glassPending = null;
            }
            if (StyleFlatOnly()) { try { fresh.Dispose(); } catch { } return; }
            if (_backdropBlur != null && Visible)
            {
                if (_backdropOld != null) { try { _backdropOld.Dispose(); } catch { } }
                _backdropOld = _backdropBlur;
                _backdropFade = 0f;
                _backdropFadeAt = DateTime.Now;
            }
            else
            {
                if (_backdropBlur != null) { try { _backdropBlur.Dispose(); } catch { } }
                if (!Visible && _backdropOld != null) { try { _backdropOld.Dispose(); } catch { } _backdropOld = null; _backdropFade = 1f; }
            }
            _backdropBlur = fresh;
            _backdropOffset = off;
            _backdropValid = true;
            _backdropAt = DateTime.Now;
            _rendered = false;
        }


        // 同步版：当场抓屏 + 模糊，要 12~16ms（窗口 658x658 实测），会卡住 UI 线程。
        // 正常路径一律用 RequestBackdropAsync —— 这里留着只是为了排查问题和测试对比。
        public void CaptureBackdrop()
        {
            if (StyleFlatOnly()) { FreeBackdrop(); return; }
            try
            {
                if (Width < 20 || Height < 20) return;
                Rectangle vs = SystemInformation.VirtualScreen;
                Rectangle want = new Rectangle(Left, Top, Width, Height);
                Rectangle got = Rectangle.Intersect(want, vs);
                if (got.Width < 8 || got.Height < 8) return;
                Bitmap fresh = null;
                using (Bitmap full = new Bitmap(got.Width, got.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(full))
                        g.CopyFromScreen(got.Left, got.Top, 0, 0, new Size(got.Width, got.Height), CopyPixelOperation.SourceCopy);
                    fresh = BlurBitmap(full, 6);
                }
                // 换背景不要"啪"地一跳：把旧图留着做交叉淡入（只有显示中才有必要看着它过渡）
                if (_backdropBlur != null && Visible)
                {
                    if (_backdropOld != null) { try { _backdropOld.Dispose(); } catch { } }
                    _backdropOld = _backdropBlur;
                    _backdropFade = 0f;
                    _backdropFadeAt = DateTime.Now;
                }
                else
                {
                    if (_backdropBlur != null) { try { _backdropBlur.Dispose(); } catch { } }
                    if (!Visible && _backdropOld != null) { try { _backdropOld.Dispose(); } catch { } _backdropOld = null; _backdropFade = 1f; }
                }
                _backdropBlur = fresh;
                _backdropOffset = new Point(got.Left - want.Left, got.Top - want.Top);
                _backdropValid = true;
                _backdropAt = DateTime.Now;
            }
            catch { FreeBackdrop(); }
        }


        // 缩小 -> 盒式模糊 -> 放大，得到"毛玻璃"那种糊
        static Bitmap BlurBitmap(Bitmap src, int down)
        {
            int w = Math.Max(2, src.Width / down), h = Math.Max(2, src.Height / down);
            using (Bitmap small = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
                BoxBlur(small, 3);
                BoxBlur(small, 3);
                BoxBlur(small, 2);
                Bitmap big = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(small, new Rectangle(0, 0, big.Width, big.Height));
                }
                return big;
            }
        }


        static void BoxBlur(Bitmap bmp, int r)
        {
            try
            {
                Rectangle rc = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData d = bmp.LockBits(rc, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    int W = d.Width, H = d.Height, n = W * H;
                    int[] px = new int[n];
                    int[] tmp = new int[n];
                    Marshal.Copy(d.Scan0, px, 0, n);
                    for (int y = 0; y < H; y++)
                    {
                        int row = y * W;
                        for (int x = 0; x < W; x++)
                        {
                            int a = 0, rr = 0, gg = 0, bb = 0, c = 0;
                            for (int k = -r; k <= r; k++)
                            {
                                int xx = x + k; if (xx < 0) xx = 0; if (xx >= W) xx = W - 1;
                                int v = px[row + xx];
                                a += (v >> 24) & 0xFF; rr += (v >> 16) & 0xFF; gg += (v >> 8) & 0xFF; bb += v & 0xFF; c++;
                            }
                            tmp[row + x] = ((a / c) << 24) | ((rr / c) << 16) | ((gg / c) << 8) | (bb / c);
                        }
                    }
                    for (int x = 0; x < W; x++)
                        for (int y = 0; y < H; y++)
                        {
                            int a = 0, rr = 0, gg = 0, bb = 0, c = 0;
                            for (int k = -r; k <= r; k++)
                            {
                                int yy = y + k; if (yy < 0) yy = 0; if (yy >= H) yy = H - 1;
                                int v = tmp[yy * W + x];
                                a += (v >> 24) & 0xFF; rr += (v >> 16) & 0xFF; gg += (v >> 8) & 0xFF; bb += v & 0xFF; c++;
                            }
                            px[y * W + x] = ((a / c) << 24) | ((rr / c) << 16) | ((gg / c) << 8) | (bb / c);
                        }
                    Marshal.Copy(px, 0, d.Scan0, n);
                }
                finally { bmp.UnlockBits(d); }
            }
            catch { }
        }


        bool UseBackdrop() { return _backdropValid && _backdropBlur != null && !StyleFlatOnly(); }

        // 把模糊背景裁进这个形状里（画玻璃面板前先调它）
        // alpha 必须传进来：否则淡出动画时玻璃底不跟着变淡，面板会像"卡住"一样不消失
        void BackdropClip(Graphics g, GraphicsPath path, int alpha)
        {
            if (!UseBackdrop() || alpha <= 2) return;
            try
            {
                GraphicsState st = g.Save();
                g.SetClip(path, CombineMode.Replace);
                // 这块模糊底是"物理像素"尺寸，而当前处在 UiK 缩放后的逻辑坐标系里，
                // 必须除回去，否则高 DPI 下会画得又大又偏 —— 毛玻璃直接就废了
                float bw = _backdropBlur.Width, bh = _backdropBlur.Height;
                RectangleF dest = new RectangleF(_backdropOffset.X / UiK, _backdropOffset.Y / UiK,
                                                 bw / UiK, bh / UiK);
                int al = alpha > 255 ? 255 : alpha;
                bool crossFade = _backdropOld != null && _backdropFade < 0.999f;
                if (crossFade)
                {
                    // 旧背景也要按元素 alpha 淡（漏乘的话，轮盘淡出时这层背景会一直是不透明的）
                    float ow = _backdropOld.Width, oh = _backdropOld.Height;
                    Rectangle dr0 = new Rectangle((int)Math.Round(dest.X), (int)Math.Round(dest.Y),
                        Math.Max(1, (int)Math.Round(ow / UiK)), Math.Max(1, (int)Math.Round(oh / UiK)));
                    ColorMatrix cmo = new ColorMatrix(); cmo.Matrix33 = al / 255f;
                    _iaBack.SetColorMatrix(cmo);
                    g.DrawImage(_backdropOld, dr0, 0, 0, ow, oh, GraphicsUnit.Pixel, _iaBack);
                }
                float newMul = crossFade ? Math.Max(0.06f, _backdropFade) : 1f;
                int alNew = (int)(al * newMul);
                if (alNew >= 250 && !crossFade) g.DrawImage(_backdropBlur, dest);
                else
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = alNew / 255f;
                    _iaBack.SetColorMatrix(cm);
                    g.DrawImage(_backdropBlur,
                        new Rectangle((int)Math.Round(dest.X), (int)Math.Round(dest.Y),
                                      Math.Max(1, (int)Math.Round(dest.Width)), Math.Max(1, (int)Math.Round(dest.Height))),
                        0, 0, bw, bh, GraphicsUnit.Pixel, _iaBack);
                }
                // 压一层黑：不管背后是亮桌面还是暗桌面，面板都能保持"深色玻璃"、字看得清
                int dk = (int)(26 * (StyleSolid() ? 1f : _settings.GlassPercent / 100f) * al / 255f);
                if (dk > 0)
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(dk, 0, 0, 0)))
                        g.FillPath(sb, path);
                g.Restore(st);
            }
            catch { try { g.ResetClip(); } catch { } }
        }
    }
}
