// 演示动图生成器 v3：讲清"轮盘和里面的图跟着你走"这件事。
// v1/v2 的问题：a) 太快；b) 截的图和拖的图不是同一张；c) 满是"假桌面"的自我说明（此地无银）。
// v3：全程同一张图；桌面画得像真桌面（不再写"这是假的"）；新增两段 —— 从别的窗口把图拖进环、
// 切到别的窗口轮盘照样在角落里、还能把图拖出去用。
// 全部离线渲染：桌面是画的、光标是画的 —— 不截真实屏幕、不模拟真实输入。
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

        static Rectangle ChatRect() { return new Rectangle(W - 282, 52, 252, 200); }
        static Rectangle DocRect() { return new Rectangle(44, 44, 356, 262); }
        static Rectangle SelRect() { return new Rectangle(96, 78, 420, 250); }

        // 画一个像样的桌面：壁纸 + 任务栏 + 图标，不写任何"这是假的"字样
        static Bitmap FakeDesktop(bool withDoc)
        {
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush br = new LinearGradientBrush(new Rectangle(0, 0, W, H),
                    Color.FromArgb(52, 74, 112), Color.FromArgb(22, 30, 46), 60f))
                    g.FillRectangle(br, 0, 0, W, H);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(24, 26, 34))) g.FillRectangle(sb, 0, H - 34, W, 34);
                using (Font f = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(210, 226, 236, 250)))
                {
                    for (int i = 0; i < 5; i++)
                    {
                        using (SolidBrush ic = new SolidBrush(Color.FromArgb(120, 150, 190, 235)))
                            g.FillRectangle(ic, 14 + i * 30, H - 27, 20, 20);
                        g.DrawString("文件", f, fb, 14, H - 4);
                        g.DrawString("浏览器", f, fb, 44, H - 4);
                        g.DrawString("聊天", f, fb, 104, H - 4);
                        break;
                    }
                }
                // 桌面上两个"文件"图标
                using (SolidBrush ic = new SolidBrush(Color.FromArgb(200, 235, 240, 250)))
                {
                    g.FillRectangle(ic, 30, 40, 34, 42);
                    g.FillRectangle(ic, 30, 100, 34, 42);
                }
                using (Font f = new Font("Microsoft YaHei UI", 8f))
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(220, 235, 242, 252)))
                {
                    g.DrawString("截图", f, fb, 32, 86);
                    g.DrawString("素材", f, fb, 32, 146);
                }
                {
                    Rectangle d = DocRect();
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(250, 251, 253))) g.FillRectangle(sb, d);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(60, 120, 200))) g.FillRectangle(sb, d.X, d.Y, d.Width, 26);
                    using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                    using (SolidBrush fb = new SolidBrush(Color.White)) g.DrawString("季度汇报.docx", f, fb, d.X + 10, d.Y + 5);
                    using (Pen p = new Pen(Color.FromArgb(36, 0, 0, 0), 2f))
                        for (int y = d.Y + 60; y < d.Bottom - 30; y += 26) g.DrawLine(p, d.X + 24, y, d.Right - 30, y);
                }
                {   // 聊天窗口也画上：整段故事都在同一个桌面里，不再中途换背景
                    Rectangle c = ChatRect();
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(247, 249, 251))) g.FillRectangle(sb, c);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(88, 198, 120))) g.FillRectangle(sb, c.X, c.Y, c.Width, 26);
                    using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                    using (SolidBrush fb = new SolidBrush(Color.White)) g.DrawString("聊天", f, fb, c.X + 10, c.Y + 5);
                    using (Font f = new Font("Microsoft YaHei UI", 8.5f))
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(150, 100, 110, 122)))
                        g.DrawString("拖到输入框就发出去了", f, fb, c.X + 10, c.Bottom - 22);
                }
            }
            return b;
        }

        // 一张"截图内容"：像一份网页/文档，不写"这是假的"
        static Bitmap MakeShot(Color c)
        {
            Bitmap b = new Bitmap(420, 250, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.White);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(26, c))) g.FillRectangle(sb, 0, 0, 420, 52);
                using (Font f = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold))
                using (SolidBrush fb = new SolidBrush(c)) g.DrawString("本周数据", f, fb, 16, 14);
                using (Font f = new Font("Microsoft YaHei UI", 10.5f))
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(130, 44, 48, 58)))
                {
                    g.DrawString("· 交付时间 9 月 12 日 18:00", f, fb, 18, 74);
                    g.DrawString("· 负责同学：小何", f, fb, 18, 104);
                    g.DrawString("· 备注：本周报告已发出", f, fb, 18, 134);
                }
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(70, c))) g.FillRectangle(sb, 18, 176, 150, 54);
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
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(185, 0, 0, 0)))
            {
                SizeF sz = g.MeasureString(s, f);
                g.FillRectangle(bg, 20, 30, sz.Width + 20, sz.Height + 10);
                g.DrawString(s, f, fb, 30, 35);
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_gif3");
            Directory.CreateDirectory(tmp);
            Settings.OverridePath = Path.Combine(tmp, "settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(tmp, "wheels.ini");
            Err.OverridePath = Path.Combine(tmp, "error.log");

            Settings s = new Settings();
            s.SaveToDisk = false;
            WheelManager mgr = new WheelManager(s);
            Store st = mgr.ActiveStore;
            st.SaveToDisk = false;

            Bitmap shot = MakeShot(Color.FromArgb(232, 86, 110));
            Bitmap outside = MakeShot(Color.FromArgb(46, 148, 214));   // 从别处拖进来的那张
            Bitmap deskDoc = FakeDesktop(true);   // 文档窗口 + 聊天窗口都画在同一个桌面里

            WheelForm wf = new WheelForm(mgr, s);
            int wwh = wf.Width, whh = wf.Height;
            const float ws = 0.62f;    // 轮盘贴进画布时的缩放（见 wheel 委托里的说明）

            // 给轮盘的玻璃底喂一张"模拟桌面"的底图。
            // 不喂会怎样：`_backdropBlur` 是 null，`UseBackdrop()` 直接返回 false ——
            // 玻璃走兜底的**平涂**，出图就是一块奶白色，盖在模拟桌面上发灰、看着不像玻璃
            // （真机上是抓真实桌面再模糊，离线渲染里没得抓）。
            // 做法：把轮盘窗口在画布上占的那块区域从模拟桌面上抠出来，放大回窗口尺寸再模糊，
            // 于是"玻璃后面透出的是被它盖住的那块桌面"，和真机所见一致。
            try
            {
                using (Bitmap bd = new Bitmap(wwh, whh, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bd))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        Rectangle src = new Rectangle(0, H - (int)(whh * ws), (int)(wwh * ws), (int)(whh * ws));
                        g.DrawImage(deskDoc, new Rectangle(0, 0, wwh, whh), src, GraphicsUnit.Pixel);
                    }
                    // BlurBitmap 是 **static** 的（Call() 找的是实例方法，所以这里单独反射）
                    MethodInfo bm = typeof(WheelForm).GetMethod("BlurBitmap",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (bm == null) throw new Exception("找不到 WheelForm.BlurBitmap");
                    object blurred = bm.Invoke(null, new object[] { bd, 6 });
                    F(wf, "_backdropBlur", blurred);
                    F(wf, "_backdropOld", null);
                    F(wf, "_backdropFade", 1f);
                    F(wf, "_backdropOffset", new Point(0, 0));
                    F(wf, "_backdropValid", true);
                    Console.WriteLine("玻璃底已喂入（" + wwh + "x" + whh + "，取自模拟桌面）");
                }
            }
            catch (Exception ex) { Console.WriteLine("喂玻璃底失败（出图会是平涂）：" + ex.Message); }

            List<Bitmap> frames = new List<Bitmap>();
            List<int> delays = new List<int>();
            Action<Bitmap, int> add = delegate(Bitmap b, int ms) { frames.Add(b); delays.Add(ms); };

            Action<Graphics, float> wheel = delegate(Graphics g, float introT)
            {
                F(wf, "_intro", introT < 0.999f);
                F(wf, "_introT", introT);
                F(wf, "_collapsing", false);
                F(wf, "_collapsed", false);
                F(wf, "_show", 1f);
                using (Bitmap b = new Bitmap(wwh, whh, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics gg = Graphics.FromImage(b))
                    {
                        gg.Clear(Color.Transparent);
                        gg.SmoothingMode = SmoothingMode.AntiAlias;
                        gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        Call(wf, "DrawWheel", gg, wwh, whh);
                    }
                    // ⚠️ 轮盘窗口是**真实尺寸**（本机 658x658），而这张画布只有 720x430 ——
                    // 1:1 贴上去，比画布还高，糊住了整个左半屏（"轮盘和桌面主次反了"）。
                    // 缩到 0.62 之后，轮盘的可见那一瓣落在左下角、大小和真机观感接近。
                    // 绕"窗口左下角"缩（轮盘圆心就在那个角上），所以圆心仍然钉在 (0, H)。
                    g.DrawImage(b, 0f, H - whh * ws, wwh * ws, whh * ws);
                }
            };
            Func<int, PointF> cardCenter = delegate(int i)
            {
                PointF p = (PointF)Call(wf, "ItemCenter", i);
                return new PointF(p.X * ws, p.Y * ws + (H - whh * ws));   // 和上面同一套缩放
            };

            Rectangle sel = SelRect();
            Rectangle chat = ChatRect();
            Rectangle doc = DocRect();

            add(new Bitmap(deskDoc), 1100);                 // 先在别的窗口前停一下：轮盘就在角落挂着

            // (1) 框选
            for (int i = 0; i <= 7; i++)
            {
                Bitmap fr = new Bitmap(deskDoc);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    float t = i / 7f;
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
                    Caption(g, "Ctrl+Shift+S 框选，随手一截");
                }
                add(fr, 120);
            }

            // (2) 飞进环里
            StoreItem item1 = st.Add(shot);
            PointF c1 = cardCenter(0);
            for (int i = 0; i <= 9; i++)
            {
                Bitmap fr = new Bitmap(deskDoc);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    float t = Ease(i / 9f);
                    float cx = Lerp(sel.X + sel.Width / 2f, c1.X, t), cy = Lerp(sel.Y + sel.Height / 2f, c1.Y, t);
                    float sw = Lerp(sel.Width, 150, t), sh = Lerp(sel.Height, 90, t);
                    ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = Math.Max(0f, 1f - t * 0.5f);
                    ImageAttributes ia = new ImageAttributes(); ia.SetColorMatrix(cm);
                    Rectangle d = new Rectangle((int)(cx - sw / 2), (int)(cy - sh / 2), (int)sw, (int)sh);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(90 * (1 - t)), 0, 0, 0)))
                        g.FillRectangle(sb, d.X + 4, d.Y + 5, d.Width, d.Height);
                    g.DrawImage(shot, d, 0, 0, shot.Width, shot.Height, GraphicsUnit.Pixel, ia);
                    ia.Dispose();
                    wheel(g, Math.Min(1f, t * 1.3f));
                    Cursor(g, cx, cy, 1f);
                    Caption(g, "截完不用保存，直接飞进角落的环里");
                }
                add(fr, 110);
            }

            {
                Bitmap fr = new Bitmap(deskDoc);
                using (Graphics g = Graphics.FromImage(fr)) { wheel(g, 1f); Cursor(g, c1.X, c1.Y, 1f); Caption(g, "图就挂在环上：换到哪个窗口它都在"); }
                add(fr, 1300);
            }

            // (3) 从别处（桌面/网页/聊天记录）把图拖进环里 —— 环会亮起来接住
            PointF c2 = cardCenter(1);
            for (int i = 0; i <= 10; i++)
            {
                Bitmap fr = new Bitmap(deskDoc);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    float t = Ease(i / 10f);
                    float x0 = 47, y0 = 60;                       // 桌面上的"截图"图标
                    float cx = Lerp(x0, c2.X, t), cy = Lerp(y0, c2.Y, t);
                    int dw = (int)Lerp(34, 150, t), dh = (int)Lerp(42, 90, t);
                    bool over = t > 0.62f;
                    F(wf, "_dropActive", over);
                    F(wf, "_dropExternal", true);
                    F(wf, "_dropCount", over ? 1 : 0);
                    if (over) { foreach (StoreItem k in st.Items) if (k != item1) { } }
                    wheel(g, 1f);
                    if (over) { if (st.Items.Count < 2) st.Add(outside); }
                    g.DrawImage(outside, new Rectangle((int)(cx - dw / 2), (int)(cy - dh / 2), dw, dh));
                    Cursor(g, cx, cy, 1f);
                    Caption(g, over ? "松手就收进环里（环会亮起来接住）" : "外面看到的图，也能直接拖进环里");
                }
                add(fr, 110);
            }
            F(wf, "_dropActive", false); F(wf, "_dropExternal", false); F(wf, "_dropCount", 0);
            if (st.Items.Count < 2) st.Add(outside);   // 松手之后才真的进环
            c2 = cardCenter(1);

            {
                Bitmap fr = new Bitmap(deskDoc);
                using (Graphics g = Graphics.FromImage(fr)) { wheel(g, 1f); Cursor(g, c2.X, c2.Y, 1f); Caption(g, "两段素材都在环上，切窗口也不会丢"); }
                add(fr, 1300);
            }

            // (4) 切到文档窗口，把环上的图拖进文档里用
            for (int i = 0; i <= 10; i++)
            {
                Bitmap fr = new Bitmap(deskDoc);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    float t = Ease(i / 10f);
                    float cx = Lerp(c1.X, doc.X + 230, t), cy = Lerp(c1.Y, doc.Y + 170, t);
                    int dw = 180, dh = 108;
                    F(wf, "_dragOutItem", item1);
                    F(wf, "_dragOutProg", i / 10f);
                    wheel(g, 1f);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                        g.FillRectangle(sb, cx - dw / 2 + 5, cy - dh / 2 + 6, dw, dh);
                    g.DrawImage(shot, new Rectangle((int)(cx - dw / 2), (int)(cy - dh / 2), dw, dh));
                    using (Pen p = new Pen(Color.FromArgb(110, 170, 255), 2f))
                        g.DrawRectangle(p, (int)(cx - dw / 2), (int)(cy - dh / 2), dw, dh);
                    Cursor(g, cx + dw / 2 - 10, cy + dh / 2 - 8, 1.1f);
                    Caption(g, "换到文档里也一样：拖出去就用");
                }
                add(fr, 110);
            }

            {
                Bitmap fr = new Bitmap(deskDoc);
                using (Graphics g = Graphics.FromImage(fr))
                {
                    g.DrawImage(shot, new Rectangle(doc.X + 140, doc.Y + 116, 180, 108));
                    F(wf, "_dragOutItem", null); F(wf, "_dragOutProg", 0f);
                    wheel(g, 1f);
                    Cursor(g, doc.X + 260, doc.Y + 250, 1.1f);
                    Caption(g, "轮盘和里面的图一直跟着你 —— 拖出去也只是复制，图还在环上");
                }
                add(fr, 2000);
            }

            Directory.CreateDirectory("docs");
            string path = Path.Combine(Directory.GetCurrentDirectory(), "docs", "demo.gif");
            WriteGif(path, frames, delays);
            double sec = 0; for (int i = 0; i < delays.Count; i++) sec += delays[i] / 1000.0;
            Console.WriteLine("写出 {0}：{1} 帧，{2}x{3}，总时长 {4:0.0} 秒，{5} KB",
                path, frames.Count, W, H, sec, Math.Round(new FileInfo(path).Length / 1024.0, 1));

            // 分镜图：GIF 是动的，**看第一帧什么也判断不了** —— 要确认"太快没有""飞进去的和
            // 拖出去的是不是同一张""有没有那种此地无银的自我说明"，只能真去播一遍。
            // 那是人最贵的时间。拼一张分镜，扫一眼就能看出来。
            try
            {
                int[] pick = { 0, 2, 5, 9, 13, 17, 21, 25, 28 };
                int cols = 3, cw = W, ch = H + 26;
                int rows = (pick.Length + cols - 1) / cols;
                using (Bitmap sb = new Bitmap(cols * cw, rows * ch, PixelFormat.Format32bppPArgb))
                using (Graphics sg = Graphics.FromImage(sb))
                {
                    sg.Clear(Color.FromArgb(18, 20, 24));
                    for (int i = 0; i < pick.Length; i++)
                    {
                        int fi = pick[i]; if (fi >= frames.Count) continue;
                        int cx = (i % cols) * cw, cy = (i / cols) * ch;
                        using (Font f2 = new Font("Segoe UI", 11, FontStyle.Bold))
                            sg.DrawString("#" + fi + "  " + delays[fi] + "ms", f2, Brushes.White, cx + 8, cy + 4);
                        sg.DrawImage(frames[fi], cx, cy + 24);
                    }
                    string sbp = Path.Combine("docs", "_raw", "demo-storyboard.png");   // _raw 是 gitignore 的：这是审阅用的中间产物，不进仓库
                    sb.Save(sbp, ImageFormat.Png);
                    Console.WriteLine("写出 " + sbp + "（不用播 GIF 也能看出动效对不对）");
                }
            }
            catch (Exception ex) { Console.WriteLine("分镜图失败：" + ex.Message); }

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
            // 统一放慢 1.5 倍（用户反馈"还是太快"）：帧数不变 -> 体积不变，只是节奏更从容
            for (int i = 0; i < delays.Count; i++) delays[i] = delays[i] * 3 / 2;

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
