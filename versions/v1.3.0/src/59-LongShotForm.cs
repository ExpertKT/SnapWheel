using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnapWheel
{
    // ==================== 滚动长截图（0.6.0）：交互层 ====================
    // 交互是"**他自己滚，程序跟着无缝拼接**"——不给目标窗口发合成滚轮消息，所以不挑窗口、
    // 也不怕惯性滚动和滚动节流。
    //
    // 0.6.0 修订：入口从托盘搬到**截图浮层的工具条**（用户要求），而且**只抓他在浮层里框出来的那块选区**：
    //   · 以前抓整个屏幕，长图里混着任务栏、侧边栏、别的窗口；现在你在哪个区域滚，就只拼那个区域。
    //   · 提示条贴在选区**上方**（放不下就改到下方），不会挡住你要看的内容。
    //
    // 这个窗口就是那条提示条：显示已接了几段、长图现在多高、失配时直接说原因；Enter 出图、Esc 取消。
    //
    // 三个关键点：
    //   ① 窗口设了 WDA_EXCLUDEFROMCAPTURE —— 抓屏拍不到它自己（否则每帧都带着这条提示、长图一路重复）。
    //   ② 每 200ms 抓一帧交给 LongShot.Push 去判：接得上就接，接不上就等下一帧。
    //      **不单独做"停稳检测"**：滚动中抓到的帧本来就匹配不上，判据交给拼接算法，逻辑只有一份。
    //   ③ 抓屏用一张复用的位图，不每 200ms 分配一次（选区大时那是十几 MB）。
    class LongShotForm : Form, IMessageFilter
    {
        public Bitmap Result;

        readonly Rectangle _region;           // 要拼的那块屏幕区域（就是浮层里的选区）
        readonly LongShot _ls = new LongShot();
        readonly Timer _t;
        readonly Bitmap _scratch;             // 复用的抓屏位图（选区尺寸）
        string _msg = Lang.T("滚到哪儿它接哪儿", "It stitches as it scrolls");
        bool _err = false;
        bool _busy = false;
        int _shots = 0;
        IntPtr _target = IntPtr.Zero;         // 选区下面那个窗口（滚轮消息发给它）
        byte[] _prevGray;                     // 上一帧的灰度采样（判断画面有没有在动）
        int _tick = 0, _stalls = 0;
        const int ScrollSteps = 1;            // 每次自动滚 1 格：步长小 → 重叠区大 → 匹配得上（3 格实测一次滚 300~500px，重叠太少）
        const double StillTol = 3.0;          // 判定画面没动的灰度差阈值

        public LongShotForm(Rectangle region)
        {
            if (region.Width < 16 || region.Height < 16)
                region = new Rectangle(region.Left, region.Top, Math.Max(16, region.Width), Math.Max(16, region.Height));

            // ── 把抓帧区域**夹进工作区**：一次把"任务栏那一条"排除掉 ──
            //
            // 为什么是这 3 行，而不是在拼接引擎里写一套"静止区自动检测"：
            //   用户报的症状是"长图里有任务栏、而且后面内容重复"。根因是他**框了整屏**，
            //   把任务栏也框了进去 —— 而任务栏不跟着页面滚，拼出来自然是重复的一条。
            //
            //   引擎里那套"猜哪几行没动"的检测，我试着修了好几轮：真机上有半透明任务栏、
            //   有亚像素滚动，逐行像素**在原理上就分不干净**（"跟不上滚动的正文"和
            //   "半透明任务栏"是同一个形态）。它已经花掉几轮、还没修干净。
            //
            //   而这 3 行是**确定性的**：不猜，直接排除。任务栏本来就不该出现在长图里 ——
            //   没有谁截长图是为了留住任务栏。
            //
            // 夹完之后如果太小（比如整块都在任务栏里），就不动它 —— 让下游照旧报"区域太小"。
            try
            {
                Rectangle wa = Screen.FromRectangle(region).WorkingArea;
                Rectangle clipped = Rectangle.Intersect(region, wa);
                if (clipped.Width >= 16 && clipped.Height >= 16) region = clipped;
            }
            catch { }

            _region = region;

            // 双缓冲：抓帧/状态刷新时提示条不再闪（用户反馈：进入滚动时 UI 在抖）
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(24, 26, 32);
            Font = new Font("Microsoft YaHei UI", 9.5f);

            int barH = Ui.S(86);
            int barW = Math.Max(Ui.S(420), _region.Width);
            Rectangle scr;
            try { scr = Screen.FromRectangle(_region).Bounds; }
            catch { scr = new Rectangle(_region.Left, _region.Top, barW, barH); }

            int by = _region.Top - barH - Ui.S(8);          // 默认贴在选区上方
            if (by < scr.Top) by = Math.Min(scr.Bottom - barH, _region.Bottom + Ui.S(8));   // 上方放不下就放下方
            int bx = Math.Max(scr.Left, Math.Min(_region.Left, scr.Right - barW));
            ClientSize = new Size(barW, barH);
            Location = new Point(bx, by);

            try { _scratch = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppPArgb); }
            catch { _scratch = null; }

            string err = null;
            Bitmap first = Grab();
            bool ok = (first != null) && _ls.Start(first, out err);
            if (!ok) { _msg = err ?? Lang.T("没能开始长截图", "Could not start the scrolling capture"); _err = true; }

            // Esc/Enter 用应用级消息过滤来收：提示条是无边框置顶窗口，焦点很容易被下面的
            // 目标程序抢走（用户反馈 Esc 按了没用，只能用鼠标点提示条退出）。
            Application.AddMessageFilter(this);

            // 选区正中心下面是谁？滚轮消息就发给他（自动滚动，不用用户自己滚）
            try { _target = Native.WindowFromPoint(new Native.POINT(_region.Left + _region.Width / 2, _region.Top + _region.Height / 2)); }
            catch { _target = IntPtr.Zero; }
            _prevGray = (first == null) ? null : LongShot.Sample(first);

            _t = new Timer();
            _t.Interval = 300;      // 300ms 抓一帧：200ms 太密，会把目标程序的滚动拖得不平滑（用户反馈）
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

        // 抓一帧（写进复用位图里）
        Bitmap Grab()
        {
            if (_scratch == null) return null;
            try
            {
                using (Graphics g = Graphics.FromImage(_scratch))
                    g.CopyFromScreen(_region.Left, _region.Top, 0, 0,
                                     new Size(_scratch.Width, _scratch.Height), CopyPixelOperation.SourceCopy);
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
                if (_ls.Full) { _msg = Lang.T("已经到最大高度了，按 Enter 出图", "Maximum height reached - press Enter to finish"); Invalidate(); return; }

                Bitmap frame = Grab();
                if (frame == null) { _msg = Lang.T("抓屏失败（可能被安全软件拦了）", "Screen capture failed (possibly blocked by security software)"); _err = true; Invalidate(); return; }

                byte[] g2 = LongShot.Sample(frame);
                _tick++;

                if (_tick == 1) { _prevGray = g2; ScrollNext(); _msg = Lang.T("开始自动滚动…", "Auto-scrolling…"); Invalidate(); return; }

                double diff = Diff(_prevGray, g2);
                _prevGray = g2;

                if (diff < StillTol)
                {
                    // 画面几乎没动：到底了，或者目标窗口不吃合成的滚轮消息
                    _stalls++;
                    if (_stalls >= 2) { _msg = Lang.T("到底了，正在出图", "Reached the end, generating the image"); Invalidate(); Finish(); return; }
                    _msg = Lang.T("画面没动（", "No movement (") + _stalls + Lang.T("/2），再试一次", "/2), trying again");
                    Invalidate();
                    ScrollNext();
                    return;
                }

                _stalls = 0;
                int added = 0;
                if (_ls.Push(frame, out added)) { _shots++; _err = false; _msg = Lang.T("已接 ", "Stitched ") + added + Lang.T(" 行", " rows"); }
                else { _err = true; _msg = (_ls.LastWhy == null ? Lang.T("这一屏没对上", "This screen does not line up") : _ls.LastWhy); }
                Invalidate();
                ScrollNext();
            }
            catch (Exception ex)
            {
                try { Err.Log("LongShot", ex); } catch { }
            }
            finally { _busy = false; }
        }

        // 给选区正中心下面那个窗口发一个合成的滚轮消息（往下滚几格）。
        // 这是**产品功能**（用户要求：滚动交给他自己太不可控，速度不重要、可用性优先），
        // 与验证时不许模拟真实输入那条纪律是两件事。
        void ScrollNext()
        {
            if (_target == IntPtr.Zero) return;
            try
            {
                int delta = -120 * ScrollSteps;                 // 负数 = 向下滚
                int wp = (delta << 16);
                int lx = _region.Left + _region.Width / 2;
                int ly = _region.Top + _region.Height / 2;
                int lp = (lx & 0xFFFF) | ((ly & 0xFFFF) << 16);
                Native.PostMessage(_target, Native.WM_MOUSEWHEEL, (IntPtr)wp, (IntPtr)lp);
            }
            catch { }
        }

        // 两帧灰度采样之间变化有多大（抽样算平均差）
        static double Diff(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 999;
            long s = 0; int n = 0;
            for (int i = 0; i < a.Length; i += 37) { int d = a[i] - b[i]; s += d < 0 ? -d : d; n++; }
            return n == 0 ? 999 : (double)s / n;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Finish();     // 在条上点一下也算结束
        }

        // 全局按键过滤：不管焦点在哪个窗口，Esc 取消、Enter 出图
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_KEYDOWN = 0x0100;
            if (m.Msg == WM_KEYDOWN)
            {
                int k = m.WParam.ToInt32();
                if (k == 27) { Cancel(); return true; }        // Esc
                if (k == 13) { Finish(); return true; }        // Enter
            }
            return false;
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
            int top = Ui.S(10);
            TextRenderer.DrawText(g, Lang.T("滚动长截图：自动滚动中，不用你操作", "Scrolling capture: auto-scrolling, no action needed"), Font, new Point(x, top),
                Color.FromArgb(236, 238, 244), TextFormatFlags.NoPadding);
            using (Font fs = new Font("Microsoft YaHei UI", 8.5f))
                TextRenderer.DrawText(g, Lang.T("到底会自动停 · Enter 提前出图 · Esc 取消", "Stops at the end · Enter finishes early · Esc cancels"), fs, new Point(x, top + Ui.S(20)),
                    Color.FromArgb(150, 154, 164), TextFormatFlags.NoPadding);

            string line;
            Color lc = Color.FromArgb(150, 154, 164);
            if (_err) { line = _msg; lc = Color.FromArgb(240, 190, 120); }
            else if (_shots == 0) line = Lang.T("还没接上：在那块区域里往下滚滚轮", "Not stitched yet: scroll down inside that area");
            else line = Lang.T("已接 ", "Stitched ") + _shots + Lang.T(" 段 · 长图 ", " sections · image ") + _ls.Height + Lang.T(" px 高", " px tall");

            int rw = Math.Max(Ui.S(200), ClientSize.Width - x - pad);   // 自适应：原来写死 300，长句子会被右边缘裁掉
            Rectangle rr = new Rectangle(ClientSize.Width - pad - rw, top, rw, Ui.S(36));
            TextRenderer.DrawText(g, line, Font, rr, lc,
                TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPadding);

            int pw = rw, ph = Ui.S(4);
            int py = rr.Bottom - Ui.S(2);
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
            try { Application.RemoveMessageFilter(this); } catch { }
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
