// 九宫格宣传图生成器（朋友圈用）：把界面渲染图和文案合成成九张 1080x1080 方图。
// 编译：csc /nologo /target:exe /main:SnapWheel.Promo9 /out:promo9.exe src\*.cs tests\promo9.cs
// 用法：promo9.exe <输出目录>      依赖 %TEMP%\snapwheel_ui 里的界面渲染图（先跑 ui-shot）
//
// 排布是按朋友圈九宫格的阅读顺序（左上 → 右下）设计的：
//   1 主视觉（名字 + 一句话，抓眼）
//   2 核心交互「截完就滑进角落」      3 核心交互「拖出去 = 发出去」
//   4 新功能「滚动长截图」
//   5 **正中心**：品牌 + 硬数据（九宫格里最抢眼的位置，放最有说服力的东西）
//   6 新功能「取字 + 翻译」
//   7 万能键「一个圆盘管所有」        8 标注 / 贴图 / 撤销删除
//   9 收尾（开源 + 下载 + 系统要求）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using System.IO;

namespace SnapWheel
{
    static class Promo9
    {
        const string FONT = "Microsoft YaHei UI";
        static readonly Color BgTop = Color.FromArgb(44, 58, 88);
        static readonly Color BgBottom = Color.FromArgb(17, 20, 28);
        static readonly Color Accent = Color.FromArgb(0, 138, 226);
        static readonly Color Sub = Color.FromArgb(148, 158, 178);
        static readonly Color ChipBg = Color.FromArgb(38, 255, 255, 255);

        static string shotDir, outDir;

        static void Main(string[] args)
        {
            outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "snapwheel_promo9");
            Directory.CreateDirectory(outDir);
            shotDir = Path.Combine(Path.GetTempPath(), "snapwheel_ui");

            try
            {
                Shot(1, "快照轮环", "截图不落文件，直接挂在屏幕角上", "", "promo_wheel.png", true);
                Shot(2, "截完就滑进角落", "不弹保存框 · 不用切窗口 · 不用翻文件夹", "Ctrl+Shift+S 框选，图自己滑进环里", "wheel_bl.png", false);
                Shot(3, "拖出去 = 发出去", "微信 / QQ / 文档 / 文件夹，松手就到", "环上还留着一份，随时能再拖一次", "promo_drop.png", false);
                Shot(4, "滚动长截图", "框一块区域，剩下的它自己滚、自己拼", "边滚边无缝拼接 · 到底自动停 · 0.6.0 新增", "wheel_empty.png", false);
                Center5();
                Shot(6, "取字 + 翻译", "圈住文字就认出来，一键翻译成中/英文", "低对比度也能认 · 默认免费接口 · 0.6.0 强化", "ocr.png", false);
                Shot(7, "万能键：一个圆盘管所有", "新建 / 切换 / 删除 / 上一个，四个方向四个动作", "长按圆盘拖向对应方向松手即可", "promo_menu.png", false);
                Shot(8, "标注 · 贴图 · 后悔药", "箭头方框马赛克文字 · 中键钉在屏幕上 · 删错能找回", "四色可选 · Ctrl+Z 撤销 · 最近 8 次都能撤", "promo_intro.png", false);
                Shot(9, "开源 · MIT", "github.com/ExpertKT/SnapWheel", "完整版 / 无万能键版都在 Releases", "settings.png", false);
                Merge();
                Console.WriteLine("完成：9 张图 + 一张九宫格总览已输出到 " + outDir);
            }
            catch (Exception ex) { Console.WriteLine("失败：" + ex.Message); }
        }

        static Bitmap Load(string file)
        {
            string p = Path.Combine(shotDir, file);
            if (!File.Exists(p)) return null;
            try { using (Bitmap b = new Bitmap(p)) return new Bitmap(b); } catch { return null; }
        }

