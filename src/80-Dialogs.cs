using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
namespace SnapWheel
{
    // ============ 内容比窗口高时的"整块滚动"（给说明类窗口用） ============
    // 为什么需要它：说明窗口的内容是按 DPI 放大后**按内容长高**的（150% 下引导窗口内容约 1500px），
    // 再高就超过屏幕工作区 —— 那时候**不是字被裁，而是整个按钮被顶到屏幕外面**，同样叫"显示不全"。
    // 所以：窗口高度夹到工作区以内，装不下的部分用滚轮上下翻。
    //
    // 刻意**不用** Panel/AutoScroll：那会盖住窗口的半透明毛玻璃底（Panel 要不透明才不闪），
    // 而这里只需要把子控件整块上下挪 —— 挪出窗口的部分由窗口自己裁掉，背景一动不动。
    class UiScroll
    {
        readonly List<Control> _cs = new List<Control>();
        readonly List<int> _top0 = new List<int>();
        int _scroll, _contentH, _viewH;

        public bool Active { get { return _contentH > _viewH; } }
        public int Scroll { get { return _scroll; } }

        // 把一个内容控件登记进来（登记时它已经在最终位置上了，这里记下原始 Top）
        public void Add(Control c)
        {
            _cs.Add(c);
            _top0.Add(c.Top);
        }

        // 排完版调用：contentH = 内容理想高度，viewH = 实际能看到的区域高度
        public void Finish(int contentH, int viewH)
        {
            _contentH = contentH;
            _viewH = viewH;
            _scroll = 0;
            Apply();
        }

        // 滚轮：一格的位移按 DPI 走（96dpi 下一格 48px）
        public bool Wheel(int delta)
        {
            if (!Active) return false;
            return ScrollBy(-(delta / 120) * Ui.S(48));
        }

        public bool ScrollBy(int dy)
        {
            if (!Active) return false;
            int max = _contentH - _viewH;
            int was = _scroll;
            _scroll += dy;
            if (_scroll < 0) _scroll = 0;
            if (_scroll > max) _scroll = max;
            if (_scroll == was) return false;
            Apply();
            return true;
        }

        void Apply()
        {
            for (int i = 0; i < _cs.Count; i++)
                _cs[i].Top = _top0[i] - _scroll;
        }
    }

    // 新手上路 / 升级说明窗口。
    //
    // ⚠️ DPI（0.5.3 修订）：这个窗口原来**每个坐标都是写死的像素**，而程序是 per-monitor DPI aware 的 ——
    // 150% 缩放下字体按 DPI 放大 1.5 倍、格子却还是原来那么大，于是长句子右半截被裁、
    // 说明文字只看得见第一行（用户报的「引导界面 / 新手引导显示不全」就是这个）。
    // 现在的规矩：
    //   · 长度（坐标 / 宽 / 高 / 行距 / 按钮）一律过 Ui.S() 乘 DPI 系数；
    //   · 说明文字交给 Ui.Wrap()：自己折行、自己报高度 —— 比"把高度算准"可靠；
    //   · 窗口 ClientSize 最后按内容算一次；**再夹到屏幕工作区以内**，装不下的部分滚轮翻（见 UiScroll）；
    //   · 底部按钮固定贴在窗口底边 —— 永远看得见，滚到哪儿都点得到。
    class GuideForm : Form
    {
        // ---- 版面常量（逻辑像素；用的时候都乘 K）----
        const int WinW = 540;
        const int PadL = 28, PadR = 28, PadT = 24;
        const int Gutter = 16;      // 正文相对条目标题的右缩进
        const int RowGap = 14;      // 相邻两条之间的空隙
        const int BtnH = 38;

