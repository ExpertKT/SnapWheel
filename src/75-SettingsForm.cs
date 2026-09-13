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
    class SettingsForm : Form
    {
        TableLayoutPanel _root;
        // 应用真·毛玻璃：窗口背景半透明 + 系统 acrylic 模糊；系统不支持就退回不透明浅底
        void ApplyGlass()
        {
            // 对话框用干净的浅色实底：半透明窗体 + 子控件（按钮/输入框）在 Windows 上
            // 容易出现"四角没画到、重绘才恢复"的脏块，实测得不偿失。
            // 真正需要毛玻璃的地方是轮盘本体，那边是自己绘制的，好控制。
            BackColor = Color.FromArgb(248, 249, 252);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyGlass();
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
            Text = AppInfo.Name + " 设置  ·  BETA";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(250, 250, 252);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20, 14, 20, 12);

            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 2;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.Dock = DockStyle.Fill;
            root.Margin = new Padding(0);
            _root = root;
            Controls.Add(root);

            // 左右两栏：内容多也不会把窗口顶出屏幕（原来一列排下来 970px 高）
            TableLayoutPanel colL = new TableLayoutPanel();
            colL.ColumnCount = 1;
            colL.AutoSize = true;
            colL.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            colL.Margin = new Padding(0, 0, 34, 0);
            TableLayoutPanel colR = new TableLayoutPanel();
            colR.ColumnCount = 1;
            colR.AutoSize = true;
            colR.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            colR.Margin = new Padding(0);

            Label head = new Label();
            head.AutoSize = true;
            head.Text = AppInfo.Name + " 设置";
            head.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(32, 34, 38);
            head.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(head);
            root.SetColumnSpan(head, 2);
            root.Controls.Add(colL, 0, 1);
            root.Controls.Add(colR, 1, 1);

            CheckBox chkDisk = new CheckBox();
            chkDisk.AutoSize = true;
            chkDisk.Text = "保存到硬盘（否则只存内存，退出即清）";
            chkDisk.Checked = s.SaveToDisk;
            chkDisk.Margin = new Padding(0, 4, 0, 4);
            colL.Controls.Add(Section("行为"));
            colL.Controls.Add(chkDisk);

            CheckBox chkAutoStart = new CheckBox();
            chkAutoStart.AutoSize = true;
            chkAutoStart.Text = "开机自动启动（登录后自动在后台运行）";
            chkAutoStart.Checked = AutoRun.IsEnabled();
            chkAutoStart.Margin = new Padding(0, 4, 0, 4);
            colL.Controls.Add(chkAutoStart);

            TextBox txtDir = new TextBox();
            txtDir.Text = s.Dir;
            txtDir.Width = 300;
            txtDir.Margin = new Padding(0, 5, 8, 0);
            Button browse = new Button();
            browse.Text = "浏览";
            browse.AutoSize = true;
            browse.MinimumSize = new Size(60, 26);
            browse.Margin = new Padding(0, 4, 0, 0);
            browse.Click += new EventHandler(delegate(object o, EventArgs e2) {
                FolderBrowserDialog d = new FolderBrowserDialog();
                if (d.ShowDialog() == DialogResult.OK) txtDir.Text = d.SelectedPath;
            });
            colL.Controls.Add(Row(MkLabel("保存目录"), txtDir, browse));

            colL.Controls.Add(Section("外观"));
            NumericUpDown numMax = Num(1, 999, s.MaxCount);
            NumericUpDown numThumb = Num(40, 260, s.ThumbSize);
            colL.Controls.Add(Row(MkLabel("最多保留张数"), numMax, Gap(24), MkLabel("缩略图大小"), numThumb));

            NumericUpDown numRad = Num(120, 700, s.Radius);
            NumericUpDown numSlots = Num(2, 12, s.Slots);
            NumericUpDown numLabel = Num(9, 40, s.LabelSize);
            colL.Controls.Add(Row(MkLabel("环半径"), numRad, Gap(24), MkLabel("弧上张数"), numSlots, Gap(24), MkLabel("序号字号"), numLabel));

            // 分辨率 / DPI 适配
            ComboBox cmbScale = new ComboBox();
            cmbScale.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbScale.FlatStyle = FlatStyle.Flat;
            cmbScale.Width = 170;
            cmbScale.Margin = new Padding(0, 6, 0, 0);
            int[] scVals = { 0, 80, 90, 100, 110, 125, 150, 175, 200, 250 };
            cmbScale.Items.Add("自动（按显示器 DPI）");
            for (int i = 1; i < scVals.Length; i++) cmbScale.Items.Add(scVals[i] + "%");
            cmbScale.SelectedIndex = 0;
            for (int i = 0; i < scVals.Length; i++) if (scVals[i] == s.UiScale) cmbScale.SelectedIndex = i;
            Label hint = new Label();
            hint.AutoSize = true;
            hint.Text = "（整块轮盘等比放大，含文字和图标）";
            hint.ForeColor = Color.FromArgb(150, 152, 160);
            hint.Margin = new Padding(0, 10, 0, 0);
            colL.Controls.Add(Row(MkLabel("界面缩放"), cmbScale, Gap(12), hint));

            NumericUpDown numPeek = Num(120, 500, s.PeekPercent);

            // 收起态：像贴边小球一样，缩到屏幕边上留个小把手
            CheckBox chkCollapse = new CheckBox();
            chkCollapse.AutoSize = true;
            chkCollapse.Text = "收起状态：缩到屏幕边上留个小把手";
            chkCollapse.Checked = s.CollapseMode;
            colL.Controls.Add(Row(chkCollapse));

            // 收起 / 展开的动画速度（独立于上面的"动画速度"）
            ComboBox cmbRing = new ComboBox();
            cmbRing.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbRing.FlatStyle = FlatStyle.Flat;
            cmbRing.Width = 130;
            cmbRing.Margin = new Padding(0, 6, 0, 0);
            int[] ringVals = { 220, 150, 100, 80, 60, 45 };
            string[] ringNames = { "极快", "快", "标准", "慢", "很慢", "最慢" };
            for (int i = 0; i < ringNames.Length; i++) cmbRing.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            cmbRing.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.ExpandSpeed) cmbRing.SelectedIndex = i;
            Label hintRing = new Label();
            hintRing.AutoSize = true;
            hintRing.Text = "（只管收起 / 展开；百分比越大越快）";
            hintRing.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing.Margin = new Padding(0, 10, 0, 0);
            colL.Controls.Add(Row(MkLabel("展开速度"), cmbRing, Gap(10), hintRing));

            ComboBox cmbRing2 = new ComboBox();
            cmbRing2.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbRing2.FlatStyle = FlatStyle.Flat;
            cmbRing2.Width = 130;
            cmbRing2.Margin = new Padding(0, 6, 0, 0);
            for (int i = 0; i < ringNames.Length; i++) cmbRing2.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            cmbRing2.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.CollapseSpeed) cmbRing2.SelectedIndex = i;
            Label hintRing2 = new Label();
            hintRing2.AutoSize = true;
            hintRing2.Text = "（默认比展开快一档，收起要干脆）";
            hintRing2.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing2.Margin = new Padding(0, 10, 0, 0);
            colL.Controls.Add(Row(MkLabel("收起速度"), cmbRing2, Gap(10), hintRing2));

            CheckBox chkSingle = new CheckBox();
            chkSingle.AutoSize = true;
            chkSingle.Text = "只用一个把手：左边那个点一下展开、再点一下收起（任务栏自动隐藏时更省事）";
            chkSingle.Checked = s.NubSingle;
            colL.Controls.Add(Row(chkSingle));

            CheckBox chkBalloon = new CheckBox();
            chkBalloon.AutoSize = true;
            chkBalloon.Text = "显示托盘气泡提示（关掉就不再弹右下角通知）";
            chkBalloon.Checked = s.ShowBalloon;
            colL.Controls.Add(Row(chkBalloon));

            // 剪贴板自动收纳 + 玻璃底定时刷新
            CheckBox chkClip = new CheckBox();
            chkClip.AutoSize = true;
            chkClip.Text = "复制图片后自动收进轮盘";
            chkClip.Checked = s.ClipboardImport;
            CheckBox chkGlassRefresh = new CheckBox();
            chkGlassRefresh.AutoSize = true;
            chkGlassRefresh.Text = "毛玻璃定时刷新（轮盘挂久了背景也是新的）";
            chkGlassRefresh.Checked = s.GlassRefresh;
            colL.Controls.Add(Row(chkClip));
            colL.Controls.Add(Row(chkGlassRefresh));

            CheckBox chkDragFile = new CheckBox();
            chkDragFile.AutoSize = true;
            chkDragFile.Text = "拖出时同时带上\"文件\"（拖到桌面/文件夹会落地成文件）";
            chkDragFile.Checked = s.DragOutAsFile;
            colL.Controls.Add(Row(chkDragFile));

            CheckBox chkUpdate = new CheckBox();
            chkUpdate.AutoSize = true;
            chkUpdate.Text = "启动时检查有没有新版本（只提示，不自动安装）";
            chkUpdate.Checked = s.CheckUpdate;
            colL.Controls.Add(Row(chkUpdate));
            CheckBox chkIntroAnim = new CheckBox();
            chkIntroAnim.AutoSize = true;
            chkIntroAnim.Text = "启动时播放开启动画";
            chkIntroAnim.Checked = s.IntroAnim;
            chkIntroAnim.Margin = new Padding(0, 10, 0, 0);
            colL.Controls.Add(Row(MkLabel("长按放大(%)"), numPeek, Gap(24), chkIntroAnim));

            // ---------------- 风格（新拟态 + 扁平化 + 毛玻璃）----------------
            int advFrom = colR.Controls.Count;      // 从这里开始是"高级（外观微调）"，稍后整体折叠
            colR.Controls.Add(Section("风格"));

            ComboBox cmbStyle = new ComboBox();
            cmbStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbStyle.FlatStyle = FlatStyle.Flat;
            cmbStyle.Width = 170;
            cmbStyle.Margin = new Padding(0, 6, 0, 0);
            cmbStyle.Items.AddRange(new object[] { "新拟态 + 毛玻璃", "纯扁平", "高对比（不透明）" });
            cmbStyle.SelectedIndex = (s.UiStyle == "flat") ? 1 : (s.UiStyle == "solid" ? 2 : 0);

            ComboBox cmbAccent = new ComboBox();
            cmbAccent.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbAccent.FlatStyle = FlatStyle.Flat;
            cmbAccent.Width = 170;
            cmbAccent.Margin = new Padding(0, 6, 0, 0);
            cmbAccent.Items.Add("跟随 Wheel 颜色");
            for (int i = 0; i < Palette.Names.Length; i++) cmbAccent.Items.Add("统一：" + Palette.Names[i]);
            cmbAccent.SelectedIndex = (s.AccentIndex >= 0 && s.AccentIndex < Palette.Names.Length) ? s.AccentIndex + 1 : 0;
            colR.Controls.Add(Row(MkLabel("界面风格"), cmbStyle, Gap(24), MkLabel("主题色"), cmbAccent));

            NumericUpDown numGlass = Num(20, 100, s.GlassPercent);
            NumericUpDown numRadius = Num(0, 30, s.CardRadius);
            NumericUpDown numShadow = Num(0, 100, s.ShadowPercent);
            colR.Controls.Add(Row(MkLabel("玻璃不透明度"), numGlass, Gap(16), MkLabel("圆角(%)"), numRadius,
                                  Gap(16), MkLabel("阴影强度"), numShadow));

            ComboBox cmbAnim = new ComboBox();
            cmbAnim.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbAnim.FlatStyle = FlatStyle.Flat;
            cmbAnim.Width = 170;
            cmbAnim.Margin = new Padding(0, 6, 0, 0);
            cmbAnim.Items.AddRange(new object[] { "慢", "标准", "快" });
            cmbAnim.SelectedIndex = (s.AnimSpeed <= 85) ? 0 : (s.AnimSpeed >= 120 ? 2 : 1);

            CheckBox chkName = new CheckBox();
            chkName.AutoSize = true;
            chkName.Text = "显示名称标签";
            chkName.Checked = s.ShowNameLabel;
            chkName.Margin = new Padding(0, 10, 0, 0);
            CheckBox chkCount = new CheckBox();
            chkCount.AutoSize = true;
            chkCount.Text = "显示计数标签";
            chkCount.Checked = s.ShowCountLabel;
            chkCount.Margin = new Padding(20, 10, 0, 0);
            colR.Controls.Add(Row(MkLabel("动画速度"), cmbAnim, Gap(24), chkName, chkCount));

            // ---------- 把「风格」这几行收进一个默认折叠的高级区 ----------
            // 外观微调项对大多数人是噪音，默认藏起来（第一次用不懂该选什么），需要时勾一下即可。
            {
                int advTo = colR.Controls.Count;
                TableLayoutPanel advR = new TableLayoutPanel();
                advR.ColumnCount = 1;
                advR.AutoSize = true;
                advR.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                advR.Margin = new Padding(0);
                advR.Visible = false;
                System.Collections.Generic.List<Control> move = new System.Collections.Generic.List<Control>();
                for (int i = advFrom; i < advTo; i++) move.Add(colR.Controls[i]);
                for (int i = 0; i < move.Count; i++) { colR.Controls.Remove(move[i]); advR.Controls.Add(move[i]); }
                colR.Controls.Add(advR);
                CheckBox chkAdv = new CheckBox();
                chkAdv.AutoSize = true;
                chkAdv.Text = "显示高级选项（外观微调 / 动画细节）";
                chkAdv.Margin = new Padding(0, 12, 0, 0);
                chkAdv.CheckedChanged += new EventHandler(delegate(object o, EventArgs e2) {
                    advR.Visible = chkAdv.Checked;
                    advR.PerformLayout();
                    PerformLayout();
                });
                colR.Controls.Add(chkAdv);
            }

            CheckBox chkAuto = new CheckBox();
            chkAuto.AutoSize = true;
            chkAuto.Text = "空闲后自动收起轮盘";
            chkAuto.Checked = s.AutoHide;
            chkAuto.Margin = new Padding(0, 4, 0, 4);
            NumericUpDown numSec = Num(2, 600, s.AutoHideSeconds);
            colL.Controls.Add(Row(chkAuto, Gap(16), MkLabel("空闲秒数"), numSec));

            CheckBox chkTop = new CheckBox();
            chkTop.AutoSize = true;
            chkTop.Text = "总在最前（始终置顶显示）";
            chkTop.Checked = s.AlwaysOnTop;
            chkTop.Margin = new Padding(0, 4, 0, 4);
            colL.Controls.Add(chkTop);

            ComboBox cmb = new ComboBox();
            cmb.DropDownStyle = ComboBoxStyle.DropDownList;
            cmb.Width = 170;
            cmb.Margin = new Padding(0, 6, 0, 0);
            cmb.Items.AddRange(HotkeyUtil.Names);
            cmb.SelectedItem = s.Hotkey;
            if (cmb.SelectedIndex < 0) cmb.SelectedIndex = 0;
            colR.Controls.Add(Row(MkLabel("截图热键"), cmb));

            ComboBox cmbCorner = new ComboBox();
            cmbCorner.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCorner.Width = 170;
            cmbCorner.Margin = new Padding(0, 6, 0, 0);
            cmbCorner.Items.AddRange(new object[] { "左下角", "右下角", "左上角", "右上角" });
            cmbCorner.SelectedIndex = CornerIndex(s.Corner);
            colR.Controls.Add(Row(MkLabel("圆环位置"), cmbCorner));

            ComboBox cmbDel = new ComboBox();
            cmbDel.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDel.Width = 170;
            cmbDel.Margin = new Padding(0, 6, 0, 0);
            cmbDel.Items.AddRange(new object[] { "双击右键删除", "单击右键删除" });
            cmbDel.SelectedIndex = (s.DeleteMode == "single") ? 1 : 0;
            colR.Controls.Add(Row(MkLabel("删除方式"), cmbDel));

            ComboBox cmbSwitch = new ComboBox();
            cmbSwitch.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSwitch.Width = 170;
            cmbSwitch.Margin = new Padding(0, 6, 0, 0);
            cmbSwitch.Items.AddRange(new object[] { "长按万能键弹圆盘", "长按后左右滑动" });
            cmbSwitch.SelectedIndex = (s.SwitchMode == "swipe") ? 1 : 0;
            colR.Controls.Add(Row(MkLabel("Wheel 切换"), cmbSwitch));

            // ---------- 万能键：四个分区各绑一个动作（以前是写死的） ----------
            colR.Controls.Add(Section("万能键"));
            ComboBox[] keyBox = new ComboBox[4];
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
                keyBox[i] = kb;
                // 选中就立刻写进设置：不依赖"确定"按钮里那段保存循环（之前那里没生效）
                {
                    int myI = i; ComboBox self = kb;
                    kb.SelectedIndexChanged += new EventHandler(delegate(object o, EventArgs e2) {
                        if (self.SelectedIndex >= 0) s.SetKeyAction(myI, Settings.KeyActionIds[self.SelectedIndex]);
                    });
                }
                if (i % 2 == 0)
                {
                    ComboBox kb2 = null; string dir2 = null;
                    if (i + 1 < 4) { dir2 = keyDir[i + 1]; }
                    Label l1 = MkLabel(keyDir[i]);
                    if (dir2 != null)
                    {
                        // 一行放两个方向，省竖直空间
                        ComboBox kb1 = kb;
                        ComboBox kb3 = new ComboBox();
                        kb3.DropDownStyle = ComboBoxStyle.DropDownList;
                        kb3.Width = 150;
                        kb3.Margin = new Padding(0, 6, 0, 0);
                        for (int j = 0; j < Settings.KeyActionIds.Length; j++)
                            kb3.Items.Add(Settings.KeyActionName(Settings.KeyActionIds[j]));
                        string cur3 = s.KeyActionAt(i + 1);
                        int idx3 = 0;
                        for (int j = 0; j < Settings.KeyActionIds.Length; j++) if (Settings.KeyActionIds[j] == cur3) idx3 = j;
                        kb3.SelectedIndex = idx3;
                        keyBox[i + 1] = kb3;
                        {
                            int myI2 = i + 1; ComboBox self2 = kb3;
                            kb3.SelectedIndexChanged += new EventHandler(delegate(object o, EventArgs e4) {
                                if (self2.SelectedIndex >= 0) s.SetKeyAction(myI2, Settings.KeyActionIds[self2.SelectedIndex]);
                            });
                        }
                        kb2 = kb3;
                        colR.Controls.Add(Row(l1, kb1, Gap(16), MkLabel(dir2), kb2));
                    }
                    else
                    {
                        colR.Controls.Add(Row(l1, kb));
                    }
                }
            }
            Label keyHint = MkLabel("按住万能键弹出圆盘，往哪个方向松手就执行哪个动作");
            keyHint.ForeColor = Color.FromArgb(140, 146, 158);
            colR.Controls.Add(keyHint);

            RoundButton ok = new RoundButton();
            ok.Text = "确定";
            ok.Size = new Size(104, 36);
            ok.Fill = Color.FromArgb(0, 122, 204);
            ok.FillHover = Color.FromArgb(0, 140, 232);
            ok.TextColor = Color.White;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            ok.Margin = new Padding(10, 0, 0, 0);
            ok.Click += new EventHandler(delegate(object o, EventArgs e2) {
                s.SaveToDisk = chkDisk.Checked;
                s.Dir = txtDir.Text.Trim();
                s.MaxCount = (int)numMax.Value;
                s.AutoHide = chkAuto.Checked;
                s.AutoHideSeconds = (int)numSec.Value;
                s.AlwaysOnTop = chkTop.Checked;
                s.ThumbSize = (int)numThumb.Value;
                s.Radius = (int)numRad.Value;
                s.Slots = (int)numSlots.Value;
                s.LabelSize = (int)numLabel.Value;
                s.Corner = IndexCorner(cmbCorner.SelectedIndex);
                s.AutoStart = chkAutoStart.Checked;
                s.DeleteMode = (cmbDel.SelectedIndex == 1) ? "single" : "double";
                s.SwitchMode = (cmbSwitch.SelectedIndex == 1) ? "swipe" : "radial";
                s.PeekPercent = (int)numPeek.Value;
                s.UiScale = scVals[cmbScale.SelectedIndex < 0 ? 0 : cmbScale.SelectedIndex];
                s.CollapseMode = chkCollapse.Checked;
                s.ClipboardImport = chkClip.Checked;
                s.GlassRefresh = chkGlassRefresh.Checked;
                s.ExpandSpeed = ringVals[cmbRing.SelectedIndex < 0 ? 2 : cmbRing.SelectedIndex];
                s.CollapseSpeed = ringVals[cmbRing2.SelectedIndex < 0 ? 2 : cmbRing2.SelectedIndex];
                s.NubSingle = chkSingle.Checked;
                s.ShowBalloon = chkBalloon.Checked;
                s.DragOutAsFile = chkDragFile.Checked;
                s.CheckUpdate = chkUpdate.Checked;
                // 保存万能键四分区。这里必须立刻 s.Save() 落盘：
                // 否则下次打开设置窗口会从文件里读到旧值，一点确定就把刚改的打回原形
                // （"圆盘上的动作名改完不变"的根因）。这条行为现在由 tests\behavior-test.cs 守着。
                try
                {
                    for (int ki = 0; ki < 4; ki++)
                    {
                        if (keyBox[ki] == null) continue;
                        int ksel = keyBox[ki].SelectedIndex;
                        if (ksel < 0) ksel = 0;
                        s.SetKeyAction(ki, Settings.KeyActionIds[ksel]);
                    }
                    s.Save();
                }
                catch (Exception kex) { Err.Log("SettingsSaveKey", kex); }
                s.IntroAnim = chkIntroAnim.Checked;
                s.UiStyle = (cmbStyle.SelectedIndex == 1) ? "flat" : (cmbStyle.SelectedIndex == 2 ? "solid" : "neu");
                s.AccentIndex = cmbAccent.SelectedIndex - 1;
                s.GlassPercent = (int)numGlass.Value;
                s.CardRadius = (int)numRadius.Value;
                s.ShadowPercent = (int)numShadow.Value;
                s.AnimSpeed = (cmbAnim.SelectedIndex == 0) ? 70 : (cmbAnim.SelectedIndex == 2 ? 140 : 100);
                s.ShowNameLabel = chkName.Checked;
                s.ShowCountLabel = chkCount.Checked;
                if (cmb.SelectedItem != null) s.Hotkey = cmb.SelectedItem.ToString();
                AutoRun.Apply(s.AutoStart);
                s.Save();
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
            cancel.Margin = new Padding(10, 0, 0, 0);
            cancel.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });

            RoundButton guide = new RoundButton();
            guide.Text = "新手引导";
            guide.Size = new Size(104, 36);
            guide.Fill = Color.FromArgb(236, 240, 246);
            guide.FillHover = Color.FromArgb(226, 233, 243);
            guide.TextColor = Color.FromArgb(40, 90, 150);
            guide.Font = new Font("Microsoft YaHei UI", 10f);
            guide.Margin = new Padding(0, 0, 0, 0);
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
            reset.Margin = new Padding(10, 0, 0, 0);
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

            // 按钮行：用四列表格把确定/取消靠右对齐。
            // 原来是一个 250px 的假占位控件硬顶 + FlowLayoutPanel，窗口 AutoSize 一算就错位/被裁
            TableLayoutPanel btnRow = new TableLayoutPanel();
            btnRow.ColumnCount = 5;
            btnRow.RowCount = 1;
            btnRow.AutoSize = true;
            btnRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            btnRow.Dock = DockStyle.Fill;
            btnRow.Margin = new Padding(0, 14, 0, 0);
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.Controls.Add(guide, 0, 0);
            btnRow.Controls.Add(reset, 1, 0);
            btnRow.Controls.Add(new Panel(), 2, 0);
            btnRow.Controls.Add(ok, 3, 0);
            btnRow.Controls.Add(cancel, 4, 0);
            btnRow.AutoSize = false;
            btnRow.Height = 42;
            btnRow.Dock = DockStyle.Fill;
            root.Controls.Add(btnRow);
            root.SetColumnSpan(btnRow, 2);

            Label about = new Label();
            about.AutoSize = true;
            about.Text = AppInfo.Name + "   v" + AppInfo.Version + "   ·   by " + AppInfo.Author + "   ·   BETA";
            about.ForeColor = Color.FromArgb(150, 150, 160);
            about.Margin = new Padding(0, 16, 0, 0);
            root.Controls.Add(about);

            // 屏幕矮的时候别把窗口顶出屏幕（两栏布局后一般用不到，保险起见留个上限）
            try
            {
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                MaximumSize = new Size((int)(wa.Width * 0.95), (int)(wa.Height * 0.94));
            }
            catch { }
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
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = false;
            f.Margin = new Padding(0, 5, 0, 5);   // 行距：设置项变多了，压紧一点免得窗口太高
            for (int i = 0; i < cs.Length; i++) f.Controls.Add(cs[i]);
            return f;
        }
    }
}
