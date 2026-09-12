// UI 截图工具：把设置窗口和轮盘"画"到 PNG 里，方便离线看设计效果（不会弹窗、不碰鼠标）
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.UiShot /out:uishot.exe SnapWheel.cs tests\ui-shot.cs
// 用法：uishot.exe [输出目录]      默认输出到 %TEMP%\snapwheel_ui\
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SnapWheel
{
    static class UiShot
    {
        static string outDir;

        static void F(object o, string name, object val)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + name);
            fi.SetValue(o, val);
        }

        static object Call(object o, string name, params object[] args)
        {
            MethodInfo mi = o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) throw new Exception("找不到方法 " + name);
            return mi.Invoke(o, args);
        }

        static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(5); }
        }

        // 造一个假的"桌面"当玻璃底：README 里放截图时，不能把你真实屏幕糊进去
        static void FakeBackdrop(WheelForm f)
        {
            try
            {
                int w = Math.Max(64, f.Width), h = Math.Max(64, f.Height);
                Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    // 渐变壁纸（亮一点，玻璃才有东西可透）
                    using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                        new Rectangle(0, 0, w, h), Color.FromArgb(255, 96, 140, 210), Color.FromArgb(255, 232, 178, 150), 35f))
                        g.FillRectangle(lg, 0, 0, w, h);
                    // 假窗口（亮底 + 标题条）
                    using (var sb = new SolidBrush(Color.FromArgb(255, 246, 248, 252)))
                        g.FillRectangle(sb, w / 5, h / 6, w * 3 / 5, h / 3);
                    using (var sb = new SolidBrush(Color.FromArgb(255, 132, 150, 178)))
                        g.FillRectangle(sb, w / 5, h / 6, w * 3 / 5, 30);
                    // 几个彩色块，让模糊后看得出层次
                    using (var sb = new SolidBrush(Color.FromArgb(230, 66, 133, 244))) g.FillEllipse(sb, w / 8, h / 2, w / 4, w / 4);
                    using (var sb = new SolidBrush(Color.FromArgb(210, 240, 150, 60))) g.FillEllipse(sb, w / 2, h / 3, w / 5, w / 5);
                    using (var sb = new SolidBrush(Color.FromArgb(190, 90, 200, 150))) g.FillEllipse(sb, w / 3, h / 8, w / 6, w / 6);
                    using (var sb = new SolidBrush(Color.FromArgb(220, 255, 255, 255))) g.FillRectangle(sb, 0, h * 3 / 4, w, h / 4);
                }
                var bf = typeof(WheelForm).GetField("_backdropBlur", BindingFlags.NonPublic | BindingFlags.Instance);
                var vf = typeof(WheelForm).GetField("_backdropValid", BindingFlags.NonPublic | BindingFlags.Instance);
                var of = typeof(WheelForm).GetField("_backdropOffset", BindingFlags.NonPublic | BindingFlags.Instance);
                bf.SetValue(f, b); vf.SetValue(f, true); of.SetValue(f, new Point(0, 0));
            }
            catch (Exception ex) { Console.WriteLine("   （假背景失败：" + ex.Message + "）"); }
        }

        // 轮盘：用 DrawWheel 直接画进一张带"桌面底色"的位图，方便看效果
        static void ShotWheel(WheelManager mgr, Settings s, string file, Action<WheelForm> setup)
        {
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Pump(700);
            F(f, "_show", 1f);
            if (setup != null) setup(f);
            FakeBackdrop(f);                       // 用假桌面，别把真实屏幕带进截图
            Pump(120);
            MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            int w = f.ClientSize.Width, h = f.ClientSize.Height;
            if (w < 50) { w = 660; h = 660; }
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                // 假装背景是桌面（深色），这样透明部分也能看清
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(255, 28, 30, 34))) g.FillRectangle(bg, 0, 0, w, h);
                // 画几个"桌面图标"当参照物
                using (SolidBrush ib = new SolidBrush(Color.FromArgb(255, 62, 66, 74)))
                    for (int i = 0; i < 6; i++) g.FillRectangle(ib, 20 + (i % 3) * 70, 20 + (i / 3) * 70, 48, 48);
                dw.Invoke(f, new object[] { g, w, h });
            }
            b.Save(file, ImageFormat.Png);
            b.Dispose();
            try { f.HideWheel(); f.Close(); f.Dispose(); } catch { }
            Console.WriteLine("  写出 " + Path.GetFileName(file) + "  (" + w + "x" + h + ")");
        }

        static void ShotSettings(Settings s, string file)
        {
            SettingsForm f = new SettingsForm(s);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);      // 挪到屏幕外，避免闪一下
            f.ShowInTaskbar = false;
            f.Show();
            Pump(500);
            Bitmap b = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
            b.Save(file, ImageFormat.Png);
            b.Dispose();
            int w = f.Width, h = f.Height;
            foreach (Control c in f.Controls)
            {
                Console.WriteLine("    · {0} {1} bounds={2} visible={3} top={4}",
                    c.GetType().Name, c.Text, c.Bounds, c.Visible, f.Controls.GetChildIndex(c));
            }
            f.Close(); f.Dispose();
            Console.WriteLine("  写出 " + Path.GetFileName(file) + "  (" + w + "x" + h + ")");
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "snapwheel_ui");
            Directory.CreateDirectory(outDir);
            Console.WriteLine("输出目录: " + outDir);

            Settings s = Settings.Load();
            s.SaveToDisk = false;
            s.ThumbSize = 96; s.Radius = 300; s.Slots = 5; s.LabelSize = 16; s.Corner = "BL";
            WheelManager mgr = new WheelManager(s);
            // 截图工具用固定的演示名字/配色，保证出图稳定（不受本机实际 Wheel 影响）
            mgr.ActiveWheel.Name = "设计稿";
            mgr.ActiveWheel.ColorIndex = 0;
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;

            // 塞几张不同比例的图
            Action<int, int, Color> add = delegate(int w, int h, Color c)
            {
                Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    using (SolidBrush br = new SolidBrush(c)) g.FillRectangle(br, 0, 0, w, h);
                    using (Pen p = new Pen(Color.FromArgb(90, 255, 255, 255), 3)) g.DrawRectangle(p, 3, 3, w - 7, h - 7);
                }
                st.Add(b);
            };
            add(320, 200, Color.FromArgb(70, 130, 200));
            add(200, 200, Color.FromArgb(210, 120, 70));
            add(240, 140, Color.FromArgb(90, 180, 120));
            add(160, 220, Color.FromArgb(170, 110, 200));
            add(300, 180, Color.FromArgb(200, 90, 110));

            Console.WriteLine("收起态 / 把手:");
            {
                WheelForm cf = new WheelForm(mgr, s);
                cf.StartCollapsed();
                Pump(300);
                using (Bitmap b = new Bitmap(cf.Width, cf.Height, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(b))
                        typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance)
                            .Invoke(cf, new object[] { g, cf.Width, cf.Height });
                    b.Save(Path.Combine(outDir, "collapsed.png"), ImageFormat.Png);
                }
                Console.WriteLine("  写出 collapsed.png  ({0}x{1})  收起={2}", cf.Width, cf.Height, cf.IsCollapsed);

                // 收起动画中途（看环在缩回）
                cf.ExpandWheel();
                Pump(1200);
                F(cf, "_collapsing", true);
                F(cf, "_intro", true);
                F(cf, "_introT", 0.45f);
                F(cf, "_introDur", 1.6f);
                F(cf, "_introAt", DateTime.Now);
                FakeBackdrop(cf);
                using (Bitmap b = new Bitmap(cf.Width, cf.Height, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(b))
                        typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance)
                            .Invoke(cf, new object[] { g, cf.Width, cf.Height });
                    b.Save(Path.Combine(outDir, "collapsing.png"), ImageFormat.Png);
                }
                Console.WriteLine("  写出 collapsing.png");

                // 展开态（看在另一条边上出现的"收起"把手）
                F(cf, "_collapsing", false);
                F(cf, "_intro", false);
                F(cf, "_introT", 1f);
                F(cf, "_collapsed", false);
                F(cf, "_show", 1f);
                FakeBackdrop(cf);
                Pump(120);
                using (Bitmap b = new Bitmap(cf.Width, cf.Height, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(b))
                        typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance)
                            .Invoke(cf, new object[] { g, cf.Width, cf.Height });
                    b.Save(Path.Combine(outDir, "expanded_nub.png"), ImageFormat.Png);
                }
                Console.WriteLine("  写出 expanded_nub.png");
                try { cf.Close(); cf.Dispose(); } catch { }
            }

            Console.WriteLine("轮盘:");
            ShotWheel(mgr, s, Path.Combine(outDir, "wheel_bl.png"), null);

            // 缩放适配
            s.UiScale = 100; ShotWheel(mgr, s, Path.Combine(outDir, "scale_100.png"), null);
            s.UiScale = 125; ShotWheel(mgr, s, Path.Combine(outDir, "scale_125.png"), null);
            s.UiScale = 150; ShotWheel(mgr, s, Path.Combine(outDir, "scale_150.png"), null);
            s.UiScale = 80; ShotWheel(mgr, s, Path.Combine(outDir, "scale_80.png"), null);
            s.UiScale = 0;

            // 三种风格
            s.UiStyle = "flat"; ShotWheel(mgr, s, Path.Combine(outDir, "style_flat.png"), null);
            s.UiStyle = "solid"; ShotWheel(mgr, s, Path.Combine(outDir, "style_solid.png"), null);
            s.UiStyle = "neu";
            s.GlassPercent = 45; ShotWheel(mgr, s, Path.Combine(outDir, "style_thin_glass.png"), null);
            s.GlassPercent = 80;
            s.CardRadius = 30; s.ShadowPercent = 100; ShotWheel(mgr, s, Path.Combine(outDir, "style_round_heavy.png"), null);
            s.CardRadius = 0; s.ShadowPercent = 0; ShotWheel(mgr, s, Path.Combine(outDir, "style_zero.png"), null);
            s.CardRadius = 14; s.ShadowPercent = 55;
            s.AccentIndex = 1; ShotWheel(mgr, s, Path.Combine(outDir, "style_accent_red.png"), null);
            s.AccentIndex = -1;
            s.ShowNameLabel = false; s.ShowCountLabel = false;
            ShotWheel(mgr, s, Path.Combine(outDir, "style_no_labels.png"), null);
            s.ShowNameLabel = true; s.ShowCountLabel = true;

            ShotWheel(mgr, s, Path.Combine(outDir, "wheel_menu.png"), delegate(WheelForm f)
            { F(f, "_menuOpen", true); F(f, "_menuT", 1f); F(f, "_sector", 1); F(f, "_keyDown", true); });

            ShotWheel(mgr, s, Path.Combine(outDir, "wheel_delconfirm.png"), delegate(WheelForm f)
            { F(f, "_delConfirm", true); F(f, "_delConfirmAt", DateTime.Now); F(f, "_delHalf", 1); });

            ShotWheel(mgr, s, Path.Combine(outDir, "wheel_drop.png"), delegate(WheelForm f)
            { F(f, "_dropActive", true); F(f, "_dropExternal", true); F(f, "_dropCount", 3); });

            ShotWheel(mgr, s, Path.Combine(outDir, "wheel_intro40.png"), delegate(WheelForm f)
            { F(f, "_intro", true); F(f, "_introT", 0.4f); });

            ShotWheel(mgr, s, Path.Combine(outDir, "wheel_intro80.png"), delegate(WheelForm f)
            { F(f, "_intro", true); F(f, "_introT", 0.8f); });

            while (st.Items.Count > 0) st.Items.RemoveAt(0);
            ShotWheel(mgr, s, Path.Combine(outDir, "wheel_empty.png"), null);

            Console.WriteLine("设置窗口:");
            ShotSettings(s, Path.Combine(outDir, "settings.png"));

            Console.WriteLine("新手引导:");
            {
                GuideForm gf = new GuideForm();
                gf.StartPosition = FormStartPosition.Manual;
                gf.Location = new Point(-4000, -4000);
                gf.Show();
                Pump(400);
                Bitmap b = new Bitmap(gf.Width, gf.Height);
                gf.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                b.Save(Path.Combine(outDir, "guide.png"), ImageFormat.Png);
                b.Dispose();
                Console.WriteLine("  写出 guide.png (" + gf.Width + "x" + gf.Height + ")");
                gf.Close(); gf.Dispose();
            }

            Console.WriteLine("完成 -> " + outDir);
        }
    }
}
