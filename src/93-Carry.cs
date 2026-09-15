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
        bool _tickAlive;                // 只为了写一条"定时器在跑、保护期已过"的日志（诊断用）

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
                if (warmed && !_tickAlive)
                {
                    // 只写一条：确认定时器真的在跑、保护期也过了。
                    // 之前"按 C 没反应"这种问题，缺的就是这个证据 —— 到底是没跑到这里，
                    // 还是跑到了但按键判断没通过。
                    _tickAlive = true;
                    Err.Log("Carry.Tick", new Exception("定时器在跑，保护期已过（按键开始生效）"));
                }
                if (warmed)
                {
                    // 空格是主要的「放下」键（用户要求：比 Enter 顺手）；Enter 保留作为等价键。
                    if (Rising(ref _prevSpace, Keys.Space) || Rising(ref _prevEnter, Keys.Enter))
                    {
                        Err.Log("Carry.Key", new Exception("空格 触发放下"));
                        DoDrop();
                        return;
                    }
                    if (Rising(ref _prevC, Keys.C))
                    {
                        Err.Log("Carry.Key", new Exception("C 触发复制到剪贴板"));
                        UseClipboard = true;
                        DoDrop();
                        return;
                    }
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
            _busy = true;
            try { _tick.Stop(); } catch { }
            Confirmed = true;
            DropPoint = _pos;

            // 先在屏幕上把假光标藏起来，接下来的动作交给真实光标
            try { if (_hint != null) _hint.Close(); } catch { }
            try { Opacity = 0; } catch { }
            try { Hide(); } catch { }
            Application.DoEvents();

            Thread t = new Thread(delegate()
            {
                SimulateDrag(_origin, DropPoint);
                try { BeginInvoke((MethodInvoker)delegate { try { Close(); } catch { } }); } catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 把光标移到某个点，**并且生成真实的鼠标移动消息**。
        ///
        /// 为什么不能只用 SetCursorPos：它只是把光标"瞬移"过去，**不产生鼠标移动消息**。
        /// 而轮盘的拖出是靠"按下 + 鼠标移动"启动的（OLE 拖放的启动条件），收不到移动消息
        /// 就永远不会开始拖 —— 用户实测的现象正是"真鼠标指针从起点移到了终点，然后什么都没发生"。
        /// 补一个 MOUSEEVENTF_MOVE 就是在告诉系统"鼠标真的动了"（位移 0，只为了让消息发出去）。
        /// </summary>
        static void MoveTo(int x, int y)
        {
            Native.SetCursorPos(x, y);
            Native.mouse_event(Native.MOUSEEVENTF_MOVE, 0, 0, 0, IntPtr.Zero);
        }

        /// <summary>
        /// 模拟一次真实的拖放：光标移到起点 → 按下 → 分步移到终点 → 松开。
        /// 分步移动很重要：一步跳过去的话，多数程序不会把它当成拖放。
        /// </summary>
        public static void SimulateDrag(Point from, Point to)
        {
            try
            {
                MoveTo(from.X, from.Y);
                Thread.Sleep(120);      // 让光标停稳，有些程序要求按下时鼠标确实静止过
                Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);

                int steps = 22;      // 步数多一些、每步慢一些，更像人手（一步跳过去多数程序不认）
                for (int i = 1; i <= steps; i++)
                {
                    Point p = StepPoint(from, to, i, steps);
                    MoveTo(p.X, p.Y);
                    Thread.Sleep(20);
                }
                Thread.Sleep(220);      // 到终点后再停一下，给目标程序时间响应"悬停"，然后再松手
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

        public CarryHintForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(198, 20, 22, 26);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            DoubleBuffered = true;

            string s = Lang.T("① WASD / 方向键 移动（Shift 加速）　→　② Alt+Tab 切到目标窗口　→　③ 空格 放下　　（C 复制到剪贴板　·　Esc 取消）",
                               "1) WASD / arrows move (Shift = faster)  ->  2) Alt+Tab to the target window  ->  3) Space to drop    (C = copy, Esc = cancel)");
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
            string s = Lang.T("① WASD / 方向键 移动（Shift 加速）　→　② Alt+Tab 切到目标窗口　→　③ 空格 放下　　（C 复制到剪贴板　·　Esc 取消）",
                               "1) WASD / arrows move (Shift = faster)  ->  2) Alt+Tab to the target window  ->  3) Space to drop    (C = copy, Esc = cancel)");
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
