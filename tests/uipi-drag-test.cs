// 模拟“资源管理器把文件拖到轮盘上”：本程序要以普通权限(Medium)运行，
// 通过 explorer.exe 拉起即可（explorer 是 Medium，子进程也是 Medium）。
// 结果写到 %TEMP%\uidrag.log
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class UIDrag
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr h, IntPtr p);
    const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, MOVE = 0x0001;

    static StreamWriter log;
    static void L(string s) { log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s); log.Flush(); }

    static Rectangle FindWheel()
    {
        Rectangle best = Rectangle.Empty;
        uint[] pids;
        var list = new System.Collections.Generic.List<uint>();
        foreach (Process p in Process.GetProcesses())
            if (p.ProcessName.IndexOf("SnapWheel", StringComparison.OrdinalIgnoreCase) >= 0) list.Add((uint)p.Id);
        pids = list.ToArray();
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            bool want = false;
            foreach (uint q in pids) if (q == pid) want = true;
            if (!want || !IsWindowVisible(h)) return true;
            RECT r; GetWindowRect(h, out r);
            int w = r.R - r.L, hh = r.B - r.T;
            if (w > 300 && hh > 300 && w < 1200) { best = new Rectangle(r.L, r.T, w, hh); return false; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    [STAThread]
    static void Main()
    {
        log = new StreamWriter(Path.Combine(Path.GetTempPath(), "uidrag.log"), false, Encoding.UTF8);
        log.AutoFlush = true;
        try
        {
            string testPng = Path.Combine(Path.GetTempPath(), "uidrag_red.png");
            using (Bitmap b = new Bitmap(240, 240, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.Red); g.FillEllipse(Brushes.Yellow, 40, 40, 160, 160); }
                b.Save(testPng, ImageFormat.Png);
            }
            L("测试图: " + testPng);

            Rectangle wr = FindWheel();
            L("找到轮盘窗口: " + wr);
            if (wr.IsEmpty) { L("没找到轮盘窗口，先让轮盘显示出来"); return; }

            Form src = new Form();
            src.FormBorderStyle = FormBorderStyle.None;
            src.StartPosition = FormStartPosition.Manual;
            src.Bounds = new Rectangle(900, 120, 300, 200);
            src.BackColor = Color.FromArgb(30, 30, 36);
            src.Show();
            Application.DoEvents();
            Thread.Sleep(200);

            Point from = new Point(src.Left + 150, src.Top + 100);
            // 轮盘窗口的正中间（放宽后整窗都收）
            Point to = new Point(wr.Left + wr.Width / 2, wr.Top + wr.Height - 120);
            L("拖拽: " + from + " -> " + to);

            SetCursorPos(from.X, from.Y);
            Thread.Sleep(100);
            mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(10); }

            Thread mover = new Thread(delegate()
            {
                try
                {
                    Thread.Sleep(220);
                    SetCursorPos(to.X, to.Y);
                    mouse_event(MOVE, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(350);
                    mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
                }
                catch { }
            });
            mover.IsBackground = true; mover.Start();

            DataObject data = new DataObject();
            data.SetData(DataFormats.FileDrop, new string[] { testPng });
            Stopwatch sw = Stopwatch.StartNew();
            DragDropEffects eff = src.DoDragDrop(data, DragDropEffects.Copy);
            sw.Stop();
            mover.Join(800);
            L("DoDragDrop 返回 " + eff + "，耗时 " + sw.ElapsedMilliseconds + "ms");
            for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(10); }
            src.Close();
            SetCursorPos(1800, 900);       // 把鼠标挪开，别挡着截图
            L("完成");
        }
        catch (Exception ex) { L("异常: " + ex); }
        finally { log.Close(); }
    }
}
