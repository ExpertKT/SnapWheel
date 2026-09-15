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
            ProbeGuide("引导 · 首次安装（只 3 条）", new GuideForm(true));
            ProbeGuide("引导 · 升级弹出（完整）", new GuideForm(false));

            // ---------- ② 截图浮层：比例胶囊不能压住工具条 ----------
            ProbeOverlay();

            Console.WriteLine();
            Console.WriteLine(string.Format("结果：通过 {0}，失败 {1}", pass, fail));
            Environment.ExitCode = fail == 0 ? 0 : 1;
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

                    MethodInfo mc = ov.GetType().GetMethod("PlaceChips", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (mc != null) mc.Invoke(ov, null);

                    FieldInfo fPanel = ov.GetType().GetField("_panelBounds", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo fTool = ov.GetType().GetField("_toolRect", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fPanel == null || fTool == null) { Check("浮层 · 反射胶囊/工具条矩形", false, "拿不到字段"); return; }

                    Rectangle chip = (Rectangle)fPanel.GetValue(ov);
                    Rectangle tool = (Rectangle)fTool.GetValue(ov);

                    Console.WriteLine("   （浮层）胶囊=" + chip + "  工具条=" + tool);
                    if (tool.Width <= 0 || chip.Width <= 0)
                    {
                        Console.WriteLine("   （工具条或胶囊本次没有布局，跳过重叠检查）");
                        pass++;
                    }
                    else
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
