// glass-skip-test.cs -- 新底和旧底一模一样时，不许做交叉淡入。
//
// 为什么：定时刷新每 3.5 秒抓一次屏，抓到的内容和手上那张**经常完全相同**
// （看文档/看网页/桌面没动时）。而交叉淡入那 0.38 秒里每一帧都要把两张**整窗**底图混一次
// （实测 3.78ms，是那一档最大的单项），再加上控件层缓存失效要整层重画 ——
// 全是为了把一张图淡入到**它自己**身上。用户机器上 10 秒 67 帧几乎全是这种白干的帧。
//
// 这条检查盯四件事：
//   ① 底图一样 → 不淡入（_backdropOld 保持空、fade 保持 1）
//   ② 底图一样 → 计数器 +1（证明真的走了这条路，不是"碰巧"）
//   ③ **底图不一样 → 必须照常淡入**（不能为了省事把该有的过渡也砍掉）
//   ④ 底图尺寸变了 → 也算"不一样"（换分辨率/换屏的情况）
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.GlassSkipTest /out:%TEMP%\gs.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\glass-skip-test.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class GlassSkipTest
    {
        static int pass, fail;
        static void Check(string n, bool ok, string d)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + n); }
            else { fail++; Console.WriteLine("  [FAIL] " + n + "   " + d); }
        }
        static object G(object o, string n)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            if (fi == null) throw new Exception("找不到字段 " + n);
            return fi.GetValue(o);
        }
        static void S(object o, string n, object v)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            fi.SetValue(o, v);
        }

        static Bitmap Blur(WheelForm f, Bitmap src)
        {
            MethodInfo bm = typeof(WheelForm).GetMethod("BlurBitmap", BindingFlags.NonPublic | BindingFlags.Static);
            return (Bitmap)bm.Invoke(null, new object[] { src, 6 });
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

        static void Run()
        {
            try { Lang.Init("zh"); } catch { }
            Application.EnableVisualStyles();
            Settings.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_gs_settings.ini");
            WheelManager.OverrideMetaPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_gs_wheels.ini");

            Console.WriteLine("=== 换底：一样就不淡入 ===\n");

            Settings s = new Settings();
            s.GlassRefresh = false;   // 关掉定时抓屏：否则真实的 3.5 秒抓屏会插进来，把测试搅浑（抓到的是真屏幕，和我造的假底不同）
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();
            S(f, "_show", 1f); S(f, "_intro", false); S(f, "_collapsed", false);
            Application.DoEvents();

            MethodInfo apply = typeof(WheelForm).GetMethod("ApplyPendingBackdrop", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo fPending = typeof(WheelForm).GetField("_glassPending", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo fPendingOff = typeof(WheelForm).GetField("_glassPendingOffset", BindingFlags.NonPublic | BindingFlags.Instance);
            if (apply == null || fPending == null) { Check("找得到内部成员", false, "ApplyPendingBackdrop/_glassPending"); return; }

            int w = Math.Max(40, f.Width), h = Math.Max(40, f.Height);

            // 先放一张底进去（这一次没有旧底可比，必然直接换上）
            Bitmap baseShot = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(baseShot))
            {
                g.Clear(Color.FromArgb(90, 120, 160));
                g.FillRectangle(Brushes.White, 10, 10, w / 2, h / 3);
            }
            fPending.SetValue(f, Blur(f, baseShot));
            fPendingOff.SetValue(f, Point.Empty);
            apply.Invoke(f, null);
            Check("第一张底直接换上（没有旧底可比）", G(f, "_backdropBlur") != null, "底图还是空的");
            // 摆一个干净的起点：没有旧底、没有在淡入
            S(f, "_backdropOld", null); S(f, "_backdropFade", 1f);

            int before = (int)G(f, "GlassSkipForTest");

            // ① 再来一张**一模一样**的
            fPending.SetValue(f, Blur(f, baseShot));
            fPendingOff.SetValue(f, Point.Empty);
            apply.Invoke(f, null);      // **不泵消息** —— 一泵就可能插进别的抓屏
            int after = (int)G(f, "GlassSkipForTest");
            object oldBd = G(f, "_backdropOld");
            float fade = (float)G(f, "_backdropFade");

            Check("① 底图一样 → 没有开始交叉淡入（_backdropOld 还是空）", oldBd == null, "旧底被设上了");
            Check("① 底图一样 → fade 保持在 1（没有过渡要播）", Math.Abs(fade - 1f) < 0.001f, "fade=" + fade);
            Check("② 计数器 +1（真的走了这条路，不是碰巧）", after == before + 1,
                  before + " → " + after);

            // ③ 换一张**内容不同**的 —— 必须照常淡入
            using (Bitmap diff = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(diff))
                {
                    g.Clear(Color.FromArgb(90, 120, 160));
                    g.FillRectangle(Brushes.Red, 0, h / 2, w, h / 2);      // 下半屏变红
                }
                fPending.SetValue(f, Blur(f, diff));
                fPendingOff.SetValue(f, Point.Empty);
                apply.Invoke(f, null);
            }
            int after2 = (int)G(f, "GlassSkipForTest");
            Check("③ 底图**不一样** → 照常开始交叉淡入（不能把该有的过渡也砍掉）",
                  G(f, "_backdropOld") != null && (float)G(f, "_backdropFade") < 0.999f,
                  "old=" + (G(f, "_backdropOld") != null) + " fade=" + G(f, "_backdropFade"));
            Check("③ 底图不一样 → 计数器**不该**增加（不算跳过）", after2 == after, after + " → " + after2);

            baseShot.Dispose();
            f.Dispose();
            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
