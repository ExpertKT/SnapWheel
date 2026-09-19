// hint-fade-test.cs -- 把手旁边那句提示（"点我展开 / 点我收起"）到底有没有过渡。
//
// 用户反馈：「开始、展开和收起动画提示都没有过渡」。
//
// 两个叠在一起的缺陷：
//   ① 61b-WheelForm.DrawKey.cs：把手本体用 `a * vis * vis` 平滑淡入淡出，
//      而旁边那行提示写的是 `vis > 0.98f ? _nubHintT : 0f` —— 硬切。
//      把手在慢慢淡，提示却在跨过 0.98 的那一帧整块出现/整块消失。
//   ② 66-WheelForm.Layers.cs：这行提示画在**控件层里面**，而"该禁缓存"的清单漏了 `_nubHintT`，
//      于是提示的淡入淡出被整块烤死在层位图里。
//
// 怎么测：
//   提示是画在把手旁边的，直接数总像素会被把手本身的变化盖住。
//   所以用**差分**：同一个 vis 下，分别画"_nubHintT = 1"和"_nubHintT = 0"两张，
//   相减得到的就是**提示自己的墨量**。然后看它是不是跟着 vis 连续长起来。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.HintFadeTest /out:%TEMP%\hf.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\hint-fade-test.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class HintFadeTest
    {
        static int pass, fail;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + "   " + detail); }
        }

        static FieldInfo Field(string n)
        {
            FieldInfo fi = typeof(WheelForm).GetField(n, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            return fi;
        }

        // 只画"一个把手"（单把手模式，用户就是这个设置），其余什么都不画
        static Bitmap RenderNub(WheelForm f, float vis, float hintT)
        {
            Field("_nubHintT").SetValue(f, hintT);
            MethodInfo dn = typeof(WheelForm).GetMethod("DrawNubOne", BindingFlags.NonPublic | BindingFlags.Instance);
            Bitmap b = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
                dn.Invoke(f, new object[] { g, 255, true, vis });
            return b;
        }

        static int Ink(Bitmap b)
        {
            int n = 0;
            for (int y = 0; y < b.Height; y += 2)
                for (int x = 0; x < b.Width; x += 2)
                    if (b.GetPixel(x, y).A > 40) n++;
            return n;
        }

        // 提示自己的墨量 = 有提示那张 - 没提示那张。
        //
        // 注意要累加**差的绝对值**（强度），不是"有多少个像素不同"：
        // 提示的形状不变、只是 alpha 在变，数不同像素的个数会得到一条水平线
        // （第一版就是这么写的，五个 vis 全部量出 419 —— 看着像"没变化"，其实是量错了）。
        static int HintInk(WheelForm f, float vis)
        {
            using (Bitmap with = RenderNub(f, vis, 1f))
            using (Bitmap without = RenderNub(f, vis, 0f))
            {
                int d = 0;
                for (int y = 0; y < with.Height; y += 2)
                    for (int x = 0; x < with.Width; x += 2)
                    {
                        Color ca = with.GetPixel(x, y), cb = without.GetPixel(x, y);
                        d += Math.Abs(ca.A - cb.A)
                           + Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);
                    }
                return d;
            }
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
            Settings.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_hf_settings.ini");
            WheelManager.OverrideMetaPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_hf_wheels.ini");

            Console.WriteLine("=== 把手提示的淡入淡出 ===\n");

            Settings s = new Settings();
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();

            // ---------- ① 提示的墨量必须跟着 vis 连续长起来 ----------
            float[] vs = { 0.20f, 0.50f, 0.80f, 0.98f, 1.00f };
            int[] ink = new int[vs.Length];
            for (int i = 0; i < vs.Length; i++) ink[i] = HintInk(f, vs[i]);

            Console.WriteLine("   （提示自己的墨量）");
            for (int i = 0; i < vs.Length; i++)
                Console.WriteLine("      vis={0:F2} → {1}", vs[i], ink[i]);

            int full = ink[vs.Length - 1];
            Check("把手完全可见时提示是画出来的（否则下面几条没意义）", full > 0, "墨量 0");

            Check("vis=0.50 时提示**已经有一部分**（不是非要 >0.98 才出现）",
                  ink[1] > 0, "vis=0.50 时墨量还是 0 —— 说明还是阈值开关");
            Check("提示墨量随 vis 单调不减", ink[0] <= ink[1] && ink[1] <= ink[2] && ink[2] <= ink[3] && ink[3] <= ink[4],
                  string.Join(",", Array.ConvertAll(ink, delegate(int v) { return v.ToString(); })));

            // 跨过原来的 0.98 那个门槛，墨量不该有台阶
            int step = Math.Abs(ink[4] - ink[3]);
            Check("跨过 0.98 没有台阶（原来的硬切就在这里）",
                  step <= Math.Max(2, full / 4),
                  string.Format("0.98→1.00 一下跳了 {0}，满值才 {1}", step, full));

            // ---------- ② 提示淡入淡出期间，控件层不能用缓存 ----------
            {
                MethodInfo la = typeof(WheelForm).GetMethod("LayerAllowed", BindingFlags.NonPublic | BindingFlags.Instance);

                // 先把**其它**会让缓存失效的条件都推回稳态，否则量不出这一条到底管不管用
                // （刚 ShowWheel 完 _show/_nubAppearT 还在动，缓存本来就该禁）。
                Field("_show").SetValue(f, 1f);
                Field("_nubAppearT").SetValue(f, 1f);
                Field("_intro").SetValue(f, false);
                Field("_collapsing").SetValue(f, false);
                Field("_showAnimating").SetValue(f, false);
                Application.DoEvents();

                Field("_nubHintT").SetValue(f, 1f);
                bool allowedFull = (bool)la.Invoke(f, new object[] { 1 });
                Field("_nubHintT").SetValue(f, 0.5f);
                bool allowedMid = (bool)la.Invoke(f, new object[] { 1 });

                Console.WriteLine("   （控件层缓存）提示全亮时允许={0}  提示半透明时允许={1}", allowedFull, allowedMid);

                Check("提示稳定全亮、其它都稳态时，允许缓存（性能不能白丢）",
                      allowedFull, "稳态下也不许缓存，白丢性能");
                Check("提示正在淡入淡出时，控件层**不许**用缓存（否则这一段被烤死）",
                      !allowedMid, "半透明时还允许缓存，提示会被冻住");
            }

            f.Dispose();
            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
