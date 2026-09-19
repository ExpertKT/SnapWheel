// diag-shot.cs -- 诊断模式：既出图（人要能看清），也断言（机器要能验）。
//
// 诊断模式的意义是"用户截一张图，我就知道每个元素叫什么"。
// 所以它有两个要求，缺一不可：
//   ① 图必须**看得清** —— 这只能靠人眼，所以这个工具会输出 PNG；
//   ② 元素清单必须**真的被收集到** —— 这靠断言。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.DiagShot /out:%TEMP%\diag.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\diag-shot.cs
using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class DiagShot
    {
        static int pass, fail;
        static void Check(string n, bool ok, string d)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + n); }
            else { fail++; Console.WriteLine("  [FAIL] " + n + "   " + d); }
        }

        static object G(object o, string n)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            return fi.GetValue(o);
        }
        static void S(object o, string n, object v)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            fi.SetValue(o, v);
        }

        static Bitmap Shot(WheelForm f, MethodInfo dw)
        {
            Bitmap b = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b)) dw.Invoke(f, new object[] { g, f.Width, f.Height });
            return b;
        }

        static int Diff(Bitmap a, Bitmap b)
        {
            int d = 0;
            for (int y = 0; y < a.Height; y += 2)
                for (int x = 0; x < a.Width; x += 2)
                    if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) d++;
            return d;
        }

        [STAThread]
        static void Main()
        {
            try { Run(); }
            catch (Exception ex)
            {
                Console.WriteLine("出错：" + ex.GetType().Name + "  " + ex.Message);
                if (ex.InnerException != null) Console.WriteLine("  内层：" + ex.InnerException.Message);
                Environment.ExitCode = 1;
            }
        }

        static void Run()
        {
            try { Lang.Init("zh"); } catch { }
            Application.EnableVisualStyles();
            Settings.OverridePath = Path.Combine(Path.GetTempPath(), "snapwheel_diag_settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(Path.GetTempPath(), "snapwheel_diag_wheels.ini");

            Console.WriteLine("=== 诊断模式 ===\n");

            Settings s = new Settings();
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();

            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;
            for (int i = 0; i < 6; i++)
            {
                Bitmap b = new Bitmap(320 + i * 40, 200, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.CornflowerBlue); g.FillRectangle(Brushes.White, 10, 10, 60, 40); }
                st.Add(b);
            }

            S(f, "_show", 1f); S(f, "_intro", false); S(f, "_collapsed", false);
            MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);

            // 关掉诊断
            s.DiagMode = false;
            Application.DoEvents();
            Bitmap off = Shot(f, dw);
            int offCount = ((IList)G(f, "_diag")).Count;

            // 打开诊断
            s.DiagMode = true;
            Bitmap on = Shot(f, dw);
            IList list = (IList)G(f, "_diag");
            int onCount = list.Count;

            Console.WriteLine("  诊断关：收集到 {0} 个元素", offCount);
            Console.WriteLine("  诊断开：收集到 {0} 个元素", onCount);
            Console.WriteLine("  收集到的名字：");
            for (int i = 0; i < list.Count; i++)
            {
                object kv = list[i];
                object k = kv.GetType().GetProperty("Key").GetValue(kv, null);
                Console.WriteLine("      " + k);
            }

            Check("关着的时候一个都不收集（零开销）", offCount == 0, "收集了 " + offCount);
            Check("开着的时候确实收到了元素（≥ 8 个）", onCount >= 8, "只有 " + onCount);
            Check("诊断图和不诊断的图**不一样**（说明真的画上去了）", Diff(off, on) > 0, "两张一模一样");

            // 崩溃防护：鼠标在图外 / 元素重叠时也不能抛
            bool noThrow = true;
            try { Shot(f, dw); Shot(f, dw); } catch { noThrow = false; }
            Check("连续渲染不出错", noThrow, "渲染时抛异常了");

            string dir = Path.Combine(Path.GetTempPath(), "snapwheel-diag");
            Directory.CreateDirectory(dir);
            string p1 = Path.Combine(dir, "1-诊断关.png"), p2 = Path.Combine(dir, "2-诊断开.png");
            off.Save(p1, ImageFormat.Png);
            on.Save(p2, ImageFormat.Png);
            off.Dispose(); on.Dispose(); f.Dispose();

            Console.WriteLine();
            Console.WriteLine("图在 " + dir);
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
