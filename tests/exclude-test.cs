using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SnapWheel
{
    static class ExcludeTest
    {
        static int pass = 0, fail = 0;
        static void Check(bool ok, string msg)
        {
            Console.WriteLine((ok ? "  OK   " : "  FAIL ") + msg);
            if (ok) pass++; else fail++;
        }
        static void F(object o, string n, object v)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null) fi.SetValue(o, v);
        }
        static void Pump(int ms)
        {
            DateTime e = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < e) { Application.DoEvents(); Thread.Sleep(5); }
        }

        static int CountMagenta(Bitmap b)
        {
            if (b == null) return -1;
            int n = 0;
            for (int y = 0; y < b.Height; y += 3)
                for (int x = 0; x < b.Width; x += 3)
                {
                    Color c = b.GetPixel(x, y);
                    if (c.R > 100 && c.B > 100 && c.G < c.R * 0.7f && c.G < c.B * 0.7f) n++;
                }
            return n;
        }

        static Bitmap BackdropOf(WheelForm f)
        {
            f.CaptureBackdrop();
            FieldInfo bf = typeof(WheelForm).GetField("_backdropBlur", BindingFlags.NonPublic | BindingFlags.Instance);
            return (Bitmap)bf.GetValue(f);
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Console.WriteLine("=== 截屏隐身（WDA_EXCLUDEFROMCAPTURE）对分层窗口的验证 ===");
            Console.WriteLine("    判据：轮盘显示中抓背景，抓到的画面里不该有轮盘自己的洋红卡片");

            Settings s = Settings.Load();
            s.SaveToDisk = false; s.Corner = "BL"; s.UiScale = 100;
            s.ThumbSize = 140; s.Radius = 300; s.Slots = 5; s.UiStyle = "neu"; s.GlassPercent = 40;
            WheelManager mgr = new WheelManager(s);
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;
            while (st.Items.Count > 0) st.Items.RemoveAt(0);

            Bitmap m = new Bitmap(520, 520, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(m)) g.Clear(Color.Magenta);
            st.Add(m);
            m.Dispose();

            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Pump(900);
            F(f, "_show", 1f); F(f, "_intro", false); F(f, "_introT", 1f);
            Pump(400);
            Console.WriteLine("  轮盘: " + f.Bounds + "  可见=" + f.Visible);

            int imgW = 0;
            try { imgW = st.Items[0].Image.Width; } catch (Exception ex) { Console.WriteLine("  读尺寸失败: " + ex.Message); }
            Check(imgW == 520, "AddCore 存了副本：原图释放后仍能读到尺寸（读到 " + imgW + "）");

            Native.SetWindowDisplayAffinity(f.Handle, 0);
            Pump(300);
            Bitmap a = BackdropOf(f);
            int ma = CountMagenta(a);
            Console.WriteLine("  A 不设隐身：背景图 " + (a == null ? "null" : a.Width + "x" + a.Height) + "，洋红采样点 " + ma);

            bool ok = Native.SetWindowDisplayAffinity(f.Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            Pump(300);
            Bitmap b = BackdropOf(f);
            int mb = CountMagenta(b);
            Console.WriteLine("  B 设了隐身：洋红采样点 " + mb);

            // 背景图得是"真截图"，不是空白/垃圾
            int colors = 0;
            if (b != null)
            {
                System.Collections.Generic.HashSet<int> seen = new System.Collections.Generic.HashSet<int>();
                for (int y = 0; y < b.Height; y += 5)
                    for (int x = 0; x < b.Width; x += 5) seen.Add(b.GetPixel(x, y).ToArgb());
                colors = seen.Count;
            }
            Check(b != null && colors > 8, "★ 抓到的背景是真实桌面画面（" + (b == null ? "null" : b.Width + "x" + b.Height) + "，颜色数 " + colors + "）");
            Check(ok, "SetWindowDisplayAffinity 调用成功（顺带好处：用户截图时轮盘不会入镜）");
            if (ma > 20) Check(mb * 4 < ma, "★ 隐身生效：抓到的背景里轮盘自己消失了（" + ma + " -> " + mb + "）");
            else Check(mb == 0, "★ 轮盘背景里不含自己的卡片（A=" + ma + " B=" + mb + "）");

            try { f.HideWheel(); f.Close(); } catch { }
            Console.WriteLine();
            Console.WriteLine("通过 " + pass + " / 失败 " + fail);
            Environment.Exit(fail == 0 ? 0 : 1);
        }
    }
}