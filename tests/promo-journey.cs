using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;

namespace SnapWheel
{
    // 宣传图：从 v0.6 到 v0.9 的重大更新 + 正式版预告
    //
    // 生成：1080×1440 竖版（适合发微博/小红书/朋友圈），输出到桌面。
    // 用的是项目自己的绘制层（DrawKit + Gfx），顺便验证这套绘制原语够不够用。
    static class PromoJourney
    {
        const int W = 1080, H = 1440;

        [STAThread]
        static void Main()
        {
            using (Bitmap b = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                Draw(g);

                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string path = Path.Combine(dir, "SnapWheel-宣传图-0.6到0.9.png");
                b.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("已生成：" + path);
            }
        }

        static void Draw(Graphics g)
        {
            // ---------- 背景：深色渐变 ----------
            using (LinearGradientBrush bg = new LinearGradientBrush(
                new Rectangle(0, 0, W, H), Color.FromArgb(24, 27, 34), Color.FromArgb(14, 16, 21), 90f))
                g.FillRectangle(bg, 0, 0, W, H);

            // 顶部一层很淡的品牌色光晕
            using (GraphicsPath glow = new GraphicsPath())
            {
                glow.AddEllipse(-300, -520, 1700, 1000);
                using (PathGradientBrush pb = new PathGradientBrush(glow))
                {
                    pb.CenterColor = Color.FromArgb(46, 0, 122, 204);
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, 0, 122, 204) };
                    g.FillPath(pb, glow);
                }
            }

            int pad = 84;
            int y = 92;

            // ---------- 品牌行 ----------
            using (Font fb = new Font(DrawKit.UI, 34f, FontStyle.Bold))
                DrawKit.Line(g, "SnapWheel 快照轮环", pad, y, 34, Color.FromArgb(236, 240, 246), FontStyle.Bold);
            y += 56;

            using (Font fs = new Font(DrawKit.UI, 21f))
                DrawKit.Line(g, "截图 · 标注 · 取字 · 长图 —— 一个 exe，零依赖", pad, y, 21, Color.FromArgb(150, 160, 175), FontStyle.Regular);
            y += 76;

            // ---------- 主标题 ----------
            using (Font ft = new Font(DrawKit.UI, 62f, FontStyle.Bold))
                DrawKit.Line(g, "从 0.6 到 0.9", pad, y, 62, Color.White, FontStyle.Bold);
            y += 92;

            using (Font ft2 = new Font(DrawKit.UI, 30f))
                DrawKit.Line(g, "这一路加了这些", pad, y, 30, Color.FromArgb(120, 190, 240), FontStyle.Regular);
            y += 78;

            // ---------- 版本条目 ----------
            y = Card(g, pad, y, "0.6", "滚动长截图 · 翻译换引擎链",
                     "一整页网页一次拍完；翻译走免费引擎链，无 key 也能用", false);

            y = Card(g, pad, y, "0.7", "中英双语 · 符号标注 · 另存为",
                     "界面可切 English；标注多了符号工具；Ctrl+S 另存为", false);

            y = Card(g, pad, y, "0.8", "底层重构（看不见但重要）",
                     "抽出绘制度量层、拆开超大文件 —— 更稳，也更好继续做", false);

            y = Card(g, pad, y, "0.9", "键盘也能把图送出去",
                     "传递模式：按 Ctrl+Alt+C，方向键控制「假光标」，空格放下", true);

            y = Card(g, pad, y, "0.9", "引导改版 · 自动更新",
                     "首次打开只给三步；托盘可一键检查更新，免费、免安装", false);

            // ---------- 底部：正式版预告 ----------
            int by = H - 210;
            using (GraphicsPath plate = Gfx.Round(new Rectangle(pad, by, W - pad * 2, 130), 22f))
            {
                using (LinearGradientBrush pb = new LinearGradientBrush(
                    new Rectangle(pad, by, W - pad * 2, 130), Color.FromArgb(0, 122, 204), Color.FromArgb(0, 92, 172), 0f))
                    g.FillPath(pb, plate);
            }
            using (Font fv = new Font(DrawKit.UI, 40f, FontStyle.Bold))
                DrawKit.DrawFitted(g, "1.0 正式版 · 即将发布", new RectangleF(pad + 34, by + 26, W - pad * 2 - 68, 52),
                                   Color.White, 40, W - pad * 2 - 68, DrawKit.UI, FontStyle.Bold, Align.Center);
            using (Font fv2 = new Font(DrawKit.UI, 20f))
                DrawKit.DrawFitted(g, "摘掉 beta 标记 —— 功能已齐，正在做最后的稳定性验证",
                                   new RectangleF(pad + 34, by + 80, W - pad * 2 - 68, 32),
                                   Color.FromArgb(225, 240, 252), 20, W - pad * 2 - 68, DrawKit.UI, FontStyle.Regular, Align.Center);

            using (Font ff = new Font(DrawKit.UI, 18f))
                DrawKit.Line(g, "github.com/ExpertKT/SnapWheel", pad, H - 56, 18, Color.FromArgb(120, 130, 145), FontStyle.Regular);
        }

        // 一张卡片：左边版本号，右边标题 + 说明。hot=true 时用品牌色描边（突出这一版的重点）
        static int Card(Graphics g, int pad, int y, string ver, string title, string desc, bool hot)
        {
            int h = 130, w = W - pad * 2;

            using (GraphicsPath p = Gfx.Round(new Rectangle(pad, y, w, h), 18f))
            {
                using (SolidBrush b = new SolidBrush(hot ? Color.FromArgb(38, 0, 122, 204) : Color.FromArgb(255, 26, 30, 38)))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(hot ? Color.FromArgb(210, 0, 150, 240) : Color.FromArgb(58, 60, 70), hot ? 2.4f : 1.4f))
                    g.DrawPath(pen, p);
            }

            // 版本号
            DrawKit.DrawFitted(g, ver, new RectangleF(pad + 28, y + 34, 130, 60),
                               hot ? Color.FromArgb(120, 200, 255) : Color.FromArgb(120, 132, 150), 40, 130, DrawKit.UI, FontStyle.Bold, Align.Near);

            // 标题 + 说明
            int tx = pad + 176;
            int tw = w - 176 - 32;
            DrawKit.DrawFitted(g, title, new RectangleF(tx, y + 30, tw, 44),
                               hot ? Color.White : Color.FromArgb(232, 236, 243), 30, tw, DrawKit.UI, FontStyle.Bold, Align.Near);
            DrawKit.DrawFitted(g, desc, new RectangleF(tx, y + 78, tw, 34),
                               Color.FromArgb(146, 156, 170), 19, tw, DrawKit.UI, FontStyle.Regular, Align.Near);

            return y + h + 18;
        }
    }
}
