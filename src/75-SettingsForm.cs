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
    class SettingsForm : Form, IMessageFilter
    {
        // ============================ 布局总则（务必先读） ============================
        // 1. 这个窗口的布局一律"坐标明确"：每张 TableLayoutPanel 都写死 RowCount/ColumnCount，
        //    每个控件都用 Add(控件, 列, 行) 指明格子 —— **绝不靠添加顺序排行**。
        //    v0.5.2 提速时就是因为挪了添加顺序，标题跑到最底下、按钮跑到最上面（"头和屁股长反了"）。
        // 2. 四页内容 = 四张页面格，同一时间只显示一张；每页都是"两列 + 行"的明确坐标。
        // 3. 每页的控件**第一次翻到那页才建**（懒建）：构造量降到 1/4，这是打开设置变快的主因。
        //    没建过的页 = 没被看过 = 没被改过，所以"确定"时跳过它（值保持原样，不会被写回默认值）。
        // ==========================================================================
        TableLayoutPanel _root;
        Panel _body;
        PageDial _dial;
        readonly TableLayoutPanel[] _pages = new TableLayoutPanel[4];
        readonly Action[] _builders = new Action[4];
        readonly bool[] _built = new bool[4];
        int _cur = -1;
        Settings _s;
        bool _filterAdded;

        // ---- 第 1 页「行为与快捷键」 ----
        CheckBox _chkDisk, _chkAutoStart, _chkAuto, _chkTop, _chkClip, _chkBalloon, _chkUpdate, _chkDragFile;
        TextBox _txtDir;
        NumericUpDown _numSec;
        ComboBox _cmbHotkey, _cmbCorner, _cmbDel, _cmbSwitch;
        // ---- 第 2 页「轮盘与外观」 ----
        NumericUpDown _numMax, _numThumb, _numRad, _numSlots, _numLabel, _numPeek;
        ComboBox _cmbScale, _cmbRing, _cmbRing2;
        CheckBox _chkCollapse, _chkSingle, _chkIntroAnim;
        // ---- 第 3 页「风格」 ----
        ComboBox _cmbStyle, _cmbAccent, _cmbAnim;
        CheckBox _chkName, _chkCount, _chkGlassRefresh;
        // ---- 第 4 页「万能键与高级」 ----
        readonly ComboBox[] _keyBox = new ComboBox[4];
        NumericUpDown _numGlass, _numRadius, _numShadow;
        TableLayoutPanel _advR;

        static readonly int[] scVals = { 0, 80, 90, 100, 110, 125, 150, 175, 200, 250 };
        static readonly int[] ringVals = { 220, 150, 100, 80, 60, 45 };
        static readonly string[] ringNames = { "极快", "快", "标准", "慢", "很慢", "最慢" };

        // 应用真·毛玻璃：窗口背景半透明 + 系统 acrylic 模糊；系统不支持就退回不透明浅底
        void ApplyGlass()
        {
            // 对话框用干净的浅色实底：半透明窗体 + 子控件（按钮/输入框）在 Windows 上
            // 容易出现"四角没画到、重绘才恢复"的脏块，实测得不偿失。
            // 真正需要毛玻璃的地方是轮盘本体，那边是自己绘制的，好控制。
            BackColor = Color.FromArgb(248, 249, 252);
            if (_dial != null) _dial.BackColor = BackColor;   // 分页器是自绘控件，底色要跟窗口一模一样
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyGlass();
            // 滚轮翻页：装在应用级过滤器上，鼠标在窗口任何位置滚都算（数值框/下拉框也不会截胡）
            if (!_filterAdded) { try { Application.AddMessageFilter(this); _filterAdded = true; } catch { } }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 自己的定时器必须自己停（v0.5.1 的教训：窗口关了定时器还在跑，白烧 CPU）
                if (_ptimer != null) { try { _ptimer.Stop(); _ptimer.Dispose(); } catch { } _ptimer = null; }
                if (_pwatch != null) { try { _pwatch.Stop(); } catch { } _pwatch = null; }
                if (_filterAdded)
                {
                    try { Application.RemoveMessageFilter(this); } catch { }
                    _filterAdded = false;
                }
            }
            base.Dispose(disposing);
        }

        // 滚轮 = 翻页（设置窗口每页都不滚动，滚轮专门干这个）
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            if (m.Msg == WM_MOUSEWHEEL && Visible && ContainsFocus)
            {
                long w = m.WParam.ToInt64();
                int delta = (int)((w >> 16) & 0xFFFF);
                if (delta > 0x7FFF) delta -= 0x10000;
                if (delta != 0) { TryGoto(_cur + (delta > 0 ? -1 : 1)); return true; }
            }
            return false;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 半透明底：让 acrylic 透出来
            if (Blur.Supported)
            {
                using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
                return;
            }
            base.OnPaintBackground(e);
        }


        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);          // 补一次整窗重绘，按钮四角不会闪白块
        }

        public SettingsForm(Settings s)
        {
            _s = s;
            Text = AppInfo.Name + " 设置  ·  BETA";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(250, 250, 252);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = false;                     // 固定大小：翻页代替滚动，窗口不再随内容长高长胖
            ClientSize = new Size(760, 560);
            Padding = new Padding(20, 14, 20, 12);

            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.AutoSize = false;
            root.Dock = DockStyle.Fill;
            root.Margin = new Padding(0);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));    // 0 标题
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));    // 1 轮盘式分页器
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 2 当前页
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));    // 3 按钮行（右下）
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));    // 4 版本行
            _root = root;

            Label head = new Label();
            head.AutoSize = true;
            head.Text = AppInfo.Name + " 设置";
            head.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(32, 34, 38);
            head.Margin = new Padding(0, 0, 0, 6);
            root.Controls.Add(head, 0, 0);

            // 分页器：顶部小圆弧，四个扇区 = 四页（点扇区 / 滚轮翻页），新拟态凸起 + 当前页高亮
            _dial = new PageDial();
            _dial.Names = new string[] { "行为与快捷键", "轮盘与外观", "风格", "万能键与高级" };
            _dial.BackColor = Color.FromArgb(250, 250, 252);
            _dial.Dock = DockStyle.Fill;
            _dial.Margin = new Padding(0);
            _dial.PagePicked += new EventHandler(delegate(object o, EventArgs e2) { TryGoto(_dial.Picked); });
            root.Controls.Add(_dial, 0, 1);

            // 四张页面格：先建好挂上（空白），内容懒建；非当前页 Visible=false
            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Margin = new Padding(0);
            _body = body;
            for (int i = 0; i < 4; i++)
            {
                TableLayoutPanel pg = NewGrid(2);
                pg.Visible = false;
                _pages[i] = pg;
                body.Controls.Add(pg);
            }
            root.Controls.Add(body, 0, 2);
            _builders[0] = BuildPage1;
            _builders[1] = BuildPage2;
            _builders[2] = BuildPage3;
            _builders[3] = BuildPage4;

            // ---------------- 底部按钮（新手引导 / 还原默认 / 确定 取消）行为一字未改 ----------------
            RoundButton ok = new RoundButton();
            ok.Text = "确定";
            ok.Size = new Size(104, 36);
            ok.Fill = Color.FromArgb(0, 122, 204);
            ok.FillHover = Color.FromArgb(0, 140, 232);
            ok.TextColor = Color.White;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            ok.Margin = new Padding(10, 2, 0, 0);
            ok.Click += new EventHandler(delegate(object o, EventArgs e2) {
                SaveFromUi();
                DialogResult = DialogResult.OK;
                Close();
            });
            RoundButton cancel = new RoundButton();
            cancel.Text = "取消";
            cancel.Size = new Size(104, 36);
            cancel.Fill = Color.FromArgb(234, 235, 240);
            cancel.FillHover = Color.FromArgb(222, 224, 230);
            cancel.TextColor = Color.FromArgb(58, 60, 66);
            cancel.Font = new Font("Microsoft YaHei UI", 10f);
            cancel.Margin = new Padding(10, 2, 0, 0);
            cancel.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });

            RoundButton guide = new RoundButton();
            guide.Text = "新手引导";
            guide.Size = new Size(104, 36);
            guide.Fill = Color.FromArgb(236, 240, 246);
            guide.FillHover = Color.FromArgb(226, 233, 243);
            guide.TextColor = Color.FromArgb(40, 90, 150);
            guide.Font = new Font("Microsoft YaHei UI", 10f);
            guide.Margin = new Padding(0, 2, 0, 0);
            guide.Click += new EventHandler(delegate(object o, EventArgs e2)
            { GuideForm gf = new GuideForm(); gf.ShowDialog(this); });

            // 还原默认设置：只重置设置项，不动你的图片和 Wheel 内容
            RoundButton reset = new RoundButton();
            reset.Text = "还原默认";
            reset.Size = new Size(104, 36);
            reset.Fill = Color.FromArgb(252, 238, 236);
            reset.FillHover = Color.FromArgb(248, 224, 220);
            reset.TextColor = Color.FromArgb(178, 66, 52);
            reset.Font = new Font("Microsoft YaHei UI", 10f);
            reset.Margin = new Padding(10, 2, 0, 0);
            reset.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                DialogResult r2 = MessageBox.Show(this,
                    "把所有设置恢复成默认值？\r\n\r\n（不会动你的图片和 Wheel 内容，只重置外观/行为等设置项）",
                    "还原默认设置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r2 != DialogResult.OK) return;
                Settings def = new Settings();
                Settings.CopyInto(def, s);
                AutoRun.Apply(s.AutoStart);
                s.Save();
                DialogResult = DialogResult.OK;
                Close();
            });

            // 按钮行：用五列表格把确定/取消靠右对齐（引导/还原在左）。
            TableLayoutPanel btnRow = new TableLayoutPanel();
            btnRow.ColumnCount = 5;
            btnRow.RowCount = 1;
            btnRow.AutoSize = false;
            btnRow.Dock = DockStyle.Fill;
            btnRow.Margin = new Padding(0);
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            btnRow.Controls.Add(guide, 0, 0);
            btnRow.Controls.Add(reset, 1, 0);
            // 中间只放个"撑宽"的空位（把确定/取消推到右边）。这里必须给它一个小尺寸：
            // Panel 的默认尺寸是 200×100，放进 40px 高的按钮行里会顶出行高、被裁（渲染工具会报"被裁"）。
            Panel btnSpacer = new Panel();
            btnSpacer.Size = new Size(1, 1);
            btnSpacer.Margin = new Padding(0);
            btnRow.Controls.Add(btnSpacer, 2, 0);
            btnRow.Controls.Add(ok, 3, 0);
            btnRow.Controls.Add(cancel, 4, 0);
            root.Controls.Add(btnRow, 0, 3);

            Label about = new Label();
            about.AutoSize = true;
            about.Text = AppInfo.Name + "   v" + AppInfo.Version + "   ·   by " + AppInfo.Author + "   ·   BETA";
            about.ForeColor = Color.FromArgb(150, 150, 160);
            about.Margin = new Padding(0, 4, 0, 0);
            root.Controls.Add(about, 0, 4);

            ShowPage(0);             // 只建第 1 页
            Controls.Add(root);      // 全部建完才挂上去：整棵树只排一次
            PerformLayout();
        }

        // ============================ 四页的内容 ============================
        // 每页一张"两列 + 行"的格子，行号写死；一格里放一个控件（成组的行用 Row(...) 包一层）。
        // 行号从 0 开始，写 RowCount 时要 ≥ 最大行号 + 1，否则那行不显示。

        // ---- 第 1 页：行为与快捷键 ----
        void BuildPage1()
        {
            TableLayoutPanel g = _pages[0];
            SetupRows(g, 8);
            Settings s = _s;

            g.Controls.Add(Section("行为"), 0, 0);
            g.Controls.Add(Section("快捷键与操作"), 1, 0);

            _chkDisk = new CheckBox();
            _chkDisk.AutoSize = true;
            _chkDisk.Text = "保存到硬盘（否则只存内存，退出即清）";
            _chkDisk.Checked = s.SaveToDisk;
            _chkDisk.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkDisk, 0, 1);

            _cmbHotkey = new ComboBox();
            _cmbHotkey.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbHotkey.Width = 170;
            _cmbHotkey.Margin = new Padding(0, 6, 0, 0);
            _cmbHotkey.Items.AddRange(HotkeyUtil.Names);
            _cmbHotkey.SelectedItem = s.Hotkey;
            if (_cmbHotkey.SelectedIndex < 0) _cmbHotkey.SelectedIndex = 0;
            g.Controls.Add(Row(MkLabel("截图热键"), _cmbHotkey), 1, 1);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.AutoSize = true;
            _chkAutoStart.Text = "开机自动启动（登录后自动在后台运行）";
            _chkAutoStart.Checked = AutoRun.IsEnabled();
            _chkAutoStart.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkAutoStart, 0, 2);

            _cmbCorner = new ComboBox();
            _cmbCorner.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCorner.Width = 170;
            _cmbCorner.Margin = new Padding(0, 6, 0, 0);
            _cmbCorner.Items.AddRange(new object[] { "左下角", "右下角", "左上角", "右上角" });
            _cmbCorner.SelectedIndex = CornerIndex(s.Corner);
            g.Controls.Add(Row(MkLabel("圆环位置"), _cmbCorner), 1, 2);

            _chkAuto = new CheckBox();
            _chkAuto.AutoSize = true;
            _chkAuto.Text = "空闲后自动收起轮盘";
            _chkAuto.Checked = s.AutoHide;
            _chkAuto.Margin = new Padding(0, 4, 0, 4);
            _numSec = Num(2, 600, s.AutoHideSeconds);
            g.Controls.Add(Row(_chkAuto, Gap(16), MkLabel("空闲秒数"), _numSec), 0, 3);

            _cmbDel = new ComboBox();
            _cmbDel.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDel.Width = 170;
            _cmbDel.Margin = new Padding(0, 6, 0, 0);
            _cmbDel.Items.AddRange(new object[] { "双击右键删除", "单击右键删除" });
            _cmbDel.SelectedIndex = (s.DeleteMode == "single") ? 1 : 0;
            g.Controls.Add(Row(MkLabel("删除方式"), _cmbDel), 1, 3);

            _chkTop = new CheckBox();
            _chkTop.AutoSize = true;
            _chkTop.Text = "总在最前（始终置顶显示）";
            _chkTop.Checked = s.AlwaysOnTop;
            _chkTop.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkTop, 0, 4);

            _cmbSwitch = new ComboBox();
            _cmbSwitch.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbSwitch.Width = 170;
            _cmbSwitch.Margin = new Padding(0, 6, 0, 0);
            _cmbSwitch.Items.AddRange(new object[] { "长按万能键弹圆盘", "长按后左右滑动" });
            _cmbSwitch.SelectedIndex = (s.SwitchMode == "swipe") ? 1 : 0;
            g.Controls.Add(Row(MkLabel("Wheel 切换"), _cmbSwitch), 1, 4);

            // 保存目录这一行本来就宽，横跨两列（否则两列加起来会顶破窗口宽度）
            _txtDir = new TextBox();
            _txtDir.Text = s.Dir;
            _txtDir.Width = 300;
            _txtDir.Margin = new Padding(0, 5, 8, 0);
            Button browse = new Button();
            browse.Text = "浏览";
            browse.AutoSize = true;
            browse.MinimumSize = new Size(60, 26);
            browse.Margin = new Padding(0, 4, 0, 0);
            browse.Click += new EventHandler(delegate(object o, EventArgs e2) {
                FolderBrowserDialog d = new FolderBrowserDialog();
                if (d.ShowDialog() == DialogResult.OK) _txtDir.Text = d.SelectedPath;
            });
            Control dirRow = Row(MkLabel("保存目录"), _txtDir, browse);
            g.Controls.Add(dirRow, 0, 5);
            g.SetColumnSpan(dirRow, 2);

            _chkClip = new CheckBox();
            _chkClip.AutoSize = true;
            _chkClip.Text = "复制图片后自动收进轮盘";
            _chkClip.Checked = s.ClipboardImport;
            g.Controls.Add(Row(_chkClip), 0, 6);

            _chkBalloon = new CheckBox();
            _chkBalloon.AutoSize = true;
            _chkBalloon.Text = "显示托盘气泡提示（关掉就不再弹右下角通知）";
            _chkBalloon.Checked = s.ShowBalloon;
            g.Controls.Add(Row(_chkBalloon), 1, 6);

            _chkUpdate = new CheckBox();
            _chkUpdate.AutoSize = true;
            _chkUpdate.Text = "启动时检查有没有新版本（只提示，不自动安装）";
            _chkUpdate.Checked = s.CheckUpdate;
            g.Controls.Add(Row(_chkUpdate), 0, 7);

            _chkDragFile = new CheckBox();
            _chkDragFile.AutoSize = true;
            _chkDragFile.Text = "拖出时同时带上\"文件\"（拖到桌面/文件夹会落地成文件）";
            _chkDragFile.Checked = s.DragOutAsFile;
            g.Controls.Add(Row(_chkDragFile), 1, 7);
        }

        // ---- 第 2 页：轮盘与外观 ----
        void BuildPage2()
        {
            TableLayoutPanel g = _pages[1];
            SetupRows(g, 8);
            Settings s = _s;

            g.Controls.Add(Section("外观"), 0, 0);

            _numMax = Num(1, 999, s.MaxCount);
            _numThumb = Num(40, 260, s.ThumbSize);
            g.Controls.Add(Row(MkLabel("最多保留张数"), _numMax, Gap(24), MkLabel("缩略图大小"), _numThumb), 0, 1);

            _chkCollapse = new CheckBox();
            _chkCollapse.AutoSize = true;
            _chkCollapse.Text = "收起状态：缩到屏幕边上留个小把手";
            _chkCollapse.Checked = s.CollapseMode;
            g.Controls.Add(Row(_chkCollapse), 1, 1);

            _numPeek = Num(120, 500, s.PeekPercent);
            Control peekRow = Row(MkLabel("长按放大(%)"), _numPeek);
            g.Controls.Add(peekRow, 0, 2);
            g.SetColumnSpan(peekRow, 2);

            // 下面这几行本身就宽（标签 + 下拉 + 说明），横跨两列 —— 两列并排会顶破窗口宽度
            _cmbScale = new ComboBox();
            _cmbScale.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbScale.FlatStyle = FlatStyle.Flat;
            _cmbScale.Width = 170;
            _cmbScale.Margin = new Padding(0, 6, 0, 0);
            _cmbScale.Items.Add("自动（按显示器 DPI）");
            for (int i = 1; i < scVals.Length; i++) _cmbScale.Items.Add(scVals[i] + "%");
            _cmbScale.SelectedIndex = 0;
            for (int i = 0; i < scVals.Length; i++) if (scVals[i] == s.UiScale) _cmbScale.SelectedIndex = i;
            Label hint = new Label();
            hint.AutoSize = true;
            hint.Text = "（整块轮盘等比放大，含文字和图标）";
            hint.ForeColor = Color.FromArgb(150, 152, 160);
            hint.Margin = new Padding(0, 10, 0, 0);
            Control scaleRow = Row(MkLabel("界面缩放"), _cmbScale, Gap(12), hint);
            g.Controls.Add(scaleRow, 0, 3);
            g.SetColumnSpan(scaleRow, 2);

            // 收起 / 展开的动画速度（独立于"动画速度"）
            _cmbRing = new ComboBox();
            _cmbRing.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRing.FlatStyle = FlatStyle.Flat;
            _cmbRing.Width = 130;
            _cmbRing.Margin = new Padding(0, 6, 0, 0);
            for (int i = 0; i < ringNames.Length; i++) _cmbRing.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            _cmbRing.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.ExpandSpeed) _cmbRing.SelectedIndex = i;
            Label hintRing = new Label();
            hintRing.AutoSize = true;
            hintRing.Text = "（只管收起 / 展开；百分比越大越快）";
            hintRing.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing.Margin = new Padding(0, 10, 0, 0);
            Control ringRow = Row(MkLabel("展开速度"), _cmbRing, Gap(10), hintRing);
            g.Controls.Add(ringRow, 0, 4);
            g.SetColumnSpan(ringRow, 2);

            _cmbRing2 = new ComboBox();
            _cmbRing2.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRing2.FlatStyle = FlatStyle.Flat;
            _cmbRing2.Width = 130;
            _cmbRing2.Margin = new Padding(0, 6, 0, 0);
            for (int i = 0; i < ringNames.Length; i++) _cmbRing2.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            _cmbRing2.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.CollapseSpeed) _cmbRing2.SelectedIndex = i;
            Label hintRing2 = new Label();
            hintRing2.AutoSize = true;
            hintRing2.Text = "（默认比展开快一档，收起要干脆）";
            hintRing2.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing2.Margin = new Padding(0, 10, 0, 0);
            Control ring2Row = Row(MkLabel("收起速度"), _cmbRing2, Gap(10), hintRing2);
            g.Controls.Add(ring2Row, 0, 5);
            g.SetColumnSpan(ring2Row, 2);

            _numRad = Num(120, 700, s.Radius);
            _numSlots = Num(2, 12, s.Slots);
            _numLabel = Num(9, 40, s.LabelSize);
            Control radRow = Row(MkLabel("环半径"), _numRad, Gap(24), MkLabel("弧上张数"), _numSlots,
                                 Gap(24), MkLabel("序号字号"), _numLabel);
            g.Controls.Add(radRow, 0, 6);
            g.SetColumnSpan(radRow, 2);

            _chkSingle = new CheckBox();
            _chkSingle.AutoSize = true;
            _chkSingle.Text = "只用一个把手：左边那个点一下展开、再点一下收起（任务栏自动隐藏时更省事）";
            _chkSingle.Checked = s.NubSingle;
            Control singleRow = Row(_chkSingle);
            g.Controls.Add(singleRow, 0, 7);
            g.SetColumnSpan(singleRow, 2);
        }

        // ---- 第 3 页：风格 ----
        void BuildPage3()
        {
            TableLayoutPanel g = _pages[2];
            SetupRows(g, 4);
            Settings s = _s;

            g.Controls.Add(Section("风格"), 0, 0);

            _cmbStyle = new ComboBox();
            _cmbStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbStyle.FlatStyle = FlatStyle.Flat;
            _cmbStyle.Width = 170;
            _cmbStyle.Margin = new Padding(0, 6, 0, 0);
            _cmbStyle.Items.AddRange(new object[] { "新拟态 + 毛玻璃", "纯扁平", "高对比（不透明）" });
            _cmbStyle.SelectedIndex = (s.UiStyle == "flat") ? 1 : (s.UiStyle == "solid" ? 2 : 0);

            _cmbAccent = new ComboBox();
            _cmbAccent.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAccent.FlatStyle = FlatStyle.Flat;
            _cmbAccent.Width = 170;
            _cmbAccent.Margin = new Padding(0, 6, 0, 0);
            _cmbAccent.Items.Add("跟随 Wheel 颜色");
            for (int i = 0; i < Palette.Names.Length; i++) _cmbAccent.Items.Add("统一：" + Palette.Names[i]);
            _cmbAccent.SelectedIndex = (s.AccentIndex >= 0 && s.AccentIndex < Palette.Names.Length) ? s.AccentIndex + 1 : 0;
            Control styleRow = Row(MkLabel("界面风格"), _cmbStyle, Gap(24), MkLabel("主题色"), _cmbAccent);
            g.Controls.Add(styleRow, 0, 1);
            g.SetColumnSpan(styleRow, 2);

            _cmbAnim = new ComboBox();
            _cmbAnim.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAnim.FlatStyle = FlatStyle.Flat;
            _cmbAnim.Width = 170;
            _cmbAnim.Margin = new Padding(0, 6, 0, 0);
            _cmbAnim.Items.AddRange(new object[] { "慢", "标准", "快" });
            _cmbAnim.SelectedIndex = (s.AnimSpeed <= 85) ? 0 : (s.AnimSpeed >= 120 ? 2 : 1);

            _chkName = new CheckBox();
            _chkName.AutoSize = true;
            _chkName.Text = "显示名称标签";
            _chkName.Checked = s.ShowNameLabel;
            _chkName.Margin = new Padding(0, 10, 0, 0);
            _chkCount = new CheckBox();
            _chkCount.AutoSize = true;
            _chkCount.Text = "显示计数标签";
            _chkCount.Checked = s.ShowCountLabel;
            _chkCount.Margin = new Padding(20, 10, 0, 0);
            Control animRow = Row(MkLabel("动画速度"), _cmbAnim, Gap(24), _chkName, _chkCount);
            g.Controls.Add(animRow, 0, 2);
            g.SetColumnSpan(animRow, 2);

            _chkGlassRefresh = new CheckBox();
            _chkGlassRefresh.AutoSize = true;
            _chkGlassRefresh.Text = "毛玻璃定时刷新（轮盘挂久了背景也是新的）";
            _chkGlassRefresh.Checked = s.GlassRefresh;
            g.Controls.Add(Row(_chkGlassRefresh), 0, 3);

            _chkIntroAnim = new CheckBox();
            _chkIntroAnim.AutoSize = true;
            _chkIntroAnim.Text = "启动时播放开启动画";
            _chkIntroAnim.Checked = s.IntroAnim;
            g.Controls.Add(Row(_chkIntroAnim), 1, 3);
        }

        // ---- 第 4 页：万能键与高级 ----
        void BuildPage4()
        {
            TableLayoutPanel g = _pages[3];
            SetupRows(g, 7);
            Settings s = _s;

            g.Controls.Add(Section("万能键"), 0, 0);

            // 四个分区各绑一个动作（以前是写死的）。选中就立刻写进设置：
            // 不依赖"确定"里那段保存循环（之前那里没生效）。
            string[] keyDir = { "上", "右", "下", "左" };
            for (int i = 0; i < 4; i++)
            {
                ComboBox kb = new ComboBox();
                kb.DropDownStyle = ComboBoxStyle.DropDownList;
                kb.Width = 150;
                kb.Margin = new Padding(0, 6, 14, 0);
                for (int j = 0; j < Settings.KeyActionIds.Length; j++)
                    kb.Items.Add(Settings.KeyActionName(Settings.KeyActionIds[j]));
                string cur = s.KeyActionAt(i);
                int idx = 0;
                for (int j = 0; j < Settings.KeyActionIds.Length; j++) if (Settings.KeyActionIds[j] == cur) idx = j;
                kb.SelectedIndex = idx;
                _keyBox[i] = kb;
                {
                    int myI = i; ComboBox self = kb;
                    kb.SelectedIndexChanged += new EventHandler(delegate(object o, EventArgs e2) {
                        if (self.SelectedIndex >= 0) s.SetKeyAction(myI, Settings.KeyActionIds[self.SelectedIndex]);
                    });
                }
            }
            // 一行放两个方向，省竖直空间
            g.Controls.Add(Row(MkLabel(keyDir[0]), _keyBox[0], Gap(16), MkLabel(keyDir[1]), _keyBox[1]), 0, 1);
            g.Controls.Add(Row(MkLabel(keyDir[2]), _keyBox[2], Gap(16), MkLabel(keyDir[3]), _keyBox[3]), 0, 2);

            Label keyHint = MkLabel("按住万能键弹出圆盘，往哪个方向松手就执行哪个动作");
            keyHint.ForeColor = Color.FromArgb(140, 146, 158);
            Control keyHintRow = Row(keyHint);
            g.Controls.Add(keyHintRow, 0, 3);
            g.SetColumnSpan(keyHintRow, 2);

            // ---------- 高级（外观微调）：默认折叠，需要时勾一下 ----------
            // 这几项对大多数人是噪音（第一次用不懂该选什么），所以默认藏起来。
            g.Controls.Add(Section("高级"), 0, 4);

            _advR = new TableLayoutPanel();
            _advR.ColumnCount = 1;
            _advR.RowCount = 1;
            _advR.AutoSize = true;
            _advR.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _advR.Margin = new Padding(0);
            _advR.Visible = false;
            _advR.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _advR.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _numGlass = Num(20, 100, s.GlassPercent);
            _numRadius = Num(0, 30, s.CardRadius);
            _numShadow = Num(0, 100, s.ShadowPercent);
            _advR.Controls.Add(Row(MkLabel("玻璃不透明度"), _numGlass, Gap(16), MkLabel("圆角(%)"), _numRadius,
                                   Gap(16), MkLabel("阴影强度"), _numShadow), 0, 0);

            CheckBox chkAdv = new CheckBox();
            chkAdv.AutoSize = true;
            chkAdv.Text = "显示高级选项（外观微调：玻璃 / 圆角 / 阴影）";
            chkAdv.Margin = new Padding(0, 10, 0, 0);
            chkAdv.CheckedChanged += new EventHandler(delegate(object o, EventArgs e2) {
                _advR.Visible = chkAdv.Checked;
                _advR.PerformLayout();
                PerformLayout();
            });
            g.Controls.Add(chkAdv, 0, 5);
            g.Controls.Add(_advR, 0, 6);
        }

        // ============================ 翻页 ============================
        // 第一次翻到某页才建那页的控件；没建过的页 = 没看过 = 没改过。
        // 翻页带 160ms 位移动画（15ms 一帧 ≈ 11 帧，实测约 165ms）：新页从一侧滑进来、
        // 旧页朝反方向滑出去（Panel 没有透明度，所以只用位移 + 分页器高亮同步过渡，不跳变）。
        // 两页在动画期间**永远刚好拼满可视区** —— 一个在 [x, x+W]、另一个在 [x±W, x±W+W]
        // —— 所以既不重叠也不留缝。
        const int PageAnimMs = 160;      // 140~200ms 档；15ms 一帧 ≈ 11 帧
        System.Windows.Forms.Timer _ptimer;
        System.Diagnostics.Stopwatch _pwatch;       // 进度按"真实过去了多少毫秒"算，不按帧数累加
        int _animFrom = -1, _animTo = -1;
        float _animT = 1f;

        // "正在翻页"以动画状态为准，不看定时器 —— 定时器被别的东西停掉时防连点也不能失效
        bool Animating { get { return _animFrom >= 0 && _animTo >= 0 && _animT < 1f; } }

        void ShowPage(int i) { ShowPage(i, true); }

        // 用户输入路径（点扇区 / 滚轮）走这里：动画期间直接忽略 —— 这就是"防连点"，
        // 连点不会叠加动画、不会重叠、不会跳变。（把防连点放在输入层，而不是塞进 ShowPage：
        // 塞进 ShowPage 会让"程序性切页"被悄悄吞掉 —— 没消息泵时动画永远走不完，
        // 后面几次切页就全丢了，工具/测试里踩到过。）
        bool TryGoto(int i)
        {
            if (Animating) return false;
            ShowPage(i);
            return true;
        }

        // 程序性切页（首次显示 / 工具 / 测试）：一定切过去；上一段动画没收尾就先精确收尾，绝不卡住
        void ShowPage(int i, bool animate)
        {
            if (i < 0) i = 0;
            if (i > _pages.Length - 1) i = _pages.Length - 1;
            if (Animating) FinishNow();
            if (i == _cur) return;          // 已经在这一页：不重播
            BuildPage(i);
            int from = _cur;
            _cur = i;
            if (_dial != null) _dial.Current = i;
            if (from < 0 || !animate || !IsHandleCreated || _body == null
                || _body.ClientSize.Width <= 0 || _body.ClientSize.Height <= 0)
            {
                SnapTo(i);                  // 首次显示 / 窗口还没出来：直接摆好（老行为）
                return;
            }
            StartSlide(from, i);
        }

        // 把正在走的动画立刻收尾到目标页（精确落位，等同动画最后一帧）
        void FinishNow()
        {
            if (_ptimer != null) _ptimer.Stop();
            if (_pwatch != null) { try { _pwatch.Stop(); } catch { } _pwatch = null; }
            int to = _animTo;
            _animT = 1f;
            _animFrom = -1;
            if (to >= 0) SnapTo(to);
        }

        void BuildPage(int i)
        {
            if (_built[i]) return;
            _built[i] = true;
            _pages[i].SuspendLayout();
            _builders[i]();
            _pages[i].ResumeLayout(true);
        }

        // 精确落位：Dock=Fill 由布局引擎给出整格矩形，动画结束绝不留下 1px 偏移
        void SnapTo(int i)
        {
            for (int k = 0; k < _pages.Length; k++)
            {
                _pages[k].Dock = DockStyle.Fill;
                _pages[k].Visible = (k == i);
            }
            if (_dial != null)
            {
                _dial.AnimFrom = i; _dial.AnimTo = i; _dial.AnimT = 1f;
                _dial.Current = i; _dial.Invalidate();
            }
            if (_body != null) _body.PerformLayout();
        }

        void StartSlide(int from, int to)
        {
            BuildPage(from);
            _animFrom = from; _animTo = to; _animT = 0f;
            for (int k = 0; k < _pages.Length; k++) _pages[k].Visible = (k == from || k == to);
            _pages[from].Dock = DockStyle.None;      // 交给动画自己摆位置
            _pages[to].Dock = DockStyle.None;
            ApplySlide(0f);                          // 第 0 帧：新页整页在窗口外 —— 一帧都不许重叠
            _pwatch = System.Diagnostics.Stopwatch.StartNew();
            if (_ptimer == null)
            {
                _ptimer = new System.Windows.Forms.Timer();
                _ptimer.Interval = 15;               // 和轮盘动画同一个节拍（~66fps）
                _ptimer.Tick += delegate(object o, EventArgs e2) { AnimTick(); };
            }
            _ptimer.Start();
        }

        void ApplySlide(float t)
        {
            int W = _body.ClientSize.Width, H = _body.ClientSize.Height;
            if (W <= 0 || H <= 0) return;
            float e = Gfx.EaseOut(t);                // 先快后慢、收尾稳（跟轮盘同一套缓动）
            int dir = (_animTo > _animFrom) ? 1 : -1; // 往后翻：新页从右边进来、旧页往左走
            int newX = (int)Math.Round(dir * W * (1f - e));    // ±W -> 0
            int oldX = (int)Math.Round(-dir * W * e);          // 0 -> ∓W
            _pages[_animTo].Bounds = new Rectangle(newX, 0, W, H);
            if (_animFrom != _animTo) _pages[_animFrom].Bounds = new Rectangle(oldX, 0, W, H);
            if (_dial != null)
            {
                _dial.AnimFrom = _animFrom; _dial.AnimTo = _animTo; _dial.AnimT = e;
                _dial.Invalidate();                  // 扇区高亮/凸起跟着一起走过去
            }
        }

        void AnimTick()
        {
            if (_animFrom < 0 || _animTo < 0) { if (_ptimer != null) _ptimer.Stop(); return; }
            // 进度看真实时间：这一帧画得慢（负载重/重绘多）时不会把整段动画拖长，总时长始终是 160ms 左右
            _animT = _pwatch == null ? 1f : (float)(_pwatch.Elapsed.TotalMilliseconds / PageAnimMs);
            if (_animT >= 1f)
            {
                _animT = 1f;                        // 收尾精确到 1，不留 1.03 这种余量
                ApplySlide(1f);                     // 最后一帧：位置精确等于目标（0 偏移）
                int to = _animTo;
                _animFrom = -1;
                if (_ptimer != null) _ptimer.Stop();
                if (_pwatch != null) { _pwatch.Stop(); _pwatch = null; }
                SnapTo(to);                         // 再交回布局引擎（Dock=Fill），保证和静态布局逐像素一致
                _animTo = to;
                return;
            }
            ApplySlide(_animT);
        }

        // ============================ 保存 ============================
        // 把界面上改过的值写回设置。**只写"建过"的页**：没建过的页用户没看过，
        // 保持原值即可（绝不会把它覆盖回默认值 —— 老版本这里踩过一次"确定后改动打回原形"）。
        void SaveFromUi()
        {
            Settings s = _s;

            if (_built[0])
            {
                s.SaveToDisk = _chkDisk.Checked;
                s.Dir = _txtDir.Text.Trim();
                s.AutoHide = _chkAuto.Checked;
                s.AutoHideSeconds = (int)_numSec.Value;
                s.AlwaysOnTop = _chkTop.Checked;
                s.Corner = IndexCorner(_cmbCorner.SelectedIndex);
                s.AutoStart = _chkAutoStart.Checked;
                s.DeleteMode = (_cmbDel.SelectedIndex == 1) ? "single" : "double";
                s.SwitchMode = (_cmbSwitch.SelectedIndex == 1) ? "swipe" : "radial";
                s.ClipboardImport = _chkClip.Checked;
                s.ShowBalloon = _chkBalloon.Checked;
                s.CheckUpdate = _chkUpdate.Checked;
                s.DragOutAsFile = _chkDragFile.Checked;
            }
            if (_built[1])
            {
                s.MaxCount = (int)_numMax.Value;
                s.ThumbSize = (int)_numThumb.Value;
                s.Radius = (int)_numRad.Value;
                s.Slots = (int)_numSlots.Value;
                s.LabelSize = (int)_numLabel.Value;
                s.PeekPercent = (int)_numPeek.Value;
                s.UiScale = scVals[_cmbScale.SelectedIndex < 0 ? 0 : _cmbScale.SelectedIndex];
                s.CollapseMode = _chkCollapse.Checked;
                s.ExpandSpeed = ringVals[_cmbRing.SelectedIndex < 0 ? 2 : _cmbRing.SelectedIndex];
                s.CollapseSpeed = ringVals[_cmbRing2.SelectedIndex < 0 ? 2 : _cmbRing2.SelectedIndex];
                s.NubSingle = _chkSingle.Checked;
            }
            if (_built[2])
            {
                s.UiStyle = (_cmbStyle.SelectedIndex == 1) ? "flat" : (_cmbStyle.SelectedIndex == 2 ? "solid" : "neu");
                s.AccentIndex = _cmbAccent.SelectedIndex - 1;
                s.AnimSpeed = (_cmbAnim.SelectedIndex == 0) ? 70 : (_cmbAnim.SelectedIndex == 2 ? 140 : 100);
                s.ShowNameLabel = _chkName.Checked;
                s.ShowCountLabel = _chkCount.Checked;
                s.GlassRefresh = _chkGlassRefresh.Checked;
                s.IntroAnim = _chkIntroAnim.Checked;     // 注意：这个控件在第 3 页（"动画细节"），别放进上一块
            }
            if (_built[3])
            {
                s.GlassPercent = (int)_numGlass.Value;
                s.CardRadius = (int)_numRadius.Value;
                s.ShadowPercent = (int)_numShadow.Value;
                // 保存万能键四分区。这里必须立刻 s.Save() 落盘：
                // 否则下次打开设置窗口会从文件里读到旧值，一点确定就把刚改的打回原形
                // （"圆盘上的动作名改完不变"的根因）。这条行为现在由 tests\behavior-test.cs 守着。
                try
                {
                    for (int ki = 0; ki < 4; ki++)
                    {
                        if (_keyBox[ki] == null) continue;
                        int ksel = _keyBox[ki].SelectedIndex;
                        if (ksel < 0) ksel = 0;
                        s.SetKeyAction(ki, Settings.KeyActionIds[ksel]);
                    }
                    s.Save();
                }
                catch (Exception kex) { Err.Log("SettingsSaveKey", kex); }
            }
            if (_built[0] && _cmbHotkey != null && _cmbHotkey.SelectedItem != null) s.Hotkey = _cmbHotkey.SelectedItem.ToString();
            AutoRun.Apply(s.AutoStart);
            s.Save();
        }

        static int CornerIndex(string c)
        {
            if (c == "BR") return 1;
            if (c == "TL") return 2;
            if (c == "TR") return 3;
            return 0;
        }

        static string IndexCorner(int i)
        {
            if (i == 1) return "BR";
            if (i == 2) return "TL";
            if (i == 3) return "TR";
            return "BL";
        }

        // 新建一张格子：列样式先给全，行样式由各页 SetupRows 按自己的行数补
        static TableLayoutPanel NewGrid(int cols)
        {
            TableLayoutPanel g = new TableLayoutPanel();
            g.ColumnCount = cols;
            g.RowCount = 1;
            g.AutoSize = false;
            g.Dock = DockStyle.Fill;
            g.Margin = new Padding(0);
            for (int i = 0; i < cols; i++) g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return g;
        }

        // 指定行数并补满 RowStyles（行数必须 ≥ 用到的最大行号 + 1，否则那一行不显示）
        static void SetupRows(TableLayoutPanel g, int rows)
        {
            g.RowCount = rows;
            g.RowStyles.Clear();
            for (int i = 0; i < rows; i++) g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        static Label MkLabel(string t)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = t;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(0, 10, 12, 0);
            return l;
        }

        static Label Section(string t)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = t;
            l.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(0, 122, 204);
            l.Margin = new Padding(0, 14, 0, 2);
            return l;
        }

        static Control Gap(int w)
        {
            Control c = new Control();
            c.Width = w; c.Height = 1;
            c.Margin = new Padding(0);
            return c;
        }

        static NumericUpDown Num(int mn, int mx, int val)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = mn; n.Maximum = mx; n.Value = val;
            n.Width = 72;
            n.Height = 26;
            n.Margin = new Padding(0, 7, 10, 0);
            return n;
        }

        static FlowLayoutPanel Row(params Control[] cs)
        {
            RowPanel f = new RowPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = false;
            f.Margin = new Padding(0, 5, 0, 5);   // 行距：设置项变多了，压紧一点免得窗口太高
            for (int i = 0; i < cs.Length; i++) f.Controls.Add(cs[i]);
            return f;
        }
    }

    // ============================ 一行控件（行容器） ============================
    // 高度用**子控件的真实底边**兜底，不能只信 FlowLayoutPanel 自己算出来的数。
    // 起因（v0.5.2 用户报的"控件被裁"）：ComboBox 继承窗口字体（9.5pt 雅黑）之后真实高度是 27px，
    // 但它对外报的"首选高度"是 23px —— 于是 AutoSize 的行只有 29px 高，组合框的底边和下拉箭头
    // 被整整裁掉 4px。数值框（24px）则是刚好贴边。四页 12 个组合框全中。
    // 这里只在"算出来的比子控件实际需要的矮"时补高，其余行一个字都不动。
    class RowPanel : FlowLayoutPanel
    {
        public override Size GetPreferredSize(Size proposed)
        {
            Size s = base.GetPreferredSize(proposed);
            int need = 0;
            foreach (Control c in Controls)
                if (c.Visible) need = Math.Max(need, c.Bottom + c.Margin.Bottom);
            need += Padding.Bottom;
            if (need > s.Height) s.Height = need;     // 谁大听谁的：底边永远不被裁
            return s;
        }
    }

    // ============================ 轮盘式分页器 ============================
    // 顶部一个小圆弧，四个扇区 = 四页：与主界面同一套视觉语言（新拟态的"上亮下暗"凸起感），
    // 当前页高亮成主题色、数字变白。点扇区翻页，滚轮由 SettingsForm 的消息过滤器接管。
    class PageDial : Control
    {
        public string[] Names = new string[0];
        public int Current;
        // 刚被点中的扇区（交给 SettingsForm 决定要不要翻：动画期间它会忽略，所以这里不自己改 Current）
        public int Picked { get; set; }
        // 高亮过渡：AnimT=0 时高亮全在 AnimFrom、=1 时全在 AnimTo（由 SettingsForm 的翻页动画推）
        public int AnimFrom { get; set; }
        public int AnimTo { get; set; }
        public float AnimT { get; set; }
        public event EventHandler PagePicked;
        int _hover = -1;

        static readonly Color Accent = Color.FromArgb(0, 122, 204);
        static readonly Color Surface = Color.FromArgb(238, 240, 245);
        static readonly Color SurfaceHot = Color.FromArgb(246, 249, 253);
        static readonly Color NumIdle = Color.FromArgb(112, 120, 134);
        static readonly Font NumFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        static readonly Font TitleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);

        const float SweepTotal = 150f;      // 整个圆弧张开的度数（其余留白，看起来才像"顶部一小段弧"）
        const float BandW = 24f;            // 弧的厚度
        const float LiftPx = 2.2f;          // 当前页那一瓣往外凸出去多少（凸起也是平滑过渡的）

        public PageDial()
        {
            AnimT = 1f;                       // 默认"过渡已完成"：高亮就停在 AnimTo（也就是 Current）上
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        // 这一瓣的"高亮权重" 0..1：翻页时旧页 1->0、新页 0->1，两边同时走
        float Weight(int i)
        {
            float w = 0f;
            if (i == AnimTo) w += AnimT;
            if (i == AnimFrom) w += 1f - AnimT;
            return w > 1f ? 1f : (w < 0f ? 0f : w);
        }

        // 颜色按权重插值（不是改透明度：GDI 画字不吃 alpha，插颜色才真的平滑）
        static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(a.A + (int)Math.Round((b.A - a.A) * t),
                                  a.R + (int)Math.Round((b.R - a.R) * t),
                                  a.G + (int)Math.Round((b.G - a.G) * t),
                                  a.B + (int)Math.Round((b.B - a.B) * t));
        }

        int Count { get { return Names == null ? 0 : Names.Length; } }
        float Cx { get { return Width / 2f; } }
        float Cy { get { return Height - 4f; } }                    // 圆心落在控件底边上：只露出上半圆
        float Ro { get { return Math.Min(92f, Height - 8f); } }
        float Ri { get { return Ro - BandW; } }

        // 扇区环带路径（外弧顺着画、内弧倒着画，中间留一点缝，瓣与瓣之间才看得出分界）
        static GraphicsPath Band(float cx, float cy, float ri, float ro, float start, float sweep)
        {
            GraphicsPath p = new GraphicsPath();
            if (sweep <= 0.1f) return p;
            p.AddArc(new RectangleF(cx - ro, cy - ro, ro * 2f, ro * 2f), start, sweep);
            p.AddArc(new RectangleF(cx - ri, cy - ri, ri * 2f, ri * 2f), start + sweep, -sweep);
            p.CloseFigure();
            return p;
        }

        void Sector(int i, out float start, out float sweep, out PointF mid)
        {
            int n = Math.Max(1, Count);
            sweep = SweepTotal / n;
            start = 270f - SweepTotal / 2f + sweep * i;
            double a = (start + sweep / 2f) * Math.PI / 180.0;
            float mr = (Ri + Ro) / 2f;
            mid = new PointF(Cx + (float)(Math.Cos(a) * mr), Cy + (float)(Math.Sin(a) * mr));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            int n = Count;
            if (n <= 0) return;

            for (int i = 0; i < n; i++)
            {
                float start, sweep; PointF mid;
                Sector(i, out start, out sweep, out mid);
                float w = Weight(i);
                bool hot = (i == _hover) && w < 0.5f;
                // 高亮 = 从"常态底"往主题色插值；同时整瓣沿半径往外凸一点（凸起跟着高亮一起走）
                double midA = (start + sweep / 2f) * Math.PI / 180.0;
                float lift = LiftPx * w;
                float ox = (float)(Math.Cos(midA) * lift), oy = (float)(Math.Sin(midA) * lift);
                using (GraphicsPath p = Band(Cx + ox, Cy + oy, Ri, Ro, start + 1.2f, sweep - 2.4f))
                {
                    RectangleF box = p.GetBounds();
                    // 凸起感：顶上一条高光、底下一条暗边（GlassPanel 一上一下，跟轮盘控件同一套路）
                    Color fill = Mix(hot ? SurfaceHot : Surface, Accent, w);
                    int hi = (int)Math.Round(190 + (120 - 190) * w);
                    int shade = (int)Math.Round(46 + (80 - 46) * w);
                    Gfx.GlassPanel(g, p, box, fill, hi, shade, true);
                    if (w > 0.01f)
                        using (Pen pen = new Pen(Color.FromArgb((int)Math.Round(120 * w), 255, 255, 255), 1.2f))
                        { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawPath(pen, p); }
                }
                // 扇区里的序号
                Rectangle numRc = new Rectangle((int)mid.X - 12, (int)mid.Y - 9, 24, 18);
                TextRenderer.DrawText(g, (i + 1).ToString(), NumFont, numRc,
                    Mix(NumIdle, Color.White, w),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // 圆环内圈里写页名：翻页时走到一半换字（不叠字、不跳页）
            int show = (AnimT < 0.5f && AnimFrom >= 0 && AnimFrom < n) ? AnimFrom : Current;
            if (show < 0 || show >= n) show = AnimTo >= 0 && AnimTo < n ? AnimTo : 0;
            string t = (show >= 0 && show < n) ? Names[show] : "";
            int tw = TextRenderer.MeasureText(t, TitleFont).Width + 12;
            Rectangle rc = new Rectangle((int)(Cx - tw / 2f), (int)(Cy - Ri + 34f), tw, 26);
            TextRenderer.DrawText(g, t, TitleFont, rc, Color.FromArgb(64, 70, 82),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // 命中的扇区；没命中返回 -1（环带内外各放宽 6px，好点一点）
        int HitTest(Point p)
        {
            int n = Count;
            if (n <= 0) return -1;
            float dx = p.X - Cx, dy = p.Y - Cy;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            if (d > Ro + 6f || d < Ri - 6f) return -1;
            double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (ang < 0) ang += 360.0;
            double rel = ang - (270.0 - SweepTotal / 2.0);
            if (rel < 0) rel += 360.0;
            if (rel > SweepTotal) return -1;
            int idx = (int)(rel / (SweepTotal / n));
            return idx < n ? idx : n - 1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = HitTest(e.Location);
            // 只报告"点了哪一瓣"：翻不翻由 SettingsForm 定（它还要管动画期间防连点）
            if (i >= 0 && i != Current)
            {
                Picked = i;
                if (PagePicked != null) PagePicked(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = HitTest(e.Location);
            if (i != _hover) { _hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }
    }
}
