// redo-test.cs -- 标注的「重做」。
//
// 原来只有撤销、没有重做 —— 撤多了只能重画。规则和所有编辑器一样：
//   · 撤销把图元移进重做栈；
//   · 重做把它拿回来；
//   · **一旦提交了新图元，重做栈就清空**（否则"撤销 → 画新的 → 重做"会把一条作废的旧线贴回来）。
//
// 还有一个容易踩的坑也钉在这里：撤销时**不能**释放马赛克的 Cache 位图 ——
// 那是算好的马赛克结果，释放了重做出来就是一片空白。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.RedoTest /out:%TEMP%\redo.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\redo-test.cs
using System;
using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class RedoTest
    {
        static int pass, fail;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + "   " + detail); }
        }

        static object Field(object o, string n)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            return fi.GetValue(o);
        }

        static void Call(object o, string n)
        {
            MethodInfo m = o.GetType().GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) throw new Exception("找不到方法 " + n);
            m.Invoke(o, null);
        }

        static void Call1(object o, string n, object arg)
        {
            MethodInfo m = o.GetType().GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) throw new Exception("找不到方法 " + n);
            m.Invoke(o, new object[] { arg });
        }

        static Type ShapeType()
        {
            Type t = typeof(OverlayForm).GetNestedType("Shape", BindingFlags.NonPublic);
            if (t == null) throw new Exception("找不到嵌套类型 Shape");
            return t;
        }

        static object MakeShape(string tag)
        {
            Type t = ShapeType();
            object s = Activator.CreateInstance(t);
            t.GetField("Text").SetValue(s, tag);
            return s;
        }

        static string TextOf(object shape)
        {
            return (string)ShapeType().GetField("Text").GetValue(shape);
        }

        static int Shapes(object ov) { return ((IList)Field(ov, "_shapes")).Count; }
        static int Redos(object ov) { return ((IList)Field(ov, "_redo")).Count; }

        static string Snapshot(object ov)
        {
            IList l = (IList)Field(ov, "_shapes");
            string r = "";
            for (int i = 0; i < l.Count; i++) r += TextOf(l[i]);
            return r;
        }

        static void Step(object ov, string what, int wantShapes, int wantRedo)
        {
            Check(string.Format("{0} → 图元 {1} / 重做栈 {2}", what, wantShapes, wantRedo),
                  Shapes(ov) == wantShapes && Redos(ov) == wantRedo,
                  string.Format("实际 图元 {0} / 重做栈 {1}", Shapes(ov), Redos(ov)));
        }

        [STAThread]
        static void Main()
        {
            try { Run(); }
            catch (Exception ex)
            {
                Console.WriteLine("出错：" + ex.GetType().Name + "  " + ex.Message);
                if (ex.InnerException != null) Console.WriteLine("  内层：" + ex.InnerException.Message);
                Console.WriteLine("通过 {0} / 失败 {1}", pass, fail + 1);
                Environment.ExitCode = 1;
            }
        }

        static void Run()
        {
            try { Lang.Init("zh"); } catch { }

            Console.WriteLine("=== 标注：撤销 / 重做 ===\n");

            using (Bitmap shot = new Bitmap(1200, 800))
            {
                using (Graphics g = Graphics.FromImage(shot)) g.Clear(Color.SteelBlue);
                OverlayForm ov = new OverlayForm(new Rectangle(0, 0, 1200, 800), shot);

                // 提交三个
                Call1(ov, "Commit", MakeShape("A"));
                Call1(ov, "Commit", MakeShape("B"));
                Call1(ov, "Commit", MakeShape("C"));
                Step(ov, "提交 A B C", 3, 0);
                Check("顺序是 ABC", Snapshot(ov) == "ABC", "实际 " + Snapshot(ov));

                // 撤销两次
                Call(ov, "Undo");
                Call(ov, "Undo");
                Step(ov, "撤销两次", 1, 2);
                Check("撤销后剩 A", Snapshot(ov) == "A", "实际 " + Snapshot(ov));

                // 重做两次 —— 拿回来的必须还是原来那两个，顺序也要对
                Call(ov, "Redo");
                Check("重做一次回到 AB", Snapshot(ov) == "AB", "实际 " + Snapshot(ov));
                Call(ov, "Redo");
                Step(ov, "重做两次", 3, 0);
                Check("重做两次回到 ABC（顺序没乱）", Snapshot(ov) == "ABC", "实际 " + Snapshot(ov));

                // 栈空了再重做：不能出错、也不能凭空多出东西
                Call(ov, "Redo");
                Step(ov, "栈空时再重做", 3, 0);

                // 撤销之后画新的：重做链必须断掉
                Call(ov, "Undo");                       // 剩 AB，重做栈 1（里面是 C）
                Call1(ov, "Commit", MakeShape("D"));    // 提交 D
                Step(ov, "撤销后提交新图元 D", 3, 0);
                Check("新图元接在后面，是 ABD", Snapshot(ov) == "ABD", "实际 " + Snapshot(ov));
                Call(ov, "Redo");
                Step(ov, "此时重做应当无效（C 不该被贴回来）", 3, 0);
                Check("C 没有被复活", Snapshot(ov) == "ABD", "实际 " + Snapshot(ov));

                // 撤销时不能把马赛克的 Cache 位图释放掉，否则重做出来是一片空白
                {
                    object mk = MakeShape("M");
                    Bitmap cache = new Bitmap(40, 30);
                    using (Graphics g = Graphics.FromImage(cache)) g.Clear(Color.Red);
                    ShapeType().GetField("Cache").SetValue(mk, cache);
                    Call1(ov, "Commit", mk);
                    int before = Shapes(ov);

                    Call(ov, "Undo");
                    IList redoList = (IList)Field(ov, "_redo");
                    object inRedo = redoList[redoList.Count - 1];
                    Bitmap cached = (Bitmap)ShapeType().GetField("Cache").GetValue(inRedo);
                    bool alive = false;
                    try { cached.GetPixel(1, 1); alive = true; } catch { }
                    Check("撤销时没有释放马赛克缓存（否则重做是空白）",
                          alive && ReferenceEquals(cached, cache), "缓存被释放了或换了一张");

                    Call(ov, "Redo");
                    Step(ov, "重做马赛克", before, 0);
                    object last = ((IList)Field(ov, "_shapes"))[before - 1];
                    Check("重做拿回的就是同一个 Cache 对象",
                          ReferenceEquals(ShapeType().GetField("Cache").GetValue(last), cache),
                          "换成了别的位图");

                    // 反向：清重做栈时**应该**释放（不然每撤一次就漏一张位图）
                    Call(ov, "Undo");
                    Call1(ov, "Commit", MakeShape("N"));     // 这一步会 ClearRedo
                    bool freed = false;
                    try { cache.GetPixel(1, 1); } catch { freed = true; }
                    Check("提交新图元清重做栈时，缓存被释放了（不漏内存）", freed,
                          "缓存还活着，说明清栈时没释放");
                }

                ov.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
