using System; using System.Drawing; using System.Drawing.Imaging; using System.Windows.Forms;
namespace SnapWheel { static class DrawKitTest {
  static int pass = 0, fail = 0;
  static void Check(string name, bool ok, string detail) {
    if (ok) { pass++; Console.WriteLine("  [OK]   " + name + "  " + detail); }
    else { fail++; Console.WriteLine("  [FAIL] " + name + "  " + detail); }
  }
  // 扫描出实际墨迹的边界（非白像素）
  static Rectangle InkBounds(Bitmap b) {
    int minX = b.Width, minY = b.Height, maxX = -1, maxY = -1;
    for (int y = 0; y < b.Height; y++)
      for (int x = 0; x < b.Width; x++) {
        Color c = b.GetPixel(x, y);
        if (c.R < 200 || c.G < 200 || c.B < 200) {
          if (x < minX) minX = x; if (x > maxX) maxX = x;
          if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
      }
    if (maxX < 0) return Rectangle.Empty;
    return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
  }
  [STAThread] static void Main() {
    string text = "保存目录 SnapWheel 123";
    using (Bitmap b = new Bitmap(600, 120, PixelFormat.Format32bppPArgb))
    using (Graphics g = Graphics.FromImage(b)) {
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
      g.Clear(Color.White);
      var fit = DrawKit.Measure(g, text, 20, DrawKit.UI, FontStyle.Regular);
      Check("Measure 有效", fit.Valid, string.Format("量得 {0:0.0}x{1:0.0} pt={2}", fit.Layout.Width, fit.Layout.Height, fit.Pt));
      // 用量的结果画，看看实际墨迹和量的差多少
      DrawKit.Draw(g, fit, new RectangleF(20, 20, 560, 80), Color.Black, Align.Near);
      Rectangle ink = InkBounds(b);
      double ratio = fit.Layout.Width > 0 ? ink.Width / fit.Layout.Width : 0;
      Check("量≈画（同源）", ratio > 0.75 && ratio < 1.25,
            string.Format("量的宽 {0:0.0} vs 实际墨迹宽 {1}，比值 {2:0.00}（应在 0.75~1.25）", fit.Layout.Width, ink.Width, ratio));
      Check("没有溢出画布", ink.Right < b.Width && ink.Bottom < b.Height, "墨迹 " + ink);
    }
    // 测试 2：超宽自动缩 —— 把长文本塞进窄框，检查它真的缩了、且没溢出
    using (Bitmap b = new Bitmap(200, 60, PixelFormat.Format32bppPArgb))
    using (Graphics g = Graphics.FromImage(b)) {
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
      g.Clear(Color.White);
      string longText = "这是一段很长很长的中文说明文字用来测试自动缩小字号是否生效";
      var before = DrawKit.Measure(g, longText, 20, DrawKit.UI, FontStyle.Regular);
      DrawKit.DrawFitted(g, longText, new RectangleF(4, 10, 192, 40), Color.Black, 20, 192, DrawKit.UI, FontStyle.Regular, Align.Center);
      Rectangle ink = InkBounds(b);
      Check("超宽文本被缩到框内", ink.Width <= 196,
            string.Format("原宽 {0:0.0} → 墨迹 {1}（框宽 192）", before.Layout.Width, ink.Width));
      Check("缩小后仍可见", ink.Width > 20, "墨迹 " + ink);
    }
    // 测试 3：符号字体与界面字体各自能量（不能互相污染）
    using (Bitmap b = new Bitmap(200, 60, PixelFormat.Format32bppPArgb))
    using (Graphics g = Graphics.FromImage(b)) {
      var a = DrawKit.Measure(g, "√", 20, DrawKit.Symbol, FontStyle.Regular);
      var c = DrawKit.Measure(g, "√", 20, DrawKit.UI, FontStyle.Regular);
      Check("符号字体可量", a.Valid, string.Format("Symbol {0:0.0}x{1:0.0} / UI {2:0.0}x{3:0.0}", a.Layout.Width, a.Layout.Height, c.Layout.Width, c.Layout.Height));
    }
    // 测试 4：【回归】框太矮时也必须画得出来
    //
    // 这个 bug 是打了宣传图才发现的：DrawKit.DrawFitted 在框高 44px 时**整个文字消失**。
    // 逐档试出来的阈值是"框高必须 ≥ Font.Height"——
    // 30pt 的 MeasureString 报 50.8，而 DrawString 实际要 ≥52，差 1.2px 就什么都不画。
    using (Bitmap b = new Bitmap(800, 200, PixelFormat.Format32bppPArgb))
    using (Graphics g = Graphics.FromImage(b)) {
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
      foreach (int h in new int[] { 5, 20, 44, 51 }) {
        g.Clear(Color.White);
        DrawKit.DrawFitted(g, "滚动长截图 · 翻译", new RectangleF(10, 10, 700, h), Color.Black, 30, 700, DrawKit.UI, FontStyle.Bold, Align.Near);
        Rectangle ink = InkBounds(b);
        Check("框高 " + h + "px 时仍能画出文字", ink != Rectangle.Empty,
              "墨迹为空（整行消失）—— 说明框高没被撑到 Font.Height");
      }
      // 超长出框仍要截断，不能溢出
      g.Clear(Color.White);
      DrawKit.DrawFitted(g, "这是一段很长很长的中文说明文字用来测试超宽时会不会截断", new RectangleF(10, 10, 192, 40), Color.Black, 20, 192, DrawKit.UI, FontStyle.Regular, Align.Center);
      Rectangle ink2 = InkBounds(b);
      Check("超长文本被截断且不超出框", ink2 != Rectangle.Empty && ink2.Width <= 200,
            "墨迹 " + ink2 + "（框宽 192）");
    }
    Console.WriteLine();
    Console.WriteLine(string.Format("结果：通过 {0}，失败 {1}", pass, fail));
    Environment.ExitCode = fail == 0 ? 0 : 1;
  } } }