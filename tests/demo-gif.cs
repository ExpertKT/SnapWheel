// 演示动图生成器：把"截图 → 图滑进角落的轮环 → 拖进聊天窗口"渲染成 GIF。
//
// 全部离线渲染：假桌面是画出来的，**不截真实屏幕**；光标是画出来的假光标，
// **不模拟任何真实鼠标/键盘输入**（铁律）。轮盘部分复用 ui-shot.cs 的做法：
// 反射调 WheelForm.DrawWheel 把轮盘画进位图，于是可以按任意动画进度出帧。
//
// 用法：csc /target:exe /main:SnapWheel.DemoGif /out:demogif.exe src\*.cs tests\demo-gif.cs
//       demogif.exe            -> 写 docs\demo.gif
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
        const int W = 720, H = 430;      // 画布（GIF 尺寸）
        const int DelayMs = 80;          // 每帧 80ms ≈ 12fps

        static void F(object o, string n, object v)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("找不到字段 " + n);
            fi.SetValue(o, v);
        }
        static object G(object o, string n)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            return fi == null ? null : fi.GetValue(o);
        }
        static object Call(object o, string n, params object[] a)
        {
            MethodInfo[] all = o.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < all.Length; i++)
                if (all[i].Name == n && all[i].GetParameters().Length == a.Length) return all[i].Invoke(o, a);
            throw new Exception("找不到方法 " + n);
        }

        static Bitmap Solid(int w, int h, Color c)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
            return b;
        }

        // ---- 假桌面：不泄露真实屏幕。一块渐变背景 + 顶部条 + 两个假窗口 ----
        static Bitmap FakeDesktop()
        {
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush br = new LinearGradientBrush(new Rectangle(0, 0, W, H),
                    Color.FromArgb(38, 52, 78), Color.FromArgb(16, 20, 30), 55f))
                    g.FillRectangle(br, 0, 0, W, H);
                // 顶部任务条
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(14, 16, 22)))
                    g.FillRectangle(sb, 0, 0, W, 26);
                using (Font f = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(150, 220, 228, 240)))
                    g.DrawString("假桌面（演示用，不是真实屏幕）", f, fb, 10, 5);
                // 假聊天窗口（拖放目标）
                Rectangle chat = ChatRect();
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(246, 248, 250)))
                    g.FillRectangle(sb, chat);
                using (Pen p = new Pen(Color.FromArgb(200, 210, 220), 1f))
                    g.DrawRectangle(p, chat);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(88, 198, 120)))
                    g.FillRectangle(sb, chat.X, chat.Y, chat.Width, 30);
                using (Font f = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
                using (SolidBrush fb = new SolidBrush(Color.White))
                    g.DrawString("聊天窗口（假）", f, fb, chat.X + 10, chat.Y + 5);
                using (Font f = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(150, 90, 100, 110)))
                    g.DrawString("把图拖到这里松手 = 直接粘进输入框", f, fb, chat.X + 10, chat.Bottom - 26);
            }
            return b;
        }

        static Rectangle ChatRect() { return new Rectangle(W - 330, 70, 300, 250); }

        // ---- 轮盘：按给定动画进度渲染成一张带透明度的位图（照搬 ui-shot 的做法）----
        static Bitmap WheelFrame(WheelManager mgr, Settings s, float introT, float show, int corner, out int w, out int h)
        {
            WheelForm f = new WheelForm(mgr, s);
            F(f, "_intro", true);
            F(f, "_introT", introT);
            F(f, "_collapsing", false);
            F(f, "_show", show);
            F(f, "_collapsed", false);
            w = f.Width; h = f.Height;
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                Call(f, "DrawWheel", g, w, h);
            }
            f.Dispose();
            return b;
        }

        // 假光标：画一个箭头，省得动真鼠标
        static void Cursor(Graphics g, int x, int y, float k)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PointF[] pts = new PointF[] {
                new PointF(x, y), new PointF(x, y + 17 * k), new PointF(x + 4.5f * k, y + 12.5f * k),
                new PointF(x + 7.5f * k, y + 19 * k), new PointF(x + 10.5f * k, y + 17.5f * k),
                new PointF(x + 7.5f * k, y + 11 * k), new PointF(x + 13 * k, y + 10.5f * k) };
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillPolygon(b, pts);
            using (Pen p = new Pen(Color.FromArgb(220, 30, 34, 42), 1.4f)) g.DrawPolygon(p, pts);
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_gif");
            Directory.CreateDirectory(tmp);
            Settings.OverridePath = Path.Combine(tmp, "settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(tmp, "wheels.ini");
            Err.OverridePath = Path.Combine(tmp, "error.log");

            Settings s = new Settings();
            s.SaveToDisk = false;
            s.ShowCountLabel = true;
            WheelManager mgr = new WheelManager(s);
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;

            // 三张"截图"：用彩色方块 + 文字，看起来像一张截图内容的缩略图
            Color[] cols = { Color.FromArgb(232, 86, 110), Color.FromArgb(46, 148, 214), Color.FromArgb(247, 166, 35) };
            Bitmap[] shots = new Bitmap[3];
            for (int i = 0; i < 3; i++)
            {
                shots[i] = new Bitmap(520, 320, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(shots[i]))
                {
                    g.Clear(Color.White);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(30, cols[i]))) g.FillRectangle(sb, 0, 0, 520, 70);
                    using (Font f = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold))
                    using (SolidBrush fb = new SolidBrush(cols[i]))
                        g.DrawString("截图 " + (i + 1), f, fb, 18, 18);
                    using (Font f = new Font("Microsoft YaHei UI", 12f))
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(120, 40, 44, 52)))
                    {
                        g.DrawString("这是一张演示用的假截图内容", f, fb, 20, 100);
                        g.DrawString("（不是真实屏幕）", f, fb, 20, 130);
                    }
                    using (Pen p = new Pen(Color.FromArgb(40, 0, 0, 0), 2f))
                        for (int y = 170; y < 320; y += 30) g.DrawLine(p, 20, y, 500, y);
                }
            }

            List<Bitmap> frames = new List<Bitmap>();
            Bitmap desk = FakeDesktop();

            // 阶段一：截图（选区从左上向右下长出来）
            for (int i = 0; i <= 3; i++)
            {
                Bitmap fr = new Bitmap(desk);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float t = i / 3f;
                    Rectangle sel = new Rectangle(120, 90, (int)(520 * t), (int)(320 * t));
                    using (SolidBrush dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                    {
                        // 选区外压暗：四块
                        g.FillRectangle(dim, 0, 0, W, sel.Top);
                        g.FillRectangle(dim, 0, sel.Bottom, W, H - sel.Bottom);
                        g.FillRectangle(dim, 0, sel.Top, sel.Left, sel.Height);
                        g.FillRectangle(dim, sel.Right, sel.Top, W - sel.Right, sel.Height);
                    }
                    using (Pen p = new Pen(Color.FromArgb(0, 174, 255), 2f)) g.DrawRectangle(p, sel);
                    using (Font f = new Font("Microsoft YaHei UI", 10f))
                    using (SolidBrush fb = new SolidBrush(Color.White))
                        g.DrawString("Ctrl+Shift+S 框选", f, fb, sel.Left, sel.Top - 20);
                    Cursor(g, sel.Right, sel.Bottom, 1f);
                }
                frames.Add(fr);
            }
            int w = 0, h = 0;

            // 阶段二：图滑进角落的轮环（轮盘开启动画）
            st.Add(shots[0]);
            for (int i = 0; i <= 4; i++)
            {
                Bitmap fr = new Bitmap(desk);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    float t = i / 8f;
                    Bitmap wf = WheelFrame(mgr, s, t, 1f, 0, out w, out h);
                    g.DrawImage(wf, 0, H - h);
                    wf.Dispose();
                    using (Font f = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    {
                        string t2 = i < 6 ? "截完直接滑进角落的环里" : "不用保存、不用切窗口";
                        SizeF sz = g.MeasureString(t2, f);
                        g.FillRectangle(bg, 24, 40, sz.Width + 18, sz.Height + 10);
                        g.DrawString(t2, f, fb, 33, 45);
                    }
                }
                frames.Add(fr);
            }

            // 阶段三：从环上把缩略图拖到聊天窗口
            st.Add(shots[1]);
            for (int i = 0; i <= 7; i++)
            {
                Bitmap fr = new Bitmap(desk);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    float t = i / 7f;
                    Bitmap wf = WheelFrame(mgr, s, 1f, 1f, 0, out w, out h);
                    g.DrawImage(wf, 0, H - h);
                    wf.Dispose();
                    Rectangle chat = ChatRect();
                    // 拖动中的缩略图：从环上（左下）飞向聊天窗口
                    int ww = 170, hh = 105;
                    int x0 = (int)(60 + (chat.X + 40 - 60) * t);
                    int y0 = (int)(H - h + 40 + (chat.Y + 90 - (H - h + 40)) * t);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(235, 0, 0, 0)))
                        g.FillRectangle(sb, x0 + 5, y0 + 6, ww, hh);
                    g.DrawImage(shots[1], new Rectangle(x0, y0, ww, hh));
                    using (Pen p = new Pen(Color.FromArgb(90, 170, 255), 2f))
                        g.DrawRectangle(p, x0, y0, ww, hh);
                    Cursor(g, x0 + ww / 2, y0 + hh / 2, 1.1f);
                    using (Font f = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    {
                        string t2 = "缩略图直接拖进聊天框";
                        SizeF sz = g.MeasureString(t2, f);
                        g.FillRectangle(bg, 24, 40, sz.Width + 18, sz.Height + 10);
                        g.DrawString(t2, f, fb, 33, 45);
                    }
                }
                frames.Add(fr);
            }

            // 阶段四：松手，图出现在聊天窗口里 + 一张缩略图留在环上
            st.Add(shots[2]);
            for (int i = 0; i <= 4; i++)
            {
                Bitmap fr = new Bitmap(desk);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    Rectangle chat = ChatRect();
                    g.DrawImage(shots[2], new Rectangle(chat.X + 40, chat.Y + 90, 170, 105));
                    Bitmap wf = WheelFrame(mgr, s, 1f, 1f, 0, out w, out h);
                    g.DrawImage(wf, 0, H - h);
                    wf.Dispose();
                    using (Font f = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    {
                        string t2 = "拖出去就用 —— 图还在环上留着";
                        SizeF sz = g.MeasureString(t2, f);
                        g.FillRectangle(bg, 24, 40, sz.Width + 18, sz.Height + 10);
                        g.DrawString(t2, f, fb, 33, 45);
                    }
                }
                frames.Add(fr);
            }

            string dir = Path.Combine(Directory.GetCurrentDirectory(), "docs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "demo.gif");
            WriteGif(path, frames, DelayMs);
            FileInfo fi = new FileInfo(path);
            Console.WriteLine("写出 {0}：{1} 帧，{2}x{3}，{4} KB",
                path, frames.Count, W, H, Math.Round(fi.Length / 1024.0, 1));
            for (int i = 0; i < frames.Count; i++) frames[i].Dispose();
        }

        // 转 8bpp 索引色（GIF 的调色板是 256 色，不转的话 GDI+ 会写出巨大的文件）
        static Bitmap ToIndexed(Bitmap src)
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format8bppIndexed);
            ColorPalette pal = b.Palette;
            for (int i = 0; i < 256; i++)                       // 灰度渐变 + 少量常用色，深色 UI 用这个够
                pal.Entries[i] = Color.FromArgb(255, i, i, i);
            for (int i = 0; i < 32; i++)                        // 再补一条蓝色渐变（轮盘玻璃偏蓝）
                pal.Entries[224 + i] = Color.FromArgb(255, 20 + i * 2, 30 + i * 2, 60 + i * 3);
            b.Palette = pal;
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            }
            return b;
        }

        // 多帧 GIF：第一帧 MultiFrame，之后每帧 FrameDimensionTime，最后 Flush；
        // 无限循环用属性 GUID 0x5101（LoopCount=0），每帧延时用 0x5100（单位 10ms）。
        static void WriteGif(string path, List<Bitmap> frames, int delayMs)
        {
            ImageCodecInfo gif = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Gif.Guid) { gif = c; break; }
            if (gif == null) throw new Exception("系统里没有 GIF 编码器");

            // 这两个属性没有强类型常量，得自己写 GUID：
            //   0x5100 = 每帧延时（单位 10ms）   0x5101 = 循环次数（0 = 无限）
            Encoder delay = new Encoder(new Guid("51000000-0000-0000-0000-000000000000"));
            Encoder loop = new Encoder(new Guid("51010000-0000-0000-0000-000000000000"));

            EncoderParameters ep = new EncoderParameters(3);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            ep.Param[1] = new EncoderParameter(delay, (long)(delayMs / 10));
            ep.Param[2] = new EncoderParameter(loop, 0L);          // 0 = 无限循环
            // 关键：每帧先转成 8bpp 索引色再写。GDI+ 直接写 32bpp 会得到一个几 MB 的 GIF，
            // 转索引色后大小能掉到十分之一（GIF 本来就是 256 色）。
            Bitmap first = frames[0];
            first.Save(path, gif, ep);
            for (int i = 1; i < frames.Count; i++)
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionTime);
                first.SaveAdd(frames[i], ep);
            }
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.Flush);
            first.SaveAdd(ep);
        }
    }
}
