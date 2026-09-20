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
using System.Collections.Generic;
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
        // 程序体积（KB）：**从构建产物读出来**，不写死。
        // 这个数原来硬编码成 274 KB，而实际早就 365 KB 了 —— 正是 RELEASE.md 开头点名的那个坑。
        static string kbText = "— KB";

        static void ResolveKb(string hint)
        {
            try
            {
                string p = hint;
                if (string.IsNullOrEmpty(p) || !File.Exists(p))
                {
                    // 没给就自己找：调用方一般是在仓库根目录跑的
                    string[] cand = {
                        Path.Combine(Directory.GetCurrentDirectory(), @"build\SnapWheel.exe"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\build\SnapWheel.exe"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\build\SnapWheel.exe"),
                        Path.Combine(Directory.GetCurrentDirectory(), @"..\build\SnapWheel.exe"),
                    };
                    p = null;
                    for (int i = 0; i < cand.Length; i++) if (File.Exists(cand[i])) { p = cand[i]; break; }
                }
                if (p == null || !File.Exists(p)) { kbText = "— KB"; return; }
                kbText = ((int)Math.Round(new FileInfo(p).Length / 1024.0)) + " KB";
            }
            catch { kbText = "— KB"; }
        }

        static void Main(string[] args)
        {
            outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "snapwheel_promo9");
            Directory.CreateDirectory(outDir);
            shotDir = Path.Combine(Path.GetTempPath(), "snapwheel_ui");
            ResolveKb(args.Length > 1 ? args[1] : null);      // 第二个参数可选：直接给 build\SnapWheel.exe 的路径

            try
            {
                Shot(1, "截图不落文件", "拖一下就发出去，环上还留着一份", "", "wheel_bl.png", true);
                Shot(2, "截完就滑进角落", "不弹保存框 · 不用切窗口 · 不用翻文件夹", "Ctrl+Shift+S 框选，图自己滑进环里", "wheel_intro80.png", false);
                Shot(3, "拖出去 = 发出去", "微信 / QQ / 文档 / 文件夹，松手就到", "环上还留着一份，随时能再拖一次", "wheel_drop.png", false);
                Shot(4, "滚动长截图", "框一块区域，剩下的它自己滚、自己拼", "边滚边无缝拼接 · 到底自动停", "wheel_empty.png", false);
                Center5();
                Shot(6, "取字 + 翻译", "圈住文字就认出来，一键翻译成中/英文", "低对比度也能认 · 默认免费接口", "ocr.png", false);
                Shot(7, "万能键：一个圆盘管所有", "新建 / 切换 / 删除 / 上一个，四个方向四个动作", "长按圆盘拖向对应方向松手即可", "wheel_menu.png", false);
                Shot(8, "标注 · 贴图 · 后悔药", "箭头方框马赛克文字 · 中键钉在屏幕上 · 删错能找回", "四色可选 · Ctrl+Z 撤销 · 最近 8 次都能撤", "annotate.png", false);
                Shot(9, "开源 · MIT", "github.com/ExpertKT/SnapWheel", "完整版 / 无万能键版都在 Releases", "settings.png", false);
                Merge();
                Console.WriteLine("完成：9 张图 + 一张九宫格总览已输出到 " + outDir);
            }
            catch (Exception ex) { Console.WriteLine("失败：" + ex.Message); }
        }

        static Bitmap Load(string file)
        {
            // ⚠️ 先看清楚"谁真的会产出这张图"。
            // 原来这里点名的 promo_wheel / promo_drop / promo_menu / promo_intro 四张，
            // **现在的 ui-shot 根本不生成** —— 它们是 09-14 留下的旧文件，一直躺在 %TEMP% 里，
            // 于是宣传图上配的是好几天前的界面，而且没有任何东西会报错。
            // 现在只用两种来源：ui-shot 真的会写的（wheel_*.png / settings.png …），
            // 以及仓库 docs\ 下那些**进了版本库**的正式渲染（annotate / ocr / menu …）。
            string p = Path.Combine(shotDir, file);
            if (!File.Exists(p))
            {
                string alt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "docs", file);
                if (File.Exists(alt)) p = alt;
                else
                {
                    alt = Path.Combine(Directory.GetCurrentDirectory(), "docs", file);
                    if (File.Exists(alt)) p = alt;
                }
            }
            if (!File.Exists(p)) { Console.WriteLine("      ★ 找不到配图 " + file + "（这张会空着）"); return null; }
            try { using (Bitmap b = new Bitmap(p)) return Trim(new Bitmap(b)); } catch { return null; }
        }

        // 把渲染图裁到"真正有内容"的那一块。
        // 为什么必须裁：这些渲染图是定尺寸画布（660×660），轮盘本身只占角落一小块，
        // 其余都是透明的。直接按原图缩到 440 高 → 真正看得见的轮盘只有一百多像素，
        // 摆进九宫格就是"一个小白方块"，缩到朋友圈缩略图完全看不出是什么。
        // 用 LockBits 扫（项目规矩：逐像素绝不走 GetPixel）。
        static Bitmap Trim(Bitmap b)
        {
            int minX = b.Width, minY = b.Height, maxX = -1, maxY = -1;
            System.Drawing.Imaging.BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = d.Stride, hgt = b.Height, wid = b.Width;
                byte[] buf = new byte[stride * hgt];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                for (int y = 0; y < hgt; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < wid; x++)
                    {
                        if (buf[row + x * 4 + 3] > 12)          // 只要 alpha 够，就算有内容
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
            }
            finally { b.UnlockBits(d); }
            if (maxX < minX || maxY < minY) return b;           // 全透明：原样还回去
            int pad = 14;
            minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
            maxX = Math.Min(b.Width - 1, maxX + pad); maxY = Math.Min(b.Height - 1, maxY + pad);
            Rectangle r = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            Bitmap o = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(o))
                g.DrawImage(b, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            b.Dispose();
            return o;
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
                float ts = 62;                                  // 全统一：不再给主图单独放大（那会让它和图5 对齐）   // 主图 96 太大（压住副文案），收到 72 与其余统一      // 统一 62：76 太大（长标题会被右边缘裁），62 既齐又放得下 11 字
                using (Font ft = new Font(FONT, ts, FontStyle.Bold))
                using (SolidBrush sb = new SolidBrush(Color.White))
                    TextRenderer.DrawText(g, title, ft, new Point(80, 216), Color.White, TextFormatFlags.NoPadding);

                int y = 330;   // 主图是纯文字封面：标题(底约390)与第一行之间要留出呼吸感   // 主图文案再上移 30：它的副文案底原本到 536，比卡片顶(526)多出 10px      // 固定行位置：标题底(约300) 之下，所有图的说明文字落在同一条线上
                using (Font fl = new Font(FONT, FitSize(g, line1, 36, 1080 - 168)))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(228, 236, 244, 252)))
                { TextRenderer.DrawText(g, line1, fl, new Point(84, (int)y), Color.FromArgb(228, 236, 244, 252), TextFormatFlags.NoPadding); }
                using (Font fs = new Font(FONT, FitSize(g, line2, 28, 1080 - 168)))
                using (SolidBrush sb = new SolidBrush(Sub))
                { TextRenderer.DrawText(g, line2, fs, new Point(84, (int)(y + 58)), Color.FromArgb(148, 158, 178), TextFormatFlags.NoPadding); }

                // 右上角标：**过期的角标一个都不留**。
                // 原来图4/图6 挂着「0.6.0 新增」—— 那是好几个版本以前的东西了，
                // 挂在那儿等于告诉别人"这是新功能"。只有真的这次新加的才配挂角标。
                if (no == 1) Chip(g, "1.0 正式版", 1080 - 320, 122, 22);

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
        // 正中心那张：品牌 + 硬数据 + 一个抽象图形（弧 + 缩略图方块 + 中心圆 = 轮盘自己的意象）。
        // 原来它只有文字，是九张里唯一没画面的，摆在最中间就显得空。
        // 正中心那张：**一切居中**（其余八张都是左对齐，只有这一张居中，作为九宫格的正中显得庄重）。
        // 不再画抽象图形 —— 我上一版加的那段弧+方块并不好看，纯排版反而更稳。
        static void Center5()
        {
            using (Bitmap b = new Bitmap(1080, 1080, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                Backdrop(g, b.Width, b.Height, 5);

                using (Font fb = new Font(FONT, 26, FontStyle.Bold))
                    TextRenderer.DrawText(g, "SnapWheel", fb, new Point(86, 74), Accent, TextFormatFlags.NoPadding);
                using (Font fn = new Font(FONT, 22))
                    TextRenderer.DrawText(g, "5 / 9", fn, new Point(1080 - 86 - 74, 78), Color.FromArgb(110, 150, 160, 180), TextFormatFlags.NoPadding);

                // 大标题已经由 CenterBox 画掉了（见下面 boxes）

                // 下面这一串**位置由实测高度算出来**，不再写死 y。
                // 上一版写死了一串 y，"快照轮环"（84pt）的底正好压住下面那行"1.0 正式版" ——
                // 和界面里那些"量的时候用一套、画的时候用另一套"是同一个形状。
                // 量完再画，结构上就不可能重叠。
                List<Rectangle> boxes = new List<Rectangle>();
                // 大标题也走 CenterBox —— **画和量同一个来源**，不另算一份。
                boxes.Add(CenterBox(g, "快照轮环", 84, FontStyle.Bold, 250, Color.White, 540));
                boxes.AddRange(CenterStack(g, 430, 14, new string[] {
                    "1.0 正式版",
                    "一个常驻屏幕角落的圆环，把截图这件事变顺手"
                }, new float[] { 46, 26 }, new FontStyle[] { FontStyle.Bold, FontStyle.Regular },
                   new Color[] { Accent, Sub }));

                // 三个硬数据：一行三列，各自居中。
                // ⚠️ 体积**必须是量出来的**：这里原来写死 274 KB，而实际早就 365 KB 了 ——
                // 正是 docs/RELEASE.md 开头点名的那个"同一个事实写在两个地方，迟早不一致"。
                // 现在从构建产物读，读不到就问调用方要，绝不猜。
                string[] num = { kbText, "0", "1" };
                string[] cap = { "整个程序的大小", "第三方依赖", "个 exe 双击就跑" };
                int[] col = { 240, 540, 840 };
                for (int i = 0; i < 3; i++)
                {
                    Center(g, num[i], 54, FontStyle.Bold, 620, Accent, col[i]);
                    Center(g, cap[i], 24, FontStyle.Regular, 702, Sub, col[i]);
                    using (Font f2 = new Font(FONT, 54, FontStyle.Bold))
                    {
                        Size s2 = TextRenderer.MeasureText(g, num[i], f2, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                        boxes.Add(new Rectangle(col[i] - s2.Width / 2, 620, s2.Width, s2.Height));
                    }
                }
                boxes.AddRange(CenterStack(g, 806, 16, new string[] {
                    "这一版没加功能，只让它「有反应」：",
                    "拖出去有拖痕 · 新截的会亮 · 切轮盘会翻 · 环会随内容变粗",
                    "Windows 10 / 11 · 免安装 · 开源 MIT"
                }, new float[] { 28, 26, 26 }, new FontStyle[] { FontStyle.Regular, FontStyle.Regular, FontStyle.Regular },
                   new Color[] { Color.FromArgb(215, 226, 238, 250), Sub, Color.FromArgb(200, 226, 234, 244) }));

                // 中心这张图上**所有文字块两两不重叠、且全在画布内** —— 由程序量，不靠人看。
                // 用户点名要避免的"文字被裁/溢出/叠在一起"，这类事只有量才靠得住。
                int bad = 0, clipped = 0;
                for (int i = 0; i < boxes.Count; i++)
                {
                    Rectangle r = boxes[i];
                    if (r.X < 40 || r.Y < 40 || r.Right > 1080 - 40 || r.Bottom > 1080 - 40) clipped++;
                    for (int j = i + 1; j < boxes.Count; j++)
                        if (r.IntersectsWith(boxes[j])) bad++;
                }
                Console.WriteLine(bad == 0 && clipped == 0
                    ? "  图5 文字自检：  " + boxes.Count + " 块文字块，无重叠、无出界"
                    : "  图5 文字自检：  ★ 有 " + bad + " 处重叠、" + clipped + " 处出界 ★  文字块数 " + boxes.Count);

                b.Save(Path.Combine(outDir, "图5.png"), System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("  写出 图5.png  快照轮环（中心品牌位）");
            }
        }

        // 竖向堆叠居中：**位置由实测高度算出来**，不写死 y（见 Center5 里的说明）。
        // 顺带把每行的矩形还回来 —— 调用方要拿它去查"有没有压到别的东西"。
        static List<Rectangle> CenterStack(Graphics g, int top, int gap, string[] texts, float[] pts, FontStyle[] styles, Color[] cols)
        {
            List<Rectangle> boxes = new List<Rectangle>();
            int y = top;
            for (int i = 0; i < texts.Length; i++)
            {
                using (Font f = new Font(FONT, FitSize(g, texts[i], pts[i], 1000), styles[i]))
                {
                    Size sz = TextRenderer.MeasureText(g, texts[i], f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                    if (sz.Width > 1080 - 120) sz = new Size(1080 - 120, sz.Height);      // 兜底：真放不下按最宽算
                    Rectangle box = new Rectangle(540 - sz.Width / 2, y, sz.Width, sz.Height);
                    TextRenderer.DrawText(g, texts[i], f, box, cols[i], TextFormatFlags.NoPadding);
                    boxes.Add(box);
                    y += sz.Height + gap;
                }
            }
            return boxes;
        }

        // 居中画一行字（cx 省略时按整幅 1080 居中）
        static void Center(Graphics g, string text, float pt, FontStyle st, int y, Color col)
        {
            Center(g, text, pt, st, y, col, 540);
        }

        static void Center(Graphics g, string text, float pt, FontStyle st, int y, Color col, int cx)
        {
            CenterBox(g, text, pt, st, y, col, cx);
        }

        // **画和量同源**：返回的就是刚才真正画出去的那个矩形。
        // 为什么强调这一条：第一版自检里我是"另外按坐标算一个矩形"塞进去比对的 ——
        // 负向验证时把标题挪到会叠字的位置，自检照样报"无重叠"，**因为它量的和画的不是一回事**。
        // 这个项目在绘制上摔过四次同一个坑（量用一套、画用另一套），这里不犯第五次。
        static Rectangle CenterBox(Graphics g, string text, float pt, FontStyle st, int y, Color col, int cx)
        {
            using (Font f = new Font(FONT, FitSize(g, text, pt, 1000), st))
            {
                Size sz = TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                Rectangle box = new Rectangle(cx - sz.Width / 2, y, sz.Width, sz.Height);
                TextRenderer.DrawText(g, text, f, box, col, TextFormatFlags.NoPadding);
                return box;
            }
        }

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
