using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SnapWheel
{
    // ==================== Emoji 面板（0.7.0） ====================
    // 点工具条的 emoji 按钮弹出来的一小块面板：分几组常用表情，点一下就插到选区里。
    // 说明：
    //   · 用 TextRenderer（GDI）画 emoji，而不是 Graphics.DrawString —— GDI+ 对 Windows 的
    //     彩色 emoji 字体（COLR）支持不好，常常画成黑白；GDI 这条路能拿到彩色。
    //   · 面板是**无边框 + 置顶**的小窗，点选后立刻关闭；Esc 或点到面板外也会关。
    //   · 不做"搜索 emoji"，这一版只给最常用的那几组（够 90% 的使用）。
    class EmojiPicker : Form
    {
        public string Picked = null;

        static readonly string[] Groups = { "表情", "手势", "符号" };
        static readonly string[][] Sets = {
            new string[] {
                "😀","😃","😄","😁","😆","😅","😂","🤣","😊","😇","🙂","😉",
                "😍","🥰","😘","😜","🤔","🤨","😐","🙄","😏","😮","😴","😌",
                "😒","😔","😕","🙃","😲","😢","😭","😤","😩","🤯","😱","😳",
                "🤪","🥴","😠","😡","😷","🤒","🥳","🥺","🤠","🤡","🤫","🤭"
            },
            new string[] { "👍","👎","👌","✌","🤞","🤟","🤘","👏","🙌","🙏","💪","👋","🤝","👀","🖐","✋" },
            new string[] { "❤","💔","⭐","✨","🔥","💯","🎉","🎊","✅","❌","⚠","❗","❓","💡","📌","🔍","⏰","📷","🎯","⚡" }
        };

        const int Cell = 44;
        const int Cols = 12;

        public EmojiPicker(int scale)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(38, 40, 48);
            double k = scale / 100.0;
            int cell = (int)(Cell * k);
            int cols = Cols;
            int pad = (int)(10 * k);
            int headH = (int)(26 * k);

            int rows = 0;
            for (int i = 0; i < Sets.Length; i++) rows += 1 + (Sets[i].Length + cols - 1) / cols;
            int w = pad * 2 + cols * cell;
            int h = pad * 2 + Sets.Length * headH + rows * cell;
            ClientSize = new Size(w, h);

            int y = pad;
            for (int gi = 0; gi < Sets.Length; gi++)
            {
                Label head = new Label();
                head.Text = Groups[gi];
                head.ForeColor = Color.FromArgb(150, 160, 178);
                head.Font = new Font("Microsoft YaHei UI", 9f);
                head.AutoSize = false;
                head.TextAlign = ContentAlignment.MiddleLeft;
                head.Location = new Point(pad, y);
                head.Size = new Size(w - pad * 2, headH);
                Controls.Add(head);
                y += headH;

                for (int i = 0; i < Sets[gi].Length; i++)
                {
                    int r = i / cols, c = i % cols;
                    EmojiLabel el = new EmojiLabel();
                    el.Glyph = Sets[gi][i];
                    el.FontPt = 20f * (float)k;
                    el.Location = new Point(pad + c * cell, y + r * cell);
                    el.Size = new Size(cell, cell);
                    el.Click += new EventHandler(OnPick);
                    Controls.Add(el);
                }
                y += ((Sets[gi].Length + cols - 1) / cols) * cell;
            }

            // 点到面板外 / Esc 就关掉
            Deactivate += new EventHandler(delegate(object o, EventArgs e) { if (Picked == null) Close(); });
            KeyPreview = true;
            KeyDown += new KeyEventHandler(delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); });
        }

        void OnPick(object sender, EventArgs e)
        {
            EmojiLabel el = sender as EmojiLabel;
            if (el != null) Picked = el.Glyph;
            Close();
        }

        // 自绘一个 emoji 格子：悬停给个底色。用 TextRenderer 保证彩色。
        class EmojiLabel : Control
        {
            public string Glyph = "";
            public float FontPt = 20f;
            bool _hot;

            public EmojiLabel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (_hot)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 120, 170, 235)))
                        e.Graphics.FillRectangle(b, 0, 0, Width, Height);
                using (Font f = new Font("Segoe UI Emoji", FontPt))
                {
                    Size sz = TextRenderer.MeasureText(Glyph, f);
                    TextRenderer.DrawText(e.Graphics, Glyph, f,
                        new Point((Width - sz.Width) / 2, (Height - sz.Height) / 2),
                        Color.White, TextFormatFlags.NoPadding);
                }
            }
        }
    }
}
