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
    //
    // ⚠️ DPI（0.5.3 修订）：这个窗口里**一个字都没有**（FormBorderStyle.None，标题栏根本不画；也没有 Label），
    // 所以不存在"字体按 DPI 放大、写死的格子没跟着放大、长句被裁掉"那类问题 —— 这里没有会裁字的固定像素。
    // 会跟着 DPI 走的只有下面这几处**界面元素**，所以它们过一遍 Ui.S()：
    //   · 右上角那个 × 按钮：大小 / 边距 / × 两笔的留白与粗细；
    //   · "窗口够不够大才画这个按钮"的门槛 —— 它必须和按钮同单位一起乘 K，
    //     不然 150% 下 33px 的按钮会盖满一张 45px 的小贴图。
    // **有意不乘 K 的**（都是"内容像素"，乘了贴图本身就画错了）：
    //   · 图片显示尺寸 / 缩放 / 居中 / 贴到屏幕的位置：贴图的规矩是"1 个像素就是 1 个像素"，
    //     iw = 图宽 × 缩放，全在 ApplyZoom / OnPaint / OnMouseWheel 里算，一个数都不动；
    //   · Pad = 1 那圈描边：它参与"图显示多大"（w = 图宽×缩放 + Pad*2），是图片外框的一部分；
    //   · MinimumSize 的 16：只是个"远小于系统最小宽度 136px"的哨兵值，不是版面尺寸。
    class PinForm : Form
    {
        public const float MinZoom = 0.10f;
        public const float MaxZoom = 4.00f;
        const int Pad = 1;                       // 1px 描边，浅色背景下也能看清边界（参与图尺寸计算，不乘 K）

        // ---- 右上角 × 按钮的逻辑尺寸（用的时候一律过 Ui.S）----
        const int CloseSize = 22;                // 圆的直径
        const int CloseMargin = 4;               // 距窗口右上角的边距
        const int CloseGap = 7;                  // × 两笔到圆边的留白
        const int CloseMinWin = 40;              // 窗口小于这个尺寸就不画按钮（和按钮同单位一起乘 K）

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
            // 小图会被撑到 136 宽、右边多出一条白边 —— 40x30 这种小截图就废了。
            // 16 是"远小于系统下限"的哨兵值，不是版面尺寸，所以不乘 K（乘成 24 也还是小于 136，没意义）
            MinimumSize = new Size(16, 16);
            Text = Lang.T("SnapWheel 贴图", "SnapWheel pinned image");
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            ApplyZoom(1f, false);
            Location = ClampToScreen(new Point(at.X - Width / 2, at.Y - Height / 2));

            // 比屏幕还大的图（整屏截图之类）：开机先缩到能放进屏幕，别一贴上来糊满整个桌面。
            // 只缩不放 —— 小图还是原尺寸贴。
            try
            {
                Rectangle wa = Screen.FromPoint(at).WorkingArea;
                float fit = 1f;
                if (_img.Width > wa.Width * 0.9f) fit = Math.Min(fit, wa.Width * 0.9f / _img.Width);
                if (_img.Height > wa.Height * 0.9f) fit = Math.Min(fit, wa.Height * 0.9f / _img.Height);
                if (fit < 1f) { ApplyZoom(fit, false); Location = ClampToScreen(new Point(at.X - Width / 2, at.Y - Height / 2)); }
            }
            catch { }
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
            // 12 是"图缩到极小时窗口别没了"的兜底，和上面的图片尺寸同属"内容像素"这一路
            //（MinimumSize=16 也兜过一次），所以**不乘 K** —— 乘了它就不再等于"图的像素 × 缩放 + 描边"，
            // 下面按光标锚点算位置的地方会跟着错
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

        // × 按钮的矩形（贴在窗口右上角）。22 / 4 是**界面元素**的尺寸，所以过 Ui.S 跟着 DPI 长：
        // 150% 下写死 22px 的按钮太小、不好点，而且它必须和下面那个"够不够大才显示"的门槛同尺度。
        Rectangle CloseRect()
        {
            int s = Ui.S(CloseSize), m = Ui.S(CloseMargin);
            return new Rectangle(Width - s - m, m, s, s);
        }

        // 窗口小到放不下按钮就不画、也不响应（双击 / Esc 照样能关）。
        // 门槛和按钮一起过 K，所以"多小的贴图会没有按钮"这件事在任何 DPI 下都一致 ——
        // 不乘的话 150% 下 33px 的按钮会盖满一张 45px 的小贴图。
        bool CloseVisible() { return Width > Ui.S(CloseMinWin) && Height > Ui.S(CloseMinWin); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.White);               // 带透明的图也有个白底，不至于透出桌面内容
            // 按图片自己的缩放尺寸画，别按窗口大小拉伸 —— 窗口万一被系统撑大，图也不该变形
            int iw = Math.Max(1, (int)Math.Round(_img.Width * _zoom));
            int ih = Math.Max(1, (int)Math.Round(_img.Height * _zoom));
            // Pad 那圈描边保持 1px、位置也不乘 K：它和 iw / ih 是一套算式里的东西
            // （图显示尺寸 = 图宽 × 缩放 + Pad*2），属"内容像素"；乘 K 会让贴出来的图比 iw 大一圈、取景对不上
            g.DrawImage(_img, new Rectangle(Pad, Pad, iw, ih));
            using (Pen bp = new Pen(Color.FromArgb(190, 70, 74, 84), 1f))
                g.DrawRectangle(bp, 0, 0, Width - 1, Height - 1);   // -1 是"画在边界内"的调整，不是尺寸

            if (_closeHover && CloseVisible())
            {
                Rectangle r = CloseRect();
                using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 38, 42, 50)))
                    g.FillEllipse(b, r);
                // 笔宽也跟着按钮一起长（100% 下 Ui.K=1，等于没变）：按钮变大了、两笔却还是原来那么细，
                // 看着像个细叉。这里乘的是**线条粗细**，不是字体磅值，不存在双倍放大
                using (Pen p = new Pen(Color.White, 1.8f * Ui.K))
                {
                    int m = Ui.S(CloseGap);
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
            bool h = CloseVisible() && CloseRect().Contains(e.Location);
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
