using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
namespace SnapWheel
{
    // ==================== "这张剪贴板是我们自己写的"登记簿 + 往剪贴板写图的唯一入口 ====================
    //
    // 两件事，都是被同一个功能逼出来的（截图"同时复制到剪贴板"）：
    //
    // 1) 别把自己的图又收一盘。轮盘一直挂着剪贴板监听（WheelForm 的 WM_CLIPBOARDUPDATE →
    //    OnClipboardChanged，设置项 ClipboardImport 默认开），我们自己写进去的成品图会被它
    //    当成"外面复制的新图"再收一次 —— 截一次图出两张缩略图。谁写谁登记，监听先比对。
    //    故意**不用**"写完 N 毫秒内忽略"那种时间窗：它会把用户这段时间里真正复制的一张图也吞掉，
    //    而且窗口一过就失效（截图浮层是模态的，消息什么时候被泵到并不确定）。
    //
    // 2) 别在 UI 线程上写。一张 1600x1000 的图，PNG 编码 ~35ms + OLE 把 Bitmap 刷成 DIB ~45ms
    //    —— 实测整条路径在 UI 线程上要 80ms，落在"缩略图滑入"的帧上就是一帧 50ms
    //    （探针实测：基线每帧 2.4ms，写剪贴板那一帧 50.2ms；丢到工作线程后最慢 10.3ms）。
    //    所以写入交给一个**专用 STA 工作线程**（OLE 剪贴板只能在 STA 上碰），UI 线程只付一次
    //    位图拷贝（实测 ~7ms）—— 这次拷贝是必须的：轮盘那几帧正拿着同一张 GDI+ 位图在画缩略图，
    //    后台线程再去编码它，探针里直接撞出"对象当前正在其他地方使用"。
    //
    // 判定用两道，先便宜后兜底：
    //   ① 剪贴板序号（GetClipboardSequenceNumber）：没变就是我们自己刚写的那一下 → 直接跳过，
    //      **连图都不用读**（读一张 1600x1000 要 ~10ms，也落在动画帧上）；
    //   ② 指纹（尺寸 + 11 个采样点）：序号对不上时兜底 —— 这一步在"导入外部图"那条路径上本来
    //      就要读图，所以不额外花钱。
    // 两道都"只跳过一次"：命中即清；不命中（剪贴板已被别的东西替换）也清，绝不长期屏蔽。
    static class SelfClipboard
    {
        static string _fp = "";                            // 登记时算好的指纹（尺寸 + 采样像素），空 = 没有登记
        static int _w, _h;                                 // 登记时的尺寸（跟着指纹一起记，方便诊断）
        static DateTime _at = DateTime.MinValue;           // 登记时刻：只作为记录/诊断，**不参与判定**
        static long _seq = 0;                              // 我们自己写完之后剪贴板的序号（0 = 没有登记）
        static Thread _writer = null;                      // 正在后台写剪贴板的那条线程（测试/收尾要等它）

        public static bool Pending { get { return _fp.Length > 0; } }
        public static int NoteWidth { get { return _w; } }
        public static int NoteHeight { get { return _h; } }
        public static DateTime NoteAt { get { return _at; } }

        // 便宜的指纹：尺寸 + 采样若干像素。
        // 只比尺寸不够 —— "外面复制一张同样大小的图"会被误判成自己写的那张（用户点名要能区分）；
        // 所以采样点是四角 + 中心 + 四个 1/4 点 + 两个 1/3 点，11 个 GetPixel，够便宜也够准。
        internal static string Fingerprint(Image im)
        {
            Bitmap b = im as Bitmap;
            bool own = false;
            try
            {
                if (im == null || im.Width <= 0 || im.Height <= 0) return "";
                if (b == null) { b = new Bitmap(im); own = true; }
                int w = b.Width, h = b.Height;
                int[] xs = { 0, w - 1, 0, w - 1, w / 2, w / 4, w * 3 / 4, w / 4, w * 3 / 4, w / 3, w * 2 / 3 };
                int[] ys = { 0, 0, h - 1, h - 1, h / 2, h / 4, h / 4, h * 3 / 4, h * 3 / 4, h / 2, h / 2 };
                StringBuilder sb = new StringBuilder(16 + xs.Length * 8);
                sb.Append(w).Append('x').Append(h).Append(':');
                for (int i = 0; i < xs.Length; i++)
                {
                    int x = xs[i] < 0 ? 0 : (xs[i] > w - 1 ? w - 1 : xs[i]);
                    int y = ys[i] < 0 ? 0 : (ys[i] > h - 1 ? h - 1 : ys[i]);
                    sb.Append(b.GetPixel(x, y).ToArgb().ToString("X8"));
                }
                return sb.ToString();
            }
            catch { return ""; }
            finally { if (own && b != null) { try { b.Dispose(); } catch { } } }
        }

