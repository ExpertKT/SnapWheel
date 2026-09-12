// 宣传素材生成器：把界面渲染图和文案合成成可以直接发朋友圈的方图
// 编译：csc /nologo /target:exe /main:SnapWheel.PromoShot /out:promo.exe SnapWheel.cs tests\promo-shot.cs
// 用法：promo.exe <输出目录>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SnapWheel
{
    static class PromoShot
    {
        const string FONT = "Microsoft YaHei UI";
        static readonly Color BgTop = Color.FromArgb(30, 38, 58);
        static readonly Color BgBottom = Color.FromArgb(14, 16, 22);
        static readonly Color Accent = Color.FromArgb(0, 132, 220);
        static readonly Color Sub = Color.FromArgb(150, 158, 175);

        static void F(object o, string name, object val)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null) fi.SetValue(o, val);
        }

        static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(5); }
        }

        // ---------- 背景：深色渐变 + 两团柔光 ----------
        static void Background(Graphics g, int w, int h)
        {
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), BgTop, BgBottom, 62f))
                g.FillRectangle(lg, 0, 0, w, h);
            Blob(g, w * 0.82f, h * 0.18f, w * 0.55f, Color.FromArgb(90, 40, 130, 230));
            Blob(g, w * 0.12f, h * 0.86f, w * 0.5f, Color.FromArgb(70, 220, 120, 170));
        }

        static void Blob(Graphics g, float cx, float cy, float r, Color c)
        {
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                using (var pg = new PathGradientBrush(p))
                {
                    pg.CenterPoint = new PointF(cx, cy);
                    pg.CenterColor = c;
                    pg.SurroundColors = new Color[] { Color.FromArgb(0, c.R, c.G, c.B) };
                    g.FillPath(pg, p);
                }
            }
        }

        static void Text(Graphics g, string s, float x, float y, float size, Color c,
                         FontStyle st = FontStyle.Regular, StringAlignment al = StringAlignment.Near,
                         float boxW = 0f)
        {
            using (var f = new Font(FONT, size, st, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat())
            {
                sf.Alignment = al;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(s, f, b, new RectangleF(x, y, boxW <= 0 ? 4000 : boxW, size * 2f), sf);
            }
        }

        static void Chip(Graphics g, string s, ref float x, float y, float size)
        {
            using (var f = new Font(FONT, size, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                SizeF sz = g.MeasureString(s, f);
                float w = sz.Width + size * 1.7f, h = size * 2.2f;
                var r = new RectangleF(x, y, w, h);
                using (var p = Gfx.Round(r, h / 2f))
                {
                    using (var b = new SolidBrush(Color.FromArgb(46, 255, 255, 255))) g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1.4f)) g.DrawPath(pen, p);
                }
                using (var b = new SolidBrush(Color.FromArgb(226, 234, 245)))
                    g.DrawString(s, f, b, x + size * 0.85f, y + (h - sz.Height) / 2f + 1);
                x += w + size * 0.6f;
            }
        }

        static Bitmap Load(string p)
        {
            try { return new Bitmap(p); } catch { return null; }
        }

        // 把图片按比例塞进一个矩形
        static void Fit(Graphics g, Bitmap img, RectangleF box)
        {
            if (img == null) return;
            float s = Math.Min(box.Width / img.Width, box.Height / img.Height);
            float w = img.Width * s, h = img.Height * s;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, box.X + (box.Width - w) / 2f, box.Y + (box.Height - h) / 2f, w, h);
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "snapwheel_promo");
            Directory.CreateDirectory(outDir);
            string shotDir = Path.Combine(Path.GetTempPath(), "snapwheel_ui");
            Directory.CreateDirectory(shotDir);
            Console.WriteLine("输出: " + outDir);

            // 先用轮盘本体渲染一套图（右下角贴壁纸的干净效果）
            Settings s = Settings.Load();
            s.SaveToDisk = false; s.ThumbSize = 96; s.Radius = 300; s.Slots = 5; s.LabelSize = 16;
            s.Corner = "BL"; s.UiScale = 100; s.GlassPercent = 40; s.UiStyle = "neu";
            s.ShowNameLabel = true; s.ShowCountLabel = true; s.AccentIndex = 0;
            WheelManager mgr = new WheelManager(s);
            mgr.ActiveWheel.Name = "设计稿";
            mgr.ActiveWheel.ColorIndex = 0;
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;
            while (st.Items.Count > 0) st.Items.RemoveAt(0);

            Action<int, int, Color> add = delegate(int w, int h, Color c)
            {
                Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    using (var br = new SolidBrush(c)) g.FillRectangle(br, 0, 0, w, h);
                    using (var p = new Pen(Color.FromArgb(70, 255, 255, 255), 3)) g.DrawRectangle(p, 3, 3, w - 7, h - 7);
                }
                st.Add(b);
            };
            add(320, 200, Color.FromArgb(88, 150, 224));
            add(200, 200, Color.FromArgb(224, 138, 96));
            add(240, 140, Color.FromArgb(96, 196, 138));
            add(170, 230, Color.FromArgb(178, 130, 222));
            add(300, 180, Color.FromArgb(232, 96, 122));

            WheelForm f = new WheelForm(mgr, s);
            f.ShowWheel();
            Pump(700);
            F(f, "_show", 1f);
            PromoBackdrop(f);
            Pump(120);

            MethodInfo dw = typeof(WheelForm).GetMethod("DrawWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            int cw = f.Width, ch = f.Height;
            Action<string, Action<WheelForm>> render = delegate(string file, Action<WheelForm> setup)
            {
                // 每次渲染前把状态清干净，否则上一张的菜单/拖放态会串到下一张
                F(f, "_menuOpen", false); F(f, "_menuT", 0f); F(f, "_sector", -1); F(f, "_keyDown", false);
                F(f, "_intro", false); F(f, "_introT", 1f);
                F(f, "_dropActive", false); F(f, "_dropExternal", false); F(f, "_dropCount", 0);
                F(f, "_hover", -1); F(f, "_enlarged", -1); F(f, "_peekIndex", -1);
                if (setup != null) setup(f);
                F(f, "_show", 1f);
                using (Bitmap b = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        dw.Invoke(f, new object[] { g, cw, ch });     // 透明底，叠到渐变上
                    }
                    b.Save(Path.Combine(shotDir, file), ImageFormat.Png);
                }
            };
            render("promo_wheel.png", null);
            render("promo_menu.png", delegate(WheelForm x) { F(x, "_menuOpen", true); F(x, "_menuT", 1f); F(x, "_sector", 1); F(x, "_keyDown", true); });
            render("promo_intro.png", delegate(WheelForm x) { F(x, "_intro", true); F(x, "_introT", 0.45f); });
            render("promo_drop.png", delegate(WheelForm x)
            {
                F(x, "_intro", false); F(x, "_dropActive", true); F(x, "_dropExternal", true); F(x, "_dropCount", 3);
            });
            F(f, "_menuOpen", false); F(f, "_menuT", 0f); F(f, "_keyDown", false); F(f, "_intro", false);
            F(f, "_dropActive", false); F(f, "_dropExternal", false); F(f, "_dropCount", 0);
            try { f.HideWheel(); f.Close(); } catch { }

            Bitmap wheel = Load(Path.Combine(shotDir, "promo_wheel.png"));
            Bitmap menu = Load(Path.Combine(shotDir, "promo_menu.png"));
            Bitmap intro = Load(Path.Combine(shotDir, "promo_intro.png"));
            Bitmap drop = Load(Path.Combine(shotDir, "promo_drop.png"));
            // 玻璃细节：从轮盘渲染图里裁一块放大
            Bitmap glass = null;
            if (wheel != null)
            {
                var src = new Rectangle((int)(cw * 0.09f), (int)(ch * 0.70f), (int)(cw * 0.30f), (int)(ch * 0.24f));
                glass = new Bitmap(src.Width * 2, src.Height * 2, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(glass))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(wheel, new Rectangle(0, 0, glass.Width, glass.Height), src, GraphicsUnit.Pixel);
                }
            }
            // 设置窗口：用一条通用路径渲染，避免把本机用户名放进宣传图
            Bitmap setting = null;
            try
            {
                Settings s2 = Settings.Load();
                s2.Dir = @"C:\Users\Public\Pictures\SnapWheel";
                s2.UiStyle = "neu"; s2.GlassPercent = 40; s2.UiScale = 0;
                SettingsForm sf = new SettingsForm(s2);
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new Point(-4000, -4000);
                sf.Show();
                Pump(500);
                setting = new Bitmap(sf.Width, sf.Height);
                sf.DrawToBitmap(setting, new Rectangle(0, 0, setting.Width, setting.Height));
                sf.Close(); sf.Dispose();
            }
            catch { }
            Bitmap guide = Load(Path.Combine(shotDir, "guide.png"));

            // ---------- 图 1：主图 ----------
            using (Bitmap b = new Bitmap(1080, 1080))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    Background(g, 1080, 1080);
                    Text(g, "SnapWheel", 84, 96, 26, Accent, FontStyle.Bold);
                    Text(g, "截图轮盘", 82, 132, 82, Color.White, FontStyle.Bold);
                    Text(g, "截完自动滑进屏幕角落的环里", 86, 246, 34, Color.FromArgb(226, 232, 244));
                    Text(g, "拖出去就用 · 也能把图拖回来收着", 86, 294, 34, Color.FromArgb(226, 232, 244));
                    // 轮盘贴在右下
                    if (wheel != null) Fit(g, wheel, new RectangleF(430, 400, 640, 640));
                    float cx = 86, cy = 396;
                    Chip(g, "绿色免安装", ref cx, cy, 26); cy += 62; cx = 86;
                    Chip(g, "114 KB 单文件", ref cx, cy, 26); cy += 62; cx = 86;
                    Chip(g, "开源 MIT", ref cx, cy, 26);
                    Text(g, "Windows 10 / 11", 84, 1010, 24, Sub);
                }
                b.Save(Path.Combine(outDir, "图1-主图.png"), ImageFormat.Png);
            }

            // ---------- 图 2：四宫格功能 ----------
            using (Bitmap b = new Bitmap(1080, 1080))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    Background(g, 1080, 1080);
                    Text(g, "它都能干什么", 60, 52, 46, Color.White, FontStyle.Bold);
                    string[] cap = { "截图轮盘", "万能键", "拖进拖出", "新手引导 + 开启动画" };
                    string[] sub = { "截完自动滑入，滚轮翻图", "长按切 Wheel / 新建 / 删除", "和微信、文件夹直接对拖", "第一次打开就有引导" };
                    Bitmap[] img = { wheel, menu, drop, guide };
                    for (int i = 0; i < 4; i++)
                    {
                        float x = 60 + (i % 2) * 500, y = 150 + (i / 2) * 470;
                        var cell = new RectangleF(x, y, 460, 420);
                        using (var p = Gfx.Round(cell, 26f))
                        {
                            using (var bgb = new SolidBrush(Color.FromArgb(38, 255, 255, 255))) g.FillPath(bgb, p);
                            using (var pen = new Pen(Color.FromArgb(52, 255, 255, 255), 1.6f)) g.DrawPath(pen, p);
                        }
                        Fit(g, img[i], new RectangleF(x + 14, y + 14, 432, 300));
                        Text(g, cap[i], x + 26, y + 326, 30, Color.White, FontStyle.Bold);
                        Text(g, sub[i], x + 26, y + 366, 22, Sub);
                    }
                }
                b.Save(Path.Combine(outDir, "图2-功能.png"), ImageFormat.Png);
            }

            // ---------- 图 3：三步用法 ----------
            using (Bitmap b = new Bitmap(1080, 1080))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    Background(g, 1080, 1080);
                    Text(g, "用起来就三步", 60, 52, 46, Color.White, FontStyle.Bold);
                    string[] t = { "敲热键框选", "自动滑进轮盘", "拖到微信里" };
                    string[] d = { "Ctrl+Shift+S，拖一下就行", "不用保存、不用选路径", "缩略图直接拖进输入框" };
                    for (int i = 0; i < 3; i++)
                    {
                        float y = 190 + i * 250;
                        using (var p = Gfx.Round(new RectangleF(60, y, 960, 210), 24f))
                        {
                            using (var bgb = new SolidBrush(Color.FromArgb(36, 255, 255, 255))) g.FillPath(bgb, p);
                            using (var pen = new Pen(Color.FromArgb(48, 255, 255, 255), 1.6f)) g.DrawPath(pen, p);
                        }
                        using (var dot = new SolidBrush(Accent))
                            g.FillEllipse(dot, 92, y + 74, 62, 62);
                        Text(g, (i + 1).ToString(), 106, y + 82, 40, Color.White, FontStyle.Bold);
                        Text(g, t[i], 190, y + 62, 36, Color.White, FontStyle.Bold);
                        Text(g, d[i], 190, y + 116, 26, Sub);
                    }
                    Text(g, "轮盘就贴在屏幕角落，平时不挡事", 60, 990, 26, Sub);
                }
                b.Save(Path.Combine(outDir, "图3-三步.png"), ImageFormat.Png);
            }

            // ---------- 图 4：细节（毛玻璃 + 设置） ----------
            using (Bitmap b = new Bitmap(1080, 1080))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    Background(g, 1080, 1080);
                    Text(g, "细节也做了", 60, 52, 46, Color.White, FontStyle.Bold);
                    Fit(g, glass, new RectangleF(60, 130, 440, 360));
                    Text(g, "真·毛玻璃", 560, 168, 34, Color.White, FontStyle.Bold);
                    Text(g, "面板能透出背后的桌面", 560, 216, 24, Sub);
                    Text(g, "（自己抓屏 + 模糊裁进面板，", 560, 250, 22, Sub);
                    Text(g, "分层窗口用不了系统 acrylic）", 560, 280, 22, Sub);
                    Fit(g, setting, new RectangleF(60, 545, 960, 470));
                    Text(g, "外观、大小、快捷键全都能调", 60, 1035, 24, Sub);
                }
                b.Save(Path.Combine(outDir, "图4-细节.png"), ImageFormat.Png);
            }

            foreach (Bitmap x in new Bitmap[] { wheel, menu, intro, drop, glass, setting, guide }) { if (x != null) x.Dispose(); }
            Console.WriteLine("完成：4 张图已输出到 " + outDir);
        }

        // 用合成壁纸当玻璃底，避免把真实屏幕内容带进宣传图
        static void PromoBackdrop(WheelForm f)
        {
            try
            {
                int w = Math.Max(64, f.Width), h = Math.Max(64, f.Height);
                Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                        Color.FromArgb(255, 92, 138, 212), Color.FromArgb(255, 236, 182, 156), 35f))
                        g.FillRectangle(lg, 0, 0, w, h);
                    using (var sb = new SolidBrush(Color.FromArgb(255, 246, 248, 252)))
                        g.FillRectangle(sb, w / 5, h / 6, w * 3 / 5, h / 3);
                    using (var sb = new SolidBrush(Color.FromArgb(255, 132, 150, 178)))
                        g.FillRectangle(sb, w / 5, h / 6, w * 3 / 5, 30);
                    using (var sb = new SolidBrush(Color.FromArgb(230, 66, 133, 244))) g.FillEllipse(sb, w / 8, h / 2, w / 4, w / 4);
                    using (var sb = new SolidBrush(Color.FromArgb(210, 240, 150, 60))) g.FillEllipse(sb, w / 2, h / 3, w / 5, w / 5);
                    using (var sb = new SolidBrush(Color.FromArgb(220, 255, 255, 255))) g.FillRectangle(sb, 0, h * 3 / 4, w, h / 4);
                }
                F(f, "_backdropBlur", b);
                F(f, "_backdropValid", true);
                F(f, "_backdropOffset", new Point(0, 0));
            }
            catch { }
        }
    }
}
