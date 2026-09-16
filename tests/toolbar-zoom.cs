// 工具条放大镜：把标注工具条**单独裁出来放大 6 倍**渲染成 PNG。
//
// 为什么需要它：toolbar-shot.cs 出的是整屏图，工具条在上面只有 40×26 像素一块，
// 图标偏移、字形退化这类问题在那张图里根本看不清 —— 之前两轮"图标异常"就是
// 靠肉眼看整屏图猜的，结果一次也没看准。这个工具直接把按钮放到几百像素大。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.ToolbarZoom /out:zoom.exe src\*.cs tests\toolbar-zoom.cs
// 运行后写 %TEMP%\snapwheel-zoom\1-正常.png 和 2-撤销可用.png
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class ToolbarZoom
    {
        static int W = 1280, H = 800;
        const int Scale = 6;   // 放大倍数：26px 按钮 -> 156px

        static void F(object o, string name, object val)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + name);
            fi.SetValue(o, val);
        }

        static object G(object o, string name)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + name);
            return fi.GetValue(o);
        }

        static object Call(object o, string name, params object[] args)
        {
            MethodInfo m = o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) throw new Exception("找不到方法 " + name);
            return m.Invoke(o, args);
        }

        static void Zoom(string file, Rectangle sel)
        {
            Bitmap desk = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(desk))
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new Rectangle(0, 0, W, H), Color.FromArgb(32, 44, 66), Color.FromArgb(14, 18, 28), 60f))
                    g.FillRectangle(br, 0, 0, W, H);

            OverlayForm o = new OverlayForm(new Rectangle(0, 0, W, H), desk);
            F(o, "_hasSel", true);
            F(o, "_c", new PointF(sel.Left + sel.Width / 2f, sel.Top + sel.Height / 2f));
            F(o, "_sz", new SizeF(sel.Width, sel.Height));
            F(o, "_dragging", false);
            Call(o, "PlaceToolbar");

            Bitmap full = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(full))
            {
                g.Clear(Color.Black);
                Call(o, "PaintOverlay", new PaintEventArgs(g, new Rectangle(0, 0, W, H)));
            }

            Rectangle tr = (Rectangle)G(o, "_toolRect");
            bool shown = (bool)Call(o, "ToolbarVisible");
            if (!shown || tr.Width <= 0) { Console.WriteLine(file + "  工具条未显示，跳过"); return; }

            Rectangle crop = tr;
            crop.Inflate(4, 4);
            crop.Intersect(new Rectangle(0, 0, W, H));

            Bitmap big = new Bitmap(crop.Width * Scale, crop.Height * Scale, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(full, new Rectangle(0, 0, big.Width, big.Height), crop, GraphicsUnit.Pixel);
            }

            string dir = Path.Combine(Path.GetTempPath(), "snapwheel-zoom");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, file);
            big.Save(path, ImageFormat.Png);
            Console.WriteLine("{0}  工具条={1}  裁切={2}  放大后={3}x{4}  按钮数=15", file, tr, crop, big.Width, big.Height);

            big.Dispose();
            full.Dispose();
            o.Dispose();
            desk.Dispose();
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Settings.OverridePath = Path.Combine(Path.GetTempPath(), "snapwheel_zoom_settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(Path.GetTempPath(), "snapwheel_zoom_wheels.ini");

            Rectangle vs = SystemInformation.VirtualScreen;
            W = vs.Width; H = vs.Height;

            Zoom("1-工具条.png", new Rectangle((int)(W * 0.25f), (int)(H * 0.18f), (int)(W * 0.45f), (int)(H * 0.40f)));
            Console.WriteLine("图在 " + Path.Combine(Path.GetTempPath(), "snapwheel-zoom"));
        }
    }
}
