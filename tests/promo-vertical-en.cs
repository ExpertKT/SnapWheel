// 英文竖版图文生成器（给 Reddit / Product Hunt / B站英文区 用）：6 张 1080x1440，逻辑同中文版。
// 编译：csc /nologo /target:exe /main:SnapWheel.PromoVEn /out:promoven.exe src\*.cs tests\promo-vertical-en.cs
// 用法：promoven.exe <输出目录>      依赖 %TEMP%\snapwheel_ui（先跑 ui-shot）与 docs/ocr.png
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace SnapWheel
{
    static class PromoVEn
    {
        const string FONT = "Segoe UI";
        static readonly Color BgTop = Color.FromArgb(46, 62, 96);
        static readonly Color BgBottom = Color.FromArgb(15, 18, 26);
        static readonly Color Accent = Color.FromArgb(0, 148, 240);
        static readonly Color Sub = Color.FromArgb(178, 190, 210);
        static readonly Color ChipBg = Color.FromArgb(44, 0, 140, 230);

        const int W = 1080, H = 1440;
        static string outDir, shotDir;

        static void Main(string[] args)
        {
            outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "snapwheel_vertical_en");
            Directory.CreateDirectory(outDir);
            shotDir = Path.Combine(Path.GetTempPath(), "snapwheel_ui");
            try
            {
                Page(1, "Screenshot.\nDrag it out. Done.", "No saving, no window switching, no hunting through folders", "Free & open source", "promo_wheel.png");
                Page(2, "A ring that\nlives in the corner", "Screenshots slide in. Drag them out whenever you need one.", "What it is", "wheel_bl.png");
                Page(3, "Drag it into\nany app", "WeChat, Word, Explorer \u2014 release the mouse and it is sent", "Everyday use", "promo_drop.png");
                Page(4, "Need the\nwhole page?", "Scrolling capture: frame an area, it scrolls and stitches by itself", "New in 0.6.0", "longdemo");
                Page(5, "Grab text from\nany screenshot", "OCR plus one-click translation, even on dark low-contrast text", "New in 0.6.0", "ocr.png");
                Page(6, "One exe.\n274 KB. Zero deps.", "Portable \u00b7 MIT licensed \u00b7 github.com/ExpertKT/SnapWheel", "Just download", "settings.png");
                WriteCopy();
                Console.WriteLine("done -> " + outDir);
            }
            catch (Exception ex) { Console.WriteLine("failed: " + ex.Message); }
        }

        static Bitmap Load(string file)
        {
            if (file == "longdemo") return null;
            string p = Path.Combine(shotDir, file);
            if (!File.Exists(p)) return null;
            try { using (Bitmap b = new Bitmap(p)) return new Bitmap(b); } catch { return null; }
        }

        static void Page(int no, string title, string sub, string chip, string img)
        {
            using (Bitmap b = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                Backdrop(g, no);

                using (Font fb = new Font(FONT, 26, FontStyle.Bold))
                    TextRenderer.DrawText(g, "SnapWheel", fb, new Point(72, 64), Accent, TextFormatFlags.NoPadding);
                using (Font fn = new Font(FONT, 24))
                    TextRenderer.DrawText(g, no + " / 6", fn, new Point(W - 72 - 90, 68), Color.FromArgb(120, 150, 165, 190), TextFormatFlags.NoPadding);

                if (!string.IsNullOrEmpty(chip)) Chip(g, chip, 72, 138);

                string[] tl = title.Split('\n');
                int y = 246;
                foreach (string line in tl)
                {
                    using (Font f = new Font(FONT, FitSize(g, line, 92, W - 144), FontStyle.Bold))
                        TextRenderer.DrawText(g, line, f, new Point(72, y), Color.White, TextFormatFlags.NoPadding);
                    y += 132;
                }
                using (Font fs = new Font(FONT, FitSize(g, sub, 36, W - 144)))
                    TextRenderer.DrawText(g, sub, fs, new Point(72, y + 16), Sub, TextFormatFlags.NoPadding);

                if (img == "longdemo") LongDemo(g, 760);
                else
                {
                    Bitmap im = Load(img);
                    if (im != null)
                    {
                        const int bandH = 470;
                        float s = Math.Min((float)(W - 160) / im.Width, (float)bandH / im.Height);
                        int dw = (int)(im.Width * s), dh = (int)(im.Height * s);
                        int dx = (W - dw) / 2, dy = 800 + (bandH - dh) / 2;
                        using (GraphicsPath card = Gfx.Round(new RectangleF(dx - 20, dy - 20, dw + 40, dh + 40), 24f))
                        {
                            using (LinearGradientBrush cb = new LinearGradientBrush(
                                new Rectangle(dx - 20, dy - 20, dw + 40, dh + 40),
                                Color.FromArgb(248, 252, 255), Color.FromArgb(226, 236, 248), 55f))
                                g.FillPath(cb, card);
                            using (Pen bp = new Pen(Color.FromArgb(80, 140, 170, 210), 1.4f)) g.DrawPath(bp, card);
                        }
                        g.DrawImage(im, dx, dy, dw, dh);
                        im.Dispose();
                    }
                }
                using (Font fd = new Font(FONT, FitSize(g, "Free \u00b7 open source \u00b7 single file, double-click to run", 30, W - 144)))
                    TextRenderer.DrawText(g, "Free \u00b7 open source \u00b7 single file, double-click to run", fd, new Point(72, H - 104), Color.FromArgb(200, 150, 165, 190), TextFormatFlags.NoPadding);

                b.Save(Path.Combine(outDir, "img" + no + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("  img" + no + ".png");
            }
        }

        static void LongDemo(Graphics g, int top)
        {
            using (SolidBrush white = new SolidBrush(Color.FromArgb(242, 250, 252, 255)))
            using (Pen edge = new Pen(Color.FromArgb(80, 120, 150, 190), 1.4f))
            {
                RectangleF a = new RectangleF(160, top + 40, 320, 380);
                using (GraphicsPath rp = Gfx.Round(a, 18f)) { g.FillPath(white, rp); g.DrawPath(edge, rp); }
                for (int i = 0; i < 9; i++)
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb(i % 3 == 0 ? 165 : 100, 90, 120, 160)))
                        g.FillRectangle(lb, a.X + 26, a.Y + 30 + i * 38, a.Width - 52 - (i % 3) * 52, 12);
                RectangleF b2 = new RectangleF(620, top - 30, 320, 520);
                using (GraphicsPath rp = Gfx.Round(b2, 18f)) { g.FillPath(white, rp); g.DrawPath(edge, rp); }
                for (int i = 0; i < 13; i++)
                {
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb(i % 4 == 0 ? 165 : 100, 90, 120, 160)))
                        g.FillRectangle(lb, b2.X + 26, b2.Y + 26 + i * 38, b2.Width - 52 - (i % 4) * 48, 12);
                    if (i == 4 || i == 8)
                        using (Pen sp = new Pen(Color.FromArgb(160, 0, 148, 240), 2.4f))
                            g.DrawLine(sp, b2.X + 12, b2.Y + 26 + i * 38 - 8, b2.Right - 12, b2.Y + 26 + i * 38 - 8);
                }
            }
            using (Font f = new Font(FONT, 56, FontStyle.Bold))
                TextRenderer.DrawText(g, "->", f, new Point(520, top + 180), Accent, TextFormatFlags.NoPadding);
        }

        static void Backdrop(Graphics g, int seed)
        {
            using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, W, H), BgTop, BgBottom, 68f))
                g.FillRectangle(lg, 0, 0, W, H);
            int ox = 240 + (seed * 173) % 620;
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(ox - 520, H - 980, 1040, 1040);
                using (PathGradientBrush pb = new PathGradientBrush(gp))
                {
                    pb.CenterColor = Color.FromArgb(46, 0, 148, 240);
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, 0, 148, 240) };
                    g.FillPath(pb, gp);
                }
            }
        }

        static void Chip(Graphics g, string text, int x, int y)
        {
            using (Font f = new Font(FONT, 26, FontStyle.Bold))
            {
                Size sz = TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                Rectangle r = new Rectangle(x, y, sz.Width + 44, sz.Height + 20);
                using (GraphicsPath gp = new GraphicsPath())
                {
                    int rad = r.Height / 2;
                    gp.AddArc(r.X, r.Y, rad * 2, r.Height, 90, 180);
                    gp.AddArc(r.Right - rad * 2, r.Y, rad * 2, r.Height, 270, 180);
                    gp.CloseFigure();
                    using (SolidBrush sb = new SolidBrush(ChipBg)) g.FillPath(sb, gp);
                    using (Pen p = new Pen(Color.FromArgb(120, 0, 170, 255), 1.4f)) g.DrawPath(p, gp);
                }
                TextRenderer.DrawText(g, text, f, new Point(r.X + 22, r.Y + 10), Color.FromArgb(240, 225, 240, 252), TextFormatFlags.NoPadding);
            }
        }

        static float FitSize(Graphics g, string text, float pt, int maxW)
        {
            if (string.IsNullOrEmpty(text)) return pt;
            using (Font f = new Font(FONT, pt))
            {
                float w = TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                if (w <= maxW || w <= 0) return pt;
                float np = pt * (maxW / w);
                return np < 12f ? 12f : np;
            }
        }

        static void WriteCopy()
        {
            string s =
                "# Social copy (English)\r\n\r\n"
              + "## Reddit / forum title\r\n\r\n"
              + "I built a screenshot ring for Windows: capture, then just drag the thumbnail into any app (free, MIT, 274 KB)\r\n\r\n"
              + "## Reddit / Product Hunt body\r\n\r\n"
              + "Every screenshot workflow seems to be: capture -> save -> switch window -> find the file -> drag it in.\r\n"
              + "SnapWheel removes the middle steps. Press Ctrl+Shift+S, drag a region, and the shot slides into a ring that\n"
              + "sits in the corner of your screen. When you need it, drag the thumbnail straight into WeChat / Word / Explorer.\r\n\r\n"
              + "- Scrolling capture: frame an area, it scrolls and stitches the long image by itself (new in 0.6.0)\r\n"
              + "- OCR + one-click translation, works on dark, low-contrast text (new in 0.6.0)\r\n"
              + "- Middle-click a thumbnail to pin it on screen; undo delete; annotate with arrows/boxes/mosaic/text\r\n"
              + "- Single 274 KB exe, zero third-party dependencies, no installer, no ads, no telemetry\r\n\r\n"
              + "Written in C# / WinForms, everything (window, buttons, icons, the frosted glass) is drawn by code -\n"
              + "no UI toolkit, no image assets. Source and a portable exe: github.com/ExpertKT/SnapWheel\r\n\r\n"
              + "Feedback welcome - especially what breaks on your setup.\r\n\r\n"
              + "## Hacker News / short version\r\n\r\n"
              + "Show HN: SnapWheel - a screenshot ring for Windows, drag thumbnails straight into any app (274 KB, MIT)\r\n";
            File.WriteAllText(Path.Combine(outDir, "post-copy-en.md"), s, new System.Text.UTF8Encoding(false));
            Console.WriteLine("  post-copy-en.md");
        }
    }
}
