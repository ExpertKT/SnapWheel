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
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int GetDpiForSystem();
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        // 0.6.0 滚动长截图：给目标窗口发合成的滚轮消息（这是产品功能，不是测试里的模拟输入）
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        public const uint WM_MOUSEWHEEL = 0x020A;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        // 剪贴板序号：剪贴板内容一变这个计数器就往前走（系统给的全局值，同一会话里所有进程共享）。
        // 用它判断"这次 WM_CLIPBOARDUPDATE 是不是我们自己刚写出来的那一下"，比把整张图读回来算指纹便宜得多
        // （读一张 1600x1000 实测 ~10ms，正好落在"缩略图滑入"那几帧上）。返回 0 表示读不到，调用方要兜底。
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();

        // 优先"每显示器 DPI 感知 v2"：多屏不同缩放时不会把窗口拉伸糊掉，坐标也按物理像素走
        public static void SetDpiAwarenessBest()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }   // PER_MONITOR_AWARE_V2
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }                  // PER_MONITOR_DPI_AWARE
            try { SetProcessDPIAware(); } catch { }                                        // 退回到系统 DPI 感知
        }

        // 当前进程的缩放比例（1.0 = 96dpi）
        public static float DpiScaleOf(IntPtr hwnd)
        {
            try
            {
                uint d = 0;
                if (hwnd != IntPtr.Zero) d = GetDpiForWindow(hwnd);
                if (d == 0) d = (uint)GetDpiForSystem();
                if (d == 0) d = 96;
                return d / 96f;
            }
            catch { return 1f; }
        }
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;
        public const int HOTKEY_ID = 0x5A01;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x; public int y; public POINT(int X, int Y) { x = X; y = Y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx; public int cy; public SIZE(int X, int Y) { cx = X; cy = Y; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize; public int biWidth; public int biHeight;
            public short biPlanes; public short biBitCount; public int biCompression; public int biSizeImage;
            public int biXPelsPerMeter; public int biYPelsPerMeter; public int biClrUsed; public int biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }

        public const int ULW_ALPHA = 0x02;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string str);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);

        public static void PushLayered(Form f, Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBmp);
            SIZE size = new SIZE(bmp.Width, bmp.Height);
            POINT src = new POINT(0, 0);
            POINT dst = new POINT(f.Left, f.Top);
            BLENDFUNCTION bf = new BLENDFUNCTION();
            bf.BlendOp = AC_SRC_OVER; bf.BlendFlags = 0; bf.SourceConstantAlpha = 255; bf.AlphaFormat = AC_SRC_ALPHA;
            UpdateLayeredWindow(f.Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, ULW_ALPHA);
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
