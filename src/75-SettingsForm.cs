// 注：本文件的"翻页动画"已拆到 75c-SettingsForm.Slide.cs，"页面构建"在 75a，"保存"在 75b
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
    partial class SettingsForm : Form, IMessageFilter
    {
        // ============================ 布局总则（务必先读） ============================
        // 1. 这个窗口的布局一律"坐标明确"：每张 TableLayoutPanel 都写死 RowCount/ColumnCount，
        //    每个控件都用 Add(控件, 列, 行) 指明格子 —— **绝不靠添加顺序排行**。
        //    v0.5.2 提速时就是因为挪了添加顺序，标题跑到最底下、按钮跑到最上面（"头和屁股长反了"）。
        // 2. 四页内容 = 四张页面格，同一时间只显示一张；每页都是"两列 + 行"的明确坐标。
        // 3. 每页的控件**第一次翻到那页才建**（懒建）：构造量降到 1/4，这是打开设置变快的主因。
        //    没建过的页 = 没被看过 = 没被改过，所以Lang.T("确定", "OK")时跳过它（值保持原样，不会被写回默认值）。
        // ==========================================================================
        TableLayoutPanel _root;
        Panel _body;
        PageDial _dial;
        readonly TableLayoutPanel[] _pages = new TableLayoutPanel[4];
        readonly Action[] _builders = new Action[4];
        readonly bool[] _built = new bool[4];
        int _cur = -1;
        int _openW, _openH;                  // 打开时的客户区尺寸（关闭时对比，用户拖过才写回设置）
        public Size ContentNeed;             // 四页里最大的"内容首选尺寸"（工具/测试看用）
        Settings _s;
        bool _filterAdded;

        // ---- 第 1 页「行为与快捷键」 ----
        CheckBox _chkDisk, _chkAutoStart, _chkAuto, _chkTop, _chkClip, _chkCopy, _chkBalloon, _chkUpdate, _chkDragFile;
        CheckBox _chkKeep;                   // 拖出后是否在环上留一份（0.6.0）
        ComboBox _cbLang;                    // 界面语言（0.6.0）
        TextBox _txtDir;
        NumericUpDown _numSec;
        ComboBox _cmbHotkey, _cmbCorner, _cmbDel, _cmbSwitch, _cmbEdge;
        // ---- 第 2 页「轮盘与外观」 ----
        NumericUpDown _numMax, _numThumb, _numRad, _numSlots, _numLabel, _numPeek;
        ComboBox _cmbScale, _cmbRing, _cmbRing2;
        CheckBox _chkCollapse, _chkSingle, _chkIntroAnim, _chkScrollReset;
        // ---- 第 3 页「风格」 ----
        ComboBox _cmbStyle, _cmbAccent, _cmbAnim;
        CheckBox _chkName, _chkCount, _chkGlassRefresh;
        // v1.0「有生命感」的三个开关（默认开、可关，见 61d-WheelForm.Atmos.cs）
        CheckBox _chkRipple, _chkRingShadow, _chkDayMood;
        // ---- 第 4 页「万能键与高级」 ----
        readonly ComboBox[] _keyBox = new ComboBox[4];
        NumericUpDown _numGlass, _numRadius, _numShadow;
        TableLayoutPanel _advR;
        CheckBox _chkPower;                  // 省电模式（0.5.3）：只在电池供电时生效，见 12-Power.cs
        // 翻译接口（0.6.0）：留空就走内置免费引擎链（有道 → MyMemory 保底）；
        // 填上就是 OpenAI 兼容接口（DeepSeek / 豆包 / 通义 / 本地 Ollama），译文质量最好。
        TextBox _txtLlmUrl, _txtLlmKey, _txtLlmModel;

        // 窗口出厂尺寸 = 允许缩到的最小尺寸（**逻辑像素**，实际会乘 DPI 系数 K）。
        // 这个数不能随手改小：四页内容是按 720px 宽（760 - 40 边距）排的。
        const int MinClientW = 760, MinClientH = 574;

        // 测试用：强制指定 DPI 缩放系数（0 = 按真实 DPI 判断）。
        // 和 Elev.ForceForTest 一个道理 —— 不然"150% 屏幕下会不会被裁"这件事永远只能在那种屏上手测。
        public static float ForceKForTest = 0f;

        float K = 1f;                          // DPI 缩放系数（长度类尺寸都乘它，字体点数不乘）

        int S(float v) { return (int)Math.Round(v * K); }
        int S(int v) { return (int)Math.Round(v * K); }
        Padding Pad(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }

        static readonly int[] scVals = { 0, 80, 90, 100, 110, 125, 150, 175, 200, 250 };
        static readonly int[] ringVals = { 220, 150, 100, 80, 60, 45 };
        static readonly string[] ringNames = { Lang.T("极快", "Very fast"), Lang.T("快", "Fast"), Lang.T("标准", "Normal"), Lang.T("慢", "Slow"), Lang.T("很慢", "Very slow"), Lang.T("最慢", "Slowest") };

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

        // 双层缓冲 + 整窗合成：翻页滑动时子控件（文字）不会闪、不会重影。
        // WS_EX_COMPOSITED 是这里的关键 —— WinForms 的 DoubleBuffered 只管控件自己那一块，
        // 子控件（标签/勾选框/下拉框各自都是独立窗口）挪动时的重画它管不着，只有整窗合成能压住。
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000;      // WS_EX_COMPOSITED
                return cp;
            }
        }

        // 滚轮：一格 = 一页。向上滚 = 往前一页（和主界面轮盘一致）；环跟着转一格，停手后吸附回正角度。
        // 拖动中忽略；动画中允许接管（ShowPage 会先把上一段精确收尾，再从当前进度滑向新页，不跳回）。
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e.Delta != 0) { WheelStep(e.Delta > 0 ? -1 : 1); return; }   // 自己吃掉，不再往上冒（免得翻两次）
            base.OnMouseWheel(e);
        }

        // 滚一格：step = -1 往前一页、+1 往后一页
        bool WheelStep(int step)
        {
            if (step == 0) return false;
            if (_dial != null && _dial.Dragging) return false;        // 拖着转环的时候滚轮不参与
            int want = _cur + step;
            if (want < 0) want = 0;
            if (want > _pages.Length - 1) want = _pages.Length - 1;
            if (want == _cur) return false;                           // 顶到头了：什么都不做
            if (_dial != null) _dial.Nudge(want > _cur ? 1 : -1);     // 环先跟着转一格（有动画，不是硬跳）
            ShowPage(want);                                           // 内容页切过去（动画中直接接管）
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 自己的定时器必须自己停（v0.5.1 的教训：窗口关了定时器还在跑，白烧 CPU）
                if (_fadeTimer != null) { try { _fadeTimer.Stop(); _fadeTimer.Dispose(); } catch { } _fadeTimer = null; }
                if (_ptimer != null) { try { _ptimer.Stop(); _ptimer.Dispose(); } catch { } _ptimer = null; }
                if (_pwatch != null) { try { _pwatch.Stop(); } catch { } _pwatch = null; }
                FreeSlide();
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
                if (delta != 0) { WheelStep(delta > 0 ? -1 : 1); return true; }
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


        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 拖边框改大小：翻页滑动用的两张位图是按旧尺寸拍的，先精确收尾（不然会贴一张旧尺寸的图）；
            // 再把当前页按新尺寸重新排一次。页面是 Dock=Fill + 行高 AutoSize，
            // 所以"多出来的高度"自然全给了 root 第 2 行（当前页那格 Percent(100)），
            // 标题（第 0 行）、分页器（第 1 行）、按钮行（第 3 行）、页脚（第 4 行）都是固定高，不会跟着错位。
            if (_body == null || _cur < 0) return;      // 构造函数里设 ClientSize 时还没有页面
            if (Animating) FinishNow();
            SnapTo(_cur);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Gfx.RepaintAll(this);          // 补一次整窗重绘，按钮四角不会闪白块
            StartFadeIn();
        }

        // ---- 打开时的淡入 ----
        // 用户反馈：点设置后窗口出现较慢，而且**期间没有任何东西缓解"加载感/卡顿感"**。
        // 实测 `new SettingsForm()` 约 250ms（构造**体内**的标记加起来只有 ~15ms，
        // 大头还没定位到，见下面注释），那 250ms 里屏幕上什么都不发生；
        // 等到窗口出现又是"啪"地全亮 —— 一头一尾都是硬切。
        // 这一下至少把**出现**变成渐显：眼睛看到的是"它在长出来"，不是"突然多了一坨"。
        //
        // ⚠️ 还没解决的是那 250ms 本身。之所以没顺手去"优化"它：
        //    构造体内的分段计时加起来只有 15ms，说明时间不在我改得到的那段代码里 ——
        //    没定位到就改，只会把别的地方弄坏（这个项目在"猜一个改法试一次"上摔过很多次）。
        System.Windows.Forms.Timer _fadeTimer;
        float _fadeT = 0f;
        bool _fadeDone;

        void StartFadeIn()
        {
            if (_fadeDone) return;          // 只为"打开"那一次，切页不重放
            _fadeDone = true;
            try
            {
                Opacity = 0.34;
                _fadeTimer = new System.Windows.Forms.Timer();
                _fadeTimer.Interval = 15;
                _fadeTimer.Tick += new EventHandler(delegate(object o, EventArgs e2)
                {
                    try
                    {
                        _fadeT += 0.17f;
                        if (_fadeT >= 1f) { _fadeT = 1f; _fadeTimer.Stop(); _fadeTimer.Dispose(); _fadeTimer = null; }
                        Opacity = 0.34 + 0.66 * _fadeT;
                    }
                    catch { try { Opacity = 1.0; } catch { } }
                });
                _fadeTimer.Start();
            }
            catch { try { Opacity = 1.0; } catch { } }
        }

        public SettingsForm(Settings s)
        {
            _s = s;
            Text = AppInfo.Name + Lang.T(" 设置", " Settings");
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(250, 250, 252);
            // ---- DPI 缩放系数（0.5.3 补）----
            // 这个窗口的布局全是"写死的物理像素"。在 150% 缩放（144DPI）的屏幕上，**字会按 DPI 放大 1.5 倍，
            // 窗口却一动不动** —— 于是内容顶出可视区：用户报的"有些选项的字显示不全""下面还有部分被遮住"
            // （底部按钮行和页脚被切）就是这个。修法：**所有Lang.T("长度", "Length")尺寸都乘 K**；
            // **字体的点数一个都不动**（点数本来就跟着 DPI 走，再乘一次会变成 2.25 倍）。
            try { K = ForceKForTest > 0f ? ForceKForTest : Native.DpiScaleOf(IntPtr.Zero); } catch { K = 1f; }
            if (!(K >= 1f)) K = 1f;               // 小于 100% 不缩（缩了字反而更小、更看不清）
            if (K > 3f) K = 3f;
            // 有限度地自由调整大小（用户报"有些选项的字显示不全"）：可以拖边框放大 / 缩小，
            //   下限 = 出厂尺寸（760×574 逻辑像素 × K，四页内容在这个尺寸下都排得下）
            //   上限 = 屏幕工作区的 92%（再大就没意义，也不该长到屏幕外面去）
            // 只动窗口大小：**四页的布局一个字没动**（坐标明确 + 每页懒建 + 保存只写建过的页）。
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;                  // 上限已经被 MaximumSize 夹住了，最大化键没有意义
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = false;                     // 不随内容长高长胖：大小由用户拖（翻页仍然代替滚动）
            DoubleBuffered = true;                // 滑动时整窗不闪（配合 CreateParams 里的 WS_EX_COMPOSITED）
            ClientSize = new Size(S(MinClientW), S(MinClientH));
            MinimumSize = SizeFromClientSize(new Size(S(MinClientW), S(MinClientH)));
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                if (wa.Width > 100 && wa.Height > 100)
                    MaximumSize = new Size(Math.Max(MinimumSize.Width, (int)(wa.Width * 0.92f)),
                                           Math.Max(MinimumSize.Height, (int)(wa.Height * 0.92f)));
            }
            catch { }
            // 底部原来只留 12px：页脚 "by exper7" 那行的真实文字格比字体行高高，末几行像素会被窗口底边切掉
            Padding = Pad(20, 14, 20, 18);

            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.AutoSize = false;
            root.Dock = DockStyle.Fill;
            root.Margin = new Padding(0);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(28)));    // 0 标题
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(96)));    // 1 轮盘式分页器
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));      // 2 当前页
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));    // 3 按钮行（右下）
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(32)));    // 4 版本行（22 太小：标签真实高度+边距放不下，末几行像素会被裁）
            _root = root;

            Label head = new Label();
            // 别信 AutoSize：标签的高度是按"字体行高"算的（13pt 粗体只给 22px），
            // 但文字真正要占的格子是 25px —— 底下一排会被削掉（用户报的"标题被遮挡了一点"，
            // 和上一轮 ComboBox"报 23px 实高 27px"是同一个坑）。这里改成自己量出真实宽高。
            head.AutoSize = false;
            head.Text = AppInfo.Name + Lang.T(" 设置", " Settings");
            head.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(32, 34, 38);
            head.TextAlign = ContentAlignment.MiddleLeft;
            Size hsz = TextRenderer.MeasureText(head.Text, head.Font);
            head.Size = new Size(hsz.Width + 2, hsz.Height + 1);        // 真实文字格 + 1px 余量
            head.Margin = new Padding(0, 0, 0, 0);                      // 不靠 Margin 占位，行高 28 自己留白
            root.Controls.Add(head, 0, 0);

            // 分页器：顶部小圆弧，四个扇区 = 四页（点扇区 / 滚轮翻页），新拟态凸起 + 当前页高亮
            _dial = new PageDial();
            _dial.Names = new string[] { Lang.T("行为与快捷键", "Behaviour & shortcuts"), Lang.T("轮盘与外观", "Ring & appearance"), Lang.T("风格", "Style"), Lang.T("万能键与高级", "Universal key & advanced") };
            _dial.BackColor = Color.FromArgb(250, 250, 252);
            _dial.Dock = DockStyle.Fill;
            _dial.Margin = new Padding(0);
            _dial.K = K;                       // 分页器的弧线几何也跟着 DPI 走
            _dial.PagePicked += new EventHandler(delegate(object o, EventArgs e2) { TryGoto(_dial.Picked); });
            // 拖动松手：内容页跟着高亮走。这里用 ShowPage（程序性切页）而不是 TryGoto ——
            // 拖动允许直接接管上一次还没走完的过渡（从当前进度收尾后再滑向新页），不许被防连点吞掉。
            _dial.PageDropped += new EventHandler(delegate(object o, EventArgs e2) { ShowPage(_dial.Current); });
            root.Controls.Add(_dial, 0, 1);

            // 四张页面格：先建好挂上（空白），内容懒建；非当前页 Visible=false
            Panel body = new BufferedPanel();     // 双缓冲：滑动时这一块整块重画
            body.Paint += new PaintEventHandler(BodyPaint);   // 滑动期间由它贴两张页位图
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
            ok.Text = Lang.T("确定", "OK");
            ok.Size = new Size(S(104), S(36));
            ok.Fill = Color.FromArgb(0, 122, 204);
            ok.FillHover = Color.FromArgb(0, 140, 232);
            ok.TextColor = Color.White;
            ok.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            ok.Margin = Pad(10, 2, 0, 0);
            ok.Click += new EventHandler(delegate(object o, EventArgs e2) {
                SaveFromUi();
                DialogResult = DialogResult.OK;
                Close();
            });
            RoundButton cancel = new RoundButton();
            cancel.Text = "取消";
            cancel.Size = new Size(S(104), S(36));
            cancel.Fill = Color.FromArgb(234, 235, 240);
            cancel.FillHover = Color.FromArgb(222, 224, 230);
            cancel.TextColor = Color.FromArgb(58, 60, 66);
            cancel.Font = new Font("Microsoft YaHei UI", 10f);
            cancel.Margin = Pad(10, 2, 0, 0);
            cancel.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });

            RoundButton guide = new RoundButton();
            guide.Text = Lang.T("新手引导", "Getting started");
            guide.Size = new Size(S(104), S(36));
            guide.Fill = Color.FromArgb(236, 240, 246);
            guide.FillHover = Color.FromArgb(226, 233, 243);
            guide.TextColor = Color.FromArgb(40, 90, 150);
            guide.Font = new Font("Microsoft YaHei UI", 10f);
            guide.Margin = Pad(0, 2, 0, 0);
            guide.Click += new EventHandler(delegate(object o, EventArgs e2)
            { GuideForm gf = new GuideForm(); gf.ShowDialog(this); });

            // 还原默认设置：只重置设置项，不动你的图片和 Wheel 内容
            RoundButton reset = new RoundButton();
            reset.Text = Lang.T("还原默认", "Restore defaults");
            reset.Size = new Size(S(104), S(36));
            reset.Fill = Color.FromArgb(252, 238, 236);
            reset.FillHover = Color.FromArgb(248, 224, 220);
            reset.TextColor = Color.FromArgb(178, 66, 52);
            reset.Font = new Font("Microsoft YaHei UI", 10f);
            reset.Margin = Pad(10, 2, 0, 0);
            reset.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                DialogResult r2 = MessageBox.Show(this,
                    "把所有设置恢复成默认值？\r\n\r\n（不会动你的图片和 Wheel 内容，只重置外观/行为等设置项）",
                    Lang.T("还原默认设置", "Restore defaults"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r2 != DialogResult.OK) return;
                Settings def = new Settings();
                Settings.CopyInto(def, s);
                AutoRun.Apply(s.AutoStart);
                s.Save();
                DialogResult = DialogResult.OK;
                Close();
            });

            // 打赏：收款码弹窗（刻意不写进说明、不显眼，见 84-Reward.cs）
            RoundButton tip = new RoundButton();
            tip.Text = Lang.T("打赏", "Tip the author");
            tip.Size = new Size(S(104), S(36));
            tip.Fill = Color.FromArgb(252, 246, 234);
            tip.FillHover = Color.FromArgb(248, 236, 216);
            tip.TextColor = Color.FromArgb(160, 116, 30);
            tip.Font = new Font("Microsoft YaHei UI", 10f);
            tip.Margin = Pad(10, 2, 0, 0);
            tip.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                try { using (RewardForm rf = new RewardForm()) rf.ShowDialog(this); }
                catch (Exception rex) { Err.Log("RewardForm", rex); }
            });

            // 按钮行：用六列表格把确定/取消靠右对齐（引导/还原/打赏在左）。
            TableLayoutPanel btnRow = new TableLayoutPanel();
            btnRow.ColumnCount = 6;
            btnRow.RowCount = 1;
            btnRow.AutoSize = false;
            btnRow.Dock = DockStyle.Fill;
            btnRow.Margin = new Padding(0);
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            btnRow.Controls.Add(guide, 0, 0);
            btnRow.Controls.Add(reset, 1, 0);
            btnRow.Controls.Add(tip, 2, 0);           // 「还原默认」右边，样式和其它按钮一致
            // 中间只放个"撑宽"的空位（把确定/取消推到右边）。这里必须给它一个小尺寸：
            // Panel 的默认尺寸是 200×100，放进 40px 高的按钮行里会顶出行高、被裁（渲染工具会报"被裁"）。
            Panel btnSpacer = new Panel();
            btnSpacer.Size = new Size(1, 1);
            btnSpacer.Margin = new Padding(0);
            btnRow.Controls.Add(btnSpacer, 3, 0);
            btnRow.Controls.Add(ok, 4, 0);
            btnRow.Controls.Add(cancel, 5, 0);
            root.Controls.Add(btnRow, 0, 3);

            Label about = new Label();
            about.AutoSize = true;
            about.Text = AppInfo.Name + "   v" + AppInfo.Version + "   ·   by " + AppInfo.Author;
            about.ForeColor = Color.FromArgb(150, 150, 160);
            about.Margin = Pad(0, 2, 0, 0);
            root.Controls.Add(about, 0, 4);

            ShowPage(0);             // 只建第 1 页
            Controls.Add(root);      // 全部建完才挂上去：整棵树只排一次
            PerformLayout();
            SizeToContent();         // 再按"内容首选尺寸 × DPI"定默认尺寸 / 最小尺寸（见方法里的说明）

        }

        // ============================ 默认尺寸 / 记住用户拖过的尺寸 ============================
        // 用户报"一打开显示不全、还得自己拖"：所以默认尺寸不再写死，改成**按内容反推**：
        //     需要的最小客户区 = max(出厂尺寸, 那一页的"内容首选尺寸" + 固定行高 + 内边距)
        // 全部都是**物理像素**（页面里的字号已经按真实 DPI 渲染，所以量出来的首选尺寸天然含 DPI 系数，
        // 不需要再乘一次 K —— 和"长度乘 K、字体点数不乘"是同一条规矩）。
        //   · 量哪一页？**先量马上要显示的那一页**（第 1 页，构造时就建好了），其余各页等第一次翻到时
        //     在 BuildPage 里量（EnsureFit）。这样打开不比原来慢多少（"每页懒建"的初衷保住了：
        //     实测四页全量要 +130ms），但**任何一页被显示出来时都已经按内容补足过尺寸**，不会裁。
        //   · MinimumSize 用同一个值（"最小尺寸不小于内容"），上限仍是屏幕工作区 92%。
        //   · 用户拖过之后在关闭时写回 settings（WinW/WinH），下次打开就用他的尺寸；
        //     存下来的值一律夹进 [最小, 最大]（换到别的缩放比屏幕上也绝不会小于内容）。
        void SizeToContent()
        {
            // 出厂下限先当最小尺寸（量出来的只会比它大）
            MinimumSize = SizeFromClientSize(new Size(S(MinClientW), S(MinClientH)));
            ApplyMaxSize();
            ClientSize = new Size(S(MinClientW), S(MinClientH));
            if (_s != null && _s.WinW > 0 && _s.WinH > 0) { ClientSize = new Size(_s.WinW, _s.WinH); ClampClient(); }
            EnsureFit(0);                                   // 第 1 页按内容（不够就把窗口长大）
            ClampClient();
            _openW = ClientSize.Width; _openH = ClientSize.Height;
        }

        void ApplyMaxSize()
        {
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                if (wa.Width > 100 && wa.Height > 100)
                    MaximumSize = new Size(Math.Max(MinimumSize.Width, (int)(wa.Width * 0.92f)),
                                           Math.Max(MinimumSize.Height, (int)(wa.Height * 0.92f)));
            }
            catch { }
        }

        // 把第 i 页量一次；不够就把 MinimumSize 和窗口一起长大（只长大不缩小 —— 用户拖过的尺寸不会被某一页打回）
        void EnsureFit(int i)
        {
            if (i < 0 || i >= _pages.Length) return;
            try
            {
                Size pref = _pages[i].GetPreferredSize(new Size(Math.Max(1, S(MinClientW) - Padding.Horizontal), 0));
                if (pref.Width > ContentNeed.Width || pref.Height > ContentNeed.Height)
                    ContentNeed = new Size(Math.Max(ContentNeed.Width, pref.Width), Math.Max(ContentNeed.Height, pref.Height));
                int w = Math.Max(S(MinClientW), ContentNeed.Width + Padding.Horizontal);
                int h = Math.Max(S(MinClientH), ContentNeed.Height + S(28) + S(96) + S(40) + S(32) + Padding.Vertical);
                MinimumSize = SizeFromClientSize(new Size(w, h));
                ApplyMaxSize();
                if (ClientSize.Width < w || ClientSize.Height < h)
                {
                    ClientSize = new Size(Math.Max(ClientSize.Width, w), Math.Max(ClientSize.Height, h));
                    ClampClient();
                }
            }
            catch (Exception ex) { Err.Log("SettingsEnsureFit", ex); }
        }

        // 客户区夹进 [最小, 最大]（范围是以"窗口外框"记的，所以换算出客户区的边界再比）
        void ClampClient()
        {
            try
            {
                int minW = MinimumSize.Width - (Width - ClientSize.Width);
                int minH = MinimumSize.Height - (Height - ClientSize.Height);
                int maxW = MaximumSize.Width - (Width - ClientSize.Width);
                int maxH = MaximumSize.Height - (Height - ClientSize.Height);
                int w = ClientSize.Width, h = ClientSize.Height;
                if (w < minW) w = minW; if (h < minH) h = minH;
                if (maxW > 0 && w > maxW) w = maxW;
                if (maxH > 0 && h > maxH) h = maxH;
                if (w != ClientSize.Width || h != ClientSize.Height) ClientSize = new Size(w, h);
            }
            catch { }
        }

        // 关窗口时把用户拖出来的尺寸记进设置（和打开时不一样才写，免得每次都动配置文件）
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try
            {
                ClampClient();
                if (_s != null && (ClientSize.Width != _openW || ClientSize.Height != _openH))
                {
                    _s.WinW = ClientSize.Width; _s.WinH = ClientSize.Height;
                    _s.Save();
                }
            }
            catch (Exception ex) { Err.Log("SettingsWinSize", ex); }
        }

        // ============================ 四页的内容 ============================
        // 每页一张"两列 + 行"的格子，行号写死；一格里放一个控件（成组的行用 Row(...) 包一层）。
        // 行号从 0 开始，写 RowCount 时要 ≥ 最大行号 + 1，否则那行不显示。

        // 四个页面构建函数已拆到 75a-SettingsForm.Pages.cs（partial class，编译时合并）

        // ============================ 翻页 ============================
        // 第一次翻到某页才建那页的控件；没建过的页 = 没看过 = 没改过。
        // 翻页带 160ms 位移动画（15ms 一帧 ≈ 11 帧，实测约 165ms）：新页从一侧滑进来、
        // 旧页朝反方向滑出去（Panel 没有透明度，所以只用位移 + 分页器高亮同步过渡，不跳变）。
        // 两页在动画期间**永远刚好拼满可视区** —— 一个在 [x, x+W]、另一个在 [x±W, x±W+W]
        // —— 所以既不重叠也不留缝。
        const int PageAnimMs = 240;      // 0.6.0：160 -> 240ms（用户反馈翻页过渡帧率偏低，拉长时间让帧数更多、更顺）      // 140~200ms 档；15ms 一帧 ≈ 11 帧
        System.Windows.Forms.Timer _ptimer;
        System.Diagnostics.Stopwatch _pwatch;       // 进度按"真实过去了多少毫秒"算，不按帧数累加
        int _animFrom = -1, _animTo = -1;
        int _dialFrom = -1;             // 分页器高亮过渡的起点（拖动时它和 _animFrom 可能不是同一页）
        int _animDir = 1;               // 翻页方向：+1 = 新页从右边进来
        float _animT = 1f;

        // ---------- 翻页用位图滑动（不是"挪真控件"）----------
        // 页里的标签/勾选框/下拉框各自都是独立窗口，每帧挪一次就要重画一次 —— 屏幕上就是"字在闪/抖"
        // （文字每帧被重新光栅化尤其明显，实测一次翻页里体内容器被重排 23 次、页容器 25 次）。
        // 现在：切页时两页各拍一张位图，真控件全部藏起来，body 自己按整数偏移贴这两张图。
        // 双缓冲面板贴图 = 一帧只画一次，文字是同一份光栅，不重排、不重画、不抖。
        Bitmap _slideA, _slideB;        // A = 旧页（滑出）B = 新页（滑入）
        int _slideAx, _slideBx;




        // "正在翻页"以动画状态为准，不看定时器 —— 定时器被别的东西停掉时防连点也不能失效
        bool Animating { get { return _animFrom >= 0 && _animTo >= 0 && _animT < 1f; } }

        void ShowPage(int i) { ShowPage(i, true); }









        // 界面上所有控件的值写回 Settings 的逻辑已拆到 75b-SettingsForm.Save.cs

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
            BufferedGrid g = new BufferedGrid();     // 双缓冲页容器：整页滑进滑出不会闪
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

        Label MkLabel(string t)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = t;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = Pad(0, 10, 12, 0);
            return l;
        }

        Label Section(string t)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = t;
            l.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(0, 122, 204);
            l.Margin = Pad(0, 14, 0, 2);
            return l;
        }

        Control Gap(int w)
        {
            Control c = new Control();
            c.Width = S(w); c.Height = 1;
            c.Margin = new Padding(0);
            return c;
        }

        NumericUpDown Num(int mn, int mx, int val)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = mn; n.Maximum = mx; n.Value = val;
            n.Width = S(72);
            n.Height = S(26);
            n.Margin = Pad(0, 7, 10, 0);
            return n;
        }

        FlowLayoutPanel Row(params Control[] cs)
        {
            RowPanel f = new RowPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = false;
            f.Margin = Pad(0, 5, 0, 5);   // 行距：设置项变多了，压紧一点免得窗口太高（跟着 DPI 走）
            for (int i = 0; i < cs.Length; i++) f.Controls.Add(cs[i]);
            return f;
        }
    }

    // ============================ 双缓冲容器 ============================
    // 翻页是把整页容器在窗口里挪位置，单缓冲的话每次挪动都要"擦底再画"，
    // 里面的文字看起来就在闪。Panel/TableLayoutPanel 默认都是单缓冲，这里统一开成双缓冲。
    class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
            DoubleBuffered = true;
        }
    }

    class BufferedGrid : TableLayoutPanel
    {
        public BufferedGrid()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
            DoubleBuffered = true;
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
        public RowPanel()
        {
            // 行容器也会跟着页容器一起挪，同样要双缓冲（用户报的"字在闪"）
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
            DoubleBuffered = true;
        }

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
        public float K = 1f;                // DPI 缩放系数（由 SettingsForm 传进来；只作用在Lang.T("长度", "Length")上）
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
        const float BandW = 24f;            // 弧的厚度（乘以 K 才是实际像素）
        const float LiftPx = 2.2f;          // 当前页那一瓣往外凸出去多少（凸起也是平滑过渡的，乘以 K）

        // ---- 按住拖动转环（v0.5.2 追加：和主界面轮盘"能转"的手感对齐）----
        // 环整圈 = 四瓣 = 150°（SweepTotal），所以"转回正位"的周期就是 150°：
        // 角度 ≡ 0 (mod 150) 时每一页都恰好落在自己那一瓣的位上 = 和静态布局逐像素相同的样子。
        // 拖动：整环跟着指针连续转（增量累加，快速来回/转好几圈都不丢），转到指位上的那一瓣高亮；
        // 松手：ease-out 回到"离松手角度最近的正角度"，最多回弹 75°。
        public const int DragSlop = 4;      // 按下后位移 < 4px 算点击（点扇区切页），≥ 4px 算拖动
        public const int SnapMs = 160;      // 吸附时长：和翻页过渡一个量级（140~200ms）
        public float Angle { get; private set; }        // 环当前旋转角（度）
        public float TargetAngle { get; private set; }  // 这次吸附的目标角（静止时 ≡ 0 mod 150）
        public bool Dragging { get { return _drag == 2; } }
        public event EventHandler PageDropped;          // 拖动松手：Current 才是用户选的那一页
        int _drag;                 // 0=没按 1=按着（还没过阈值）2=正在拖
        Point _downPt;
        float _downAngle;          // 按下那一刻的指针角
        float _lastPtAngle;        // 上一次的指针角（按增量累加，绕圈/快速来回都不跳）
        int _grabPage;             // 按下时指针在哪一瓣上
        System.Windows.Forms.Timer _snapTimer;
        System.Diagnostics.Stopwatch _snapWatch;
        float _snapFrom;
        int _wheelSteps;           // 滚轮从上次静止起累计转过的格数（停手后吸附回正角度时清零）

        public PageDial()
        {
            AnimT = 1f;                       // 默认"过渡已完成"：高亮就停在 AnimTo（也就是 Current）上
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        // 自己的定时器自己停（AGENT-NOTES：#8 窗口关了定时器还在跑）
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_snapTimer != null) { try { _snapTimer.Stop(); _snapTimer.Dispose(); } catch { } _snapTimer = null; }
                if (_snapWatch != null) { try { _snapWatch.Stop(); } catch { } _snapWatch = null; }
            }
            base.Dispose(disposing);
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
        float Ro { get { return Math.Min(92f * K, Height - 8f); } }
        float Ri { get { return Ro - BandW * K; } }

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

        // 第 i 瓣在"环转了 rot 度"之后的位置
        void SectorAt(int i, float rot, out float start, out float sweep, out PointF mid)
        {
            int n = Math.Max(1, Count);
            sweep = SweepTotal / n;
            start = 270f - SweepTotal / 2f + sweep * i + rot;
            double a = (start + sweep / 2f) * Math.PI / 180.0;
            float mr = (Ri + Ro) / 2f;
            mid = new PointF(Cx + (float)(Math.Cos(a) * mr), Cy + (float)(Math.Sin(a) * mr));
        }

        // 角度归一化：环每 150° 重复一次，所以画/算都只用最靠近 0 的那一份（视觉完全一样，数字不会越滚越大）
        static float Norm150(float a)
        {
            a = (float)(a - 150.0 * Math.Floor((a + 75.0) / 150.0));   // 落到 (-75, 75]
            return a;
        }

        // 指针相对圆心的角度（度，0=正右，顺时针为正）
        float PointerAngle(Point p)
        {
            double a = Math.Atan2(p.Y - Cy, p.X - Cx) * 180.0 / Math.PI;
            return (float)a;
        }

        // 两个指针角的差（归到 (-180,180]，这样绕着圆心转也不会跳）
        static float DeltaDeg(float now, float prev)
        {
            float d = (float)((now - prev + 540.0) % 360.0 - 180.0);
            return d;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            int n = Count;
            if (n <= 0) return;
            float rot = Angle;      // 环的旋转

            // 只画"窗口"那一段：把窗口环带当裁剪区，每瓣再按 ±150° 各画一份，
            // 于是转出去的那部分会从另一头转进来 —— 圆弧永远是一段完整的四瓣，不会缺角、也不会歪。
            using (GraphicsPath win = Band(Cx, Cy, Ri, Ro, 270f - SweepTotal / 2f, SweepTotal))
            {
                GraphicsState st = g.Save();
                g.SetClip(win, CombineMode.Replace);
                for (int k = -1; k <= 1; k++)
                {
                    float r2 = rot + k * SweepTotal;
                    for (int i = 0; i < n; i++)
                    {
                        float start, sweep; PointF mid;
                        SectorAt(i, r2, out start, out sweep, out mid);
                        float w = Weight(i);
                        bool hot = (i == _hover) && w < 0.5f && _drag == 0;
                        // 高亮 = 从"常态底"往主题色插值；同时整瓣沿半径往外凸一点（凸起跟着高亮一起走）
                        double midA = (start + sweep / 2f) * Math.PI / 180.0;
                        float lift = LiftPx * K * w;
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
                    }
                }
                g.Restore(st);
            }

            // 扇区里的序号：只画"整块都在窗口里"的那些（贴边的半瓣不画，免得半个数字挂在弧外）
            for (int k = -1; k <= 1; k++)
            {
                float r2 = rot + k * SweepTotal;
                for (int i = 0; i < n; i++)
                {
                    float start, sweep; PointF mid;
                    SectorAt(i, r2, out start, out sweep, out mid);
                    if (mid.X < 8 * K || mid.X > Width - 8 * K) continue;
                    // 中心角必须落在窗口内（留一点余量给数字本身）
                    double rel = (start + sweep / 2f) - (270.0 - SweepTotal / 2.0);
                    if (rel < 0) rel += 360.0;
                    if (rel < 13 || rel > SweepTotal - 13) continue;
                    Rectangle numRc = new Rectangle((int)mid.X - (int)(12 * K), (int)mid.Y - (int)(9 * K), (int)(24 * K), (int)(18 * K));
                    TextRenderer.DrawText(g, (i + 1).ToString(), NumFont, numRc,
                        Mix(NumIdle, Color.White, Weight(i)),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }

            // 圆环内圈里写页名：翻页时走到一半换字（不叠字、不跳页）
            int show = (AnimT < 0.5f && AnimFrom >= 0 && AnimFrom < n) ? AnimFrom : Current;
            if (show < 0 || show >= n) show = AnimTo >= 0 && AnimTo < n ? AnimTo : 0;
            string t = (show >= 0 && show < n) ? Names[show] : "";
            int tw = TextRenderer.MeasureText(t, TitleFont).Width + (int)(12 * K);
            Rectangle rc = new Rectangle((int)(Cx - tw / 2f), (int)(Cy - Ri + 34f * K), tw, (int)(26 * K));
            TextRenderer.DrawText(g, t, TitleFont, rc, Color.FromArgb(64, 70, 82),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // 命中的扇区；没命中返回 -1（环带内外各放宽 6px，好点一点）。带上环的旋转。
        int HitTest(Point p)
        {
            int n = Count;
            if (n <= 0) return -1;
            float dx = p.X - Cx, dy = p.Y - Cy;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            if (d > Ro + 6f || d < Ri - 6f) return -1;
            double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (ang < 0) ang += 360.0;
            double rel = ang - (270.0 - SweepTotal / 2.0) - Angle;   // 减掉环的旋转
            while (rel < 0) rel += 360.0;
            while (rel >= 360.0) rel -= 360.0;
            if (rel > SweepTotal) return -1;
            int idx = (int)(rel / (SweepTotal / n));
            return idx < n ? idx : n - 1;
        }

        // 拖动中"转到指位上的那一瓣"变成当前页：环转过的瓣数直接把选中页往回推
        void UpdateDragPage()
        {
            int n = Math.Max(1, Count);
            int steps = (int)Math.Round(Angle / (SweepTotal / n));
            int p = ((_grabPage - steps) % n + n) % n;
            if (p != Current)
            {
                Current = p;
                AnimFrom = p; AnimTo = p; AnimT = 1f;   // 拖动中不做补间：指到哪亮到哪（页名也跟着）
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            CancelSnap();                       // 上一次吸附没走完就直接接管：从当前角度接着拖，不先跳回去
            _drag = 1;
            _downPt = e.Location;
            _grabPage = HitTest(e.Location);
            if (_grabPage < 0) _grabPage = Current < 0 ? 0 : Current;   // 按在弧外也允许拖
            _downAngle = PointerAngle(e.Location);
            _lastPtAngle = _downAngle;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag == 0)
            {
                int i = HitTest(e.Location);
                if (i != _hover) { _hover = i; Invalidate(); }
                return;
            }
            if (_drag == 1)
            {
                int dx = e.X - _downPt.X, dy = e.Y - _downPt.Y;
                if (dx * dx + dy * dy < DragSlop * DragSlop) return;    // 还没过阈值：仍按"可能是点击"处理
                _drag = 2;
                Capture = true;
                // 基准取"按下那一刻"的指针角：环从按下点开始跟手，位移一点都不丢
                //（阈值只有 4px ≈ 3°，所以过阈值那一下最多也就 3°，看不出来）
                _lastPtAngle = _downAngle;
            }
            // 正在拖：环跟着指针连续转（增量累加，绕圈、快速来回都不丢）
            float r = (float)Math.Sqrt((e.X - Cx) * (e.X - Cx) + (e.Y - Cy) * (e.Y - Cy));
            float a = PointerAngle(e.Location);
            if (r < 8f) { _lastPtAngle = a; return; }   // 指针贴着圆心：角度没意义，只更新基准，离开时不跳
            Angle = Norm150(Angle + DeltaDeg(a, _lastPtAngle));
            _lastPtAngle = a;
            UpdateDragPage();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag == 0) return;
            int st = _drag;
            _drag = 0;
            if (Capture) Capture = false;
            if (st == 2) { FinishDrag(); return; }
            // 没过阈值 = 点击：点哪一瓣翻哪一页（老行为，只是改到松手时判定，才能和拖动区分）
            int i = HitTest(e.Location);
            if (i >= 0 && i != Current)
            {
                Picked = i;
                if (PagePicked != null) PagePicked(this, EventArgs.Empty);
            }
        }

        // 捕获被抢走（松手在窗口外、切窗口…）也照样收尾，绝不留半路状态
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_drag != 0 && !Capture) { _drag = 0; FinishDrag(); return; }
            if (_drag == 1) _drag = 0;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_drag != 0) return;                 // 拖着的时候移出窗口不算离开（有捕获，继续拖）
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        // 滚轮用：环往"翻到下一页/上一页"的方向转一格（有动画），停手后 SnapTick 会接着吸附回正角度。
        // 方向和拖动一致：往后翻一页 = 环往负方向转（东西从右边转进来）。
        public void Nudge(int pageDir)
        {
            if (_drag != 0) return;                                  // 拖着的时候滚轮不参与
            _wheelSteps += pageDir;
            AnimateRingTo(-(SweepTotal / Math.Max(1, Count)) * _wheelSteps);
        }

        // 让环从当前角度 ease-out 转到目标角度（拖动收尾和滚轮共用一套）
        void AnimateRingTo(float target)
        {
            TargetAngle = target;
            _snapFrom = Angle;
            _snapWatch = System.Diagnostics.Stopwatch.StartNew();
            if (_snapTimer == null)
            {
                _snapTimer = new System.Windows.Forms.Timer();
                _snapTimer.Interval = 15;           // 和翻页过渡同一个节拍
                _snapTimer.Tick += delegate(object o, EventArgs e2) { SnapTick(); };
            }
            _snapTimer.Start();
            Invalidate();
        }

        // 松手：吸附到离当前角度最近的正角度（≡0 mod 150°，也就是每页都回到自己扇区位的那个角度）
        void FinishDrag()
        {
            _wheelSteps = 0;
            AnimateRingTo((float)(Math.Round(Angle / SweepTotal) * SweepTotal));
            if (PageDropped != null) PageDropped(this, EventArgs.Empty);
        }

        // 直接接管（用户按下时上一次吸附还没走完）：角度保持不动，从当前进度接着拖
        void CancelSnap()
        {
            if (_snapTimer != null && _snapTimer.Enabled) _snapTimer.Stop();
            if (_snapWatch != null) { try { _snapWatch.Stop(); } catch { } _snapWatch = null; }
        }

        void SnapTick()
        {
            float t = _snapWatch == null ? 1f : (float)(_snapWatch.Elapsed.TotalMilliseconds / SnapMs);
            if (t >= 1f)
            {
                Angle = TargetAngle;                // 精确落到目标角，不留偏差
                CancelSnap();
                Invalidate();
                float home = (float)(Math.Round(Angle / SweepTotal) * SweepTotal);
                if (Math.Abs(Angle - home) > 0.0001f) AnimateRingTo(home);   // 滚轮转出去的那一格：接着吸附回正角度
                else _wheelSteps = 0;
                return;
            }
            Angle = _snapFrom + (TargetAngle - _snapFrom) * Gfx.EaseOut(t);
            Invalidate();
        }
    }
}
