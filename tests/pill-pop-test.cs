// pill-pop-test.cs -- 名字药丸"翻一下"的时候，**字有没有跟着一起放大**。
//
// 为什么要有这一条：用户报「那个胶囊的动画很不错，但是字不会和动画一起放大缩小」。
// 根因是我上一版只把**药丸的矩形**改大，字还是原字号、只是被重新居中 ——
// 看起来就是"框在动、字不动"。
//
// 这类"框动字不动"的 bug 光看代码很容易漏（矩形确实变了、看起来逻辑是对的），
// 但**量字墨迹的包围盒**就一目了然：字跟着缩放，墨迹就会变大。
//
// 做法：把 _nameSwapT 定在几个值各渲染一帧，在药丸附近找"接近纯白"的墨迹
// （字是白色、药丸底是浅灰玻璃，明显没那么白），量它的包围盒。
//
// ⚠️ 设完 _nameSwapT **绝对不能**再 Application.DoEvents() —— 动画定时器会立刻把它推回 1
//    （`_nameSwapAt` 是旧的、elapsed 很大），于是每一帧都测成"没在翻"。
//    第一版就是这么量出"三次一模一样"的，差点以为是代码没生效。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.PillPopTest /out:%TEMP%\pp.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\pill-pop-test.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class PillPopTest
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
            return fi == null ? null : fi.GetValue(o);
        }
        static void S(object o, string n, object v)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi != null) fi.SetValue(o, v);
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

        // 量"字墨迹"：药丸里**最亮的那一簇**像素的包围盒
        //
        // ⚠️ 判据必须是**相对亮度**，不能用绝对颜色。
        // 原来写死"近白"（A>200 且 R>232 且 G>236 且 B>240），本机够用；但在 CI 那台 runner 上
        // 整帧的文字都比本机暗 —— 测得整窗只有 34 个采样点达标、而且全在药丸之外，
        // 于是药丸**明明画了**（_namePillRect 和真实矩形逐位一致）却量到 0 个像素，CI 直接红。
        // 现在先扫一遍求最大亮度，再取 >= 0.82 倍的那些：字比药丸底亮这件事在本机和 CI 上都成立，
        // 跟全局透明度、跟"白到什么程度"都无关。
        static void Ink(WheelForm f, MethodInfo dw, RectangleF rc, out int w, out int h, out int n, out int maxL)
        {
            w = 0; h = 0; n = 0; maxL = 0;
            int minX = 99999, maxX = -1, minY = 99999, maxY = -1;
            using (Bitmap b = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height), PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(b)) dw.Invoke(f, new object[] { g, f.Width, f.Height });
                int x0 = Math.Max(0, (int)rc.X) - 8, x1 = Math.Min(b.Width, (int)rc.Right) + 8;
                int y0 = Math.Max(0, (int)rc.Y) - 8, y1 = Math.Min(b.Height, (int)rc.Bottom) + 8;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        Color c0 = b.GetPixel(x, y);
                        if (c0.A < 40) continue;
                        int l0 = (c0.R + c0.G + c0.B) / 3;
                        if (l0 > maxL) maxL = l0;
                    }
                int thr = (int)(maxL * 0.82f); if (thr < 40) thr = 40;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        if (c.A > 40 && (c.R + c.G + c.B) / 3 >= thr)
                        {
                            n++;
                            if (x < minX) minX = x; if (x > maxX) maxX = x;
                            if (y < minY) minY = y; if (y > maxY) maxY = y;
                        }
                    }
            }
            if (maxX >= minX) { w = maxX - minX + 1; h = maxY - minY + 1; }
        }

        static void Run()
        {
            try { Lang.Init("zh"); } catch { }
            Application.EnableVisualStyles();
            Settings.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_pp_settings.ini");
            WheelManager.OverrideMetaPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_pp_wheels.ini");

            Console.WriteLine("=== 名字药丸：翻的时候字要跟着放大 ===\n");

            Settings s = new Settings();
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;
            for (int i = 0; i < 4; i++)
            {
                Bitmap b = new Bitmap(300, 200, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b)) g.Clear(Color.CornflowerBlue);
                st.Add(b);
            }
            S(f, "_show", 1f); S(f, "_intro", false); S(f, "_collapsing", false);
            S(f, "_collapsed", false); S(f, "_showAnimating", false);
            S(f, "_hover", -1); S(f, "_enlarged", -1); S(f, "_peekIndex", -1);
            S(f, "_nameSwapT", 1f);
            Application.DoEvents();

            MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo nr = typeof(WheelForm).GetMethod("NamePillRect", BindingFlags.NonPublic | BindingFlags.Instance);
            if (dw == null || nr == null) { Check("找得到内部方法", false, "DrawWheel / NamePillRect"); return; }

            // 先渲染一帧静息态，让 _namePillRect 有值
            using (Bitmap b0 = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)))
            using (Graphics g0 = Graphics.FromImage(b0)) dw.Invoke(f, new object[] { g0, f.Width, f.Height });
            RectangleF rc = (RectangleF)nr.Invoke(f, null);

            // 静息（没在翻）
            S(f, "_nameSwapT", 1f);
            int w0, h0, n0, l0m; Ink(f, dw, rc, out w0, out h0, out n0, out l0m);
            // 翻到最大（_nameSwapT=0.5 → sin(π/2)=1）
            S(f, "_nameSwapT", 0.5f);
            int w1, h1, n1, l1m; Ink(f, dw, rc, out w1, out h1, out n1, out l1m);

            // CI 上量到过 0 像素（本机 189）。本机复现不出来（强制 K=1/1.25/1.5 都一样），
            // 所以把当时的现场打出来，让 CI 的日志自己说清楚是"药丸没画"还是"位置不对"。
            Console.WriteLine("  窗口 {0}x{1}  UiK={2}  药丸矩形={3}  在窗口内={4}",
                f.Width, f.Height, G(f, "UiK"), rc,
                (rc.X >= 0 && rc.Y >= 0 && rc.Right <= f.Width && rc.Bottom <= f.Height));
            Console.WriteLine("  静息   字墨迹 {0}x{1}（{2} 像素）", w0, h0, n0);
            Console.WriteLine("  翻到顶 字墨迹 {0}x{1}（{2} 像素）", w1, h1, n1);
            Console.WriteLine();

            // 整窗找一遍近白像素：如果窗口里别处有、药丸那儿没有，说明药丸挪位了；
            // 整窗一个都没有，说明那段绘制根本没走。
            int allInk = 0, ax0 = 99999, ay0 = 99999, ax1 = -1, ay1 = -1;
            using (Bitmap b = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height), PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(b)) dw.Invoke(f, new object[] { g, f.Width, f.Height });
                for (int y = 0; y < b.Height; y += 2)
                    for (int x = 0; x < b.Width; x += 2)
                    {
                        Color c = b.GetPixel(x, y);
                        if (c.A > 200 && c.R > 232 && c.G > 236 && c.B > 240)
                        { allInk++; if (x < ax0) ax0 = x; if (x > ax1) ax1 = x; if (y < ay0) ay0 = y; if (y > ay1) ay1 = y; }
                    }
            }
            object rawPill = G(f, "_namePillRect");
            string diag = " ｜ 窗口 " + f.Width + "x" + f.Height + " UiK=" + G(f, "UiK")
                  + " 药丸矩形=" + rc + " _namePillRect=" + rawPill
                  + " 显示名字=" + ((Settings)G(f, "_settings")).ShowNameLabel
                  + " 整窗近白=" + allInk + " bbox=(" + ax0 + "," + ay0 + ")-(" + ax1 + "," + ay1 + ")"
                  + " 药丸内最亮=" + l0m + "/" + l1m;
            Check("字墨迹真的存在（量不到就说明测的不是字）", n0 > 60 && n1 > 60,
                  "静息 " + n0 + " / 翻到顶 " + n1 + diag);
            Check("翻的时候**字跟着一起放大**（面积至少大 6%）",
                  n1 > n0 * 1.06f, "静息 " + n0 + " → 翻到顶 " + n1 + "，几乎没变大 = 框动字不动");
            Check("翻的时候字的宽度也变大", w1 > w0, w0 + " → " + w1);

            // 动画必须有始有终（跑完停在 1，不留"永远差一点点"）
            S(f, "_nameSwapT", 0f);
            S(f, "_nameSwapAt", DateTime.Now.AddSeconds(-2));
            for (int i = 0; i < 20 && Math.Abs((float)G(f, "_nameSwapT") - 1f) > 0.001f; i++)
            { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
            float fin = (float)G(f, "_nameSwapT");
            Check("翻完会停在 1（不是永远差一点点）", Math.Abs(fin - 1f) < 0.001f, "停在 " + fin);

            f.Dispose();
            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
