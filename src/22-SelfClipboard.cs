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
using System.Windows.Forms;
using Microsoft.Win32;
namespace SnapWheel
{
    // ============================ "这张剪贴板是我们自己写的"登记簿 ============================
    // 起因（用户实测到的回归）：截图确认时会把成品图同时复制到剪贴板，而轮盘那边一直挂着
    // 剪贴板监听（WheelForm 的 WM_CLIPBOARDUPDATE → OnClipboardChanged，设置项 ClipboardImport）。
    // 于是**自己写的图被自己的监听当成"外面复制的新图"又收了一盘** —— 截一次图，轮盘上出现两张缩略图
    // （App 里本来就有一句 st.Add(ov.Result)，监听再收一张就是第二张）。
    //
    // 谁往剪贴板写图，谁先来这里登记（Note）；监听那边先比对是不是同一张（IsOurs），是就跳过。
    // 故意**不用**"写完 N 毫秒内忽略"那种时间窗：它会把用户在这段时间里真正复制的一张图也一起吞掉，
    // 而且窗口一过就失效（截图浮层是模态的，消息什么时候被泵到并不确定）。这里比的是"图长什么样"。
    //
    // 只跳过一次：命中就清登记；不命中（剪贴板已经被别的东西替换了）也清 —— 不让登记长期挂着屏蔽别人。
    static class SelfClipboard
    {
        static string _fp = "";                            // 登记时的指纹（尺寸 + 采样像素），空 = 没有登记
        static int _w, _h;                                 // 登记时的尺寸（跟着指纹一起记，方便诊断）
        static DateTime _at = DateTime.MinValue;           // 登记时刻：只作为记录/诊断，**不参与判定**

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

        // 写剪贴板之前调用：把"我要写的这张图"记下来
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

        // 剪贴板监听到一张图时调用：是不是我们自己刚写的那张？
        // 命中 → true（这一次跳过导入），并且**立刻清掉登记** —— 只跳过一次。
        // 不命中 → false，同样清掉登记（剪贴板已经被别的东西替换，这条登记过期了）。
        public static bool IsOurs(string fp)
        {
            try
            {
                if (_fp.Length == 0 || fp == null || fp.Length == 0) return false;
                return fp == _fp;
            }
            finally { Clear(); }
        }

        public static void Clear() { _fp = ""; _w = _h = 0; _at = DateTime.MinValue; }
    }
}