        // 写剪贴板之前调用：把"我要写的这张图"记下来（指纹 + 尺寸 + 时刻）
        public static void Note(Image im)
        {
            try
            {
                _fp = Fingerprint(im);
                _w = im == null ? 0 : im.Width;
                _h = im == null ? 0 : im.Height;
                _at = DateTime.Now;
            }
            catch { _fp = ""; _w = _h = 0; _at = DateTime.MinValue; }
        }

        // 写完之后记下"这一下把剪贴板序号推到了多少"（工作线程/同步写完之后调用）
        public static void NoteSequence()
        {
            try { _seq = Native.GetClipboardSequenceNumber(); }
            catch { _seq = 0; }
        }

        // ---- 第一道（便宜）：序号没变 = 还是我们自己刚写的那一下 ----
        // 命中 → true 并把登记里那张图的指纹带出去（给 _lastClipFp 去重用，省得再读一次图），同时清登记。
        public static bool TakeBySequence(out string fp)
        {
            fp = null;
            long s = _seq;
            if (s == 0 || _fp.Length == 0) return false;        // 没登记：直接用图去比（第二道）
            uint now;
            try { now = Native.GetClipboardSequenceNumber(); } catch { return false; }
            if (now == 0 || now != s) return false;              // 剪贴板已经被别的东西动过 → 交给第二道去判断
            fp = _fp;
            Clear();
            return true;
        }

        // ---- 第二道（兜底）：拿剪贴板里那张图的指纹来比 ----
        // 命中 → true（这一次跳过导入）并且清登记（只跳过一次）；
        // 不命中 → false，同样清登记（剪贴板已经被别的东西替换，这条登记过期了）。
        public static bool IsOurs(string fp)
        {
            try
            {
                if (_fp.Length == 0 || fp == null || fp.Length == 0) return false;
                return fp == _fp;
            }
            finally { Clear(); }
        }

        public static void Clear()
        {
            _fp = ""; _w = _h = 0; _at = DateTime.MinValue; _seq = 0;
        }

        // ============================ 往剪贴板写图（唯一入口） ============================
        // UI 线程调用：登记指纹 → 拷一份 → 交给专用 STA 工作线程去写（PNG 编码与 OLE flush 都在那边）。
        // 失败只写日志、绝不弹框（截图流程不能被剪贴板打断）。
        public static void BeginWrite(Image src)
        {
            try
            {
                Note(src);                          // 先登记（指纹是原图的；拷贝出来像素一模一样，比对得上）
                Bitmap copy = new Bitmap(src);      // UI 线程只付这一次拷贝（1600x1000 实测 ~7ms）
                Thread t = new Thread(delegate () { Write(copy); });
                t.SetApartmentState(ApartmentState.STA);   // OLE 剪贴板只能在 STA 线程上碰
                t.IsBackground = true;
                _writer = t;
                t.Start();
            }
            catch (Exception ex) { Err.Log("SelfClipboard.BeginWrite", ex); }
        }

        // 工作线程里的正事：一次把三种格式放上去，并立刻持久化（copy=true）
        static void Write(Bitmap img)
        {
            try
            {
                //   Bitmap / DIB —— 画图、Word、微信这些"粘贴图片"走的就是这两个；
                //   PNG        —— 认这个格式的程序（浏览器、部分编辑器/截图工具）能拿到
                //                 带 alpha 的无损原图，而且不会像 DIB 那样掉透明通道。
                DataObject data = new DataObject();
                data.SetImage(img);
                using (MemoryStream png = new MemoryStream())
                {
                    img.Save(png, ImageFormat.Png);
                    png.Position = 0;                     // 交给剪贴板前把读指针拨回开头
                    data.SetData("PNG", false, png);      // false = 原样给字节流，别自动转成 .NET 对象
                    // copy=true：立刻把数据刷进剪贴板（OleFlushClipboard），
                    // 所以这个 MemoryStream（以及拷出来的位图）之后被释放，粘贴方照样能拿到完整 PNG，
                    // 程序退出后剪贴板里也还在。
                    Clipboard.SetDataObject(data, true);
                }
                NoteSequence();                           // 记下写完之后剪贴板的序号（监听的便宜判据）
            }
            catch (Exception ex) { Err.Log("SelfClipboard.Write", ex); }
            finally { try { img.Dispose(); } catch { } }   // 拷出来的那一份，写完就还
        }

        // 等后台那次写剪贴板收工（测试、以及"要立刻读剪贴板"的地方用；正常流程没人等它）
        public static bool WaitIdle(int ms)
        {
            Thread t = _writer;
            if (t == null) return true;
            try { return t.Join(ms); } catch { return false; }
        }
    }
}
