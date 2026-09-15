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
    Console.WriteLine();
    Console.WriteLine(string.Format("结果：通过 {0}，失败 {1}", pass, fail));
    Environment.ExitCode = fail == 0 ? 0 : 1;
  } } }