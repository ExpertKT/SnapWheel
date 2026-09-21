// overlay-pin-test.cs -- 截图浮层上的「贴图」按钮（1.0.0）。
//
// 用户要求："框选完就能选择贴图，并且自动保存到轮环上。贴图按钮的存在感不能太弱。"
//
// 这条检查盯的是**链路**，不是像素：
//   ① 按钮真的在工具条里，而且能点到
//   ② 点了之后 WantPin 为真、PinAt 落在**选区里**（坐标换算错了就钉到别处去了）
//   ③ **Result 必须生成** —— 这是最容易漏的一条：Result 只在 Confirm() 里由 CropSelection 产生，
//      第一版我照抄了「长图」的出口（自己写 DialogResult=OK; Close();），Result 就是空的，
//      图既不进轮环也不进剪贴板，正好把用户要的"自动保存到轮环"弄没了。
//      纯看代码很难发现（那条路"看起来"也是 OK 退出），所以必须有断言。
//   ④ 没点贴图时 WantPin 必须是假的（别把普通截图也钉上去）
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.OverlayPinTest /out:%TEMP%\op.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\overlay-pin-test.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class OverlayPinTest
    {
        const int W = 1200, H = 800;
        static int pass, fail;

        static void Check(string n, bool ok, string d)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + n); }
            else { fail++; Console.WriteLine("  [FAIL] " + n + "   " + d); }
        }
        static void F(object o, string n, object v)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            fi.SetValue(o, v);
        }
        static object G(object o, string n)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            return fi.GetValue(o);
        }
        static object Call(object o, string n, params object[] a)
        {
            MethodInfo m = o.GetType().GetMethod(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (m == null) throw new Exception("找不到方法 " + n);
            return m.Invoke(o, a);
        }

        [STAThread]
        static void Main()
        {
            try { Run(); }
            catch (Exception ex)
            {
                Console.WriteLine("出错：" + ex.GetType().Name + "  " + ex.Message);
                Environment.ExitCode = 1;
            }
        }

        static Bitmap Desk()
        {
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(70, 90, 120));
                using (Font f = new Font("Microsoft YaHei UI", 20f))
                using (SolidBrush fb = new SolidBrush(Color.White))
                    g.DrawString("假桌面", f, fb, 60, 60);
            }
            return b;
        }

        // 造一个"已经框好了"的浮层；sel 是屏幕坐标下的选区
        static OverlayForm Make(RectangleF sel, Point clickAt, out int pinIdx)
        {
            OverlayForm o = new OverlayForm(new Rectangle(0, 0, W, H), Desk());
            F(o, "_hasSel", true);
            F(o, "_c", new PointF(sel.Left + sel.Width / 2f, sel.Top + sel.Height / 2f));
            F(o, "_sz", new SizeF(sel.Width, sel.Height));
            F(o, "_dragging", false);
            Call(o, "PlaceToolbar");
            pinIdx = OverlayForm.BtnCount - 1;    // 贴图是最后一个按钮（别再抄一份下标）
            return o;
        }

        static void ClickPin(OverlayForm o, int pinIdx)
        {
            Rectangle[] btns = (Rectangle[])G(o, "_toolBtns");
            Point pt = new Point(btns[pinIdx].Left + btns[pinIdx].Width / 2,
                                 btns[pinIdx].Top + btns[pinIdx].Height / 2);
            MethodInfo md = typeof(OverlayForm).GetMethod("OnMouseDown",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (md == null) throw new Exception("找不到 OnMouseDown");
            md.Invoke(o, new object[] { new MouseEventArgs(MouseButtons.Left, 1, pt.X, pt.Y, 0) });
        }

        static void Run()
        {
            try { Lang.Init("zh"); } catch { }
            Application.EnableVisualStyles();
            Settings.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_op_settings.ini");

            Console.WriteLine("=== 截图浮层：贴图按钮 ===\n");

            RectangleF sel = new RectangleF(300, 200, 400, 300);

            // ① 按钮在工具条里
            int pinIdx;
            OverlayForm o1 = Make(sel, Point.Empty, out pinIdx);
            Rectangle[] btns = (Rectangle[])G(o1, "_toolBtns");
            Rectangle bar = (Rectangle)G(o1, "_toolRect");
            Check("① 工具条按钮数 = OverlayForm.BtnCount", btns.Length == OverlayForm.BtnCount,
                  btns.Length + " vs " + OverlayForm.BtnCount);
            Check("① 贴图按钮（最后一个）落在工具条范围内", bar.Contains(btns[pinIdx]),
                  "按钮 " + btns[pinIdx] + " 工具条 " + bar);
            Check("④ 没点之前 WantPin 必须是假的", !(bool)G(o1, "WantPin"), "默认就是 true 了");
            o1.Dispose();

            // ② 点它
            OverlayForm o2 = Make(sel, Point.Empty, out pinIdx);
            ClickPin(o2, pinIdx);
            bool wantPin = (bool)G(o2, "WantPin");
            Point at = (Point)G(o2, "PinAt");
            Bitmap res = (Bitmap)G(o2, "Result");

            Console.WriteLine("  选区 {0}   钉在 {1}", sel, at);
            Console.WriteLine();

            Check("② 点了贴图 → WantPin 为真", wantPin, "还是 false，说明没命中按钮");
            Check("② 钉的位置落在选区里（坐标换错就会钉到别处）",
                  at.X >= sel.Left && at.X <= sel.Right && at.Y >= sel.Top && at.Y <= sel.Bottom,
                  "钉在 " + at + "，选区是 " + sel);
            // ③ 这条最重要
            Check("③ **Result 生成了**（不然图不会进轮环、也不会进剪贴板）", res != null,
                  "Result 是空的 —— 是不是自己写 DialogResult=OK;Close(); 而不是调 Confirm()？");
            if (res != null)
                Check("③ Result 尺寸 = 选区尺寸（裁对了）",
                      Math.Abs(res.Width - sel.Width) <= 2 && Math.Abs(res.Height - sel.Height) <= 2,
                      res.Width + "x" + res.Height + " vs " + sel.Width + "x" + sel.Height);

            o2.Dispose();
            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
