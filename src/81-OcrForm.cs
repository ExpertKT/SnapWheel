using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace SnapWheel
{
    // 取字（OCR）的结果框：原文可编辑 + 一键翻译 + 各自可复制。
    // 打开时自动把原文放进剪贴板 —— 取字的目的就是"拿去用"，少一步是一步。
    class OcrForm : Form
    {
        readonly TextBox _src;
        readonly TextBox _dst;
        readonly RoundButton _tr;
        readonly Label _trState;
        bool _busy;
        string _translated = "";

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

        public OcrForm(string text)
        {
            if (text == null) text = "";
            bool copied = false;
            try { Clipboard.SetText(text); copied = true; } catch { }

            int chars = text.Replace("\r", "").Replace("\n", "").Length;
            Text = AppInfo.Name + Lang.T(" 取字", " OCR");
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;

            // ===================== 尺寸基准（DPI 修订） =====================
            // 原来这一整块是 96dpi 下量出来的死数字（24 / 46 / 74 / 94 / 170 / 274 / 316 / 560…）。
            // 本程序是 per-monitor DPI aware：150% 下字体被 GDI+ 按 DPI 放大渲染，死数字却一个不动，
            // 于是「认出来 N 个字」压住副标题、说明文字整句被切、按钮被挤出窗口右下角 ——
            // 用户报的「凡是涉及界面的都显示不全」就是这个根因。
            // 约定：**长度一律过 Ui.S()（乘 K），字体磅值一个都不乘**（乘了就是双倍放大）。
            int pad = Ui.S(24);        // 左右外边距（原来满篇写死 24）
            int top = Ui.S(16);        // 顶部外边距
            // 下面这几个间距就是设计稿里"标题底 → 46 → 74 → 94"之间的空档（写死的 y 减去标签自身高度）。
            // 现在标签高度是量出来的，所以这几个数是"真的空档"，不会被字变高顶掉 —— 100% 下看起来和原来一样。
            int gHeadSub = 0;          // 标题 → 副标题：原来 46 = 16 + 标题高，中间本来就没留空档
            int gSubL1 = Ui.S(7);      // 副标题 → 「原文」小标签（原来 = 74 - 46 - 副标题高）
            int gL1Box = 0;            // 小标签 → 原文框：原来 94 = 74 + 标签高，也是紧贴着放
            int gBoxRow = Ui.S(10);    // 原文框 → 按钮行（原来 = 274 - 94 - 170）
            int gRowBox = Ui.S(8);     // 按钮行 → 译文框（原来 = 316 - 274 - 34）
            int gBoxBot = Ui.S(12);    // 译文框 → 底部那排按钮
            int botPad = Ui.S(14);     // 底部外边距（原来 560 - 48 - 34 = 14）
            int boxH = Ui.S(170);      // 两个多行框的设计高度（一屏放不下时会在下面被压矮）
            SuspendLayout();

            Label head = new Label();
            head.Text = chars > 0 ? (Lang.T("认出来 ", "Recognised ") + chars + Lang.T(" 个字", " characters")) : Lang.T("没认出文字", "No text found");
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            Controls.Add(head);

            // ---- 窗口宽度：设计稿 620 × K，但不能被标题或屏幕挤到放不下 ----
            int clientW = Ui.S(620);
            int headNeed = head.PreferredSize.Width + pad * 2;   // 标题单行要占的宽度（含左右边距）
            if (headNeed > clientW) clientW = headNeed;
            int scrW = 0, scrH = 0;
            try
            {
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                scrW = wa.Width; scrH = wa.Height;
            }
            catch { }
            // 300% 时 620 × 3 = 1860，比屏幕还宽就没意义了（屏幕为准，文字反正能折行）
            if (scrW > 0 && clientW > scrW - Ui.S(24)) clientW = scrW - Ui.S(24);
            int contentW = clientW - pad * 2;                    // 会折行的标签最多占这么宽
            Ui.Wrap(head, contentW);                             // 标题兜底：真放不下宁可折行，也不许被窗口切掉

            Label sub = new Label();
            sub.Text = chars > 0
                ? (copied ? Lang.T("原文已复制到剪贴板；要用译文点下面的「翻译」", "Original copied to the clipboard; click Translate below for the translation")
                          : Lang.T("下面就是识别结果，可以改完再复制", "The recognised text is below - edit it if needed, then copy"))
                : Lang.T("换一块更清晰、字更大的区域再试试；倾斜或花哨的字体识别率会低一些", "Try a clearer area with larger text; slanted or decorative fonts are recognised less reliably");
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            // 这句最长（没认出字那版有 33 个字）：原来 AutoSize 不封顶，150% 下它比窗口还宽，右半边整句被切掉。
            // Wrap = AutoSize + MaximumSize(内容宽, 0)：放得下就是一行，放不下自己折行、自己报出真实高度。
            Ui.Wrap(sub, contentW);
            Controls.Add(sub);

            Label l1 = new Label();
            l1.Text = Lang.T("原文", "Original");
            l1.ForeColor = Color.FromArgb(120, 124, 134);
            Ui.OneLine(l1);                                      // 短标签：AutoSize 就够，行高别再写死
            Controls.Add(l1);

            // ---- 先把每块占多高量清楚，再决定各块的 y 和窗口得多高 ----
            // 高度一律问标签自己要（AutoSize 标签报的 Height / PreferredHeight **就是**它随后要占的高度），
            // 不再猜"9.5pt 一行大概 17px" —— 那种猜法正是 150% 下裁字的根源。
            int headH = head.PreferredHeight;
            int subH = sub.PreferredHeight;
            int l1H = l1.PreferredHeight;

            // 圆角按钮上的字是 TextRenderer 直接画在按钮矩形里的（见 40-RoundButton.OnPaint）：
            // 宽或高不够就**硬裁**，按钮不会自己缩字号、也不会自己变宽。所以宽高取「设计值 × K」和
            // 「文字实测 + 内边距」里的大者。字体先在这里建出来，是为了在摆控件之前就能算准按钮高度。
            Font fBtn = new Font("Microsoft YaHei UI", 10f);
            Font fBtnB = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            string trText = chars > 0 ? (Lang.T("翻译成", "Translate to") + Translate.TargetLabel(text)) : Lang.T("翻译", "Translate");
            int trW = BtnW(fBtnB, trText, 132);
            int trH = BtnH(fBtnB, trText, 34);
            string copySrcText = Lang.T("复制原文", "Copy original");
            int copySrcW = BtnW(fBtn, copySrcText, 102);
            int copySrcH = BtnH(fBtn, copySrcText, 34);
            int rowH = Math.Max(trH, copySrcH);                  // 按钮行的行高：按这一行里最高的按钮算
            string copyDstText = Lang.T("复制译文", "Copy translation");
            int copyDstW = BtnW(fBtn, copyDstText, 102);
            int copyDstH = BtnH(fBtn, copyDstText, 34);
            string closeText = Lang.T("关闭", "Close");
            int closeW = BtnW(fBtnB, closeText, 96);
            int closeH = BtnH(fBtnB, closeText, 36);
            int bottomH = Math.Max(copyDstH, closeH);            // 底部那排的行高

            // 除两个多行框之外的固定开销（用来判断"一屏放不放得下"）
            int fixedH = top + headH + gHeadSub + subH + gSubL1 + l1H + gL1Box
                       + gBoxRow + rowH + gRowBox + gBoxBot + bottomH + botPad;
            if (scrH > 0)
            {
                int avail = scrH - Ui.S(72);                     // 给标题栏和任务栏留点余量
                if (fixedH + boxH * 2 > avail)
                {
                    // 一屏放不下时**优先压两个多行框**：它们带滚动条，压矮了内容还能滚；
                    // 标题 / 说明 / 按钮压下去就是真的裁字，所以那些一个都不动。
                    int half = (avail - fixedH) / 2;
                    if (half < boxH) boxH = half;
                    if (boxH < Ui.S(80)) boxH = Ui.S(80);        // 再挤也留这么高，不然两个框没法用
                }
            }

            // ---- 各块的 y：一律"上一块的底边 + 间距"，不再写死 46 / 74 / 94 / 274 / 316 ----
            int y = top;
            head.Location = new Point(pad, y);
            y += headH + gHeadSub;
            sub.Location = new Point(Ui.S(27), y);               // x 的 27 是设计稿里相对标题(24)的错位，保持原样
            y += subH + gSubL1;
            l1.Location = new Point(pad, y);
            y += l1H + gL1Box;
            int srcY = y;
            y += boxH + gBoxRow;
            int rowY = y;
            y += rowH + gRowBox;
            int dstY = y;
            y += boxH + gBoxBot;                                 // y 到这里 = 底部那排按钮的顶边

            _src = new TextBox();
            _src.Multiline = true;
            _src.ScrollBars = ScrollBars.Both;
            _src.WordWrap = true;
            _src.Font = new Font("Microsoft YaHei UI", 11f);
            _src.BorderStyle = BorderStyle.FixedSingle;
            _src.BackColor = Color.White;
            _src.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _src.Location = new Point(pad, srcY);
            // 宽度：原来写死 ClientSize.Width - 48，可那个 48 不跟着 DPI 放大，150% 下右边会被吃掉一块
            _src.Size = new Size(contentW, boxH);
            _src.Text = text;
            Controls.Add(_src);

            // 翻译
            _tr = new RoundButton();
            _tr.Text = trText;
            _tr.Size = new Size(trW, trH);
            _tr.Fill = Color.FromArgb(0, 122, 204);
            _tr.FillHover = Color.FromArgb(0, 140, 232);
            _tr.TextColor = Color.White;
            _tr.Font = fBtnB;
            _tr.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _tr.Location = new Point(pad, rowY);
            _tr.Click += new EventHandler(delegate(object o, EventArgs e2) { DoTranslate(); });
            Controls.Add(_tr);

            RoundButton copySrc = new RoundButton();
            copySrc.Text = copySrcText;
            copySrc.Size = new Size(copySrcW, copySrcH);
            copySrc.Fill = Color.FromArgb(238, 240, 245);
            copySrc.FillHover = Color.FromArgb(226, 230, 238);
            copySrc.TextColor = Color.FromArgb(60, 64, 74);
            copySrc.Font = fBtn;
            copySrc.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            copySrc.Location = new Point(_tr.Right + Ui.S(8), rowY);   // 原来写死 164 = 24 + 132 + 8
            copySrc.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                try { Clipboard.SetText(_src.Text); _trState.Text = Lang.T("原文已复制", "Original copied"); } catch { }
            });
            Controls.Add(copySrc);

            _trState = new Label();
            _trState.Text = Lang.T("译文", "Translation");
            _trState.ForeColor = Color.FromArgb(120, 124, 134);
            _trState.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(_trState);
            int stateX = copySrc.Right + Ui.S(14);                      // 原来写死 280 = 164 + 102 + 14
            int stateW = Math.Max(Ui.S(80), clientW - pad - stateX);     // 右边不许越过窗口边距
            Ui.Wrap(_trState, stateW);
            // 这行字运行中会变长（Lang.T("翻译中…（用 MyMemory 免费接口，要联网）", "Translating… (free MyMemory endpoint, needs internet)")、失败原因），所以
            // ①宽度封顶让它能折行；②这一行的**行高按已知最长的那句预留**（Ui.TextH 量），
            // 免得它突然折成两行、压在下面的译文框上。文字本身不动，只是把位置算出来。
            int stateH = Ui.TextH(_trState, Lang.T("翻译中…（用 MyMemory 免费接口，要联网）", "Translating… (free MyMemory endpoint, needs internet)"), stateW);
            int stateY = rowY + Math.Max(0, (rowH - stateH) / 2);        // 原来 284：按钮行(274 高 34)里垂直居中
            _trState.Location = new Point(stateX, stateY);

            _dst = new TextBox();
            _dst.Multiline = true;
            _dst.ScrollBars = ScrollBars.Both;
            _dst.WordWrap = true;
            _dst.ReadOnly = true;
            _dst.Font = new Font("Microsoft YaHei UI", 11f);
            _dst.BorderStyle = BorderStyle.FixedSingle;
            _dst.BackColor = Color.FromArgb(248, 249, 252);
            _dst.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            // 译文框的 y：状态行万一真折成两行也要让开它（取更靠下的那个），原来写死 316
            if (stateY + stateH + Ui.S(4) > dstY) dstY = stateY + stateH + Ui.S(4);
            _dst.Location = new Point(pad, dstY);
            _dst.Size = new Size(contentW, boxH);
            Controls.Add(_dst);
            // 内容真正需要的高度：窗口至少得这么高（最后一步按它定 ClientSize）
            int needH = dstY + boxH + gBoxBot + bottomH + botPad;

            RoundButton copyDst = new RoundButton();
            copyDst.Text = copyDstText;
            copyDst.Size = new Size(copyDstW, copyDstH);
            copyDst.Fill = Color.FromArgb(238, 240, 245);
            copyDst.FillHover = Color.FromArgb(226, 230, 238);
            copyDst.TextColor = Color.FromArgb(60, 64, 74);
            copyDst.Font = fBtn;
            copyDst.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            // 位置在最后统一排：这排按钮贴的是窗口**底边**，而 ClientSize 到最后一刻才定
            copyDst.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                if (_translated.Length == 0) { _trState.Text = Lang.T("还没翻译呢", "Nothing translated yet"); return; }
                try { Clipboard.SetText(_translated); _trState.Text = Lang.T("译文已复制", "Translation copied"); } catch { }
            });
            Controls.Add(copyDst);

            RoundButton close = new RoundButton();
            close.Text = closeText;
            close.Size = new Size(closeW, closeH);
            close.Fill = Color.FromArgb(0, 122, 204);
            close.FillHover = Color.FromArgb(0, 140, 232);
            close.TextColor = Color.White;
            close.Font = fBtnB;
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;

            // ===================== 最后一步：按内容把窗口尺寸定下来 =====================
            // 上面的 y 全是"上一块的底边 + 间距"推出来的，两个多行框又是 170 × K，
            // 所以 150% 下内容自然比原来的 560 高。窗口不跟着长，底部那排按钮就被切在窗外 ——
            // 折行 / AutoSize 省下来的高度必须在**这里**兑现成窗口尺寸，否则等于白改。
            int clientH = Math.Max(needH, Ui.S(560));   // 不小于设计稿观感
            if (scrH > 0 && clientH > scrH - Ui.S(24) && needH <= scrH - Ui.S(24))
            {
                // 只是"设计稿最小观感"顶出了屏幕，可以缩回来；内容本身放得下（放不下就宁可窗口高一点，也不裁内容）
                clientH = scrH - Ui.S(24);
            }
            MinimumSize = new Size(Math.Min(Ui.S(420), clientW), Math.Min(Ui.S(380), clientH));
            ClientSize = new Size(clientW, clientH);
            ResumeLayout();

            // ClientSize 变了，贴边的那几个控件再放一遍：它们的 Anchor 基准是"入伙时"的旧尺寸，
            // 显式给一遍才能保证"离右边 24 / 离底边 14"就是设计稿的边距（Anchor 之后照样对用户拖窗口生效）。
            int wide = clientW - pad * 2;
            _src.Width = wide;
            _dst.Width = wide;
            int bottomLine = clientH - botPad;                              // 底部那排共用的底边（原来 = 560 - 48 + 36 的底）
            copyDst.Location = new Point(pad, bottomLine - copyDst.Height);
            close.Location = new Point(clientW - pad - close.Width, bottomLine - close.Height);
        }

        // 圆角按钮上的字是 TextRenderer 直接画在按钮矩形里（见 40-RoundButton.OnPaint）：
        // 宽或高不够就硬裁，按钮既不会缩字号也不会自己变宽。所以尺寸取「设计稿像素 × K」和
        // 「文字实测 + 内边距」里的大者 —— 换目标语言、换字体回退、更高的 DPI，都不会让按钮上的字只剩一半。
        static int BtnW(Font f, string text, int designW)
        {
            int w = Ui.S(designW);
            try
            {
                Size t = TextRenderer.MeasureText(text, f);
                int need = t.Width + Ui.S(24);
                if (need > w) w = need;
            }
            catch { }
            return w;
        }

        static int BtnH(Font f, string text, int designH)
        {
            int h = Ui.S(designH);
            try
            {
                Size t = TextRenderer.MeasureText(text, f);
                int need = t.Height + Ui.S(12);
                if (need > h) h = need;
            }
            catch { }
            return h;
        }

        // 翻译丢到后台线程去做：网络慢的时候窗口不能卡死（这就是"别做成鸡肋"的意思）
        void DoTranslate()
        {
            if (_busy) return;
            string text = _src.Text;
            if (text.Trim().Length == 0) { _trState.Text = Lang.T("没有要翻译的文字", "Nothing to translate"); return; }
            _busy = true;
            _tr.Enabled = false;
            _trState.Text = Lang.T("翻译中…（用 MyMemory 免费接口，要联网）", "Translating… (free MyMemory endpoint, needs internet)");
            _dst.Text = "";
            _translated = "";

            string src = text;
            Thread th = new Thread(new ThreadStart(delegate()
            {
                string err;
                string result = Translate.Run(src, out err);
                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        _busy = false;
                        _tr.Enabled = true;
                        if (result == null)
                        {
                            _trState.Text = err ?? Lang.T("翻译失败", "Translation failed");
                            _dst.Text = Lang.T("（翻译失败：", "(translation failed: ") + (_trState.Text) + "）";
                        }
                        else
                        {
                            _translated = result;
                            _dst.Text = result;
                            _trState.Text = Lang.T("译文（已可复制）", "Translation (ready to copy)");
                        }
                    }));
                }
                catch { }
            }));
            th.IsBackground = true;
            th.Start();
        }
    }
}
