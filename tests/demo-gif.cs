// 演示动图生成器 v2：把"截图 → 图飞进角落的轮环 → 从环上拖进聊天窗口"渲染成 GIF。
// v1 的问题（用户反馈）：只有 22 帧 × 80ms ≈ 1.7 秒，太快、闪几下就没了，而且"图进环"没画出来。
// v2：带停顿的时间轴（关键姿势多停、动作段 110~120ms 一帧），并显式画出两段位移。
// 全部离线渲染：假桌面是画的、光标是画的 —— 不截真实屏幕、不模拟真实输入。
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
    static class DemoGif
    {
        const int W = 720, H = 430;

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
        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
        static float Ease(float t) { return 1f - (1f - t) * (1f - t); }

        static Rectangle ChatRect() { return new Rectangle(W - 300, 60, 270, 230); }
        static Rectangle SelRect() { return new Rectangle(96, 74, 430, 265); }

        static Bitmap FakeDesktop()
        {
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush br = new LinearGradientBrush(new Rectangle(0, 0, W, H),
                    Color.FromArgb(40, 54, 80), Color.FromArgb(18, 22, 32), 55f))
                    g.FillRectangle(br, 0, 0, W, H);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(14, 16, 22))) g.FillRectangle(sb, 0, 0, W, 24);
                using (Font f = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(140, 220, 228, 240)))
                    g.DrawString("假桌面（演示用，不是真实屏幕）", f, fb, 10, 4);
                Rectangle chat = ChatRect();
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(246, 248, 250))) g.FillRectangle(sb, chat);
                using (Pen p = new Pen(Color.FromArgb(200, 210, 220), 1f)) g.DrawRectangle(p, chat);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(88, 198, 120))) g.FillRectangle(sb, chat.X, chat.Y, chat.Width, 28);
                using (Font f = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
                using (SolidBrush fb = new SolidBrush(Color.White)) g.DrawString("聊天窗口（假）", f, fb, chat.X + 10, chat.Y + 4);
                using (Font f = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(150, 90, 100, 110)))
                    g.DrawString("把图拖到这里松手 = 直接粘进输入框", f, fb, chat.X + 10, chat.Bottom - 24);
            }
            return b;
        }

        static Bitmap MakeShot(int i, Color c)
        {
            Bitmap b = new Bitmap(430, 265, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.White);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(28, c))) g.FillRectangle(sb, 0, 0, 430, 56);
                using (Font f = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold))
                using (SolidBrush fb = new SolidBrush(c)) g.DrawString("截图 " + i, f, fb, 16, 12);
                using (Font f = new Font("Microsoft YaHei UI", 11f))
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(120, 40, 44, 52)))
                {
                    g.DrawString("演示用的假截图内容（不是真实屏幕）", f, fb, 18, 80);
                    g.DrawString("框选 -> 自动进环 -> 拖出去就用", f, fb, 18, 108);
                }
                using (Pen p = new Pen(Color.FromArgb(38, 0, 0, 0), 2f))
                    for (int y = 150; y < 265; y += 26) g.DrawLine(p, 18, y, 412, y);
            }
            return b;
        }

        static void Cursor(Graphics g, float x, float y, float k)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PointF[] pts = new PointF[] {
                new PointF(x, y), new PointF(x, y + 17 * k), new PointF(x + 4.5f * k, y + 12.5f * k),
                new PointF(x + 7.5f * k, y + 19 * k), new PointF(x + 10.5f * k, y + 17.5f * k),
                new PointF(x + 7.5f * k, y + 11 * k), new PointF(x + 13 * k, y + 10.5f * k) };
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillPolygon(b, pts);
            using (Pen p = new Pen(Color.FromArgb(220, 30, 34, 42), 1.4f)) g.DrawPolygon(p, pts);
        }

        static void Caption(Graphics g, string s)
        {
            using (Font f = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
            using (SolidBrush fb = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
            {
                SizeF sz = g.MeasureString(s, f);
                g.FillRectangle(bg, 20, 34, sz.Width + 20, sz.Height + 10);
                g.DrawString(s, f, fb, 30, 39);
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_gif2");
            Directory.CreateDirectory(tmp);
            Settings.OverridePath = Path.Combine(tmp, "settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(tmp, "wheels.ini");
            Err.OverridePath = Path.Combine(tmp, "error.log");

            Settings s = new Settings();
            s.SaveToDisk = false;
            WheelManager mgr = new WheelManager(s);
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;

            Bitmap shot1 = MakeShot(1, Color.FromArgb(232, 86, 110));
            Bitmap shot2 = MakeShot(2, Color.FromArgb(46, 148, 214));
            Bitmap desk = FakeDesktop();

            WheelForm wf = new WheelForm(mgr, s);
            int wwh = wf.Width, whh = wf.Height;

            List<Bitmap> frames = new List<Bitmap>();
            List<int> delays = new List<int>();
            Action<Bitmap, int> add = delegate(Bitmap b, int ms) { frames.Add(b); delays.Add(ms); };

            Action<Graphics, float, float> wheel = delegate(Graphics g, float introT, float show)
            {
                F(wf, "_intro", introT < 0.999f);
                F(wf, "_introT", introT);
                F(wf, "_collapsing", false);
                F(wf, "_collapsed", false);
                F(wf, "_show", show);
                using (Bitmap b = new Bitmap(wwh, whh, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics gg = Graphics.FromImage(b))
                    {
                        gg.Clear(Color.Transparent);
                        gg.SmoothingMode = SmoothingMode.AntiAlias;
                        gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        Call(wf, "DrawWheel", gg, wwh, whh);
                    }
                    g.DrawImage(b, 0, H - whh);
                }
            };
            Func<int, PointF> cardCenter = delegate(int i)
            {
                PointF p = (PointF)Call(wf, "ItemCenter", i);
                return new PointF(p.X, p.Y + (H - whh));
            };

            Rectangle sel = SelRect();

            add(new Bitmap(desk), 1200);          // 开场停 1.2 秒

            // (1) 框选 10 帧 x 120ms
            for (int i = 0; i <= 9; i++)
            {
                Bitmap fr = new Bitmap(desk);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    float t = i / 9f;
                    Rectangle r = new Rectangle(sel.X, sel.Y, (int)(sel.Width * t), (int)(sel.Height * t));
                    using (SolidBrush dim = new SolidBrush(Color.FromArgb(115, 0, 0, 0)))
                    {
                        g.FillRectangle(dim, 0, 0, W, r.Top);
                        g.FillRectangle(dim, 0, r.Bottom, W, H - r.Bottom);
                        g.FillRectangle(dim, 0, r.Top, r.Left, r.Height);
                        g.FillRectangle(dim, r.Right, r.Top, W - r.Right, r.Height);
                    }
                    using (Pen p = new Pen(Color.FromArgb(0, 174, 255), 2f)) g.DrawRectangle(p, r);
                    Cursor(g, r.Right, r.Bottom, 1f);
                    Caption(g, "1) Ctrl+Shift+S 框选");
                }
                add(fr, 120);
            }

            // (2) 截图飞进环里 12 帧 x 110ms
            st.Add(shot1);
            PointF c1 = cardCenter(0);
            float fx0 = sel.X + sel.Width / 2f, fy0 = sel.Y + sel.Height / 2f;
            for (int i = 0; i <= 11; i++)
            {
                Bitmap fr = new Bitmap(desk);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    float t = Ease(i / 11f);
                    float cx = Lerp(fx0, c1.X, t), cy = Lerp(fy0, c1.Y, t);
                    float sw = Lerp(sel.Width, 150, t), sh = Lerp(sel.Height, 92, t);
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = Math.Max(0f, 1f - t * 0.55f);
                    ImageAttributes ia = new ImageAttributes();
                    ia.SetColorMatrix(cm);
                    Rectangle d = new Rectangle((int)(cx - sw / 2), (int)(cy - sh / 2), (int)sw, (int)sh);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(90 * (1 - t)), 0, 0, 0)))
                        g.FillRectangle(sb, d.X + 4, d.Y + 5, d.Width, d.Height);
                    g.DrawImage(shot1, d, 0, 0, shot1.Width, shot1.Height, GraphicsUnit.Pixel, ia);
                    ia.Dispose();
                    wheel(g, Math.Min(1f, t * 1.35f), 1f);
                    Cursor(g, cx, cy, 1f);
                    Caption(g, "2) 截完自动飞进角落的环里");
                }
                add(fr, 110);
            }

            {
                Bitmap fr = new Bitmap(desk);
                using (Graphics g = Graphics.FromImage(fr)) { wheel(g, 1f, 1f); Cursor(g, c1.X, c1.Y, 1f); Caption(g, "3) 不用保存、不用切窗口，图就挂在环上"); }
                add(fr, 1400);
            }

            // (3) 从环上拖进聊天窗口 14 帧 x 110ms
            st.Add(shot2);
            Rectangle chat = ChatRect();
            for (int i = 0; i <= 13; i++)
            {
                Bitmap fr = new Bitmap(desk);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    float t = Ease(i / 13f);
                    float x1 = chat.X + 40, y1 = chat.Y + 96;
                    float cx = Lerp(c1.X, x1, t);
                    float cy = Lerp(c1.Y, y1, t) - (float)Math.Sin(t * Math.PI) * 40f;
                    int dw = 190, dh = 118;
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(210, 0, 0, 0)))
                        g.FillRectangle(sb, cx - dw / 2 + 5, cy - dh / 2 + 6, dw, dh);
                    g.DrawImage(shot2, new Rectangle((int)(cx - dw / 2), (int)(cy - dh / 2), dw, dh));
                    using (Pen p = new Pen(Color.FromArgb(110, 170, 255), 2f))
                        g.DrawRectangle(p, (int)(cx - dw / 2), (int)(cy - dh / 2), dw, dh);
                    wheel(g, 1f, 1f);
                    Cursor(g, cx + dw / 2 - 10, cy + dh / 2 - 8, 1.1f);
                    Caption(g, "4) 要用的时候，缩略图直接拖进聊天框");
                }
                add(fr, 110);
            }

            {
                Bitmap fr = new Bitmap(desk);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    g.DrawImage(shot2, new Rectangle(chat.X + 40, chat.Y + 96, 190, 118));
                    wheel(g, 1f, 1f);
                    Cursor(g, chat.X + 150, chat.Y + 250, 1.1f);
                    Caption(g, "5) 拖出去就用 -- 图还留在环上");
                }
                add(fr, 2000);
            }

            Directory.CreateDirectory("docs");
            string path = Path.Combine(Directory.GetCurrentDirectory(), "docs", "demo.gif");
            WriteGif(path, frames, delays);
            double sec = 0; for (int i = 0; i < delays.Count; i++) sec += delays[i] / 1000.0;
            Console.WriteLine("写出 {0}：{1} 帧，{2}x{3}，总时长 {4:0.0} 秒，{5} KB",
                path, frames.Count, W, H, sec, Math.Round(new FileInfo(path).Length / 1024.0, 1));
            for (int i = 0; i < frames.Count; i++) frames[i].Dispose();
            wf.Dispose();
        }

        static void WriteGif(string path, List<Bitmap> frames, List<int> delays)
        {
            ImageCodecInfo gif = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Gif.Guid) { gif = c; break; }
            if (gif == null) throw new Exception("no gif encoder");

            Encoder delayEnc = new Encoder(new Guid("51000000-0000-0000-0000-000000000000"));
            Encoder loopEnc = new Encoder(new Guid("51010000-0000-0000-0000-000000000000"));

            EncoderParameters ep = new EncoderParameters(3);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            ep.Param[1] = new EncoderParameter(delayEnc, (long)Math.Max(2, delays[0] / 10));
            ep.Param[2] = new EncoderParameter(loopEnc, 0L);
            frames[0].Save(path, gif, ep);
            for (int i = 1; i < frames.Count; i++)
            {
                EncoderParameters p2 = new EncoderParameters(2);
                p2.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionTime);
                p2.Param[1] = new EncoderParameter(delayEnc, (long)Math.Max(2, delays[i] / 10));
                frames[0].SaveAdd(frames[i], p2);
                p2.Dispose();
            }
            EncoderParameters pf = new EncoderParameters(1);
            pf.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.Flush);
            frames[0].SaveAdd(pf);
            pf.Dispose();
        }
    }
}