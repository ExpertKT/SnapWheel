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
    class GuideForm : Form
    {
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

        public GuideForm() : this(AppInfo.Name + " 快照轮环 · 使用说明", "轮盘平时就待在屏幕角落里，用鼠标滚轮就能翻图。", false) { }

        // firstEver=true：全新安装的欢迎引导；false：升级后自动弹的"这次多了什么"
        public GuideForm(bool firstEver) : this(
            firstEver ? "欢迎用 SnapWheel 快照轮环" : ("SnapWheel 更新到 v" + AppInfo.Version),
            firstEver ? "轮盘平时就待在屏幕角落里，用鼠标滚轮就能翻图。"
                      : "这次加了新东西 —— 下面标了「新」的两条就是，一分钟看完就能用上。",
            !firstEver) { }

        GuideForm(string title, string subtitle, bool markNew)
        {
            Text = AppInfo.Name + " 新手上路";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(540, 100);         // 先占位，最后按内容重算
            SuspendLayout();

            Label head = new Label();
            head.Text = title;
            head.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.AutoSize = true;
            head.Location = new Point(28, 24);
            Controls.Add(head);

            Label sub = new Label();
            sub.Text = subtitle;
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.AutoSize = true;
            sub.Location = new Point(31, 60);
            Controls.Add(sub);

            int y = 100;
            string nw = markNew ? "【新】" : "";
            AddTip(28, ref y, "第 1 步：截一张", "按 " + Settings.Load().Hotkey + " 拖框选区域，四角缩放、拖旋转键转角度，双击/回车确认。");
            AddTip(28, ref y, nw + "截完直接标注", "浮层上有条工具条：箭头 / 方框 / 马赛克 / 文字，四个颜色可选，Ctrl+Z 撤销。确认之后标注就跟着图一起进轮盘 —— 圈重点不用再去别的软件。");
            AddTip(28, ref y, "第 2 步：拖出去（最常用）", "把环上的缩略图直接拖进微信 / QQ / 文档 / 文件夹，松开就发出去 —— 不用先保存、再选文件。这一下就是它的全部意义。");
            AddTip(28, ref y, nw + "要对照着看：贴到屏幕上", "缩略图上按一下鼠标中键（就是滚轮键），这张图就钉在屏幕上了：滚轮缩放、拖着挪位置、双击或 Esc 关掉。写东西时对着参考图很方便。");
            AddTip(28, ref y, "反过来：拖回来", "从桌面、网页、聊天窗口里把图片拖到环带上松手，就收进轮盘了，随时能再拖出去。");
            AddTip(28, ref y, "连拖都不用：复制即收纳", "在任何地方「复制」一张图（截图工具、网页右键、微信里都行），它会自动滑进轮盘。不想要可以在设置里关掉。");
            AddTip(28, ref y, "按住看大图", "缩略图按住约 0.3 秒放大预览，放大倍数在设置里可调。");
            AddTip(28, ref y, "万能键（可以改成你要的）", "长按环内侧那个圆盘会弹出四个方向，往哪个方向松手就执行哪个动作。默认：上=新建轮盘，右=下一个，下=删除，左=上一个 —— 四个动作都能在设置里换。");
            AddTip(28, ref y, "收起态（默认关）", "打开后不用时会缩成屏幕边上的小把手，点一下用彩虹动画拉出来。想让桌面更干净再开。");
            AddTip(28, ref y, "托盘", "托盘右键还有：导入图片、新手引导、重播开启动画、设置、退出。");

            Label tip = new Label();
            tip.Text = "小提示：如果拖图片拖不进去，检查是不是用「以管理员身份运行」启动的（Windows 会拦掉跨权限的拖拽）。";
            tip.ForeColor = Color.FromArgb(168, 122, 36);
            tip.AutoSize = false;
            tip.Size = new Size(486, 42);
            tip.Location = new Point(31, y + 4);
            Controls.Add(tip);
            y += 52;

            RoundButton go = new RoundButton();
            go.Text = "开始使用";
            go.Size = new Size(124, 38);
            go.Fill = Color.FromArgb(0, 122, 204);
            go.FillHover = Color.FromArgb(0, 140, 232);
            go.TextColor = Color.White;
            go.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            go.Location = new Point(540 - 28 - 124, y + 12);
            go.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Controls.Add(go);
            AcceptButton = go;

            ClientSize = new Size(540, y + 12 + 38 + 24);
            ResumeLayout();
        }

        void AddTip(int x, ref int y, string title, string body)
        {
            Label t = new Label();
            t.Text = "• " + title;
            t.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            t.ForeColor = Color.FromArgb(0, 110, 190);
            t.AutoSize = true;
            t.Location = new Point(x + 3, y);
            Controls.Add(t);

            Label b = new Label();
            b.Text = body;
            b.ForeColor = Color.FromArgb(70, 74, 84);
            b.AutoSize = false;
            b.Size = new Size(486, 20);
            b.Location = new Point(x + 16, y + 21);
            Controls.Add(b);
            y += 51;
        }
    }

    class AdminForm : Form
    {
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
            ClientSize = new Size(560, 100);           // 先占位，最后按内容重算
            SuspendLayout();

            Label head = new Label();
            head.Text = "管理员模式下，拖拽会被 Windows 拦住";
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.AutoSize = true;
            head.Location = new Point(28, 24);
            Controls.Add(head);

            Label sub = new Label();
            sub.Text = "不是 SnapWheel 的毛病，是系统的安全限制（UIPI）：管理员进程和普通程序（资源管理器、微信、浏览器）之间不允许互相拖拽。";
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.AutoSize = false;
            sub.Size = new Size(506, 40);
            sub.Location = new Point(31, 58);
            Controls.Add(sub);

            int y = 108;
            AddTip(28, ref y, "想拖拽 → 换普通权限", "点下面那个按钮：SnapWheel 会先退出，再由资源管理器用普通权限重新启动。设置、轮盘、存的图片都不受影响。");
            AddTip(28, ref y, "不换权限也能用", "托盘右键「导入图片…」能直接选文件收进轮盘；在任何地方「复制」一张图，它也会自动滑进来 —— 这两个都不受权限影响。");
            AddTip(28, ref y, "什么时候才需要管理员", "只有要截「管理员窗口」（任务管理器、某些安装程序）时才需要；平时用普通权限最省事，拖拽也正常。");

            RoundButton go = new RoundButton();
            go.Text = "以普通权限重启";
            go.Size = new Size(150, 38);
            go.Fill = Color.FromArgb(0, 122, 204);
            go.FillHover = Color.FromArgb(0, 140, 232);
            go.TextColor = Color.White;
            go.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            go.Location = new Point(560 - 28 - 150, y + 12);
            go.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Controls.Add(go);
            AcceptButton = go;

            RoundButton no = new RoundButton();
            no.Text = "知道了";
            no.Size = new Size(104, 38);
            no.Fill = Color.FromArgb(238, 240, 245);
            no.FillHover = Color.FromArgb(226, 230, 238);
            no.TextColor = Color.FromArgb(60, 64, 74);
            no.Font = new Font("Microsoft YaHei UI", 10f);
            no.Location = new Point(560 - 28 - 150 - 12 - 104, y + 12);
            no.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });
            Controls.Add(no);
            CancelButton = no;

            ClientSize = new Size(560, y + 12 + 38 + 24);
            ResumeLayout();
        }

        void AddTip(int x, ref int y, string title, string body)
        {
            Label t = new Label();
            t.Text = "• " + title;
            t.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            t.ForeColor = Color.FromArgb(0, 110, 190);
            t.AutoSize = true;
            t.Location = new Point(x + 3, y);
            Controls.Add(t);

            Label b = new Label();
            b.Text = body;
            b.ForeColor = Color.FromArgb(70, 74, 84);
            b.AutoSize = false;
            b.Size = new Size(506, 20);
            b.Location = new Point(x + 16, y + 21);
            Controls.Add(b);
            y += 51;
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
