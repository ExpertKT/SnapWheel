// 探针：看看在轮盘那些点上，系统认为“最顶上的窗口”到底是谁
// 编译：csc /nologo /target:exe /main:SnapWheel.ProbeTest /out:probe.exe src\*.cs tests\probe-test.cs
using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SnapWheel
{
    static class ProbeTest
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
        [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);

        static string Info(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(null)";
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            uint pid; GetWindowThreadProcessId(h, out pid);
            IntPtr root = GetAncestor(h, 2);
            StringBuilder sb2 = new StringBuilder(256);
            GetClassName(root, sb2, 256);
            return string.Format("hwnd=0x{0:X} class={1} pid={2} ex=0x{3:X8} | root=0x{4:X} class={5}",
                h.ToInt64(), sb, pid, GetWindowLong(h, -20), root.ToInt64(), sb2);
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Settings s = new Settings();
            s.SaveToDisk = false;
            WheelManager mgr = new WheelManager(s);
            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            DateTime end = DateTime.Now.AddMilliseconds(1200);
            while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(10); }

            Console.WriteLine("轮盘窗口 handle=0x{0:X}  rect={1}  visible={2}", f.Handle.ToInt64(), f.Bounds, f.Visible);

            MethodInfo keyRect = typeof(WheelForm).GetMethod("KeyRect", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo atPhi = typeof(WheelForm).GetMethod("ItemCenterAtPhiRadius", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo effR = typeof(WheelForm).GetMethod("EffR", BindingFlags.NonPublic | BindingFlags.Instance);
            Rectangle kr = (Rectangle)keyRect.Invoke(f, null);
            PointF ring = (PointF)atPhi.Invoke(f, new object[] { (float)(Math.PI / 4), (float)effR.Invoke(f, null) });

            Point[] pts = {
                new Point(kr.X + kr.Width/2, kr.Y + kr.Height/2),
                new Point((int)ring.X, (int)ring.Y),
                new Point(f.Left + 20, f.Top + 20),
                new Point(f.Left + f.Width/2, f.Top + f.Height/2)
            };
            string[] names = { "摇杆键中心", "环带45度", "窗口左上角", "窗口正中" };

            for (int i = 0; i < pts.Length; i++)
            {
                POINT p = new POINT();
                p.x = f.Left + pts[i].X; p.y = f.Top + pts[i].Y;
                IntPtr h = WindowFromPoint(p);
                bool mine = (h == f.Handle) || (GetAncestor(h, 2) == f.Handle);
                Console.WriteLine("  {0}  屏幕({1},{2}) -> {3} {4}", names[i], p.x, p.y, Info(h), mine ? "[轮盘自己]" : "[!! 不是轮盘]");
            }

            // 用 SetLayeredWindowAttributes 关掉 alpha 命中测试会怎样？先看看现在有没有 alpha
            Console.WriteLine();
            Console.WriteLine("提示：分层窗口的命中测试是按 alpha 走的，alpha=0 的地方系统会当作透明跳过。");
            f.HideWheel();
            f.Close();
        }
    }
}
