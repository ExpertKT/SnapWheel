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
    static class ImageIO
    {
        public const int MaxDim = 4096;      // 存进轮盘的图最大边；再大的等比缩下来，防止内存/磁盘爆

        public static readonly string[] Exts = {
            ".png", ".apng", ".jpg", ".jpeg", ".jpe", ".jfif", ".jiff",
            ".bmp", ".dib", ".rle", ".gif", ".tif", ".tiff",
            ".ico", ".cur", ".exif", ".emf", ".wmf",
            ".webp", ".heic", ".heif", ".avif", ".jxl", ".jxr", ".wdp", ".hdp", ".dds",
            ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".pef", ".srw", ".raw"
        };

        // 打开文件对话框用的过滤器（"图片文件|*.png;*.jpg;…|所有文件|*.*"）
        public static string DialogFilter()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Exts.Length; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append('*').Append(Exts[i]);
            }
            return "图片文件|" + sb.ToString() + "|所有文件|*.*";
        }

        public static bool IsImageExt(string path)        {
            string e = "";
            try { e = Path.GetExtension(path); } catch { }
            if (string.IsNullOrEmpty(e)) return false;
            e = e.ToLowerInvariant();
            for (int i = 0; i < Exts.Length; i++) if (Exts[i] == e) return true;
            return false;
        }

        // 展开文件夹（只取一层）并过滤出图片；cap 限制总数，避免一次拖进来一大堆把内存吃满
        public static List<string> Collect(string[] paths, int cap)
        {
            List<string> ok = new List<string>();
            if (paths == null) return ok;
            for (int i = 0; i < paths.Length && ok.Count < cap; i++)
            {
                string p = paths[i];
                try
                {
                    if (Directory.Exists(p))
                    {
                        string[] fs = Directory.GetFiles(p);
                        Array.Sort(fs);
                        for (int k = 0; k < fs.Length && ok.Count < cap; k++)
                            if (IsImageExt(fs[k])) ok.Add(fs[k]);
                    }
                    else if (File.Exists(p) && IsImageExt(p)) ok.Add(p);
                }
                catch { }
            }
            return ok;
        }

        public static Bitmap Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string ext = "";
            try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch { }
            Bitmap b = null;
            if (ext == ".ico" || ext == ".cur") b = LoadIcon(path);
            if (b == null) b = LoadGdi(path);
            if (b == null) b = LoadWic(path);
            if (b == null) return null;
            return Fit(b, MaxDim);
        }

        // GDI+：先整份读进内存再解，避免 Image.FromFile 一直占着原文件
        static Bitmap LoadGdi(string path)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(raw))
                using (Image img = Image.FromStream(ms))
                    return Clone(img);
            }
            catch { return null; }
        }

        static Bitmap LoadIcon(string path)
        {
            int[] want = { 256, 128, 64, 48, 32, 16 };
            for (int i = 0; i < want.Length; i++)
            {
                try
                {
                    using (Icon ic = new Icon(path, want[i], want[i]))
                    {
                        Bitmap b = ic.ToBitmap();
                        if (b.Width > 1 && b.Height > 1) return b;
                        b.Dispose();
                    }
                }
                catch { }
            }
            try { using (Icon ic = new Icon(path)) return ic.ToBitmap(); } catch { }
            return null;
        }

        // PresentationCore 不一定在 GAC 里（本机就只在框架目录的 WPF 子目录下），
        // 所以先 Assembly.Load，失败再 LoadFrom 那个固定位置。只试一次，结果缓存。
        static Assembly _wicAsm;
        static bool _wicTried;

        static Assembly WicAsm()
        {
            if (_wicTried) return _wicAsm;
            _wicTried = true;
            try { _wicAsm = Assembly.Load("PresentationCore"); }
            catch { _wicAsm = null; }
            if (_wicAsm == null)
            {
                try
                {
                    string d = RuntimeEnvironment.GetRuntimeDirectory();
                    string p = Path.Combine(Path.Combine(d, "WPF"), "PresentationCore.dll");
                    if (File.Exists(p)) _wicAsm = Assembly.LoadFrom(p);
                }
                catch { _wicAsm = null; }
            }
            return _wicAsm;
        }

        // 系统 WIC（反射拿 PresentationCore，编不进来也不影响本体）
        static Bitmap LoadWic(string path)
        {
            try
            {
                Assembly pc = WicAsm();
                if (pc == null) return null;
                Type tDec = pc.GetType("System.Windows.Media.Imaging.BitmapDecoder", false);
                Type tOpt = pc.GetType("System.Windows.Media.Imaging.BitmapCreateOptions", false);
                Type tCch = pc.GetType("System.Windows.Media.Imaging.BitmapCacheOption", false);
                Type tEnc = pc.GetType("System.Windows.Media.Imaging.PngBitmapEncoder", false);
                if (tDec == null || tOpt == null || tCch == null || tEnc == null) return null;

                MethodInfo create = tDec.GetMethod("Create", new Type[] { typeof(Uri), tOpt, tCch });
                if (create == null) return null;
                object dec = create.Invoke(null, new object[] {
                    new Uri(path), Enum.ToObject(tOpt, 0), Enum.ToObject(tCch, 1) });
                if (dec == null) return null;

                object frames = tDec.GetProperty("Frames").GetValue(dec, null);
                IList fl = frames as IList;
                if (fl == null || fl.Count == 0) return null;
                object frame = fl[0];

                object enc = Activator.CreateInstance(tEnc);
                object encFrames = tEnc.GetProperty("Frames").GetValue(enc, null);
                IList ef = encFrames as IList;
                if (ef == null) return null;
                ef.Add(frame);

                MethodInfo save = tEnc.GetMethod("Save", new Type[] { typeof(Stream) });
                if (save == null) return null;
                using (MemoryStream ms = new MemoryStream())
                {
                    save.Invoke(enc, new object[] { ms });
                    ms.Position = 0;
                    using (Image img = Image.FromStream(ms))
                        return Clone(img);
                }
            }
            catch { return null; }
        }

        // 复制成不依赖流/文件的独立位图（保留 alpha）
        static Bitmap Clone(Image img)
        {
            Bitmap c = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(c))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, new Rectangle(0, 0, c.Width, c.Height));
            }
            return c;
        }

        // 太大就等比缩到 MaxDim（顺手把原图释放掉）
        public static Bitmap Fit(Bitmap b, int max)
        {
            if (b == null) return null;
            if (b.Width <= max && b.Height <= max) return b;
            float s = Math.Min((float)max / b.Width, (float)max / b.Height);
            int w = Math.Max(1, (int)Math.Round(b.Width * s));
            int h = Math.Max(1, (int)Math.Round(b.Height * s));
            Bitmap c = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(c))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(b, new Rectangle(0, 0, w, h));
            }
            try { b.Dispose(); } catch { }
            return c;
        }

        public static bool HasAlpha(Bitmap b)
        {
            try { return b != null && Image.IsAlphaPixelFormat(b.PixelFormat); }
            catch { return false; }
        }

        // 真正去看像素里有没有“半透明/透明”。注意 IsAlphaPixelFormat 对任何 32bpp 图都返回 true，
        // 光看格式会把所有照片都当带透明的，于是全存成 PNG（又大又没必要）。
        public static bool HasRealAlpha(Bitmap b)
        {
            if (!HasAlpha(b)) return false;
            try
            {
                BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height),
                                          ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = Math.Abs(d.Stride);
                    byte[] row = new byte[stride];
                    long baseAddr = d.Scan0.ToInt64();
                    for (int y = 0; y < b.Height; y++)
                    {
                        Marshal.Copy(new IntPtr(baseAddr + (long)y * d.Stride), row, 0, stride);
                        for (int x = 3; x < row.Length; x += 4)
                            if (row[x] != 255) return true;
                    }
                }
                finally { b.UnlockBits(d); }
            }
            catch { return true; }      // 读不出来就按“有透明”处理，宁可存 PNG 也别丢通道
            return false;
        }

        // 有真透明 -> PNG（无损）；不透明 -> JPEG（省磁盘，照片也不失真）
        public static string ExtFor(Bitmap b) { return HasRealAlpha(b) ? ".png" : ".jpg"; }

        public static void SaveAs(Bitmap b, string path)
        {
            string low = path.ToLowerInvariant();
            if (!low.EndsWith(".jpg") && !low.EndsWith(".jpeg"))
            {
                b.Save(path, ImageFormat.Png);
                return;
            }
            ImageCodecInfo jpg = null;
            try
            {
                ImageCodecInfo[] cs = ImageCodecInfo.GetImageEncoders();
                for (int i = 0; i < cs.Length; i++)
                    if (cs[i].FormatID == ImageFormat.Jpeg.Guid) { jpg = cs[i]; break; }
            }
            catch { }
            if (jpg == null) { b.Save(path, ImageFormat.Png); return; }
            using (EncoderParameters ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                b.Save(path, jpg, ep);
            }
        }
    }
}
