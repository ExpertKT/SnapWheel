using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace SnapWheel
{
    // 环的目录名是按**显示名**生成的（`WheelManager.ApplySettings`：Dir = 根目录 \ SafeName(名字)）。
    // 而显示名可以重复。所以「两个环同名」不是理论问题，它有两条真实路径：
    //   · 改名窗口（`70-WheelsForm.cs` 的 RenameForm）根本不查重；
    //   · 新建环原来是 `Wheels.Count + 1` 命名 —— 三个环 项目1/项目2/项目3，
    //     删掉中间那个 项目2 之后 count=2，下一个又叫 项目3。
    //
    // 撞名的后果是**丢数据**：两个环共用一个目录，而 Remove() 原来直接
    // `Directory.Delete(dir, true)` 递归删 —— 会把**另一个环**的图一起删掉。
    // 这条闸绕过了 `Store.Owned`（"只删我们自己写的文件"），是最危险的一类。
    //
    // 这个套件钉的就是这两件事：名字不重复、以及就算同名也绝不连带删别人的文件。
    static class RingDirTest
    {
        static int pass, fail;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + "   " + detail); }
        }

        static void Main()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "swringdir_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                Names(tmp);
                Collision(tmp);
            }
            catch (Exception ex) { Check("没有异常跑完", false, ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(tmp, true); } catch { } }
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        static Settings Make(string tmp, string sub)
        {
            Settings s = Settings.Load();
            s.Dir = Path.Combine(tmp, sub);
            s.SaveToDisk = true;
            WheelManager.OverrideMetaPath = Path.Combine(tmp, sub + "-wheels.ini");
            return s;
        }

        static int Distinct(List<Wheel> ws)
        {
            List<string> seen = new List<string>();
            foreach (Wheel w in ws) if (!seen.Contains(w.Name)) seen.Add(w.Name);
            return seen.Count;
        }

        static bool HasName(List<Wheel> ws, string n)
        {
            foreach (Wheel w in ws) if (w.Name == n) return true;
            return false;
        }

        // ---- 1. 默认命名不能重复：删掉中间那个再新建，必须拿回空出来的那个名字
        static void Names(string tmp)
        {
            WheelManager m = new WheelManager(Make(tmp, "n"));
            if (m.Wheels.Count == 0) m.New();
            string n1 = m.Wheels[0].Name;
            Wheel w2 = m.New();
            Wheel w3 = m.New();
            Check("三个环的默认名字互不相同", Distinct(m.Wheels) == 3,
                  "名字：" + m.Wheels[0].Name + " / " + w2.Name + " / " + w3.Name);

            int mid = m.Wheels.IndexOf(w2);
            Check("找到中间那个环", mid >= 0, "IndexOf 返回 " + mid);
            if (mid < 0) return;
            m.Remove(mid);
            Check("删掉中间那个之后还剩两个环", m.Wheels.Count == 2, "Count=" + m.Wheels.Count);

            Wheel w4 = m.New();
            Check("新建的名字跟任何还在的环都不撞（原来是 Wheels.Count+1，会重新叫出被删掉的那个名字）",
                  Distinct(m.Wheels) == m.Wheels.Count,
                  "现在：" + string.Join(" / ", Names(m.Wheels)));
            Check("拿回的正是空出来的那个名字", w4.Name == w2.Name, w4.Name + " != " + w2.Name);
            Check("原来的第一个环名字没被动过", m.Wheels[0].Name == n1, m.Wheels[0].Name + " != " + n1);
        }

        static string[] Names(List<Wheel> ws)
        {
            string[] a = new string[ws.Count];
            for (int i = 0; i < ws.Count; i++) a[i] = ws[i].Name;
            return a;
        }

        // ---- 2. 就算真的同名（共用一个目录），删一个也绝不能删到另一个的文件
        static void Collision(string tmp)
        {
            WheelManager m = new WheelManager(Make(tmp, "c"));
            if (m.Wheels.Count == 0) m.New();
            Wheel a = m.Wheels[0];
            Wheel b = m.New();
            // 用户在改名窗口里把两个环改成同一个名字 —— 改名窗口不查重，这条路走得通
            a.Name = "同名环";
            b.Name = "同名环";
            m.ApplySettings();

            Check("两个环同名时，目录确实是同一个（这就是那个前提）",
                  a.Store.Dir == b.Store.Dir && a.Store.Dir.Length > 0,
                  "[" + a.Store.Dir + "] vs [" + b.Store.Dir + "]");
            if (a.Store.Dir != b.Store.Dir) return;

            a.Store.Add(new Bitmap(8, 8));
            b.Store.Add(new Bitmap(8, 8));
            Check("两个环各有一格图", a.Store.Items.Count == 1 && b.Store.Items.Count == 1,
                  a.Store.Items.Count + " / " + b.Store.Items.Count);
            if (a.Store.Items.Count == 0 || b.Store.Items.Count == 0) return;
            string fa = a.Store.Items[0].FilePath;
            string fb = b.Store.Items[0].FilePath;
            Check("两张图各自落盘（不同文件）", fa != fb && File.Exists(fa) && File.Exists(fb),
                  fa + " / " + fb);

            m.Remove(0);   // 删掉 a
            Check("删掉同名环之一之后，另一个环的图还在盘上（不再被连带递归删掉）",
                  File.Exists(fb), "没了：" + fb);
            Check("被删掉的那个环，自己的图照常清掉了", !File.Exists(fa), "还在：" + fa);
            Check("目录没被整个删掉（另一个环还要用）",
                  Directory.Exists(a.Store.Dir), "目录没了：" + a.Store.Dir);
        }
    }
}
