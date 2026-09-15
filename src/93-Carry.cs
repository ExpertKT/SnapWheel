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

        Bitmap _thumb;                   // 吸附在假光标上的缩略图（传递中可以换）
        Point _origin;                   // 起点（轮盘上那张缩略图的位置）—— 模拟拖放时从这里按下
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

        // ---- "飞回"动画：按 Esc 取消时，不是啪一下消失，而是让缩略图飞回它在轮盘上的位置 ----
        // 直接消失会让人不知道刚才那张去哪了；飞回去明确表达"放回原处了"。
        // 不需要额外的窗口 —— 假光标窗口本身就是透明置顶的，让它自己移动+淡出即可。
        bool _flyBack;
        Point _flyFrom, _flyTo;
        DateTime _flyStart;
        float _flyScale = 1f;            // 飞行途中缩略图一起缩小（1 → 0.4）
        const int FlyMs = 320;

        /// <summary>用户确认放下了（Enter/空格）。</summary>
        public bool Confirmed;
        /// <summary>放下的位置（屏幕坐标）。</summary>
        public Point DropPoint;

        // true = 用的是「复制到剪贴板」，而不是模拟拖放（对键盘用户更顺，也更可靠）
        public bool UseClipboard;

        /// <summary>
        /// 用户在传递模式里要求换一张图（按 , 或 .）。
        /// 参数是相对位移：-1 上一张、+1 下一张。
        /// 由 App 订阅去换图，然后回调 SetThumb 把新的缩略图换上来。
        /// </summary>
        public event Action<int> SwitchRequested;

        /// <summary>换掉假光标上吸附的那张图（App 换好之后回调进来）。</summary>
        public void SetThumb(Bitmap thumb, Point origin)
        {
            try { if (_thumb != null) _thumb.Dispose(); } catch { }
            _thumb = thumb;
            _origin = origin;
            try { Invalidate(); } catch { }
        }

        /// <summary>换提示条上的文字（提示条是独立小窗，转交给它）。</summary>
        public void SetHintText(string text)
        {
            try { if (_hint != null) _hint.SetText(text); } catch { }
        }

        bool _prevPrev, _prevNext;       // 换图键的边沿状态

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
            // ⚠️ 键盘钩子先停用：装上去之后"一进传递模式就卡死"（用户实测）。
            // 低级键盘钩子一旦处理不当会卡住整个系统的输入链，风险太高 —— 先回到可用状态，
            // 再换一种不插进系统输入链的做法来处理"WASD 漏到前台窗口"的问题。
            // InstallHook();
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

            // 飞行分支：按 Esc 之后走这条，把缩略图送回轮盘上的位置
            if (_flyBack)
            {
                try
                {
                    double t = (DateTime.Now - _flyStart).TotalMilliseconds / (double)FlyMs;
                    if (t >= 1.0) { try { _tick.Stop(); } catch { } try { Close(); } catch { } return; }
                    float eased = 1f - (float)Math.Pow(1.0 - t, 3);    // ease-out：起步快、收尾慢
                    _fx = _flyFrom.X + (_flyTo.X - _flyFrom.X) * eased;
                    _fy = _flyFrom.Y + (_flyTo.Y - _flyFrom.Y) * eased;
                    _pos = new Point((int)Math.Round(_fx), (int)Math.Round(_fy));
                    _flyScale = 1f - eased * 0.6f;                     // 缩到 40%
                    try { Opacity = Math.Max(0.05, 1.0 - t * 0.85); } catch { }
                    ApplyPos();
                }
                catch { try { Close(); } catch { } }
                return;
            }

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
                    // 空格和 Enter 都能"放下"（空格更顺手，Enter 保留）。
                    // 注意两个键各用一个 _prev 变量：共用一个的话，先按 Enter 再按空格时
                    // 第二个键会被当成"一直按着"，上升沿不成立。
                    if (Rising(ref _prevSpace, Keys.Space) || Rising(ref _prevEnter, Keys.Enter)) { DoDrop(); return; }
                    if (Rising(ref _prevC, Keys.C)) { UseClipboard = true; DoDrop(); return; }   // 复制到剪贴板
                    if (Rising(ref _prevEsc, Keys.Escape)) { Cancel(); return; }

                    // 换一张图（不想传这张了，不用退出去重来）。
                    // 用 [ ] 而不是 Q/E：Q E 是会"打字"的字母键，按下去目标窗口里会留下字母、
                    // 还可能把输入法叫出来；方括号不产生文字，也就没有这个问题。
                    // （这也是为什么移动键推荐方向键 —— 同理不产生文字。）
                    if (Rising(ref _prevPrev, Keys.OemOpenBrackets)) { RaiseSwitch(-1); return; }
                    if (Rising(ref _prevNext, Keys.OemCloseBrackets)) { RaiseSwitch(1); return; }
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
            // 不直接消失：让缩略图"飞回"它在轮盘上的位置（缓动 + 淡出 + 缩小，约 320ms）。
            // 真正关闭在 OnTick 的飞行分支里做。
            if (_flyBack) { try { Close(); } catch { } return; }
            _flyBack = true;
            Confirmed = false;
            _flyFrom = _pos;
            _flyTo = _origin;
            _flyStart = DateTime.Now;
            try { if (_hint != null) _hint.Close(); } catch { }   // 提示条先收掉
            try { _tick.Start(); } catch { }                       // 定时器继续跑，改走飞行分支
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

        /// <summary>通知 App "用户要换图了"（-1 上一张 / +1 下一张）。</summary>
        void RaiseSwitch(int delta)
        {
            try { if (SwitchRequested != null) SwitchRequested(delta); } catch { }
        }

        // ---------- 键盘钩子：把属于传递模式的按键"吃掉" ----------
        //
        // 不做这件事的话，用户按 WASD 时那些字母会照常送进前台窗口：
        // 输入法弹出来、聊天框里被打进一堆字母（用户实测就是这个）。
        // 轮询只能"读"按键，要"拦"就必须在系统输入链上装钩子。
        IntPtr _hook = IntPtr.Zero;
        Native.LowLevelKeyboardProc _hookProc;      // 必须保留引用：被 GC 回收的话钩子会失效甚至崩

        void InstallHook()
        {
            try
            {
                if (_hook != IntPtr.Zero) return;
                _hookProc = new Native.LowLevelKeyboardProc(HookCallback);
                _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc,
                                                Native.GetModuleHandle(null), 0);
                Err.Log("Carry.Hook", new Exception("键盘钩子已安装：" + (_hook != IntPtr.Zero)));
            }
            catch (Exception ex) { Err.Log("Carry.Hook", ex); }
        }

        void UninstallHook()
        {
            try
            {
                if (_hook != IntPtr.Zero)
                {
                    Native.UnhookWindowsHookEx(_hook);
                    Err.Log("Carry.Hook", new Exception("键盘钩子已卸载"));
                }
            }
            catch { }
            _hook = IntPtr.Zero;
            _hookProc = null;
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
                    if (IsCarryKey(vk)) return (IntPtr)1;   // 返回 1 = 吞掉，不让它继续传下去
                }
            }
            catch { }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>传递模式自己要用到的键（这些键在传递期间不该传给别人）。</summary>
        static bool IsCarryKey(int vk)
        {
            switch (vk)
            {
                case 0x57:   // W
                case 0x41:   // A
                case 0x53:   // S
                case 0x44:   // D
                case 0x51:   // Q  上一张
                case 0x45:   // E  下一张
                case 0x20:   // 空格 放下
                case 0x0D:   // Enter 放下
                case 0x1B:   // Esc 取消
                case 0x10:   // Shift 加速
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 轮盘窗口的句柄，由 App 在进入传递模式时设置。
        /// 模拟拖放前必须先把它拉到前台 —— 见下面 SimulateDrag 里的说明。
        /// </summary>
        public static IntPtr WheelHandle = IntPtr.Zero;

        /// <summary>
        /// 把光标移到某个点，**并且生成真实的鼠标移动消息**。
        ///
        /// 为什么不能只用 SetCursorPos：它只是把光标"瞬移"过去，**不产生鼠标移动消息**。
        /// 而轮盘的拖出是靠"按下 + 鼠标移动"启动的（OLE 拖放的启动条件），收不到移动消息
        /// 就永远不会开始拖 —— 用户实测的现象正是"真鼠标指针从起点移到了终点，然后什么都没发生"。
        /// 补一个 MOUSEEVENTF_MOVE 就是在告诉系统"鼠标真的动了"（位移 0，只为了让消息发出去）。
        ///
        /// ⚠️ 这几行在代码回退时丢过一次，结果"放下"又变成同一个失败现象。
        /// 改动这段时务必保留 —— 它看起来多余，其实是拖放能启动的关键。
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
                // 关键的第一步：把轮盘拉到前台。
                //
                // 轮盘是"不激活窗口"（当初为了不抢焦点、让用户能 Alt+Tab 切过去），
                // 而 Windows 的规则是：**非活动窗口的第一次点击会被系统用来激活它，应用收不到**。
                // 我们的模拟点击正好就是那第一次点击 —— 被系统吃掉，轮盘没收到"按下"，
                // 也就不会有 DoDragDrop。所以这里先显式把它带到前台，让后面的点击真正送达。
                if (WheelHandle != IntPtr.Zero)
                {
                    bool fg = Native.SetForegroundWindow(WheelHandle);
                    // 记下来：这一步成不成，直接决定后面的模拟点击能不能送到轮盘手上
                    Err.Log("Carry.Drag", new Exception("放下开始：拿前台=" + fg + " 起点=" + from.X + "," + from.Y + " 终点=" + to.X + "," + to.Y));
                    Thread.Sleep(180);
                }
                else
                {
                    Err.Log("Carry.Drag", new Exception("放下开始：WheelHandle 没拿到！起点=" + from.X + "," + from.Y));
                }

                MoveTo(from.X, from.Y);
                Thread.Sleep(100);

                // 核对坐标：我们**以为**移到了起点，实际落在哪？
                try
                {
                    Point actual = Cursor.Position;
                    Rectangle vs = SystemInformation.VirtualScreen;
                    Err.Log("Carry.Drag", new Exception(
                        "移到起点 " + from.X + "," + from.Y + " → 实际光标 " + actual.X + "," + actual.Y +
                        "　虚拟屏幕=" + vs.Width + "x" + vs.Height));
                }
                catch { }

                Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);

                int steps = 22;      // 步数多一些、每步慢一些，更像人手（一步跳过去多数程序不认）
                for (int i = 1; i <= steps; i++)
                {
                    Point p = StepPoint(from, to, i, steps);
                    MoveTo(p.X, p.Y);
                    Thread.Sleep(20);
                }
                Thread.Sleep(120);
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
                // 阴影（跟着缩略图一起缩）
                int shw = (int)(ThumbW * _flyScale), shh = (int)(ThumbH * _flyScale);
                for (int i = 4; i >= 1; i--)
                {
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(26, 0, 0, 0)))
                        g.FillRectangle(sb, tx - i + 2, ty - i + 3, shw + i * 2, shh + i * 2);
                }
                // 图（按 _flyScale 缩放：飞行途中一起缩小，回到环上时正好是缩略图大小）
                int tw = (int)(ThumbW * _flyScale), th = (int)(ThumbH * _flyScale);
                int cx = tx + ThumbW / 2, cy = ty + ThumbH / 2;
                Rectangle box = new Rectangle(cx - tw / 2, cy - th / 2, tw, th);
                if (_thumb != null) g.DrawImage(_thumb, box);
                // 白边（提到"被拿起来"的感觉）
                using (Pen p = new Pen(Color.FromArgb(230, 255, 255, 255), 2f))
                    g.DrawRectangle(p, box);
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
            UninstallHook();     // 一定要卸：钩子挂着不卸会影响全局键盘输入
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
        string _text;      // 当前显示的说明文字（换图/放下之后会换成别的提示）

        /// <summary>
        /// 换上新的说明文字。文字长短不同，窗口要**重新量一次**并重新贴到底部居中，
        /// 不然新文字会被裁掉。
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

            string s = _text ?? Lang.T("方向键 移动　·　Shift 加速　·　空格 放下　·　[ ] 换一张　·　Esc 取消　（WASD 也能用，但会在目标窗口里留下字母）",
                                       "Arrow keys move  ·  Shift faster  ·  Space drop  ·  [ ] switch  ·  Esc cancel   (WASD also works, but it types letters into the target window)");
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
            try { return new Font(DrawKit.UI, 10.5f); }
            catch { return new Font(FontFamily.GenericSansSerif, 10.5f); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            string s = _text ?? Lang.T("方向键 移动　·　Shift 加速　·　空格 放下　·　[ ] 换一张　·　Esc 取消　（WASD 也能用，但会在目标窗口里留下字母）",
                                       "Arrow keys move  ·  Shift faster  ·  Space drop  ·  [ ] switch  ·  Esc cancel   (WASD also works, but it types letters into the target window)");
            using (Font f = HintFont())
            using (SolidBrush b = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
                DrawKit.DrawFitted(g, s, new RectangleF(PadX - 6, PadY - 4, ClientSize.Width - PadX * 2 + 12, ClientSize.Height - PadY * 2 + 8),
                                   b.Color, 11, ClientSize.Width - PadX * 2 + 12, DrawKit.UI, FontStyle.Regular, Align.Center);
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
