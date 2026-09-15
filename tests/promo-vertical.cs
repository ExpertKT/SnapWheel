// 竖版图文生成器（抖音 / 小红书用）：1080x1440（3:4，两个平台都吃），一页讲一件事，按顺序滑。
// 和九宫格的区别：不需要"宫格"排版，前 3 秒必须让人看懂这是什么、以及凭什么用它。
// 编译：csc /nologo /target:exe /main:SnapWheel.PromoV /out:promov.exe src\*.cs tests\promo-vertical.cs
// 用法：promov.exe <输出目录>      依赖 %TEMP%\snapwheel_ui（先跑 ui-shot）与 docs/ocr.png
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace SnapWheel
{
    static class PromoV
    {
        const string FONT = "Microsoft YaHei UI";
        static readonly Color BgTop = Color.FromArgb(46, 62, 96);
        static readonly Color BgBottom = Color.FromArgb(15, 18, 26);
        static readonly Color Accent = Color.FromArgb(0, 148, 240);
        static readonly Color Sub = Color.FromArgb(170, 182, 202);
        static readonly Color ChipBg = Color.FromArgb(44, 0, 140, 230);

        const int W = 1080, H = 1440;
        static string outDir, shotDir;

        static void Main(string[] args)
        {
            outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "snapwheel_vertical");
            Directory.CreateDirectory(outDir);
            shotDir = Path.Combine(Path.GetTempPath(), "snapwheel_ui");

            try
            {
                // 每页：大标题（吸睛）/ 副标题（说清价值）/ 可选角标 / 配图
                Page(1, "截完图\n拖一下就发出去了", "不用保存、不用切窗口、不用翻文件夹", "效率工具 · 开源", "promo_wheel.png");
                Page(2, "屏幕角上\n常驻一个圆环", "截图自动滑进去，要用的时候拖出来", "一眼看懂", "wheel_bl.png");
                Page(3, "拖进微信 / 文档\n松手即发", "环上还留着一份，随时能再拖一次", "最常用", "promo_drop.png");
                Page(4, "网页想截全？\n让它自己滚", "滚动长截图：框一块区域，剩下的它自己滚自己拼", "0.6.0 新增", "longdemo");
                Page(5, "圈住文字\n就能复制", "取字 + 一键翻译，暗色小字也认得准", "0.6.0 强化", "ocr.png");
                Page(6, "一个 exe\n274 KB 零依赖", "免安装 · 开源 MIT · GitHub 搜 SnapWheel", "下载即用", "settings.png");
                WriteCopy();
                Console.WriteLine("完成：6 张竖版图文 + 发帖文案 已输出到 " + outDir);
            }
            catch (Exception ex) { Console.WriteLine("失败：" + ex.Message); }
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

                // 顶部：品牌 + 页码（小字，不抢戏）
                using (Font fb = new Font(FONT, 26, FontStyle.Bold))
                    TextRenderer.DrawText(g, "SnapWheel 快照轮环", fb, new Point(72, 64), Accent, TextFormatFlags.NoPadding);
                using (Font fn = new Font(FONT, 24))
                    TextRenderer.DrawText(g, no + " / 6", fn, new Point(W - 72 - 90, 68), Color.FromArgb(120, 150, 165, 190), TextFormatFlags.NoPadding);

                // 角标：一页一个卖点标签（吸睛用）
                if (!string.IsNullOrEmpty(chip)) Chip(g, chip, 72, 138);

                // 大标题：超大、自动适配宽度（手机上一眼看清就是靠它）
                string[] tl = title.Split('\n');
                int y = 246;
                foreach (string line in tl)
                {
                    using (Font f = new Font(FONT, FitSize(g, line, 104, W - 144), FontStyle.Bold))
                        TextRenderer.DrawText(g, line, f, new Point(72, y), Color.White, TextFormatFlags.NoPadding);
                    y += 152;
                }

                // 副标题
                using (Font fs = new Font(FONT, FitSize(g, sub, 40, W - 144)))
                    TextRenderer.DrawText(g, sub, fs, new Point(72, y + 16), Sub, TextFormatFlags.NoPadding);

                // 配图：统一带（同高、居中），长截图那页用现场画的示意
                if (img == "longdemo") LongDemo(g, 760);
                else
                {
                    Bitmap im = Load(img);
                    if (im != null)
                    {
                        const int bandH = 560;
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

                // 底部：一句话召唤
                using (Font fd = new Font(FONT, FitSize(g, "免费 · 开源 · 单文件，双击就能用", 30, W - 144)))
                    TextRenderer.DrawText(g, "免费 · 开源 · 单文件，双击就能用", fd, new Point(72, H - 104), Color.FromArgb(200, 150, 165, 190), TextFormatFlags.NoPadding);

                b.Save(Path.Combine(outDir, "图" + no + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("  写出 图" + no + ".png");
            }
        }

        // 「滚动长截图」那一页的示意：左边一屏 → 右边拼出的长条
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
                TextRenderer.DrawText(g, "→", f, new Point(520, top + 180), Accent, TextFormatFlags.NoPadding);
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

        // 实测宽度、超了就缩字号（用 GDI 量、GDI 画，两边同源）
        static float FitSize(Graphics g, string text, float pt, int maxW)
        {
            if (string.IsNullOrEmpty(text)) return pt;
            using (Font f = new Font(FONT, pt))
            {
                float w = TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                if (w <= maxW || w <= 0) return pt;
                float np = pt * (maxW / w);
                return np < 14f ? 14f : np;
            }
        }

        // 发帖文案（图文正文 + 话题标签），一起写出来给用户直接复制
        static void WriteCopy()
        {
            string s =
                "# 发帖文案（直接复制）\r\n\r\n"
              + "## 标题（小红书 20 字内最合适）\r\n\r\n"
              + "截图不用再存文件了，拖一下就发出去\r\n\r\n"
              + "## 正文\r\n\r\n"
              + "每次截完图，是不是都要：保存 → 切窗口 → 翻文件夹 → 拖进聊天框？\r\n"
              + "我做了个小工具叫「快照轮环」，专门省掉中间这几步：\r\n\r\n"
              + "🖼 屏幕角上常驻一个圆环，按 Ctrl+Shift+S 框选，图直接滑进环里，不弹保存框\r\n"
              + "↔️ 要用的时候，把缩略图拖进微信 / QQ / 文档 / 文件夹，松手就发出去\r\n"
              + "📜 网页想截全？框一块区域，它自己滚自己拼，出一张长图\r\n"
              + "🔍 圈住文字就能复制，还能一键翻译\r\n"
              + "↩️ 删错了能撤回\r\n"
              + "📦 一个 exe，274 KB，零依赖，免安装，Windows 10/11 双击就跑\r\n"
              + "🆓 开源 MIT，不要钱、没广告、不联网也能用\r\n\r\n"
              + "GitHub 搜 SnapWheel（作者 ExpertKT），或者评论区问我\r\n\r\n"
              + "## 话题标签\r\n\r\n"
              + "#效率工具 #截图 #Windows #电脑技巧 #办公神器 #免费软件 #开源 #桌面整理 #生产力工具 #SnapWheel\r\n";
            File.WriteAllText(Path.Combine(outDir, "发帖文案.md"), s, new System.Text.UTF8Encoding(false));
            Console.WriteLine("  写出 发帖文案.md");
        }
    }
}
