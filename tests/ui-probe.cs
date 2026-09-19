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
                Check("引导 · 从 0.9.4 升上来 → 只标 0.9.5 那一条", fromPrev == 1, "实际 " + fromPrev);
                Check("引导 · 从 0.8.0 升上来 → 0.8.1 / 0.9.0 / 0.9.5 共三条", fromOld == 3, "实际 " + fromOld);
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
    }
}
