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

            // ---------- ③ 取字结果框：新插进去的「取字引擎」那一行 ----------
            ProbeOcrForm();
            // ---------- ③b 换引擎之后**真的**重新识别并把原文换掉 ----------
            ProbeOcrSwitch();

            // ---------- ④ 设置里那个新的「移进来」开关（1.3.0）----------
            ProbeMoveInToggle();

            // ---------- ⑤ 改名字那个小窗口里新的「这个环收什么」（1.3.0 的 takes）----------
            ProbeWheelTakes();

            Console.WriteLine();
            Console.WriteLine(string.Format("结果：通过 {0}，失败 {1}", pass, fail));
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        // ---------- ④ 设置里那个新的「移进来」开关（1.3.0）----------
        //
        // 为什么值得单独钉一条：新加一个勾选框最容易犯的错是**加了控件、忘了接保存**
        // ——界面上能勾、确定之后什么都没发生，而且没有任何渲染/逻辑测试会红
        //（这个项目在 0.9.x 踩过一次"确定后改动打回原形"，所以这类断言留在这儿）。
        // 全链路：界面勾上 → SaveFromUi → Settings.MoveInOnDrop → WheelManager.ApplySettings → Store.MoveInOnDrop。
        static void ProbeMoveInToggle()
        {
            SettingsForm f = null;
            try
            {
                string ini = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_uiprobe_settings.ini");
                string meta = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_uiprobe_wheels.txt");
                try { if (System.IO.File.Exists(ini)) System.IO.File.Delete(ini); } catch { }
                Settings.OverridePath = ini;
                WheelManager.OverrideMetaPath = meta;

                Settings s = new Settings();
                s.MoveInOnDrop = true;          // 初始态必须**跟着设置走**，不能永远 false
                f = new SettingsForm(s);
                f.CreateControl();
                IntPtr h = f.Handle;            // 强制建句柄，触发布局
                f.PerformLayout();

                FieldInfo fi = typeof(SettingsForm).GetField("_chkMoveIn", BindingFlags.Instance | BindingFlags.NonPublic);
                Check("设置界面 · 第 1 页有「移进来」开关", fi != null, "找不到 _chkMoveIn 字段");
                if (fi != null)
                {
                    CheckBox c = fi.GetValue(f) as CheckBox;
                    // ⚠️ 这里**不能**判 c.Visible：窗口从来没 Show 过，WinForms 的 Visible getter 会连带父级一起算，
                    // 于是永远回 false（假警报）。"看得见"这件事由下面那条几何断言负责（在不在客户区里）。
                    Check("设置界面 · 开关挂在页面上、没被禁掉", c != null && c.Parent != null && c.Enabled,
                          "控件没挂进布局 或 Enabled 为假");
                    if (c != null)
                    {
                        Check("设置界面 · 开关初始状态跟着设置走", c.Checked, "MoveInOnDrop=true 但勾选框是空的");

                        // 用户实测反馈（2026-09-27）："移进来的功能不生效"，而他 settings.ini 里 MoveInOnDrop=0。
                        // 所以先钉住一件在真机上才会不同的事：**这一行到底在不在窗口里**。
                        // 它是第 1 页最后一行，而窗口高度上限是工作区的 92%，页面又不会滚动 —— 长过头就被裁掉，
                        // 裁掉 = 用户根本点不到，功能"不生效"。
                        Control rowHost = c.Parent;
                        while (rowHost != null && rowHost.Parent != f) rowHost = rowHost.Parent;   // 找到直接挂在窗体上的那一层
                        if (rowHost != null)
                        {
                            Rectangle rb = InRoot(f, rowHost);
                            Check("设置界面 · 「移进来」开关在窗口可见范围内（顶 " + rb.Top + " 底 " + rb.Bottom
                                  + " 左 " + rb.Left + " 右 " + rb.Right + " / 客户区 " + f.ClientSize.Width + "×" + f.ClientSize.Height + "）",
                                  rb.Bottom > 0 && rb.Bottom <= f.ClientSize.Height && rb.Left >= 0 && rb.Right <= f.ClientSize.Width,
                                  "开关落在窗口外（被裁掉了），用户点不到它");
                        }

                        c.Checked = false;      // 用户把它关掉
                        MethodInfo mi = typeof(SettingsForm).GetMethod("SaveFromUi", BindingFlags.Instance | BindingFlags.NonPublic);
                        Check("设置界面 · 找得到保存函数（SaveFromUi）", mi != null, "没有 SaveFromUi");
                        if (mi != null)
                        {
                            mi.Invoke(f, null);
                            Check("设置界面 · 关掉之后真的写回了设置", !s.MoveInOnDrop, "点了确定 MoveInOnDrop 还是 true");
                        }

                        // 上面那条走的是反射调 SaveFromUi；用户走的是**真按钮**。两条路要都能到。
                        // ⚠️ 不能用 ok.PerformClick()（没 Show 过的窗口 CanSelect 为假，不触发 Click），
                        // 所以直接反射调 RoundButton.OnClick —— 跟 ProbeWheelTakes 里同一个坑。
                        c.Checked = true;
                        Button okBtn = null;
                        Walk(f, delegate(Control cc)
                        {
                            Button b = cc as Button;
                            if (okBtn == null && b != null && b.Text == Lang.T("确定", "OK")) okBtn = b;
                        });
                        Check("设置界面 · 找得到「确定」按钮", okBtn != null, "按钮树上没有「确定」");
                        if (okBtn != null)
                        {
                            MethodInfo oc = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
                            if (oc != null) oc.Invoke(okBtn, new object[] { EventArgs.Empty });
                            Check("设置界面 · 点真「确定」把开关写回设置", s.MoveInOnDrop,
                                  "勾上「移进来」后点确定，MoveInOnDrop 还是 false");
                        }
                    }
                }

                // 设置 → 轮盘 → 格子：中间这两行如果写错（比如接成了 SaveToDisk），下面这条会红
                Settings s2 = new Settings();
                s2.MoveInOnDrop = true;
                WheelManager wm = new WheelManager(s2);
                wm.ApplySettings();
                Check("设置 → 轮盘 → 格子：开关真的传到 Store 上（不是接错线）", wm.ActiveStore.MoveInOnDrop,
                      "ApplySettings 没把 MoveInOnDrop 设下去");
            }
            catch (Exception ex) { Check("设置界面 · 移进来开关探针没炸", false, ex.GetType().Name + " " + ex.Message); }
            finally { if (f != null) try { f.Dispose(); } catch { } }
        }

        // ---------- ⑤ 改名字那个小窗口里新的「这个环收什么」（1.3.0 的 takes）----------
        //
        // 钉的是**选择有没有真的交出去**：窗口里选好之后按「改好了」，RenameWheel 才会拿 rf.Takes
        // 写回 Wheel.Takes 并落盘。这类"控件建了、值没接出来"的错和上一条（勾选框没接保存）是同一类。
        static void ProbeWheelTakes()
        {
            RenameForm rf = null;
            try
            {
                string ini = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_uiprobe_settings.ini");
                string meta = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_uiprobe_wheels.txt");
                Settings.OverridePath = ini;
                WheelManager.OverrideMetaPath = meta;

                rf = new RenameForm(Lang.T("项目", "Project"), Wheel.TakesAny);
                rf.CreateControl();
                IntPtr h = rf.Handle;      // 强制建句柄，触发布局
                rf.PerformLayout();

                FieldInfo fi = typeof(RenameForm).GetField("_cmbTakes", BindingFlags.Instance | BindingFlags.NonPublic);
                Check("改名字窗口 · 有「这个环收什么」下拉框", fi != null, "找不到 _cmbTakes 字段");
                if (fi != null)
                {
                    ComboBox cb = fi.GetValue(rf) as ComboBox;
                    Check("改名字窗口 · 下拉框挂在窗口上、3 档、默认「什么都收」",
                          cb != null && cb.Parent != null && cb.Items.Count == 3 && cb.SelectedIndex == Wheel.TakesAny,
                          cb == null ? "控件没挂进布局" : cb.Items.Count + " 档，选中 " + cb.SelectedIndex);
                    // 三档的**顺序**就是 takes 的取值顺序（0/1/2）：上面那条注释写死了这个约定，这里钉住它
                    if (cb != null)
                    {
                        Button ok = null;
                        Walk(rf, delegate(Control c)
                        {
                            Button b = c as Button;
                            if (ok == null && b != null && b.Text == Lang.T("改好了", "Renamed")) ok = b;
                        });
                        Check("改名字窗口 · 找得到「改好了」按钮", ok != null, "没有 RoundButton");
                        cb.SelectedIndex = Wheel.TakesImage;
                        // ⚠️ 不能用 ok.PerformClick()：它内部有 CanSelect 这道门，窗口没 Show 过就**不会**触发 Click
                        // （而这里特意不 Show —— 跑测试不该在用户屏幕上闪一个对话框）。
                        // 直接触发 Click 事件本身：要钉的正是"挂在 Click 上那个委托有没有把值交出来"。
                        if (ok != null)
                        {
                            MethodInfo oc = ok.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
                            if (oc != null) oc.Invoke(ok, new object[] { EventArgs.Empty });
                        }
                        Check("改名字窗口 · 按了「改好了」之后选择真的交出来了（RenameWheel 才能写回环上）",
                              rf.Takes == Wheel.TakesImage, "Takes=" + rf.Takes);
                    }
                }
            }
            catch (Exception ex) { Check("改名字窗口 · 收什么探针没炸", false, ex.GetType().Name + " " + ex.Message); }
            finally { if (rf != null) try { rf.Dispose(); } catch { } }
        }
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
                    // ⚠️ 重排是**防抖**的（拖动中每动一下就整套重排会卡，见 GuideForm.OnResize）：
                    //    所以要等那个 140ms 的定时器真的跑完，再量。
                    for (int w = 0; w < 12; w++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }
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
        // ---------- ③b 换引擎 = 存设置 + 用同一张图重认 + 把原文换掉 ----------
        //
        // 这一段验的是"那根线有没有接上"：切换动作在 UI 线程、重认在后台线程、结果再用 BeginInvoke
        // 回到 UI 线程改文本框 —— 中间任何一环断了，用户看到的就是"选了没反应"。
        // 重认本身用一个假的 redo（不真跑 OCR），所以这条检查在哪台机器上都一样快、一样确定。
        // 副作用：会写一次设置文件，跑完还原成原来的值。
        static void ProbeOcrSwitch()
        {
            OcrForm f = null;
            string orig = "auto";
            try
            {
                orig = Settings.Load().OcrEngine;
                f = new OcrForm("原文甲乙丙", delegate(out string error) { error = null; return "重认结果：" + Ocr.Engine; });
                f.CreateControl();
                IntPtr h = f.Handle;
                f.PerformLayout();

                ComboBox cb = null;
                TextBox src = null;
                Walk(f, delegate(Control c)
                {
                    if (c is ComboBox && cb == null) cb = (ComboBox)c;
                    if (c is TextBox && src == null) src = (TextBox)c;
                });
                if (cb == null || src == null)
                {
                    Check("取字框 · 换引擎的探测（找得到下拉框和原文框）", false, "cb=" + (cb != null) + " src=" + (src != null));
                    return;
                }

                string before = src.Text;
                cb.SelectedIndex = 2;                              // → native
                // 后台线程 + BeginInvoke：必须抽消息它才回得来
                for (int i = 0; i < 250 && src.Text.IndexOf("重认结果", StringComparison.Ordinal) < 0; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(20);
                }

                Check("取字框 · 换引擎之后自动重认并把原文换掉",
                      src.Text.IndexOf("重认结果：native", StringComparison.Ordinal) >= 0,
                      "原=" + before + " 现=" + src.Text);
                Check("取字框 · 换引擎同时写进设置（下次启动记得住）",
                      string.Equals(Settings.Load().OcrEngine, "native", StringComparison.OrdinalIgnoreCase),
                      "设置里是 " + Settings.Load().OcrEngine);
                Check("取字框 · 重认完下拉框恢复可用（不是卡住）", cb.Enabled, "Enabled=" + cb.Enabled);
            }
            catch (Exception ex)
            {
                Check("取字框 · 换引擎探测", false, "异常：" + ex.Message);
            }
            finally
            {
                try { Settings.SaveOcrEngine(orig); } catch { }
                Ocr.Engine = "auto";
                try { if (f != null) f.Dispose(); } catch { }
            }
        }

        // ---------- ③ 取字结果框：新加的「取字引擎」那行 ----------
        //
        // 为什么要专门量它：这一行的位置不是写死的，而是"在副标题和「原文」之间插一段"算出来的
        // （见 81-OcrForm.cs 的 y 累加），而**插进去一块**最容易把下面的东西顶出窗口 ——
        // 这个项目在 150% DPI 下反复吃过这个亏。所以三档引擎各构造一次，量三件事：
        // 下拉框在不在/选中的对不对、有没有东西越出客户区、下拉框有没有压住别的标签。
        static void ProbeOcrForm()
        {
            string[] engines = { "auto", "system", "native" };
            for (int i = 0; i < engines.Length; i++)
            {
                string eng = engines[i];
                OcrForm f = null;
                try
                {
                    Ocr.Engine = eng;
                    f = new OcrForm("第一行文字\r\n第二行 ABC 1234");
                    f.CreateControl();
                    IntPtr h = f.Handle;            // 强制建句柄，触发布局
                    f.PerformLayout();

                    ComboBox cb = null;
                    Label note = null;
                    var labels = new List<Control>();
                    Walk(f, delegate(Control c)
                    {
                        if (c is ComboBox && cb == null) cb = (ComboBox)c;
                        if (c is Label) labels.Add(c);
                    });

                    Check("取字框(" + eng + ") · 有「取字引擎」下拉框且有 3 个选项",
                          cb != null && cb.Items.Count == 3,
                          cb == null ? "没有 ComboBox" : "选项数 " + cb.Items.Count);
                    if (cb == null) { f.Dispose(); continue; }

                    // auto/system/native ↔ 0/1/2（映射在 81-OcrForm.cs 的 EngineIndex）
                    int want = eng == "system" ? 1 : (eng == "native" ? 2 : 0);
                    Check("取字框(" + eng + ") · 下拉框显示的正是当前引擎",
                          cb.SelectedIndex == want, "实际 " + cb.SelectedIndex + "，应为 " + want);

                    // 用户明确要的"作说明"：那条说明得真的在，而且是句人话（不是空标签）
                    for (int k = 0; k < labels.Count; k++)
                    {
                        Label lb = labels[k] as Label;
                        if (lb != null && lb.Text != null && lb.Text.Length > 40 && note == null) note = lb;
                    }
                    Check("取字框(" + eng + ") · 引擎那一行有说明文字", note != null, "没找到长说明标签");

                    // 没有任何控件越出客户区（越界 = 被窗口切掉，用户看不到）
                    int over = 0; string who = "";
                    Walk(f, delegate(Control c)
                    {
                        Rectangle r = InRoot(f, c);
                        if (r.Right > f.ClientSize.Width + 1 || r.Bottom > f.ClientSize.Height + 1)
                        {
                            over++;
                            if (who.Length == 0) who = c.GetType().Name + "「" + c.Text + "」" + r;
                        }
                    });
                    Check("取字框(" + eng + ") · 没有控件越出窗口（" + f.ClientSize.Width + "x" + f.ClientSize.Height + "）",
                          over == 0, over + " 个越界，例如 " + who);

                    // 下拉框不能压住别的标签：它和「原文」小标签之间只隔一个间距，间距算错就直接撞上
                    Rectangle rcb = InRoot(f, cb);
                    int clash = 0; string ex = "";
                    for (int k = 0; k < labels.Count; k++)
                    {
                        if (labels[k] == note) continue;
                        Rectangle lb = InRoot(f, labels[k]);
                        if (lb.IntersectsWith(rcb))
                        {
                            clash++;
                            if (ex.Length == 0) ex = "「" + labels[k].Text + "」" + lb;
                        }
                    }
                    Check("取字框(" + eng + ") · 下拉框没压住别的标签", clash == 0, clash + " 处，例如 " + ex);
                }
                catch (Exception ex)
                {
                    Check("取字框(" + eng + ") · 探测", false, "异常：" + ex.Message);
                }
                finally { try { if (f != null) f.Dispose(); } catch { } }
            }
            Ocr.Engine = "auto";                    // 探针不改全局状态，跑完还原
        }
    }
}
