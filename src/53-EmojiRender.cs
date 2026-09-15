using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

namespace SnapWheel
{
    // ==================== 彩色 emoji 渲染（0.7.0） ====================
    // 为什么非要用 WPF：Windows 的彩色 emoji 靠 Segoe UI Emoji 里的 COLR/CPAL 表实现，
    //   而 **GDI 和 GDI+ 都不认这两张表** —— 只会画成黑白轮廓（第一版就是这样，用户说丑）。
    //   能拿到真正彩色的只有 DirectWrite，.NET 里对应 WPF 的 FormattedText + RenderTargetBitmap。
    //   PresentationCore / WindowsBase 是 .NET Framework 自带的程序集（Framework64\v4.0.30319\WPF），
    //   不算第三方依赖，用户不用装东西。
    // 试过用反射 Load 来避免改构建脚本，但 LoadFrom 之后 GetType 拿不到类型，于是改为编译期引用：
    //   build.ps1 里给 csc 的引用数组补两个 /r: 即可。
    // 任何异常都返回 null，调用方退回黑白画法，不会崩。
    static class EmojiRender
    {
        static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();
        public static string Why = "none";      // 诊断用：最后一次失败的原因

        public static bool Available
        {
            get { try { return Get("A", 16) != null; } catch { return false; } }
        }

        public static Bitmap Get(string glyph, int px)
        {
            if (string.IsNullOrEmpty(glyph)) return null;
            if (px < 8) px = 8;
            if (px > 400) px = 400;
            string key = glyph + "@" + px;
            Bitmap hit;
            if (_cache.TryGetValue(key, out hit) && hit != null) return hit;

            Bitmap b = null;
            try { b = Render(glyph, px); }
            catch (Exception ex) { Why = ex.GetType().Name + ": " + ex.Message; b = null; }

            if (b != null)
            {
                if (_cache.Count > 240)
                {
                    foreach (KeyValuePair<string, Bitmap> kv in new List<KeyValuePair<string, Bitmap>>(_cache))
                    {
                        if (kv.Value != null) kv.Value.Dispose();
                        _cache.Remove(kv.Key);
                        break;
                    }
                }
                _cache[key] = b;
            }
            return b;
        }

        static Bitmap Render(string glyph, int px)
        {
            // ⚠️ 已知限制：这里用的是 FormattedText，它走低层文本路径，会把彩色 emoji 画成**单色剪影**
            //    （形状是对的，但只有一种颜色）。彩色需要 TextBlock 那种完整排版管线，而那条路要引
            //    PresentationFramework.dll + System.Xaml.dll，并且它自带的 Microsoft.Win32.OpenFileDialog
            //    会和 WinForms 的同名类型撞车（90-App.cs 当场报二义性），牵动面太大，
            //    所以这一版先保持单色，把彩色留给专门一轮处理。
            System.Windows.Media.FormattedText ft = new System.Windows.Media.FormattedText(
                glyph, CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Segoe UI Emoji"),
                px, System.Windows.Media.Brushes.Black, 96.0);

            int w = (int)Math.Ceiling(ft.Width) + 4;
            int h = (int)Math.Ceiling(ft.Height) + 4;
            if (w < 4 || h < 4) return null;

            System.Windows.Media.DrawingVisual dv = new System.Windows.Media.DrawingVisual();
            using (System.Windows.Media.DrawingContext dc = dv.RenderOpen())
                dc.DrawText(ft, new System.Windows.Point(2, 2));

            System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(dv);

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { rtb.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), bd.Scan0, bd.Stride * h, bd.Stride); }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
    }
}