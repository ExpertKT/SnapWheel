// 竖版 GIF 生成器：图1 那个封面做成动图 —— 轮盘展开 → 一张图飞进环里 → 再被拖出去用。
// 1080x1440（和小红书/抖音的其余 5 张同尺寸），背景与文字**只渲染一次**当底图，只有轮盘那块逐帧变。
// 全部离线渲染：桌面是画的、光标是画的 —— 不截真实屏幕、不模拟真实输入。
// 编译：csc /nologo /target:exe /main:SnapWheel.PromoVGif /out:promovg.exe src\*.cs tests\promo-vertical-gif.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SnapWheel
{
    static class PromoVGif
    {
        const int W = 1080, H = 1440;
        const int WheelTop = 500;              // 上移：下面还要放一行小字，帧也会被裁紧
        const string FONT = "Microsoft YaHei UI";
        static readonly Color Accent = Color.FromArgb(0, 148, 240);
        static readonly Color Sub = Color.FromArgb(170, 182, 202);

        static void F(object o, string n, object v)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("no field " + n);
            fi.SetValue(o, v);
        }
        static object Call(object o, string n, params object[] a)
        {
            MethodInfo[] all = o.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < all.Length; i++)
                if (all[i].Name == n && all[i].GetParameters().Length == a.Length) return all[i].Invoke(o, a);
            throw new Exception("no method " + n);
        }
        static float Ease(float t) { return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t); }
        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        static Bitmap MakeShot(Color c)
        {
            Bitmap b = new Bitmap(420, 250, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, 420, 250), c, Gfx.Shade(c, -0.35f), 45f))
                    g.FillRectangle(lg, 0, 0, 420, 250);
                using (SolidBrush w = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    g.FillRectangle(w, 34, 40, 300, 16);
                using (SolidBrush w2 = new SolidBrush(Color.FromArgb(180, 255, 255, 255)))
                {
                    g.FillRectangle(w2, 34, 84, 240, 12);
                    g.FillRectangle(w2, 34, 112, 268, 12);
                    g.FillRectangle(w2, 34, 176, 180, 12);
                }
            }
            return b;
        }

        static void Cursor(Graphics g, float x, float y)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(250, 255, 255, 255)))
            using (Pen p = new Pen(Color.FromArgb(200, 20, 30, 45), 1.6f))
            {
                PointF[] pts = new PointF[] {
                    new PointF(x, y), new PointF(x, y + 26), new PointF(x + 7, y + 19),
                    new PointF(x + 12, y + 29), new PointF(x + 17, y + 27), new PointF(x + 12, y + 17), new PointF(x + 21, y + 16) };
                g.FillPolygon(b, pts); g.DrawPolygon(p, pts);
            }
        }

        // 静态底：渐变背景 + 品牌行 + 大标题 + 副标题（每帧都用它，只有轮盘区是变的）
        static Bitmap BuildStatic()
        {
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, W, H),
                    Color.FromArgb(46, 62, 96), Color.FromArgb(15, 18, 26), 68f))
                    g.FillRectangle(lg, 0, 0, W, H);
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(140, H - 940, 1000, 1000);
                    using (PathGradientBrush pb = new PathGradientBrush(gp))
                    {
                        pb.CenterColor = Color.FromArgb(46, 0, 148, 240);
                        pb.SurroundColors = new Color[] { Color.FromArgb(0, 0, 148, 240) };
                        g.FillPath(pb, gp);
                    }
                }
                using (Font fb = new Font(FONT, 26, FontStyle.Bold))
                    TextRenderer.DrawText(g, "SnapWheel 快照轮环", fb, new Point(180, 64), Accent, TextFormatFlags.NoPadding);
                // 角标
                using (Font fc = new Font(FONT, 26, FontStyle.Bold))
                {
                    Size sz = TextRenderer.MeasureText(g, "看一眼就懂", fc, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                    Rectangle r = new Rectangle(72, 138, sz.Width + 44, sz.Height + 20);
                    using (GraphicsPath cp = new GraphicsPath())
                    {
                        int rad = r.Height / 2;
                        cp.AddArc(r.X, r.Y, rad * 2, r.Height, 90, 180);
                        cp.AddArc(r.Right - rad * 2, r.Y, rad * 2, r.Height, 270, 180);
                        cp.CloseFigure();
                        using (SolidBrush sb = new SolidBrush(Color.FromArgb(44, 0, 140, 230))) g.FillPath(sb, cp);
                        using (Pen p = new Pen(Color.FromArgb(120, 0, 170, 255), 1.4f)) g.DrawPath(p, cp);
                    }
                    TextRenderer.DrawText(g, "看一眼就懂", fc, new Point(r.X + 22, r.Y + 10), Color.FromArgb(240, 225, 240, 252), TextFormatFlags.NoPadding);
                }
                using (Font ft = new Font(FONT, 104, FontStyle.Bold))
                    TextRenderer.DrawText(g, "截完图", ft, new Point(180, 246), Color.White, TextFormatFlags.NoPadding);
                using (Font ft2 = new Font(FONT, 76, FontStyle.Bold))
                    TextRenderer.DrawText(g, "拖一下就发出去了", ft2, new Point(180, 396), Color.White, TextFormatFlags.NoPadding);
                using (Font fs = new Font(FONT, 36))
                    TextRenderer.DrawText(g, "不用保存、不用切窗口、不用翻文件夹", fs, new Point(180, 512), Sub, TextFormatFlags.NoPadding);
                using (Font fd = new Font(FONT, 28))
                    TextRenderer.DrawText(g, "免费 · 开源 · 单文件，双击就能用", fd, new Point(180, 1210), Color.FromArgb(200, 150, 165, 190), TextFormatFlags.NoPadding);
            }
            return b;
        }

        static void Main()
        {
            Application.EnableVisualStyles();
            string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_vgif");
            Directory.CreateDirectory(tmp);
            Settings.OverridePath = Path.Combine(tmp, "settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(tmp, "wheels.ini");
            Err.OverridePath = Path.Combine(tmp, "error.log");

            Settings s = new Settings();
            s.SaveToDisk = false;
            s.CollapseMode = false;
            s.ShowNameLabel = true;
            s.ShowCountLabel = true;
            WheelManager mgr = new WheelManager(s);
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;

            WheelForm wf = new WheelForm(mgr, s);
            int ww = wf.Width, wh = wf.Height;
            int offX = (W - ww) / 2;

            Bitmap back = BuildStatic();
            Bitmap shot = MakeShot(Color.FromArgb(232, 86, 110));
            List<Bitmap> frames = new List<Bitmap>();
            List<int> delays = new List<int>();
            Action<Bitmap, int> add = delegate(Bitmap b, int ms)
            {
                // 缩到 720x960 再入帧：1080x1440 x 43 帧要 18MB，微信/小红书发不出去。
                // 手机上看 720 宽足够清晰，体积能压到三分之一。
                // 裁紧再缩：原来整幅 1080x1440 里轮盘只占 61% 宽，主次反了。
                Rectangle crop = new Rectangle(135, 190, 810, 1080);   // 轮盘占 81% 宽
                Bitmap small = new Bitmap(720, 960, PixelFormat.Format32bppPArgb);
                using (Graphics sg = Graphics.FromImage(small))
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    sg.DrawImage(b, new Rectangle(0, 0, 720, 960), crop, GraphicsUnit.Pixel);
                }
                b.Dispose();
                frames.Add(small); delays.Add(ms);
            };

            // 把轮盘某一时刻画进底图
            Action<Graphics, float> wheel = delegate(Graphics g, float introT)
            {
                F(wf, "_intro", introT < 0.999f);
                F(wf, "_introT", introT);
                F(wf, "_collapsing", false);
                F(wf, "_collapsed", false);
                F(wf, "_show", 1f);
                using (Bitmap b = new Bitmap(ww, wh, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics gg = Graphics.FromImage(b))
                    {
                        gg.Clear(Color.Transparent);
                        gg.SmoothingMode = SmoothingMode.AntiAlias;
                        gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        Call(wf, "DrawWheel", gg, ww, wh);
                    }
                    g.DrawImage(b, offX, WheelTop);
                }
            };

            // ── 第 1 段：轮盘展开（环从角上扫出来）
            for (int i = 0; i <= 9; i++)
            {
                float t = Ease(i / 9f);
                Bitmap fr = new Bitmap(back);
                using (Graphics g = Graphics.FromImage(fr)) wheel(g, t);
                add(fr, 90);
            }
            // ── 第 2 段：一张图飞进环里（从画面中间飞到第一个卡片位置）
            StoreItem it = st.Add(shot);
            PointF c1 = (PointF)Call(wf, "ItemCenter", 0);
            c1 = new PointF(c1.X + offX, c1.Y + WheelTop);
            PointF from = new PointF(W / 2f, WheelTop - 40);
            for (int i = 0; i <= 9; i++)
            {
                float t = Ease(i / 9f);
                Bitmap fr = new Bitmap(back);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    wheel(g, 1f);
                    float cx = Lerp(from.X, c1.X, t), cy = Lerp(from.Y, c1.Y, t);
                    float sw = Lerp(300, 150, t), sh = Lerp(178, 90, t);   // 原来起始 460，太大抢戏
                    ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = Math.Max(0f, 1f - t * 0.45f);
                    ImageAttributes ia = new ImageAttributes(); ia.SetColorMatrix(cm);
                    Rectangle d = new Rectangle((int)(cx - sw / 2), (int)(cy - sh / 2), (int)sw, (int)sh);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(90 * (1 - t)), 0, 0, 0)))
                        g.FillRectangle(sb, d.X + 6, d.Y + 8, d.Width, d.Height);
                    g.DrawImage(shot, d, 0, 0, shot.Width, shot.Height, GraphicsUnit.Pixel, ia);
                    ia.Dispose();
                    Cursor(g, cx, cy);
                }
                add(fr, 90);
            }
            // ── 第 3 段：把它拖出去用（从环上拖到右侧）
            for (int i = 0; i <= 9; i++)
            {
                float t = Ease(i / 9f);
                Bitmap fr = new Bitmap(back);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    wheel(g, 1f);
                    float cx = Lerp(c1.X, W / 2f + 250, t), cy = Lerp(c1.Y, WheelTop + 300, t);
                    float sw = Lerp(150, 330, t), sh = Lerp(90, 196, t);
                    Rectangle d = new Rectangle((int)(cx - sw / 2), (int)(cy - sh / 2), (int)sw, (int)sh);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(90 * (1 - t * 0.5f)), 0, 0, 0)))
                        g.FillRectangle(sb, d.X + 8, d.Y + 10, d.Width, d.Height);
                    g.DrawImage(shot, d, 0, 0, shot.Width, shot.Height, GraphicsUnit.Pixel);
                    Cursor(g, cx, cy);
                }
                add(fr, 90);
            }
            // ── 收尾停一拍（让它循环时有个呼吸）
            {
                Bitmap fr = new Bitmap(back);
                using (Graphics g = Graphics.FromImage(fr)) wheel(g, 1f);
                add(fr, 900);
            }

            string outp = Path.Combine(Path.GetTempPath(), "snapwheel_vertical");
            Directory.CreateDirectory(outp);
            string gif = Path.Combine(outp, "图1.gif");
            WriteGif(gif, frames, delays);
            Console.WriteLine("写出 " + gif + "  " + frames.Count + " 帧  " + (new FileInfo(gif).Length / 1024) + " KB");
        }

        // 写循环 GIF（GDI+ 自己不带 NETSCAPE2.0 循环块，所以要手工补：见 docs 里 demo.gif 的同类处理）
        static void WriteGif(string path, List<Bitmap> frames, List<int> delays)
        {
            Encoder enc = Encoder.SaveFlag;
            ImageCodecInfo gif = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/gif") gif = c;
            EncoderParameters ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(enc, (long)EncoderValue.MultiFrame);
            frames[0].Save(path, gif, ep);
            ep.Param[0] = new EncoderParameter(enc, (long)EncoderValue.FrameDimensionTime);
            for (int i = 1; i < frames.Count; i++)
            {
                EncoderParameters pf = new EncoderParameters(1);
                pf.Param[0] = new EncoderParameter(enc, (long)EncoderValue.FrameDimensionTime);
                frames[0].SaveAdd(frames[i], pf);
            }
            ep.Param[0] = new EncoderParameter(enc, (long)EncoderValue.Flush);
            frames[0].SaveAdd(ep);

            // 手工补 NETSCAPE2.0 循环块（GDI+ 不写它，于是图只播一遍就停）
            byte[] all = File.ReadAllBytes(path);
            byte[] loop = new byte[] { 0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S',
                                       (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0',
                                       0x03, 0x01, 0x00, 0x00, 0x00 };
            List<byte> outp = new List<byte>(all.Length + loop.Length);
            // 找到全局调色板结束处（在第一个图像块 0x2C 之前插入最稳）
            int pos = 13;
            if (all.Length > 10 && (all[10] & 0x80) != 0) pos = 13 + 3 * (1 << ((all[10] & 0x07) + 1));
            outp.AddRange(new ArraySegment<byte>(all, 0, pos));
            outp.AddRange(loop);
            outp.AddRange(new ArraySegment<byte>(all, pos, all.Length - pos));
            File.WriteAllBytes(path, outp.ToArray());
        }
    }
}
