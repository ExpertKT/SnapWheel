// usage-test.cs -- 本地使用统计：默认关、开了才写、而且**只记事件不记内容**。
//
// 这东西是拿来看"我到底在用它做什么"的，所以它自己必须先可信：
//   · 默认必须是关的（不然就是偷偷记录）；
//   · 关着的时候一个字节都不该写；
//   · 打开之后每调一次记一行，格式固定，机器能解析；
//   · **不能带内容** —— 不写窗口标题、不写文件名、不写图上的字。
//
// 最后一条只能靠"我们只传了这些参数"来保证，所以顺带断言一下写出来的行里
// 没有换行/制表符混进来把格式搞乱。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.UsageTest /out:%TEMP%\usage.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\usage-test.cs
using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class UsageTest
    {
        static int pass, fail;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + "   " + detail); }
        }

        static string[] Lines(string p)
        {
            if (!File.Exists(p)) return new string[0];
            return File.ReadAllLines(p, System.Text.Encoding.UTF8);
        }

        [STAThread]
        static void Main()
        {
            try { Run(); }
            catch (Exception ex)
            {
                Console.WriteLine("出错：" + ex.GetType().Name + "  " + ex.Message);
                Console.WriteLine("通过 {0} / 失败 {1}", pass, fail + 1);
                Environment.ExitCode = 1;
            }
        }

        static void Run()
        {
            Console.WriteLine("=== 本地使用统计 ===\n");

            // 默认值：必须关
            {
                Settings s = new Settings();
                Check("设置里默认是**关**的（不能偷偷记录）", !s.UsageLog, "默认是开的");
            }

            string p = Path.Combine(Path.GetTempPath(), "snapwheel-usage-test.tsv");
            if (File.Exists(p)) File.Delete(p);
            Usage.OverridePath = p;

            // 关着的时候一个字节都不写
            Usage.On = false;
            Usage.Ev("Shot", "标注=2");
            Usage.Ev("DragOut");
            Check("关着的时候**一个字节都不写**", !File.Exists(p), "关着也写文件了");

            // 打开之后正常记
            Usage.On = true;
            Usage.Ev("Shot", "标注=2 Arrow=2");
            Usage.Ev("RingItems", "1");
            Usage.Ev("LongShot.Start", "800x600");

            string[] l = Lines(p);
            int data = 0;
            for (int i = 0; i < l.Length; i++) if (l[i].Length > 0 && l[i][0] != '#') data++;

            Check("打开之后记了 3 条（不含注释行）", data == 3, "实际 " + data);

            // 格式：时间 \t 事件 \t 细节
            string row = "";
            for (int i = 0; i < l.Length; i++) if (l[i].Length > 0 && l[i][0] != '#') { row = l[i]; break; }
            string[] f = row.Split('\t');
            Check("每行是 3 列：时间 / 事件 / 细节", f.Length == 3, "实际 " + f.Length + " 列：" + row);
            Check("第一列是时间戳", f.Length > 0 && f[0].Length >= 19 && f[0][4] == '-', "第一列是 " + (f.Length > 0 ? f[0] : ""));
            Check("第二列是事件名", f.Length > 1 && f[1] == "Shot", "第二列是 " + (f.Length > 1 ? f[1] : ""));

            // 细节里塞了换行/制表符也不能把格式搞乱
            Usage.Ev("Tricky", "a\tb\nc");
            string[] l2 = Lines(p);
            int data2 = 0;
            for (int i = 0; i < l2.Length; i++) if (l2[i].Length > 0 && l2[i][0] != '#') data2++;
            bool threeCols = true;
            for (int i = 0; i < l2.Length; i++)
                if (l2[i].Length > 0 && l2[i][0] != '#')
                    if (l2[i].Split('\t').Length != 3) threeCols = false;
            Check("细节里的制表符/换行被清掉，不会把一行拆成两行", data2 == 4 && threeCols,
                  "行数 " + data2 + "，列数一致=" + threeCols);

            // 文件头的说明必须写清楚"只在本机"
            bool header = false;
            for (int i = 0; i < l2.Length; i++) if (l2[i].IndexOf("不上传") >= 0) header = true;
            Check("文件头写清楚「只在本机、不上传」", header, "没找到说明");

            Usage.On = false;
            Usage.OverridePath = null;
            try { File.Delete(p); } catch { }

            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
