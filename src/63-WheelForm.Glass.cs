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

        int _backdropGen;                    // 玻璃底换到第几代（分层缓存的签名要用它，见 66-Layers）


        void FreeBackdrop()
        {
            if (_backdropBlur != null) { try { _backdropBlur.Dispose(); } catch { } _backdropBlur = null; }
            if (_backdropOld != null) { try { _backdropOld.Dispose(); } catch { } _backdropOld = null; }
            // 后台线程糊好、还没换上来的那张也要放掉：窗口关掉后它还挂在这儿白占内存
            if (_backdropMix != null) { try { _backdropMix.Dispose(); } catch { } _backdropMix = null; }
            _backdropMixFrame = -1;
            lock (_glassLock)
            {
                if (_glassPending != null) { try { _glassPending.Dispose(); } catch { } _glassPending = null; }
            }
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
        // 测试用：因为"新底和旧底一样"而跳掉了几次交叉淡入
        public static int GlassSkipForTest = 0;

        // 两片底图是不是**一模一样**（尺寸、偏移、像素）。
        // 用 LockBits 逐字节比，别用 GetPixel：658x658 就是 43 万次调用，那才是真的慢。
        // 逐字节全比，不做"抽样比几个点"——抽样会漏掉局部变化，而那正是要淡入的东西。
        // 这个函数 3.5 秒才跑一次，一次约 1ms，完全可以接受。
        static bool SameBackdrop(Bitmap a, Bitmap b, Point offA, Point offB)
        {
            if (a == null || b == null) return false;
            if (a.Width != b.Width || a.Height != b.Height) return false;
            if (offA.X != offB.X || offA.Y != offB.Y) return false;
            try
            {
                Rectangle r = new Rectangle(0, 0, a.Width, a.Height);
                BitmapData da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    BitmapData db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        int len = da.Stride * a.Height;
                        if (db.Stride * b.Height != len) return false;
                        byte[] ba = new byte[len], bb = new byte[len];
                        System.Runtime.InteropServices.Marshal.Copy(da.Scan0, ba, 0, len);
                        System.Runtime.InteropServices.Marshal.Copy(db.Scan0, bb, 0, len);
                        for (int i = 0; i < len; i++) if (ba[i] != bb[i]) return false;
                        return true;
                    }
                    finally { b.UnlockBits(db); }
                }
                finally { a.UnlockBits(da); }
            }
            catch { return false; }     // 比不了就当"变了"，保守：宁可白淡一次，也不要少淡一次
        }

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
            // ⚠️ **新底和旧底一模一样时，别做交叉淡入。**
            // 定时刷新每 3.5 秒抓一次屏，抓到的内容和手上这张**经常完全相同**
            // （你在看文档、看网页、桌面没动的时候）。而交叉淡入那 0.38 秒里，每一帧都要把
            // 两张**整窗**底图混一次（实测 3.78ms，是那一档最大的单项），再加上控件层缓存失效
            // 要整层重画 —— 全是为了把一张图淡入到它自己身上。
            // 实测：用户机器上 10 秒里的 67 帧几乎全是这种"白干"的帧，平均 17ms、最慢 29ms。
            //
            // 比的是**两片已经在内存里的位图**，不是"再抓一次屏幕看变没变"——
            // 后者早就量过、是反的（抓屏是按次计费的固定成本，采小块比整屏还贵，见 63 文件顶部的历史注释）。
            if (_backdropBlur != null && Visible && SameBackdrop(fresh, _backdropBlur, off, _backdropOffset))
            {
                // 内容一样：直接换掉、**不碰** _backdropOld/_backdropFade，
                // 于是这一轮没有任何过渡要播，一帧都不用重画。
                try { _backdropBlur.Dispose(); } catch { }
                _backdropBlur = fresh;
                _backdropOffset = off;
                _backdropValid = true;
                _backdropAt = DateTime.Now;
                _backdropGen++;
                BackdropChanged();
                _rendered = false;
                GlassSkipForTest++;
                return;
            }
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
            BackdropChanged();
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
                BackdropChanged();
            }
            catch { FreeBackdrop(); }
        }


        // 换上一张新玻璃底时统一收尾：代次 +1（让分层缓存的签名失效）、丢掉上一轮的整帧混图。
        // 混图必须丢：_backdropMixFrame 记的是"渲染帧号"，而帧号只在真的出图时才 +1，
        // 所以换底那一帧的帧号很可能只比上一轮最后一张混图大 1 —— 不丢就会把
        // **上一轮淡入结束时那张几乎全是新底的混图**当成第一帧贴出去，开头闪一下新底。
        void BackdropChanged()
        {
            _backdropGen++;
            _backdropMixFrame = -1;
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

        // ---- 换底交叉淡入：整帧只混一次 ----
        Bitmap _backdropMix;          // 旧底 + 新底按当前进度混好的一张图
        int _backdropMixFrame = -1;   // 是哪一帧混的（同一帧里多张卡片共用）

        // 把"旧底"和"新底"按 _backdropFade 混成一张：旧底在下、新底按进度压上去，
        // 跟原来逐卡片混出来的画面一致（原来就是先画旧、再按 fade 压新）。
        Bitmap BackdropMix()
        {
            if (_backdropOld == null || _backdropBlur == null) return null;
            // 一帧只混一次，同一帧里所有卡片共用（这才是"整帧只混一次"的原意）。
            // 以前这里写的是 _frameNo - _backdropMixFrame < 2（隔两帧才重建），但帧号每帧都 +1、
            // 淡入进度也是每帧都推进，于是差值恒为 1 —— 等于**永远**在用上一帧那张混图：
            // 淡入被量化成 2 帧一步（实测 0.38 秒只有 9 档，一步约 8%，看着一格一格地跳）。
            if (_backdropMix != null && _backdropMixFrame == _frameNo) return _backdropMix;
            try
            {
                if (_backdropMix == null || _backdropMix.Width != _backdropBlur.Width || _backdropMix.Height != _backdropBlur.Height)
                {
                    if (_backdropMix != null) { try { _backdropMix.Dispose(); } catch { } }
                    _backdropMix = new Bitmap(_backdropBlur.Width, _backdropBlur.Height, PixelFormat.Format32bppPArgb);
                }
                using (Graphics g = Graphics.FromImage(_backdropMix))
                {
                    // SourceCopy 直接铺旧底（等于清屏 + 画旧底，省一次全窗口填充）
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(_backdropOld, new Rectangle(0, 0, _backdropMix.Width, _backdropMix.Height));
                    g.CompositingMode = CompositingMode.SourceOver;
                    // 不要再加 Math.Max(0.06f, …) 那种下限：淡入第一帧 fade 就是 0，
                    // 强制按 6% 新底画 = 换底那一瞬间凭空跳 6%，正是"突兀"的来源之一。
                    float fade = _backdropFade < 0f ? 0f : (_backdropFade > 1f ? 1f : _backdropFade);
                    ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = fade;
                    _iaBack.SetColorMatrix(cm);
                    g.DrawImage(_backdropBlur, new Rectangle(0, 0, _backdropMix.Width, _backdropMix.Height),
                        0, 0, _backdropBlur.Width, _backdropBlur.Height, GraphicsUnit.Pixel, _iaBack);
                }
                _backdropMixFrame = _frameNo;
                return _backdropMix;
            }
            catch { return null; }
        }

        // 把一张"整窗口尺寸"的玻璃底裁进当前形状 —— 只取形状真正会用到的那一小块源图。
        // 为什么必须这么干：一张 822x822 的底，裁进 6 张卡片 + 十来个控件（万能键盘/圆按钮/药丸/把手），
        // 每次都让 GDI+ 把**整张图**过一遍采样，一帧就是十几倍全窗口的开销（实测卡片底 2.5ms x 6 张）。
        // 这里按形状的包围盒把源矩形缩到实际需要的几十像素见方，画出来完全一样、只是不再白算。
        // 只在交叉淡入分支用：稳态那一路的画法（乃至它的采样细节）一个字都不动，别去碰用户天天看的那张毛玻璃。
        void DrawGlassCrop(Graphics g, Bitmap bmp, GraphicsPath path, int al)
        {
            try
            {
                // 形状在**设备坐标**下的包围盒：当前变换里既有 UiK 缩放、也可能有控件层的位移
                RectangleF pb = path.GetBounds(g.Transform);
                if (pb.Width < 1f || pb.Height < 1f) return;
                // 往外放 3px：抗锯齿的边缘 + 后面换算的取整，不能露出一条没画到的缝
                float pad = 3f;
                int sx = (int)Math.Floor(pb.X - pad) - _backdropOffset.X;
                int sy = (int)Math.Floor(pb.Y - pad) - _backdropOffset.Y;
                int sw = (int)Math.Ceiling(pb.Width + pad * 2f + 2f);
                int sh = (int)Math.Ceiling(pb.Height + pad * 2f + 2f);
                if (sx < 0) { sw += sx; sx = 0; }
                if (sy < 0) { sh += sy; sy = 0; }
                if (sx + sw > bmp.Width) sw = bmp.Width - sx;
                if (sy + sh > bmp.Height) sh = bmp.Height - sy;
                if (sw < 1 || sh < 1) return;
                // 目标直接用**设备像素整数矩形**：临时把画布变换复位（裁剪区是按设备坐标记住的，
                // 复位不影响它），于是这是一次 1:1 贴图，既不缩放也不重采样 ——
                // 而且落点和稳态那条"整张图画进 dest"的路完全同一个像素位置，换路时不会跳。
                System.Drawing.Drawing2D.Matrix m = g.Transform;
                try
                {
                    g.ResetTransform();
                    Rectangle dd = new Rectangle(sx + _backdropOffset.X, sy + _backdropOffset.Y, sw, sh);
                    if (al >= 250) g.DrawImage(bmp, dd, sx, sy, sw, sh, GraphicsUnit.Pixel);   // 满 alpha 就别走 ColorMatrix（慢路径）
                    else
                    {
                        ColorMatrix cmo = new ColorMatrix(); cmo.Matrix33 = al / 255f;
                        _iaBack.SetColorMatrix(cmo);
                        g.DrawImage(bmp, dd, sx, sy, sw, sh, GraphicsUnit.Pixel, _iaBack);
                    }
                }
                finally { try { g.Transform = m; } catch { } }
            }
            catch { }
        }

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
                    // 换底那 0.38 秒里，以前是**每张卡片各混一遍**（旧底 + 新底两张全窗口图），
                    // 8 张卡片就是 16 次全窗口绘制 —— 实测这一档每帧 20ms 就是这么来的。
                    // 现在整帧只混一次（BackdropMix），每张卡片只画一张图。
                    Bitmap mix = BackdropMix();
                    if (mix != null) DrawGlassCrop(g, mix, path, al);
                }
                else
                {
                    if (al >= 250) g.DrawImage(_backdropBlur, dest);
                    else
                    {
                        ColorMatrix cm = new ColorMatrix();
                        cm.Matrix33 = al / 255f;
                        _iaBack.SetColorMatrix(cm);
                        g.DrawImage(_backdropBlur,
                            new Rectangle((int)Math.Round(dest.X), (int)Math.Round(dest.Y),
                                          Math.Max(1, (int)Math.Round(dest.Width)), Math.Max(1, (int)Math.Round(dest.Height))),
                            0, 0, bw, bh, GraphicsUnit.Pixel, _iaBack);
                    }
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
