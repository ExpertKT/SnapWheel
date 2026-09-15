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
    // 多轮盘管理窗口：左边一列子轮盘，右边是「名称 / 颜色 / 新建 / 删除 / 完成」。
    //
    // ⚠️ DPI（本次修订）：这个窗口原来**坐标、尺寸、行高全是写死的像素**，而程序是 per-monitor DPI aware 的 ——
    // 150% 缩放下字体由 GDI+ 按 DPI 放大 1.5 倍渲染，格子却还是原来那么大，于是：
    //   · 列表行高写死 26，装不下放大后的字 → 每一行的名字上下都被切（这就是用户报的「显示不全」）；
    //   · 右列排在写死的 x=412 处，窗口却是写死的 430 宽 → 「新建 / 删除 / 完成」被挤出可视区；
    //   · 底部那句提示是 AutoSize=false 的死格子 (16,276,260,22) → 一行放不下就只剩半句。
    // 现在的规矩（和 75-SettingsForm / 80-Dialogs 一模一样，**别再发明第二套**）：
    //   · 长度（坐标 / 宽 / 高 / 行高 / 边距 / 按钮尺寸）一律过 Ui.S() 乘 DPI 系数；
    //   · **字体磅值一个都不乘** —— GDI+ 已经按 DPI 渲染过一遍，再乘就是双倍放大；
    //   · 说明性文字的 Label 交给 Ui.Wrap()：自己折行、自己报 PreferredHeight，比"把高度算准"可靠；
    //   · 窗口 ClientSize **最后**按内容重算一次，内容多高窗口就多高，任何 DPI 下都不裁。
    // 版面写法保持原样：还是手写坐标 + 常量，**没有**引入 TableLayoutPanel / FlowLayoutPanel
    // （这个项目在布局重构上翻过车，见 0.4.x 那次）。这次只动"数字怎么来的"，不动"谁排在哪"。
    class WheelsForm : Form
    {
        // ---- 版面常量（逻辑像素；用的时候一律过 Ui.S() 乘 K）----
        // 这几个数字就是原来写死在代码里的那些值，原样搬过来当"逻辑像素"用 ——
        // 这样 100% 缩放下算出来的结果和改之前一模一样，改动只在高 DPI 下才生效，出问题好对账。
        const int WinW = 430, WinH = 330;   // 原 ClientSize；乘 K 之后当窗口下限
        const int PadL = 16, PadT = 16;     // 内容左上边距（原 (16,16)）
        const int PadR = 18, PadB = 32;     // 内容右下边距（原右列右沿 412 → 430-412=18；提示底 298 → 330-298=32）
        const int ListW = 240, ListH = 250; // 左侧列表（250 / 26 ≈ 9.6 行，可见行数和以前一致）
        const int ColX = 272;               // 右列起点（原写死 272）
        const int ColW = 140;               // 右列控件宽（272+140 = 412，正好顶到右边距）
        const int BoxH = 24;                // 输入框 / 下拉框的版面高（原写死 24）
        const int RowH = 26;                // 列表行高 —— **必须**过 Ui.S()，写死 26 就是"行里文字被切"的元凶
        const int BtnW = 66, BtnH = 30;     // 「新建 / 删除」小按钮（原写死 66×30）
        const int CloseH = 34;              // 「完成」按钮高（原写死 34）
        const int Lab1Y = 18, Box1Y = 40;   // 「名称」标签 / 输入框的 y（原写死）
        const int Lab2Y = 74, Box2Y = 96;   // 「颜色」标签 / 下拉框的 y（原写死）
        const int BtnY = 136;               // 「新建 / 删除」那一行的 y（原写死）
        const int CloseY = 178;             // 「完成」的 y（原写死）
        const int GapTip = 10;              // 列表底 → 提示文字的间距（原 266 → 276）

        WheelManager _mgr;
        ListBox _list;
        TextBox _name;
        ComboBox _color;
        bool _loading = false;

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

        public WheelsForm(WheelManager mgr)
        {
            _mgr = mgr;
            Text = Lang.T("管理 Wheel", "Manage wheels");
            Icon = Brand.Get();
            Font = new Font("Microsoft YaHei UI", 9.5f);        // 磅值不动：GDI+ 已按 DPI 渲染过一遍
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = Ui.Sz(WinW, WinH);                     // 先按原尺寸占位，构造完最后按内容再算一次

            int mL = Ui.S(PadL), mT = Ui.S(PadT), mR = Ui.S(PadR), mB = Ui.S(PadB);
            int right = Ui.S(WinW) - mR;                        // 右列右沿（原 412），右列控件全部贴它对齐

            _list = new ListBox();
            _list.Bounds = new Rectangle(mL, mT, Ui.S(ListW), Ui.S(ListH));
            _list.IntegralHeight = false;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = Ui.S(RowH);                      // ← 跟着 K 走：不然 150% 下每行的字上下都被切
            _list.DrawItem += new DrawItemEventHandler(OnDrawItem);
            _list.SelectedIndexChanged += new EventHandler(OnSelect);
            Controls.Add(_list);

            // 「名称」「颜色」是单行短标签：原来给的是写死的 60×22 死格子，靠"字比格子小"侥幸没出事。
            // 改成 AutoSize 之后宽度/高度都由字自己报，换一档缩放、换一种字体都不用再回来调数字。
            Label l1 = new Label();
            l1.Text = Lang.T("名称", "Name");
            l1.Location = Ui.Pt(ColX, Lab1Y);
            Ui.OneLine(l1);
            Controls.Add(l1);

            _name = new TextBox();
            _name.Bounds = new Rectangle(Ui.S(ColX), Ui.S(Box1Y), Ui.S(ColW), Ui.S(BoxH));
            _name.TextChanged += new EventHandler(OnNameChanged);
            Controls.Add(_name);

            Label l2 = new Label();
            l2.Text = Lang.T("颜色", "Colour");
            l2.Location = Ui.Pt(ColX, Lab2Y);
            Ui.OneLine(l2);
            Controls.Add(l2);

            _color = new ComboBox();
            _color.DropDownStyle = ComboBoxStyle.DropDownList;
            _color.Bounds = new Rectangle(Ui.S(ColX), Ui.S(Box2Y), Ui.S(ColW), Ui.S(BoxH));
            for (int i = 0; i < Palette.Names.Length; i++) _color.Items.Add(Palette.Names[i]);
            _color.SelectedIndexChanged += new EventHandler(OnColorChanged);
            Controls.Add(_color);

            RoundButton add = new RoundButton();
            add.Text = Lang.T("新建", "New"); add.Size = Ui.Sz(BtnW, BtnH);
            add.Fill = Color.FromArgb(0, 122, 204); add.FillHover = Color.FromArgb(0, 140, 232);
            add.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);   // 磅值不动
            add.Location = Ui.Pt(ColX, BtnY);
            add.Click += new EventHandler(delegate(object o, EventArgs e2) { _mgr.New(); _mgr.Save(); Reload(_mgr.Wheels.Count - 1); });
            Controls.Add(add);

            RoundButton del = new RoundButton();
            del.Text = Lang.T("删除", "Delete"); del.Size = Ui.Sz(BtnW, BtnH);
            del.Fill = Color.FromArgb(214, 70, 84); del.FillHover = Color.FromArgb(230, 90, 104);
            del.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);   // 磅值不动
            // 右对齐到列尾：原来写死 346（= 272+140-66）也对，但那样是"数值凑巧对"，
            // 用 ColX+ColW-BtnW 表达之后，改列宽/改按钮宽都不会再跑偏。
            del.Location = Ui.Pt(ColX + ColW - BtnW, BtnY);
            del.Click += new EventHandler(delegate(object o, EventArgs e2) {
                if (_list.SelectedIndex < 0) return;
                if (MessageBox.Show(Lang.T("确定删除 Wheel「", "Delete wheel \"") + _mgr.Wheels[_list.SelectedIndex].Name + Lang.T("」及其截图？", "\" and its screenshots?"),
                        Lang.T("删除 Wheel", "Delete wheel"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                _mgr.Remove(_list.SelectedIndex); _mgr.Save(); Reload(Math.Min(_list.SelectedIndex, _mgr.Wheels.Count - 1));
            });
            Controls.Add(del);

            RoundButton close = new RoundButton();
            close.Text = Lang.T("完成", "Done"); close.Size = Ui.Sz(ColW, CloseH);   // 宽度 = 整列宽（原写死 140，正好等于 ColW）
            close.Fill = Color.FromArgb(233, 234, 238); close.FillHover = Color.FromArgb(222, 224, 230);
            close.TextColor = Color.FromArgb(58, 60, 66);
            close.Font = new Font("Microsoft YaHei UI", 9.5f);      // 磅值不动
            close.Location = Ui.Pt(ColX, CloseY);
            close.Click += new EventHandler(delegate(object o, EventArgs e2) { _mgr.Save(); DialogResult = DialogResult.OK; Close(); });
            Controls.Add(close);

            Label tip = new Label();
            tip.Text = Lang.T("点一下左侧即可切换为当前 Wheel", "Click one on the left to make it the active wheel");
            tip.ForeColor = Color.FromArgb(150, 150, 158);
            // 原来是 AutoSize=false 的死格子 (16,276,260,22)：一行放不下就只剩半句。现在交给 Wrap()，
            // 让它自己折行、自己报高度。竖直位置也不再写死 276，而是吊在"列表和右列谁更低"的下面 ——
            // 原来 276 是靠"右列肯定比列表矮"这个巧合才没被压住，右列被 DPI 撑高之后这个巧合就没了。
            tip.Location = new Point(mL, Math.Max(_list.Bottom, close.Bottom) + Ui.S(GapTip));
            Ui.Wrap(tip, right - mL);                           // 可用宽 = 窗口内容宽（已乘过 K 的物理像素）
            Controls.Add(tip);

            // 窗口尺寸**最后**按内容重算一次（原来是写死的 430×330）：内容多高窗口就多高，绝不裁。
            // 再和"原尺寸 × K"取大值 —— 这是下限，保证 100% 缩放下和改之前完全一样，不会看着变小。
            int needW = Math.Max(_list.Right, Math.Max(close.Right, tip.Right)) + mR;
            int needH = tip.Bottom + mB;
            ClientSize = new Size(Math.Max(Ui.S(WinW), needW), Math.Max(Ui.S(WinH), needH));

            Reload(_mgr.Active);
        }

        void Reload(int sel)
        {
            _loading = true;
            _list.Items.Clear();
            for (int i = 0; i < _mgr.Wheels.Count; i++)
                _list.Items.Add((i == _mgr.Active ? "● " : "   ") + _mgr.Wheels[i].Name);
            if (sel >= 0 && sel < _list.Items.Count) _list.SelectedIndex = sel;
            _loading = false;
            SyncFields();
        }

        void SyncFields()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _mgr.Wheels.Count) return;
            _loading = true;
            _name.Text = _mgr.Wheels[i].Name;
            _color.SelectedIndex = Math.Abs(_mgr.Wheels[i].ColorIndex) % Palette.Names.Length;
            _loading = false;
        }

        void OnSelect(object sender, EventArgs e)
        {
            if (_loading) return;
            int i = _list.SelectedIndex;
            if (i < 0) return;
            _mgr.Active = i;
            _mgr.Save();
            Reload(i);
        }

        void OnNameChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _mgr.Wheels.Count) return;
            _mgr.Wheels[i].Name = _name.Text;
            _list.Items[i] = (i == _mgr.Active ? "● " : "   ") + _name.Text;
            _mgr.ApplySettings();
            _mgr.Save();
        }

        void OnColorChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _mgr.Wheels.Count) return;
            _mgr.Wheels[i].ColorIndex = _color.SelectedIndex;
            _mgr.Save();
            _list.Invalidate();
        }

        // 自绘列表的每一行：色点 + 名字。
        // 自绘里的常量**同样**要过 Ui.S()：行高已经按 DPI 放大了，色点 12 / 偏移 6 / 文字 26 却写死的话，
        // 150% 下色点会缩在行左上角、文字也比行高大 —— 上下被切。
        // 竖直位置干脆不再写死魔数，改成"在行高里居中"：行高、字体、缩放任一个变了都自动跟着走。
        void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            Wheel w = _mgr.Wheels[e.Index];
            int d = Ui.S(12);                                   // 色点直径（原写死 12）
            int ey = e.Bounds.Top + Math.Max(0, (e.Bounds.Height - d) / 2);        // 原写死 +7
            using (SolidBrush b = new SolidBrush(w.Accent))
                e.Graphics.FillEllipse(b, e.Bounds.Left + Ui.S(6), ey, d, d);      // 原写死 +6
            int ty = e.Bounds.Top + Math.Max(0, (e.Bounds.Height - e.Font.Height) / 2);   // 原写死 +5
            using (SolidBrush t = new SolidBrush(e.ForeColor))
                e.Graphics.DrawString(_list.Items[e.Index].ToString(), e.Font, t, e.Bounds.Left + Ui.S(26), ty);  // 原写死 +26
        }
    }

    // 改名字的小窗口（点轮盘上的名字药丸弹出来的那个）。
    //
    // ⚠️ DPI（本次修订）：原来 ClientSize 写死 360×128、输入框写死 250 宽、两个按钮写死 104/90×34、
    // 按钮位置还用 ClientSize 现算（width-24-104 / height-46）—— 150% 下字体放大 1.5 倍之后：
    //   · 上面那行说明文字会顶出窗口右边缘（AutoSize 只保证"不裁字"，不保证"不出界"，得给它折行上限）；
    //   · 输入框和按钮挤在一起、按钮被窗口下沿裁掉半截。
    // 规矩和 WheelsForm 完全一致：长度过 Ui.S()、字体磅值不动、文字用 Wrap()、窗口尺寸最后按内容重算。
    class RenameForm : Form
    {
        public const int MaxName = 12;      // 名字最长 12 个字（药丸宽度可控）
        public string Value = "";
        TextBox _box;

        // ---- 版面常量（逻辑像素；用的时候一律过 Ui.S() 乘 K）----
        const int WinW = 360, WinH = 128;   // 原 ClientSize；乘 K 之后当窗口下限
        const int PadL = 22, PadR = 24, PadT = 18;
        const int LabDX = 2;                // 输入框相对说明文字右挪 2px（原来 22 → 24，视觉上更好对齐标题）
        const int BoxW = 250;               // 输入框宽（原写死 250）
        const int GapBox = 9;               // 说明文字下沿 → 输入框（原来 18+17 的文字底 → 44）
        const int GapBtn = 14;              // 输入框下沿 → 按钮行（原来 68 → 82）
        const int GapEnd = 12;              // 按钮下沿 → 窗口下沿（原来 116 → 128）
        const int OkW = 104, CancelW = 90;  // 两个按钮宽（原写死）
        const int BtnH = 34;                // 按钮高（原写死）
        const int BtnGap = 10;              // 两个按钮之间（原写死 10）

        public RenameForm(string cur)
        {
            Text = Lang.T("给这个 Wheel 起个名", "Name this wheel");
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);        // 磅值不动：GDI+ 已按 DPI 渲染过一遍
            BackColor = Color.FromArgb(250, 250, 252);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = Ui.Sz(WinW, WinH);                     // 先按原尺寸占位，最后按内容再算一次

            int mL = Ui.S(PadL), mR = Ui.S(PadR), mT = Ui.S(PadT);
            int winW = Ui.S(WinW);
            int right = winW - mR;                              // 内容右沿，按钮全部贴它对齐

            Label l = new Label();
            l.Text = Lang.T("名字（最多 ", "Name (up to ") + MaxName + Lang.T(" 个字，会显示在轮盘上）", " characters, shown on the ring)");
            l.ForeColor = Color.FromArgb(110, 114, 124);
            l.Location = new Point(mL, mT);
            Ui.Wrap(l, right - mL);      // 给折行上限：光 AutoSize 的话，字比窗口宽就直接伸出窗口被裁掉
            Controls.Add(l);

            _box = new TextBox();
            _box.Text = cur;
            _box.Font = new Font("Microsoft YaHei UI", 11f);    // 磅值不动
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.Width = Ui.S(BoxW);                            // 高度是字体驱动的，不用管也别写死
            _box.MaxLength = MaxName;      // 直接限制输入长度，避免打到超长
            // 位置按"说明文字的真实下沿"往下推，不写死 44：文字因缩放换了一档、或者多折了一行，
            // 都不会压到输入框上（写死 44 时这两件事任意一件发生就会重叠）。
            _box.Location = new Point(mL + Ui.S(LabDX), l.Bottom + Ui.S(GapBox));
            Controls.Add(_box);

            int yBtn = _box.Bottom + Ui.S(GapBtn);              // 同理：按钮行吊在输入框下面，不写死

            RoundButton ok = new RoundButton();
            ok.Text = Lang.T("改好了", "Renamed");
            ok.Size = Ui.Sz(OkW, BtnH);
            ok.Fill = Color.FromArgb(0, 122, 204);
            ok.FillHover = Color.FromArgb(0, 140, 232);
            ok.TextColor = Color.White;
            ok.Primary = true;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);   // 磅值不动
            // 右对齐到内容右沿：原来那版用的是"当前 ClientSize - 24 - 104"，
            // 而 ClientSize 这时还没按内容重算，等于拿旧尺寸定位 —— 改成用目标窗口宽算，稳。
            ok.Location = new Point(right - ok.Width, yBtn);
            ok.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                Value = _box.Text.Trim();
                if (Value == null || Value.Length == 0) Value = cur;
                if (Value.Length > MaxName) Value = Value.Substring(0, MaxName);   // 名字太长会把药丸撑宽、挡住旁边
                DialogResult = DialogResult.OK;
                Close();
            });
            Controls.Add(ok);

            RoundButton cancel = new RoundButton();
            cancel.Text = Lang.T("取消", "Cancel");
            cancel.Size = Ui.Sz(CancelW, BtnH);
            cancel.Fill = Color.FromArgb(234, 235, 240);
            cancel.FillHover = Color.FromArgb(222, 224, 230);
            cancel.TextColor = Color.FromArgb(58, 60, 66);
            cancel.Font = new Font("Microsoft YaHei UI", 10f);  // 磅值不动
            cancel.Location = new Point(ok.Left - Ui.S(BtnGap) - cancel.Width, yBtn);
            cancel.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            // 窗口尺寸**最后**按内容重算一次（原来是写死的 360×128）：内容多宽多高，窗口就多大，绝不裁。
            // 同样和"原尺寸 × K"取大值当下限 —— 100% 缩放下和改之前一致，高 DPI 下只可能变大、不会裁。
            // 说明文字不用参与：它的宽度已经被上面的 Wrap(right-mL) 卡死在内容区里，不可能顶出窗口
            int needW = Math.Max(_box.Right, Math.Max(ok.Right, cancel.Right)) + mR;
            ClientSize = new Size(Math.Max(winW, needW), Math.Max(Ui.S(WinH), ok.Bottom + Ui.S(GapEnd)));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);
            _box.Focus();
            _box.SelectAll();
        }
    }
}
