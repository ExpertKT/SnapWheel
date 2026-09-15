using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapWheel
{
    // ==================== 符号面板（0.7.0） ====================
    // 为什么不是 emoji：Windows 的彩色 emoji 靠 Segoe UI Emoji 的 COLR/CPAL 表，
    //   而 .NET Framework 的整条文本栈都不读这两张表 —— GDI / GDI+ / WPF FormattedText /
    //   WPF TextBlock 四条路全试过，统统只能画出黑色剪影（做过像素分析确认）。
    //   真彩色要上 Direct2D COM 互操作，对"单 exe、零依赖、纯 GDI 自绘"这个项目代价太大。
    // 所以改用**纯矢量符号**：字体直接有这些字形，单色、可自由上色、放大不糊，
    //   而且风格和现有的箭头/方框/文字标注完全一致。
    //
    // 面板结构：顶部一排颜色（红/黄/蓝/黑，与标注色一致），下面是三组符号。
    // 点一个符号 → 回调（符号 + 当前颜色）。非模态 Show(owner)，点到别处/按 Esc 都会关。
    class SymbolPicker : Form
    {
        static readonly string[] GroupNames = { "标记", "箭头", "编号 / 其它" };
        static readonly string[][] Sets = {
            new string[] {
                "✓","✔","✗","✘","☑","☒","●","○","■","□","▲","△",
                "◆","◇","★","☆","♥","♡","※","§","¶","†","‡","✚"
            },
            new string[] {
                "→","←","↑","↓","↔","↕","⇒","⇐","⇑","⇓","➜","➤",
                "⟶","⟵","⤴","⤵","↻","↺","⇢","⇠","⇡","⇣","⇉","⇄"
            },
            new string[] {
                "①","②","③","④","⑤","⑥","⑦","⑧","⑨","⑩","⑪","⑫",
                "✎","✏","✂","✉","☎","♪","♫","⚑","⚐","☀","☂","❄"
            }
        };

        Action<string, Color> _onPick;
        Color _color = Color.FromArgb(238, 70, 90);
        bool _picked;
        ColorDot[] _dots;

        // 面板弹出的位置：贴在工具条上那个按钮的下面（越界会翻到上方）
        public static void Popup(Form owner, Point screenPt, double scale, Color initial, Action<string, Color> onPick)
        {
            SymbolPicker pk = new SymbolPicker(scale, initial, onPick);
            try
            {
                Rectangle scr = Screen.FromPoint(screenPt).WorkingArea;
                int x = screenPt.X, y = screenPt.Y;
                if (x + pk.Width > scr.Right) x = Math.Max(scr.Left, scr.Right - pk.Width);
                if (y + pk.Height > scr.Bottom) y = Math.Max(scr.Top, screenPt.Y - pk.Height - 60);
                pk.Location = new Point(x, y);
            }
            catch { pk.Location = screenPt; }
            pk.Show(owner);
            pk.Activate();
        }

        SymbolPicker(double scale, Color initial, Action<string, Color> onPick)
        {
            _onPick = onPick;
            _color = initial;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(40, 42, 50);
            double k = scale <= 0 ? 1.0 : scale;

            const int Cols = 12;
            int pad = (int)(10 * k);
            int headH = (int)(24 * k);
            int cell = (int)(48 * k);        // 原来 40 装不下 20pt 的符号，会被裁掉一截
            int colorH = (int)(34 * k);

            int rows = 0;
            for (int i = 0; i < Sets.Length; i++) rows += 1 + (Sets[i].Length + Cols - 1) / Cols;
            ClientSize = new Size(pad * 2 + Cols * cell, pad * 2 + colorH + Sets.Length * headH + rows * cell);

            // 顶部：颜色（与标注工具条同一组颜色）
            Label cl = new Label();
            cl.Text = "颜色";
            cl.ForeColor = Color.FromArgb(155, 165, 182);
            cl.Font = new Font("Microsoft YaHei UI", 9f * (float)k);
            cl.AutoSize = false;
            cl.TextAlign = ContentAlignment.MiddleLeft;
            cl.Location = new Point(pad, pad);
            cl.Size = new Size((int)(52 * k), colorH);
            Controls.Add(cl);

            _dots = new ColorDot[AnnotColorsStatic.Length];
            for (int i = 0; i < AnnotColorsStatic.Length; i++)
            {
                ColorDot d = new ColorDot();
                d.C = AnnotColorsStatic[i];
                d.Selected = (AnnotColorsStatic[i].ToArgb() == _color.ToArgb());
                d.Location = new Point(pad + (int)(58 * k) + i * (int)(30 * k), pad + (int)(4 * k));
                d.Size = new Size((int)(26 * k), (int)(26 * k));
                d.Click += new EventHandler(OnColor);
                Controls.Add(d);
                _dots[i] = d;
            }

            int y = pad + colorH;
            for (int gi = 0; gi < Sets.Length; gi++)
            {
                Label head = new Label();
                head.Text = GroupNames[gi];
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
                    GlyphCell el = new GlyphCell();
                    el.Glyph = Sets[gi][i];
                    el.Px = (float)(19 * k);
                    el.Ink = Color.FromArgb(228, 236, 246);
                    el.Location = new Point(pad + c * cell, y + r * cell);
                    el.Size = new Size(cell, cell);
                    el.Click += new EventHandler(OnPickGlyph);
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

        // 和标注工具条共用同一组颜色（红/黄/蓝/黑）
        static readonly Color[] AnnotColorsStatic = {
            Color.FromArgb(238, 70, 90),
            Color.FromArgb(250, 176, 42),
            Color.FromArgb(0, 150, 240),
            Color.FromArgb(26, 28, 34)
        };

        void OnColor(object sender, EventArgs e)
        {
            ColorDot d = sender as ColorDot;
            if (d == null) return;
            _color = d.C;
            for (int i = 0; i < _dots.Length; i++) { _dots[i].Selected = (_dots[i] == d); _dots[i].Invalidate(); }
        }

        void OnPickGlyph(object sender, EventArgs e)
        {
            GlyphCell el = sender as GlyphCell;
            _picked = true;
            try { if (el != null && _onPick != null) _onPick(el.Glyph, _color); } catch { }
            Close();
        }

        // 一个符号格子
        class GlyphCell : Control
        {
            public string Glyph = "";
            public float Px = 20f;
            public Color Ink = Color.White;
            bool _hot;

            public GlyphCell()
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
                using (Font f = new Font("Segoe UI Symbol", Px))
                {
                    // 按实际度量居中放（不用 VerticalCenter 标志：单字符时它会偏上，看着像被裁）
                    Size gsz = TextRenderer.MeasureText(Glyph, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(e.Graphics, Glyph, f,
                        new Point((Width - gsz.Width) / 2, (Height - gsz.Height) / 2), Ink, TextFormatFlags.NoPadding);
                }
            }
        }

        // 一个颜色圆点
        class ColorDot : Control
        {
            public Color C = Color.Red;
            public bool Selected;
            bool _hot;

            public ColorDot()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                Cursor = Cursors.Hand;
            }
            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(2, 2, Width - 5, Height - 5);
                using (SolidBrush b = new SolidBrush(C)) e.Graphics.FillEllipse(b, r);
                if (Selected || _hot)
                    using (Pen p = new Pen(Color.FromArgb(Selected ? 255 : 140, 255, 255, 255), Selected ? 2.2f : 1.2f))
                        e.Graphics.DrawEllipse(p, r);
            }
        }
    }
}
