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
    static class Gfx
    {
        // 弹框显示后强制整窗重绘一次：自定义绘制的按钮第一帧容易取到还没定下来的父底色，
        // 四角会闪一下白块（鼠标划过去才恢复）。这里补一次完整重绘，用户看不到那一帧。
        public static void RepaintAll(Form f)
        {
            try
            {
                f.Invalidate(true);
                f.Update();
                foreach (Control c in f.Controls) c.Invalidate();
                f.Update();
            }
            catch { }
        }
        // f > 0 往白里混，f < 0 往黑里混（用来做拟物高光/暗部）
        public static Color Shade(Color c, float f)
        {
            if (f >= 0f)
            {
                return Color.FromArgb(c.A,
                    (int)Math.Round(c.R + (255 - c.R) * f),
                    (int)Math.Round(c.G + (255 - c.G) * f),
                    (int)Math.Round(c.B + (255 - c.B) * f));
            }
            float t = 1f + f;      // f=-0.4 -> 0.6
            return Color.FromArgb(c.A, (int)Math.Round(c.R * t), (int)Math.Round(c.G * t), (int)Math.Round(c.B * t));
        }

        public static Color A(Color c, int a)
        {
            if (a < 0) a = 0; if (a > 255) a = 255;
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        public static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        // 缓动：先快后慢，收尾很稳
        public static float EaseOut(float t)
        {
            t = Clamp01(t);
            return 1f - (1f - t) * (1f - t) * (1f - t);
        }

        // 缓动：两头慢中间快（滑入用这个最顺）
        public static float EaseInOut(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        // 回弹一点点的收尾，显得有"弹性"
        public static float EaseBack(float t)
        {
            t = Clamp01(t);
            float s = 1.70158f;
            float u = t - 1f;
            return u * u * ((s + 1f) * u + s) + 1f;
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 0.5f) { p.AddRectangle(r); return p; }        // 圆角为 0 时别画成椭圆
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---- 新拟态 / 毛玻璃 基元 ----

        // 玻璃面板：竖向微渐变底 + 顶部内侧高光 + 底部内侧暗边（新拟态的关键就是这一上一下）
        public static void GlassPanel(Graphics g, GraphicsPath path, RectangleF r, Color fill,
                                      int topHi, int bottomShade, bool verticalGradient)
        {
            if (verticalGradient && r.Height > 2f)
            {
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new RectangleF(r.X, r.Y - 1f, r.Width, r.Height + 2f),
                    Shade(fill, 0.10f), Shade(fill, -0.10f), LinearGradientMode.Vertical))
                    g.FillPath(lg, path);
            }
            else using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);

            if (topHi > 0)
            {
                // 上半圈高光（浅色描边，像光从左上打过来）
                using (Pen hi = new Pen(Color.FromArgb(topHi, 255, 255, 255), 1.2f))
                {
                    hi.StartCap = LineCap.Round; hi.EndCap = LineCap.Round;
                    g.DrawPath(hi, path);
                }
            }
            if (bottomShade > 0)
            {
                using (Pen sh = new Pen(Color.FromArgb(bottomShade, 0, 0, 0), 1.4f))
                {
                    sh.StartCap = LineCap.Round; sh.EndCap = LineCap.Round;
                    g.DrawPath(sh, path);
                }
            }
        }

        // 新拟态圆钮：外凸（亮边在左上，暗边在右下）或内凹（反过来的 hover / 按下态）
        public static void NeuCircle(Graphics g, RectangleF r, Color surface, Color accent,
                                     bool primary, bool pressed, int hiA, int shA)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(r);
                Color baseC = primary ? accent : surface;
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new RectangleF(r.X, r.Y - 1f, r.Width, r.Height + 2f),
                    primary ? Shade(baseC, 0.22f) : Shade(baseC, 0.14f),
                    primary ? Shade(baseC, -0.20f) : Shade(baseC, -0.16f),
                    LinearGradientMode.Vertical))
                    g.FillPath(lg, p);

                if (!pressed)
                {
                    // 左上高光
                    using (Pen hi = new Pen(Color.FromArgb(hiA, 255, 255, 255), 1.4f))
                        g.DrawArc(hi, r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f, 175f, 130f);
                }
                // 右下暗边（内凹感）
                using (Pen sh = new Pen(Color.FromArgb(shA, 0, 0, 0), 1.6f))
                    g.DrawArc(sh, r.X + 0.6f, r.Y + 0.6f, r.Width - 1.2f, r.Height - 1.2f, -5f, 130f);
            }
        }

        // 柔和外阴影（新拟态的"浮起来"感）：画几层递减的圆角轮廓
        public static void SoftShadow(Graphics g, GraphicsPath path, int strength, float spread)
        {
            if (strength <= 2) return;
            for (int i = 3; i >= 1; i--)
            {
                int a = (int)(strength * (0.10f + 0.06f * (3 - i)) / 3f);
                if (a <= 0) continue;
                using (Pen p = new Pen(Color.FromArgb(a, 0, 0, 0), i * spread))
                { p.LineJoin = LineJoin.Round; g.DrawPath(p, path); }
            }
        }

        // fit the whole image inside dest, preserving aspect (letterbox)
        public static RectangleF FitContain(Size img, RectangleF dest)
        {
            float s = Math.Min(dest.Width / img.Width, dest.Height / img.Height);
            float w = img.Width * s, h = img.Height * s;
            return new RectangleF(dest.X + (dest.Width - w) / 2f, dest.Y + (dest.Height - h) / 2f, w, h);
        }
    }

    static class Blur
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WINCOMPATTRDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [StructLayout(LayoutKind.Sequential)]
        struct ACCENTPOLICY { public int AccentState; public int AccentFlags; public int GradientColor; public int AnimationId; }

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

        const int ACCENT_DISABLED = 0;
        const int ACCENT_ENABLE_BLURBEHIND = 3;
        const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        const int WCA_ACCENT_POLICY = 19;

        public static bool Supported
        {
            get
            {
                try { return Environment.OSVersion.Version.Major >= 10 && TransparencyOn; }
                catch { return false; }
            }
        }

        // 系统里把"透明效果"关掉时 acrylic 不生效 —— 那种情况下再画半透明底会变成
        // "玻璃没了、却透着一层桌面"，字看不清，所以直接退回不透明底
        public static bool TransparencyOn
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        if (k == null) return true;
                        object v = k.GetValue("EnableTransparency");
                        if (v == null) return true;
                        return Convert.ToInt32(v) != 0;
                    }
                }
                catch { return true; }
            }
        }

        // tint = ABGR 颜色（GradientColor 是 0xAABBGGRR）
        public static bool Apply(IntPtr hwnd, int a, int r, int g, int b, bool acrylic)
        {
            try
            {
                ACCENTPOLICY ap = new ACCENTPOLICY();
                ap.AccentState = acrylic ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_BLURBEHIND;
                ap.AccentFlags = 2;                     // 四边都画
                ap.GradientColor = (a << 24) | (b << 16) | (g << 8) | r;
                WINCOMPATTRDATA d = new WINCOMPATTRDATA();
                d.Attribute = WCA_ACCENT_POLICY;
                d.Data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ACCENTPOLICY)));
                Marshal.StructureToPtr(ap, d.Data, false);
                d.SizeOfData = Marshal.SizeOf(typeof(ACCENTPOLICY));
                int hr = SetWindowCompositionAttribute(hwnd, ref d);
                Marshal.FreeHGlobal(d.Data);
                return hr != 0;
            }
            catch { return false; }
        }

        public static void Clear(IntPtr hwnd)
        {
            try
            {
                ACCENTPOLICY ap = new ACCENTPOLICY();
                ap.AccentState = ACCENT_DISABLED;
                WINCOMPATTRDATA d = new WINCOMPATTRDATA();
                d.Attribute = WCA_ACCENT_POLICY;
                d.Data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ACCENTPOLICY)));
                Marshal.StructureToPtr(ap, d.Data, false);
                d.SizeOfData = Marshal.SizeOf(typeof(ACCENTPOLICY));
                SetWindowCompositionAttribute(hwnd, ref d);
                Marshal.FreeHGlobal(d.Data);
            }
            catch { }
        }
    }

    static class Brand
    {
        static System.Drawing.Icon _icon;
        public static System.Drawing.Icon Get()
        {
            if (_icon != null) return _icon;
            try
            {
                Bitmap b = new Bitmap(32, 32);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(0, 122, 204)))
                        g.FillEllipse(bg, 0, 0, 31, 31);
                    using (Pen p = new Pen(Color.White, 3f))
                    {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        g.DrawLines(p, new PointF[] { new PointF(10, 6), new PointF(6, 6), new PointF(6, 10) });
                        g.DrawLines(p, new PointF[] { new PointF(22, 6), new PointF(26, 6), new PointF(26, 10) });
                        g.DrawLines(p, new PointF[] { new PointF(10, 26), new PointF(6, 26), new PointF(6, 22) });
                        g.DrawLines(p, new PointF[] { new PointF(22, 26), new PointF(26, 26), new PointF(26, 22) });
                    }
                    using (SolidBrush d = new SolidBrush(Color.White))
                        g.FillEllipse(d, 13, 13, 6, 6);
                }
                _icon = System.Drawing.Icon.FromHandle(b.GetHicon());
            }
            catch { _icon = SystemIcons.Application; }
            return _icon;
        }
    }
}
