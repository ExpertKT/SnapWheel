// edge-anchor-test.cs -- 「轮盘靠边方式」（0.9.11 新增设置 EdgeAnchor）。
//
// 背景：Windows 的"工作区"**总是**扣掉任务栏那一条，任务栏自动隐藏时也照样预留
// （本机实测 Bounds 1707×1067、WorkingArea 1707×1019 —— 底下那 48px 明明看不见却就是不给）。
// 于是自动隐藏任务栏的用户看到的轮盘底下悬着一条缝。这个设置就是让用户自己选贴哪条边。
//
// 这里能测的只有**判断本身**（AnchorIsScreen 是纯函数），因为真实的任务栏状态测不了：
//   · 三档 ×（工作区被扣 / 没被扣）×（自动隐藏 / 没自动隐藏）= 六种组合，逐一断言；
//   · "auto" 的默认值不能变（老用户升级后行为不许变）；
//   · 值要能从配置文件原样读写回来，**而且非法值不许写进去**（写进去就成了第七种模式）。
// 真正的"贴到哪"由 PlaceBottomLeft() 用 AnchorRect() 的结果算，逻辑只有一行减法，不在这里重复测。
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.EdgeAnchorTest /out:%TEMP%\edge.exe src\*.cs tests\edge-anchor-test.cs
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SnapWheel
{
    static class EdgeAnchorTest
    {
        static int pass, fail;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + "   " + detail); }
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
            Console.WriteLine("=== 轮盘靠边方式 ===\n");

            // ---- 1. 默认值：必须是 auto（老用户升级后行为不变）----
            {
                Settings s = new Settings();
                Check("默认是 auto（升级后行为不变）", s.EdgeAnchor == "auto", "实际 " + s.EdgeAnchor);
            }

            // ---- 2. 纯判断：三档 × 四种环境 ----
            // 参数顺序：mode, reserved(工作区被扣了一块), autohide(任务栏自动隐藏)
            Check("screen：无论如何都贴屏幕边（被扣+自动隐藏）", Native.AnchorIsScreen("screen", true, true), "false");
            Check("screen：无论如何都贴屏幕边（没被扣）",      Native.AnchorIsScreen("screen", false, false), "false");
            Check("work：无论如何都贴工作区边（被扣+自动隐藏）", !Native.AnchorIsScreen("work", true, true), "true");
            Check("work：无论如何都贴工作区边（没被扣）",       !Native.AnchorIsScreen("work", false, false), "true");
            // auto 是这次的重点：只有"确实扣了 + 任务栏自动隐藏"才贴屏幕边
            Check("auto：工作区被扣 + 任务栏自动隐藏 → 贴屏幕边",
                  Native.AnchorIsScreen("auto", true, true), "false（就是用户报的那条缝）");
            Check("auto：工作区被扣但任务栏没自动隐藏 → 贴工作区边",
                  !Native.AnchorIsScreen("auto", true, false), "true（会把轮盘压到任务栏底下）");
            Check("auto：工作区没被扣 → 贴工作区边（两者本来重合）",
                  !Native.AnchorIsScreen("auto", false, false), "true");
            Check("auto：工作区没被扣但任务栏报自动隐藏 → 还是工作区边",
                  !Native.AnchorIsScreen("auto", false, true), "true");

            // ---- 3. AnchorRect：两种情况都必须是"屏幕里的一块"，且工作区 ⊆ 屏幕 ----
            {
                Rectangle ok = Native.AnchorRect("work");
                Rectangle sc = Native.AnchorRect("screen");
                Rectangle bd = Screen.PrimaryScreen.Bounds;
                Rectangle wk = Screen.PrimaryScreen.WorkingArea;
                Check("work 档拿到的就是工作区", ok == wk, ok + " vs " + wk);
                Check("screen 档拿到的就是屏幕物理边", sc == bd, sc + " vs " + bd);
                Check("工作区不会比屏幕还大", wk.Width <= bd.Width && wk.Height <= bd.Height,
                      wk + " vs " + bd);
                Check("三种档位都能拿到一个像样的矩形",
                      Native.AnchorRect("auto").Width > 0 && Native.AnchorRect("auto").Height > 0 &&
                      ok.Width > 0 && sc.Width > 0, "有空矩形");
            }

            // ---- 4. 设置文件往返：值原样读写回来 ----
            {
                string p = Path.Combine(Path.GetTempPath(), "snapwheel-edge-test.ini");
                if (File.Exists(p)) File.Delete(p);
                string old = Settings.OverridePath;
                try
                {
                    Settings.OverridePath = p;

                    Settings a = new Settings();
                    a.EdgeAnchor = "screen";
                    a.Save();

                    Settings b = Settings.Load();
                    Check("存了 screen，读回来还是 screen", b.EdgeAnchor == "screen", "读回来是 " + b.EdgeAnchor);

                    Settings c = new Settings();
                    c.EdgeAnchor = "work";
                    c.Save();
                    Check("再存 work，读回来是 work", Settings.Load().EdgeAnchor == "work",
                          "读回来是 " + Settings.Load().EdgeAnchor);

                    // 非法值：载入时必须被忽略（否则就成了第七种模式，AnchorRect 会当 auto 处理，
                    // 界面上也对不上——组合框会落到"自动"那一项，用户看到的和实际行为不一致）
                    File.WriteAllText(p, "[General]\r\nEdgeAnchor=banana\r\n", System.Text.Encoding.UTF8);
                    Check("配置里的非法值被忽略（仍然 auto）",
                          Settings.Load().EdgeAnchor == "auto", "读回来是 " + Settings.Load().EdgeAnchor);

                    // 原样存 auto
                    File.WriteAllText(p, "[General]\r\nEdgeAnchor=auto\r\n", System.Text.Encoding.UTF8);
                    Check("配置里写 auto 就读 auto", Settings.Load().EdgeAnchor == "auto",
                          "读回来是 " + Settings.Load().EdgeAnchor);
                }
                finally
                {
                    Settings.OverridePath = old;
                    try { File.Delete(p); } catch { }
                }
            }

            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
