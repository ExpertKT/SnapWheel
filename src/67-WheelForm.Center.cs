using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnapWheel
{
    // 无万能键版（0.2.x）的中间按钮组 —— v0.5.2 起
    //
    // 背景：0.2 线没有"万能键"那个摇杆圆盘，环的内侧就空着一大块，看着像少了什么。
    // 用户的要求是"把没万能键的地方用起来，按键排布更合理"，同时**默认轮盘整体缩小一点**
    // （没圆盘就不需要那么大的内圈了）。这里把原来摇杆的四个动作做成 2×2 的圆按钮：
    // 上=新建 / 右=下一个 / 下=删除 / 左=上一个，动作仍然跟着设置里的"万能键四分区动作"走，
    // 所以设置界面不用改，语义也一致。
    //
    // 位置算法只有一份（CenterRects），绘制和命中都用它 —— 之前"动画和位置对不上"这类问题
    // 多半是画一套、点另一套造成的。
    partial class WheelForm
    {
        int _noKeyDown = -1;        // 正被按下的那个按钮（-1 = 没有）
        int _noKeyHover = -1;
#if NO_KEY
        int _noKeyFrame = -1;                 // 这一组按钮的坐标是按帧缓存的（画和点都用同一份）
        int _noKeyRectCount = 0;
        readonly Rectangle[] _noKeyRectCache = new Rectangle[4];
#endif

        const float NoKeyBtnSize = 52f;      // 逻辑像素
        const float NoKeyBtnGap = 10f;

        // 中间那块"空地方"的中心：跟原摇杆盘同一个位置（环的内侧中点）
        PointF NoKeyCenter()
        {
            float mid = (_phiMin + _phiMax) / 2f;
            float kr = EffR() - _thumb * 1.25f;
            if (kr < 60f) kr = 60f;
            return ItemCenterAtPhiRadius(mid, kr);
        }

        // 四个按钮的位置（2×2）。顺序 = 万能键的分区顺序：上(0) 右(1) 下(2) 左(3)
        Rectangle[] CenterRects()
        {
#if NO_KEY
            if (_noKeyFrame == _frameNo && _noKeyRectCount == 4) return _noKeyRectCache;
            PointF c = NoKeyCenter();
            float s = NoKeyBtnSize, g = NoKeyBtnGap;
            float x0 = c.X - s - g / 2f;
            float y0 = c.Y - s - g / 2f;
            _noKeyRectCache[0] = new Rectangle((int)Math.Round(x0 + s + g), (int)Math.Round(y0), (int)s, (int)s);            // 上
            _noKeyRectCache[1] = new Rectangle((int)Math.Round(x0 + s + g + s + g), (int)Math.Round(y0 + s + g), (int)s, (int)s); // 右
            _noKeyRectCache[2] = new Rectangle((int)Math.Round(x0 + s + g), (int)Math.Round(y0 + s + g + s + g), (int)s, (int)s); // 下
            _noKeyRectCache[3] = new Rectangle((int)Math.Round(x0), (int)Math.Round(y0 + s + g), (int)s, (int)s);            // 左
            _noKeyFrame = _frameNo;
            _noKeyRectCount = 4;
            return _noKeyRectCache;
#else
            return null;      // 完整版没有这组按钮
#endif
        }

        int CenterHit(Point p)
        {
            Rectangle[] rs = CenterRects();
            if (rs == null) return -1;
            for (int i = 0; i < rs.Length; i++) if (rs[i].Contains(p)) return i;
            return -1;
        }

        // 鼠标按下/松开。返回 true = 这次事件被按钮组吃掉了
        bool CenterMouseDown(Point p)
        {
            int i = CenterHit(p);
            if (i < 0) return false;
            _noKeyDown = i;
            _rendered = false;
            Render();
            return true;
        }

        bool CenterMouseUp(Point p)
        {
            if (_noKeyDown < 0) return false;
            int i = _noKeyDown;
            _noKeyDown = -1;
            _rendered = false;
            if (CenterHit(p) == i) DoKeyAction(i);     // 松手时还在同一个按钮上才算点击
            Render();
            return true;
        }

        bool CenterMouseMove(Point p)
        {
            int i = CenterHit(p);
            if (i == _noKeyHover) return false;
            _noKeyHover = i;
            _rendered = false;
            Render();
            return i >= 0;
        }

        void DrawCenterButtons(Graphics g, int a)
        {
            Rectangle[] rs = CenterRects();
            if (rs == null) return;
            float pk = IntroP(0.16f);                        // 和原摇杆同一档出场动画
            if (pk < 0.01f) return;
            PointF sh = IntroShift(pk);
            int aa = (int)(a * pk);
            Color acc = _accentCur;
            System.Drawing.Drawing2D.Matrix m = g.Transform;
            g.TranslateTransform(sh.X, sh.Y);
            for (int i = 0; i < rs.Length; i++)
            {
                bool pressed = (_noKeyDown == i);
                bool hov = (_noKeyHover == i) || pressed;
                Rectangle r = rs[i];
                if (pressed) r = new Rectangle(r.X, r.Y + (int)(2 * UiK), r.Width, r.Height);   // 按下：沉下去
                Color surface = Gfx.A(GlassBase(), GlassA((int)((hov ? 250 : 172) * aa / 255f)));
                Gfx.NeuCircle(g, r, surface, Gfx.A(acc, (int)((hov ? 255 : 190) * aa / 255f)), false, pressed,
                    (int)((StyleNeu() ? 70 : 24) * aa / 255f), (int)((StyleNeu() ? 70 : 0) * aa / 255f));
                string txt = KeyActionShort(_settings.KeyActionAt(i));
                if (!string.IsNullOrEmpty(txt))
                {
                    using (Font f = new Font("Microsoft YaHei UI", (hov ? 12.5f : 12f), FontStyle.Bold))
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb((int)((hov ? 255 : 232) * aa / 255f), 255, 255, 255)))
                    {
                        StringFormat sf = new StringFormat();
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(txt, f, tb, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                    }
                }
            }
            g.Transform = m;
        }
    }
}
