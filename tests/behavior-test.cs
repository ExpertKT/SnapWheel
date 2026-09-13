// 行为测试：测「做完之后真的发生了什么」，跟绘制测试（render-smoke）互补。
//
// 为什么单开一个：v0.4.8 那轮修掉的四个 bug（删除不落盘、连点删除两张都删不掉、
// 点设置确定轮盘消失、万能键动作名改完不变）全都是"编译过、150 项绘制断言全绿、
// 但行为是错的"。这个文件专门盯这几条行为，防止改回去。
//
// 编译（build.ps1 -Test 会自动跑）：
//   csc /nologo /target:exe /main:SnapWheel.BehaviorTest /out:btest.exe src\*.cs tests\behavior-test.cs
//
// 安全：全程把设置文件和轮盘清单指到临时目录（Settings.OverridePath /
// WheelManager.OverrideMetaPath），绝不碰用户真实的 %APPDATA%\SnapWheel。
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
    static class BehaviorTest
    {
        static int pass = 0, fail = 0;
        static string tmp;

        // ---------- 反射小工具（测私有字段/方法，和 render-smoke 一致） ----------
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

        // ---- 每个窗口都登记，测完就释放 ----
        // 为什么必须这样：轮盘窗口带着"整屏毛玻璃底图"（虚拟屏 3755x1152 ≈ 17MB 一张），
        // 测试里几十个窗口不释放 → 内存一路涨、GC 一路抖，一条测试能从 0.1 秒拖到 90 秒。
        // 慢到那种程度会掩盖真正的问题（曾经就是这样误判成"卡死"）。
        static readonly List<Form> _liveForms = new List<Form>();

        static T Track<T>(T f) where T : Form { _liveForms.Add(f); return f; }

        static WheelForm NewWheel(WheelManager mgr, Settings s) { return Track(new WheelForm(mgr, s)); }

        static void CleanupForms()
        {
            for (int i = 0; i < _liveForms.Count; i++)
            {
                try { _liveForms[i].Dispose(); } catch { }
            }
            _liveForms.Clear();
        }

        static void Check(string what, bool ok, string detail)
        {
            Console.WriteLine("  {0} {1}{2}", ok ? "OK  " : "FAIL", what,
                string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）");
            if (ok) pass++; else fail++;
        }

        // 心跳：万一哪条测试卡住（比如某个后台定时器抛异常→WinForms 弹出错框→卡死），
        // 至少能看出卡在哪一条上，而不是看着半天没有输出。
        static volatile string currentTest = "(还没开始)";
        static volatile bool finished = false;
        static DateTime testStart = DateTime.Now;

        static void Run(string what, Func<string> test)
        {
            // 只跑名字里含 SW_TEST_ONLY 的那几条（调试用，比如只跑"撤销删除"那几条）
            string only = Environment.GetEnvironmentVariable("SW_TEST_ONLY");
            if (!string.IsNullOrEmpty(only) && what.IndexOf(only, StringComparison.Ordinal) < 0)
            {
                Console.WriteLine("  跳过 {0}", what);
                return;
            }
            currentTest = what;
            testStart = DateTime.Now;
            try
            {
                string detail = test();
                double sec = (DateTime.Now - testStart).TotalSeconds;
                // 只有明显慢的才把秒数带出来：整套应该几秒跑完，超过 5 秒就值得看一眼
                Check(what, detail == null, detail + (sec >= 5.0 ? "  [这条跑了 " + sec.ToString("0.0") + " 秒]" : ""));
            }
            catch (Exception ex)
            {
                Exception real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Check(what, false, real.GetType().Name + ": " + real.Message);
            }
            CleanupForms();     // 不留窗口：否则内存越跑越大，后面每条都变慢
        }

        static Bitmap Solid(int w, int h, Color c)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
            return b;
        }

        // 空转动画帧，直到"正在删的那张"被真正删掉（或超时）
        static bool TickUntilDeleted(WheelForm f, int maxFrames)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                if (G(f, "_deletingItem") == null) return true;
                Call(f, "AnimTickCore");
            }
            return G(f, "_deletingItem") == null;
        }

        // 场景：轮盘 → 改设置 → 点确定（AfterSettingsApplied）→ 轮盘必须还是看得见的样子。
        // 返回 null 表示通过，否则返回"哪里不对"。
        static string SettingsApplyScene(bool startCollapse, bool collapseFirst, bool endCollapse)
        {
            Settings s = new Settings();
            s.SaveToDisk = false;
            s.CollapseMode = startCollapse;
            WheelManager mgr = new WheelManager(s);
            WheelForm f = NewWheel(mgr, s);
            f.ShowWheel();
            Application.DoEvents();

            if (collapseFirst)
            {
                f.CollapseWheel(true);
                for (int i = 0; i < 400 && !f.IsCollapsed; i++) { Call(f, "AnimTickCore"); Thread.Sleep(5); }
                if (!f.IsCollapsed) return "前置条件不成立：没进收起态";
            }

            s.CollapseMode = endCollapse;      // 用户在设置窗口里改的值
            f.AfterSettingsApplied();          // 点确定后的界面侧收尾

            for (int i = 0; i < 400 && Convert.ToSingle(G(f, "_show")) < 0.99f; i++) { Call(f, "AnimTickCore"); Thread.Sleep(5); }
            if (f.IsCollapsed) return "还卡在收起态（只剩个把手）";
            if (!f.Visible) return "轮盘被藏起来了";
            float tgt = Convert.ToSingle(G(f, "_targetShow"));
            if (tgt < 0.99f) return "展开目标被改成 " + tgt.ToString("0.00") + "（=隐藏）";
            float show = Convert.ToSingle(G(f, "_show"));
            if (show < 0.99f) return "等了一秒轮盘还是没显示出来（show=" + show.ToString("0.00") + "）";
            return null;
        }

        // ---------- 标注测试用的小工具 ----------
        static void Mouse(object o, string handler, int x, int y)
        {
            MethodInfo m = o.GetType().GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) throw new Exception("找不到 " + handler);
            m.Invoke(o, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
        }

        static object AnnotKindValue(string name)
        {
            Type t = typeof(OverlayForm).GetNestedType("AnnotKind", BindingFlags.NonPublic);
            if (t == null) throw new Exception("找不到 AnnotKind");
            return Enum.Parse(t, name);
        }

        static int ShapeCount(object overlay)
        {
            return ((System.Collections.ICollection)G(overlay, "_shapes")).Count;
        }

        static double RegionVariance(Bitmap b, Rectangle r)
        {
            double s = 0, s2 = 0; int n = 0;
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) continue;
                    double v = b.GetPixel(x, y).R; s += v; s2 += v * v; n++;
                }
            if (n == 0) return 0;
            double m = s / n;
            return s2 / n - m * m;
        }

        static int RedPixels(Bitmap b, Rectangle r)
        {
            int n = 0;
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) continue;
                    Color c = b.GetPixel(x, y);
                    if (c.R > 150 && c.G < 130 && c.B < 130) n++;
                }
            return n;
        }

        // 造一个"选好区、工具已选"的浮层（不显示出来，免得测试时满屏闪一个遮罩）
        static OverlayForm MakeOverlay(Bitmap shot, string tool)
        {
            OverlayForm o = Track(new OverlayForm(new Rectangle(0, 0, 1920, 1080), shot));
            F(o, "_hasSel", true);
            F(o, "_c", new PointF(200f, 150f));
            F(o, "_sz", new SizeF(200f, 100f));
            F(o, "_ang", 0f);
            if (tool != null) F(o, "_tool", AnnotKindValue(tool));
            return o;
        }

        // 造一张深色图、写一行"重点"，数一下图里近白像素有多少（用来判断有没有白底）
        static int TextWhitePixels(bool bg)
        {
            Bitmap shot = Solid(400, 300, Color.FromArgb(40, 40, 40));
            OverlayForm o = MakeOverlay(shot, "Text");
            F(o, "_textBg", bg);
            Mouse(o, "OnMouseDown", 150, 140);
            TextBox tb = null;
            foreach (Control c in o.Controls) { TextBox t = c as TextBox; if (t != null) { tb = t; break; } }
            if (tb == null) { o.Dispose(); return -1; }
            tb.Text = "重点";
            Call(o, "EndText", true);
            Call(o, "Confirm");
            Bitmap res = o.Result;
            int n = 0;
            if (res != null)
                for (int y = 0; y < res.Height; y++)
                    for (int x = 0; x < res.Width; x++)
                    {
                        Color c = res.GetPixel(x, y);
                        // 白底是 65% 不透明叠在深灰底上 → 实际约 180 灰，不是纯白，阈值别卡太死
                        if (c.R > 140 && c.G > 140 && c.B > 140) n++;
                    }
            o.Dispose();
            return n;
        }

        // 读任意对象的字段（Shape 是私有嵌套类，字段是 public）
        static object Field2(object o, string name)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + name);
            return fi.GetValue(o);
        }

        // 画一行字然后确认，数图里的红色像素（用来验证字号变化真的画出来了）
        static int TextRedPixels(float size)
        {
            Bitmap shot = Solid(400, 300, Color.White);
            OverlayForm o = MakeOverlay(shot, "Text");
            Call(o, "SetNextTextSize", size);
            Mouse(o, "OnMouseDown", 120, 130);
            TextBox tb = null;
            foreach (Control c in o.Controls) { TextBox t = c as TextBox; if (t != null) { tb = t; break; } }
            if (tb == null) { o.Dispose(); return -1; }
            tb.Text = "重点";
            Call(o, "EndText", true);
            Call(o, "Confirm");
            Bitmap res = o.Result;
            int n = RedPixels(res, new Rectangle(0, 0, res.Width, res.Height));
            o.Dispose();
            return n;
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            // 界面线程里的异常默认会弹一个"未处理的异常"模态框 —— 测试里没人去点它，整个套件就永远卡住。
            // 改成捕获并打印：卡死变成一条 FAIL，还顺带告诉我们是谁抛的。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object so, ThreadExceptionEventArgs se)
            {
                Console.WriteLine("  !! 界面线程抛异常（已拦下，避免弹框卡死）: {0}", se.Exception);
                fail++;
            };

            // 卡住超过 20 秒就报一次"现在卡在哪条"，方便定位
            Thread watchdog = new Thread(delegate()
            {
                while (!finished)
                {
                    Thread.Sleep(20000);
                    if (finished) break;
                    Console.WriteLine("  .. 还在跑：{0}（已 {1:0.0} 秒）", currentTest, (DateTime.Now - testStart).TotalSeconds);
                }
            });
            watchdog.IsBackground = true;
            watchdog.Start();

            tmp = Path.Combine(Path.GetTempPath(), "snapwheel_behavior_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            // 关键：测试不碰用户真实配置
            Settings.OverridePath = Path.Combine(tmp, "settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(tmp, "wheels.ini");
            Err.OverridePath = Path.Combine(tmp, "error.log");

            Console.WriteLine("--- 行为测试（临时目录 {0}）---", tmp);
            Console.WriteLine();

            // ================= 1. 设置存盘 -> 重新读回来 =================
            Run("设置改完存盘，重新读回来还是改后的值（万能键动作名不变的根因）", delegate
            {
                Settings a = new Settings();
                a.SaveToDisk = false;
                a.SetKeyAction(0, "delete");
                a.SetKeyAction(3, "clear");
                a.Save();                                  // 确定按钮的落盘
                Settings b = Settings.Load();              // 下次打开设置窗口读到的
                if (b.KeyActionAt(0) != "delete" || b.KeyActionAt(3) != "clear")
                    return "读回来是 " + b.KeyActionAt(0) + "," + b.KeyActionAt(1) + "," + b.KeyActionAt(2) + "," + b.KeyActionAt(3);
                if (b.KeyActionAt(1) != "next" || b.KeyActionAt(2) != "delete")
                    return "没动的分区被改坏了";
                return null;
            });

            // ================= 2. 删除真的落盘 =================
            Run("删除某张图：列表里没了，磁盘上的文件也真删了", delegate
            {
                Settings s = new Settings();
                s.SaveToDisk = false;
                WheelManager mgr = new WheelManager(s);
                Store st = mgr.ActiveStore;
                st.SaveToDisk = true;
                st.Dir = Path.Combine(tmp, "imgs");
                Directory.CreateDirectory(st.Dir);

                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel();
                Application.DoEvents();

                StoreItem it = st.Add(Solid(40, 30, Color.CornflowerBlue));
                if (it.FilePath == null || !File.Exists(it.FilePath)) return "发图时没落盘，测试前提不成立";

                f.RemoveItem(it, true);
                if (st.Items.Contains(it)) return "列表里还在";
                if (File.Exists(it.FilePath)) return "磁盘文件还在：" + Path.GetFileName(it.FilePath);
                return null;
            });

            // ================= 3. 删除动画走完 = 真删掉 =================
            Run("播放删除动画之后，真的删掉（不是只有动画）", delegate
            {
                Settings s = new Settings();
                s.SaveToDisk = false;
                WheelManager mgr = new WheelManager(s);
                Store st = mgr.ActiveStore;
                st.SaveToDisk = true;
                st.Dir = Path.Combine(tmp, "imgs3");
                Directory.CreateDirectory(st.Dir);

                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel();
                Application.DoEvents();

                StoreItem it = st.Add(Solid(50, 40, Color.Goldenrod));
                Call(f, "BeginDelete", it);
                if (!TickUntilDeleted(f, 300)) return "动画跑完了但没删掉";
                if (st.Items.Contains(it)) return "还在列表里";
                if (File.Exists(it.FilePath)) return "磁盘文件还在";
                return null;
            });

            // ================= 4. 连点删除：两张都要删掉 =================
            Run("连点删除两张：两张都真删掉（不是后者覆盖前者）", delegate
            {
                Settings s = new Settings();
                s.SaveToDisk = false;
                WheelManager mgr = new WheelManager(s);
                Store st = mgr.ActiveStore;
                st.SaveToDisk = true;
                st.Dir = Path.Combine(tmp, "imgs4");
                Directory.CreateDirectory(st.Dir);

                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel();
                Application.DoEvents();

                StoreItem one = st.Add(Solid(45, 35, Color.SeaGreen));
                StoreItem two = st.Add(Solid(45, 35, Color.Crimson));

                Call(f, "BeginDelete", one);
                Call(f, "BeginDelete", two);          // 连点：第一张必须当场落地，否则会被覆盖掉
                if (st.Items.Contains(one)) return "第一张还在列表里（被后一张覆盖了）";
                if (File.Exists(one.FilePath)) return "第一张的磁盘文件还在";
                if (!TickUntilDeleted(f, 300)) return "第二张动画没走完";
                if (st.Items.Contains(two)) return "第二张还在列表里";
                if (File.Exists(two.FilePath)) return "第二张的磁盘文件还在";
                if (st.Items.Count != 0) return "还剩 " + st.Items.Count + " 张";
                return null;
            });

            // ================= 5. 设置点确定之后，轮盘不能消失 =================
            Run("点设置里的确定之后，轮盘还在（展开/收起三种状态各试一次）", delegate
            {
                // A 展开着、把"收起态"从开改成关 —— 老代码就在这条路径上当场把轮盘藏了
                string r = SettingsApplyScene(true, false, false);
                if (r != null) return "A(展开着 + 收起态改成关)：" + r;
                // B 收起态下把"收起态"改成关 —— 必须自己展开，不能卡在只剩个把手
                r = SettingsApplyScene(true, true, false);
                if (r != null) return "B(收起态 + 收起态改成关)：" + r;
                // C 展开着、收起态设置没动 —— 也不该被顺手藏掉
                r = SettingsApplyScene(true, false, true);
                if (r != null) return "C(展开着 + 收起态保持开)：" + r;
                return null;
            });

            // ================= 6. 万能键四分区动作真的执行 =================
            Run("万能键动作真的执行（none 不动 / next 换盘 / delete 进确认）", delegate
            {
                Settings s = new Settings();
                s.SaveToDisk = false;
                s.SetKeyAction(0, "delete");
                s.SetKeyAction(1, "next");
                s.SetKeyAction(2, "none");
                s.SetKeyAction(3, "prev");
                WheelManager mgr = new WheelManager(s);
                mgr.New();                                  // 两个轮盘，next 才有得换
                mgr.Active = 0;
                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel();
                Application.DoEvents();

                Call(f, "DoKeyAction", 2);                  // none：什么都不该发生
                if (mgr.Active != 0) return "none 分区换盘了";
                if ((bool)G(f, "_delConfirm")) return "none 分区进了删除确认";

                Call(f, "DoKeyAction", 1);                  // next
                if (mgr.Active != 1) return "next 分区没换盘（active=" + mgr.Active + "）";

                Call(f, "DoKeyAction", 0);                  // delete -> 原地进左右两半确认
                if (!(bool)G(f, "_delConfirm")) return "delete 分区没进删除确认";
                return null;
            });

            // ================= 7. 圆盘上的字跟着设置变 =================
            // 取代原来那三处运行期诊断日志（KeyInit / KeySave / KeyLabels）。
            // 一条链全查：改一次 -> 落盘 -> 再打开设置读回来还是新的 -> 画标签读到的就是新动作
            Run("圆盘上的字跟着设置变（改一次 → 再打开设置还在 → 标签用新动作）", delegate
            {
                string[] want = { "clear", "prev", "folder", "collapse" };   // 四个方向都改，且互不相同
                Settings s = new Settings();
                s.SaveToDisk = false;
                for (int i = 0; i < 4; i++) s.SetKeyAction(i, want[i]);
                s.Save();                                  // 设置窗口点确定时做的事（含强制落盘）

                Settings again = Settings.Load();          // 再打开设置窗口，下拉里读到的
                for (int i = 0; i < 4; i++)
                    if (again.KeyActionAt(i) != want[i])
                        return "方向 " + i + " 读回来是 " + again.KeyActionAt(i) + "（应该是 " + want[i] + "）";

                // 画圆盘标签那一刻，txt = KeyActionShort(KeyActionAt(q))
                MethodInfo ks = typeof(WheelForm).GetMethod("KeyActionShort", BindingFlags.NonPublic | BindingFlags.Static);
                if (ks == null) return "找不到 KeyActionShort";
                Settings def = new Settings();             // 出厂默认，用来确认"确实变了、不是还画着旧的"
                for (int i = 0; i < 4; i++)
                {
                    string got = (string)ks.Invoke(null, new object[] { again.KeyActionAt(i) });
                    string old = (string)ks.Invoke(null, new object[] { def.KeyActionAt(i) });
                    if (string.IsNullOrEmpty(got)) return "方向 " + i + " 画出来是空的";
                    if (got == old) return "方向 " + i + " 画出来的还是旧字「" + old + "」";
                }
                string a0 = (string)ks.Invoke(null, new object[] { again.KeyActionAt(0) });
                string a2 = (string)ks.Invoke(null, new object[] { again.KeyActionAt(2) });
                if (a0 == a2) return "方向 0 和 2 画成了同一个字：" + a0;
                return null;
            });

            // ================= 8. 管理员模式下的拖放引导 =================
            Run("管理员拖不动：弹一次说明；普通权限不弹", delegate
            {
                Settings s = new Settings();
                s.SaveToDisk = false;
                WheelManager mgr = new WheelManager(s);
                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel();
                Application.DoEvents();

                int shown = 0;
                f.AdminHelpRequested += delegate(object o, EventArgs e2) { shown++; };

                // 普通权限：DoDragDrop 返回 None 只是"拖到了不收图的地方"，不该弹管理员说明
                Elev.ForceForTest = false;
                Call(f, "NotifyAdminDragBlocked");
                Application.DoEvents();                    // 说明是 BeginInvoke 抛出来的，要过一遍消息循环
                if (shown != 0) return "普通权限下也弹了说明";

                // 管理员：第一次拖不动要弹说明（这才是"用户容易懵"的那一刻）
                Elev.ForceForTest = true;
                Call(f, "NotifyAdminDragBlocked");
                Application.DoEvents();
                if (shown != 1) return "管理员第一次拖不动没弹说明（shown=" + shown + "）";

                // 同一次运行里不再反复弹，改成 toast
                Call(f, "NotifyAdminDragBlocked");
                Application.DoEvents();
                if (shown != 1) return "说明被重复弹了（shown=" + shown + "）";

                Elev.ForceForTest = null;                  // 还原，别影响后面的用例
                return null;
            });

            // ================= 9. 贴图（图钉）本身 =================
            Run("贴图窗口：尺寸跟图走、缩放夹在 10%~400%、不跑出屏幕、能关掉", delegate
            {
                Bitmap img = Solid(120, 60, Color.SteelBlue);
                PinForm p = Track(new PinForm(img, new Point(240, 200)));
                p.Show();
                Application.DoEvents();

                if (p.Width != 122 || p.Height != 62) return "初始尺寸不对：" + p.Width + "x" + p.Height + "（应为 122x62 = 120x60 + 1px 描边）";
                if (p.Zoom != 1f) return "初始缩放不是 1：" + p.Zoom;
                if (!p.TopMost) return "没有置顶（贴图必须压在最上面才有意义）";

                p.SetZoom(2f);
                if (p.Width != 242 || p.Height != 122) return "放大 2 倍后尺寸不对：" + p.Width + "x" + p.Height;
                p.SetZoom(100f);
                if (p.Zoom != PinForm.MaxZoom) return "没夹住最大缩放：" + p.Zoom;
                p.SetZoom(0.001f);
                if (p.Zoom != PinForm.MinZoom) return "没夹住最小缩放：" + p.Zoom;

                // 拖到屏幕外再缩放一次，必须被夹回可见范围
                p.Location = new Point(-99999, -99999);
                p.SetZoom(1.5f);
                Rectangle vs = SystemInformation.VirtualScreen;
                if (p.Left < vs.Left || p.Top < vs.Top || p.Right > vs.Right || p.Bottom > vs.Bottom)
                    return "缩放后跑到屏幕外了：" + p.Left + "," + p.Top;

                p.Close();
                if (!p.IsDisposed) return "关不掉";
                return null;
            });

            // ================= 10. 中键 -> 贴图 的整条链路 =================
            Run("中键点缩略图：真的把这张图交给贴图", delegate
            {
                Settings s = new Settings();
                s.SaveToDisk = false;
                WheelManager mgr = new WheelManager(s);
                Store st = mgr.ActiveStore;
                st.SaveToDisk = false;
                st.Add(Solid(80, 50, Color.Orange));
                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel();
                Application.DoEvents();

                float k = Convert.ToSingle(G(f, "UiK"));
                MethodInfo hit = typeof(WheelForm).GetMethod("HitTest", BindingFlags.NonPublic | BindingFlags.Instance);
                if (hit == null) return "找不到 HitTest";
                Point card = Point.Empty; bool found = false;
                for (int y = 0; y < f.Height && !found; y += 4)
                    for (int x = 0; x < f.Width; x += 4)
                        if ((int)hit.Invoke(f, new object[] { new Point((int)(x / k), (int)(y / k)) }) >= 0) { card = new Point(x, y); found = true; break; }
                if (!found) return "找不到卡片在窗口里的位置（前置条件不成立）";

                int pinned = 0; Bitmap handed = null;
                f.PinRequested += delegate(Bitmap b, Point at) { pinned++; handed = b; };
                MethodInfo md = typeof(WheelForm).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                md.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Middle, 1, card.X, card.Y, 0) });

                if (pinned != 1) return "中键没有触发贴图（pinned=" + pinned + "）";
                if (handed == null) return "没把图传出去";
                if (handed.Width != 80 || handed.Height != 50) return "传出去的图不对：" + handed.Width + "x" + handed.Height;
                return null;
            });

            // ================= 11. 标注：画上去 + 真的合成进图里 =================
            Run("标注箭头：确认后图里真的有箭头，别的像素没被动", delegate
            {
                Bitmap shot = Solid(400, 300, Color.White);
                OverlayForm o = MakeOverlay(shot, "Arrow");
                Mouse(o, "OnMouseDown", 120, 120);
                Mouse(o, "OnMouseMove", 280, 180);
                Mouse(o, "OnMouseUp", 280, 180);
                if (ShapeCount(o) != 1) return "没记下这个箭头（shapes=" + ShapeCount(o) + "）";

                Call(o, "Confirm");
                Bitmap res = o.Result;
                if (res == null) return "没有产出图片";
                if (res.Width != 200 || res.Height != 100) return "裁出来的尺寸不对：" + res.Width + "x" + res.Height;

                // 箭头在裁图里是 (20,20)->(180,80)
                int ink = RedPixels(res, new Rectangle(0, 0, res.Width, res.Height));
                if (ink < 30) return "图里找不到箭头像素（只有 " + ink + " 个红点）";
                if (RedPixels(res, new Rectangle(0, 0, 12, 12)) > 0) return "左上角本来该是白的，被画脏了";
                o.Dispose();
                return null;
            });

            Run("标注马赛克：确认后那一块真的糊了（方差大幅下降）", delegate
            {
                Bitmap shot = new Bitmap(400, 300, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(shot))
                    for (int y = 0; y < 300; y += 4)
                        for (int x = 0; x < 400; x += 4)
                            using (SolidBrush b = new SolidBrush(((x / 4 + y / 4) % 2 == 0) ? Color.Black : Color.White))
                                g.FillRectangle(b, x, y, 4, 4);

                OverlayForm o = MakeOverlay(shot, "Mosaic");
                Mouse(o, "OnMouseDown", 130, 130);
                Mouse(o, "OnMouseMove", 270, 170);
                Mouse(o, "OnMouseUp", 270, 170);
                // 先把"糊之前"量出来：Confirm 之后 _shot 会被浮层释放掉，那时再读就 ArgumentException
                double before = RegionVariance(shot, new Rectangle(140, 140, 120, 80));
                Call(o, "Confirm");
                Bitmap res = o.Result;
                if (res == null) return "没有产出图片";

                double after = RegionVariance(res, new Rectangle(40, 40, 120, 80));
                o.Dispose();
                if (!(after < before * 0.6)) return "没糊：马赛克前 " + before.ToString("0") + " -> 之后 " + after.ToString("0");
                return null;
            });

            Run("标注文字：输入框里打的字会落进图里", delegate
            {
                Bitmap shot = Solid(400, 300, Color.White);
                OverlayForm o = MakeOverlay(shot, "Text");
                Mouse(o, "OnMouseDown", 150, 140);

                TextBox tb = null;
                foreach (Control c in o.Controls) { TextBox t = c as TextBox; if (t != null) { tb = t; break; } }
                if (tb == null) return "点了没弹出输入框";
                tb.Text = "重点";
                Call(o, "EndText", true);
                if (ShapeCount(o) != 1) return "文字没被记下来";

                Call(o, "Confirm");
                Bitmap res = o.Result;
                if (res == null) return "没有产出图片";
                int ink = RedPixels(res, new Rectangle(0, 0, res.Width, res.Height));
                o.Dispose();
                if (ink < 10) return "图里找不到文字像素（" + ink + " 个）";
                return null;
            });

            Run("标注撤销：Ctrl+Z 只撤最后一个", delegate
            {
                Bitmap shot = Solid(400, 300, Color.White);
                OverlayForm o = MakeOverlay(shot, "Rect");
                Mouse(o, "OnMouseDown", 120, 120); Mouse(o, "OnMouseMove", 180, 160); Mouse(o, "OnMouseUp", 180, 160);
                Mouse(o, "OnMouseDown", 200, 120); Mouse(o, "OnMouseMove", 280, 190); Mouse(o, "OnMouseUp", 280, 190);
                if (ShapeCount(o) != 2) { o.Dispose(); return "两个方框没收全（" + ShapeCount(o) + "）"; }

                Call(o, "AnnotKey", new KeyEventArgs(Keys.Control | Keys.Z));
                int after = ShapeCount(o);
                o.Dispose();
                if (after != 1) return "撤销后剩 " + after + " 个（应为 1）";
                return null;
            });

            // ================= 15. 打字时按回车：只落字，别确认整张截图 =================
            Run("文字输入中按回车只落字（不会把截图确认掉）；再按一次才确认", delegate
            {
                Bitmap shot = Solid(400, 300, Color.White);
                OverlayForm o = MakeOverlay(shot, "Text");
                Mouse(o, "OnMouseDown", 160, 150);
                TextBox tb = null;
                foreach (Control c in o.Controls) { TextBox t = c as TextBox; if (t != null) { tb = t; break; } }
                if (tb == null) return "点了没弹出输入框";
                tb.Text = "改这里";

                Call(o, "OnKeyDown", new KeyEventArgs(Keys.Enter));      // 第一次：应该只落字
                if (ShapeCount(o) != 1) return "回车没把文字落下来（shapes=" + ShapeCount(o) + "）";
                if (o.Result != null) return "回车把整张截图确认掉了（就是用户踩到的那个 bug）";
                if (G(o, "_textBox") != null) return "输入框没收掉";

                Call(o, "OnKeyDown", new KeyEventArgs(Keys.Enter));      // 第二次：没有输入框了，确认
                if (o.Result == null) return "再按一次回车却没确认";
                o.Dispose();
                return null;
            });

            // ================= 16. 标注文字的白底开关 =================
            Run("标注文字：白底可开关（带底和不带底真的不一样）", delegate
            {
                int withBg = TextWhitePixels(true);
                int noBg = TextWhitePixels(false);
                if (withBg < 0 || noBg < 0) return "输入框没建起来";
                if (!(withBg > noBg * 1.5)) return "带底和不带底差不多（近白像素 " + withBg + " vs " + noBg + "）";
                return null;
            });

            // ================= 17. 超大图贴图先缩进屏幕 =================
            Run("贴图：比屏幕还大的图自动先缩小，不糊满整个桌面", delegate
            {
                Bitmap big = Solid(4000, 2400, Color.SteelBlue);
                PinForm p = Track(new PinForm(big, new Point(600, 400)));
                p.Show();
                Application.DoEvents();
                float z = p.Zoom;
                int w = p.Width, h = p.Height;
                p.Close();
                Rectangle vs = SystemInformation.VirtualScreen;
                if (z >= 1f) return "没有自动缩小（zoom=" + z.ToString("0.00") + "）";
                if (w > vs.Width || h > vs.Height) return "还是超出屏幕：" + w + "x" + h;
                return null;
            });

            // ================= 18. 新增的设置项要能存住 =================
            Run("提示开关 / 引导版本 / 文字底 存盘后读得回来（漏一行就会每次启动都弹提示）", delegate
            {
                Settings a = new Settings();
                a.AnnotHintDone = true; a.PinHintDone = true;
                a.GuideSeenVersion = "9.9.9"; a.TextBg = false;
                a.Save();
                Settings b = Settings.Load();
                if (!b.AnnotHintDone || !b.PinHintDone) return "提示开关没存住";
                if (b.GuideSeenVersion != "9.9.9") return "引导版本没存住：" + b.GuideSeenVersion;
                if (b.TextBg) return "文字底开关没存住";
                return null;
            });

            // ================= 19. 文字画完能拖动 =================
            Run("文字画完自动选中：能拖着挪到别的位置", delegate
            {
                Bitmap shot = Solid(400, 300, Color.White);
                OverlayForm o = MakeOverlay(shot, "Text");
                Call(o, "SetNextTextSize", 20f);
                Mouse(o, "OnMouseDown", 150, 140);
                TextBox tb = null;
                foreach (Control c in o.Controls) { TextBox t = c as TextBox; if (t != null) { tb = t; break; } }
                if (tb == null) { o.Dispose(); return "输入框没出来"; }
                tb.Text = "重点";
                Call(o, "EndText", true);

                object sel = G(o, "_sel");
                if (sel == null) { o.Dispose(); return "画完没有自动选中（那就拖不动）"; }
                PointF before = (PointF)Field2(sel, "A");

                Mouse(o, "OnMouseDown", (int)before.X + 6, (int)before.Y + 8);
                Mouse(o, "OnMouseMove", (int)before.X + 46, (int)before.Y + 33);
                Mouse(o, "OnMouseUp", (int)before.X + 46, (int)before.Y + 33);
                PointF after = (PointF)Field2(sel, "A");
                o.Dispose();
                if (Math.Abs(after.X - before.X - 40) > 2 || Math.Abs(after.Y - before.Y - 25) > 2)
                    return "没拖到位：（" + before.X + "," + before.Y + "）->（" + after.X + "," + after.Y + "）";
                return null;
            });

            // ================= 20. 字号：滚轮和 A+/A- 按钮 =================
            Run("文字改大小：滚轮和 A+ 按钮都能改，画出来真的更大", delegate
            {
                Bitmap shot = Solid(400, 300, Color.White);
                OverlayForm o = MakeOverlay(shot, "Text");
                Call(o, "SetNextTextSize", 20f);
                Mouse(o, "OnMouseDown", 150, 140);
                TextBox tb = null;
                foreach (Control c in o.Controls) { TextBox t = c as TextBox; if (t != null) { tb = t; break; } }
                if (tb == null) { o.Dispose(); return "输入框没出来"; }
                tb.Text = "重点";
                Call(o, "EndText", true);

                object sel = G(o, "_sel");
                if (sel == null) { o.Dispose(); return "画完没有自动选中"; }
                float s0 = (float)Field2(sel, "Size");

                Call(o, "AnnotWheel", new MouseEventArgs(MouseButtons.None, 0, 0, 0, 120));   // 滚轮往上
                float s1 = (float)Field2(sel, "Size");
                if (!(s1 > s0)) { o.Dispose(); return "滚轮没把字号改大（" + s0 + " -> " + s1 + "）"; }

                Call(o, "PlaceToolbar");
                Rectangle[] btns = (Rectangle[])G(o, "_toolBtns");
                if (btns.Length < 12) { o.Dispose(); return "工具条按钮数不对：" + btns.Length; }
                Rectangle plus = btns[12];                    // A+（0-5 工具、6-9 颜色、10 文字底、11 A-、12 A+、13 撤销）
                Mouse(o, "OnMouseDown", plus.X + plus.Width / 2, plus.Y + plus.Height / 2);
                float s2 = (float)Field2(sel, "Size");
                o.Dispose();
                if (!(s2 > s1)) return "A+ 按钮没生效（" + s1 + " -> " + s2 + "）";

                int small = TextRedPixels(20f);
                int big = TextRedPixels(40f);
                if (small < 0 || big < 0) return "输入框没建起来";
                if (!(big > small * 1.5)) return "放大后图里的字没明显变大（红像素 " + small + " -> " + big + "）";
                return null;
            });

            // ================= 21. 选中后 Del 删除 =================
            Run("选中文字按 Del：删掉这一个，不影响别的", delegate
            {
                Bitmap shot = Solid(400, 300, Color.White);
                OverlayForm o = MakeOverlay(shot, "Rect");
                Mouse(o, "OnMouseDown", 120, 120); Mouse(o, "OnMouseMove", 180, 170); Mouse(o, "OnMouseUp", 180, 170);
                Mouse(o, "OnMouseDown", 210, 120); Mouse(o, "OnMouseMove", 280, 190); Mouse(o, "OnMouseUp", 280, 190);
                if (ShapeCount(o) != 2) { o.Dispose(); return "两个方框没画上（" + ShapeCount(o) + "）"; }

                // 选择工具下点中第二个方框
                F(o, "_tool", AnnotKindValue("Select"));
                Mouse(o, "OnMouseDown", 240, 150);
                if (G(o, "_sel") == null) { o.Dispose(); return "点方框没有选中"; }
                Mouse(o, "OnMouseUp", 240, 150);

                Call(o, "AnnotKey", new KeyEventArgs(Keys.Delete));
                int left = ShapeCount(o);
                o.Dispose();
                if (left != 1) return "Del 之后剩 " + left + " 个（应为 1）";
                return null;
            });

            // ================= 22. 首次进截图界面的教程面板 =================
            Run("第一次进截图界面：教程面板真的画在画面中间，点一下才没", delegate
            {
                int W = 900, H = 620;
                Bitmap shot = Solid(W, H, Color.White);
                OverlayForm o = Track(new OverlayForm(new Rectangle(0, 0, W, H), shot));
                F(o, "_hasSel", true);
                F(o, "_c", new PointF(450f, 300f));
                F(o, "_sz", new SizeF(560f, 340f));
                F(o, "_ang", 0f);
                F(o, "_annotHint", true);
                Call(o, "PlaceToolbar");

                Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(b))
                    Call(o, "PaintOverlay", new PaintEventArgs(g, new Rectangle(0, 0, W, H)));
                Rectangle panel = (Rectangle)G(o, "_introRect");
                if (panel.Width < 150 || panel.Height < 120) { b.Dispose(); o.Dispose(); return "教程面板没画出来（rect=" + panel + "）"; }
                Color c = b.GetPixel(panel.Left + panel.Width / 2, panel.Top + panel.Height / 2);
                b.Dispose();
                if (c.R > 120 && c.G > 120 && c.B > 120) { o.Dispose(); return "面板位置还是浅色底，看不出画了面板"; }
                if (panel.Left < 0 || panel.Top < 0 || panel.Right > W || panel.Bottom > H) { o.Dispose(); return "面板跑到屏幕外了：" + panel; }

                Mouse(o, "OnMouseDown", panel.Left + panel.Width / 2, panel.Top + panel.Height / 2);
                bool gone = !(bool)G(o, "_annotHint");
                o.Dispose();
                if (!gone) return "点了一下教程面板还在";
                return null;
            });

            // ================= 23. 取字（OCR）=================
            Run("取字：中文逐字空格要拼回来，英文空格要留着", delegate
            {
                MethodInfo tighten = typeof(Ocr).GetMethod("TightenCjk", BindingFlags.NonPublic | BindingFlags.Static);
                if (tighten == null) return "找不到 TightenCjk";
                string a = (string)tighten.Invoke(null, new object[] { "本 周 报 告 已 发 出 ， 请 查 收 。" });
                if (a != "本周报告已发出，请查收。") return "中文没拼回去：「" + a + "」";
                string b = (string)tighten.Invoke(null, new object[] { "Deadline is Friday 5pm, please confirm." });
                if (b != "Deadline is Friday 5pm, please confirm.") return "英文空格被吃了：「" + b + "」";
                string c = (string)tighten.Invoke(null, new object[] { "交 付 时 间 ： 9 月 1 2 日 1 8 ： 00" });
                if (c != "交付时间：9月12日18：00") return "中英数混排没弄对：「" + c + "」";
                return null;
            });

            Run("取字：系统 OCR 真能把图上的字认出来（没装语言包就跳过）", delegate
            {
                if (!Ocr.Available)
                {
                    Console.WriteLine("       （这台机器没有 OCR 语言包，跳过：{0}）", Ocr.Why);
                    return null;
                }
                Bitmap img = new Bitmap(700, 200, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(img))
                {
                    g.Clear(Color.White);
                    using (Font f = new Font("Microsoft YaHei UI", 28f, FontStyle.Bold))
                    using (SolidBrush b = new SolidBrush(Color.Black))
                        g.DrawString("轮盘取字测试 OK 9527", f, b, 20, 50);
                }
                string err;
                string txt = Ocr.Recognize(img, out err);
                img.Dispose();
                if (txt == null) return "识别失败：" + err;
                if (txt.IndexOf("9527") < 0) return "没认出关键数字，结果是：" + txt;
                return null;
            });

            Run("取字按钮：没框选时点它直接返回，不弹窗卡住", delegate
            {
                Bitmap shot = Solid(400, 300, Color.White);
                OverlayForm o = Track(new OverlayForm(new Rectangle(0, 0, 400, 300), shot));
                F(o, "_hasSel", false);
                o.DoOcr();
                Call(o, "PlaceToolbar");
                Rectangle[] btns = (Rectangle[])G(o, "_toolBtns");
                o.Dispose();
                if (btns.Length != 14) return "工具条按钮数不对：" + btns.Length + "（应为 14 = 5 工具 + 4 颜色 + 文字底 + A- + A+ + 字 + 撤销）";
                return null;
            });

            // ================= 24. 拖出去必须同时给"图片"和"文件"两种格式 =================
            Run("拖出去的载荷：图片格式 + 文件格式都要有（少一个就有一半地方放不进去）", delegate
            {
                Settings s = new Settings();
                s.SaveToDisk = false;
                s.DragOutAsFile = true;
                WheelManager mgr = new WheelManager(s);
                Store st = mgr.ActiveStore;
                st.SaveToDisk = false;
                StoreItem it = st.Add(Solid(80, 50, Color.Orange));
                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel();
                Application.DoEvents();

                DataObject d = f.BuildDragData(it);
                bool hasBmp = d.GetDataPresent(DataFormats.Bitmap);
                bool hasFile = d.GetDataPresent(DataFormats.FileDrop);
                if (!hasBmp) { f.Dispose(); return "连图片格式都没有"; }
                if (!hasFile) { f.Dispose(); return "没有文件格式 —— 拖到资源管理器/桌面就会放不进去（v0.4.8 的回归）"; }

                // 设置里关掉之后才允许不给文件格式
                s.DragOutAsFile = false;
                DataObject d2 = f.BuildDragData(it);
                bool file2 = d2.GetDataPresent(DataFormats.FileDrop);
                bool bmp2 = d2.GetDataPresent(DataFormats.Bitmap);
                f.Dispose();
                if (file2) return "设置关掉了却还是给了文件格式";
                if (!bmp2) return "关掉文件格式后连图片格式也没了";
                return null;
            });

            // ================= 25. 老配置要迁移（用户就是这么中招的）=================
            Run("老配置迁移：Rev=2 且 DragOutAsFile=0 的配置，读出来要恢复成带文件格式", delegate
            {
                string p = Path.Combine(tmp, "settings_old.ini");
                File.WriteAllText(p,
                    "SaveToDisk=1" + Environment.NewLine +
                    "Dir=C:\\Users\\Maverick\\Pictures\\SnapWheel" + Environment.NewLine +
                    "DragOutAsFile=0" + Environment.NewLine +
                    "Rev=2" + Environment.NewLine, new System.Text.UTF8Encoding(false));
                string save = Settings.OverridePath;
                Settings.OverridePath = p;
                Settings loaded = Settings.Load();
                Settings.OverridePath = save;
                if (!loaded.DragOutAsFile) return "老配置没有被迁移回「带文件格式」，拖出去还是放不进去";
                if (loaded.Rev < 3) return "Rev 没升到 3（下次还会再迁移一遍）";
                // 迁移必须落到文件里，不能只在内存里生效（只改内存的话，下次启动又按旧值走一遍）
                string back = File.ReadAllText(p, System.Text.Encoding.UTF8);
                if (back.IndexOf("DragOutAsFile=1") < 0) return "迁移没落盘：文件里还是 DragOutAsFile=0";
                if (back.IndexOf("Rev=3") < 0) return "迁移没落盘：文件里还是 Rev=2";
                return null;
            });

            // ================= 26. 信息面板不能跑到副屏（陈年老 bug）=================
            Run("信息面板贴在「当前这块屏幕」的右上角，不会跑到副屏", delegate
            {
                int w = 400, h = 40;
                // 副屏在右边：虚拟屏幕 (0,0)-(3840,1080)，正在用的是主屏 (0,0)-(1920,1080)
                Rectangle vs = new Rectangle(0, 0, 3840, 1080);
                Rectangle scr = new Rectangle(0, 0, 1920, 1080);
                Rectangle r = OverlayForm.InfoPanelRect(vs, scr, w, h);
                int sx = vs.Left + r.Left, sy = vs.Top + r.Top;
                if (sx + w > scr.Right) return "面板跑到副屏去了：屏幕坐标 x=" + sx + "（主屏右边界 " + scr.Right + "）";
                if (sx < scr.Left) return "面板跑到主屏左外边了：x=" + sx;
                if (sy < scr.Top || sy + h > scr.Bottom) return "面板纵向超出主屏：y=" + sy;

                // 副屏在左边：虚拟屏幕 (-1920,0)-(1920,1080)
                Rectangle vs2 = new Rectangle(-1920, 0, 3840, 1080);
                Rectangle r2 = OverlayForm.InfoPanelRect(vs2, scr, w, h);
                int sx2 = vs2.Left + r2.Left;
                if (sx2 < scr.Left || sx2 + w > scr.Right) return "副屏在左边时也算错了：x=" + sx2 + "（主屏 " + scr.Left + ".." + scr.Right + "）";
                return null;
            });

            // ================= 27. 取字工具 + 翻译 =================
            Run("取字是个工具：能选中、拖框只画示意框（不会变成标注进图）", delegate
            {
                Bitmap shot = Solid(400, 300, Color.White);
                OverlayForm o = MakeOverlay(shot, "Select");
                // 快捷键 O 选中取字工具
                Call(o, "AnnotKey", new KeyEventArgs(Keys.O));
                if (!G(o, "_tool").ToString().Equals("Ocr")) { o.Dispose(); return "按 O 没选中取字工具：" + G(o, "_tool"); }
                Call(o, "PlaceToolbar");
                Rectangle[] btns = (Rectangle[])G(o, "_toolBtns");
                if (btns.Length != 14) { o.Dispose(); return "工具条按钮数不对：" + btns.Length; }

                Mouse(o, "OnMouseDown", 130, 130);
                object drawing = G(o, "_drawing");
                if (drawing == null) { o.Dispose(); return "拖框没起来"; }
                string kind = Field2(drawing, "Kind").ToString();
                if (kind != "Ocr") { o.Dispose(); return "拖出来的不是取字框，而是 " + kind; }
                F(o, "_drawing", null);        // 不触发 mouse up（那会真的去识别并弹窗）
                o.Dispose();
                return null;
            });

            Run("翻译：方向判定 + 接口返回解析（不联网也能测的部分）", delegate
            {
                if (!Translate.LooksChinese("本周报告已发出，请查收。")) return "中文没认出来";
                if (Translate.LooksChinese("Deadline is Friday 5pm.")) return "英文被当成中文了";
                if (Translate.TargetLabel("你好") != "英文") return "中文该翻成英文";
                if (Translate.TargetLabel("hello") != "中文") return "英文该翻成中文";
                // 抠字段 + 反转义（\u 中文、\" 引号、\n 换行）
                string json = "{\"responseData\":{\"translatedText\":\"\\u4f60\\u597d\\\"世界\\\"\\n第二行\"}}";
                string got = Translate.ExtractField(json, "translatedText");
                if (got != "你好\"世界\"\n第二行") return "解析不对：" + (got == null ? "(null)" : got.Replace("\n", "\\n"));
                return null;
            });

            Run("翻译：失败也要给出人话原因（限流 / 接口报错 / 空返回各不一样）", delegate
            {
                string err;

                // 正常
                string ok = Translate.ReadResult("{\"responseData\":{\"translatedText\":\" hello 世界 \"},\"responseStatus\":200}", out err);
                if (ok != "hello 世界") return "正常返回没解析对：" + (ok == null ? "null/" + err : ok);

                // 限流：MyMemory 把警告塞在 translatedText 里
                string q = Translate.ReadResult("{\"responseData\":{\"translatedText\":\"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY\"},\"responseStatus\":429}", out err);
                if (q != null) return "限流居然当成成功了：" + q;
                if (err == null || err.IndexOf("额度") < 0) return "限流没给出限流的提示：" + err;

                // 限流：另一种形态（warning 在 responseDetails 里）
                q = Translate.ReadResult("{\"responseData\":{\"translatedText\":\"\"},\"responseDetails\":\"QUERY LENGTH LIMIT EXCEEDED\",\"responseStatus\":403}", out err);
                if (q != null || err == null || err.IndexOf("额度") < 0) return "没认出 responseDetails 里的限流：" + err;

                // 别的错误状态：原因原样带出来
                q = Translate.ReadResult("{\"responseData\":null,\"responseDetails\":\"INVALID LANGUAGE PAIR\",\"responseStatus\":500}", out err);
                if (q != null) return "报错却返回了内容";
                if (err == null || err.IndexOf("INVALID LANGUAGE PAIR") < 0) return "把接口给的原因丢了：" + err;

                // 空内容：不算失败，返回空串（上层会当成"这段没内容"跳过）
                q = Translate.ReadResult("{\"responseData\":{\"translatedText\":\"\"},\"responseStatus\":200}", out err);
                if (q == null || q.Length != 0) return "空返回没当成空串：" + (q == null ? err : q);

                // 完全不是 JSON
                q = Translate.ReadResult("<html>502 Bad Gateway</html>", out err);
                if (q != null || string.IsNullOrEmpty(err)) return "垃圾返回没报错";
                return null;
            });

            Run("翻译：真的调一次免费接口（连不上就跳过，不算失败）", delegate
            {
                string err;
                string r = Translate.Run("hello world", out err);
                if (r == null)
                {
                    Console.WriteLine("       （跳过：{0}）", err);
                    return null;
                }
                if (r.IndexOf("世界") < 0) return "返回的译文不像中文：" + r;
                return null;
            });

            Run("取字：像素直通路径（省掉 PNG 来回）也能认出同样的字", delegate
            {
                if (!Ocr.Available) { Console.WriteLine("       （没有 OCR 语言包，跳过）"); return null; }
                Bitmap img = new Bitmap(640, 200, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(img))
                {
                    g.Clear(Color.White);
                    using (Font f = new Font("Microsoft YaHei UI", 26f, FontStyle.Bold))
                    using (SolidBrush b = new SolidBrush(Color.Black))
                        g.DrawString("像素直通 4321", f, b, 20, 50);
                }
                int w, h;
                byte[] px = Ocr.PixelsOf(img, out w, out h);
                img.Dispose();
                if (px == null || px.Length != w * h * 4) return "像素长度不对：" + (px == null ? "null" : px.Length.ToString());
                string err;
                string txt = Ocr.RecognizePixels(px, w, h, out err);
                if (txt == null) return "识别失败：" + err;
                if (txt.IndexOf("4321") < 0) return "没认出关键数字：" + txt;
                return null;
            });

            Run("取字准确率：14px 小字也要认得准（以前只有 25%）", delegate
            {
                if (!Ocr.Available) { Console.WriteLine("       （没有 OCR 语言包，跳过）"); return null; }
                string truth = "本周报告已发出，请查收。交付时间：9月12日18:00。负责同学：小何 13800008821";
                Bitmap img = new Bitmap(900, 260, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(img))
                {
                    g.Clear(Color.White);
                    using (Font f = new Font("Microsoft YaHei UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (SolidBrush b = new SolidBrush(Color.Black))
                    {
                        g.DrawString("本周报告已发出，请查收。", f, b, 12, 30);
                        g.DrawString("交付时间：9月12日18:00", f, b, 12, 60);
                        g.DrawString("负责同学：小何 13800008821", f, b, 12, 90);
                    }
                }
                string err;
                string txt = Ocr.Recognize(img, out err);
                img.Dispose();
                if (txt == null) return "识别失败：" + err;
                string t = truth.Replace(" ", "");
                string g2 = txt.Replace(" ", "").Replace("\r", "").Replace("\n", "");
                int hit = 0, gi = 0;
                for (int i = 0; i < t.Length; i++) { int k = g2.IndexOf(t[i], gi); if (k >= 0) { hit++; gi = k + 1; } }
                double acc = (double)hit / t.Length;
                if (acc < 0.8) return "小字准确率只有 " + (acc * 100).ToString("0") + "%（放大后应该 80%+）：" + txt;
                return null;
            });

            // ================= 34. 撤销删除（后悔药） =================
            Run("撤销删除：删掉的图能放回轮盘，文件也重新落盘", delegate
            {
                Undo.Clear();
                Settings s = new Settings();
                s.SaveToDisk = false;
                WheelManager mgr = new WheelManager(s);
                Store st = mgr.ActiveStore;
                st.SaveToDisk = true;
                st.Dir = Path.Combine(tmp, "undo_imgs1");
                Directory.CreateDirectory(st.Dir);
                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel(); Application.DoEvents();

                StoreItem it = st.Add(Solid(60, 40, Color.Orange));
                string orig = it.FilePath;
                if (orig == null || !File.Exists(orig)) { f.Dispose(); return "前置条件不成立：发图时没落盘"; }

                f.RemoveItem(it, true);
                if (st.Items.Count != 0) { f.Dispose(); return "删完轮盘里还有 " + st.Items.Count + " 张"; }
                if (File.Exists(orig)) { f.Dispose(); return "原文件没删掉（那下次启动又会被扫回来）"; }
                if (!Undo.CanUndo) { f.Dispose(); return "删完却说没有可撤销的"; }

                string wheel;
                int n = Undo.UndoLast(mgr, out wheel);
                int back = st.Items.Count;
                int w = back > 0 ? st.Items[0].Image.Width : 0;
                string newPath = back > 0 ? st.Items[0].FilePath : null;
                bool fileBack = newPath != null && File.Exists(newPath);
                f.Dispose();
                if (n != 1) return "撤回了 " + n + " 张（应该 1）";
                if (back != 1) return "撤回后轮盘里是 " + back + " 张";
                if (w != 60) return "放回来的图不对（宽 " + w + "，应该 60）";
                if (!fileBack) return "撤回后文件没重新落盘";
                if (Undo.CanUndo) return "撤回一次之后栈里还留着东西";
                return null;
            });

            Run("撤销删除：连删两张，一次撤一张，两张都能回来", delegate
            {
                Undo.Clear();
                Settings s = new Settings();
                s.SaveToDisk = false;
                WheelManager mgr = new WheelManager(s);
                Store st = mgr.ActiveStore;
                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel(); Application.DoEvents();

                StoreItem a = st.Add(Solid(70, 50, Color.Red));
                StoreItem b = st.Add(Solid(30, 30, Color.Blue));
                f.RemoveItem(a, true);
                f.RemoveItem(b, true);
                if (st.Items.Count != 0) { f.Dispose(); return "删完还剩 " + st.Items.Count + " 张"; }

                string wheel;
                int n1 = Undo.UndoLast(mgr, out wheel);      // 先回来的是后删的那张
                int afterFirst = st.Items.Count;
                int w1 = afterFirst > 0 ? st.Items[0].Image.Width : 0;
                int n2 = Undo.UndoLast(mgr, out wheel);
                int afterSecond = st.Items.Count;
                bool canMore = Undo.CanUndo;
                f.Dispose();
                if (n1 != 1 || n2 != 1) return "每次应该各撤回 1 张，实际 " + n1 + " / " + n2;
                if (afterFirst != 1) return "第一次撤回后是 " + afterFirst + " 张";
                if (w1 != 30) return "第一次撤回来的不对（宽 " + w1 + "，应该是后删的 30）";
                if (afterSecond != 2) return "第二次撤回后是 " + afterSecond + " 张";
                if (canMore) return "两次都撤完了还能继续撤";
                return null;
            });

            Run("撤销删除：整盘清空也能一次全撤回（张数和内容都对）", delegate
            {
                Undo.Clear();
                Settings s = new Settings();
                s.SaveToDisk = false;
                WheelManager mgr = new WheelManager(s);
                Store st = mgr.ActiveStore;
                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel(); Application.DoEvents();

                st.Add(Solid(40, 40, Color.Red));
                st.Add(Solid(50, 50, Color.Green));
                st.Add(Solid(60, 60, Color.Blue));
                f.ClearCurrentWheel();
                if (st.Items.Count != 0) { f.Dispose(); return "清空后还剩 " + st.Items.Count + " 张"; }
                if (!Undo.CanUndo) { f.Dispose(); return "清空后没有可撤销的"; }

                string wheel;
                int n = Undo.UndoLast(mgr, out wheel);
                int back = st.Items.Count;
                bool has40 = false, has50 = false, has60 = false;
                for (int i = 0; i < back; i++)
                {
                    int w = st.Items[i].Image.Width;
                    if (w == 40) has40 = true; else if (w == 50) has50 = true; else if (w == 60) has60 = true;
                }
                f.Dispose();
                if (n != 3) return "撤回了 " + n + " 张（应该 3）";
                if (back != 3) return "撤回后轮盘里是 " + back + " 张";
                if (!has40 || !has50 || !has60) return "放回来的内容不对（40/50/60 三张要都在）";
                return null;
            });

            Run("撤销删除：没删过时撤回是空操作；删太多次只留最近几批", delegate
            {
                Undo.Clear();
                Settings s = new Settings();
                s.SaveToDisk = false;
                WheelManager mgr = new WheelManager(s);
                Store st = mgr.ActiveStore;
                WheelForm f = NewWheel(mgr, s);
                f.ShowWheel(); Application.DoEvents();

                string wheel;
                if (Undo.CanUndo) { f.Dispose(); return "刚清空还说能撤销"; }
                if (Undo.UndoLast(mgr, out wheel) != 0) { f.Dispose(); return "空栈撤回居然返回了东西"; }
                if (st.Items.Count != 0) { f.Dispose(); return "空撤回把轮盘弄脏了"; }

                // 删 12 次：栈必须有上限，否则删图删得越多，内存里堆得越多
                for (int i = 0; i < 12; i++)
                {
                    StoreItem it = st.Add(Solid(20, 20, Color.Gray));
                    f.RemoveItem(it, true);
                }
                int batches = Undo.BatchCount;
                int items = Undo.ItemCount;
                f.Dispose();
                if (batches > Undo.MaxBatches) return "批次没被裁到上限：留着 " + batches + " 批（上限 " + Undo.MaxBatches + "）";
                if (batches < 2) return "只留了 " + batches + " 批，裁太狠了";
                if (items > batches) return "每批张数记错了：" + batches + " 批却有 " + items + " 张";
                return null;
            });


            // ================= 38. 工具条绝不压住选区 =================
            Run("工具条不压截图：优先在选区外，上下没地方就竖排到左右，实在没地方才压住且更透", delegate
            {
                Rectangle scr = new Rectangle(0, 0, 1920, 1080);
                int n = 14, bw = 46, bh = 40, gp = 6;    // 跟真实工具条同量级
                bool vertical, overlap;

                // (1) 普通选区：应当在选区下方，且完全不接触选区
                RectangleF sel1 = new RectangleF(400, 300, 600, 300);
                Rectangle r1 = OverlayForm.ToolbarRect(scr, sel1, n, bw, bh, gp, gp, Rectangle.Empty, out vertical, out overlap);
                if (r1.IntersectsWith(Rectangle.Round(sel1))) return "普通选区：工具条压在选区上了 " + r1;
                if (vertical) return "普通选区：下面明明有地方却竖排了";
                if (overlap) return "普通选区：不该标记成压住";
                if (r1.Top < sel1.Bottom) return "普通选区：没有摆在选区下方";

                // (2) 贴着屏幕下边的选区：应当翻到选区上方
                RectangleF sel2 = new RectangleF(400, 700, 600, 380);
                Rectangle r2 = OverlayForm.ToolbarRect(scr, sel2, n, bw, bh, gp, gp, Rectangle.Empty, out vertical, out overlap);
                if (r2.IntersectsWith(Rectangle.Round(sel2))) return "贴底选区：工具条压在选区上了 " + r2;
                if (r2.Bottom > sel2.Top) return "贴底选区：没有翻到选区上方";

                // (3) 竖着截一整条（上下都没地方）：必须竖排贴到选区左右，仍然不接触
                RectangleF sel3 = new RectangleF(600, 0, 700, 1080);
                Rectangle r3 = OverlayForm.ToolbarRect(scr, sel3, n, bw, bh, gp, gp, Rectangle.Empty, out vertical, out overlap);
                if (r3.IntersectsWith(Rectangle.Round(sel3))) return "整条选区：工具条还是压在选区上了 " + r3;
                if (!vertical) return "整条选区：左右还有地方，应该竖排（横向排在屏幕里塞不下就该换方向）";

                // (4) 选区铺满整屏：没地方也只能压住，但要标记出来（好让它画得更透）并且别出屏
                RectangleF sel4 = new RectangleF(0, 0, 1920, 1080);
                Rectangle r4 = OverlayForm.ToolbarRect(scr, sel4, n, bw, bh, gp, gp, Rectangle.Empty, out vertical, out overlap);
                if (!overlap) return "铺满整屏：应该标记成压住（画的时候要更透）";
                if (r4.Left < scr.Left || r4.Right > scr.Right || r4.Top < scr.Top || r4.Bottom > scr.Bottom)
                    return "铺满整屏：工具条跑到屏幕外面了 " + r4;

                // (5) 高 DPI 小屏：按钮尺寸翻倍后竖排会比屏幕还高 —— 收紧间距也得塞进屏幕，且仍不压选区
                int bw2 = 69, bh2 = 60;                  // 相当于 1080p 开 150%
                RectangleF sel5 = new RectangleF(200, 0, 900, 1080);
                Rectangle r5 = OverlayForm.ToolbarRect(scr, sel5, n, bw2, bh2, 3, 3, Rectangle.Empty, out vertical, out overlap);
                if (r5.Top < scr.Top || r5.Bottom > scr.Bottom) return "紧凑竖排还是比屏幕高：" + r5;
                if (r5.IntersectsWith(Rectangle.Round(sel5))) return "紧凑竖排压住选区了：" + r5;
                return null;
            });

            Run("拖框选的时候工具条先不显示（不然它追着鼠标、正好挡在你要选的地方）", delegate
            {
                Bitmap shot = Solid(800, 600, Color.White);
                OverlayForm o = Track(new OverlayForm(new Rectangle(0, 0, 800, 600), shot));
                F(o, "_hasSel", true);
                F(o, "_c", new PointF(400f, 300f));
                F(o, "_sz", new SizeF(400f, 300f));
                F(o, "_dragging", false);
                bool idle = (bool)Call(o, "ToolbarVisible");
                F(o, "_dragging", true);
                bool dragging = (bool)Call(o, "ToolbarVisible");
                o.Dispose();
                if (!idle) return "没在拖的时候工具条也不见了";
                if (dragging) return "拖框选的时候工具条还在显示（会挡住正在选的区域）";
                return null;
            });

            // ================= 40. 出错日志不会无限长大 =================
            Run("出错日志超过上限就转存成 error.log.1（挂久了也不会涨成几 MB）", delegate
            {
                string d = Path.Combine(tmp, "logrot");
                Directory.CreateDirectory(d);
                string p = Path.Combine(d, "error.log");
                string oldPath = Err.OverridePath;
                long oldMax = Err.MaxBytes;
                bool rotated, hasLog;
                long len;
                try
                {
                    Err.OverridePath = p;
                    Err.MaxBytes = 400;                 // 人为压低，几条就能撑满
                    Err.Log("测试日志", new Exception("第一条"));
                    hasLog = File.Exists(p);
                    for (int i = 0; i < 8; i++) Err.Log("测试日志", new Exception("第 " + i + " 条，把日志撑大"));
                    rotated = File.Exists(p + ".1");
                    len = File.Exists(p) ? new FileInfo(p).Length : 0;
                }
                finally { Err.OverridePath = oldPath; Err.MaxBytes = oldMax; }
                if (!hasLog) return "一条都没写进去";
                if (!rotated) return "超过上限了却没有转存（日志会一直涨）";
                if (len > 400 * 3) return "转存之后新日志还是太大：" + len + " 字节";
                return null;
            });

            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            finished = true;

            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
