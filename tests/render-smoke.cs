// 绘制冒烟测试：把 WheelForm 的各种状态组合都画一遍（包括拖拽高亮、删除两半确认、圆盘菜单、提示条），
// 任何一处绘制抛异常都算失败 —— 上线前挡住「画错一帧就把程序打死」这类问题。
// 编译：
//   csc /nologo /target:winexe /main:SnapWheel.RenderSmoke /out:rendertest.exe SnapWheel.cs tests\render-smoke.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SnapWheel
{
    static class RenderSmoke
    {
        static int pass = 0, fail = 0;

        static void F(object o, string name, object val)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + name);
            fi.SetValue(o, val);
        }

        static object G(object o, string name)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            return fi.GetValue(o);
        }

        static double Variance(Bitmap b)
        {
            double s = 0, s2 = 0; int n = 0;
            for (int y = 0; y < b.Height; y += 2)
                for (int x = 0; x < b.Width; x += 2)
                {
                    Color c = b.GetPixel(x, y);
                    double v = c.R; s += v; s2 += v * v; n++;
                }
            if (n == 0) return 0;
            double m = s / n;
            return s2 / n - m * m;
        }

        static void DrawOne(WheelForm f, string what)
        {
            MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            if (dw == null) { Console.WriteLine("  FAIL 找不到 DrawWheel"); fail++; return; }
            int w = 900, h = 900;
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                try
                {
                    dw.Invoke(f, new object[] { g, w, h });
                    // 顺便量一下确实画了东西（不是全透明）
                    bool any = false;
                    for (int y = 0; y < h && !any; y += 7)
                        for (int x = 0; x < w; x += 7)
                            if (b.GetPixel(x, y).A > 8) { any = true; break; }
                    Console.WriteLine("  OK   {0}（画面非空={1}）", what, any);
                    pass++;
                }
                catch (Exception ex)
                {
                    Exception real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    Console.WriteLine("  FAIL {0} -> {1}: {2}", what, real.GetType().Name, real.Message);
                    Console.WriteLine("       {0}", real.StackTrace == null ? "" : real.StackTrace.Split('\n')[0].Trim());
                    fail++;
                }
            }
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Settings s = new Settings();
            s.SaveToDisk = false;
            WheelManager mgr = new WheelManager(s);
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;

            // 放几张不同宽高比 / 不同像素格式的图（含 1x1、超宽、带透明）
            Bitmap a = new Bitmap(320, 200, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(a)) { g.Clear(Color.CornflowerBlue); g.FillEllipse(Brushes.White, 20, 20, 80, 80); }
            st.Add(a);
            Bitmap bmp2 = new Bitmap(60, 240, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp2)) { g.Clear(Color.FromArgb(120, 220, 80, 40)); }
            st.Add(bmp2);
            Bitmap bmp3 = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            st.Add(bmp3);
            Bitmap bmp4 = new Bitmap(1200, 40, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp4)) g.Clear(Color.Goldenrod);
            st.Add(bmp4);

            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Application.DoEvents();

            string[] names = {
                "_show", "_menuT", "_menuOpen", "_delConfirm", "_dropActive", "_dropExternal", "_keyDown",
                "_switchFlash", "_hover", "_enlarged", "_peekIndex", "_chipsOpen", "_chipsT", "_dragOutProg"
            };
            Console.WriteLine("--- 绘制状态矩阵（store 里有 {0} 张图）---", st.Items.Count);

            object[] bools = { false, true };
            foreach (object dAct in bools)
                foreach (object dExt in bools)
                    foreach (object del in bools)
                        foreach (object menu in bools)
                        {
                            F(f, "_show", 1f);
                            F(f, "_dropActive", dAct);
                            F(f, "_dropExternal", dExt);
                            F(f, "_dropCount", dExt.Equals(true) ? 3 : 0);
                            F(f, "_delConfirm", del);
                            F(f, "_delConfirmAt", DateTime.Now);
                            F(f, "_menuOpen", menu);
                            F(f, "_menuT", menu.Equals(true) ? 1f : 0f);
                            F(f, "_sector", menu.Equals(true) ? 2 : -1);
                            F(f, "_keyDown", menu.Equals(true));
                            F(f, "_switchFlash", 0.8f);
                            F(f, "_toast", dExt.Equals(true) ? "已加入 3 张图片" : "");
                            F(f, "_toastAt", DateTime.Now);
                            DrawOne(f, string.Format("drop={0}/{1} del={2} menu={3} toast={4}", dAct, dExt, del, menu, dExt.Equals(true) ? 1 : 0));
                        }

            Console.WriteLine();
            Console.WriteLine("--- 极端参数 ---");
            F(f, "_dropActive", false); F(f, "_delConfirm", false); F(f, "_menuOpen", false); F(f, "_menuT", 0f);
            F(f, "_toast", "");
            float[] shows = { 0f, 0.001f, 0.5f, 0.999f, 1f };
            foreach (float sv in shows) { F(f, "_show", sv); DrawOne(f, "_show=" + sv); }
            F(f, "_show", 1f);

            // 空 store
            while (st.Items.Count > 0) st.Items.RemoveAt(0);
            DrawOne(f, "store 空");
            F(f, "_dropActive", true); F(f, "_dropExternal", true); F(f, "_dropCount", 7);
            DrawOne(f, "store 空 + 拖拽高亮");
            F(f, "_dropActive", false);

            // 极端 size（很小 / 很大）
            F(f, "_thumb", 4f); F(f, "_R", 40f); DrawOne(f, "极小尺寸");
            F(f, "_thumb", 200f); F(f, "_R", 1400f); DrawOne(f, "极大尺寸");
            F(f, "_thumb", 96f); F(f, "_R", 300f);

            Console.WriteLine();
            Console.WriteLine("--- 开启动画各进度 ---");
            foreach (float it in new float[] { 0f, 0.1f, 0.25f, 0.4f, 0.55f, 0.7f, 0.85f, 1f })
            {
                F(f, "_intro", true);
                F(f, "_introT", it);
                F(f, "_introDur", 1.8f);
                F(f, "_show", 1f);
                DrawOne(f, "开启动画 " + (int)(it * 100) + "%");
            }
            F(f, "_intro", false); F(f, "_introT", 1f);

            Console.WriteLine();
            Console.WriteLine("--- 万能键按下动画 ---");
            foreach (float kt in new float[] { 0f, 0.5f, 1f })
            {
                F(f, "_keyT", kt);
                F(f, "_keyDown", kt > 0.5f);
                DrawOne(f, "万能键按下 " + (int)(kt * 100) + "%");
            }
            F(f, "_keyT", 0f); F(f, "_keyDown", false);

            Console.WriteLine();
            Console.WriteLine("--- 风格极端组合（毛玻璃/圆角/阴影/主题色/标签开关/动画速度）---");
            {
                string[] styles = { "neu", "flat", "solid" };
                int[] glass = { 40, 100 };
                int[] radii = { 0, 30 };
                int[] shadows = { 0, 100 };
                int[] speeds = { 70, 140 };
                int combos = 0, bad2 = 0;
                foreach (string st2 in styles)
                    foreach (int gl in glass)
                        foreach (int rd in radii)
                            foreach (int sh2 in shadows)
                                foreach (int sp in speeds)
                                {
                                    s.UiStyle = st2; s.GlassPercent = gl; s.CardRadius = rd;
                                    s.ShadowPercent = sh2; s.AnimSpeed = sp;
                                    s.AccentIndex = (combos % 8);
                                    s.ShowNameLabel = (combos % 2 == 0);
                                    s.ShowCountLabel = (combos % 3 != 0);
                                    F(f, "_show", 1f);
                                    combos++;
                                    string name = string.Format("{0} 玻璃{1} 圆角{2} 阴影{3} 速度{4}", st2, gl, rd, sh2, sp);
                                    int before = fail;
                                    DrawOne(f, name);
                                    if (fail != before) bad2++;
                                }
                s.UiStyle = "neu"; s.GlassPercent = 80; s.CardRadius = 14;
                s.ShadowPercent = 55; s.AnimSpeed = 100; s.AccentIndex = -1;
                s.ShowNameLabel = true; s.ShowCountLabel = true;
                Console.WriteLine("  组合数 {0}，失败 {1}", combos, bad2);
            }

            Console.WriteLine();
            Console.WriteLine("--- 毛玻璃：模糊管线 ---");
            {
                // 棋盘格 -> 模糊 -> 方差应该大幅下降（说明确实糊了），尺寸不变
                Bitmap src = new Bitmap(120, 120, PixelFormat.Format32bppArgb);
                for (int y = 0; y < 120; y++)
                    for (int x = 0; x < 120; x++)
                        src.SetPixel(x, y, ((x / 6 + y / 6) % 2 == 0) ? Color.Black : Color.White);
                double v0 = Variance(src);
                MethodInfo blur = typeof(WheelForm).GetMethod("BlurBitmap", BindingFlags.NonPublic | BindingFlags.Static);
                Bitmap bl = (Bitmap)blur.Invoke(null, new object[] { src, 6 });
                double v1 = Variance(bl);
                bool ok = bl.Width == src.Width && bl.Height == src.Height && v1 < v0 * 0.25;
                Console.WriteLine("  {0} 模糊后方差 {1:F0} -> {2:F0}，尺寸 {3}x{4}",
                    ok ? "OK  " : "FAIL", v0, v1, bl.Width, bl.Height);
                if (ok) pass++; else fail++;
                src.Dispose(); bl.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine("--- 毛玻璃：真背景抓取 ---");
            {
                FieldInfo bf = typeof(WheelForm).GetField("_backdropBlur", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo vf = typeof(WheelForm).GetField("_backdropValid", BindingFlags.NonPublic | BindingFlags.Instance);
                try { f.CaptureBackdrop(); } catch { }
                bool got = (bool)vf.GetValue(f);
                Bitmap bd = (Bitmap)bf.GetValue(f);
                Console.WriteLine("  {0} 抓到背景={1} 尺寸={2}", got ? "OK  " : "FAIL", got, bd == null ? "null" : (bd.Width + "x" + bd.Height));
                if (got && bd != null) pass++; else fail++;
            }

            Console.WriteLine();
            Console.WriteLine("--- 改名入口（这块之前是坏的：药丸不在可点区域，点了会穿透到桌面）---");
            {
                F(f, "_show", 1f);
                F(f, "_intro", false);
                DrawOne(f, "先画一帧，记录名字药丸位置");
                Rectangle krNow = (Rectangle)typeof(WheelForm)
                    .GetMethod("KeyRect", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null);
                if (krNow.Width < 8)
                {
                    Console.WriteLine("  --   无万能键变体：没有名字药丸，跳过改名入口检查");
                    pass += 2;
                }
                else
                {
                MethodInfo npr = typeof(WheelForm).GetMethod("NamePillRect", BindingFlags.NonPublic | BindingFlags.Instance);
                RectangleF pill = (RectangleF)npr.Invoke(f, null);
                bool ok1 = pill.Width > 10 && pill.Height > 10;
                Console.WriteLine("  {0} 名字药丸矩形 = {1}", ok1 ? "OK  " : "FAIL", pill);
                if (ok1) pass++; else fail++;

                MethodInfo oc = typeof(WheelForm).GetMethod("OverContent", BindingFlags.NonPublic | BindingFlags.Instance);
                Point ctr = new Point((int)(pill.X + pill.Width / 2), (int)(pill.Y + pill.Height / 2));
                bool over = (bool)oc.Invoke(f, new object[] { ctr });
                Console.WriteLine("  {0} 药丸中心 {1} 属于可点区域（false 就会被穿透，点不动）", over ? "OK  " : "FAIL", ctr);
                if (over) pass++; else fail++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- 改名弹框逻辑（不碰鼠标，直接触发按钮）---");
            {
                Type rf = typeof(WheelForm).Assembly.GetType("SnapWheel.RenameForm");
                FieldInfo boxF = rf.GetField("_box", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo valF = rf.GetField("Value", BindingFlags.Public | BindingFlags.Instance);
                Func<Form, Button> okBtn = delegate(Form fm)
                {
                    foreach (Control c in fm.Controls)
                    {
                        Button b = c as Button;
                        if (b != null && b.Text == "改好了") return b;
                    }
                    return null;
                };
                // 窗口没显示过，PerformClick 会因为 CanSelect=false 而不触发，这里直接走 OnClick
                MethodInfo onClick = typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance);
                Action<Button> press = delegate(Button b) { onClick.Invoke(b, new object[] { EventArgs.Empty }); };
                using (Form d = (Form)Activator.CreateInstance(rf, new object[] { "项目1" }))
                {
                    ((TextBox)boxF.GetValue(d)).Text = "  新项目  ";
                    press(okBtn(d));
                    string got = (string)valF.GetValue(d);
                    bool ok = got == "新项目";
                    Console.WriteLine("  {0} 输入「  新项目  」-> 存成「{1}」", ok ? "OK  " : "FAIL", got);
                    if (ok) pass++; else fail++;
                }
                using (Form d = (Form)Activator.CreateInstance(rf, new object[] { "项目2" }))
                {
                    ((TextBox)boxF.GetValue(d)).Text = "   ";
                    press(okBtn(d));
                    string got = (string)valF.GetValue(d);
                    bool ok = got == "项目2";
                    Console.WriteLine("  {0} 空名字 -> 回退原名「{1}」", ok ? "OK  " : "FAIL", got);
                    if (ok) pass++; else fail++;
                }
                using (Form d = (Form)Activator.CreateInstance(rf, new object[] { "x" }))
                {
                    ((TextBox)boxF.GetValue(d)).Text = new string('长', 80);
                    press(okBtn(d));
                    string got = (string)valF.GetValue(d);
                    bool ok = got.Length == 12;
                    Console.WriteLine("  {0} 80 个字 -> 截到 {1} 个（上限 12）", ok ? "OK  " : "FAIL", got.Length);
                    if (ok) pass++; else fail++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- 分辨率/DPI 缩放适配 ---");
            {
                int[] scales = { 0, 80, 100, 125, 150, 200, 250 };
                Type wt = typeof(WheelForm);
                MethodInfo oc = wt.GetMethod("OverContent", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo npr = wt.GetMethod("NamePillRect", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo keyR = wt.GetMethod("KeyRect", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo uiK = wt.GetField("UiK", BindingFlags.NonPublic | BindingFlags.Instance);
                bool allOk = true;
                foreach (int sc in scales)
                {
                    s.UiScale = sc;
                    f.ApplyLayout();
                    Application.DoEvents();
                    F(f, "_show", 1f);
                    F(f, "_intro", false);
                    int before = fail;
                    DrawOne(f, "缩放 " + (sc == 0 ? "自动" : sc + "%"));
                    float k = (float)uiK.GetValue(f);
                    Rectangle kr2 = (Rectangle)keyR.Invoke(f, null);
                    if (kr2.Width < 8)
                    {
                        // 无万能键变体：没有键和名字药丸，只验尺寸随缩放变化、且没画崩
                        bool okN = fail == before && f.Width > 300;
                        Console.WriteLine("    {0} k={1:F2} 窗口={2}px（无万能键变体，跳过键/药丸检查）",
                            okN ? "OK " : "FAIL", k, f.Width);
                        if (okN) pass++; else { fail++; allOk = false; }
                        continue;
                    }
                    Point kc = new Point(kr2.X + kr2.Width / 2, kr2.Y + kr2.Height / 2);
                    bool kOver = (bool)oc.Invoke(f, new object[] { kc });
                    // 键的物理尺寸必须落在窗口里
                    bool inWin = (kr2.Right * k) <= f.Width + 1 && (kr2.Bottom * k) <= f.Height + 1 && kr2.Width > 20;
                    RectangleF pill = (RectangleF)npr.Invoke(f, null);
                    Point pc = new Point((int)(pill.X + pill.Width / 2), (int)(pill.Y + pill.Height / 2));
                    bool pOver = (bool)oc.Invoke(f, new object[] { pc });
                    bool ok = fail == before && kOver && inWin && pOver;
                    Console.WriteLine("    {0} k={1:F2} 窗口={2}px 键在窗口内={3} 键可点={4} 药丸可点={5}",
                        ok ? "OK " : "FAIL", k, f.Width, inWin, kOver, pOver);
                    if (!ok) { allOk = false; fail++; } else pass++;
                }
                s.UiScale = 0;
                f.ApplyLayout();
                Console.WriteLine("  {0} 各缩放下的布局与命中一致", allOk ? "OK  " : "FAIL");
            }

            Console.WriteLine();
            Console.WriteLine("--- 名字长度限制（避免药丸被撑宽）---");
            {
                MethodInfo fn = typeof(WheelForm).GetMethod("FitName", BindingFlags.Public | BindingFlags.Static);
                string nm1 = (string)fn.Invoke(null, new object[] { "项目一", 12 });
                string nm2 = (string)fn.Invoke(null, new object[] { new string('长', 80), 12 });
                bool ok = nm1 == "项目一" && nm2.Length == 12 && nm2.IndexOf('…') > 0;
                Console.WriteLine("  {0} 短名保持不变；80 字 -> {1} 字且带省略号（{2}）",
                    ok ? "OK  " : "FAIL", nm2.Length, nm1);
                if (ok) pass++; else fail++;
            }

            Console.WriteLine();
            Console.WriteLine("--- 淡出动画：玻璃底必须跟着一起变淡 ---");
            {
                // 保证有玻璃底（之前的问题是模糊背景没乘淡出 alpha，面板像"卡住"不消失）
                try { f.CaptureBackdrop(); } catch { }
                MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);
                double a100 = 0, a50 = 0, a25 = 0;
                float[] fadeShows = { 1f, 0.5f, 0.25f };
                double[] res = new double[3];
                List<Point> hot = new List<Point>();
                for (int i = 0; i < fadeShows.Length; i++)
                {
                    F(f, "_show", fadeShows[i]);
                    F(f, "_intro", false);
                    using (Bitmap b = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppPArgb))
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        dw.Invoke(f, new object[] { g, f.Width, f.Height });
                        if (i == 0)
                        {
                            // 先在 _show=1 里挑出"画了东西"的点，后面几帧比同一批点
                            for (int y = 0; y < b.Height; y += 3)
                                for (int x = 0; x < b.Width; x += 3)
                                    if (b.GetPixel(x, y).A > 60) hot.Add(new Point(x, y));
                        }
                        double sum = 0;
                        for (int k = 0; k < hot.Count; k++) sum += b.GetPixel(hot[k].X, hot[k].Y).A;
                        res[i] = hot.Count == 0 ? 0 : sum / hot.Count;
                    }
                }
                a100 = res[0]; a50 = res[1]; a25 = res[2];
                bool ok = hot.Count > 50 && a100 > 90 && a50 < a100 * 0.78 && a25 < a100 * 0.6;
                Console.WriteLine("  {0} 关键像素平均 alpha（{1} 个点）_show=1.00:{2:F1}  0.50:{3:F1}  0.25:{4:F1}",
                    ok ? "OK  " : "FAIL", hot.Count, a100, a50, a25);
                if (ok) pass++; else fail++;
                F(f, "_show", 1f);
            }

            Console.WriteLine();
            Console.WriteLine("--- 收起态（贴边小把手）---");
            {
                Type wt = typeof(WheelForm);
                MethodInfo oc = wt.GetMethod("OverContent", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo nubOut = wt.GetMethod("NubOutRect", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo nubIn = wt.GetMethod("NubInRect", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo keyR2 = wt.GetMethod("KeyRect", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo dw2 = wt.GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);

                f.CollapseWheel();                       // 走一次真实的收起动画
                for (int i = 0; i < 40 && !f.IsCollapsed; i++) { Application.DoEvents(); Thread.Sleep(60); }
                bool collapsed = f.IsCollapsed;
                Console.WriteLine("  {0} 收起动画跑完进入收起态（IsCollapsed={1}）", collapsed ? "OK  " : "FAIL", collapsed);
                if (collapsed) pass++; else fail++;

                RectangleF nr = (RectangleF)nubOut.Invoke(f, null);
                bool atEdge = (nr.X <= 0.5f) && nr.Width > 4 && nr.Height > 20;      // 贴着屏幕左边
                Point nubC = new Point((int)(nr.X + nr.Width / 2), (int)(nr.Y + nr.Height / 2));
                bool nubClickable = (bool)oc.Invoke(f, new object[] { nubC });
                Console.WriteLine("  {0} 拉出把手贴左边={1} 可点={2}",
                    (atEdge && nubClickable) ? "OK  " : "FAIL", atEdge, nubClickable);
                if (atEdge && nubClickable) pass++; else fail++;

                Rectangle kr = (Rectangle)keyR2.Invoke(f, null);
                Point keyC = new Point(kr.X + kr.Width / 2, kr.Y + kr.Height / 2);
                bool keyOver = (bool)oc.Invoke(f, new object[] { keyC });
                Console.WriteLine("  {0} 收起时环上区域不可点（点上去穿透给底下的窗口）", keyOver ? "FAIL" : "OK  ");
                if (!keyOver) pass++; else fail++;

                int opaqueCollapsed = 0, opaqueExpanded = 0;
                using (Bitmap b = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(b)) dw2.Invoke(f, new object[] { g, f.Width, f.Height });
                    for (int y = 0; y < b.Height; y += 4) for (int x = 0; x < b.Width; x += 4) if (b.GetPixel(x, y).A > 60) opaqueCollapsed++;
                }
                f.ExpandWheel();
                F(f, "_intro", false); F(f, "_introT", 1f); F(f, "_show", 1f);
                Application.DoEvents();
                using (Bitmap b = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(b)) dw2.Invoke(f, new object[] { g, f.Width, f.Height });
                    for (int y = 0; y < b.Height; y += 4) for (int x = 0; x < b.Width; x += 4) if (b.GetPixel(x, y).A > 60) opaqueExpanded++;
                }
                // 收起时可见面积应该远小于展开（NO_KEY 变体没有万能键盘，整体像素本来就少）
                bool thin = opaqueCollapsed > 0 && opaqueCollapsed * 5 < opaqueExpanded * 2;
                Console.WriteLine("  {0} 收起时画面里只剩把手（采样点 收起={1} vs 展开={2}）",
                    thin ? "OK  " : "FAIL", opaqueCollapsed, opaqueExpanded);
                if (thin) pass++; else fail++;

                RectangleF nr2 = (RectangleF)nubIn.Invoke(f, null);
                bool inEdge = (nr2.Y + nr2.Height) >= f.Height - 1.5f;     // 贴着屏幕下边
                Point nc2 = new Point((int)(nr2.X + nr2.Width / 2), (int)(nr2.Y + nr2.Height / 2));
                bool inClickable = (bool)oc.Invoke(f, new object[] { nc2 });
                Console.WriteLine("  {0} 展开后出现「收起」把手，贴下边={1} 可点={2}",
                    (inEdge && inClickable) ? "OK  " : "FAIL", inEdge, inClickable);
                if (inEdge && inClickable) pass++; else fail++;
            }

            Console.WriteLine();
            Console.WriteLine("--- 剪贴板自动收纳 ---");
            {
                string oldText = null; Bitmap oldImg = null;
                try
                {
                    if (Clipboard.ContainsImage()) oldImg = new Bitmap(Clipboard.GetImage());
                    else if (Clipboard.ContainsText()) oldText = Clipboard.GetText();
                }
                catch { }
                Type wt = typeof(WheelForm);
                MethodInfo occ = wt.GetMethod("OnClipboardChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo selfAt = wt.GetField("_selfClipboardAt", BindingFlags.NonPublic | BindingFlags.Instance);
                int baseCount = st.Items.Count;
                bool on = s.ClipboardImport;

                s.ClipboardImport = true;
                selfAt.SetValue(f, DateTime.MinValue);
                using (Bitmap b = new Bitmap(320, 200, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.CornflowerBlue); g.FillEllipse(Brushes.Orange, 20, 20, 90, 90); }
                    Clipboard.SetImage(b);
                }
                Application.DoEvents(); Thread.Sleep(150);
                occ.Invoke(f, null);
                int after1 = st.Items.Count;
                selfAt.SetValue(f, DateTime.MinValue);
                occ.Invoke(f, null);                 // 同一张图再来一次：指纹应该挡住
                int after2 = st.Items.Count;
                bool dedupOk = (after1 == baseCount + 1) && (after2 == after1);
                Console.WriteLine("  {0} 剪贴板图自动收进轮盘（{1} -> {2}），同一张不重收（{3}）",
                    dedupOk ? "OK  " : "FAIL", baseCount, after1, after2);
                if (dedupOk) pass++; else fail++;

                using (Bitmap b2 = new Bitmap(200, 200, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(b2)) g.Clear(Color.SeaGreen);
                    Clipboard.SetImage(b2);
                }
                selfAt.SetValue(f, DateTime.Now);    // 模拟"是我们自己写进去的"
                Application.DoEvents(); Thread.Sleep(150);
                occ.Invoke(f, null);
                bool selfOk = st.Items.Count == after2;
                Console.WriteLine("  {0} 自己写进剪贴板的图不会回环再收一遍", selfOk ? "OK  " : "FAIL");
                if (selfOk) pass++; else fail++;

                s.ClipboardImport = false;
                selfAt.SetValue(f, DateTime.MinValue);
                occ.Invoke(f, null);
                bool offOk = st.Items.Count == after2;
                Console.WriteLine("  {0} 关掉开关后不再自动收纳", offOk ? "OK  " : "FAIL");
                if (offOk) pass++; else fail++;
                s.ClipboardImport = on;

                try
                {
                    if (oldImg != null) Clipboard.SetImage(oldImg);
                    else if (oldText != null) Clipboard.SetText(oldText);
                    else Clipboard.Clear();
                }
                catch { }
                if (oldImg != null) oldImg.Dispose();
                while (st.Items.Count > baseCount) st.Items.RemoveAt(st.Items.Count - 1);
            }

            try { f.HideWheel(); } catch { }
            try { f.Close(); f.Dispose(); } catch { }

            Console.WriteLine();
            Console.WriteLine("--- 极端比例判定（阈值 " + WheelForm.ExtremeRatio + ":1）---");
            {
                MethodInfo isEx = typeof(WheelForm).GetMethod("IsExtreme", BindingFlags.NonPublic | BindingFlags.Static);
                float limit = WheelForm.ExtremeRatio;
                // 比例 -> 期望是否算“极端”
                object[][] cases = {
                    new object[] { 200, 200, false, "1:1" },
                    new object[] { 320, 180, false, "16:9 (1.78)" },
                    new object[] { 460, 200, false, "2.3:1（21:9 超宽屏）" },
                    new object[] { 355, 100, false, "3.55:1（32:9 带鱼屏）" },
                    new object[] { (int)(limit * 100) - 10, 100, false, "刚好没到阈值" },
                    new object[] { (int)(limit * 100) + 10, 100, true, "刚过阈值" },
                    new object[] { 800, 100, true, "8:1（长截图）" },
                    new object[] { 100, 800, true, "1:8（长竖图）" },
                    new object[] { 200, 200, false, "1:1" }
                };
                int ok = 0, bad = 0;
                foreach (object[] c in cases)
                {
                    int w = (int)c[0], h = (int)c[1]; bool want = (bool)c[2]; string name = (string)c[3];
                    StoreItem it = new StoreItem();
                    it.Image = new Bitmap(w, h);
                    bool got = (bool)isEx.Invoke(null, new object[] { it });
                    bool pass2 = got == want;
                    Console.WriteLine("  {0} {1} ({2}x{3}) -> 极端={4}（期望 {5}）", pass2 ? "OK  " : "FAIL", name, w, h, got, want);
                    if (pass2) ok++; else bad++;
                    it.Image.Dispose();
                }
                Console.WriteLine("  小计 通过 {0} / 失败 {1}", ok, bad);
                pass += ok; fail += bad;
            }

            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.Exit(fail == 0 ? 0 : 1);
        }
    }
}
