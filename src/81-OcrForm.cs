using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapWheel
{
    // 取字（OCR）的结果框：可编辑的多行文本 + 一键复制。
    // 打开时自动把结果放进剪贴板 —— 取字的目的就是"拿去用"，少一步是一步。
    class OcrForm : Form
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
            ClientSize = new Size(560, 380);
            MinimumSize = new Size(360, 240);
            SuspendLayout();

            Label head = new Label();
            head.Text = chars > 0 ? ("认出来 " + chars + " 个字") : "没认出文字";
            head.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(28, 30, 36);
            head.AutoSize = true;
            head.Location = new Point(24, 18);
            Controls.Add(head);

            Label sub = new Label();
            sub.Text = chars > 0
                ? (copied ? "已复制到剪贴板，直接去粘贴就行（下面也能改）"
                          : "下面就是识别结果，可以改完再复制（刚才没能写进剪贴板）")
                : "换一块更清晰、字更大的区域再试试；倾斜或花哨的字体识别率会低一些";
            sub.ForeColor = Color.FromArgb(120, 124, 134);
            sub.AutoSize = true;
            sub.Location = new Point(27, 50);
            Controls.Add(sub);

            TextBox box = new TextBox();
            box.Multiline = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = true;
            box.Font = new Font("Microsoft YaHei UI", 11f);
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Color.White;
            box.Location = new Point(24, 76);
            box.Size = new Size(ClientSize.Width - 48, ClientSize.Height - 76 - 62);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            box.Text = text;
            Controls.Add(box);

            RoundButton copy = new RoundButton();
            copy.Text = "复制并关闭";
            copy.Size = new Size(126, 36);
            copy.Fill = Color.FromArgb(0, 122, 204);
            copy.FillHover = Color.FromArgb(0, 140, 232);
            copy.TextColor = Color.White;
            copy.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            copy.Location = new Point(ClientSize.Width - 24 - 126, ClientSize.Height - 24 - 36);
            copy.Click += new EventHandler(delegate(object o, EventArgs e2)
            {
                try { Clipboard.SetText(box.Text); } catch { }
                DialogResult = DialogResult.OK;
                Close();
            });
            Controls.Add(copy);
            AcceptButton = copy;

            RoundButton close = new RoundButton();
            close.Text = "关闭";
            close.Size = new Size(88, 36);
            close.Fill = Color.FromArgb(238, 240, 245);
            close.FillHover = Color.FromArgb(226, 230, 238);
            close.TextColor = Color.FromArgb(60, 64, 74);
            close.Font = new Font("Microsoft YaHei UI", 10f);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.Location = new Point(ClientSize.Width - 24 - 126 - 10 - 88, ClientSize.Height - 24 - 36);
            close.Click += new EventHandler(delegate(object o, EventArgs e2) { DialogResult = DialogResult.Cancel; Close(); });
            Controls.Add(close);
            CancelButton = close;

            ResumeLayout();
        }
    }
}
