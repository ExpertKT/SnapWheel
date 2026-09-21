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
    // 设置窗口的四个页面构建（从 75-SettingsForm.cs 拆出来，纯搬移，行为不变）。
    // 拆的理由：主文件原本 1607 行，页面构建占了 462 行，改一处要翻很久。
    // partial class 让一个类写在多个文件里，编译时合并。
    partial class SettingsForm
    {
        // ---- 第 1 页：行为与快捷键 ----
        void BuildPage1()
        {
            TableLayoutPanel g = _pages[0];
            SetupRows(g, 10);   // 0.6.0：多了Lang.T("拖出后保留一份", "Keep a copy after drag-out")
            Settings s = _s;

            g.Controls.Add(Section(Lang.T("行为", "Behaviour")), 0, 0);
            g.Controls.Add(Section(Lang.T("快捷键与操作", "Shortcuts")), 1, 0);

            _chkDisk = new CheckBox();
            _chkDisk.AutoSize = true;
            _chkDisk.Text = Lang.T("保存到硬盘（否则只存内存，退出即清）", "Save to disk (otherwise memory only, cleared on exit)");
            _chkDisk.Checked = s.SaveToDisk;
            _chkDisk.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkDisk, 0, 1);

            _cmbHotkey = new ComboBox();
            _cmbHotkey.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbHotkey.Width = S(170);
            _cmbHotkey.Margin = new Padding(0, 6, 0, 0);
            _cmbHotkey.Items.AddRange(HotkeyUtil.Names);
            _cmbHotkey.SelectedItem = s.Hotkey;
            if (_cmbHotkey.SelectedIndex < 0) _cmbHotkey.SelectedIndex = 0;
            g.Controls.Add(Row(MkLabel(Lang.T("截图热键", "Capture hotkey")), _cmbHotkey), 1, 1);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.AutoSize = true;
            _chkAutoStart.Text = Lang.T("开机自动启动（登录后自动在后台运行）", "Start with Windows\n(runs in background after sign-in)");
            _chkAutoStart.Checked = AutoRun.IsEnabled();
            _chkAutoStart.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkAutoStart, 0, 2);

            _cmbCorner = new ComboBox();
            _cmbCorner.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCorner.Width = S(170);
            _cmbCorner.Margin = new Padding(0, 6, 0, 0);
            _cmbCorner.Items.AddRange(new object[] { Lang.T("左下角", "Bottom left"), Lang.T("右下角", "Bottom right"), Lang.T("左上角", "Top left"), Lang.T("右上角", "Top right") });
            _cmbCorner.SelectedIndex = CornerIndex(s.Corner);
            g.Controls.Add(Row(MkLabel(Lang.T("圆环位置", "Ring position")), _cmbCorner), 1, 2);

            _chkAuto = new CheckBox();
            _chkAuto.AutoSize = true;
            _chkAuto.Text = Lang.T("空闲后自动收起轮盘", "Auto-collapse the ring when idle");
            _chkAuto.Checked = s.AutoHide;
            _chkAuto.Margin = new Padding(0, 4, 0, 4);
            _numSec = Num(2, 600, s.AutoHideSeconds);
            g.Controls.Add(Row(_chkAuto, Gap(16), MkLabel(Lang.T("空闲秒数", "Idle seconds")), _numSec), 0, 3);

            _cmbDel = new ComboBox();
            _cmbDel.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDel.Width = S(170);
            _cmbDel.Margin = new Padding(0, 6, 0, 0);
            _cmbDel.Items.AddRange(new object[] { Lang.T("双击右键删除", "Double right-click to delete"), Lang.T("单击右键删除", "Right-click to delete") });
            _cmbDel.SelectedIndex = (s.DeleteMode == "single") ? 1 : 0;
            g.Controls.Add(Row(MkLabel(Lang.T("删除方式", "Delete gesture")), _cmbDel), 1, 3);

            // 轮盘贴哪条边。
            // 为什么要有这个：Windows 的"工作区"**总是**扣掉任务栏那一条 ——
            // 哪怕任务栏是自动隐藏的，也照样预留 48 像素。
            // 于是自动隐藏的用户会看到轮盘底下悬着一条看不见的空隙，像没靠到底。
            _cmbEdge = new ComboBox();
            _cmbEdge.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbEdge.Width = S(170);
            _cmbEdge.Margin = new Padding(0, 6, 0, 0);
            _cmbEdge.Items.AddRange(new object[] {
                Lang.T("自动（任务栏隐藏时贴屏幕边）", "Auto (screen edge when the taskbar auto-hides)"),
                Lang.T("贴屏幕边（会被任务栏压住一角）", "Screen edge (may sit under the taskbar)"),
                Lang.T("贴工作区边（永远避让任务栏）", "Work-area edge (always avoids the taskbar)") });
            _cmbEdge.SelectedIndex = (s.EdgeAnchor == "screen") ? 1 : (s.EdgeAnchor == "work") ? 2 : 0;
            // 放在第 8 行第 1 列：第 3、4 行都满了，第 5、6 行被"保存目录""剪贴板"横跨两列占掉。
            g.Controls.Add(Row(MkLabel(Lang.T("轮盘靠边方式", "Which edge the ring hugs")), _cmbEdge), 1, 8);

            _chkTop = new CheckBox();
            _chkTop.AutoSize = true;
            _chkTop.Text = Lang.T("总在最前（始终置顶显示）", "Always on top");
            _chkTop.Checked = s.AlwaysOnTop;
            _chkTop.Margin = new Padding(0, 4, 0, 4);
            g.Controls.Add(_chkTop, 0, 4);

            _cmbSwitch = new ComboBox();
            _cmbSwitch.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbSwitch.Width = S(170);
            _cmbSwitch.Margin = new Padding(0, 6, 0, 0);
            _cmbSwitch.Items.AddRange(new object[] { Lang.T("长按万能键弹圆盘", "Long-press the universal key for the dial"), Lang.T("长按后左右滑动", "Long-press then slide left/right") });
            _cmbSwitch.SelectedIndex = (s.SwitchMode == "swipe") ? 1 : 0;
            g.Controls.Add(Row(MkLabel(Lang.T("Wheel 切换", "Wheel switching")), _cmbSwitch), 1, 4);

            // 保存目录这一行本来就宽，横跨两列（否则两列加起来会顶破窗口宽度）
            _txtDir = new TextBox();
            _txtDir.Text = s.Dir;
            _txtDir.Width = S(300);
            _txtDir.Margin = new Padding(0, 5, 8, 0);
            Button browse = new Button();
            browse.Text = Lang.T("浏览", "Browsing");
            browse.AutoSize = true;
            browse.MinimumSize = new Size(S(60), S(26));
            browse.Margin = new Padding(0, 4, 0, 0);
            browse.Click += new EventHandler(delegate(object o, EventArgs e2) {
                FolderBrowserDialog d = new FolderBrowserDialog();
                if (d.ShowDialog() == DialogResult.OK) _txtDir.Text = d.SelectedPath;
            });
            Control dirRow = Row(MkLabel(Lang.T("保存目录", "Save folder")), _txtDir, browse);
            g.Controls.Add(dirRow, 0, 5);
            g.SetColumnSpan(dirRow, 2);

            _chkClip = new CheckBox();
            _chkClip.AutoSize = true;
            _chkClip.Text = Lang.T("复制图片后自动收进轮盘", "Collect copied images automatically");
            _chkClip.Checked = s.ClipboardImport;
            // 新勾选框：这一行的排版是量出来的，别随手改。
            // 完整提示（"要立刻粘贴时直接 Ctrl+V"）量出来是 342px，塞回左列会把第 1 页顶到 825px
            // （两列最小宽度 317 + 360 = 677，页面只有 720）—— 所以这一行改成**横跨两列**：
            // 跨列行不进任何一列的最小宽度，整行 167+6+342+6+128 = 658 ≤ 720 ✓。
            // 代价只有一个：原来在右列的气泡勾选框跟着流到本行第三个，说明括号去掉
            // （留着的话整行 762 > 720，右列会挨着裁）—— Lang.T("显示托盘气泡提示", "Show tray balloon tips")这个名字本身已经说明它是什么。
            _chkCopy = new CheckBox();
            _chkCopy.AutoSize = true;
            _chkCopy.Text = Lang.T("截图后同时复制到剪贴板（要立刻粘贴时直接 Ctrl+V）", "Also copy to the clipboard on capture\n(so Ctrl+V just works)");
            _chkCopy.Checked = s.CopyOnCapture;
            _chkCopy.Margin = new Padding(S(6), 3, 0, 3);

            _chkBalloon = new CheckBox();
            _chkBalloon.AutoSize = true;
            _chkBalloon.Text = Lang.T("显示托盘气泡提示", "Show tray balloon tips");
            _chkBalloon.Checked = s.ShowBalloon;
            _chkBalloon.Margin = new Padding(S(6), 3, 0, 3);

            Control clipRow = Row(_chkClip, _chkCopy, _chkBalloon);
            g.Controls.Add(clipRow, 0, 6);
            g.SetColumnSpan(clipRow, 2);

            _chkUpdate = new CheckBox();
            _chkUpdate.AutoSize = true;
            _chkUpdate.Text = Lang.T("启动时检查有没有新版本（只提示，不自动安装）", "Check for updates on start\n(notify only, never auto-install)");
            _chkUpdate.Checked = s.CheckUpdate;
            g.Controls.Add(Row(_chkUpdate), 0, 7);

            // 拖出之后要不要在环上留一份（默认留）：拖出是 Copy 语义，留着才能再拖给别的窗口
            _chkKeep = new CheckBox();
            _chkKeep.AutoSize = true;
            _chkKeep.Text = Lang.T("缩略图拖出去后，环上保留一份（关掉就是拖出去即从环上移走）", "Keep a copy in the ring after\ndragging a thumbnail out");
            _chkKeep.Checked = s.KeepAfterDragOut;
            g.Controls.Add(Row(_chkKeep), 0, 8);

            // 界面语言（0.6.0 第一轮 i18n）：启动时生效，切换后要重启
            // 界面语言（0.6.0 第一轮 i18n）：启动时生效，切换后要重启
            // 用 FlowLayoutPanel 自动排：原来手工算 x 坐标（lbLang.PreferredWidth + 10），
            // PreferredWidth 一旦不准，标签就会压住下拉框（用户反馈的"有遮挡"）。
            Label lbLang = new Label();
            lbLang.AutoSize = true;
            lbLang.Text = Lang.T("界面语言（切换后重启生效）", "Language (restart to apply)");
            lbLang.ForeColor = Color.FromArgb(60, 64, 74);
            lbLang.Margin = new Padding(0, 6, 10, 0);
            _cbLang = new ComboBox();
            _cbLang.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbLang.Items.AddRange(new object[] { Lang.T("跟随系统", "Follow system"), Lang.T("中文", "Chinese"), "English" });
            _cbLang.SelectedIndex = (s.UiLanguage == "en") ? 2 : (s.UiLanguage == "zh" ? 1 : 0);
            _cbLang.Width = S(160);
            _cbLang.Margin = new Padding(0);
            FlowLayoutPanel langRow = new FlowLayoutPanel();
            langRow.AutoSize = true;
            langRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            langRow.FlowDirection = FlowDirection.LeftToRight;
            langRow.WrapContents = false;
            langRow.Margin = new Padding(0);
            langRow.Padding = new Padding(0);
            langRow.Controls.Add(lbLang);
            langRow.Controls.Add(_cbLang);
            g.Controls.Add(Row(langRow), 0, 9);

            _chkDragFile = new CheckBox();
            _chkDragFile.AutoSize = true;
            _chkDragFile.Text = Lang.T("拖出时同时带上\"文件\"（拖到桌面/文件夹会落地成文件）", "Attach a real file when dragging out (dropping onto the desktop / a folder writes a file)");
            _chkDragFile.Checked = s.DragOutAsFile;
            g.Controls.Add(Row(_chkDragFile), 1, 7);
        }

        // ---- 第 2 页：轮盘与外观 ----
        void BuildPage2()
        {
            TableLayoutPanel g = _pages[1];
            SetupRows(g, 8);
            Settings s = _s;

            g.Controls.Add(Section(Lang.T("外观", "Appearance")), 0, 0);

            _numMax = Num(1, 999, s.MaxCount);
            _numThumb = Num(40, 260, s.ThumbSize);
            g.Controls.Add(Row(MkLabel(Lang.T("最多保留张数", "Max items kept")), _numMax, Gap(24), MkLabel(Lang.T("缩略图大小", "Thumbnail size")), _numThumb), 0, 1);

            _chkCollapse = new CheckBox();
            _chkCollapse.AutoSize = true;
            _chkCollapse.Text = Lang.T("收起状态：缩到屏幕边上留个小把手", "Collapse into a small pull-tab\nat the screen edge");
            _chkCollapse.Checked = s.CollapseMode;
            g.Controls.Add(Row(_chkCollapse), 1, 1);

            _numPeek = Num(120, 500, s.PeekPercent);
            // 顺手把 0.5.3 的"截图后重置滚动位置"放在这一行的空处：这一行只有标签 + 数值框，
            // 右边空着一大片。**刻意不单独占一行** —— 第 2 页再加一行要多 31px，
            // 760×574（窗口允许缩到的最小尺寸）下页面格只有 346px、内容已经要 324px，加一行就顶出去被裁了。
            _chkScrollReset = new CheckBox();
            _chkScrollReset.AutoSize = true;
            _chkScrollReset.Text = Lang.T("截图后把滚动位置重置到最新那张（好让滑入动画看得见）", "Reset scroll to the newest item\nafter capture");
            _chkScrollReset.Checked = s.ResetScrollOnCapture;
            _chkScrollReset.Margin = new Padding(S(30), 4, 0, 4);
            Control peekRow = Row(MkLabel(Lang.T("长按放大(%)", "Hold-to-zoom (%)")), _numPeek, _chkScrollReset);
            g.Controls.Add(peekRow, 0, 2);
            g.SetColumnSpan(peekRow, 2);

            // 下面这几行本身就宽（标签 + 下拉 + 说明），横跨两列 —— 两列并排会顶破窗口宽度
            _cmbScale = new ComboBox();
            _cmbScale.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbScale.FlatStyle = FlatStyle.Flat;
            _cmbScale.Width = S(170);
            _cmbScale.Margin = new Padding(0, 6, 0, 0);
            _cmbScale.Items.Add(Lang.T("自动（按显示器 DPI）", "Auto (by monitor DPI)"));
            for (int i = 1; i < scVals.Length; i++) _cmbScale.Items.Add(scVals[i] + "%");
            _cmbScale.SelectedIndex = 0;
            for (int i = 0; i < scVals.Length; i++) if (scVals[i] == s.UiScale) _cmbScale.SelectedIndex = i;
            Label hint = new Label();
            hint.AutoSize = true;
            hint.Text = Lang.T("（整块轮盘等比放大，含文字和图标）", "(scales the whole ring, text and icons included)");
            hint.ForeColor = Color.FromArgb(150, 152, 160);
            hint.Margin = new Padding(0, 10, 0, 0);
            Control scaleRow = Row(MkLabel(Lang.T("界面缩放", "UI scale")), _cmbScale, Gap(12), hint);
            g.Controls.Add(scaleRow, 0, 3);
            g.SetColumnSpan(scaleRow, 2);

            // 收起 / 展开的动画速度（独立于Lang.T("动画速度", "Animation speed")）
            _cmbRing = new ComboBox();
            _cmbRing.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRing.FlatStyle = FlatStyle.Flat;
            _cmbRing.Width = S(130);
            _cmbRing.Margin = new Padding(0, 6, 0, 0);
            for (int i = 0; i < ringNames.Length; i++) _cmbRing.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            _cmbRing.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.ExpandSpeed) _cmbRing.SelectedIndex = i;
            Label hintRing = new Label();
            hintRing.AutoSize = true;
            hintRing.Text = Lang.T("（只管收起 / 展开；百分比越大越快）", "(collapse / expand only; higher = faster)");
            hintRing.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing.Margin = new Padding(0, 10, 0, 0);
            Control ringRow = Row(MkLabel(Lang.T("展开速度", "Expand speed")), _cmbRing, Gap(10), hintRing);
            g.Controls.Add(ringRow, 0, 4);
            g.SetColumnSpan(ringRow, 2);

            _cmbRing2 = new ComboBox();
            _cmbRing2.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRing2.FlatStyle = FlatStyle.Flat;
            _cmbRing2.Width = S(130);
            _cmbRing2.Margin = new Padding(0, 6, 0, 0);
            for (int i = 0; i < ringNames.Length; i++) _cmbRing2.Items.Add(ringNames[i] + "（" + ringVals[i] + "%）");
            _cmbRing2.SelectedIndex = 2;
            for (int i = 0; i < ringVals.Length; i++) if (ringVals[i] == s.CollapseSpeed) _cmbRing2.SelectedIndex = i;
            Label hintRing2 = new Label();
            hintRing2.AutoSize = true;
            hintRing2.Text = Lang.T("（默认比展开快一档，收起要干脆）", "(one notch faster than expand by default)");
            hintRing2.ForeColor = Color.FromArgb(150, 152, 160);
            hintRing2.Margin = new Padding(0, 10, 0, 0);
            Control ring2Row = Row(MkLabel(Lang.T("收起速度", "Collapse speed")), _cmbRing2, Gap(10), hintRing2);
            g.Controls.Add(ring2Row, 0, 5);
            g.SetColumnSpan(ring2Row, 2);

            _numRad = Num(120, 700, s.Radius);
            _numSlots = Num(2, 12, s.Slots);
            _numLabel = Num(9, 40, s.LabelSize);
            Control radRow = Row(MkLabel(Lang.T("环半径", "Ring radius")), _numRad, Gap(24), MkLabel(Lang.T("弧上张数", "Items on the arc")), _numSlots,
                                 Gap(24), MkLabel(Lang.T("序号字号", "Index font size")), _numLabel);
            g.Controls.Add(radRow, 0, 6);
            g.SetColumnSpan(radRow, 2);

            _chkSingle = new CheckBox();
            _chkSingle.AutoSize = true;
            _chkSingle.Text = Lang.T("只用一个把手：左边那个点一下展开、再点一下收起（任务栏自动隐藏时更省事）", "Single handle: click to expand,\nclick again to collapse");
            _chkSingle.Checked = s.NubSingle;
            Control singleRow = Row(_chkSingle);
            g.Controls.Add(singleRow, 0, 7);
            g.SetColumnSpan(singleRow, 2);
        }

        // ---- 第 3 页：风格 ----
        void BuildPage3()
        {
            TableLayoutPanel g = _pages[2];
            SetupRows(g, 6);   // v1.0：多了「有生命感」那三个开关 + 演示模式
            Settings s = _s;

            g.Controls.Add(Section(Lang.T("风格", "Style")), 0, 0);

            _cmbStyle = new ComboBox();
            _cmbStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbStyle.FlatStyle = FlatStyle.Flat;
            _cmbStyle.Width = S(170);
            _cmbStyle.Margin = new Padding(0, 6, 0, 0);
            _cmbStyle.Items.AddRange(new object[] { Lang.T("新拟态 + 毛玻璃", "Neumorphic + frosted glass"), Lang.T("纯扁平", "Flat"), Lang.T("高对比（不透明）", "High contrast (opaque)") });
            _cmbStyle.SelectedIndex = (s.UiStyle == "flat") ? 1 : (s.UiStyle == "solid" ? 2 : 0);

            _cmbAccent = new ComboBox();
            _cmbAccent.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAccent.FlatStyle = FlatStyle.Flat;
            _cmbAccent.Width = S(170);
            _cmbAccent.Margin = new Padding(0, 6, 0, 0);
            _cmbAccent.Items.Add(Lang.T("跟随 Wheel 颜色", "Follow wheel colour"));
            for (int i = 0; i < Palette.Names.Length; i++) _cmbAccent.Items.Add("统一：" + Palette.Names[i]);
            _cmbAccent.SelectedIndex = (s.AccentIndex >= 0 && s.AccentIndex < Palette.Names.Length) ? s.AccentIndex + 1 : 0;
            Control styleRow = Row(MkLabel(Lang.T("界面风格", "UI style")), _cmbStyle, Gap(24), MkLabel(Lang.T("主题色", "Accent colour")), _cmbAccent);
            g.Controls.Add(styleRow, 0, 1);
            g.SetColumnSpan(styleRow, 2);

            _cmbAnim = new ComboBox();
            _cmbAnim.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAnim.FlatStyle = FlatStyle.Flat;
            _cmbAnim.Width = S(170);
            _cmbAnim.Margin = new Padding(0, 6, 0, 0);
            _cmbAnim.Items.AddRange(new object[] { Lang.T("慢", "Slow"), Lang.T("标准", "Normal"), Lang.T("快", "Fast") });
            _cmbAnim.SelectedIndex = (s.AnimSpeed <= 85) ? 0 : (s.AnimSpeed >= 120 ? 2 : 1);

            _chkName = new CheckBox();
            _chkName.AutoSize = true;
            _chkName.Text = Lang.T("显示名称标签", "Show name label");
            _chkName.Checked = s.ShowNameLabel;
            _chkName.Margin = new Padding(0, 10, 0, 0);
            _chkCount = new CheckBox();
            _chkCount.AutoSize = true;
            _chkCount.Text = Lang.T("显示计数标签", "Show counter label");
            _chkCount.Checked = s.ShowCountLabel;
            _chkCount.Margin = new Padding(S(20), 10, 0, 0);
            Control animRow = Row(MkLabel(Lang.T("动画速度", "Animation speed")), _cmbAnim, Gap(24), _chkName, _chkCount);
            g.Controls.Add(animRow, 0, 2);
            g.SetColumnSpan(animRow, 2);

            _chkGlassRefresh = new CheckBox();
            _chkGlassRefresh.AutoSize = true;
            _chkGlassRefresh.Text = Lang.T("毛玻璃定时刷新（轮盘挂久了背景也是新的）", "Refresh frosted glass periodically\n(background stays current)");
            _chkGlassRefresh.Checked = s.GlassRefresh;
            g.Controls.Add(Row(_chkGlassRefresh), 0, 3);

            _chkIntroAnim = new CheckBox();
            _chkIntroAnim.AutoSize = true;
            _chkIntroAnim.Text = Lang.T("启动时播放开启动画", "Play the startup animation");
            _chkIntroAnim.Checked = s.IntroAnim;
            g.Controls.Add(Row(_chkIntroAnim), 1, 3);

            // ---- v1.0「有生命感」的三个开关 ----
            // 放在这一页（风格）最后一行：它们不影响功能，只影响"看起来怎么样"，
            // 和上面那一行的"显示名称/计数标签"是同一类东西。
            _chkRipple = new CheckBox();
            _chkRipple.AutoSize = true;
            _chkRipple.Text = Lang.T("涟漪（新图进来时扩散一圈）", "Ripple (a ring spreads out when an image arrives)");
            _chkRipple.Checked = s.Ripple;
            _chkRipple.Margin = new Padding(0, 6, 0, 0);

            _chkRingShadow = new CheckBox();
            _chkRingShadow.AutoSize = true;
            _chkRingShadow.Text = Lang.T("环有影子", "Shadow under the ring");
            _chkRingShadow.Checked = s.RingShadow;
            _chkRingShadow.Margin = new Padding(S(20), 6, 0, 0);

            _chkDayMood = new CheckBox();
            _chkDayMood.AutoSize = true;
            _chkDayMood.Text = Lang.T("时间感（早上偏暖、深夜变暗）", "Time of day (warmer in the morning, dimmer at night)");
            _chkDayMood.Checked = s.DayMood;
            _chkDayMood.Margin = new Padding(S(20), 6, 0, 0);

            Control moodRow = Row(_chkRipple, _chkRingShadow, _chkDayMood);
            g.Controls.Add(moodRow, 0, 4);
            g.SetColumnSpan(moodRow, 2);

            // 演示模式（v1.0）：录屏录不到轮盘 —— 因为它默认对屏幕捕获隐身。
            // 打开这一项才能把轮盘录进视频里；代价是自己截图时轮盘会进图，
            // 所以这里同时把毛玻璃的定时刷新停掉（见 WheelForm.ApplyCaptureVisibility）。
            _chkRecordable = new CheckBox();
            _chkRecordable.AutoSize = true;
            _chkRecordable.Text = Lang.T("录屏时能拍到轮盘（演示用；打开后自己截图也会带上它）",
                                        "Let screen recorders capture the ring\n(for demos; your own screenshots will include it too)");
            _chkRecordable.Checked = s.Recordable;
            _chkRecordable.Margin = new Padding(0, 8, 0, 0);
            Control recRow = Row(_chkRecordable);
            g.Controls.Add(recRow, 0, 5);
            g.SetColumnSpan(recRow, 2);
        }

        // ---- 第 4 页：万能键与高级 ----
        void BuildPage4()
        {
            TableLayoutPanel g = _pages[3];
            SetupRows(g, 11);            // 0.6.0：多了两行翻译接口（URL/模型 一行、API Key 一行）
            Settings s = _s;

            g.Controls.Add(Section(Lang.T("万能键", "Universal key")), 0, 0);

            // 四个分区各绑一个动作（以前是写死的）。选中就立刻写进设置：
            // 不依赖Lang.T("确定", "OK")里那段保存循环（之前那里没生效）。
            string[] keyDir = { Lang.T("上", "Up"), Lang.T("右", "Right"), Lang.T("下", "Down"), Lang.T("左", "Left") };
            for (int i = 0; i < 4; i++)
            {
                ComboBox kb = new ComboBox();
                kb.DropDownStyle = ComboBoxStyle.DropDownList;
                kb.Width = S(150);
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

            Label keyHint = MkLabel(Lang.T("按住万能键弹出圆盘，往哪个方向松手就执行哪个动作", "Hold the universal key and the dial appears; release towards a direction to run that action"));
            keyHint.ForeColor = Color.FromArgb(140, 146, 158);
            Control keyHintRow = Row(keyHint);
            g.Controls.Add(keyHintRow, 0, 3);
            g.SetColumnSpan(keyHintRow, 2);

            // ---------- 高级（外观微调）：默认折叠，需要时勾一下 ----------
            // 这几项对大多数人是噪音（第一次用不懂该选什么），所以默认藏起来。
            g.Controls.Add(Section(Lang.T("高级", "Advanced")), 0, 4);

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
            _advR.Controls.Add(Row(MkLabel(Lang.T("玻璃不透明度", "Glass opacity")), _numGlass, Gap(16), MkLabel(Lang.T("圆角(%)", "Corner radius (%)")), _numRadius,
                                   Gap(16), MkLabel(Lang.T("阴影强度", "Shadow strength")), _numShadow), 0, 0);

            CheckBox chkAdv = new CheckBox();
            chkAdv.AutoSize = true;
            chkAdv.Text = Lang.T("显示高级选项（外观微调：玻璃 / 圆角 / 阴影）", "Show advanced options\n(fine-tune glass / corners / shadow)");
            chkAdv.Margin = new Padding(0, 10, 0, 0);
            chkAdv.CheckedChanged += new EventHandler(delegate(object o, EventArgs e2) {
                _advR.Visible = chkAdv.Checked;
                _advR.PerformLayout();
                PerformLayout();
            });
            g.Controls.Add(chkAdv, 0, 5);

            // 省电模式（0.5.3）：归在Lang.T("高级", "Advanced")这组里 —— 它是电源相关的行为开关，不是外观微调。
            // 排在 _advR **之前**：展开"高级选项"时往下顶的是这一行，微调行仍紧贴它自己的开关。
            // 第 4 页由此从 7 行变 8 行（和其余页持平）；万一它成了最高的一页，
            // EnsureFit 会在翻到它时把窗口补够（只长大不裁切），不会切掉这一行。
            _chkPower = new CheckBox();
            _chkPower.AutoSize = true;
            _chkPower.Text = Lang.T("省电模式：用电池时停掉定时毛玻璃刷新、重绘减半（插电自动恢复）", "Power saving: on battery, stop glass refresh\nand halve redraws (auto-restores on AC)");
            _chkPower.Margin = new Padding(0, 10, 0, 0);
            _chkPower.Checked = s.PowerSave;
            g.Controls.Add(_chkPower, 0, 6);
            g.Controls.Add(_advR, 0, 7);

            // ---- 翻译接口（0.6.0）----
            // 为什么放这一页：它是Lang.T("高级", "Advanced")配置，普通用户留空即可（内置免费引擎链），
            // 愿意填 key 的人自己会翻到这里。**不放进 _advR** —— 那个组默认是隐藏的。
            _txtLlmUrl = new TextBox();
            _txtLlmUrl.Text = s.LlmUrl;
            _txtLlmUrl.Width = S(300);
            _txtLlmUrl.Margin = new Padding(0, 5, 0, 0);
            _txtLlmModel = new TextBox();
            _txtLlmModel.Text = s.LlmModel;
            _txtLlmModel.Width = S(150);
            _txtLlmModel.Margin = new Padding(0, 5, 0, 0);
            Control llmRow = Row(MkLabel(Lang.T("翻译接口", "Translation API")), _txtLlmUrl, Gap(10), MkLabel(Lang.T("模型", "Model")), _txtLlmModel);
            g.Controls.Add(llmRow, 0, 8);
            g.SetColumnSpan(llmRow, 2);

            _txtLlmKey = new TextBox();
            _txtLlmKey.UseSystemPasswordChar = true;      // 别在屏幕上明着显示 key
            _txtLlmKey.Text = s.LlmKey;
            _txtLlmKey.Width = S(300);
            _txtLlmKey.Margin = new Padding(0, 5, 0, 0);
            Label llmKeyHint = new Label();
            llmKeyHint.AutoSize = true;
            llmKeyHint.Text = Lang.T("（留空就用内置免费接口；key 只存在本机配置文件里）", "(leave empty to use the built-in free endpoint; the key is stored only in your local config file)");
            llmKeyHint.ForeColor = Color.FromArgb(150, 152, 160);
            llmKeyHint.Margin = new Padding(0, 10, 0, 0);
            Control keyRow = Row(MkLabel("API Key"), _txtLlmKey, Gap(10), llmKeyHint);
            g.Controls.Add(keyRow, 0, 9);
            g.SetColumnSpan(keyRow, 2);

            // 反馈入口（0.6.0）：一键提 issue（预填环境信息）+ 复制诊断信息。
            // 放在Lang.T("高级", "Advanced")页最下面：不占常用路径，但用户真遇到问题时找得到。
            RoundButton fb = new RoundButton();
            fb.Text = Lang.T("反馈 / 报告问题…", "Feedback / report a problem…");
            fb.Font = new Font("Microsoft YaHei UI", 10f);
            fb.Fill = Color.FromArgb(238, 240, 245);
            fb.FillHover = Color.FromArgb(226, 230, 238);
            fb.TextColor = Color.FromArgb(60, 64, 74);
            fb.Size = new Size(S(170), S(34));
            fb.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                FeedbackForm ff = new FeedbackForm();
                try { ff.ShowDialog(this); } catch { }
                try { ff.Dispose(); } catch { }
            });
            Control fbRow = Row(fb);
            g.Controls.Add(fbRow, 0, 10);
            g.SetColumnSpan(fbRow, 2);
        }
    }
}
