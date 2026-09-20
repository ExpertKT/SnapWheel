// hit-test.cs -- 交互层的"点得中吗"：命中区互不重叠 + 每个控件中心都命中自己。
//
// 为什么要补这一层：这个项目**所有 P0 都出在交互层** —— 窗口层级、拖放失效、
// "点不动"、"点到别的元素上去了"。而它们几乎全是"两个元素的命中区叠在一起"或
// "元素跑出窗口"造成的，这两件事**读代码很难发现**（要把位置、尺寸、坐标系同时在脑子里算一遍），
// 但让程序自己量一遍就一目了然 —— 和 ui-probe 量"控件重叠"是同一个思路。
//
// 几何从哪来：`_diag` 注册表。**元素的矩形是它画自己的时候顺手记下来的**，
// 不是另写一套几何去推算 —— 后者迟早就和真实绘制对不上（反例 #1）。
//
// 覆盖三块：
//   ① 命中区：三小按钮 / 万能键 / 名字药丸 / 计数胶囊 两两不重叠，且各自中心命中自己
//   ② 窗口属性：置顶、不占任务栏、不激活、分层 —— 这些正是"截图不在最顶层"那类 P0 的判据
//   ③ 浮层：铺满虚拟屏幕 + 置顶（截图浮层的本分）
//
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.HitTest /out:%TEMP%\hit.exe /r:System.Runtime.WindowsRuntime.dll src\*.cs tests\hit-test.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class HitTest
    {
        static int pass, fail;
        static void Check(string n, bool ok, string d)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + n); }
            else { fail++; Console.WriteLine("  [FAIL] " + n + "   " + d); }
        }

        static object G(object o, string n)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            return fi.GetValue(o);
        }
        static void S(object o, string n, object v)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            fi.SetValue(o, v);
        }
        static bool M(object o, string n, object[] args)
        {
            MethodInfo mi = o.GetType().GetMethod(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (mi == null) throw new Exception("找不到方法 " + n);
            object r = mi.Invoke(o, args);
            return r is bool ? (bool)r : false;
        }

        [STAThread]
        static void Main()
        {
            try { Run(); }
            catch (Exception ex)
            {
                Console.WriteLine("出错：" + ex.GetType().Name + "  " + ex.Message);
                if (ex.InnerException != null) Console.WriteLine("  内层：" + ex.InnerException.Message);
                Environment.ExitCode = 1;
            }
        }

        // 这些是"能点的控件"，必须两两不重叠。环、缩略图、提示条这些大件不参与 ——
        // 它们的包围盒天生会互相包含，拿包围盒比没有意义。
        // 「计数胶囊」也**故意不参与**：它注册的是那条弧形路径的包围盒（98×98），
        // 不是命中区（胶囊本身不是点击目标），拿它比会把一堆无关的东西判成重叠。
        static readonly string[] Interactive = {
            "关闭键（短按收起 / 长按 0.65s 退出）",
            "设置键", "截图键", "万能键（圆盘）",
            "名字药丸（当前轮盘名）"
        };

        // 两个圆角控件在**角上**蹭到几像素不算重叠 —— 那里本来就点不到东西。
        // 只有两个方向都压进去超过这个数，才是真的"点这儿会点到别人身上"。
        const float OverlapSlack = 4f;

        static void Run()
        {
            try { Lang.Init("zh"); } catch { }
            Application.EnableVisualStyles();
            Settings.OverridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_hit_settings.ini");
            WheelManager.OverrideMetaPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapwheel_hit_wheels.ini");

            Console.WriteLine("=== 交互层：点得中吗 ===\n");

            Settings s = new Settings();
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();

            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;
            for (int i = 0; i < 5; i++)
            {
                Bitmap b = new Bitmap(320, 220, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.CornflowerBlue); }
                st.Add(b);
            }
            S(f, "_show", 1f); S(f, "_intro", false); S(f, "_collapsing", false);
            S(f, "_collapsed", false); S(f, "_showAnimating", false);
            S(f, "_hover", -1); S(f, "_enlarged", -1); S(f, "_peekIndex", -1);
            Application.DoEvents();

            MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);

            // 几何从 `_diag` 注册表来 —— 而它**只在诊断模式开着时才记**（关着是零开销，这是设计）。
            // 所以这里打开它：测的正是"元素自己报出来的矩形"。
            s.DiagMode = true;

            // ---------- ① 几个角落 / 几个缩放档下都量一遍 ----------
            string[] corners = { "BL", "BR", "TL", "TR" };
            bool allOk = true, anyMeasured = false;
            for (int ci = 0; ci < corners.Length; ci++)
            {
                for (int ui = 0; ui < 3; ui++)
                {
                    s.Corner = corners[ci];
                    s.UiScale = (ui == 0) ? 0 : (ui == 1 ? 100 : 150);
                    f.ApplyLayout();
                    Application.DoEvents();
                    S(f, "_show", 1f); S(f, "_intro", false); S(f, "_collapsed", false);
                    using (Bitmap bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)))
                    using (Graphics g = Graphics.FromImage(bmp))
                        dw.Invoke(f, new object[] { g, f.Width, f.Height });

                    IList list = (IList)G(f, "_diag");
                    Dictionary<string, RectangleF> got = new Dictionary<string, RectangleF>();
                    for (int i = 0; i < list.Count; i++)
                    {
                        object kv = list[i];
                        string k = (string)kv.GetType().GetProperty("Key").GetValue(kv, null);
                        object v = kv.GetType().GetProperty("Value").GetValue(kv, null);
                        for (int n = 0; n < Interactive.Length; n++)
                            if (k == Interactive[n]) got[k] = (RectangleF)v;
                    }

                    string tag = corners[ci] + "/" + (s.UiScale == 0 ? "自动" : s.UiScale + "%");
                    if (got.Count < Interactive.Length)
                    {
                        Check("命中区 · " + tag + " 每个可点控件都画出来了", false,
                              "只画出来 " + got.Count + " / " + Interactive.Length + " 个：" +
                              string.Join(",", new List<string>(got.Keys).ToArray()));
                        allOk = false;
                        continue;
                    }
                    anyMeasured = true;
                    if (ui == 1 && ci == 0)
                        for (int a1 = 0; a1 < Interactive.Length; a1++)
                            Console.WriteLine("      " + Interactive[a1] + "  = " + got[Interactive[a1]]);

                    // 两两不重叠（角上蹭几像素不算，见 OverlapSlack）
                    for (int a1 = 0; a1 < Interactive.Length; a1++)
                        for (int b1 = a1 + 1; b1 < Interactive.Length; b1++)
                        {
                            RectangleF ra = got[Interactive[a1]], rb = got[Interactive[b1]];
                            RectangleF ix = RectangleF.Intersect(ra, rb);
                            if (ix.Width > OverlapSlack && ix.Height > OverlapSlack)
                            {
                                Check("命中区 · " + tag + " 不重叠", false,
                                      Interactive[a1] + " 与 " + Interactive[b1] + " 相交 " + ix);
                                allOk = false;
                            }
                        }

                    // 每个控件的中心必须**命中它自己**（也就是不落在别人身上）
                    for (int a1 = 0; a1 < Interactive.Length; a1++)
                    {
                        RectangleF r = got[Interactive[a1]];
                        PointF c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
                        bool inSelf = r.Contains(c);
                        bool inOther = false;
                        for (int b1 = 0; b1 < Interactive.Length; b1++)
                        {
                            if (b1 == a1) continue;
                            if (got[Interactive[b1]].Contains(c)) inOther = true;
                        }
                        if (!inSelf || inOther)
                        {
                            Check("命中区 · " + tag + " · " + Interactive[a1] + " 中心命中自己", false,
                                  "inSelf=" + inSelf + " inOther=" + inOther);
                            allOk = false;
                        }
                    }

                    // 所有控件的中心都必须在窗口里（跑出去就点不到了）
                    for (int a1 = 0; a1 < Interactive.Length; a1++)
                    {
                        RectangleF r = got[Interactive[a1]];
                        SizeF ls = (SizeF)typeof(WheelForm).GetMethod("LogicalSize", BindingFlags.NonPublic | BindingFlags.Instance)
                                                             .Invoke(f, null);
                        float margin = 6f;
                        if (r.X < -margin || r.Y < -margin || r.Right > ls.Width + margin || r.Bottom > ls.Height + margin)
                        {
                            Check("命中区 · " + tag + " · " + Interactive[a1] + " 在窗口内", false,
                                  r + " 超出 " + ls);
                            allOk = false;
                        }
                    }
                }
            }
            Check("命中区：四种角落 × 三档缩放下，五个可点控件都互不重叠、中心命中自己、且在窗口内",
                  allOk && anyMeasured, allOk ? "一个都没量到" : "见上面的红字");

            // ---------- ①b 瞬时反馈（提示条）不能被常驻元素压住 ----------
            //
            // 起因：用户实机复现 —— 「松手把 3 张图加入「项目1」」这条提示被**计数胶囊**盖住了字。
            // 根因不是巧合，是**绘制顺序写死了**：计数胶囊画在提示条之后，于是它永远在上面。
            // 这条检查就是盯着"画出来的先后"和"该有的先后"是不是一回事。
            {
                s.DiagMode = true;
                string[] corners2 = { "BL", "BR", "TL", "TR" };
                bool toastOk = true, measured = false;
                for (int ci = 0; ci < corners2.Length; ci++)
                {
                    for (int ui = 0; ui < 2; ui++)
                    {
                        s.Corner = corners2[ci];
                        s.UiScale = (ui == 0) ? 0 : 150;
                        f.ApplyLayout();
                        Application.DoEvents();
                        S(f, "_show", 1f); S(f, "_intro", false); S(f, "_collapsing", false);
                        S(f, "_collapsed", false); S(f, "_showAnimating", false);
                        // 摆一条最长的那种提示（太长会被 12 字截断，这条正好贴着上限）
                        S(f, "_toast", "松手把 3 张图加入「项目1」");
                        S(f, "_toastAt", DateTime.Now.AddSeconds(-0.35));   // 淡入已完成、还没开始淡出
                        using (Bitmap bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)))
                        using (Graphics g = Graphics.FromImage(bmp))
                            dw.Invoke(f, new object[] { g, f.Width, f.Height });

                        IList list = (IList)G(f, "_diag");
                        RectangleF toast = RectangleF.Empty; bool hasToast = false;
                        List<string> blockers = new List<string>();
                        for (int i = 0; i < list.Count; i++)
                        {
                            object kv = list[i];
                            string k = (string)kv.GetType().GetProperty("Key").GetValue(kv, null);
                            object v = kv.GetType().GetProperty("Value").GetValue(kv, null);
                            if (k.StartsWith("提示条")) { toast = (RectangleF)v; hasToast = true; break; }
                        }
                        if (!hasToast) { toastOk = false; Check("提示条 · 画出来了", false, "注册表里没有提示条"); continue; }
                        measured = true;

                        string tag2 = corners2[ci] + "/" + (s.UiScale == 0 ? "自动" : s.UiScale + "%");
                        for (int i = 0; i < list.Count; i++)
                        {
                            object kv = list[i];
                            string k = (string)kv.GetType().GetProperty("Key").GetValue(kv, null);
                            object v = kv.GetType().GetProperty("Value").GetValue(kv, null);
                            if (k.StartsWith("提示条")) continue;
                            // 「环」注册的是那条**四分之一圆弧的包围盒**（一个覆盖全窗口的大方块），
                            // 不是它真正画到的像素 —— 拿它比会把一切都判成"压住"。
                            // 同理由见上面 Interactive 里对「计数胶囊」的说明。
                            if (k.StartsWith("环")) continue;
                            RectangleF other = (RectangleF)v;
                            RectangleF ix = RectangleF.Intersect(toast, other);
                            // 角上蹭几像素不算（和上面同一把尺子）
                            if (ix.Width > OverlapSlack && ix.Height > OverlapSlack)
                                blockers.Add(k + " " + ix);
                        }
                        if (blockers.Count > 0)
                        {
                            Check("提示条 · " + tag2 + " 不被别的元素压住", false,
                                  "被压住：" + string.Join("；", blockers.ToArray()));
                            toastOk = false;
                        }
                        // 提示条本身也不能跑出窗口
                        SizeF ls2 = (SizeF)typeof(WheelForm).GetMethod("LogicalSize", BindingFlags.NonPublic | BindingFlags.Instance)
                                                              .Invoke(f, null);
                        if (toast.X < -6f || toast.Y < -6f || toast.Right > ls2.Width + 6f || toast.Bottom > ls2.Height + 6f)
                        {
                            Check("提示条 · " + tag2 + " 在窗口内", false, toast + " 超出 " + ls2);
                            toastOk = false;
                        }

                        // **开启动画进行中也要查**：用户实机看到的那次重叠就发生在展开动画里 ——
                        // 那时计数胶囊还在往自己位置上滑（IntroShift 最多把它挪 110px 到角落里），
                        // 正好路过提示条。只查"完全展开之后"是查不到这个 bug 的。
                        float[] midIntro = { 0.30f, 0.55f, 0.80f };
                        for (int m = 0; m < midIntro.Length; m++)
                        {
                            S(f, "_intro", true); S(f, "_introT", midIntro[m]);
                            S(f, "_collapsing", false); S(f, "_collapsed", false);
                            using (Bitmap bmp2 = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)))
                            using (Graphics g2 = Graphics.FromImage(bmp2))
                                dw.Invoke(f, new object[] { g2, f.Width, f.Height });
                            IList l2 = (IList)G(f, "_diag");
                            RectangleF t2 = RectangleF.Empty; bool has2 = false;
                            for (int i = 0; i < l2.Count; i++)
                            {
                                object kv = l2[i];
                                string k = (string)kv.GetType().GetProperty("Key").GetValue(kv, null);
                                if (!k.StartsWith("提示条")) continue;
                                t2 = (RectangleF)kv.GetType().GetProperty("Value").GetValue(kv, null); has2 = true; break;
                            }
                            if (!has2) continue;
                            List<string> bl2 = new List<string>();
                            for (int i = 0; i < l2.Count; i++)
                            {
                                object kv = l2[i];
                                string k = (string)kv.GetType().GetProperty("Key").GetValue(kv, null);
                                if (k.StartsWith("提示条") || k.StartsWith("环")) continue;
                                RectangleF ix2 = RectangleF.Intersect(t2, (RectangleF)kv.GetType().GetProperty("Value").GetValue(kv, null));
                                if (ix2.Width > OverlapSlack && ix2.Height > OverlapSlack) bl2.Add(k + " " + ix2);
                            }
                            if (bl2.Count > 0)
                            {
                                Check("提示条 · " + tag2 + " 展开进度 " + midIntro[m].ToString("0.00") + " 时也不被压住", false,
                                      "被压住：" + string.Join("；", bl2.ToArray()));
                                toastOk = false;
                            }
                        }
                        S(f, "_intro", false); S(f, "_introT", 1f);
                    }
                }
                Check("提示条：四种角落 × 两档缩放下，都不被任何元素压住、且都在窗口内",
                      toastOk && measured, toastOk ? "一次都没量到" : "见上面的红字");
                S(f, "_toast", "");
                s.DiagMode = false;
            }

            // ---------- ② 窗口属性（"截图不在最顶层"那类 P0 的判据）----------
            s.DiagMode = false;                 // 关掉，免得影响后面
            s.Corner = "BL"; s.UiScale = 0; f.ApplyLayout();
            bool topmost = f.TopMost;
            Check("轮盘：置顶（设置里「总在最前」默认开）", topmost, "TopMost=" + topmost);
            Check("轮盘：不占任务栏", !f.ShowInTaskbar, "ShowInTaskbar=" + f.ShowInTaskbar);
            Check("轮盘：不抢焦点（WS_EX_NOACTIVATE）",
                  (f.ExStyleForTest & 0x08000000) != 0, "没有 NOACTIVATE：0x" + f.ExStyleForTest.ToString("X"));
            Check("轮盘：分层窗口（WS_EX_LAYERED，自己画透明底）",
                  (f.ExStyleForTest & 0x00080000) != 0, "没有 LAYERED：0x" + f.ExStyleForTest.ToString("X"));
            Check("轮盘：不出现在 Alt+Tab 里（WS_EX_TOOLWINDOW）",
                  (f.ExStyleForTest & 0x00000080) != 0, "没有 TOOLWINDOW：0x" + f.ExStyleForTest.ToString("X"));

            // ---------- ③ 浮层：铺满虚拟屏幕 + 置顶 ----------
            try
            {
                using (Bitmap shot = new Bitmap(400, 300))
                {
                    Rectangle vs = SystemInformation.VirtualScreen;
                    using (OverlayForm ov = new OverlayForm(vs, shot, s))
                    {
                        Check("浮层：置顶（截图时必须在所有窗口之上）", ov.TopMost, "TopMost=false");
                        Check("浮层：铺满整个虚拟屏幕",
                              ov.Bounds.Width == vs.Width && ov.Bounds.Height == vs.Height,
                              ov.Bounds + " vs " + vs);
                        Check("浮层：不占任务栏", !ov.ShowInTaskbar, "ShowInTaskbar=true");
                    }
                }
            }
            catch (Exception ex) { Check("浮层：构造", false, ex.Message); }

            f.Dispose();
            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
