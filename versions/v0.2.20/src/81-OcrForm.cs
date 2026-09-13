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
            Text = AppInfo.Name + " 取字";
            Icon = Brand.Get();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 252, 254);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 560);
            MinimumSize = new Size(420, 380);
            SuspendLayout();

            Label head = new Label();
            head.Text = chars > 0 ? ("认出来 " + chars + " 个字") : "没认出文字";
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.AutoSize = true;
            head.Location = new Point(24, 16);
            Controls.Add(head);

            Label sub = new Label();
            sub.Text = chars > 0
                ? (copied ? "原文已复制到剪贴板；要用译文点下面的「翻译」"
                          : "下面就是识别结果，可以改完再复制")
                : "换一块更清晰、字更大的区域再试试；倾斜或花哨的字体识别率会低一些";
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.AutoSize = true;
            sub.Location = new Point(27, 46);
            Controls.Add(sub);

            Label l1 = new Label();
            l1.Text = "原文";
            l1.ForeColor = Color.FromArgb(120, 124, 134);
            l1.AutoSize = true;
            l1.Location = new Point(24, 74);
            Controls.Add(l1);

            _src = new TextBox();
            _src.Multiline = true;
            _src.ScrollBars = ScrollBars.Both;
            _src.WordWrap = true;
            _src.Font = new Font("Microsoft YaHei UI", 11f);
            _src.BorderStyle = BorderStyle.FixedSingle;
            _src.BackColor = Color.White;
            _src.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _src.Location = new Point(24, 94);
            _src.Size = new Size(ClientSize.Width - 48, 170);
            _src.Text = text;
            Controls.Add(_src);

            // 翻译
            _tr = new RoundButton();
            _tr.Text = chars > 0 ? ("翻译成" + Translate.TargetLabel(text)) : "翻译";
            _tr.Size = new Size(132, 34);
            _tr.Fill = Color.FromArgb(0, 122, 204);
            _tr.FillHover = Color.FromArgb(0, 140, 232);
            _tr.TextColor = Color.White;
            _tr.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            _tr.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _tr.Location = new Point(24, 274);
            _tr.Click += new EventHandler(delegate(object o, EventArgs e2) { DoTranslate(); });
            Controls.Add(_tr);

            RoundButton copySrc = new RoundButton();
            copySrc.Text = "复制原文";
            copySrc.Size = new Size(102, 34);
            copySrc.Fill = Color.FromArgb(238, 240, 245);
            copySrc.FillHover = Color.FromArgb(226, 230, 238);
            copySrc.TextColor = Color.FromArgb(60, 64, 74);
            copySrc.Font = new Font("Microsoft YaHei UI", 10f);
            copySrc.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            copySrc.Location = new Point(164, 274);
            copySrc.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                try { Clipboard.SetText(_src.Text); _trState.Text = "原文已复制"; } catch { }
            });
            Controls.Add(copySrc);

            _trState = new Label();
            _trState.Text = "译文";
            _trState.ForeColor = Color.FromArgb(120, 124, 134);
            _trState.AutoSize = true;
            _trState.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _trState.Location = new Point(280, 284);
            Controls.Add(_trState);

            _dst = new TextBox();
            _dst.Multiline = true;
            _dst.ScrollBars = ScrollBars.Both;
            _dst.WordWrap = true;
            _dst.ReadOnly = true;
            _dst.Font = new Font("Microsoft YaHei UI", 11f);
            _dst.BorderStyle = BorderStyle.FixedSingle;
            _dst.BackColor = Color.FromArgb(248, 249, 252);
            _dst.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _dst.Location = new Point(24, 316);
            _dst.Size = new Size(ClientSize.Width - 48, 170);
            Controls.Add(_dst);

            RoundButton copyDst = new RoundButton();
            copyDst.Text = "复制译文";
            copyDst.Size = new Size(102, 34);
            copyDst.Fill = Color.FromArgb(238, 240, 245);
            copyDst.FillHover = Color.FromArgb(226, 230, 238);
            copyDst.TextColor = Color.FromArgb(60, 64, 74);
            copyDst.Font = new Font("Microsoft YaHei UI", 10f);
            copyDst.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            copyDst.Location = new Point(24, ClientSize.Height - 48);
            copyDst.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                if (_translated.Length == 0) { _trState.Text = "还没翻译呢"; return; }
                try { Clipboard.SetText(_translated); _trState.Text = "译文已复制"; } catch { }
            });
            Controls.Add(copyDst);

            RoundButton close = new RoundButton();
            close.Text = "关闭";
            close.Size = new Size(96, 36);
            close.Fill = Color.FromArgb(0, 122, 204);
            close.FillHover = Color.FromArgb(0, 140, 232);
            close.TextColor = Color.White;
            close.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.Location = new Point(ClientSize.Width - 24 - 96, ClientSize.Height - 48);
            close.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.OK; Close(); });
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;

            ResumeLayout();
        }

        // 翻译丢到后台线程去做：网络慢的时候窗口不能卡死（这就是"别做成鸡肋"的意思）
        void DoTranslate()
        {
            if (_busy) return;
            string text = _src.Text;
            if (text.Trim().Length == 0) { _trState.Text = "没有要翻译的文字"; return; }
            _busy = true;
            _tr.Enabled = false;
            _trState.Text = "翻译中…（用 MyMemory 免费接口，要联网）";
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
                            _trState.Text = err ?? "翻译失败";
                            _dst.Text = "（翻译失败：" + (_trState.Text) + "）";
                        }
                        else
                        {
                            _translated = result;
                            _dst.Text = result;
                            _trState.Text = "译文（已可复制）";
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
