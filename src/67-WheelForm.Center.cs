using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnapWheel
{
    // 无万能键版（0.2.x）的中间按钮组 —— v0.5.2 起
    //
    // 0.2 线是**另一条路线**：没有万能键那张"多 Wheel 摇杆盘"，中间也就不该塞一套
    // 新建/换盘/删除的十字（那是完整版的语义）。这条线只要三件事：**截图 / 设置 / 关闭**。
    // 多 Wheel 仍然能用（名字药丸 + 托盘「管理 Wheel…」），但不占显眼位置。
    //
    // 默认尺寸也相应小一号（缩略图 80 / 半径 250，见 Settings 里的 #if NO_KEY）。
    partial class WheelForm
    {
#if NO_KEY
        int _noKeyDown = -1;        // 正被按下的那个按钮（-1 = 没有）
        int _noKeyHover = -1;
        int _noKeyFrame = -1;
        readonly Rectangle[] _noKeyRectCache = new Rectangle[3];

        const float NoKeyBtnSize = 54f;      // 逻辑像素
        const float NoKeyBtnGap = 12f;

        // 中间那块空地方的中心：跟原来摇杆盘同一个位置（环的内侧中点）
        PointF NoKeyCenter()
        {
            float mid = (_phiMin + _phiMax) / 2f;
            float kr = EffR() - _thumb * 1.25f;
            if (kr < 60f) kr = 60f;
            return ItemCenterAtPhiRadius(mid, kr);
        }

        // 三个按钮一排：截图 / 设置 / 关闭。绘制和命中共用这一份坐标
        //（"动画和位置对不上"这类问题，多半就是画一套、点另一套造成的）
        Rectangle[] CenterRects()
        {
            if (_noKeyFrame == _frameNo) return _noKeyRectCache;
            PointF c = NoKeyCenter();
            float s = NoKeyBtnSize, g = NoKeyBtnGap;
            float total = s * 3 + g * 2;
            float x0 = c.X - total / 2f;
            float y0 = c.Y - s / 2f;
            for (int i = 0; i < 3; i++)
                _noKeyRectCache[i] = new Rectangle((int)Math.Round(x0 + i * (s + g)), (int)Math.Round(y0), (int)s, (int)s);
            _noKeyFrame = _frameNo;
            return _noKeyRectCache;
        }

        int CenterHit(Point p)
        {
            Rectangle[] rs = CenterRects();
            for (int i = 0; i < rs.Length; i++) if (rs[i].Contains(p)) return i;
            return -1;
        }

        void DoCenterAction(int i)
        {
            if (i == 0) { if (CaptureRequested != null) CaptureRequested(this, EventArgs.Empty); }
            else if (i == 1) { if (SettingsRequested != null) SettingsRequested(this, EventArgs.Empty); }
            else DismissWheel();                      // 收起（收起态没开就是隐藏）
        }

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
            if (CenterHit(p) == i) DoCenterAction(i);   // 松手还在同一个按钮上才算点中了
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
            float pk = IntroP(0.16f);                        // 和原摇杆同一档出场动画
            if (pk < 0.01f) return;
            PointF sh = IntroShift(pk);
            int aa = (int)(a * pk);
            Color acc = _accentCur;
            Matrix m = g.Transform;
            g.TranslateTransform(sh.X, sh.Y);
            string[] txt = new string[] { "截图", "设置", "关闭" };
            for (int i = 0; i < rs.Length; i++)
            {
                bool pressed = (_noKeyDown == i);
                bool hov = (_noKeyHover == i) || pressed;
                Rectangle r = rs[i];
                if (pressed) r = new Rectangle(r.X, r.Y + (int)(2 * UiK), r.Width, r.Height);   // 按下：沉下去
                // 截图是这条线最主要的动作，给它主色；设置/关闭用玻璃底
                bool primary = (i == 0);
                Color surface = primary ? Gfx.A(acc, (int)((hov ? 250 : 214) * aa / 255f))
                                        : Gfx.A(GlassBase(), GlassA((int)((hov ? 250 : 172) * aa / 255f)));
                Gfx.NeuCircle(g, r, surface, Gfx.A(acc, (int)((hov ? 255 : 190) * aa / 255f)), primary, pressed,
                    (int)((StyleNeu() ? 70 : 24) * aa / 255f), (int)((StyleNeu() ? 70 : 0) * aa / 255f));
                using (Font f = new Font("Microsoft YaHei UI", (hov ? 13f : 12.5f), FontStyle.Bold))
                using (SolidBrush tb = new SolidBrush(Color.FromArgb((int)((hov ? 255 : 236) * aa / 255f), 255, 255, 255)))
                {
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(txt[i], f, tb, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                }
            }
            g.Transform = m;
        }
#else
        // 完整版：中间是万能键摇杆盘，这里是空实现
        internal int CenterHit(Point p) { return -1; }
        void DrawCenterButtons(Graphics g, int a) { }
        bool CenterMouseDown(Point p) { return false; }
        bool CenterMouseUp(Point p) { return false; }
        bool CenterMouseMove(Point p) { return false; }
#endif
    }
}