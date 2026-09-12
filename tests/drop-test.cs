// 真·拖拽落地测试
//   源窗口 = 一个普通 Form（模拟资源管理器），由它发起 DoDragDrop
//   目标 A = 另一个普通 Form（对照组，验证模拟拖拽本身有效）
//   目标 B = WheelForm（真正要测的）
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.DropTest /out:droptest.exe SnapWheel.cs tests\drop-test.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SnapWheel
{
    static class DropTest
    {
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
        const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, MOVE = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public INPUTUNION u; }
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        static void Esc()
        {
            INPUT[] a = new INPUT[2];
            a[0].type = 1; a[0].u.ki.vk = 0x1B;
            a[1].type = 1; a[1].u.ki.vk = 0x1B; a[1].u.ki.flags = 2;
            try { SendInput(2, a, Marshal.SizeOf(typeof(INPUT))); } catch { }
        }

        static int pass = 0, fail = 0;
        static string dir;

        static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(6); }
        }

        static object Call(object o, string name, params object[] args)
        {
            MethodInfo mi = o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) throw new Exception("找不到方法 " + name);
            return mi.Invoke(o, args);
        }

        static DragDropEffects Drag(Control source, DataObject data, Point from, Point to, int timeoutMs)
        {
            bool done = false;
            SetCursorPos(from.X, from.Y);
            Thread.Sleep(80);
            mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Pump(180);            // 必须抽消息，GetKeyState 才会认为左键真的按下去了
            Thread.Sleep(100);

            Thread mover = new Thread(delegate()
            {
                try
                {
                    Thread.Sleep(200);
                    SetCursorPos(to.X, to.Y);
                    mouse_event(MOVE, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(300);
                    mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);   // 松手 -> Drop
                }
                catch { }
            });
            mover.IsBackground = true; mover.Start();

            Thread wd = new Thread(delegate() { Thread.Sleep(timeoutMs); if (!done) Esc(); });
            wd.IsBackground = true; wd.Start();

            DragDropEffects eff = DragDropEffects.None;
            Stopwatch sw = Stopwatch.StartNew();
            try { eff = source.DoDragDrop(data, DragDropEffects.Copy); }
            catch (Exception ex) { Console.WriteLine("      DoDragDrop 抛异常: " + ex.Message); }
            sw.Stop();
            done = true;
            try { mover.Join(800); } catch { }
            Pump(200);
            Console.WriteLine("      （耗时 {0}ms，返回 {1}）", sw.ElapsedMilliseconds, eff);
            return eff;
        }

        // 直接构造 DragEventArgs 调处理器（不依赖能不能模拟 OLE 拖拽）
        static ConstructorInfo DDCtor;
        static DragEventArgs MakeArgs(IDataObject data, int x, int y)
        {
            if (DDCtor == null)
            {
                foreach (ConstructorInfo c in typeof(DragEventArgs).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    ParameterInfo[] ps = c.GetParameters();
                    if (ps.Length == 6 && ps[0].ParameterType == typeof(IDataObject)) { DDCtor = c; break; }
                }
            }
            if (DDCtor == null) throw new Exception("找不到 DragEventArgs 的构造函数");
            return (DragEventArgs)DDCtor.Invoke(new object[] { data, 0, x, y, DragDropEffects.Copy, DragDropEffects.None });
        }

        static void FireOver(WheelForm f, IDataObject data, Point p)
        {
            MethodInfo mi = typeof(WheelForm).GetMethod("OnDragOverWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            mi.Invoke(f, new object[] { f, MakeArgs(data, p.X, p.Y) });
        }

        static DragDropEffects FireDrop(WheelForm f, IDataObject data, Point p)
        {
            MethodInfo mi = typeof(WheelForm).GetMethod("OnDragDropWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            DragEventArgs a = MakeArgs(data, p.X, p.Y);
            mi.Invoke(f, new object[] { f, a });
            return a.Effect;
        }

        [STAThread]
        public static void Main()
        {
          try
          {
            Console.WriteLine("线程单元状态: " + Thread.CurrentThread.GetApartmentState());
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += new System.Threading.ThreadExceptionEventHandler(
                delegate(object o, System.Threading.ThreadExceptionEventArgs ea)
                { Console.WriteLine("   [UI异常] " + ea.Exception.Message); });

            dir = Path.Combine(Path.GetTempPath(), "snapwheel_droptest");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);
            string png = Path.Combine(dir, "pic.png");
            string jpg = Path.Combine(dir, "photo.jpg");
            string ico = Path.Combine(dir, "icon.ico");
            string sub = Path.Combine(dir, "folder");
            Directory.CreateDirectory(sub);
            using (Bitmap b = new Bitmap(200, 140, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.Transparent); g.FillEllipse(Brushes.DeepSkyBlue, 10, 10, 120, 120); }
                b.Save(png, ImageFormat.Png);
                b.Save(jpg, ImageFormat.Jpeg);
                IntPtr h = b.GetHicon();
                using (Icon ic = Icon.FromHandle(h))
                using (FileStream fs = new FileStream(ico, FileMode.Create)) ic.Save(fs);
            }
            using (Bitmap b = new Bitmap(300, 200, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.SeaGreen);
                b.Save(Path.Combine(sub, "f1.png"), ImageFormat.Png);
                b.Save(Path.Combine(sub, "f2.jpg"), ImageFormat.Jpeg);
                b.Save(Path.Combine(sub, "f3.bmp"), ImageFormat.Bmp);
            }

            Form srcForm = new Form();
            srcForm.FormBorderStyle = FormBorderStyle.None;
            srcForm.StartPosition = FormStartPosition.Manual;
            srcForm.Bounds = new Rectangle(700, 100, 380, 220);
            srcForm.BackColor = Color.FromArgb(40, 44, 52);
            srcForm.Show();
            Point from = new Point(srcForm.Left + 190, srcForm.Top + 110);

            Form ctrl = new Form();
            ctrl.FormBorderStyle = FormBorderStyle.None;
            ctrl.StartPosition = FormStartPosition.Manual;
            ctrl.Bounds = new Rectangle(700, 380, 380, 220);
            ctrl.BackColor = Color.DarkSlateBlue;
            ctrl.AllowDrop = true;
            int cOvers = 0, cDrops = 0;
            ctrl.DragEnter += new DragEventHandler(delegate(object o, DragEventArgs e) { cOvers++; e.Effect = DragDropEffects.Copy; });
            ctrl.DragOver += new DragEventHandler(delegate(object o, DragEventArgs e) { e.Effect = DragDropEffects.Copy; });
            ctrl.DragDrop += new DragEventHandler(delegate(object o, DragEventArgs e) { cDrops++; });
            ctrl.Show();
            Point toCtrl = new Point(ctrl.Left + 190, ctrl.Top + 110);

            Settings s = new Settings();
            s.SaveToDisk = false;
            WheelManager mgr = new WheelManager(s);
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Pump(900);

            Console.WriteLine("源 {0}  对照组 {1}  轮盘 {2}@{3} {4}",
                srcForm.Bounds, ctrl.Bounds, f.Visible ? "可见" : "不可见", f.Location, f.Size);

            Console.WriteLine();
            Console.WriteLine("--- 第 0 步：对照组（普通 Form）能否收到拖放（仅探测模拟能力，不计分）---");
            DataObject cd = new DataObject();
            cd.SetData(DataFormats.FileDrop, new string[] { png });
            Drag(srcForm, cd, from, toCtrl, 3000);
            Console.WriteLine("  对照组 Enter={0} Drop={1}  {2}", cOvers, cDrops,
                cDrops > 0 ? "（模拟拖拽可用）" : "（模拟拖拽在本机不生效，下面改用直接调用处理器验证）");

            Console.WriteLine();
            Console.WriteLine("--- 第 1 步：拖到轮盘（真实 OLE 模拟）---");
            Func<Point> keyPt = delegate()
            {
                Rectangle kr = (Rectangle)Call(f, "KeyRect");
                return new Point(f.Left + kr.X + kr.Width / 2, f.Top + kr.Y + kr.Height / 2);   // 屏幕坐标
            };
            Func<Point> ringPt = delegate()
            {
                MethodInfo m = f.GetType().GetMethod("ItemCenterAtPhiRadius", BindingFlags.NonPublic | BindingFlags.Instance);
                PointF p = (PointF)m.Invoke(f, new object[] { (float)(Math.PI / 4), (float)Call(f, "EffR") });
                return new Point(f.Left + (int)p.X, f.Top + (int)p.Y);                        // 屏幕坐标
            };
            Func<Point> cornerPt = delegate() { return new Point(f.Right - 12, f.Top + 12); };

            string[][] sets = {
                new string[] { png }, new string[] { jpg }, new string[] { ico },
                new string[] { png, jpg }, new string[] { sub }
            };
            string[] names = { "单个 PNG", "单个 JPG", "ICO", "多选 PNG+JPG", "整个文件夹" };
            Func<Point>[] pts = { keyPt, ringPt, ringPt, ringPt, ringPt };

            if (cDrops > 0)
            {
                for (int i = 0; i < sets.Length; i++)
                {
                    int before = st.Items.Count;
                    DataObject d = new DataObject();
                    d.SetData(DataFormats.FileDrop, sets[i]);
                    Point p = pts[i]();
                    Console.WriteLine("  · {0} -> ({1},{2})  当前 {3} 张", names[i], p.X, p.Y, before);
                    Drag(srcForm, d, from, p, 3000);
                    bool ok = st.Items.Count > before;
                    Console.WriteLine("    {0} store {1} -> {2}", ok ? "OK  " : "FAIL", before, st.Items.Count);
                    if (ok) pass++; else fail++;
                }
            }
            else
            {
                while (st.Items.Count > 0) st.Items.RemoveAt(0);
                Console.WriteLine("  （跳过真实 OLE，见第 1b 步）");
            }

            Console.WriteLine();
            Console.WriteLine("--- 第 1b 步：直接调用拖放处理器（验证逻辑本身）---");
            while (st.Items.Count > 0) st.Items.RemoveAt(0);
            // 窗口任意位置都应该收外部图片
            Point[] targets = { keyPt(), ringPt(), cornerPt(), new Point(f.Left + 20, f.Top + 20) };
            string[] tnames = { "摇杆键", "环带", "窗口右上空白", "窗口左上空白" };
            for (int i = 0; i < targets.Length; i++)
            {
                int before = st.Items.Count;
                DataObject d = new DataObject();
                d.SetData(DataFormats.FileDrop, new string[] { png });
                DragDropEffects eff = FireDrop(f, d, targets[i]);
                bool ok = st.Items.Count == before + 1 && eff == DragDropEffects.Copy;
                Console.WriteLine("  {0} {1} -> store {2}->{3} effect={4}", ok ? "OK  " : "FAIL", tnames[i], before, st.Items.Count, eff);
                if (ok) pass++; else fail++;
            }

            // 各种格式 + 文件夹 + 多选
            for (int i = 0; i < sets.Length; i++)
            {
                int before = st.Items.Count;
                int want = sets[i].Length == 1 && sets[i][0] == sub ? 3 : sets[i].Length;
                DataObject d = new DataObject();
                d.SetData(DataFormats.FileDrop, sets[i]);
                FireDrop(f, d, ringPt());
                int added = st.Items.Count - before;
                bool ok = added == want;
                Console.WriteLine("  {0} {1}：期望 +{2}，实际 +{3}", ok ? "OK  " : "FAIL", names[i], want, added);
                if (ok) pass++; else fail++;
            }

            // 位图数据（从网页/别的程序直接拖一张图）
            {
                int before = st.Items.Count;
                Bitmap srcBmp = new Bitmap(120, 90);
                DataObject d = new DataObject();
                d.SetData(DataFormats.Bitmap, true, (object)srcBmp);
                object got = null;
                try { got = d.GetData(DataFormats.Bitmap); } catch (Exception ex) { got = "异常:" + ex.Message; }
                Console.WriteLine("      （DataObject.GetData(Bitmap) 返回 {0}）", got == null ? "null" : got.GetType().FullName);
                DragDropEffects eff = FireDrop(f, d, ringPt());
                bool ok = st.Items.Count == before + 1;
                Console.WriteLine("  {0} 直接拖位图（网页里的图）-> +{1} effect={2}", ok ? "OK  " : "FAIL", st.Items.Count - before, eff);
                if (ok) pass++; else fail++;
                srcBmp.Dispose();
            }

            // 新加入的必须带滑入动画（_enterT0 里要有它）
            {
                Dictionary<StoreItem, DateTime> enter = (Dictionary<StoreItem, DateTime>)f.GetType()
                    .GetField("_enterT0", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f);
                StoreItem last = st.Items[st.Items.Count - 1];
                bool ok = enter.ContainsKey(last);
                Console.WriteLine("  {0} 最后加入的那张有滑入动画标记", ok ? "OK  " : "FAIL");
                if (ok) pass++; else fail++;
            }

            Console.WriteLine();
            Console.WriteLine("--- 第 1c 步：把轮盘里的图拖出去再拖回来（内部拖拽）---");
            {
                int before = st.Items.Count;
                StoreItem it = st.Items[st.Items.Count - 1];
                string file = Path.Combine(dir, "back.png");
                using (Bitmap b = new Bitmap(160, 120)) { using (Graphics g = Graphics.FromImage(b)) g.Clear(Color.Orange); b.Save(file, ImageFormat.Png); }
                DataObject d = new DataObject();
                d.SetData("SnapWheelMove", 1);                       // 内部拖拽标记
                d.SetData(DataFormats.FileDrop, new string[] { file });
                FireOver(f, d, ringPt());
                DragDropEffects eff = FireDrop(f, d, ringPt());
                bool kept = st.Items.Contains(it);
                bool anim = ((Dictionary<StoreItem, DateTime>)f.GetType()
                    .GetField("_enterT0", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f)).ContainsKey(it);
                bool ok = eff == DragDropEffects.Move && kept && anim && st.Items.Count == before;
                Console.WriteLine("  {0} 拖回来 -> effect={1} 还在={2} 有滑入动画={3} 数量={4}->{5}",
                    ok ? "OK  " : "FAIL", eff, kept, anim, before, st.Items.Count);
                if (ok) pass++; else fail++;
            }

            Console.WriteLine();
            Console.WriteLine("--- 第 2 步：不该收的情况 ---");
            int b1 = st.Items.Count;
            File.WriteAllText(Path.Combine(dir, "nope.txt"), "not image");
            DataObject bad = new DataObject();
            bad.SetData(DataFormats.FileDrop, new string[] { Path.Combine(dir, "nope.txt") });
            FireDrop(f, bad, ringPt());
            Console.WriteLine("  {0} 非图片不收（{1} -> {2}）", st.Items.Count == b1 ? "OK  " : "FAIL", b1, st.Items.Count);
            if (st.Items.Count == b1) pass++; else fail++;

            int b2 = st.Items.Count;
            DataObject d2 = new DataObject();
            d2.SetData(DataFormats.FileDrop, new string[] { png });
            DragDropEffects emptyEff = FireDrop(f, d2, new Point(f.Right - 10, f.Top + 10));
            Console.WriteLine("  提示：窗口空白处现在也接收（effect={0}，{1} -> {2}）", emptyEff, b2, st.Items.Count);
            if (st.Items.Count == b2 + 1) pass++; else fail++;

            Console.WriteLine();
            Console.WriteLine("最终 store: {0} 张", st.Items.Count);
            foreach (StoreItem it in st.Items) Console.WriteLine("   {0}x{1}", it.Image.Width, it.Image.Height);

            try { f.HideWheel(); f.Close(); } catch { }
            try { srcForm.Close(); ctrl.Close(); } catch { }
            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.Exit(fail == 0 ? 0 : 1);
          }
          catch (Exception ex)
          {
              Console.WriteLine("!! 测试自身异常: " + ex.GetType().Name + ": " + ex.Message);
              Console.WriteLine(ex.StackTrace);
              Environment.Exit(2);
          }
        }
    }
}
