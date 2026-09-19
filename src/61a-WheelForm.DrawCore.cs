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
    // 轮盘绘制：渲染管线与缓存（分层贴图、缩略图缓存、帧状态）（从 61-WheelForm.Draw.cs 拆出来，纯搬移，行为不变）。
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

        // animating：这张卡片当前的缩放**不是静止值**（放大预览 / 悬停 / 删除 / 拖动 / 收起动画中）。
        // 为什么要传这个：下面两处"救急"逻辑原来都挂在「目标尺寸是否超过原图」上，
        // 而那个条件**漏掉了最常见的一种情况** —— 大图放大后仍然小于原图宽
        // （2560x1440 的图放大到 346x194 并没有超过原图）。于是对最容易卡的大图，
        // 两条救急全部不生效。见下面中转图那段。
        Bitmap ScaledThumb(StoreItem it, int w, int h, bool animating)
        {
            // w/h 是逻辑尺寸；实际按物理像素生成，缩放到高 DPI 屏上才不会发虚。
            // 注意：尺寸**不能量化**。量化会让"1:1 贴图"变成重采样贴图，实测反而更慢
            // （缩略图那一段 2.98 → 3.72ms），所以这里保持精确尺寸。
            int dw = Math.Max(1, (int)Math.Round(w * UiK));
            int dh = Math.Max(1, (int)Math.Round(h * UiK));
            Dictionary<long, Bitmap> d;
            if (!_thumbCache.TryGetValue(it, out d)) { d = new Dictionary<long, Bitmap>(); _thumbCache[it] = d; }
            // 尺寸量化只在**尺寸正在动**的时候做：
            //   · 静止时（animating=false）必须保持精确尺寸 —— 1:1 贴图靠它，量化会把它变成重采样；
            //   · 动画中每帧尺寸都不同，不量化就帧帧未命中、缓存很快被撑满清空，
            //     又退回到每帧从原图做高质量双三次（用户反馈：大图的放大动画缓慢且有抖动）。
            if (animating || dw > it.Image.Width || dh > it.Image.Height)
            {
                dw = Math.Max(1, (dw + 7) / 8 * 8);
                dh = Math.Max(1, (dh + 7) / 8 * 8);
            }
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

            if (d.Count > 200)
            {
                // 只清小的、留住面积最大的那张：清空的话下一帧又得从原图重做一次高质量缩放，
                // 大图上那一下就是几十毫秒（放大动画卡顿的主要来源）。
                long keepKey = 0, keepArea = 0;
                foreach (System.Collections.Generic.KeyValuePair<long, Bitmap> kv in d)
                {
                    long a2 = (long)kv.Value.Width * kv.Value.Height;
                    if (a2 > keepArea) { keepArea = a2; keepKey = kv.Key; }
                }
                System.Collections.Generic.List<long> dead = new System.Collections.Generic.List<long>();
                foreach (System.Collections.Generic.KeyValuePair<long, Bitmap> kv in d)
                    if (kv.Key != keepKey) dead.Add(kv.Key);
                for (int q = 0; q < dead.Count; q++) { try { d[dead[q]].Dispose(); } catch { } d.Remove(dead[q]); }
                src = it.Image;
                if (d.Count > 0) foreach (System.Collections.Generic.KeyValuePair<long, Bitmap> kv in d) { src = kv.Value; break; }
            }
            // 放大的时候（目标比原图还大）如果没找到"够大"的现成图，就退而用缓存里**最大的那张**当源。
            // 从已有的小图放大，比从几千像素的原图缩下来快一个数量级 —— 而画质几乎看不出差别
            // （都是小尺寸重采样）。"大图的缩略图动画第一次播会卡"就是这个原因：
            // 首次没有大尺寸缓存，于是每一帧都从原图重做一次高质量双三次。
            if (src == it.Image && (dw > it.Image.Width || dh > it.Image.Height))
            {
                long bigA = 0;
                foreach (KeyValuePair<long, Bitmap> kv in d)
                {
                    long a3 = (long)kv.Value.Width * kv.Value.Height;
                    if (a3 > bigA) { bigA = a3; src = kv.Value; }
                }
            }
            // 上面那条「退而用缓存里最大的那张」为什么救不了大图：
            // 它只在**目标比原图还大**时才生效，而 2560x1440 的图放大到 346x194 并没有超过原图宽。
            // 而且就算强行用它（拿 144x81 的小缩略图放大 2.4 倍），画质会明显发虚 ——
            // 那是拿"不卡"换"更糊"，不划算。
            //
            // 所以这里补的是那一段真正缺的东西：**动画的头几帧，先从原图做一张"最终倍率"的中转图**
            // （稳定 key，整段动画只生成一次），后面的帧全部从它往下缩 —— 又快又清楚。
            // 大图上"第一次长按放大掉帧"就是这么来的：首次没有大尺寸缓存，于是每一帧
            // 都从几千像素的原图重做一次高质量双三次。
            if (animating && src == it.Image)
            {
                SizeF bs = CardSize(it);
                int mw = (int)Math.Round(bs.Width * PeekScale * UiK);
                int mh = (int)Math.Round(bs.Height * PeekScale * UiK);
                if (mw < dw) mw = dw;
                if (mh < dh) mh = dh;
                mw = Math.Max(8, (mw + 7) / 8 * 8);
                mh = Math.Max(8, (mh + 7) / 8 * 8);
                long mkey = ((long)mw << 20) | (uint)mh;
                Bitmap mid;
                if (d.TryGetValue(mkey, out mid))
                {
                    if (mid.Width >= dw && mid.Height >= dh) src = mid;
                }
                else
                {
                    mid = new Bitmap(mw, mh, PixelFormat.Format32bppPArgb);
                    using (Graphics gm = Graphics.FromImage(mid))
                    {
                        gm.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        gm.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        gm.DrawImage(it.Image, new Rectangle(0, 0, mw, mh));
                    }
                    d[mkey] = mid;
                    src = mid;
                }
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

        // public：托盘的「诊断模式」开关要立刻重画一帧，否则要等下一次动画 tick 才看到效果
        public void Render()
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

    }
}
