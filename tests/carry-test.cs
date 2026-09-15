using System; using System.Drawing; using System.Reflection; using System.Windows.Forms;
namespace SnapWheel { static class CarryTest {
  static int pass=0, fail=0;
  static void Ck(string n, bool ok, string d) { if (ok) { pass++; Console.WriteLine("  [OK]   " + n); } else { fail++; Console.WriteLine("  [FAIL] " + n + "  " + d); } }
  [STAThread] static void Main() {
    // 用"遍历程序集找类型"而不是 Type.GetType(名字)：后者在有些情况下找不到，
    // 而且拿不到类型时后面的反射会以 NullReference 崩掉（这里就被这个坑绊过一次）。
    Func<string, Type> find = delegate(string name) {
      foreach (Type ty in Assembly.GetExecutingAssembly().GetTypes()) if (ty.Name == name) return ty;
      return null;
    };
    Type cf = find("CarryForm");
    Type app = find("AppCtx");      // 承载托盘与入口的类叫 AppCtx（ApplicationContext）
    Ck("找到 CarryForm 类型", cf != null, "");
    Ck("找到 AppCtx 类型", app != null, "");
    if (cf == null || app == null) { Console.WriteLine("类型都找不到，后面的测不了"); return; }
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
      // 不变形的正确验法：在源图里画一个正方形，看它在输出里还是不是正方形。
      // （一开始我按"两侧加黑边"去断言，那是 contain 语义；实现用的是 cover
      //   —— 居中裁切填满，视觉上更好看。期望写错了，不是代码错了。）
      using (Bitmap wide = new Bitmap(400, 100)) {
        using (Graphics g = Graphics.FromImage(wide)) {
          g.Clear(Color.Blue);
          using (SolidBrush b = new SolidBrush(Color.Red)) g.FillRectangle(b, 175, 25, 50, 50);   // 正中 50x50
        }
        using (Bitmap t = (Bitmap)mk.Invoke(null, new object[]{ wide, 132, 99 })) {
          Ck("宽图 -> 132x99", t.Width == 132 && t.Height == 99, t.Width + "x" + t.Height);
          Color mid = t.GetPixel(66, 49);
          Ck("中间保留原图内容", mid.R > 180 && mid.B < 80, mid.ToString());
          // 量红色区域的实际宽高（应该仍然接近 1:1）
          int minX = t.Width, maxX = -1, minY = t.Height, maxY = -1;
          for (int y = 0; y < t.Height; y++)
            for (int x = 0; x < t.Width; x++) {
              Color cc = t.GetPixel(x, y);
              if (cc.R > 150 && cc.B < 100) {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
              }
            }
          int rw = maxX - minX + 1, rh = maxY - minY + 1;
          double ar = (double)rw / Math.Max(1, rh);
          Ck("正方形没被拉变形(宽高比 ≈1)", ar > 0.75 && ar < 1.33, "实测 " + rw + "x" + rh + " 比值 " + ar.ToString("0.00"));
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