// 浮层出图工具：把截图浮层（含标注工具条）离屏渲染成 PNG，用来**用眼睛验收**布局，
// 不用真的去动用户的鼠标、也不碰真实屏幕。
//
// 为什么需要它：用户报过"工具栏挡住了截图区域"这类问题 —— 光靠断言只能证明"没相交"，
// 看不出"摆在那儿顺不顺眼"。这个工具把几种典型选区渲染成一排 PNG，直接看图说话。
//
// 编译（csc，跟其他测试一样）：
//   csc /nologo /target:exe /main:SnapWheel.ToolbarShot /out:shot.exe src\*.cs tests\toolbar-shot.cs
// 运行后会往 %TEMP%\snapwheel-shot\ 下写 4 张图。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class ToolbarShot
    {
        // 按真实虚拟屏尺寸出图：生产环境里浮层就是铺满虚拟屏的，布局也是按"当前那块屏幕"算的，
        // 画布比屏幕小的话，工具条会被算到画布外面（第一版就踩了这个坑）。
        static int W = 1280, H = 800;

        static void F(object o, string name, object val)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + name);
            fi.SetValue(o, val);
        }

        static object Call(object o, string name, params object[] args)
        {
            MethodInfo m = o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) throw new Exception("找不到方法 " + name);
            return m.Invoke(o, args);
        }

        static object G(object o, string name)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + name);
            return fi.GetValue(o);
        }

        // 造一张假桌面：不是截真实屏幕 —— 出图给别人看也不会泄露用户桌面
        static Bitmap FakeDesktop()
        {
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new Rectangle(0, 0, W, H), Color.FromArgb(32, 44, 66), Color.FromArgb(14, 18, 28), 60f))
                    g.FillRectangle(br, 0, 0, W, H);
                using (Pen p = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
                    for (int x = 0; x < W; x += 40) g.DrawLine(p, x, 0, x, H);
                using (Pen p = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
                    for (int y = 0; y < H; y += 40) g.DrawLine(p, 0, y, W, y);
                using (Font f = new Font("Microsoft YaHei UI", 22f, FontStyle.Bold))
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                {
                    g.DrawString("假桌面（内容用来判断工具条有没有挡住它）", f, fb, 60, 60);
                    g.DrawString("一二三四五六七八九十", f, fb, 60, H - 120);
                }
            }
            return b;
        }

        static void Shot(string file, RectangleF sel, bool dragging)
        {
            Bitmap desk = FakeDesktop();
            OverlayForm o = new OverlayForm(new Rectangle(0, 0, W, H), desk);
            F(o, "_hasSel", true);
            F(o, "_c", new PointF(sel.Left + sel.Width / 2f, sel.Top + sel.Height / 2f));
            F(o, "_sz", new SizeF(sel.Width, sel.Height));
            F(o, "_dragging", dragging);
            Call(o, "PlaceToolbar");

            Bitmap outb = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(outb))
            {
                g.Clear(Color.Black);
                Call(o, "PaintOverlay", new PaintEventArgs(g, new Rectangle(0, 0, W, H)));
            }

            // 把工具条位置画个红框标出来，方便一眼看到它到底摆哪了
            // （工具条这会儿不显示的话就不画框 —— 比如正在拖框选，那属于"故意不显示"）
            string dir = Path.Combine(Path.GetTempPath(), "snapwheel-shot");
            Directory.CreateDirectory(dir);
            Rectangle tr = (Rectangle)G(o, "_toolRect");
            bool shown = (bool)Call(o, "ToolbarVisible");
            using (Graphics g = Graphics.FromImage(outb))
            {
                if (shown)
                    using (Pen p = new Pen(Color.FromArgb(220, 255, 60, 60), 3f))
                        g.DrawRectangle(p, tr);
            }
            outb.Save(Path.Combine(dir, file), ImageFormat.Png);
            Console.WriteLine("{0}  选区 {1}  工具条 {2}  显示={3}  压住选区={4}", file, sel, tr, shown,
                shown && tr.IntersectsWith(Rectangle.Round(sel)));

            outb.Dispose();
            o.Dispose();
            desk.Dispose();
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            // 出图不需要用户真实配置
            Settings.OverridePath = Path.Combine(Path.GetTempPath(), "snapwheel_shot_settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(Path.GetTempPath(), "snapwheel_shot_wheels.ini");

            Rectangle vs = SystemInformation.VirtualScreen;
            W = vs.Width; H = vs.Height;

            Shot("1-普通选区.png", new RectangleF(W * 0.25f, H * 0.18f, W * 0.45f, H * 0.40f), false);
            Shot("2-贴底选区.png", new RectangleF(W * 0.25f, H * 0.60f, W * 0.45f, H * 0.33f), false);
            Shot("3-竖长选区.png", new RectangleF(W * 0.30f, H * 0.03f, W * 0.35f, H * 0.92f), false);
            Shot("4-拖框选中.png", new RectangleF(W * 0.25f, H * 0.18f, W * 0.45f, H * 0.40f), true);
            Console.WriteLine("图在 " + Path.Combine(Path.GetTempPath(), "snapwheel-shot"));
        }
    }
}
