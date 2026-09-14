using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnapWheel
{
    // ==================== 滚动长截图（0.6.0）：交互层 ====================
    // 用户拍板的交互是「**他自己滚，程序跟着无缝拼接**」—— 不给目标窗口发合成滚轮消息，
    // 所以不挑窗口、也不怕惯性滚动和滚动节流。
    //
    // 这个窗口就是一条**贴在屏幕顶部的提示条**（宽 = 光标所在屏幕的宽，高 76px）：
    //   · 显示已接了几段、长图现在多高、距离上限还有多少；
    //   · 失配时直接说原因（"这一屏没对上，再滚一下"），不静默失败；
    //   · Enter = 结束并出图，Esc = 取消。
    //
    // 三个关键点：
    //   ① 窗口设了 WDA_EXCLUDEFROMCAPTURE —— **抓屏拍不到它自己**，否则每一帧都带着这条提示，
    //      拼出来的长图会一路重复这条黑边。
    //   ② 每 200ms 抓一屏就交给 LongShot.Push 去判：接得上就接，接不上就等下一帧。
    //      **不单独做"停稳检测"**：滚动中抓到的帧本来就匹配不上，判据交给拼接算法，逻辑只有一份。
    //      抓屏用一张复用的位图，避免每 200ms 分配一次 16MB。
    //   ③ 提示条只占屏幕顶部一条 —— 不遮内容（遮罩式浮层会让人看不清要滚的东西），
    //      鼠标也不会跑上去，滚轮照旧作用在下面的目标窗口。
    class LongShotForm : Form
    {
        public Bitmap Result;

        readonly Rectangle _scr;              // 提示条所在屏幕
        readonly Rectangle _vs;               // 虚拟屏（抓帧用它 —— 必须和第一帧同尺寸，否则 Push 会全部判"尺寸变了"）
        readonly LongShot _ls = new LongShot();
        readonly Timer _t;
        readonly Bitmap _scratch;             // 复用的抓屏位图
        string _msg = "滚到哪儿它接哪儿";
        bool _err = false;
        bool _busy = false;
        int _shots = 0;
        int _fails = 0;

        public LongShotForm(Bitmap firstFrame)
        {
            _vs = SystemInformation.VirtualScreen;
            try { _scr = Screen.FromPoint(Cursor.Position).Bounds; }
            catch { _scr = new Rectangle(_vs.Left, _vs.Top, _vs.Width, _vs.Height); }

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(24, 26, 32);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(_scr.Width, Ui.S(76));
            Location = new Point(_scr.Left, _scr.Top);

            string err = null;
            bool ok = (firstFrame != null) && _ls.Start(firstFrame, out err);
            if (!ok) { _msg = err ?? "没能开始长截图"; _err = true; }

            try { _scratch = new Bitmap(_vs.Width, _vs.Height, PixelFormat.Format32bppPArgb); }
            catch { _scratch = null; }

            _t = new Timer();
            _t.Interval = 200;
            _t.Tick += new EventHandler(OnTick);
            if (ok) _t.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 抓屏不许拍到自己（不然长图上会一路重复这条提示条）
            try { Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); } catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();                 // 要能收到 Enter / Esc
            BringToFront();
        }

        // 抓一屏（写到复用的位图里，省得每 200ms 分配一张全屏图）
        Bitmap Grab()
        {
            if (_scratch == null) return null;
            try
            {
                using (Graphics g = Graphics.FromImage(_scratch))
                    g.CopyFromScreen(_vs.Left, _vs.Top, 0, 0,
                                     new Size(_scratch.Width, _scr.Height), CopyPixelOperation.SourceCopy);
                return _scratch;
            }
            catch { return null; }
        }

        void OnTick(object o, EventArgs e)
        {
            if (_busy || _ls.Result == null) return;
            _busy = true;
            try
            {
                if (_ls.Full) { _msg = "已经到最大高度了，按 Enter 出图"; Invalidate(); return; }

                Bitmap frame = Grab();
                if (frame == null) { _msg = "抓屏失败（可能被安全软件拦了）"; _err = true; Invalidate(); return; }

                int added = 0;
                bool ok = _ls.Push(frame, out added);
                if (ok)
                {
                    _shots++;
                    _fails = 0;
                    _err = false;
                    _msg = "刚接上 " + added + " 行";
                }
                else
                {
                    _fails++;
                    _err = true;
                    _msg = (_ls.LastWhy == null ? "这一屏没对上，再滚一下" : _ls.LastWhy);
                }
                Invalidate();
            }
            catch (Exception ex)
            {
                try { Err.Log("LongShot", ex); } catch { }
            }
            finally { _busy = false; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Finish();     // 在条上点一下也算结束
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { Finish(); return; }
            if (e.KeyCode == Keys.Escape) { Cancel(); return; }
            base.OnKeyDown(e);
        }

        void Finish()
        {
            try { _t.Stop(); } catch { }
            try { Result = _ls.Finish(); } catch { }
            DialogResult = Result == null ? DialogResult.Cancel : DialogResult.OK;
            Close();
        }

        void Cancel()
        {
            try { _t.Stop(); } catch { }
            Result = null;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 左侧：图标块 + 标题
            int pad = Ui.S(14);
            int ih = Ui.S(44);
            Rectangle icon = new Rectangle(pad, (ClientSize.Height - ih) / 2, ih, ih);
            using (GraphicsPath p = Gfx.Round(icon, Ui.S(10)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 122, 204)))
                g.FillPath(b, p);
            using (Font f = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold))
                TextRenderer.DrawText(g, "↕", f, icon, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int x = icon.Right + Ui.S(12);
            int top = Ui.S(12);
            TextRenderer.DrawText(g, "滚动长截图：滚到哪儿它接哪儿", Font, new Point(x, top),
                Color.FromArgb(236, 238, 244), TextFormatFlags.NoPadding);
            using (Font fs = new Font("Microsoft YaHei UI", 8.5f))
                TextRenderer.DrawText(g, "按 Enter 结束出图 · 滚太快对不上就慢一点", fs, new Point(x, top + Ui.S(21)),
                    Color.FromArgb(150, 154, 164), TextFormatFlags.NoPadding);

            // 右侧：状态 + 高度进度
            string line;
            Color lc = Color.FromArgb(150, 154, 164);
            if (_err) { line = _msg; lc = Color.FromArgb(240, 190, 120); }
            else if (_shots == 0) line = "还没接上：滚动下面窗口的内容试试";
            else line = "已接 " + _shots + " 段 · 长图 " + _ls.Height + " px 高";

            int rw = Ui.S(300);
            Rectangle rr = new Rectangle(ClientSize.Width - pad - rw, top, rw, Ui.S(40));
            TextRenderer.DrawText(g, line, Font, rr, lc,
                TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPadding);

            // 进度条：画布高度 / 上限
            int pw = rw, ph = Ui.S(4);
            int py = rr.Bottom - Ui.S(6);
            Rectangle track = new Rectangle(rr.Right - pw, py, pw, ph);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 64, 74))) g.FillRectangle(b, track);
            double frac = (double)_ls.Height / LongShot.MaxCanvasH;
            if (frac < 0) frac = 0; if (frac > 1) frac = 1;
            Rectangle fill = new Rectangle(track.X, track.Y, Math.Max(1, (int)(track.Width * frac)), ph);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 140, 232))) g.FillRectangle(b, fill);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 定时器必须停 + 释放（之前项目里泄漏的 15ms 定时器把测试拖到 300 秒）
            try { if (_t != null) { _t.Stop(); _t.Dispose(); } } catch { }
            try { if (_scratch != null) _scratch.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { if (_t != null) { _t.Stop(); _t.Dispose(); } } catch { } }
            base.Dispose(disposing);
        }
    }
}
