// 行为测试：测「做完之后真的发生了什么」，跟绘制测试（render-smoke）互补。
//
// 为什么单开一个：v0.4.8 那轮修掉的四个 bug（删除不落盘、连点删除两张都删不掉、
// 点设置确定轮盘消失、万能键动作名改完不变）全都是"编译过、150 项绘制断言全绿、
// 但行为是错的"。这个文件专门盯这几条行为，防止改回去。
//
// 编译（build.ps1 -Test 会自动跑）：
//   csc /nologo /target:exe /main:SnapWheel.BehaviorTest /out:btest.exe SnapWheel.cs tests\behavior-test.cs
//
// 安全：全程把设置文件和轮盘清单指到临时目录（Settings.OverridePath /
// WheelManager.OverrideMetaPath），绝不碰用户真实的 %APPDATA%\SnapWheel。
using System;
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

        static void Check(string what, bool ok, string detail)
        {
            Console.WriteLine("  {0} {1}{2}", ok ? "OK  " : "FAIL", what,
                string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）");
            if (ok) pass++; else fail++;
        }

        static void Run(string what, Func<string> test)
        {
            try
            {
                string detail = test();
                Check(what, detail == null, detail);
            }
            catch (Exception ex)
            {
                Exception real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Check(what, false, real.GetType().Name + ": " + real.Message);
            }
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
            WheelForm f = new WheelForm(mgr, s);
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

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();

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

                WheelForm f = new WheelForm(mgr, s);
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

                WheelForm f = new WheelForm(mgr, s);
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

                WheelForm f = new WheelForm(mgr, s);
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
                WheelForm f = new WheelForm(mgr, s);
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

            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);

            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
