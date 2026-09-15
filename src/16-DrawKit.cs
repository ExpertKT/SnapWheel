using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace SnapWheel
{
    // ==================== 绘制度量层（0.8.0） ====================
    //
    // 为什么要有这一层：项目里同一个错误犯了四次，全都是「量的时候用一套参数、画的时候用另一套」——
    //   ① 竖版宣传图：用 GDI+ 的 MeasureString 量、却用另一种方式画 → 实际更宽 → 右边缘被切；
    //   ② 表情面板：格子按字号算，而实际行高远大于字号 → 表情被裁；
    //   ③ 符号标注：量框用中文字体、画的时候用符号字体 → 宽高不同 → 框和符号错位；锚点也不一致；
    //   ④ GDI 与 GDI+ 混用：用 TextRenderer(GDI) 画，却期望它响应 GDI+ 的旋转/平移变换 ——
    //      它完全不理会，于是预览正常、一合成到成品图就跑到图外。
    //
    // 根子是「约定没有被强制」：调用方可以自由地"这次这样量、那次那样画"。
    // 这一层的做法：把「量」的参数和结果封装成一个 Fit 值，**画的时候只接受 Fit** ——
    //   想不同源都做不到（因为画的时候拿不到字体/字号，只能从 Fit 里取）。
    //
    // 另一个决定：**全线用 GDI+（Graphics.DrawString / Graphics.MeasureString），不用 GDI（TextRenderer）**。
    //   原因是实测（见 docs/LEARNING.md §2.4）：GDI 的 DrawText 会沿整个目标表面处理裁剪区域，
    //   在 2560×1440 的位图上单次要 9.2ms，而同样操作在小位图上只要 0.43ms —— 相差 21 倍；
    //   GDI+ 的 DrawString 与目标大小无关（0.015ms）。
    //   两者都用 GDI+，量出来的宽度和画出来的宽度才自然是同一套。
    //
    // 用法：
    //   var fit = DrawKit.Measure(g, text, 12, DrawKit.UI, FontStyle.Regular);
    //   DrawKit.Draw(g, fit, box, Color.White, Align.Center);
    // 或者一步到位（超宽会自动缩字号）：
    //   DrawKit.DrawFitted(g, text, box, Color.White, 14, box.Width, DrawKit.UI, FontStyle.Bold, Align.Center);

    enum Align { Near, Center, Far }

    static class DrawKit
    {
        public const string UI = "Microsoft YaHei UI";      // 界面常用
        public const string Symbol = "Segoe UI Symbol";     // 符号（①②★→ 这类）

        // 一次「量」的结果。它带着画的时候需要的一切 ——
        // 调用方拿不到单独的字号，也就没法"另起一套"去画。
        public struct Fit
        {
            public string Text;
            public string FontName;
            public int Pt;
            public FontStyle Style;
            // GDI+ MeasureString 用的是「含行距的排版框」，和 DrawString 的矩形语义一致；
            // 这里存的是排版框尺寸（用它对齐才不会偏）。
            public SizeF Layout;
            public bool Valid;
        }

        static FontStyle SafeStyle(string fontName, FontStyle st)
        {
            // 有些字体没有 Bold/Italic 变体，构造时会抛异常或静默回退，这里统一兜一下
            try
            {
                using (Font f = new Font(fontName, 12f, st)) { }
                return st;
            }
            catch { return FontStyle.Regular; }
        }

        // ---- 量 ----
        public static Fit Measure(Graphics g, string text, int pt, string fontName, FontStyle style)
        {
            Fit fit = new Fit();
            if (string.IsNullOrEmpty(text) || pt <= 0 || g == null) { fit.Valid = false; return fit; }
            if (pt < 6) pt = 6;
            if (pt > 400) pt = 400;
            try
            {
                fontName = string.IsNullOrEmpty(fontName) ? UI : fontName;
                style = SafeStyle(fontName, style);
                using (Font f = new Font(fontName, pt, style))
                {
                    // StringFormat.GenericTypographic 更贴近实际绘制宽度；默认那个会多留边距
                    SizeF sz = g.MeasureString(text, f, new PointF(0, 0), StringFormat.GenericTypographic);
                    fit.Text = text; fit.FontName = fontName; fit.Pt = pt; fit.Style = style;
                    fit.Layout = sz; fit.Valid = sz.Width > 0 && sz.Height > 0;
                }
            }
            catch { fit.Valid = false; }
            return fit;
        }

        // 量的时候用 GDI+，所以需要一个 Graphics。没有的话给个 1x1 的临时画布（结果一样）。
        public static Fit Measure(string text, int pt, string fontName, FontStyle style)
        {
            using (Bitmap b = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(b))
                return Measure(g, text, pt, fontName, style);
        }

        // ---- 画 ----
        // 只接受 Fit：字体、字号、样式都从它里面取，调用方无法"另起一套"。
        public static void Draw(Graphics g, Fit fit, RectangleF box, Color color, Align align)
        {
            if (!fit.Valid || g == null || string.IsNullOrEmpty(fit.Text)) return;
            DrawInternal(g, fit.Text, fit.Pt, fit.FontName, fit.Style, box, color, align);
        }

        // 一步到位：量 → 若超宽就按比例缩字号 → 画。
        // 「超宽自动缩」是画宣传图、竖版图时反复需要的东西，收进来省得每处各写一遍。
        public static void DrawFitted(Graphics g, string text, RectangleF box, Color color,
                                      int pt, int maxW, string fontName, FontStyle style, Align align)
        {
            if (g == null || string.IsNullOrEmpty(text) || box.Width <= 1) return;
            if (pt < 6) pt = 6;
            if (maxW <= 0) maxW = (int)box.Width;
            try
            {
                fontName = string.IsNullOrEmpty(fontName) ? UI : fontName;
                style = SafeStyle(fontName, style);
                // 超宽就缩。注意：字号与宽度**不是线性关系**（字体渲染有舍入和 hinting），
                // 按比例算一次往往还差几个像素 —— 测试就抓到过这个：773px 的文本缩进 192px 的框，
                // 按比例算完仍溢出 8px。所以这里**迭代缩小 + 留 2% 余量**，最多试 4 轮。
                int use = pt;
                for (int iter = 0; iter < 4; iter++)
                {
                    SizeF sz;
                    using (Font probe = new Font(fontName, use, style))
                        sz = g.MeasureString(text, probe, new PointF(0, 0), StringFormat.GenericTypographic);
                    if (sz.Width <= maxW || use <= 6) break;
                    int next = (int)Math.Floor(use * (maxW / sz.Width) * 0.98);
                    if (next >= use) next = use - 1;
                    use = Math.Max(6, next);
                }
            catch { }
        }

        // 内部唯一的绘制出口：量和画都在这条路径上，不可能不同源
        static void DrawInternal(Graphics g, string text, int pt, string fontName, FontStyle style,
                                 RectangleF box, Color color, Align align)
        {
            using (Font f = new Font(fontName, pt, style))
            {
                StringFormat fmt = new StringFormat(StringFormat.GenericTypographic);
                fmt.Alignment = (align == Align.Near) ? StringAlignment.Near
                              : (align == Align.Center) ? StringAlignment.Center : StringAlignment.Far;
                fmt.LineAlignment = StringAlignment.Center;
                fmt.FormatFlags |= StringFormatFlags.NoWrap;
                using (SolidBrush b = new SolidBrush(color))
                    g.DrawString(text, f, b, box, fmt);
            }
        }

        // ---- 常用组合：胶囊/按钮上的一行字（居中 + 自动缩） ----
        public static void ChipText(Graphics g, string text, RectangleF box, Color color, int pt, FontStyle style)
        {
            DrawFitted(g, text, box, color, pt, (int)(box.Width - 8), UI, style, Align.Center);
        }

        // ---- 常用组合：左对齐的一行说明（不缩，超了就让它溢出，方便发现排版问题） ----
        public static void Line(Graphics g, string text, float x, float y, int pt, Color color, FontStyle style)
        {
            if (g == null || string.IsNullOrEmpty(text)) return;
            try
            {
                using (Font f = new Font(UI, pt, SafeStyle(UI, style)))
                {
                    SizeF sz = g.MeasureString(text, f, new PointF(0, 0), StringFormat.GenericTypographic);
                    using (SolidBrush b = new SolidBrush(color))
                        g.DrawString(text, f, b, new RectangleF(x, y, sz.Width + 2, sz.Height + 2), StringFormat.GenericTypographic);
                }
            }
            catch { }
        }
    }
}