        int _contentW;              // 已乘 K 的内容宽（= 窗口宽 - 左右边距）
        readonly UiScroll _sc = new UiScroll();
        Label _scrollHint;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Blur.Supported)
            {
                BackColor = Color.FromArgb(240, 247, 248, 251);
                try { Blur.Apply(Handle, 232, 246, 248, 252, true); } catch { }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);
        }

        // 滚轮上下翻内容（窗口本身不滚动，只是整块挪）
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _sc.Wheel(e.Delta);
            base.OnMouseWheel(e);
        }

        public GuideForm() : this(AppInfo.Name + " 快照轮环 · 使用说明", Lang.T("轮盘平时就待在屏幕角落里，用鼠标滚轮就能翻图。", "The ring sits in a screen corner; scroll the mouse wheel over it to browse."), false) { }

        // firstEver=true：全新安装的欢迎引导；false：升级后自动弹的"这次多了什么"
        public GuideForm(bool firstEver) : this(
            firstEver ? Lang.T("欢迎用 SnapWheel 快照轮环", "Welcome to SnapWheel") : (Lang.T("SnapWheel 更新到 v", "SnapWheel updated to v") + AppInfo.Version),
            firstEver ? Lang.T("轮盘平时就待在屏幕角落里，用鼠标滚轮就能翻图。", "The ring sits in a screen corner; scroll the mouse wheel over it to browse.")
                      : Lang.T("这次加了新东西 —— 下面标了「新」的两条就是，一分钟看完就能用上。", "Something new in this version - the two items marked [NEW] below; a minute to read and you are using them."),
            !firstEver) { }

        GuideForm(string title, string subtitle, bool markNew)
        {
            Text = AppInfo.Name + " 新手上路";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Ui.S(WinW), Ui.S(200));     // 先占位，最后按内容重算
            SuspendLayout();

            Rectangle wa = WorkArea();
            int winW = Ui.S(WinW);
            int maxW = (int)(wa.Width * 0.92) - Ui.S(16);     // 别顶满屏幕，留点边
            if (winW > maxW) winW = Math.Max(Ui.S(320), maxW);
            int mL = Ui.S(PadL), mR = Ui.S(PadR);
            _contentW = winW - mL - mR;

            Label head = new Label();
            head.Text = title;
            head.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.Location = new Point(mL, Ui.S(PadT));
            W(head, _contentW);
            Add2(head);

            int y = head.Top + head.PreferredSize.Height + Ui.S(8);

            Label sub = new Label();
            sub.Text = subtitle;
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.Location = new Point(mL + Ui.S(3), y);
            W(sub, _contentW - Ui.S(3));
            Add2(sub);

            y += TxtH(sub, _contentW - Ui.S(3)) + Ui.S(16);

            string nw = markNew ? Lang.T("【新】", "[NEW]") : "";
            AddTip(mL, ref y, Lang.T("第 1 步：截一张", "Step 1: capture"), Lang.T("按 ", "Press ") + Settings.Load().Hotkey + " 拖框选区域，四角缩放、拖旋转键转角度，双击/回车确认。");
            AddTip(mL, ref y, nw + Lang.T("截完直接标注", "Annotate right after capturing"), Lang.T("浮层上有条工具条：箭头 / 方框 / 马赛克 / 文字，四个颜色可选，Ctrl+Z 撤销。确认之后标注就跟着图一起进轮盘 —— 圈重点不用再去别的软件。", "The overlay has a toolbar: arrow / box / mosaic / text, four colours, Ctrl+Z to undo. Annotations are baked into the image that lands in the ring - no separate editor needed."));
            AddTip(mL, ref y, Lang.T("第 2 步：拖出去（最常用）", "Step 2: drag it out (the everyday use)"), Lang.T("把环上的缩略图直接拖进微信 / QQ / 文档 / 文件夹，松开就发出去 —— 不用先保存、再选文件。这一下就是它的全部意义。", "Drag a thumbnail straight into WeChat / Word / a folder and release - no saving, no picking files. That is the whole point."));
            AddTip(mL, ref y, nw + Lang.T("要对照着看：贴到屏幕上", "Need a reference? Pin it on screen"), Lang.T("缩略图上按一下鼠标中键（就是滚轮键），这张图就钉在屏幕上了：滚轮缩放、拖着挪位置、双击或 Esc 关掉。写东西时对着参考图很方便。", "Middle-click a thumbnail to pin that image on screen: scroll to zoom, drag to move, double-click or Esc to close. Handy when writing against a reference."));
            AddTip(mL, ref y, Lang.T("反过来：拖回来", "Or the other way: drag it back"), Lang.T("从桌面、网页、聊天窗口里把图片拖到环带上松手，就收进轮盘了，随时能再拖出去。", "Drop an image from the desktop, a web page or a chat window onto the ring to keep it - drag it out again whenever you need it."));
            AddTip(mL, ref y, Lang.T("连拖都不用：复制即收纳", "Do not even drag: copy and it is collected"), Lang.T("在任何地方「复制」一张图（截图工具、网页右键、微信里都行），它会自动滑进轮盘。不想要可以在设置里关掉。", "Copy an image anywhere (a screenshot tool, a web page, WeChat) and it slides into the ring. Turn this off in settings if you do not want it."));
            AddTip(mL, ref y, Lang.T("按住看大图", "Hold to zoom"), Lang.T("缩略图按住约 0.3 秒放大预览，放大倍数在设置里可调。", "Hold a thumbnail for ~0.3 s to preview it enlarged; the zoom factor is adjustable in settings."));
            AddTip(mL, ref y, Lang.T("万能键（可以改成你要的）", "Universal key (rebindable)"), Lang.T("长按环内侧那个圆盘会弹出四个方向，往哪个方向松手就执行哪个动作。默认：上=新建轮盘，右=下一个，下=删除，左=上一个 —— 四个动作都能在设置里换。", "Long-press the dial inside the ring and four directions appear; release towards one to run that action. Defaults: up = new wheel, right = next, down = delete, left = previous - all rebindable in settings."));
            AddTip(mL, ref y, Lang.T("收起态（默认关）", "Collapsed mode (off by default)"), Lang.T("打开后不用时会缩成屏幕边上的小把手，点一下用彩虹动画拉出来。想让桌面更干净再开。", "When idle it shrinks into a small pull-tab at the screen edge; click it and the ring slides back out. Turn on for a tidier desktop."));
            AddTip(mL, ref y, Lang.T("托盘", "Tray"), Lang.T("托盘右键还有：导入图片、新手引导、重播开启动画、设置、退出。", "The tray menu also has: import images, getting started, replay startup animation, settings, exit."));

            int tipW = _contentW - Ui.S(3);
            Label tip = new Label();
            tip.Text = Lang.T("小提示：如果拖图片拖不进去，检查是不是用「以管理员身份运行」启动的（Windows 会拦掉跨权限的拖拽）。", "Tip: if you cannot drag images in, check whether SnapWheel was started as administrator (Windows blocks cross-privilege dragging).");
            tip.ForeColor = Color.FromArgb(168, 122, 36);
            tip.Location = new Point(mL + Ui.S(3), y);
            W(tip, tipW);
            Add2(tip);
            y += TxtH(tip, tipW);

            int contentH = y;                                  // 内容理想高度
            int btnRowH = Ui.S(10) + Ui.S(BtnH) + Ui.S(22);     // 底部按钮行占的高度（含上下留白）

            int btnY = contentH + Ui.S(10);                    // 按钮的"内容坐标"
            RoundButton go = new RoundButton();
            go.Text = Lang.T("开始使用", "Get started");
            go.Size = Ui.Sz(124, BtnH);
            go.Fill = Color.FromArgb(0, 122, 204);
            go.FillHover = Color.FromArgb(0, 140, 232);
            go.TextColor = Color.White;
            go.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            go.Location = new Point(winW - mR - go.Width, btnY);
            go.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Add2Raw(go);                    // 贴底固定：**不**参与滚动，滚到哪儿都看得见
            AcceptButton = go;

            _scrollHint = new Label();
            _scrollHint.Text = Lang.T("（内容较多，鼠标滚轮可上下翻看）", "(long page - use the mouse wheel to scroll)");
            _scrollHint.ForeColor = Color.FromArgb(150, 152, 160);
            _scrollHint.AutoSize = true;
            _scrollHint.Location = new Point(mL, btnY + (go.Height - _scrollHint.Font.Height) / 2);
            Add2Raw(_scrollHint);

            // ---- 定尺寸：按内容长高，但绝不超过屏幕工作区 ----
            int idealH = contentH + btnRowH;
            int maxH = wa.Height - Ui.S(24);
            if (maxH < Ui.S(260)) maxH = Ui.S(260);
            int winH = Math.Min(idealH, maxH);
            ClientSize = new Size(winW, winH);

            int viewH = winH - btnRowH;                        // 内容区能露出来的高度
            _sc.Finish(contentH, viewH);
            _scrollHint.Visible = _sc.Active;

            // 按钮/提示固定在底边（不随内容滚动）
            go.Top = winH - Ui.S(22) - go.Height;
            _scrollHint.Top = go.Top + (go.Height - _scrollHint.Font.Height) / 2;

            ResumeLayout();
        }

        // 登记一个控件：既加入窗口，也告诉滚动器它的原始位置
        void Add2(Control c)
        {
            Controls.Add(c);
            _sc.Add(c);
        }

        // 固定贴底的东西（按钮、滚动提示）：加进窗口但**不**参与滚动
        void Add2Raw(Control c) { Controls.Add(c); }

        // 会折行的标签
        static void W(Label l, int maxW) { Ui.Wrap(l, maxW); }

        // 一条说明：粗体小标题 + 一段正文，两行都自己折行、自己报高度
        void AddTip(int x, ref int y, string title, string body)
        {
            int mkW = _contentW - Ui.S(3);
            Label t = new Label();
            t.Text = "• " + title;
            t.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            t.ForeColor = Color.FromArgb(0, 110, 190);
            t.Location = new Point(x + Ui.S(3), y);
            W(t, mkW);
            Add2(t);
            y += TxtH(t, mkW) + Ui.S(4);

            int bw = _contentW - Ui.S(Gutter);
            Label b = new Label();
            b.Text = body;
            b.ForeColor = Color.FromArgb(70, 74, 84);
            b.Location = new Point(x + Ui.S(Gutter), y);
            W(b, bw);
            Add2(b);
            y += TxtH(b, bw) + Ui.S(RowGap);
        }

        // 折行后的真实高度：两种量法取大的那个（宁可多留一点空白，也不切字）
        static int TxtH(Label l, int maxW)
        {
            int h = l.PreferredSize.Height;
            int h2 = Ui.TextH(l, l.Text, maxW);
            return Math.Max(Math.Max(h, h2), l.Font.Height);
        }

        // 窗口该待在哪个屏幕上：跟着鼠标走（多屏时不会跑到另一块屏的边角上）
        internal static Rectangle WorkArea()
        {
            try { return Screen.FromPoint(Cursor.Position).WorkingArea; }
            catch
            {
                try { return Screen.PrimaryScreen.WorkingArea; }
                catch { return new Rectangle(0, 0, 1024, 768); }
            }
        }
    }

    // 管理员模式提示窗口（拖拽被 UIPI 拦掉时弹的那个）。
    // ⚠️ DPI：同上 —— 原来那段说明是写死 506×40 的 Label，150% 下会被裁掉一半。
    class AdminForm : Form
    {
        const int WinW = 560;
        const int PadL = 28, PadR = 28, PadT = 24;
        const int Gutter = 16;
        const int RowGap = 14;
        const int BtnH = 38;

        int _contentW;
        readonly UiScroll _sc = new UiScroll();

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Blur.Supported)
            {
                BackColor = Color.FromArgb(240, 247, 248, 251);
                try { Blur.Apply(Handle, 232, 246, 248, 252, true); } catch { }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _sc.Wheel(e.Delta);
            base.OnMouseWheel(e);
        }

        public AdminForm()
        {
            Text = AppInfo.Name + " 管理员模式";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Ui.S(WinW), Ui.S(200));     // 先占位，最后按内容重算
            SuspendLayout();

            Rectangle wa = GuideForm.WorkArea();
            int winW = Ui.S(WinW);
            int maxW = (int)(wa.Width * 0.92) - Ui.S(16);
            if (winW > maxW) winW = Math.Max(Ui.S(320), maxW);
            int mL = Ui.S(PadL), mR = Ui.S(PadR);
            _contentW = winW - mL - mR;

            Label head = new Label();
            head.Text = Lang.T("管理员模式下，拖拽会被 Windows 拦住", "In administrator mode Windows blocks dragging");
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.Location = new Point(mL, Ui.S(PadT));
            Add2(head, _contentW);

            int y = head.Top + head.PreferredSize.Height + Ui.S(8);

            Label sub = new Label();
            sub.Text = Lang.T("不是 SnapWheel 的毛病，是系统的安全限制（UIPI）：管理员进程和普通程序（资源管理器、微信、浏览器）之间不允许互相拖拽。", "This is not SnapWheel's fault - it is a Windows security rule (UIPI): dragging between an elevated process and normal programs (Explorer, WeChat, browsers) is blocked.");
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.Location = new Point(mL + Ui.S(3), y);
            Add2(sub, _contentW - Ui.S(3));

            y += TxtH(sub, _contentW - Ui.S(3)) + Ui.S(16);

            AddTip(mL, ref y, Lang.T("想拖拽 → 换普通权限", "Want dragging? Switch to normal permissions"), Lang.T("点下面那个按钮：SnapWheel 会先退出，再由资源管理器用普通权限重新启动。设置、轮盘、存的图片都不受影响。", "Click the button below: SnapWheel exits, then Explorer restarts it with normal permissions. Settings, wheels and saved images are untouched."));
            AddTip(mL, ref y, Lang.T("不换权限也能用", "Works without changing permissions"), Lang.T("托盘右键「导入图片…」能直接选文件收进轮盘；在任何地方「复制」一张图，它也会自动滑进来 —— 这两个都不受权限影响。", "Tray -> Import images... puts files straight into the ring; and copying an image anywhere slides it in automatically. Neither is affected by permissions."));
            AddTip(mL, ref y, Lang.T("什么时候才需要管理员", "When do you actually need administrator?"), Lang.T("只有要截「管理员窗口」（任务管理器、某些安装程序）时才需要；平时用普通权限最省事，拖拽也正常。", "Only needed to capture elevated windows (Task Manager, some installers). Normal permissions are simpler day to day and dragging works."));

            int contentH = y;
            int btnRowH = Ui.S(10) + Ui.S(BtnH) + Ui.S(22);
            int right = winW - mR;
            int btnY = contentH + Ui.S(10);

            RoundButton go = new RoundButton();
            go.Text = Lang.T("以普通权限重启", "Restart with normal permissions");
            go.Size = Ui.Sz(150, BtnH);
            go.Fill = Color.FromArgb(0, 122, 204);
            go.FillHover = Color.FromArgb(0, 140, 232);
            go.TextColor = Color.White;
            go.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            go.Location = new Point(right - go.Width, btnY);
            go.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Add2Raw(go);
            AcceptButton = go;

            RoundButton no = new RoundButton();
            no.Text = Lang.T("知道了", "Got it");
            no.Size = Ui.Sz(104, BtnH);
            no.Fill = Color.FromArgb(238, 240, 245);
            no.FillHover = Color.FromArgb(226, 230, 238);
            no.TextColor = Color.FromArgb(60, 64, 74);
            no.Font = new Font("Microsoft YaHei UI", 10f);
            no.Location = new Point(go.Left - Ui.S(12) - no.Width, btnY);
            no.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });
            Add2Raw(no);
            CancelButton = no;

            int idealH = contentH + btnRowH;
            int maxH = wa.Height - Ui.S(24);
            if (maxH < Ui.S(260)) maxH = Ui.S(260);
            int winH = Math.Min(idealH, maxH);
            ClientSize = new Size(winW, winH);

            _sc.Finish(contentH, winH - btnRowH);

            go.Top = winH - Ui.S(22) - go.Height;
            no.Top = go.Top;
            ResumeLayout();
        }

        // 折行标签：加进窗口 + 登记滚动
        void Add2(Label l, int maxW)
        {
            Ui.Wrap(l, maxW);
            Controls.Add(l);
            _sc.Add(l);
        }

        // 固定贴底的东西（按钮）：加进窗口但**不**参与滚动
        void Add2Raw(Control c) { Controls.Add(c); }

        void AddTip(int x, ref int y, string title, string body)
        {
            int mkW = _contentW - Ui.S(3);
            Label t = new Label();
            t.Text = "• " + title;
            t.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            t.ForeColor = Color.FromArgb(0, 110, 190);
            t.Location = new Point(x + Ui.S(3), y);
            Add2(t, mkW);
            y += TxtH(t, mkW) + Ui.S(4);

            int bw = _contentW - Ui.S(Gutter);
            Label b = new Label();
            b.Text = body;
            b.ForeColor = Color.FromArgb(70, 74, 84);
            b.Location = new Point(x + Ui.S(Gutter), y);
            Add2(b, bw);
            y += TxtH(b, bw) + Ui.S(RowGap);
        }

        static int TxtH(Label l, int maxW)
        {
            int h = l.PreferredSize.Height;
            int h2 = Ui.TextH(l, l.Text, maxW);
            return Math.Max(Math.Max(h, h2), l.Font.Height);
        }
    }

    class HotkeyForm : Form
    {
        public event EventHandler Hotkey;
        public bool Registered = false;

        public HotkeyForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-2000, -2000);
            Size = new Size(1, 1);
            IntPtr h = Handle;
        }

        public bool Register(uint mods, uint vk)
        {
            try { Native.UnregisterHotKey(Handle, Native.HOTKEY_ID); } catch { }
            bool ok = Native.RegisterHotKey(Handle, Native.HOTKEY_ID, mods | Native.MOD_NOREPEAT, vk);
            Registered = ok;
            return ok;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == Native.HOTKEY_ID)
                if (Hotkey != null) Hotkey(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }
    }
}
