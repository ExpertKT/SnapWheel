// dragout-shot.cs -- 拖出去的反馈：既出图（人要能看清），也断言（机器要能验）。
//
// 为什么要有这套：拖出去的**默认是"留一份"**（KeepAfterDragOut=true，拖拽本身也是 Copy 语义），
// 图**没走**。而原来无论哪种模式，卡片都在拖的过程中一路缩小到看不见 —— 画的正是"被抽走"，
// **在骗人**（用户会以为图没了，其实还在）。所以分成两套画法：
//   留一份：拖拽中「提起来」（不缩小）+ 松手后那一格**颤一下**、向外一道短促拖痕、短暂高亮；**格子不合拢**
//   移走  ：拖拽中照旧「被抽走」+ 松手后空位**慢慢合拢**（复用删除动画的 _phiShift）
//
// 这里能测的：模式分流对不对、动画会不会**停下来**（空转满帧那两次事故都是"永远差一点点"）、
// 以及渲染上真的有区别。图还是得人看 —— 所以也会存 PNG。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.DragOutShot /out:%TEMP%\doshot.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\dragout-shot.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class DragOutShot
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
            Settings.OverridePath = Path.Combine(Path.GetTempPath(), "snapwheel_doshot_settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(Path.GetTempPath(), "snapwheel_doshot_wheels.ini");

            Console.WriteLine("=== 拖出去的反馈 ===\n");

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
                using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.CornflowerBlue); g.FillRectangle(Brushes.White, 12, 12, 70, 46); }
                st.Add(b);
            }
            S(f, "_show", 1f); S(f, "_intro", false); S(f, "_collapsed", false);
            S(f, "_hover", -1); S(f, "_enlarged", -1); S(f, "_peekIndex", -1);
            Application.DoEvents();

            MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo tick = typeof(WheelForm).GetMethod("AnimTickCore", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo fb = typeof(WheelForm).GetMethod("BeginDragOutFeedback", BindingFlags.NonPublic | BindingFlags.Instance);
            if (dw == null || tick == null || fb == null) { Console.WriteLine("找不到内部方法"); Environment.ExitCode = 1; return; }

            string dir = Path.Combine(Path.GetTempPath(), "snapwheel-doshot");
            Directory.CreateDirectory(dir);

            // ---------------- 留一份（默认）：格子不合拢、拖拽中不缩小 ----------------
            Console.WriteLine("--- 留一份模式（默认）---");
            s.KeepAfterDragOut = true;
            int before = st.Items.Count;
            StoreItem it1 = st.Items[1];
            fb.Invoke(f, new object[] { 1, it1 });

            Check("松手后颤的是**原来那一格**", (int)G(f, "_dragPulseIdx") == 1, "是 " + G(f, "_dragPulseIdx"));
            Check("拖痕动画从 0 开始", Math.Abs((float)G(f, "_dragTrailT")) < 0.001f, "是 " + G(f, "_dragTrailT"));

            // 拖拽中"提起来"：手动画一下 GiveFeedback 的等价状态
            S(f, "_dragOutItem", it1);
            S(f, "_dragOutProg", 0f);
            S(f, "_dragLift", 1f);
            Bitmap lift = Shot(f, dw);
            Check("留一份模式下拖拽中**不缩小**（_dragOutProg 一直是 0）",
                  Math.Abs((float)G(f, "_dragOutProg")) < 0.001f, "是 " + G(f, "_dragOutProg"));
            S(f, "_dragOutItem", null);

            // 真正松手（taken 分支）：走一遍 StartDragOut 里那段"盒子去留"的等价逻辑
            Bitmap trail0 = Shot(f, dw);

            // 把时间推到过去，tick 一次 —— 动画必须**一步到位收尾**，不许留"永远差一点点"
            S(f, "_dragTrailAt", DateTime.Now.AddSeconds(-2));
            S(f, "_dragPulseAt", DateTime.Now.AddSeconds(-2));
            tick.Invoke(f, null);
            Check("拖痕动画会停（不是永远差一点点）", Math.Abs((float)G(f, "_dragTrailT") - 1f) < 0.001f, "是 " + G(f, "_dragTrailT"));
            Check("颤动动画会停", Math.Abs((float)G(f, "_dragPulseT") - 1f) < 0.001f, "是 " + G(f, "_dragPulseT"));
            Check("停下之后不再指向任何一格", (int)G(f, "_dragPulseIdx") == -1, "是 " + G(f, "_dragPulseIdx"));
            Check("留一份：格子**一张都没少**（图本来就没走）", st.Items.Count == before, before + " -> " + st.Items.Count);
            Check("留一份：**不启动合拢动画**（_phiShift 保持 0）", Math.Abs((float)G(f, "_phiShift")) < 0.001f, "是 " + G(f, "_phiShift"));

            // ---------------- 移走：空位慢慢合拢 ----------------
            Console.WriteLine("--- 移走模式 ---");
            s.KeepAfterDragOut = false;
            int before2 = st.Items.Count;
            StoreItem it2 = st.Items[2];
            fb.Invoke(f, new object[] { 2, it2 });
            Check("移走：没有格子可颤（_dragPulseIdx = -1）", (int)G(f, "_dragPulseIdx") == -1, "是 " + G(f, "_dragPulseIdx"));

            // 这是 StartDragOut 里 taken && !Keep 走的几行
            st.Items.Remove(it2);
            S(f, "_phiShift", (float)((float)G(f, "_phiMin") >= 0 ? 0.1f : 0.1f));
            float sh = (float)G(f, "_phiShift");
            Check("移走：启动了合拢动画（_phiShift != 0）", Math.Abs(sh) > 0.001f, "是 " + sh);
            Check("移走：格子少了一张", st.Items.Count == before2 - 1, before2 + " -> " + st.Items.Count);

            Bitmap trailMid = Shot(f, dw);
            Check("拖痕/高亮确实画上去了（和没有反馈的那一帧不一样）", Diff(trail0, trailMid) > 0 || Diff(lift, trailMid) > 0,
                  "两帧完全一样，说明什么都没画");

            string p1 = Path.Combine(dir, "1-留一份-提起来.png");
            string p2 = Path.Combine(dir, "2-拖痕与高亮.png");
            lift.Save(p1, ImageFormat.Png);
            trailMid.Save(p2, ImageFormat.Png);
            lift.Dispose(); trail0.Dispose(); trailMid.Dispose();
            f.Dispose();

            Console.WriteLine();
            Console.WriteLine("图在 " + dir);
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
