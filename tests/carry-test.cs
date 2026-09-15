using System; using System.Drawing; using System.Reflection; using System.Windows.Forms;
namespace SnapWheel { static class CarryTest {
  static int pass=0, fail=0;
  static void Ck(string n, bool ok, string d) { if (ok) { pass++; Console.WriteLine("  [OK]   " + n); } else { fail++; Console.WriteLine("  [FAIL] " + n + "  " + d); } }
  [STAThread] static void Main() {
    Type cf = Type.GetType("SnapWheel.CarryForm");
    Type app = Type.GetType("SnapWheel.App");
    // ---- 1) 拖放分步插值 ----
    MethodInfo sp = cf.GetMethod("StepPoint", BindingFlags.Public | BindingFlags.Static);
    Point from = new Point(100, 200), to = new Point(1000, 800);
    Point p0 = (Point)sp.Invoke(null, new object[]{ from, to, 0, 14 });
    Point p1 = (Point)sp.Invoke(null, new object[]{ from, to, 1, 14 });
    Point pe = (Point)sp.Invoke(null, new object[]{ from, to, 14, 14 });
    Ck("第 0 步 = 起点", p0 == from, p0.ToString());
    Ck("最后一步 = 终点", pe == to, pe.ToString());
    Ck("第 1 步在起点之后", p1.X > from.X && p1.X < to.X, p1.ToString());
    bool mono = true; int lastX = from.X;
    for (int i = 1; i <= 14; i++) { Point q = (Point)sp.Invoke(null, new object[]{ from, to, i, 14 }); if (q.X < lastX) mono = false; lastX = q.X; }
    Ck("整个过程单调前进", mono, "");
    Point over = (Point)sp.Invoke(null, new object[]{ from, to, 99, 14 });
    Ck("越界步数被夹住", over == to, over.ToString());
    Point zero = (Point)sp.Invoke(null, new object[]{ from, to, 5, 0 });
    Ck("步数为 0 不崩", zero == to, zero.ToString());
    // ---- 2) 缩略图生成：尺寸正确 + 不变形 ----
    MethodInfo mk = app.GetMethod("MakeCarryThumb", BindingFlags.NonPublic | BindingFlags.Static);
    if (mk == null) { Ck("找到 MakeCarryThumb", false, "反射拿不到"); }
    else {
      using (Bitmap wide = new Bitmap(400, 100)) {
        using (Graphics g = Graphics.FromImage(wide)) g.Clear(Color.Red);
        using (Bitmap t = (Bitmap)mk.Invoke(null, new object[]{ wide, 132, 99 })) {
          Ck("宽图 -> 132x99", t.Width == 132 && t.Height == 99, t.Width + "x" + t.Height);
          // 4:1 的源放进 4:3 的框，应该左右留黑边（居中加边），上下填满
          Color corner = t.GetPixel(1, 1);
          Color mid = t.GetPixel(66, 49);
          Ck("中间是原图内容", mid.R > 200 && mid.G < 60, mid.ToString());
          Ck("两侧是加边(不是拉伸变形)", corner.R < 60 && corner.G < 60, corner.ToString());
        }
      }
      using (Bitmap tall = new Bitmap(100, 400)) {
        using (Graphics g = Graphics.FromImage(tall)) g.Clear(Color.Blue);
        using (Bitmap t = (Bitmap)mk.Invoke(null, new object[]{ tall, 132, 99 })) {
          Ck("高图 -> 132x99", t.Width == 132 && t.Height == 99, t.Width + "x" + t.Height);
          Color mid = t.GetPixel(66, 49);
          Ck("高图中间是原图内容", mid.B > 200, mid.ToString());
        }
      }
      Ck("null 不崩", mk.Invoke(null, new object[]{ null, 132, 99 }) == null, "");
    }
    Console.WriteLine();
    Console.WriteLine(string.Format("结果：通过 {0}，失败 {1}", pass, fail));
    Environment.ExitCode = fail == 0 ? 0 : 1;
  } } }