        // 一张方图：深色渐变底 + 序号角标 + 大标题 + 两行说明 + 右下角界面渲染
        static void Shot(int no, string title, string line1, string line2, string img, bool hero)
        {
            using (Bitmap b = new Bitmap(1080, 1080, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                Backdrop(g, b.Width, b.Height, no);

                // 序号与品牌（左上）
                using (Font fb = new Font(FONT, 26, FontStyle.Bold))
                using (SolidBrush sb = new SolidBrush(Accent))
                    TextRenderer.DrawText(g, "SnapWheel 快照轮环", fb, new Point(86, 74), Accent, TextFormatFlags.NoPadding);
                using (Font fn = new Font(FONT, 22))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(110, 150, 160, 180)))
                    TextRenderer.DrawText(g, no + " / 9", fn, new Point(1080 - 86 - 74, 78), Color.FromArgb(110, 150, 160, 180), TextFormatFlags.NoPadding);

                // 标题（hero 那张更大）
                float ts = hero ? 72 : 62;   // 主图 96 太大（压住副文案），收到 72 与其余统一      // 统一 62：76 太大（长标题会被右边缘裁），62 既齐又放得下 11 字
                using (Font ft = new Font(FONT, ts, FontStyle.Bold))
                using (SolidBrush sb = new SolidBrush(Color.White))
                    TextRenderer.DrawText(g, title, ft, new Point(80, hero ? 300 : 216), Color.White, TextFormatFlags.NoPadding);

                int y = hero ? 416 : 330;   // 主图是纯文字封面：标题(底约390)与第一行之间要留出呼吸感   // 主图文案再上移 30：它的副文案底原本到 536，比卡片顶(526)多出 10px      // 固定行位置：标题底(约300) 之下，所有图的说明文字落在同一条线上
                using (Font fl = new Font(FONT, FitSize(g, line1, hero ? 40 : 36, 1080 - 168)))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(228, 236, 244, 252)))
                { TextRenderer.DrawText(g, line1, fl, new Point(84, (int)y), Color.FromArgb(228, 236, 244, 252), TextFormatFlags.NoPadding); }
                using (Font fs = new Font(FONT, FitSize(g, line2, 28, 1080 - 168)))
                using (SolidBrush sb = new SolidBrush(Sub))
                { TextRenderer.DrawText(g, line2, fs, new Point(84, (int)(y + (hero ? 78 : 58))), Color.FromArgb(148, 158, 178), TextFormatFlags.NoPadding); }

                // 右上角标（hero 打新，其它打卖点）
                if (no == 4 || no == 6) Chip(g, "0.6.0 新增", 1080 - 300, 122, 22);   // 右上角：原来贴在文案下，会压住第二行

                // 界面渲染：贴在下方（hero 靠右放小一点，避免压住文案）
                if (no == 4) LongDemo(g);          // 长截图没有现成渲染图，现场画个示意
                Bitmap im = (no == 4) ? null : Load(img);
                if (im != null)
                {
                    // 所有图的界面渲染放进**同一条横向带**：同高上限、水平居中、带内垂直居中 ——
                    // 原来按各自比例算尺寸和位置，九张摆一起就是参差不齐的，图1 的卡片还会压住文案。
                    const int bandTop = 548, bandH = 440;
                    float s = Math.Min((float)(1080 - 120) / im.Width, (float)bandH / im.Height);   // 宽度上限放宽：只有很宽的图才受它限制，其余都能撑满 440 高
                    int dw = (int)(im.Width * s), dh = (int)(im.Height * s);
                    int dx = (1080 - dw) / 2;
                    int dy = bandTop + (bandH - dh) / 2;
                    // 浅色卡片底：截图本身是深色界面，直接贴在深色背景上根本看不见（第一版就是这样）
                    using (GraphicsPath card = Gfx.Round(new RectangleF(dx - 22, dy - 22, dw + 44, dh + 44), 22f))
                    {
                        using (SolidBrush sh = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                            g.FillPath(sh, Gfx.Round(new RectangleF(dx - 14, dy - 12, dw + 44, dh + 44), 22f));
                        using (LinearGradientBrush cb = new LinearGradientBrush(
                            new Rectangle(dx - 22, dy - 22, dw + 44, dh + 44),
                            Color.FromArgb(248, 252, 255), Color.FromArgb(226, 236, 248), 55f))
                            g.FillPath(cb, card);
                        using (Pen bp = new Pen(Color.FromArgb(70, 140, 170, 210), 1.4f)) g.DrawPath(bp, card);
                    }
                    g.DrawImage(im, dx, dy, dw, dh);
                    im.Dispose();
                }
                b.Save(Path.Combine(outDir, "图" + no + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("  写出 图" + no + ".png  " + title);
            }
        }

        // 正中心那张：不放界面图，放品牌 + 硬数据（九宫格最抢眼的位置）
        static void Center5()
        {
            using (Bitmap b = new Bitmap(1080, 1080, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                Backdrop(g, b.Width, b.Height, 5);

                using (Font fb = new Font(FONT, 26, FontStyle.Bold))
                using (SolidBrush sb = new SolidBrush(Accent))
                    TextRenderer.DrawText(g, "SnapWheel", fb, new Point(86, 74), Accent, TextFormatFlags.NoPadding);
                using (Font fn = new Font(FONT, 22))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(110, 150, 160, 180)))
                    TextRenderer.DrawText(g, "5 / 9", fn, new Point(1080 - 86 - 74, 78), Color.FromArgb(110, 150, 160, 180), TextFormatFlags.NoPadding);

                using (Font ft = new Font(FONT, 104, FontStyle.Bold))
                using (SolidBrush sb = new SolidBrush(Color.White))
                    TextRenderer.DrawText(g, "快照轮环", ft, new Point(80, 300), Color.White, TextFormatFlags.NoPadding);
                using (Font fs = new Font(FONT, 30))
                using (SolidBrush sb = new SolidBrush(Sub))
                    g.DrawString("一个常驻屏幕角落的圆环，把截图这件事变顺手", fs, sb, 84, 460);

                // 三个硬数据
                string[] num = { "274 KB", "0", "1" };
                string[] cap = { "整个程序的大小", "第三方依赖", "个 exe，双击就跑" };
                int cx = 84;
                for (int i = 0; i < 3; i++)
                {
                    using (Font f1 = new Font(FONT, 58, FontStyle.Bold))
                    using (SolidBrush sb = new SolidBrush(Accent))
                        g.DrawString(num[i], f1, sb, cx, 600);
                    using (Font f2 = new Font(FONT, 26))
                    using (SolidBrush sb = new SolidBrush(Sub))
                        g.DrawString(cap[i], f2, sb, cx, 700);
                    cx += 370;
                }
                using (Font f2 = new Font(FONT, 28))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(210, 226, 234, 244)))
                    g.DrawString("Windows 10 / 11 · 绿色免安装 · 开源 MIT", f2, sb, 84, 850);

                b.Save(Path.Combine(outDir, "图5.png"), System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("  写出 图5.png  快照轮环（中心品牌位）");
            }
        }

        // 九宫格总览：3x3 拼一张，方便一眼看排布
        static void Merge()
        {
            using (Bitmap b = new Bitmap(1080 * 3 / 2, 1080 * 3 / 2, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(8, 9, 12)))
            {
                g.FillRectangle(bg, 0, 0, b.Width, b.Height);
                int cell = 1080 / 2 - 6;
                for (int i = 0; i < 9; i++)
                {
                    string p = Path.Combine(outDir, "图" + (i + 1) + ".png");
                    if (!File.Exists(p)) continue;
                    using (Bitmap im = new Bitmap(p))
                        g.DrawImage(im, (i % 3) * (cell + 9), (i / 3) * (cell + 9), cell, cell);
                }
                b.Save(Path.Combine(outDir, "九宫格总览.png"), System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("  写出 九宫格总览.png（预览排布用，发朋友圈不用它）");
            }
        }

        // 「滚动长截图」那格的示意图：左边一屏网页 → 右边拼出来的长条。
        // 没有现成截图素材（这个功能是 0.6.0 新加的），所以直接画。
        static void LongDemo(Graphics g)
        {
            int cx = 540, top = 610;
            using (SolidBrush white = new SolidBrush(Color.FromArgb(242, 250, 252, 255)))
            using (Pen edge = new Pen(Color.FromArgb(80, 120, 150, 190), 1.4f))
            {
                // 左：普通一屏
                RectangleF a = new RectangleF(cx - 350, top, 280, 320);
                using (GraphicsPath rp = Gfx.Round(a, 16f)) { g.FillPath(white, rp); g.DrawPath(edge, rp); }
                for (int i = 0; i < 8; i++)
                {
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb(i % 3 == 0 ? 160 : 95, 90, 120, 160)))
                        g.FillRectangle(lb, a.X + 22, a.Y + 26 + i * 34, a.Width - 44 - (i % 3) * 44, 11);
                }
                // 右：拼出来的长条（分成几段，示意"一段段接上去"）
                RectangleF b = new RectangleF(cx + 80, top - 60, 280, 440);
                using (GraphicsPath rp = Gfx.Round(b, 16f)) { g.FillPath(white, rp); g.DrawPath(edge, rp); }
                for (int i = 0; i < 11; i++)
                {
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb(i % 4 == 0 ? 160 : 95, 90, 120, 160)))
                        g.FillRectangle(lb, b.X + 22, b.Y + 24 + i * 36, b.Width - 44 - (i % 4) * 40, 11);
                    if (i == 3 || i == 7)     // 拼接缝
                        using (Pen sp = new Pen(Color.FromArgb(150, 0, 138, 226), 2f))
                            g.DrawLine(sp, b.X + 10, b.Y + 24 + i * 36 - 8, b.Right - 10, b.Y + 24 + i * 36 - 8);
                }
            }
            using (Font f = new Font(FONT, 52, FontStyle.Bold))
            using (SolidBrush sb = new SolidBrush(Accent))
                g.DrawString("→", f, sb, cx - 46, top + 120);
            using (Font f = new Font(FONT, 24))
            using (SolidBrush sb = new SolidBrush(Sub))
            {
                g.DrawString("一屏一屏自动滚", f, sb, cx - 360, top + 350);
                g.DrawString("无缝拼成一张长图", f, sb, cx + 90, top + 400);
            }
        }
        // 画文字之前**实测宽度**：超了就按比例缩字号（可用宽度由调用方给）。
        // 以前靠"字数 × 估的每字宽"判断，中文实际比估的宽，于是图7/图8 的第一行被右边缘切掉。
        static float FitSize(Graphics g, string text, float pt, int maxW)
        {
            if (string.IsNullOrEmpty(text)) return pt;
            using (Font f = new Font(FONT, pt))
            {
                // 用 TextRenderer 量（GDI），和下面的 DrawText 同源 —— GDI+ 的 MeasureString 量出来比实际画出来的窄，之前就是被它骗了
                float w = TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                if (w <= maxW * 0.94f || w <= 0) return pt;   // 再留 6% 余量
                float np = pt * (maxW * 0.94f / w);
                return np < 12f ? 12f : np;
            }
        }
        static void Backdrop(Graphics g, int w, int h, int seed)
        {
            using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), BgTop, BgBottom, 62f))
                g.FillRectangle(lg, 0, 0, w, h);
            // 一点光斑，避免整张太平
            int ox = 200 + (seed * 137) % 640;
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(ox - 420, h - 720, 840, 840);
                using (PathGradientBrush pb = new PathGradientBrush(gp))
                {
                    pb.CenterColor = Color.FromArgb(38, 0, 138, 226);
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, 0, 138, 226) };
                    g.FillPath(pb, gp);
                }
            }
        }

        static void Chip(Graphics g, string text, int x, int y, int fs)
        {
            using (Font f = new Font(FONT, fs, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(text, f);
                Rectangle r = new Rectangle(x, y, (int)sz.Width + 40, (int)sz.Height + 16);
                using (GraphicsPath gp = new GraphicsPath())
                {
                    int rad = r.Height / 2;
                    gp.AddArc(r.X, r.Y, rad * 2, r.Height, 90, 180);
                    gp.AddArc(r.Right - rad * 2, r.Y, rad * 2, r.Height, 270, 180);
                    gp.CloseFigure();
                    using (SolidBrush sb = new SolidBrush(ChipBg)) g.FillPath(sb, gp);
                    using (Pen p = new Pen(Color.FromArgb(70, 120, 200, 255), 1.2f)) g.DrawPath(p, gp);
                }
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(235, 220, 235, 250)))
                    g.DrawString(text, f, sb, r.X + 20, r.Y + 8);
            }
        }
    }
}
