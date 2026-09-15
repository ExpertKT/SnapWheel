using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapWheel
{
    // ==================== Emoji 面板（0.7.0 第二版） ====================
    // 第一版的两个毛病，这一版都改掉了：
    //   ① **关不上**：第一版用 ShowDialog（模态），而模态窗口不会失去激活 —— 我写的"点到外面就关"
    //      永远不触发，加上无边框 + 置顶 + Esc 被浮层截走，就彻底无路可退。
    //      现在用 **非模态 Show(owner)**，Deactivate 正常触发，点到别处/按 Esc 都会关。
    //   ② **表情被裁**：第一版格子 44px 配 20pt 字号，而 emoji 的行高比字号大得多，装不下就裁。
    //      现在格子尺寸**按渲染出来的位图算**，而且绘制直接用位图（不经过字体度量）。
    // 彩色来自 EmojiRender（WPF/DirectWrite），GDI+ 只会画黑白。
    class EmojiPicker : Form
    {
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

        Action<string> _onPick;
        bool _picked;

        public static void Popup(Form owner, Point screenPt, double scale, Action<string> onPick)
        {
            EmojiPicker pk = new EmojiPicker(scale, onPick);
            try
            {
                Rectangle scr = Screen.FromPoint(screenPt).WorkingArea;
                int x = screenPt.X, y = screenPt.Y;
                if (x + pk.Width > scr.Right) x = Math.Max(scr.Left, scr.Right - pk.Width);
                if (y + pk.Height > scr.Bottom) y = Math.Max(scr.Top, screenPt.Y - pk.Height - 60);
                pk.Location = new Point(x, y);
            }
            catch { pk.Location = screenPt; }
            pk.Show(owner);     // 非模态：这样才能"点到外面就关"
            pk.Activate();
        }

        EmojiPicker(double scale, Action<string> onPick)
        {
            _onPick = onPick;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(40, 42, 50);
            double k = scale <= 0 ? 1.0 : scale;

            const int Cols = 12;
            int pad = (int)(10 * k);
            int headH = (int)(24 * k);
            int cell = (int)(44 * k);          // 格子：装得下渲染出来的 emoji（按 30px 渲染，实际位图约 34x40）
            int glyphPx = (int)(30 * k);

            int rows = 0;
            for (int i = 0; i < Sets.Length; i++) rows += 1 + (Sets[i].Length + Cols - 1) / Cols;
            ClientSize = new Size(pad * 2 + Cols * cell, pad * 2 + Sets.Length * headH + rows * cell);

            int y = pad;
            for (int gi = 0; gi < Sets.Length; gi++)
            {
                Label head = new Label();
                head.Text = Groups[gi];
                head.ForeColor = Color.FromArgb(155, 165, 182);
                head.Font = new Font("Microsoft YaHei UI", 9f * (float)k);
                head.AutoSize = false;
                head.TextAlign = ContentAlignment.MiddleLeft;
                head.Location = new Point(pad, y);
                head.Size = new Size(ClientSize.Width - pad * 2, headH);
                Controls.Add(head);
                y += headH;

                for (int i = 0; i < Sets[gi].Length; i++)
                {
                    int r = i / Cols, c = i % Cols;
                    Cell el = new Cell();
                    el.Glyph = Sets[gi][i];
                    el.Px = glyphPx;
                    el.Location = new Point(pad + c * cell, y + r * cell);
                    el.Size = new Size(cell, cell);
                    el.Click += new EventHandler(OnPick);
                    Controls.Add(el);
                }
                y += ((Sets[gi].Length + Cols - 1) / Cols) * cell;
            }

            Deactivate += new EventHandler(delegate(object o, EventArgs e) { if (!_picked) Close(); });
            KeyPreview = true;
            KeyDown += new KeyEventHandler(delegate(object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { _picked = true; Close(); }
            });
        }

        void OnPick(object sender, EventArgs e)
        {
            Cell el = sender as Cell;
            _picked = true;
            try { if (el != null && _onPick != null) _onPick(el.Glyph); } catch { }
            Close();
        }

        // 一个 emoji 格子：直接贴 EmojiRender 渲染出来的彩色位图（不经过字体度量，所以不会裁）
        class Cell : Control
        {
            public string Glyph = "";
            public int Px = 30;
            bool _hot;

            public Cell()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (_hot)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(80, 120, 170, 235)))
                        e.Graphics.FillRectangle(b, 0, 0, Width, Height);
                Bitmap bmp = EmojiRender.Get(Glyph, Px);
                if (bmp != null)
                {
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(bmp, (Width - bmp.Width) / 2, (Height - bmp.Height) / 2, bmp.Width, bmp.Height);
                }
                else
                {
                    using (Font f = new Font("Segoe UI Emoji", Px * 0.75f))
                        TextRenderer.DrawText(e.Graphics, Glyph, f, new Point(6, 6), Color.White, TextFormatFlags.NoPadding);
                }
            }
        }
    }
}
