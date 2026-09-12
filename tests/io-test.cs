// 图片加载回归测试：把 SnapWheel.cs 一起编进来（/main:SnapWheel.IoTest.Main），
// 逐个格式生成样本文件 -> 走 ImageIO.Load 读回 -> 校验尺寸/alpha。
// 编译：
//   csc /nologo /target:exe /main:SnapWheel.IoTest.Main /out:iotest.exe SnapWheel.cs tests\io-test.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace SnapWheel
{
    static class IoTest
    {
        static int pass = 0, fail = 0;
        static string dir;

        static void Check(string what, Bitmap b, int ew, int eh)
        {
            if (b == null) { Console.WriteLine("  FAIL {0}: 读不出来", what); fail++; return; }
            bool ok = true;
            string extra = "";
            if (ew > 0 && (b.Width != ew || b.Height != eh)) { ok = false; extra = string.Format(" 期望 {0}x{1}", ew, eh); }
            Console.WriteLine("  {0} {1}: {2}x{3} {4}{5}", ok ? "OK  " : "FAIL", what, b.Width, b.Height,
                Image.IsAlphaPixelFormat(b.PixelFormat) ? "ARGB" : "RGB ", extra);
            if (ok) pass++; else fail++;
            b.Dispose();
        }

        static Bitmap Sample(bool alpha)
        {
            Bitmap b = new Bitmap(120, 80, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush br = new SolidBrush(alpha ? Color.FromArgb(200, 40, 160, 220) : Color.FromArgb(255, 40, 160, 220)))
                    g.FillRectangle(br, 0, 0, 120, 80);
                g.FillEllipse(Brushes.White, 20, 15, 50, 50);
            }
            return b;
        }

        public static void Main()
        {
            dir = Path.Combine(Path.GetTempPath(), "snapwheel_iotest");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            try { Directory.CreateDirectory(dir); } catch { }
            Console.WriteLine("样本目录: " + dir);
            Console.WriteLine();

            // --- 生成各种格式 ---
            using (Bitmap s = Sample(true)) s.Save(Path.Combine(dir, "a.png"), ImageFormat.Png);
            using (Bitmap s = Sample(false)) s.Save(Path.Combine(dir, "a.jpg"), ImageFormat.Jpeg);
            using (Bitmap s = Sample(false)) { var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(Encoder.Quality, 95L);
                ImageCodecInfo jpg = null; foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
                s.Save(Path.Combine(dir, "a.jpeg"), jpg, ep); ep.Dispose(); }
            using (Bitmap s = Sample(false)) s.Save(Path.Combine(dir, "a.bmp"), ImageFormat.Bmp);
            using (Bitmap s = Sample(false)) s.Save(Path.Combine(dir, "a.gif"), ImageFormat.Gif);
            using (Bitmap s = Sample(false)) s.Save(Path.Combine(dir, "a.tif"), ImageFormat.Tiff);
            using (Bitmap s = Sample(false)) s.Save(Path.Combine(dir, "a.wmf"), ImageFormat.Wmf);
            // ICO：从 64x64 图生成
            using (Bitmap s = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(s)) { g.Clear(Color.Transparent); g.FillEllipse(Brushes.OrangeRed, 4, 4, 56, 56); }
                IntPtr h = s.GetHicon();
                using (Icon ic = Icon.FromHandle(h))
                using (FileStream fs = new FileStream(Path.Combine(dir, "a.ico"), FileMode.Create))
                    ic.Save(fs);
            }
            File.Copy(Path.Combine(dir, "a.png"), Path.Combine(dir, "noext_but_png"), true);
            File.Copy(Path.Combine(dir, "a.jpg"), Path.Combine(dir, "b.JPG"), true);       // 大写后缀
            File.Copy(Path.Combine(dir, "a.png"), Path.Combine(dir, "c.JPEG"), true);      // 内容与后缀不符也能读

            // --- 逐个读 ---
            string[] names = { "a.png", "a.jpg", "a.jpeg", "b.JPG", "a.bmp", "a.gif", "a.tif", "a.wmf", "a.ico" };
            foreach (string n in names)
                Check(n, ImageIO.Load(Path.Combine(dir, n)), 0, 0);

            Console.WriteLine();
            Console.WriteLine("--- 扩展名过滤 ---");
            string[] exts = { ".png", ".PNG", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".cur",
                              ".webp", ".heic", ".avif", ".jxl", ".dds", ".exif", ".emf", ".wmf", ".dng", ".cr2",
                              ".txt", ".exe", ".zip", ".cs", ".pdf" };
            int good = 0, bad = 0;
            foreach (string e in exts)
            {
                bool want = e != ".txt" && e != ".exe" && e != ".zip" && e != ".cs" && e != ".pdf";
                bool got = ImageIO.IsImageExt("x" + e);
                if (want == got) good++; else { bad++; Console.WriteLine("  FAIL {0} -> {1}", e, got); }
            }
            Console.WriteLine("  识别正确 {0} 个，错误 {1} 个", good, bad);
            fail += bad; pass += good;

            Console.WriteLine();
            Console.WriteLine("--- 文件夹展开 + 上限 ---");
            string sub = Path.Combine(dir, "folder");
            try { Directory.CreateDirectory(sub); } catch { }
            for (int i = 0; i < 8; i++) File.Copy(Path.Combine(dir, "a.png"), Path.Combine(sub, "f" + i + ".png"), true);
            File.WriteAllText(Path.Combine(sub, "note.txt"), "x");
            var got1 = ImageIO.Collect(new string[] { sub }, 50);
            Console.WriteLine("  文件夹 -> {0} 张（期望 8）  {1}", got1.Count, got1.Count == 8 ? "OK" : "FAIL");
            if (got1.Count == 8) pass++; else fail++;
            var got2 = ImageIO.Collect(new string[] { sub }, 3);
            Console.WriteLine("  cap=3 -> {0} 张  {1}", got2.Count, got2.Count == 3 ? "OK" : "FAIL");
            if (got2.Count == 3) pass++; else fail++;
            var got3 = ImageIO.Collect(new string[] { Path.Combine(dir, "note.txt"), Path.Combine(dir, "a.png") }, 50);
            Console.WriteLine("  非图片被过滤 -> {0} 张（期望 1）  {1}", got3.Count, got3.Count == 1 ? "OK" : "FAIL");
            if (got3.Count == 1) pass++; else fail++;

            Console.WriteLine();
            Console.WriteLine("--- 坏文件必须安静返回 null（不崩）---");
            File.WriteAllText(Path.Combine(dir, "broken.png"), "这不是图片");
            File.WriteAllBytes(Path.Combine(dir, "empty.jpg"), new byte[0]);
            foreach (string n in new string[] { "broken.png", "empty.jpg", "根本不存在.png" })
            {
                Bitmap rb = null; string err = null;
                try { rb = ImageIO.Load(Path.Combine(dir, n)); } catch (Exception ex) { err = ex.GetType().Name; }
                bool ok = rb == null && err == null;
                Console.WriteLine("  {0} {1}: {2}", ok ? "OK  " : "FAIL", n, err != null ? ("抛异常 " + err) : (rb == null ? "null" : "竟然读出来了"));
                if (ok) pass++; else fail++;
                if (rb != null) rb.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine("--- 超大图自动缩到 4096 ---");
            using (Bitmap big = new Bitmap(5000, 200, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(big)) g.Clear(Color.CornflowerBlue);
                big.Save(Path.Combine(dir, "big.png"), ImageFormat.Png);
            }
            using (Bitmap b = ImageIO.Load(Path.Combine(dir, "big.png")))
            {
                bool ok = b != null && b.Width == 4096 && b.Height == 164;
                Console.WriteLine("  {0} 5000x200 -> {1}x{2}（期望 4096x164）", ok ? "OK  " : "FAIL", b == null ? 0 : b.Width, b == null ? 0 : b.Height);
                if (ok) pass++; else fail++;
            }

            Console.WriteLine();
            Console.WriteLine("--- SaveAs 选取格式 ---");
            using (Bitmap s = Sample(true))
            {
                Console.WriteLine("  有真透明 -> {0}（期望 .png）", ImageIO.ExtFor(s));
                if (ImageIO.ExtFor(s) == ".png") pass++; else fail++;
                string p = Path.Combine(dir, "s1.png");
                ImageIO.SaveAs(s, p);
                using (Image r = Image.FromFile(p)) { }
                Console.WriteLine("  写出 PNG 正常");
                pass++;
            }
            using (Bitmap s = Sample(false))
            {
                Console.WriteLine("  全不透明 -> {0}（期望 .jpg）", ImageIO.ExtFor(s));
                if (ImageIO.ExtFor(s) == ".jpg") pass++; else fail++;
                string p = Path.Combine(dir, "s2.jpg");
                ImageIO.SaveAs(s, p);
                using (Image r = Image.FromFile(p))
                {
                    bool ok = r.RawFormat.Guid == ImageFormat.Jpeg.Guid;
                    Console.WriteLine("  {0} .jpg 文件真的是 JPEG（不是 PNG 改了后缀）", ok ? "OK  " : "FAIL");
                    if (ok) pass++; else fail++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- 透明 PNG 存成 PNG 后透明还在 ---");
            using (Bitmap s = Sample(true))
            {
                string p = Path.Combine(dir, "s3.png");
                ImageIO.SaveAs(s, p);
                using (Bitmap r = ImageIO.Load(p))
                {
                    bool ok = r != null && ImageIO.HasRealAlpha(r);
                    Console.WriteLine("  {0} 透明保住了", ok ? "OK  " : "FAIL");
                    if (ok) pass++; else fail++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- Store.Import 落盘 ---");
            {
                string wdir = Path.Combine(dir, "wheel");
                Store st = new Store();
                st.SaveToDisk = true;
                st.Dir = wdir;
                StoreItem i1 = st.Import(Path.Combine(dir, "a.png"));
                StoreItem i2 = st.Import(Path.Combine(dir, "a.jpg"));
                StoreItem i3 = st.Import(Path.Combine(dir, "a.ico"));
                StoreItem i4 = st.Import(Path.Combine(dir, "broken.png"));
                StoreItem i5 = st.Import(Path.Combine(dir, "根本没有.png"));

                bool ok = i1 != null && i2 != null && i3 != null && i4 == null && i5 == null;
                Console.WriteLine("  {0} 正常读入 3 张 / 坏文件返回 null -> Items={1}", ok ? "OK  " : "FAIL", st.Items.Count);
                if (ok) pass++; else fail++;

                bool files = i1 != null && File.Exists(i1.FilePath) && i1.FilePath.ToLower().EndsWith(".png")
                          && i2 != null && File.Exists(i2.FilePath) && i2.FilePath.ToLower().EndsWith(".jpg")
                          && i3 != null && File.Exists(i3.FilePath);
                Console.WriteLine("  {0} 磁盘文件后缀正确（透明->png / 不透明->jpg）", files ? "OK  " : "FAIL");
                if (files) pass++; else fail++;
                foreach (var it in st.Items) Console.WriteLine("      {0}", Path.GetFileName(it.FilePath));

                // 磁盘上能被重新扫回来（模拟重启后的 LoadImagesFromDisk）
                List<string> back = new List<string>();
                back.AddRange(Directory.GetFiles(wdir, "snap_*.png"));
                back.AddRange(Directory.GetFiles(wdir, "snap_*.jpg"));
                back.Sort(StringComparer.OrdinalIgnoreCase);
                int re = 0;
                foreach (string f in back) { using (Bitmap b = ImageIO.Load(f)) if (b != null) re++; }
                bool ok2 = re == st.Items.Count;
                Console.WriteLine("  {0} 重启后能回读 {1}/{2} 张", ok2 ? "OK  " : "FAIL", re, st.Items.Count);
                if (ok2) pass++; else fail++;

                // 超过上限自动淘汰最早的
                Store st2 = new Store();
                st2.SaveToDisk = false;
                st2.MaxCount = 3;
                for (int i = 0; i < 6; i++) st2.Import(Path.Combine(dir, "a.png"));
                bool ok3 = st2.Items.Count == 3;
                Console.WriteLine("  {0} MaxCount=3 时导入 6 张 -> {1} 张", ok3 ? "OK  " : "FAIL", st2.Items.Count);
                if (ok3) pass++; else fail++;
            }

            Console.WriteLine();
            Console.WriteLine("--- WIC 分支（反射调用私有 LoadWic，验证反射管线本身通）---");
            {
                var mi = typeof(ImageIO).GetMethod("LoadWic", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (mi == null) { Console.WriteLine("  FAIL 找不到 LoadWic"); fail++; }
                else
                {
                    Bitmap w = null; string err = null;
                    try { w = (Bitmap)mi.Invoke(null, new object[] { Path.Combine(dir, "a.png") }); }
                    catch (Exception ex) { err = (ex.InnerException ?? ex).GetType().Name; }
                    bool ok = w != null && w.Width == 120 && w.Height == 80;
                    Console.WriteLine("  {0} WIC 解 PNG -> {1}  {2}", ok ? "OK  " : "FAIL",
                        w == null ? (err == null ? "null（本机没有 WPF/WIC？）" : err) : (w.Width + "x" + w.Height), "");
                    if (ok) pass++; else fail++;
                    if (w != null) w.Dispose();
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- 真实 WebP（能下到才测）---");
            string webp = Path.Combine(Path.GetTempPath(), "sw_test.webp");
            if (File.Exists(webp))
            {
                Bitmap b = ImageIO.Load(webp);
                bool ok = b != null && b.Width > 10;
                Console.WriteLine("  {0} 1.webp -> {1}", ok ? "OK  " : "FAIL", b == null ? "读不出来（本机没装 WebP 解码器）" : (b.Width + "x" + b.Height));
                if (ok) pass++; else fail++;
                if (b != null) b.Dispose();
                Console.WriteLine("  IsImageExt(.webp) = {0}", ImageIO.IsImageExt("x.webp"));
                if (ImageIO.IsImageExt("x.webp")) pass++; else fail++;
            }
            else Console.WriteLine("  （没有样本，跳过）");

            Console.WriteLine();
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
        }
    }
}
