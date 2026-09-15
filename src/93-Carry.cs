using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SnapWheel
{
    // ==================== 传递模式（0.9.0） ====================
    //
    // 目标：**不用鼠标也能把一张图"拖"进别的程序**。
    //
    // 设计（用户提的方案）：
    //   ① 在轮盘上用键盘选中一张缩略图
    //   ② 进入传递模式：缩略图被"提起来"，跟着一个**假光标**走
    //   ③ 用户自己切到目标窗口（Alt+Tab 或鼠标点都行，随便）
    //   ④ 用 WASD（或方向键）操控假光标移动，Shift 加速
    //   ⑤ 按 Enter/空格 = 放下；Esc = 取消
    //
    // 为什么"放下"要真的去动鼠标：目标程序只认**真实的拖放**（OLE 拖放 / WM_DROPFILES），
    // 它没法理解"我这边有个假光标"。所以放下的瞬间，我们做的事是：
    //   把真实光标挪到假光标的位置 → 左键按下 → 分几步移动过去（模拟人手）→ 左键松开。
    // 这样目标程序收到的就是一次普普通通的拖放，兼容性最好。
    //
    // 关于"怎么知道用户按了 WASD"：**不用低级键盘钩子**，而是定时器轮询 GetAsyncKeyState。
    // 钩子会插进系统输入链、容易被安全软件盯上、还得保证一定卸载；轮询简单得多，效果一样。

    class CarryForm : Form
    {
        const int TickMs = 15;
        const float SpeedSlow = 6f;      // 像素/帧
        const float SpeedFast = 20f;     // 按住 Shift
        const int ThumbW = 132, ThumbH = 99;   // 吸附的缩略图尺寸
        const int CursorW = 26, CursorH = 26;  // 假光标尺寸

        readonly Bitmap _thumb;
        readonly Point _origin;          // 起点（轮盘上那张缩略图的位置）—— 模拟拖放时从这里按下
        Point _pos;                      // 假光标的屏幕坐标
        float _fx, _fy;                  // 亚像素累积（不然慢速移动会卡顿或跳格）
        System.Windows.Forms.Timer _tick;
        CarryHintForm _hint;            // 屏幕底部的操作提示（独立小窗，不抢焦点）
        DateTime _started;
        bool _busy;                      // 正在执行"放下"的模拟，别让定时器再插一脚
        // 一次性动作键要用"边沿"判断：只在"刚才没按、现在按下"那一刻触发。
        // 不这么做会出事 —— 传递热键是 Ctrl+Alt+C，用户按完 C 键还按着不放，
        // 窗口一出现就检测到 C 是按下状态，立刻当成"复制到剪贴板"并关闭，
        // 表现就是"闪了一下就没了"（用户实测出来的）。
        bool _prevEnter, _prevEsc, _prevC, _prevSpace;
        int _dropCount;                 // 这次传递里按了几次"放下"（显示在提示条上，帮用户判断要不要改用剪贴板）

        /// <summary>用户确认放下了（Enter/空格）。</summary>
        public bool Confirmed;
        /// <summary>放下的位置（屏幕坐标）。</summary>
        public Point DropPoint;

        // true = 用的是「复制到剪贴板」，而不是模拟拖放（对键盘用户更顺，也更可靠）
        // true = 用的是「复制到剪贴板」，而不是模拟拖放（对键盘用户更顺，也更可靠）
        public bool UseClipboard;

        public CarryForm(Bitmap thumb, Point origin)
        {
            _thumb = thumb;
            _origin = origin;
            _pos = new Point(origin.X, origin.Y);
            _fx = origin.X; _fy = origin.Y;
            _started = DateTime.Now;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Magenta;          // 透明键：这个颜色会被系统抠掉，只留下光标和缩略图
            TransparencyKey = Color.Magenta;
            ClientSize = new Size(ThumbW + CursorW + 24, ThumbH + CursorH + 24);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            DoubleBuffered = true;
            KeyPreview = true;

            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = TickMs;
            _tick.Tick += new EventHandler(OnTick);
            _tick.Start();

            // 操作提示条：第一次用传递模式的人不可能知道按什么键，所以必须写在屏幕上。
            try { _hint = new CarryHintForm(); _hint.Show(); } catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                TopMost = true;
                BringToFront();
                // 这里**故意不** Activate()：假光标靠全局轮询 GetAsyncKeyState 读按键，
                // 不需要键盘焦点；抢焦点会让用户 Alt+Tab 切到别的程序时"切不过去"（用户实测）。
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            catch { }
            ApplyPos();
        }

        static bool Down(Keys k)
        {
            try { return (Native.GetAsyncKeyState((int)k) & 0x8000) != 0; }
            catch { return false; }
        }

        /// <summary>只在"刚才没按、现在按下"的那一刻返回 true（上升沿）。</summary>
        static bool Rising(ref bool prev, Keys k)
        {
            bool now = Down(k);
            bool edge = now && !prev;
            prev = now;
            return edge;
        }

        void OnTick(object sender, EventArgs e)
        {
            if (_busy) return;
            try
            {
                // 超时保护：两分钟没有任何确认就自己取消，免得假光标一直赖在屏幕上
                if ((DateTime.Now - _started).TotalSeconds > 120) { Cancel(); return; }

                float v = Down(Keys.ShiftKey) || Down(Keys.LShiftKey) || Down(Keys.RShiftKey) ? SpeedFast : SpeedSlow;
                float dx = 0, dy = 0;

                // WASD 和方向键都支持（有人习惯方向键）
                if (Down(Keys.W) || Down(Keys.Up))    dy -= v;
                if (Down(Keys.S) || Down(Keys.Down))  dy += v;
                if (Down(Keys.A) || Down(Keys.Left))  dx -= v;
                if (Down(Keys.D) || Down(Keys.Right)) dx += v;

                if (dx != 0 && dy != 0) { dx *= 0.7071f; dy *= 0.7071f; }   // 斜着走不要更快

                if (dx != 0 || dy != 0)
                {
                    _fx += dx; _fy += dy;
                    // 夹在虚拟屏幕范围内，别让假光标跑出屏幕
                    Rectangle vs = SystemInformation.VirtualScreen;
                    if (_fx < vs.Left) _fx = vs.Left;
                    if (_fy < vs.Top) _fy = vs.Top;
                    if (_fx > vs.Right - CursorW) _fx = vs.Right - CursorW;
                    if (_fy > vs.Bottom - CursorH) _fy = vs.Bottom - CursorH;
                    _pos = new Point((int)Math.Round(_fx), (int)Math.Round(_fy));
                    ApplyPos();
                }

                // 一次性动作：必须用上升沿（见 _prev* 字段的注释）。
                // 另外刚显示后的 350ms 里不响应按键 —— 用户刚按完热键，
                // 手指还压在键上，那段窗口期不该被当成"用户操作"。
                bool warmed = (DateTime.Now - _started).TotalMilliseconds > 350;
                if (warmed)
                {
                    // 空格是主要的「放下」键（用户要求：比 Enter 顺手）；Enter 保留作为等价键。
                    if (Rising(ref _prevSpace, Keys.Space) || Rising(ref _prevEnter, Keys.Enter)) { DoDrop(); return; }
                    if (Rising(ref _prevC, Keys.C)) { UseClipboard = true; DoDrop(); return; }   // 复制到剪贴板
                    if (Rising(ref _prevEsc, Keys.Escape)) { Cancel(); return; }
                }
            }
            catch { }
        }

        void ApplyPos()
        {
            try { Location = _pos; Invalidate(); } catch { }
        }

        void Cancel()
        {
            try { _tick.Stop(); } catch { }
            Confirmed = false;
            try { if (_hint != null) _hint.Close(); } catch { }
            try { Close(); } catch { }
        }

        // ---------- 放下：把假光标的位置"演"成一次真实的鼠标拖放 ----------
        void DoDrop()
        {
            if (_busy) return;              // 正在放就别重复触发
            _busy = true;
            Confirmed = true;
            DropPoint = _pos;

            // 剪贴板模式：不动鼠标，直接收摊，剩下的交给 App 去写剪贴板
            if (UseClipboard)
            {
                try { _tick.Stop(); } catch { }
                try { if (_hint != null) _hint.Close(); } catch { }
                try { Close(); } catch { }
                return;
            }

            // 关键：**不要**在这里把窗口藏起来。
            // 原来这里是 Hide() + Opacity = 0，用户看到的是"假光标消失了、鼠标自己动了几下、
            // 然后就什么都干不了了" —— 既看不到结果，也没法重试（用户反馈）。
            // 现在改成：假光标留在原地，模拟拖放在后台跑，跑完还能再按空格重试。
            // 定时器也不停：还要继续读按键（空格重试 / C 复制 / Esc 退出）。
            _dropCount++;
            SetHint(_dropCount == 1
                ? Lang.T("正在放下…　（没收到就按 C 复制到剪贴板，或再按一次空格重试）",
                         "Dropping…  (if nothing arrived, press C to copy to the clipboard, or press Space again to retry)")
                : Lang.T("已尝试放下 " + _dropCount + " 次　（建议按 C 复制到剪贴板 —— 那一定有效）",
                         "Drop attempted " + _dropCount + " time(s)  (press C to copy to the clipboard instead - that always works)"));

            Thread t = new Thread(delegate()
            {
                SimulateDrag(_origin, DropPoint);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _busy = false;      // 允许再按空格重试
                        Confirmed = false;  // 还没真正离开传递模式
                        SetHint(Lang.T("放下动作已完成。收到图了就按 Esc 退出；没收到就按 C 复制到剪贴板。",
                                       "Drop finished. If the image arrived, press Esc to exit; if not, press C to copy to the clipboard."));
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>改提示条上的文字（提示条是独立的小窗，这里转交给它）。</summary>
        void SetHint(string text)
        {
            try { if (_hint != null) _hint.SetText(text); } catch { }
        }

        /// <summary>
        /// 模拟一次真实的拖放：光标移到起点 → 按下 → 分步移到终点 → 松开。
        /// 分步移动很重要：一步跳过去的话，多数程序不会把它当成拖放。
        /// </summary>
        public static void SimulateDrag(Point from, Point to)
        {
            try
            {
                Native.SetCursorPos(from.X, from.Y);
                Thread.Sleep(60);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);

                int steps = 22;      // 步数多一些、每步慢一些，更像人手（一步跳过去多数程序不认）
                for (int i = 1; i <= steps; i++)
                {
                    Point p = StepPoint(from, to, i, steps);
                    Native.SetCursorPos(p.X, p.Y);
                    Thread.Sleep(20);
                }
                Thread.Sleep(80);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(40);
            }
            catch { }
        }

        // ---------- 绘制：缩略图（带阴影）+ 假光标 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // 缩略图吸附在假光标右下角
            int tx = CursorW / 2 + 6;
            int ty = CursorH / 2 + 6;

            try
            {
                // 阴影
                for (int i = 4; i >= 1; i--)
                {
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(26, 0, 0, 0)))
                        g.FillRectangle(sb, tx - i + 2, ty - i + 3, ThumbW + i * 2, ThumbH + i * 2);
                }
                // 图
                if (_thumb != null) g.DrawImage(_thumb, new Rectangle(tx, ty, ThumbW, ThumbH));
                // 白边（提到"被拿起来"的感觉）
                using (Pen p = new Pen(Color.FromArgb(230, 255, 255, 255), 2f))
                    g.DrawRectangle(p, tx, ty, ThumbW, ThumbH);
            }
            catch { }

            // 假光标：画一个和系统箭头形状接近的白色箭头 + 黑描边
            try
            {
                PointF[] arrow = new PointF[]
                {
                    new PointF(2f, 2f),
                    new PointF(2f, 20f),
                    new PointF(7f, 15.5f),
                    new PointF(10.5f, 23f),
                    new PointF(14f, 21.5f),
                    new PointF(10.5f, 14f),
                    new PointF(17f, 13.5f),
                };
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddPolygon(arrow);
                    using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, gp);
                    using (Pen p = new Pen(Color.FromArgb(240, 20, 20, 20), 1.6f)) g.DrawPath(p, gp);
                }
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _tick.Stop(); _tick.Dispose(); } catch { }
            try { if (_hint != null) { _hint.Close(); _hint.Dispose(); _hint = null; } } catch { }
            base.OnFormClosed(e);
        }

        // 让窗口不抢焦点也能收到 Esc（保险）
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Cancel(); return true; }
            if (keyData == Keys.Enter || keyData == Keys.Space) { DoDrop(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        /// <summary>
        /// 拖放过程的第 i 步落在哪：在起点和终点之间按比例插值。
        /// 抽成独立方法有两个原因：① 语义清楚（这正是"分步移动"的核心）；
        /// ② **可测** —— 分步是否符合预期是能在离线环境验证的，不必真去动鼠标。
        /// </summary>
        public static Point StepPoint(Point from, Point to, int i, int steps)
        {
            if (steps < 1) steps = 1;
            if (i < 0) i = 0;
            if (i > steps) i = steps;
            return new Point(from.X + (to.X - from.X) * i / steps,
                             from.Y + (to.Y - from.Y) * i / steps);
        }

    }

    /// <summary>
    /// 传递模式的操作提示条：贴在屏幕底部居中，只说明按什么键，不接受任何操作。
    /// 单独一个小窗而不是画在假光标窗口里 —— 假光标窗口只有光标那么大，
    /// 而且它跟着光标到处跑，提示条会一直晃。
    /// </summary>
    class CarryHintForm : Form
    {
        const int PadX = 22, PadY = 12;

        string _text;      // 当前显示的说明文字（按下"放下"之后会换成结果提示）

        /// <summary>
        /// 换上新的说明文字。
        /// 注意文字长短不同，窗口要**重新量一次**并重新贴到底部居中 —— 不然新文字会被裁掉。
        /// </summary>
        public void SetText(string t)
        {
            if (string.IsNullOrEmpty(t)) return;
            _text = t;
            try
            {
                using (Font f = HintFont())
                using (Bitmap b = new Bitmap(1, 1))
                using (Graphics g = Graphics.FromImage(b))
                {
                    SizeF sz = g.MeasureString(t, f, new PointF(0, 0), StringFormat.GenericTypographic);
                    ClientSize = new Size((int)Math.Ceiling((double)sz.Width) + PadX * 2,
                                          (int)Math.Ceiling((double)sz.Height) + PadY * 2);
                }
                Rectangle scr = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(scr.Left + (scr.Width - Width) / 2, scr.Bottom - Height - 40);
                Invalidate();
            }
            catch { }
        }

        static string DefaultText()
        {
            return Lang.T("① WASD / 方向键 移动（Shift 加速）　→　② Alt+Tab 切到目标窗口　→　③ 空格 放下　　（C 复制到剪贴板　·　Esc 取消）",
                          "1) WASD / arrows move (Shift = faster)  ->  2) Alt+Tab to the target window  ->  3) Space to drop    (C = copy, Esc = cancel)");
        }

        public CarryHintForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(198, 20, 22, 26);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            DoubleBuffered = true;

            string s = DefaultText();
            using (Font f = HintFont())
            {
                SizeF sz;
                using (Bitmap b = new Bitmap(1, 1))
                using (Graphics g = Graphics.FromImage(b))
                    sz = g.MeasureString(s, f, new PointF(0, 0), StringFormat.GenericTypographic);
                ClientSize = new Size((int)Math.Ceiling((double)sz.Width) + PadX * 2, (int)Math.Ceiling((double)sz.Height) + PadY * 2);
            }

            // 贴在"光标所在那块屏幕"的底部居中
            Rectangle scr = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(scr.Left + (scr.Width - Width) / 2, scr.Bottom - Height - 40);
        }

        static Font HintFont()
        {
            try { return new Font(DrawKit.UI, 12f); }
            catch { return new Font(FontFamily.GenericSansSerif, 10.5f); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            string s = _text ?? DefaultText();
            using (Font f = HintFont())
            using (SolidBrush b = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
                DrawKit.DrawFitted(g, s, new RectangleF(PadX - 6, PadY - 4, ClientSize.Width - PadX * 2 + 12, ClientSize.Height - PadY * 2 + 8),
                                   b.Color, 13, ClientSize.Width - PadX * 2 + 12, DrawKit.UI, FontStyle.Regular, Align.Center);
        }

        // 不抢焦点：不然用户按 Alt+Tab 切窗口时提示条会把焦点抢回来
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                TopMost = true;
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            catch { }
    }
}
}
