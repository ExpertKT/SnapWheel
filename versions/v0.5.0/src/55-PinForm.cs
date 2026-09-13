using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnapWheel
{
    // 贴图（图钉）：把一张图钉在屏幕上，随时对照着看 —— Snipaste 的招牌能力，也是"截图之后
    // 拿来用"这条主线上最自然的一步：不用再拖来拖去，看的时候它就在那儿。
    // 入口：轮盘上中键单击一张缩略图（左键=拖出去、右键=删除、双击=复制，中键是空的）。
    // 交互：左键拖 = 移动；滚轮 = 缩放（以光标为锚点）；双击 / Esc / 右上角 × = 关掉。
    class PinForm : Form
    {
        public const float MinZoom = 0.10f;
        public const float MaxZoom = 4.00f;
        const int Pad = 1;                       // 1px 描边，浅色背景下也能看清边界

        readonly Bitmap _img;
        float _zoom = 1f;
        bool _drag = false;
        Point _dragFrom;
        Point _formFrom;
        bool _closeHover = false;

        public float Zoom { get { return _zoom; } }
        public Bitmap Image { get { return _img; } }

        public PinForm(Bitmap img, Point at)
        {
            // 自己留一份：图钉要活得比轮盘上那张缩略图久（轮盘里删掉了它也该还钉着）
            try { _img = new Bitmap(img); } catch { _img = img; }

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            // 必须关掉自动缩放：贴图要的是"1 个像素就是 1 个像素"，
            // 默认的 Font 缩放会按字体/DPI 把窗口尺寸改掉（测出来 122 变 136）
            AutoScaleMode = AutoScaleMode.None;
            // 系统的"最小窗口宽度"是 136px（SM_CXMINTRACK），不显式设 MinimumSize 的话，
            // 小图会被撑到 136 宽、右边多出一条白边 —— 40x30 这种小截图就废了
            MinimumSize = new Size(16, 16);
            Text = "SnapWheel 贴图";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            ApplyZoom(1f, false);
            Location = ClampToScreen(new Point(at.X - Width / 2, at.Y - Height / 2));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { Activate(); } catch { }        // 要能接住 Esc
        }

        public void SetZoom(float z) { ApplyZoom(z, true); }

        // 缩放：以窗口中心为准（滚轮那条走 OnMouseWheel 的光标锚点版本）
        void ApplyZoom(float z, bool keepCenter)
        {
            if (z < MinZoom) z = MinZoom;
            if (z > MaxZoom) z = MaxZoom;
            Point c = new Point(Left + Width / 2, Top + Height / 2);
            _zoom = z;
            int w = Math.Max(12, (int)Math.Round(_img.Width * _zoom) + Pad * 2);
            int h = Math.Max(12, (int)Math.Round(_img.Height * _zoom) + Pad * 2);
            ClientSize = new Size(w, h);
            if (keepCenter) Location = ClampToScreen(new Point(c.X - Width / 2, c.Y - Height / 2));
            Invalidate();
        }

        // 别让它跑到屏幕外面去（两块屏 / 拔掉显示器之后重新摆位都靠这个兜底）
        Point ClampToScreen(Point p)
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            int x = Math.Max(vs.Left, Math.Min(p.X, vs.Right - Width));
            int y = Math.Max(vs.Top, Math.Min(p.Y, vs.Bottom - Height));
            return new Point(x, y);
        }

        Rectangle CloseRect()
        {
            int s = 22;
            return new Rectangle(Width - s - 4, 4, s, s);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.White);               // 带透明的图也有个白底，不至于透出桌面内容
            // 按图片自己的缩放尺寸画，别按窗口大小拉伸 —— 窗口万一被系统撑大，图也不该变形
            int iw = Math.Max(1, (int)Math.Round(_img.Width * _zoom));
            int ih = Math.Max(1, (int)Math.Round(_img.Height * _zoom));
            g.DrawImage(_img, new Rectangle(Pad, Pad, iw, ih));
            using (Pen bp = new Pen(Color.FromArgb(190, 70, 74, 84), 1f))
                g.DrawRectangle(bp, 0, 0, Width - 1, Height - 1);

            if (_closeHover && Width > 40 && Height > 40)
            {
                Rectangle r = CloseRect();
                using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 38, 42, 50)))
                    g.FillEllipse(b, r);
                using (Pen p = new Pen(Color.White, 1.8f))
                {
                    int m = 7;
                    g.DrawLine(p, r.Left + m, r.Top + m, r.Right - m, r.Bottom - m);
                    g.DrawLine(p, r.Right - m, r.Top + m, r.Left + m, r.Bottom - m);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_closeHover && CloseRect().Contains(e.Location)) { Close(); return; }
            _drag = true;
            _dragFrom = PointToScreen(e.Location);
            _formFrom = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = Width > 40 && Height > 40 && CloseRect().Contains(e.Location);
            if (h != _closeHover) { _closeHover = h; Invalidate(); }
            try { Cursor = h ? Cursors.Hand : (_drag ? Cursors.SizeAll : Cursors.Default); } catch { }
            if (!_drag) return;
            Point now = PointToScreen(e.Location);
            Location = new Point(_formFrom.X + (now.X - _dragFrom.X), _formFrom.Y + (now.Y - _dragFrom.Y));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = false;
            try { Cursor = Cursors.Default; } catch { }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
            // 以光标为锚点：光标底下那块内容原地不动，缩放才不会"跑"
            Point anchorScreen = PointToScreen(e.Location);
            float relX = (e.Location.X - Pad) / Math.Max(0.01f, _img.Width * _zoom);
            float relY = (e.Location.Y - Pad) / Math.Max(0.01f, _img.Height * _zoom);
            ApplyZoom(_zoom * factor, false);
            Location = ClampToScreen(new Point(
                (int)Math.Round(anchorScreen.X - Pad - relX * _img.Width * _zoom),
                (int)Math.Round(anchorScreen.Y - Pad - relY * _img.Height * _zoom)));
        }

        protected override void OnDoubleClick(EventArgs e) { Close(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _img != null) { try { _img.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
