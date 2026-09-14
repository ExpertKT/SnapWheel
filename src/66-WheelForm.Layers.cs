using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SnapWheel
{
    // 分层缓存（v0.5.2 性能优化）
    //
    // 为什么要它：实测一帧 12.7ms 里，控件层（关闭键/设置键/万能键/名字/把手）占 5.45ms、
    // 环占 0.48ms，推送分层窗口只占 0.38ms —— 也就是说慢在"一笔一笔重画矢量图形"上，
    // 不是慢在推屏。而这些矢量图形在**稳态**下每一帧画出来是一模一样的（悬停/按下/动画
    // 之外没有变化），所以最划算的做法是：画一次存成位图，之后每帧只贴一张图。
    //
    // 正确的关键在"什么时候能用缓存"：
    //   1) 只在稳态用（没有入场/收起/展开动画、alpha 全亮、没有控件在悬停或按下、没提示条）；
    //   2) 用一份**签名**把这一层依赖的所有状态都算进去（几何/风格/强调色/控件状态…），
    //      签名一变就重画。签名是 64 位哈希，不是字符串，每帧算一次几乎不要钱。
    //   两个都做到，视觉上就跟"每帧重画"完全一致（贴图是 1:1 设备像素，不做缩放）。
    partial class WheelForm
    {
        Bitmap _layerBack;         // 环（画在图片下面）
        Bitmap _layerFront;        // 控件层（画在图片上面）
        long _layerBackSig, _layerFrontSig;

        // 签名用的混合函数（FNV 风格；64 位下碰撞可以忽略）
        static long Mix(long h, long v) { unchecked { return (h ^ v) * 1099511628211L; } }
        static long Mix(long h, double v) { return Mix(h, BitConverter.DoubleToInt64Bits(Math.Round(v, 2))); }
        static long Mix(long h, bool v) { return Mix(h, v ? 1L : 0L); }
        static long Mix(long h, string s)
        {
            long x = 1469598103934665603L;
            if (s != null) for (int i = 0; i < s.Length; i++) x = unchecked((x ^ s[i]) * 1099511628211L);
            return Mix(h, x);
        }
        static long Sig0() { return 1469598103934665603L; }

        // 这一层现在能不能用缓存（"稳态"判定，宁可少用也别画错）
        bool LayerAllowed(int which)
        {
            if (_store == null || _settings == null) return false;
            if (_show < 0.999f || _intro || _collapsing || _showAnimating) return false;
            if (_deletingItem != null || _dragOutItem != null || _dropActive) return false;
            if (_switchFlash > 0.01f) return false;
            // 玻璃底正在交叉淡入（换底后的那 0.38 秒）时，控件层绝不能用缓存：
            // 万能键玻璃盘 / 两个圆按钮 / 名字药丸 / 把手 的模糊底是**烤进这一层位图**里的，
            // 贴缓存 = 这几处玻璃整整 0.38 秒一动不动，等下一次签名变化（用户一悬停）再整块跳过去
            // —— 实测 22978 个"受换底影响的像素"里有 18342 个（80%）就没跟着淡，一戳签名直接跳到 0.91。
            // 宁可这 0.38 秒每帧老实重画（这一档的耗时本来就在 perf 基准里单独计），也不要那种"啪"的一跳。
            if (which == 1 && _backdropOld != null && _backdropFade < 0.999f) return false;
            if (which == 0) return true;
            // 控件层：任何"动着的 / 按下的 / 弹着的"状态都不用缓存
            if (_toast.Length > 0 || _delConfirm || _menuOpen || _menuT > 0.001f) return false;
            if (_closeDown > 0.01f || _closeHover || _closePend || _closeHoldP > 0.001f || _closeLong) return false;
            if (_gearDown > 0.01f || _gearHover || _gearPend) return false;
            if (_shootDown > 0.01f || _shootHover || _shootPend) return false;
            if (_keyDown || _keyHover) return false;
            if (_nameHover) return false;
            if (_nubAppearT < 0.999f || _nubHov > 0.01f || _nubOutHover || _nubInHover) return false;
            return true;
        }

        // 这一层依赖的全部状态
        long LayerSig(int which)
        {
            long h = Sig0();
            h = Mix(h, which);
            h = Mix(h, Width); h = Mix(h, Height); h = Mix(h, (double)UiK);
            h = Mix(h, _collapsed);
            h = Mix(h, _accentCur.ToArgb());
            h = Mix(h, _settings.UiStyle);
            h = Mix(h, _settings.GlassPercent); h = Mix(h, _settings.ShadowPercent);
            h = Mix(h, _settings.UiScale); h = Mix(h, _settings.NubSingle);
            h = Mix(h, _settings.ShowNameLabel); h = Mix(h, _settings.ShowCountLabel);
            h = Mix(h, _settings.LabelSize); h = Mix(h, _settings.KeyActions);
            // 几何相关的也进签名：换了贴边角落、但窗口尺寸恰好没变时，环的位置是会变的，
            // 漏掉这一项就会贴出"上一个角落"的旧层 —— 这种 bug 光看窗口尺寸发现不了。
            h = Mix(h, _settings.Corner); h = Mix(h, _settings.Radius);
            h = Mix(h, _settings.ThumbSize); h = Mix(h, _settings.CardRadius);
            h = Mix(h, Left); h = Mix(h, Top);
            // 玻璃底的"第几代"也要进签名：换了一张完全不同的桌面之后，这一层里烤的旧底
            // 就没用了 —— 只靠"淡入期间禁缓存"能保证过程对，但淡入一结束签名又会重新匹配上，
            // 于是把**旧底那一版**整块贴回来（等于换底白换了）。代次一变，层必须重画。
            h = Mix(h, _backdropGen);
            // 悬停/按下的**进度值**也必须进签名：按钮的反馈是"渐变"出来的（_keyHov/_closeDown…），
            // 只看那几个 bool 的话，过渡期间会一直贴旧层 —— 表现就是"鼠标放上去没反应"。
            h = Mix(h, (double)_keyHov); h = Mix(h, (double)_keyT);
            h = Mix(h, (double)_closeDown); h = Mix(h, (double)_gearDown); h = Mix(h, (double)_shootDown);
            h = Mix(h, (double)_closeHoldP); h = Mix(h, (double)_nubHov);
            h = Mix(h, (double)_nubAppearT); h = Mix(h, (double)_nubHintT);
            h = Mix(h, (double)_nubOutDist); h = Mix(h, (double)_nubInDist);
            if (which == 0)
            {
                h = Mix(h, (double)EffR());
                h = Mix(h, _store.Items.Count);
                return h;
            }
            h = Mix(h, _store.Items.Count);                       // 计数胶囊
            h = Mix(h, _mgr.ActiveWheel.Name);                    // 名字药丸
            for (int i = 0; i < 4; i++) h = Mix(h, KeyActionShort(_settings.KeyActionAt(i)));   // 万能键四个分区上的字
            return h;
        }

        // 命中就直接贴（1:1 设备像素：临时把画布变换复位再贴，保证不缩放、不采样）
        bool TryBlitLayer(Graphics g, int which)
        {
            Bitmap bmp = (which == 0) ? _layerBack : _layerFront;
            if (bmp == null || bmp.Width != Width || bmp.Height != Height) return false;
            if (!LayerAllowed(which)) return false;
            if ((which == 0 ? _layerBackSig : _layerFrontSig) != LayerSig(which)) return false;
            BlitLayer(g, bmp);
            return true;
        }

        void BlitLayer(Graphics g, Bitmap bmp)
        {
            System.Drawing.Drawing2D.Matrix m = g.Transform;
            try
            {
                g.ResetTransform();
                g.DrawImageUnscaled(bmp, 0, 0);
            }
            catch { }
            finally { try { g.Transform = m; } catch { } }
        }

        // 把这一层画进缓存（用同一份绘制代码，所以画出来跟原来一模一样），并顺手贴到当前帧
        void StoreLayer(int which, Graphics g, int a, bool force)
        {
            try
            {
                if (!force && !LayerAllowed(which)) return;
                Bitmap bmp = (which == 0) ? _layerBack : _layerFront;
                if (bmp == null || bmp.Width != Width || bmp.Height != Height)
                {
                    if (bmp != null) { try { bmp.Dispose(); } catch { } }
                    bmp = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                    if (which == 0) _layerBack = bmp; else _layerFront = bmp;
                }
                using (Graphics lg = Graphics.FromImage(bmp))
                {
                    lg.CompositingMode = CompositingMode.SourceCopy;
                    lg.Clear(Color.Transparent);
                    lg.CompositingMode = CompositingMode.SourceOver;
                    lg.SmoothingMode = SmoothingMode.AntiAlias;
                    lg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    lg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    lg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    if (Math.Abs(UiK - 1f) > 0.001f) lg.ScaleTransform(UiK, UiK);
                    if (which == 0) DrawRing(lg, a); else DrawControls(lg, a);
                }
                if (which == 0) _layerBackSig = LayerSig(0); else _layerFrontSig = LayerSig(1);
                // 注意：**不要再贴一次**。调用方在 store 之前已经直接画过一遍了，
                // 再贴一层等于半透明元素叠两遍 —— 用户看到的就是"一放万能键所有 UI 一起闪"。
                // 缓存从下一帧开始生效，那一帧省下的时间才是我们要的。
            }
            catch { /* 缓存失败就当没缓存：外面已经照常画过了 */ }
        }

        // 尺寸/风格变了、或者窗口关掉时把缓存扔掉
        void DropLayers()
        {
            if (_layerBack != null) { try { _layerBack.Dispose(); } catch { } _layerBack = null; }
            if (_layerFront != null) { try { _layerFront.Dispose(); } catch { } _layerFront = null; }
            _layerBackSig = 0; _layerFrontSig = 0;
            foreach (Bitmap b in _plates.Values) { try { b.Dispose(); } catch { } }
            _plates.Clear();
        }

        // ---- 卡片的小贴片（阴影 / 面板底 / 描边）----
        // 每张卡片每帧要重画：柔和阴影（三层宽描边模拟模糊）、圆角玻璃底（渐变 + 两条内描边）、描边。
        // 实测这块每张卡片约 1.2ms，8 张就接近 10ms；但"尺寸和状态不变"时每帧画出来一模一样，
        // 所以按 (尺寸, 圆角, 样式, 状态) 缓存成小位图，每帧只贴一次。
        // alpha（淡入淡出 / 收起 / 拖动）在贴的时候用 ColorMatrix 乘上去，跟逐元素乘 alpha 等价。
        readonly Dictionary<string, Bitmap> _plates = new Dictionary<string, Bitmap>();

        // 把区域对齐到 24px 网格（往外扩）：放大预览时卡片尺寸每帧变一点点，
        // 量化之后同一个网格里的尺寸共用一张贴片 —— 贴片画布略大一点，内容是精确坐标画的，
        // 贴回去还是 1:1，不引入任何缩放。
        static RectangleF Quantize(RectangleF r)
        {
            const float grid = 24f;
            float x = (float)(Math.Floor(r.X / grid) * grid);
            float y = (float)(Math.Floor(r.Y / grid) * grid);
            float rr = (float)(Math.Ceiling(r.Right / grid) * grid);
            float bb = (float)(Math.Ceiling(r.Bottom / grid) * grid);
            return new RectangleF(x, y, rr - x, bb - y);
        }

        // 门槛版：尺寸不稳定的卡片（动画中）直接返回 null，让调用方走"直接画"
        Bitmap PlateIf(bool on, string key, RectangleF area, Action<Graphics> draw)
        {
            return on ? Plate(key, area, draw) : null;
        }

        // 同一帧最多新建这么多张贴片：滚动时同时冒出来好几张新卡片的话，
        // 一帧里连做五六张贴片会把这一帧顶到 20ms 以上（尖峰就是这么来的）。
        // 超出的那些这一帧走"直接画"（画出来一样，只是没那么便宜），下一帧再建。
        const int MaxPlateGenPerFrame = 2;
        int _plateGenFrame = -1, _plateGenCount = 0;

        Bitmap Plate(string key, RectangleF area, Action<Graphics> draw)
        {
            Bitmap b;
            if (_plates.TryGetValue(key, out b)) return b;
            if (area.Width < 1f || area.Height < 1f) return null;
            if (_plateGenFrame != _frameNo) { _plateGenFrame = _frameNo; _plateGenCount = 0; }
            if (_plateGenCount >= MaxPlateGenPerFrame) return null;
            _plateGenCount++;
            if (_plates.Count > 120)
            {
                foreach (Bitmap v in _plates.Values) { try { v.Dispose(); } catch { } }
                _plates.Clear();
            }
            try
            {
                int pw = Math.Max(1, (int)Math.Ceiling(area.Width * UiK));
                int ph = Math.Max(1, (int)Math.Ceiling(area.Height * UiK));
                b = new Bitmap(pw, ph, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    if (Math.Abs(UiK - 1f) > 0.001f) g.ScaleTransform(UiK, UiK);
                    g.TranslateTransform(-area.X, -area.Y);      // 贴片内部照旧用轮盘的绝对坐标
                    draw(g);
                }
                _plates[key] = b;
                return b;
            }
            catch { return null; }
        }
    }
}
