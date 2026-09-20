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

        // 把 _dropVis 定在指定值渲染一帧，数"拖放提示"自己的矩形里有多少不透明像素。
        // 直接设值 + 直接渲染（**不泵消息**），所以动画定时器不会把它改掉。
        static int CountInHint(WheelForm f, MethodInfo dw, float vis)
        {
            S(f, "_dropVis", vis);
            int n = 0;
            using (Bitmap b = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(b)) dw.Invoke(f, new object[] { g, f.Width, f.Height });
                IList dl = (IList)G(f, "_diag");
                RectangleF rc = RectangleF.Empty; bool has = false;
                for (int i = 0; i < dl.Count; i++)
                {
                    object kv = dl[i];
                    string kk = (string)kv.GetType().GetProperty("Key").GetValue(kv, null);
                    if (!kk.StartsWith("拖放提示")) continue;
                    rc = (RectangleF)kv.GetType().GetProperty("Value").GetValue(kv, null); has = true; break;
                }
                if (!has) return 0;
                int x0 = Math.Max(0, (int)rc.X), x1 = Math.Min(b.Width, (int)rc.Right);
                int y0 = Math.Max(0, (int)rc.Y), y1 = Math.Min(b.Height, (int)rc.Bottom);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        // 用 **alpha 总和**当可见度，不用"不透明像素个数"：
                        // 半透明时像素照样"存在"（A=117 一样 > 60），个数根本区分不出来 ——
                        // 第一版就是这么写的，结果半透明和全显数出来一模一样（8282 == 8282）。
                        n += b.GetPixel(x, y).A;
            }
            return n;
        }

        // 整窗"绿色像素"数：直接量"环有没有变绿"这件事本身（用户原话就是"环随之变绿复原"）。
        // 用不透明像素总数区分度太低 —— 光晕本身很淡，数出来只差几百个。
        static int CountGreen(WheelForm f, MethodInfo dw, float vis)
        {
            // **把那条绿提示关掉**再数：不然数到的全是提示自己（它是绿的、又大），
            // 环那一圈淡光晕的变化会被完全淹没 —— 第一版就是这样，
            // 负向验证把环改回硬切，检查照样报 OK。
            S(f, "_dropExternalShown", false);
            S(f, "_dropVis", vis);
            int n = 0;
            using (Bitmap b = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(b)) dw.Invoke(f, new object[] { g, f.Width, f.Height });
                for (int y = 0; y < b.Height; y += 2)
                    for (int x = 0; x < b.Width; x += 2)
                    {
                        Color c = b.GetPixel(x, y);
                        if (c.A > 60 && c.G > c.R + 25 && c.G > c.B + 25) n++;
                    }
            }
            return n;
        }

        // 整窗不透明像素数（用来量"环上那圈光晕有没有跟着淡"）
        static int CountOpaque(WheelForm f, MethodInfo dw, float vis)
        {
            S(f, "_dropVis", vis);
            int n = 0;
            using (Bitmap b = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(b)) dw.Invoke(f, new object[] { g, f.Width, f.Height });
                for (int y = 0; y < b.Height; y += 2)
                    for (int x = 0; x < b.Width; x += 2)
                        if (b.GetPixel(x, y).A > 60) n++;
            }
            return n;
        }

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
            MethodInfo tk = typeof(WheelForm).GetMethod("AnimTickCore", BindingFlags.NonPublic | BindingFlags.Instance);

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

            // ---------- ①b 瞬时反馈不能被常驻元素压住、也不该互相压住 ----------
            //
            // 起因：用户实机复现 —— 「松手把 3 张图加入「项目1」」这条提示被**计数胶囊**盖住了字。
            //
            // ⚠️ 这个检查第一版只盯着「提示条（Toast）」，而用户看到的那条**根本不是 toast** ——
            // 是"拖放提示"，另一个元素，而且它**连 Diag 名字都没有**。
            // 于是检查全绿、用户那边照旧。教训：**没有名字的元素，任何几何检查都拦不住**。
            // 现在三条瞬时消息（回执 / 拖放提示 / 长按提示）一起查，而且必须**都登记**。
            {
                s.DiagMode = true;
                string[] corners2 = { "BL", "BR", "TL", "TR" };
                string[] names = { "提示条", "拖放提示", "长按关闭键提示" };
                bool allOk2 = true, measured = false;

                for (int k = 0; k < names.Length; k++)
                {
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
                            // 只摆当前这一条，另外两条必须**不出现**（它们共用状态区那一格）
                            S(f, "_toast", k == 0 ? "松手把 3 张图加入「项目1」" : "");
                            S(f, "_toastAt", DateTime.Now.AddSeconds(-0.35));
                            S(f, "_dropActive", k == 1); S(f, "_dropExternal", k == 1);
                            S(f, "_dropCount", 3);
                            S(f, "_dropVis", k == 1 ? 1f : 0f);
                            tk.Invoke(f, null);   // 让锁存跟着走一步（真实运行时每帧都会 tick）
                            S(f, "_closeHoldP", k == 2 ? 0.60f : 0f);

                            // 完全展开 + 展开动画中途各查一遍
                            // （用户那次就发生在动画里：计数胶囊还在往自己位置滑，最多被挪 110px）
                            float[] phases = { 1f, 0.30f, 0.55f, 0.80f };
                            for (int ph = 0; ph < phases.Length; ph++)
                            {
                                bool mid = phases[ph] < 0.999f;
                                S(f, "_intro", mid); S(f, "_introT", phases[ph]);
                                using (Bitmap bmp2 = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)))
                                using (Graphics g2 = Graphics.FromImage(bmp2))
                                    dw.Invoke(f, new object[] { g2, f.Width, f.Height });

                                IList l2 = (IList)G(f, "_diag");
                                RectangleF mine = RectangleF.Empty; bool has2 = false;
                                List<string> others = new List<string>();
                                for (int i = 0; i < l2.Count; i++)
                                {
                                    object kv = l2[i];
                                    string kk = (string)kv.GetType().GetProperty("Key").GetValue(kv, null);
                                    RectangleF vv = (RectangleF)kv.GetType().GetProperty("Value").GetValue(kv, null);
                                    if (kk.StartsWith(names[k])) { mine = vv; has2 = true; continue; }
                                    if (kk.StartsWith("环")) continue;   // 弧的包围盒，不是真实像素
                                    others.Add(kk + "|" + vv.X + "," + vv.Y + "," + vv.Width + "," + vv.Height);
                                }
                                string tag2 = names[k] + " · " + corners2[ci] + "/" +
                                              (s.UiScale == 0 ? "自动" : s.UiScale + "%") +
                                              (mid ? " · 展开进度 " + phases[ph].ToString("0.00") : "");
                                if (!has2)
                                {
                                    Check(tag2 + " 画出来了（且登记了名字）", false, "注册表里找不到它 —— 没有名字就等于没有检查");
                                    allOk2 = false; continue;
                                }
                                measured = true;
                                List<string> blockers = new List<string>();
                                for (int i = 0; i < others.Count; i++)
                                {
                                    string[] p = others[i].Split('|');
                                    string[] q = p[1].Split(',');
                                    RectangleF ix = RectangleF.Intersect(mine,
                                        new RectangleF(float.Parse(q[0]), float.Parse(q[1]), float.Parse(q[2]), float.Parse(q[3])));
                                    if (ix.Width > OverlapSlack && ix.Height > OverlapSlack) blockers.Add(p[0] + " " + ix);
                                }
                                if (blockers.Count > 0)
                                {
                                    Check(tag2 + " 不被别的元素压住", false, "被压住：" + string.Join("；", blockers.ToArray()));
                                    allOk2 = false;
                                }
                                SizeF ls2 = (SizeF)typeof(WheelForm).GetMethod("LogicalSize", BindingFlags.NonPublic | BindingFlags.Instance)
                                                                      .Invoke(f, null);
                                if (mine.X < -6f || mine.Y < -6f || mine.Right > ls2.Width + 6f || mine.Bottom > ls2.Height + 6f)
                                {
                                    Check(tag2 + " 在窗口内", false, mine + " 超出 " + ls2);
                                    allOk2 = false;
                                }
                            }
                        }
                    }
                }
                Check("瞬时反馈（回执 / 拖放提示 / 长按提示）：四种角落 × 两档缩放 × 展开动画中途，" +
                      "都登记了名字、不被压住、都在窗口内",
                      allOk2 && measured, allOk2 ? "一次都没量到" : "见上面的红字");
                S(f, "_toast", ""); S(f, "_dropActive", false); S(f, "_dropExternal", false); S(f, "_closeHoldP", 0f);
                S(f, "_intro", false); S(f, "_introT", 1f);
                s.DiagMode = false;
            }

            // ---------- ①c 拖放态必须有过渡（不能硬切）----------
            //
            // 用户反馈：「现在这个绿提示的出现消失、还有环的随之变绿复原，没有任何过渡」。
            // 根因就是画的时候直接用了 `_dropActive` 这个 bool。现在两边共用一个 `_dropVis` 进度。
            // 这里盯两件事：**进度真的会走到端点**（不许永远差一点点），
            // 以及**画出来的像素确实跟着进度变**（只改状态不落笔是最容易漏的）。
            {
                s.Corner = "BL"; s.UiScale = 0; f.ApplyLayout();
                Application.DoEvents();
                S(f, "_show", 1f); S(f, "_intro", false); S(f, "_collapsing", false);
                S(f, "_collapsed", false); S(f, "_showAnimating", false);

                // 1) 淡入能走到 1、淡出能回到 0（靠真实的动画定时器跑，不是手算）
                S(f, "_dropExternal", true); S(f, "_dropCount", 3);
                S(f, "_dropActive", true); S(f, "_dropVis", 0f);
                for (int k = 0; k < 70 && Math.Abs((float)G(f, "_dropVis") - 1f) > 0.001f; k++)
                { Application.DoEvents(); System.Threading.Thread.Sleep(14); }
                float up = (float)G(f, "_dropVis");
                Check("拖放态：淡入能走到 1（不是永远差一点点）", Math.Abs(up - 1f) < 0.001f, "停在 " + up);

                S(f, "_dropActive", false);
                for (int k = 0; k < 70 && Math.Abs((float)G(f, "_dropVis")) > 0.001f; k++)
                { Application.DoEvents(); System.Threading.Thread.Sleep(14); }
                float dn = (float)G(f, "_dropVis");
                Check("拖放态：淡出能回到 0", Math.Abs(dn) < 0.001f, "停在 " + dn);

                // 2) 半路上的**像素**必须比终点少 —— 那就是"过渡"的样子。
                //    只查状态不查像素的话，"状态在渐变但画的时候没乘它"照样能过。
                s.DiagMode = true;
                S(f, "_dropActive", true); S(f, "_dropExternal", true);
                tk.Invoke(f, null);           // 先让锁存生效，再逐档量
                int nHalf = CountInHint(f, dw, 0.5f);
                int nFull = CountInHint(f, dw, 1.0f);
                int nNone = CountInHint(f, dw, 0.0f);
                s.DiagMode = false;
                Check("拖放态：半透明时的像素明显少于全显（说明 alpha 真的跟着进度走）",
                      nHalf > 0 && nHalf < nFull * 0.88f, "半=" + nHalf + " 全=" + nFull + "（alpha 总和）");
                Check("拖放态：进度为 0 时提示不画（不能残留）", nNone == 0, "还有 " + nNone + " 个像素");

                // 3) 环上的绿光晕也得跟着淡 —— 整窗像素数在两端要差出一大截
                int w0 = CountGreen(f, dw, 0f), w1 = CountGreen(f, dw, 1f);
                Check("拖放态：环随之变绿、也跟着复原（绿像素 0 时远少于 1 时）", w1 > w0 * 1.5 + 200,
                      "0 时 " + w0 + " / 1 时 " + w1);

                S(f, "_dropActive", false); S(f, "_dropVis", 0f); S(f, "_dropExternal", false); S(f, "_dropCount", 0);
                Application.DoEvents();
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
