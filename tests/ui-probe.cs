using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    // ==================== 界面布局探针 ====================
    //
    // 为什么不测"逻辑"而要专门测"布局"：
    // 这个项目里最难查的几个问题**全是"两个控件重叠"** ——
    //   · 引导的正文压住了底部的「开始使用」按钮；
    //   · 比例胶囊展开时压住了工具条。
    // 这类问题**只用眼睛读代码很难发现**（要同时把两边的位置、尺寸、坐标系都在脑子里算一遍），
    // 但**用程序量一遍就一目了然**。
    //
    // 做法：把每个子控件的矩形**统一换算到窗口坐标系**，然后两两求交。
    // 这是从"引导遮挡"那个 bug 里总结出来的做法 —— 当时写了同类探针，
    // 一跑就抓到了那个越界的标签（它的 Y 比窗口高度还大）。
    //
    // 跑法：编译进测试程序后直接运行；返回码 0 = 全部通过。

    static class UiProbe
    {
        static int pass, fail;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + "   " + detail); }
        }

        // 把一个控件在屏幕上的位置换算到（root 的）客户坐标系
        static Rectangle InRoot(Control root, Control c)
        {
            try { return root.RectangleToClient(c.RectangleToScreen(c.ClientRectangle)); }
            catch { return Rectangle.Empty; }
        }

        static void Walk(Control root, Action<Control> visit)
        {
            foreach (Control c in root.Controls) { visit(c); Walk(c, visit); }
        }

        [STAThread]
        static void Main()
        {
            try { Lang.Init("zh"); } catch { }

            Console.WriteLine("=== 界面布局探针 ===\n");

            // ---------- ① 引导窗口：内容不能压住底部按钮 ----------
            ProbeGuide("引导 · 设置里调出（完整）", new GuideForm());
            ProbeGuide("引导 · 首次安装（只 3 条）", new GuideForm(true, AppInfo.Version));
            ProbeGuide("引导 · 升级弹出（完整）", new GuideForm(false, AppInfo.Version));

            // ---------- ①b 【新】标记必须跟着「上次看过的版本」走 ----------
            ProbeNewTags();

            // ---------- ② 截图浮层：比例胶囊不能压住工具条 ----------
            ProbeOverlay();
            // ---------- ②b 同上，但**用一块指定大小的屏幕算**（本机屏幕大，小屏那种撞法看不到）----------
            ProbeOverlayOnScreen();

            Console.WriteLine();
            Console.WriteLine(string.Format("结果：通过 {0}，失败 {1}", pass, fail));
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        // ---------- 【新】标记：只有"比用户上次看过的那版更新"的说明才该标 ----------
        //
        // 为什么要专门测它：原来的实现是一个**写死的布尔量**贴在 7 条说明上，
        // 于是每次升级那 7 条都重新标一遍【新】—— 升到 0.9.4 还在说「传递模式」是新的（那是 0.9.0 的东西）。
        // **标错的【新】比不标更糟**：用户会以为功能刚加，然后去找一个早就存在的东西。
        // 这类"输出看着正常、语义全错"的问题，正是要有断言钉住的那种。
        static int CountNew(GuideForm gf)
        {
            int n = 0;
            try
            {
                gf.CreateControl();
                IntPtr h = gf.Handle;      // 强制建句柄，触发布局
                gf.PerformLayout();
                Walk(gf, delegate(Control c)
                {
                    if (c is Label && c.Text != null && c.Text.Contains("【新】")) n++;
                });
            }
            catch { }
            try { gf.Dispose(); } catch { }
            return n;
        }

        static void ProbeNewTags()
        {
            try
            {
                string v = AppInfo.Version;
                int atCurrent   = CountNew(new GuideForm(false, v));
                int fromPrev    = CountNew(new GuideForm(false, "0.9.4"));
                int fromOld     = CountNew(new GuideForm(false, "0.8.0"));
                int fromNothing = CountNew(new GuideForm(false, ""));
                int firstEver   = CountNew(new GuideForm(true, v));

                Console.WriteLine("   （【新】条数）看过的版本 = 当前(" + v + ") → " + atCurrent
                                + " ｜ 0.9.4 → " + fromPrev
                                + " ｜ 0.8.0 → " + fromOld
                                + " ｜ 空 → " + fromNothing);

                Check("引导 · 看过当前版本 → 一条【新】都不标", atCurrent == 0, "实际 " + atCurrent);
                // v1.0.0 加了「环现在会『有反应』了」那一条，所以这两条基线跟着 +1。
                // 这是**故意**写成硬编码的：加了引导条目就必须来改这里 ——
                // 否则"新功能"会悄悄标错版本（这个坑在 0.9.5 出过，见 docs/RELEASE.md 最后一节）。
                Check("引导 · 从 0.9.4 升上来 → 标 0.9.5 与 1.0.0（1.0.0 有两条）共三条", fromPrev == 3, "实际 " + fromPrev);
                Check("引导 · 从 0.8.0 升上来 → 0.8.1 / 0.9.0 / 0.9.5 / 1.0.0 共五条", fromOld == 5, "实际 " + fromOld);
                // 窗口可缩放 + 内容跟着重排（用户要求"和设置界面一样"）。
                // 判据：把窗口拖宽之后，内容**总高度必须变小**（同样的话在更宽的行里折行更少）。
                // 如果只把窗口拉大而内容不重排，这个高度不会变 —— 那就是没做成。
                {
                    GuideForm g1 = new GuideForm();
                    g1.StartPosition = FormStartPosition.Manual;
                    g1.Location = new Point(-4000, -4000);
                    g1.Show();
                    Application.DoEvents();
                    int hNarrow = 0;
                    foreach (Control c in g1.Controls) if (c is Panel) foreach (Control k in c.Controls) hNarrow = Math.Max(hNarrow, k.Bottom);
                    g1.Width = g1.Width + 260;                 // 拖宽
                    Application.DoEvents();
                    int hWide = 0;
                    foreach (Control c in g1.Controls) if (c is Panel) foreach (Control k in c.Controls) hWide = Math.Max(hWide, k.Bottom);
                    Console.WriteLine("      内容总高：窄 {0} -> 宽 {1}", hNarrow, hWide);
                    Check("引导 · 拖宽之后内容**重新折行**（总高变小 = 真的重排了，不是只把窗口拉大）",
                          hNarrow > 0 && hWide > 0 && hWide < hNarrow, "窄 " + hNarrow + " -> 宽 " + hWide);
                    Check("引导 · 窗口可缩放（不再是 FixedDialog）", g1.FormBorderStyle == FormBorderStyle.Sizable,
                          "实际 " + g1.FormBorderStyle);
                    g1.Close();
                }

                Check("引导 · 版本越老【新】越多（单调不减）",
                      atCurrent <= fromPrev && fromPrev <= fromOld && fromOld <= fromNothing,
                      atCurrent + " / " + fromPrev + " / " + fromOld + " / " + fromNothing);
                Check("引导 · 全新安装一条【新】都不标（对新人每条都是新的，标满等于没标）",
                      firstEver == 0, "实际 " + firstEver);
            }
            catch (Exception ex)
            {
                Check("引导 · 【新】标记探测", false, "异常：" + ex.Message);
            }
        }

        static void ProbeGuide(string name, GuideForm gf)
        {
            try
            {
                gf.CreateControl();
                IntPtr h = gf.Handle;      // 强制建句柄，触发布局
                gf.PerformLayout();

                Control btn = null;
                var labels = new List<Control>();
                Walk(gf, delegate(Control c)
                {
                    if (c.GetType().Name == "RoundButton" && btn == null) btn = c;
                    if (c is Label && !string.IsNullOrEmpty(c.Text)) labels.Add(c);
                });

                if (btn == null) { Check(name + "：找得到底部按钮", false, "没有 RoundButton"); gf.Dispose(); return; }

                Rectangle bb = InRoot(gf, btn);
                int overlap = 0;
                string example = "";
                foreach (Control l in labels)
                {
                    Rectangle lb = InRoot(gf, l);
                    if (lb.IntersectsWith(bb))
                    {
                        overlap++;
                        if (example.Length == 0)
                            example = "「" + (l.Text.Length > 14 ? l.Text.Substring(0, 14) + "…" : l.Text) + "」在 " + lb;
                    }
                }

                Check(name + "：内容没压住按钮（窗口 " + gf.ClientSize.Width + "x" + gf.ClientSize.Height + "）",
                      overlap == 0, "重叠 " + overlap + " 处，例如 " + example);

                // 窗口不该高过屏幕的 80%（曾经顶满整屏，用户反馈"太长"）
                int maxAccept = (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.80);
                Check(name + "：窗口高度合理（≤ 屏幕 80%）",
                      gf.ClientSize.Height <= maxAccept,
                      "实际 " + gf.ClientSize.Height + " > " + maxAccept);

                gf.Dispose();
            }
            catch (Exception ex)
            {
                Check(name, false, "探测异常：" + ex.Message);
            }
        }

        // 浮层：直接调 PlaceChips（它会算胶囊区域 _panelBounds），再和工具栏矩形 _toolRect 比
        static void ProbeOverlay()
        {
            OverlayForm ov = null;
            try
            {
                Settings s = new Settings();
                using (Bitmap shot = new Bitmap(1600, 900))
                {
                    using (Graphics g = Graphics.FromImage(shot)) g.Clear(Color.SteelBlue);
                    ov = new OverlayForm(new Rectangle(0, 0, 1600, 900), shot, s);

                    FieldInfo fSel = ov.GetType().GetField("_hasSel", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo fSz = ov.GetType().GetField("_sz", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo fC = ov.GetType().GetField("_c", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo fOpen = ov.GetType().GetField("_chipsOpen", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fSel == null || fSz == null || fC == null) { Check("浮层 · 反射字段", false, "拿不到内部字段"); return; }

                    fSel.SetValue(ov, true);
                    fSz.SetValue(ov, new SizeF(700, 420));
                    fC.SetValue(ov, new PointF(800, 620));
                    if (fOpen != null) fOpen.SetValue(ov, true);

                    // 先让工具条走一遍它自己的布局。
                    // **这一步原来漏了** —— 于是 _toolRect 一直是 Rectangle.Empty，
                    // 下面每次都打印"跳过重叠检查"，却把它算作通过。
                    // 名字在、实际不测的检查，比没有检查更危险：它会让人以为这里已经有人看着了。
                    MethodInfo mt = ov.GetType().GetMethod("PlaceToolbar", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (mt == null) { Check("浮层 · 找得到 PlaceToolbar", false, "没有这个方法"); return; }
                    mt.Invoke(ov, null);

                    MethodInfo mc = ov.GetType().GetMethod("PlaceChips", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (mc != null) mc.Invoke(ov, null);

                    FieldInfo fPanel = ov.GetType().GetField("_panelBounds", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo fTool = ov.GetType().GetField("_toolRect", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fPanel == null || fTool == null) { Check("浮层 · 反射胶囊/工具条矩形", false, "拿不到字段"); return; }

                    Rectangle chip = (Rectangle)fPanel.GetValue(ov);
                    Rectangle tool = (Rectangle)fTool.GetValue(ov);

                    Console.WriteLine("   （浮层）胶囊=" + chip + "  工具条=" + tool);

                    // 两个矩形都必须是"真的布局过"的。
                    // 空矩形不是"本次不适用"，而是**这个检查根本没在测东西** —— 判失败，不算通过。
                    bool laidOut = tool.Width > 0 && tool.Height > 0 && chip.Width > 0 && chip.Height > 0;
                    Check("浮层 · 工具条与胶囊都真的布局了（否则下面那条是空检查）",
                          laidOut, "工具条=" + tool + "  胶囊=" + chip);

                    if (laidOut)
                    {
                        Check("浮层 · 比例胶囊展开后不压住工具条",
                              !chip.IntersectsWith(tool),
                              "胶囊 " + chip + " 与工具条 " + tool + " 相交");
                    }
                }
            }
            catch (Exception ex)
            {
                Check("浮层 · 探测", false, "异常：" + ex.Message);
            }
            finally
            {
                try { if (ov != null) ov.Dispose(); } catch { }
            }
        }

        // ---------- ②b 比例胶囊 vs 工具条：**离线**用指定大小的屏幕算一遍 ----------
        //
        // 为什么要离线：上面那条走的是真实屏幕（ScreenFor → Screen.FromPoint），
        // 而"胶囊和工具条都只能摆在选区上方、于是叠在一起"这种事**只有屏幕矮的时候才发生**。
        // 本机 1067 高，上下都塞得下，所以本机永远是绿的 —— CI 的 runner 是 1024×768，必现。
        //
        // 一条只在别人机器上失败的检查，等于把"没测过"伪装成"测过了"。这里把屏幕尺寸变成参数，
        // 于是**本机就能跑出 CI 的结果**。用的是 ToolbarRect / ChipRowY 这两个纯静态函数，
        // 和正式绘制走的是同一份代码（不是另写一套几何去推算）。
        static void ProbeOverlayOnScreen()
        {
            // 和 CI runner 同尺寸，外加两台常见的小屏笔记本
            Rectangle[] screens = {
                new Rectangle(0, 0, 1024, 768),
                new Rectangle(0, 0, 1366, 768),
                new Rectangle(0, 0, 1920, 1080),
            };
            RectangleF sel = new RectangleF(450, 410, 700, 420);   // 和上面的浮层探针同一块选区
            const int bw = 34, bh = 30, gp = 6, outer = 6;   // OverlayForm 里的常量（96 DPI 下不缩放）
            int n = OverlayForm.BtnCount;                    // 直接引用真实常量，别再抄一份数字
            const int chipH = 32, chipW = 495;

            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle scr = screens[i];
                bool vertical, overlap;
                Rectangle tool = OverlayForm.ToolbarRect(scr, sel, n, bw, bh, gp, outer,
                                                         Rectangle.Empty, out vertical, out overlap);
                int rowY = OverlayForm.ChipRowY(sel, chipH, tool, scr.Top, scr.Bottom, 1f);
                Rectangle chip = new Rectangle((int)sel.Left, rowY, chipW, chipH);

                string what = scr.Width + "x" + scr.Height;
                Check("浮层 · " + what + " 上比例胶囊也不压住工具条",
                      !chip.IntersectsWith(tool),
                      "胶囊 " + chip + " 与工具条 " + tool + " 相交");
                // 顺带守住"两个矩形都真的算过"：空矩形不是"本次不适用"，而是这条检查没在测东西
                Check("浮层 · " + what + " 上工具条与胶囊都真的算出来了",
                      tool.Width > 0 && tool.Height > 0 && chip.Width > 0 && chip.Height > 0,
                      "工具条=" + tool + "  胶囊=" + chip);
            }
        }
    }
}
