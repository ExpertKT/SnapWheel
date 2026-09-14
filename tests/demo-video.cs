// 演示视频生成器：自己封装 MJPEG/AVI（机器上没有 ffmpeg / ImageMagick，也不装任何东西）。
// 画面全部离线绘制：假桌面、假窗口、假光标、字幕，以及用反射调 WheelForm.DrawWheel 渲染的轮盘。
// **不截真实屏幕、不模拟任何真实鼠标键盘输入** —— 光标、点击、拖拽都是画出来的。
//
// 分镜（40 秒 / 15fps / 600 帧）：
//   0–3s   桌面切窗口、聊天窗口弹消息
//   3–10s  左右分屏对比：左"主流截图 7 步"，右"SnapWheel 2 步"
//   10–13s 下载文件夹一堆同名文件 + 搜索无结果（没保存就不在）
//   13–14s 过渡：Ctrl+Shift+S
//   14–19s SnapWheel：框选 → 缩略图滑进环 → 拖进聊天框发送
//   19–24s 中键钉图：贴在屏幕上对着看
//   24–29s 取字：「字」→ 框选文字 → 结果窗口 → 翻译成中文 → 复制
//   29–33s 删除 + 托盘「撤销上一次删除」
//   33–38s 环上的图依次拖进文档，镜头拉远
//   38–40s 定格 + 片尾
//
// 用法：
//   demo-video.exe                  -> docs/demo.avi        (960x540)
//   demo-video.exe --vertical       -> docs/demo-vertical.avi (540x960)
//   demo-video.exe --verify <file>  -> 只做解码回读校验
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace SnapWheel
{
    static class DemoVideo
    {
        // ==================== 基本参数 ====================
        const int FPS = 15;
        const int TOTAL = 600;                       // 40.0 秒
        const int JQ = 80;                           // JPEG 质量
        const long BUDGET = 56L * 1024 * 1024;       // 体积上限（要求 ≤60MB）

        static int W = 960, H = 540;
        static bool VERT = false;
        static int SH = 450;            // 屏幕区（"桌面"）高度
        static int BANDH = 90;          // 底部字幕带高度
        static float KUI = 1f;          // 界面字号系数

        // 屏幕区内的布局
        static RectangleF RScreen, RTask, RDoc, RChat, RTable, RPin;

        static void InitLayout()
        {
            if (VERT) { W = 540; H = 960; SH = 790; BANDH = 170; KUI = 0.98f; }
            else { W = 960; H = 540; SH = 450; BANDH = 90; KUI = 1f; }
            RScreen = new RectangleF(0, 0, W, SH);
            if (VERT)
            {
                RTask = new RectangleF(0, SH - 40, W, 40);
                RChat = new RectangleF(26, 14, W - 52, 258);
                RDoc = new RectangleF(26, 336, W - 52, 240);
                RTable = new RectangleF(26, 14, W - 52, 296);
                RPin = new RectangleF(60, 330, 420, 230);
            }
            else
            {
                // 左下角留给轮盘（轮盘占 x<300、y>180），窗口都摆开一点
                RTask = new RectangleF(0, SH - 34, W, 34);
                RChat = new RectangleF(614, 24, 318, 330);
                RDoc = new RectangleF(300, 30, 302, 268);
                RTable = new RectangleF(318, 24, 486, 300);
                RPin = new RectangleF(330, 244, 292, 166);
            }
        }

        // ==================== 反射小工具 ====================
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

        // ==================== 小数学 ====================
        static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
        static float Eu(float t) { t = Clamp01(t); return t * t * (3f - 2f * t); }
        static float Seg(double t, double a, double b) { if (t <= a) return 0f; if (t >= b) return 1f; return (float)((t - a) / (b - a)); }
        static float SegE(double t, double a, double b) { return Eu(Seg(t, a, b)); }
        static PointF Pth(PointF a, PointF b, float p) { p = Eu(p); return new PointF(Lerp(a.X, b.X, p), Lerp(a.Y, b.Y, p)); }
        static string Part(string s, float p) { int n = (int)Math.Round(s.Length * Clamp01(p)); return s.Substring(0, n); }
        static Color A(Color c, int a) { return Color.FromArgb(a < 0 ? 0 : (a > 255 ? 255 : a), c); }

        static GraphicsPath Rnd(RectangleF r, float rad)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            if (d < 1f) { gp.AddRectangle(r); return gp; }
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }
        static void FillR(Graphics g, RectangleF r, float rad, Color c)
        {
            using (GraphicsPath gp = Rnd(r, rad)) using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, gp);
        }
        static void StrokeR(Graphics g, RectangleF r, float rad, Color c, float w)
        {
            using (GraphicsPath gp = Rnd(r, rad)) using (Pen p = new Pen(c, w)) g.DrawPath(p, gp);
        }
        static void Shadow(Graphics g, RectangleF r, float rad, int alpha, float dy)
        {
            for (int i = 4; i >= 1; i--)
            {
                RectangleF rr = new RectangleF(r.X - i * 1.5f, r.Y + dy - i * 1.5f + i, r.Width + i * 3f, r.Height + i * 3f);
                using (GraphicsPath gp = Rnd(rr, rad + i))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha / 6, 0, 0, 0))) g.FillPath(b, gp);
            }
        }

        // ==================== 字体缓存 ====================
        static Dictionary<string, Font> _fonts = new Dictionary<string, Font>();
        static Font PF(float px, FontStyle st)
        {
            if (px < 5f) px = 5f;
            string key = ((int)(px * 2)).ToString() + "|" + (int)st + "|" + (VERT ? "v" : "h");
            Font f;
            if (_fonts.TryGetValue(key, out f)) return f;
            string fam = "Microsoft YaHei UI";
            try { f = new Font(fam, px, st, GraphicsUnit.Pixel); }
            catch { f = new Font(FontFamily.GenericSansSerif, px, st, GraphicsUnit.Pixel); }
            _fonts[key] = f;
            return f;
        }
        static void Txt(Graphics g, string s, float px, FontStyle st, Color c, float x, float y)
        {
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, PF(px, st), b, x, y);
        }
        static void TxtC(Graphics g, string s, float px, FontStyle st, Color c, RectangleF r)
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, PF(px, st), b, r, sf);
            sf.Dispose();
        }
        // 居中 + 自动换行（中英混排都按字符宽度贪心折行）
        static void WrapC(Graphics g, string s, float px, FontStyle st, Color c, RectangleF r)
        {
            Font f = PF(px, st);
            List<string> lines = WrapLines(g, s, f, r.Width);
            float lh = px * 1.35f;
            float y = r.Y + (r.Height - lines.Count * lh) / 2f;
            for (int i = 0; i < lines.Count; i++)
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                using (SolidBrush b = new SolidBrush(c)) g.DrawString(lines[i], f, b, new RectangleF(r.X, y, r.Width, lh), sf);
                sf.Dispose();
                y += lh;
            }
        }
        static List<string> WrapLines(Graphics g, string s, Font f, float maxW)
        {
            List<string> outp = new List<string>();
            string cur = "";
            for (int i = 0; i < s.Length; i++)
            {
                string nxt = cur + s[i];
                if (cur.Length > 0 && g.MeasureString(nxt, f).Width > maxW) { outp.Add(cur); cur = s[i].ToString(); }
                else cur = nxt;
            }
            if (cur.Length > 0) outp.Add(cur);
            if (outp.Count == 0) outp.Add("");
            return outp;
        }
        static float TW(Graphics g, string s, float px, FontStyle st) { return g.MeasureString(s, PF(px, st)).Width; }

        // ==================== 轮盘（反射驱动） ====================
        static Settings _set;
        static WheelManager _mgr;
        static Store _st;
        static WheelForm _wf;
        static int _wfW, _wfH;

        static bool _wIntro; static float _wIntroT = 1f;
        static bool _wDropActive, _wDropExternal; static int _wDropCount;
        static StoreItem _wDragItem; static float _wDragProg;
        static StoreItem _wDelItem; static float _wDelProg;
        static string _wToast = ""; static float _wToastAge = -1f;
        static int _wHover = -1; static float _wHoverScale = 1f;
        static Dictionary<string, Bitmap> _wCache = new Dictionary<string, Bitmap>();
        static List<string> _wKeys = new List<string>();

        static void ApplyWheelState()
        {
            F(_wf, "_intro", _wIntro);
            F(_wf, "_introT", _wIntroT);
            F(_wf, "_collapsing", false);
            F(_wf, "_collapsed", false);
            F(_wf, "_show", 1f);
            F(_wf, "_dropActive", _wDropActive);
            F(_wf, "_dropExternal", _wDropExternal);
            F(_wf, "_dropCount", _wDropCount);
            F(_wf, "_dragOutItem", _wDragItem);
            F(_wf, "_dragOutProg", _wDragProg);
            F(_wf, "_deletingItem", _wDelItem);
            F(_wf, "_deleteProg", _wDelProg);
            F(_wf, "_hover", _wHover);
            F(_wf, "_toast", _wToast == null ? "" : _wToast);
            F(_wf, "_toastAt", DateTime.Now.AddSeconds(-(_wToastAge < 0 ? 0f : _wToastAge)));
            // 悬停放大：直接写 _scales（AnimTick 在离线渲染里不跑，动画得自己定）
            Dictionary<int, float> sc = (Dictionary<int, float>)GetField(_wf, "_scales");
            if (_wHover >= 0) sc[_wHover] = _wHoverScale; else sc.Clear();
        }
        static object GetField(object o, string n)
        {
            FieldInfo fi = o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) throw new Exception("no field " + n);
            return fi.GetValue(o);
        }
        // 让第 i 张缩略图停在"滑进来"的某个进度上（0=还在外面，1=完全到位）
        static void SetEnter(StoreItem it, float p)
        {
            Dictionary<StoreItem, DateTime> d = (Dictionary<StoreItem, DateTime>)GetField(_wf, "_enterT0");
            if (it == null) return;
            if (p >= 1f) { d.Remove(it); return; }
            d[it] = DateTime.Now.AddSeconds(-0.42 * Clamp01(p));
        }
        static void ClearEnter() { ((Dictionary<StoreItem, DateTime>)GetField(_wf, "_enterT0")).Clear(); }

        static string WheelKey()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(_st.Items.Count).Append('|').Append(_wIntro ? 1 : 0).Append('|').Append((int)(_wIntroT * 40));
            sb.Append('|').Append(_wDropActive ? 1 : 0).Append(_wDropExternal ? 1 : 0).Append(_wDropCount);
            sb.Append('|').Append(IdxOf(_wDragItem)).Append(':').Append((int)(_wDragProg * 60));
            sb.Append('|').Append(IdxOf(_wDelItem)).Append(':').Append((int)(_wDelProg * 60));
            sb.Append('|').Append(_wToast).Append(':').Append((int)(_wToastAge * 20));
            sb.Append('|').Append(_wHover).Append(':').Append((int)(_wHoverScale * 50));
            return sb.ToString();
        }
        static int IdxOf(StoreItem it) { return it == null ? -1 : _st.Items.IndexOf(it); }

        static bool Alive(Bitmap b) { if (b == null) return false; try { return b.Width > 0; } catch { return false; } }
        static Bitmap WheelBitmap()
        {
            string key = WheelKey();
            Bitmap b;
            if (_wCache.TryGetValue(key, out b))
            {
                if (Alive(b)) return b;                       // 缓存里那张必须还是活的（被误释放过就重画）
                _wCache.Remove(key); _wKeys.Remove(key);
            }
            ApplyWheelState();
            Bitmap nb = new Bitmap(_wfW, _wfH, PixelFormat.Format32bppPArgb);
            using (Graphics gg = Graphics.FromImage(nb))
            {
                gg.Clear(Color.Transparent);
                gg.SmoothingMode = SmoothingMode.AntiAlias;
                gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                gg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                Call(_wf, "DrawWheel", gg, _wfW, _wfH);
            }
            _wCache[key] = nb;
            _wKeys.Add(key);
            while (_wKeys.Count > 10)
            {
                string old = _wKeys[0]; _wKeys.RemoveAt(0);
                if (old == key) continue;                     // 绝不释放刚画的这张
                if (!_wKeys.Contains(old))
                {
                    Bitmap ob;
                    if (_wCache.TryGetValue(old, out ob)) { _wCache.Remove(old); if (ob != null && ob != nb) ob.Dispose(); }
                }
            }
            return nb;
        }
        // 把轮盘贴到 (ax, ay) 这个"工作区左下角"，size = 视频里轮盘窗口的边长
        static void DrawWheelLayer(Graphics g, float ax, float ay, float size)
        {
            Bitmap wb = WheelBitmap();
            InterpolationMode im = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(wb, new RectangleF(ax, ay - size, size, size));
            g.InterpolationMode = im;
        }
        // 环上第 i 张缩略图的中心（视频坐标）
        static PointF CardPos(float ax, float ay, float size, int i)
        {
            PointF p = (PointF)Call(_wf, "ItemCenter", i);
            float k = size / (float)_wfW;
            return new PointF(ax + p.X * k, ay - size + p.Y * k);
        }

        // ==================== 素材 ====================
        static Bitmap _shot;      // 主角截图（周报数据）
        static Bitmap _shot2;     // 图表
        static Bitmap _shot3;     // 邮件/名单

        static Bitmap MakeShotA()
        {
            Bitmap b = new Bitmap(440, 280, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.White);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(236, 242, 250))) g.FillRectangle(sb, 0, 0, 440, 58);
                Txt(g, "本周数据", 21, FontStyle.Bold, Color.FromArgb(38, 86, 168), 18, 16);
                using (Pen p = new Pen(Color.FromArgb(84, 122, 190), 2f)) g.DrawLine(p, 18, 47, 120, 47);
                Txt(g, "交付时间", 14, FontStyle.Regular, Color.FromArgb(120, 128, 140), 18, 84);
                Txt(g, "9 月 12 日 18:00", 16, FontStyle.Bold, Color.FromArgb(38, 44, 56), 128, 82);
                Txt(g, "负责同学", 14, FontStyle.Regular, Color.FromArgb(120, 128, 140), 18, 122);
                Txt(g, "小何", 16, FontStyle.Bold, Color.FromArgb(38, 44, 56), 128, 120);
                Txt(g, "完成度", 14, FontStyle.Regular, Color.FromArgb(120, 128, 140), 18, 160);
                Txt(g, "82%", 16, FontStyle.Bold, Color.FromArgb(232, 86, 110), 128, 158);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(232, 242, 236)))
                    FillR(g, new RectangleF(180, 154, 200, 18), 9, Color.FromArgb(226, 234, 240));
                FillR(g, new RectangleF(180, 154, 164, 18), 9, Color.FromArgb(72, 182, 128));
                Txt(g, "备注：本周报告已发出", 14, FontStyle.Regular, Color.FromArgb(120, 128, 140), 18, 206);
                FillR(g, new RectangleF(18, 232, 150, 30), 8, Color.FromArgb(238, 90, 112));
                TxtC(g, "查看明细", 13, FontStyle.Bold, Color.White, new RectangleF(18, 232, 150, 30));
            }
            return b;
        }
        static Bitmap MakeShotB()
        {
            Bitmap b = new Bitmap(440, 280, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.White);
                Txt(g, "近 7 天交付量", 19, FontStyle.Bold, Color.FromArgb(38, 44, 56), 18, 16);
                int[] v = { 42, 58, 36, 74, 66, 88, 61 };
                string[] d = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
                for (int i = 0; i < 7; i++)
                {
                    float x = 24 + i * 58, h = v[i] * 1.7f;
                    FillR(g, new RectangleF(x, 236 - h, 34, h), 6, Color.FromArgb(72, 132, 226));
                    TxtC(g, d[i], 11, FontStyle.Regular, Color.FromArgb(120, 128, 140), new RectangleF(x - 8, 240, 50, 18));
                }
            }
            return b;
        }
        static Bitmap MakeShotC()
        {
            Bitmap b = new Bitmap(440, 280, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.White);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(245, 246, 248))) g.FillRectangle(sb, 0, 0, 440, 52);
                Txt(g, "本周排期", 19, FontStyle.Bold, Color.FromArgb(38, 44, 56), 18, 14);
                for (int i = 0; i < 6; i++)
                {
                    float y = 70 + i * 32;
                    FillR(g, new RectangleF(18, y, 14, 14), 4, i % 2 == 0 ? Color.FromArgb(72, 182, 128) : Color.FromArgb(228, 176, 74));
                    Txt(g, "任务 " + (i + 1) + "：交付验收", 13, FontStyle.Regular, Color.FromArgb(70, 76, 88), 44, y - 2);
                    using (Pen p = new Pen(Color.FromArgb(228, 232, 238), 1f)) g.DrawLine(p, 18, y + 24, 422, y + 24);
                }
            }
            return b;
        }

        // ==================== 假桌面基础件 ====================
        static void DesktopBase(Graphics g)
        {
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(0, 0, W, SH),
                Color.FromArgb(56, 78, 118), Color.FromArgb(20, 26, 40), 62f))
                g.FillRectangle(lg, 0, 0, W, SH);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(22, 120, 170, 255)))
            { g.FillEllipse(b, -SH * 0.25f, SH * 0.28f, SH * 0.9f, SH * 0.9f); }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(16, 90, 220, 200)))
            { g.FillEllipse(b, W * 0.55f, -SH * 0.35f, SH * 0.8f, SH * 0.8f); }
        }

        static void Taskbar(Graphics g, string clock, int flashIdx, float pulse)
        {
            RectangleF r = RTask;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(236, 20, 22, 28))) g.FillRectangle(b, r);
            float k = VERT ? 1f : 0.9f;
            float icon = r.Height - 12 * k;
            float y = r.Y + (r.Height - icon) / 2f;
            // 开始按钮
            using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 240, 244, 250)))
            {
                for (int i = 0; i < 4; i++)
                    g.FillRectangle(b, r.X + 12 * k + (i % 2) * (icon * 0.42f), y + (i / 2) * (icon * 0.42f), icon * 0.36f, icon * 0.36f);
            }
            // 应用图标：文件 / 浏览器 / 聊天 / 截图工具
            string[] apps = { "文件", "浏览器", "聊天", "截图" };
            for (int i = 0; i < apps.Length; i++)
            {
                float x = r.X + (54 + i * 84) * k;
                RectangleF br = new RectangleF(x, y - 3 * k, 60 * k, icon + 6 * k);
                bool hot = (i == flashIdx);
                if (hot)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(60 + 60 * pulse), 90, 150, 240)))
                        FillR(g, br, 5 * k, Color.FromArgb((int)(40 + 70 * pulse), 90, 150, 240));
                }
                else FillR(g, br, 5 * k, Color.FromArgb(24, 255, 255, 255));
                // 图标（简笔）
                float ix = x + 10 * k, iy = y + 2 * k, isz = icon - 8 * k;
                Color ic = hot ? Color.White : Color.FromArgb(210, 226, 236, 250);
                using (Pen p = new Pen(ic, 1.6f * k))
                {
                    if (i == 0) { g.DrawRectangle(p, ix, iy + isz * 0.2f, isz * 0.8f, isz * 0.5f); g.DrawLine(p, ix, iy + isz * 0.25f, ix + isz * 0.4f, iy); }
                    else if (i == 1) { g.DrawEllipse(p, ix, iy, isz * 0.8f, isz * 0.8f); g.DrawLine(p, ix + isz * 0.4f, iy, ix + isz * 0.4f, iy + isz * 0.8f); }
                    else if (i == 2)
                    {
                        using (SolidBrush b = new SolidBrush(ic)) g.FillEllipse(b, ix, iy + isz * 0.15f, isz * 0.75f, isz * 0.55f);
                    }
                    else { g.DrawRectangle(p, ix, iy + isz * 0.15f, isz * 0.8f, isz * 0.6f); g.DrawLine(p, ix + isz * 0.25f, iy, ix + isz * 0.25f, iy + isz * 0.18f); }
                }
                Txt(g, apps[i], 10f * k, FontStyle.Regular, hot ? Color.White : Color.FromArgb(200, 214, 226, 244), x + 32 * k, y + 4 * k);
                if (hot) FillR(g, new RectangleF(br.X, br.Bottom - 2 * k, br.Width, 2.5f * k), 1.5f * k, Color.FromArgb(120, 170, 255));
            }
            // 托盘：SnapWheel 的环图标 + 时间
            float tx = r.Right - 96 * k;
            DrawRingGlyph(g, new RectangleF(tx, y + 1 * k, icon - 6 * k, icon - 6 * k), 0.9f);
            Txt(g, "17:30", 11f * k, FontStyle.Regular, Color.FromArgb(226, 236, 248), r.Right - 52 * k, y + 1 * k);
            Txt(g, "9/12 周五", 9.5f * k, FontStyle.Regular, Color.FromArgb(190, 206, 226), r.Right - 62 * k, y + 16 * k);
        }
        static void DrawRingGlyph(Graphics g, RectangleF r, float a)
        {
            float d = Math.Min(r.Width, r.Height);
            RectangleF sq = new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
            using (Pen p = new Pen(Color.FromArgb((int)(235 * a), 120, 200, 255), Math.Max(1.5f, d * 0.16f)))
            { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawArc(p, sq, 100, 300); }
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(240 * a), 255, 255, 255)))
                g.FillEllipse(b, sq.X + sq.Width * 0.62f, sq.Y + sq.Height * 0.10f, d * 0.24f, d * 0.24f);
        }

        static void WinFrame(Graphics g, RectangleF r, string title, bool active, float k, Color? accent)
        {
            float tb = 26 * k;
            Shadow(g, r, 8 * k, 130, 6 * k);
            FillR(g, r, 8 * k, Color.FromArgb(252, 252, 253));
            Color ac = accent.HasValue ? accent.Value : Color.FromArgb(58, 110, 200);
            if (!active) ac = Color.FromArgb(238, 240, 244);
            FillR(g, new RectangleF(r.X, r.Y, r.Width, tb), 8 * k, ac);
            using (SolidBrush b = new SolidBrush(ac)) g.FillRectangle(b, r.X, r.Y + tb - 8 * k, r.Width, 8 * k);
            Color tc = active ? Color.White : Color.FromArgb(70, 76, 88);
            Txt(g, title, 12f * k, FontStyle.Bold, tc, r.X + 12 * k, r.Y + 6 * k);
            using (Pen p = new Pen(active ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(150, 90, 96, 108), 1.4f * k))
            {
                float cx = r.Right - 18 * k;
                g.DrawLine(p, cx - 4 * k, r.Y + 9 * k, cx + 4 * k, r.Y + 17 * k);
                g.DrawLine(p, cx + 4 * k, r.Y + 9 * k, cx - 4 * k, r.Y + 17 * k);
            }
        }

        // ---- 聊天窗口 ----
        class Msg { public string Text; public bool Mine; public Bitmap Img; public float T; public bool Key; }
        static void PaintChat(Graphics g, RectangleF r, float k, List<Msg> msgs, bool active, string typing)
        {
            WinFrame(g, r, "项目群", active, k, Color.FromArgb(72, 176, 116));
            float tb = 26 * k;
            RectangleF body = new RectangleF(r.X, r.Y + tb, r.Width, r.Height - tb);
            FillR(g, body, 0, Color.FromArgb(246, 248, 250));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(246, 248, 250))) g.FillRectangle(b, body);
            float y = body.Y + 12 * k;
            float maxW = body.Width * 0.72f;
            if (msgs != null) for (int i = 0; i < msgs.Count; i++)
            {
                Msg m = msgs[i];
                float ap = Eu(m.T);
                if (ap <= 0.01f) continue;
                float pop = 0.9f + 0.1f * ap;
                if (m.Img != null)
                {
                    float iw = maxW, ih = iw * m.Img.Height / (float)m.Img.Width;
                    float bx = m.Mine ? body.Right - iw - 12 * k : body.X + 12 * k;
                    RectangleF br = new RectangleF(bx, y, iw, ih + 6 * k);
                    FillR(g, br, 8 * k, Color.FromArgb((int)(255 * ap), m.Mine ? Color.FromArgb(198, 232, 208) : Color.White));
                    InterpolationMode im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(m.Img, new RectangleF(bx + 3 * k, y + 3 * k, iw - 6 * k, ih));
                    g.InterpolationMode = im;
                    y += ih + 16 * k;
                }
                else if (m.Text != null && m.Text.Length > 0)
                {
                    float fs = 12.5f * k;
                    Font f = PF(fs, m.Key ? FontStyle.Bold : FontStyle.Regular);
                    List<string> lines = WrapLines(g, m.Text, f, maxW - 20 * k);
                    float w = 0; for (int j = 0; j < lines.Count; j++) w = Math.Max(w, g.MeasureString(lines[j], f).Width);
                    w += 20 * k;
                    float h = lines.Count * fs * 1.35f + 12 * k;
                    float bx = m.Mine ? body.Right - w - 12 * k : body.X + 12 * k;
                    RectangleF br = new RectangleF(bx, y, w, h);
                    Color bc = m.Mine ? Color.FromArgb(206, 236, 214) : Color.White;
                    FillR(g, br, 8 * k, Color.FromArgb((int)(255 * ap), bc));
                    if (m.Key)
                    {
                        using (Pen p = new Pen(Color.FromArgb((int)(255 * ap), 232, 96, 110), 1.6f * k))
                            StrokeR(g, br, 8 * k, Color.FromArgb((int)(255 * ap), 232, 96, 110), 1.6f * k);
                    }
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(245 * ap), 38, 44, 56)))
                        for (int j = 0; j < lines.Count; j++)
                            g.DrawString(lines[j], f, b, bx + 10 * k, y + 5 * k + j * fs * 1.35f);
                    y += h + 10 * k;
                }
            }
            // 输入框
            float ih2 = 30 * k;
            RectangleF inp = new RectangleF(body.X + 10 * k, body.Bottom - ih2 - 10 * k, body.Width - 20 * k, ih2);
            FillR(g, inp, 6 * k, Color.White);
            StrokeR(g, inp, 6 * k, Color.FromArgb(224, 228, 234), 1.2f * k);
            if (typing != null && typing.Length > 0)
            {
                Txt(g, typing, 12.5f * k, FontStyle.Regular, Color.FromArgb(44, 50, 62), inp.X + 10 * k, inp.Y + 6 * k);
                float cx = inp.X + 10 * k + TW(g, typing, 12.5f * k, FontStyle.Regular);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 100, 116))) g.FillRectangle(b, cx + 1, inp.Y + 7 * k, 1.6f * k, 16 * k);
            }
            else Txt(g, "输入消息…", 12.5f * k, FontStyle.Regular, Color.FromArgb(168, 174, 184), inp.X + 10 * k, inp.Y + 6 * k);
            FillR(g, new RectangleF(inp.Right - 54 * k, inp.Y, 54 * k, ih2), 6 * k, Color.FromArgb(72, 176, 116));
            TxtC(g, "发送", 12f * k, FontStyle.Bold, Color.White, new RectangleF(inp.Right - 54 * k, inp.Y, 54 * k, ih2));
        }

        // ---- 文档窗口（周报表单） ----
        static void PaintDoc(Graphics g, RectangleF r, float k, string title, bool active, string[] lines, List<Bitmap> pics, string typing)
        {
            WinFrame(g, r, title, active, k, Color.FromArgb(58, 110, 200));
            float tb = 26 * k;
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, new RectangleF(r.X + 1, r.Y + tb, r.Width - 2, r.Height - tb - 1));
            float y = r.Y + tb + 14 * k;
            Txt(g, "本周周报", 16f * k, FontStyle.Bold, Color.FromArgb(34, 40, 52), r.X + 18 * k, y);
            y += 30 * k;
            if (lines != null) for (int i = 0; i < lines.Length; i++)
            {
                Txt(g, lines[i], 12.5f * k, FontStyle.Regular, Color.FromArgb(74, 82, 96), r.X + 18 * k, y);
                y += 22 * k;
            }
            if (typing != null)
            {
                Txt(g, typing, 12.5f * k, FontStyle.Regular, Color.FromArgb(34, 40, 52), r.X + 18 * k, y);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 100, 116)))
                    g.FillRectangle(b, r.X + 18 * k + TW(g, typing, 12.5f * k, FontStyle.Regular) + 1, y + 2 * k, 1.6f * k, 15 * k);
            }
            if (pics != null && pics.Count > 0)
            {
                int cols = pics.Count >= 3 ? 3 : 2;
                float gapx = 6 * k;
                float px = r.X + 18 * k, py = y + 12 * k;
                float pw = (r.Width - 36 * k - (cols - 1) * gapx) / cols;
                float ph = pw * 0.62f;
                if (py + ph > r.Bottom - 8 * k) { ph = r.Bottom - 8 * k - py; pw = ph / 0.62f; }
                for (int i = 0; i < pics.Count && i < 6; i++)
                {
                    float bx = px + (i % cols) * (pw + gapx);
                    float by = py + (i / cols) * (ph + gapx);
                    if (by + ph > r.Bottom - 6 * k) break;
                    FillR(g, new RectangleF(bx - 2, by - 2, pw + 4, ph + 4), 5 * k, Color.FromArgb(228, 232, 238));
                    InterpolationMode im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(pics[i], new RectangleF(bx, by, pw, ph));
                    g.InterpolationMode = im;
                }
            }
        }

        // ---- 英文表格窗口（取字那一段的主角） ----
        static void PaintTable(Graphics g, RectangleF r, float k, bool active, string[] rows, float hl)
        {
            WinFrame(g, r, "订单数据.xlsx", active, k, Color.FromArgb(46, 140, 92));
            float tb = 26 * k;
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, new RectangleF(r.X + 1, r.Y + tb, r.Width - 2, r.Height - tb - 1));
            Txt(g, "Order list", 17f * k, FontStyle.Bold, Color.FromArgb(34, 40, 52), r.X + 20 * k, r.Y + tb + 14 * k);
            float y = r.Y + tb + 52 * k;
            for (int i = 0; i < rows.Length; i++)
            {
                RectangleF rr = new RectangleF(r.X + 16 * k, y - 4 * k, r.Width - 32 * k, 26 * k);
                if (hl > 0 && i == 0) FillR(g, rr, 4 * k, Color.FromArgb((int)(150 * hl), 255, 236, 170));
                Txt(g, rows[i], 13.5f * k, i == 0 ? FontStyle.Bold : FontStyle.Regular, Color.FromArgb(56, 62, 76), r.X + 24 * k, y);
                using (Pen p = new Pen(Color.FromArgb(236, 238, 242), 1f)) g.DrawLine(p, r.X + 16 * k, y + 22 * k, r.Right - 16 * k, y + 22 * k);
                y += 32 * k;
            }
        }

        // ---- 资源管理器：下载文件夹 ----
        static string[] _dlFiles;
        static void PaintExplorer(Graphics g, RectangleF r, float k, float scroll, int hover, string query, float noRes)
        {
            WinFrame(g, r, "下载", true, k, Color.FromArgb(240, 196, 84));
            float tb = 26 * k;
            RectangleF body = new RectangleF(r.X + 1, r.Y + tb, r.Width - 2, r.Height - tb - 1);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, body);
            // 工具栏 + 搜索框
            float bar = 30 * k;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(248, 249, 251))) g.FillRectangle(b, new RectangleF(body.X, body.Y, body.Width, bar));
            Txt(g, "新建", 11.5f * k, FontStyle.Regular, Color.FromArgb(80, 88, 100), body.X + 12 * k, body.Y + 7 * k);
            Txt(g, "排序", 11.5f * k, FontStyle.Regular, Color.FromArgb(80, 88, 100), body.X + 56 * k, body.Y + 7 * k);
            RectangleF sb = new RectangleF(body.Right - 176 * k, body.Y + 4 * k, 164 * k, 22 * k);
            FillR(g, sb, 11 * k, Color.White);
            StrokeR(g, sb, 11 * k, noRes > 0.5f ? Color.FromArgb(214, 96, 96) : Color.FromArgb(220, 224, 230), 1.2f * k);
            Txt(g, query == null || query.Length == 0 ? "搜索 下载" : query, 11.5f * k, FontStyle.Regular,
                query == null || query.Length == 0 ? Color.FromArgb(170, 176, 186) : Color.FromArgb(48, 54, 66), sb.X + 10 * k, sb.Y + 4 * k);
            using (Pen p = new Pen(Color.FromArgb(120, 128, 140), 1.4f * k))
            { g.DrawEllipse(p, sb.Right - 18 * k, sb.Y + 6 * k, 9 * k, 9 * k); g.DrawLine(p, sb.Right - 10 * k, sb.Y + 15 * k, sb.Right - 6 * k, sb.Y + 19 * k); }
            // 面包屑
            float cy2 = body.Y + bar + 4 * k;
            Txt(g, "此电脑  ›  下载", 11.5f * k, FontStyle.Regular, Color.FromArgb(110, 118, 130), body.X + 12 * k, cy2);
            // 列表
            float listTop = cy2 + 24 * k;
            RectangleF list = new RectangleF(body.X + 8 * k, listTop, body.Width - 26 * k, body.Bottom - listTop - 6 * k);
            g.SetClip(list);
            if (noRes > 0.5f)
            {
                TxtC(g, "没有匹配的结果", 14f * k, FontStyle.Bold, Color.FromArgb(190, 196, 206), list);
                TxtC(g, "试试别的关键词", 11.5f * k, FontStyle.Regular, Color.FromArgb(200, 206, 214), new RectangleF(list.X, list.Y + 26 * k, list.Width, 24 * k));
            }
            else
            {
                Txt(g, "名称", 11f * k, FontStyle.Regular, Color.FromArgb(140, 148, 160), list.X + 34 * k, list.Y);
                Txt(g, "修改日期", 11f * k, FontStyle.Regular, Color.FromArgb(140, 148, 160), list.X + list.Width * 0.62f, list.Y);
                float ly = list.Y + 22 * k;
                for (int i = 0; i < 14; i++)
                {
                    int idx = i + (int)Math.Round(scroll);
                    if (idx < 0 || idx >= _dlFiles.Length) { ly += 24 * k; continue; }
                    RectangleF row = new RectangleF(list.X, ly - 2 * k, list.Width, 23 * k);
                    if (i == hover) FillR(g, row, 4 * k, Color.FromArgb(60, 96, 170, 240));
                    FillR(g, new RectangleF(list.X + 6 * k, ly, 14 * k, 16 * k), 3 * k, Color.FromArgb(226, 232, 240));
                    Txt(g, _dlFiles[idx], 12f * k, FontStyle.Regular, Color.FromArgb(58, 64, 76), list.X + 34 * k, ly);
                    Txt(g, "今天 17:0" + (idx % 10), 11f * k, FontStyle.Regular, Color.FromArgb(150, 156, 168), list.X + list.Width * 0.62f, ly + 1 * k);
                    ly += 24 * k;
                }
            }
            g.ResetClip();
            // 滚动条
            RectangleF track = new RectangleF(body.Right - 12 * k, listTop, 8 * k, list.Bottom - listTop);
            FillR(g, track, 4 * k, Color.FromArgb(242, 244, 247));
            float th = track.Height * 0.32f;
            float tp = track.Y + (track.Height - th) * Clamp01(scroll / Math.Max(1f, _dlFiles.Length - 14));
            FillR(g, new RectangleF(track.X + 1 * k, tp, track.Width - 2 * k, th), 4 * k, Color.FromArgb(190, 198, 210));
        }

        // ---- Windows 通知（右下角） ----
        static void PaintNotif(Graphics g, RectangleF r, float k, string title, string body, string sub, Bitmap thumb, float a)
        {
            if (a <= 0.01f) return;
            float slide = (1f - Eu(a)) * 30 * k;
            r = new RectangleF(r.X + slide, r.Y, r.Width, r.Height);
            Shadow(g, r, 8 * k, 160, 5 * k);
            FillR(g, r, 8 * k, Color.FromArgb((int)(242 * a), 32, 34, 40));
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(255 * a), 236, 240, 246)))
                g.FillRectangle(b, r.X + 12 * k, r.Y + 12 * k, 22 * k, 22 * k);
            Txt(g, title, 11.5f * k, FontStyle.Bold, Color.FromArgb((int)(240 * a), 236, 240, 246), r.X + 42 * k, r.Y + 10 * k);
            Txt(g, body, 12.5f * k, FontStyle.Regular, Color.FromArgb((int)(250 * a), 255, 255, 255), r.X + 12 * k, r.Y + 40 * k);
            if (sub != null) Txt(g, sub, 11f * k, FontStyle.Regular, Color.FromArgb((int)(190 * a), 170, 190, 220), r.X + 12 * k, r.Y + 62 * k);
            if (thumb != null)
            {
                float iw = r.Width - 24 * k, ih = iw * thumb.Height / (float)thumb.Width;
                if (ih > r.Height - 84 * k) { ih = r.Height - 84 * k; iw = ih * thumb.Width / (float)thumb.Height; }
                InterpolationMode im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = a; ia.SetColorMatrix(cm);
                    g.DrawImage(thumb, new Rectangle((int)(r.X + 12 * k), (int)(r.Y + 82 * k), (int)iw, (int)ih), 0, 0, thumb.Width, thumb.Height, GraphicsUnit.Pixel, ia);
                }
                g.InterpolationMode = im;
            }
        }

        // ---- 截图工具 / 另存为 ----
        static void PaintSnipTool(Graphics g, RectangleF r, float k, Bitmap shot, float hlSave)
        {
            WinFrame(g, r, "截图和草图", true, k, Color.FromArgb(64, 96, 180));
            float tb = 26 * k;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(246, 247, 249))) g.FillRectangle(b, new RectangleF(r.X + 1, r.Y + tb, r.Width - 2, r.Height - tb - 1));
            RectangleF ir = new RectangleF(r.X + 16 * k, r.Y + tb + 14 * k, r.Width - 32 * k, r.Height - tb - 62 * k);
            InterpolationMode im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(shot, ir);
            g.InterpolationMode = im;
            StrokeR(g, ir, 2 * k, Color.FromArgb(214, 220, 228), 1f * k);
            float by = r.Bottom - 36 * k;
            string[] bs = { "编辑", "保存", "复制" };
            for (int i = 0; i < bs.Length; i++)
            {
                RectangleF br = new RectangleF(r.X + 16 * k + i * 76 * k, by, 68 * k, 26 * k);
                bool hot = (i == 1 && hlSave > 0.5f);
                FillR(g, br, 5 * k, hot ? Color.FromArgb(198, 224, 255) : Color.FromArgb(238, 241, 245));
                StrokeR(g, br, 5 * k, hot ? Color.FromArgb(58, 110, 200) : Color.FromArgb(220, 226, 234), 1.2f * k);
                TxtC(g, bs[i], 12f * k, FontStyle.Bold, hot ? Color.FromArgb(24, 60, 130) : Color.FromArgb(70, 78, 92), br);
            }
        }
        static void PaintSaveDlg(Graphics g, RectangleF r, float k, string name, int hlFolder, float hlSave)
        {
            WinFrame(g, r, "另存为", true, k, Color.FromArgb(64, 96, 180));
            float tb = 26 * k;
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, new RectangleF(r.X + 1, r.Y + tb, r.Width - 2, r.Height - tb - 1));
            string[] folders = { "快速访问", "桌面", "下载", "文档", "图片" };
            float sideW = r.Width * 0.3f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(246, 247, 249)))
                g.FillRectangle(b, new RectangleF(r.X + 1, r.Y + tb, sideW, r.Height - tb - 1));
            for (int i = 0; i < folders.Length; i++)
            {
                RectangleF fr = new RectangleF(r.X + 6 * k, r.Y + tb + 12 * k + i * 26 * k, sideW - 12 * k, 23 * k);
                if (i == hlFolder) FillR(g, fr, 4 * k, Color.FromArgb(206, 226, 250));
                Txt(g, folders[i], 12f * k, FontStyle.Regular, i == hlFolder ? Color.FromArgb(28, 62, 130) : Color.FromArgb(70, 78, 92), fr.X + 10 * k, fr.Y + 3 * k);
            }
            float lx = r.X + sideW + 12 * k;
            Txt(g, "名称", 11f * k, FontStyle.Regular, Color.FromArgb(140, 148, 160), lx, r.Y + tb + 14 * k);
            RectangleF nf = new RectangleF(lx, r.Y + tb + 46 * k, r.Width - sideW - 30 * k, 26 * k);
            FillR(g, nf, 4 * k, Color.White);
            StrokeR(g, nf, 4 * k, Color.FromArgb(120, 160, 220), 1.4f * k);
            Txt(g, name, 12f * k, FontStyle.Regular, Color.FromArgb(40, 46, 58), nf.X + 8 * k, nf.Y + 4 * k);
            for (int i = 0; i < 5; i++)
            {
                float y = r.Y + tb + 86 * k + i * 22 * k;
                if (y > r.Bottom - 44 * k) break;
                if (i == 0) FillR(g, new RectangleF(lx - 4 * k, y - 3 * k, r.Width - sideW - 20 * k, 21 * k), 4 * k, Color.FromArgb(60, 96, 170, 240));
                Txt(g, i == 0 ? name : "Screenshot(" + (35 - i) + ").png", 11.5f * k, FontStyle.Regular, Color.FromArgb(70, 78, 92), lx + 4 * k, y);
            }
            float by = r.Bottom - 36 * k;
            RectangleF s1 = new RectangleF(r.Right - 170 * k, by, 76 * k, 26 * k);
            RectangleF s2 = new RectangleF(r.Right - 88 * k, by, 76 * k, 26 * k);
            bool hot = hlSave > 0.5f;
            FillR(g, s1, 5 * k, hot ? Color.FromArgb(58, 110, 200) : Color.FromArgb(238, 241, 245));
            StrokeR(g, s1, 5 * k, hot ? Color.FromArgb(30, 70, 150) : Color.FromArgb(220, 226, 234), 1.2f * k);
            TxtC(g, "保存", 12f * k, FontStyle.Bold, hot ? Color.White : Color.FromArgb(70, 78, 92), s1);
            FillR(g, s2, 5 * k, Color.FromArgb(238, 241, 245));
            StrokeR(g, s2, 5 * k, Color.FromArgb(220, 226, 234), 1.2f * k);
            TxtC(g, "取消", 12f * k, FontStyle.Regular, Color.FromArgb(70, 78, 92), s2);
        }

        // ---- 标注工具条（截图后的那条） ----
        static void PaintToolbar(Graphics g, RectangleF r, float k, int sel, int hover)
        {
            Shadow(g, r, 10 * k, 170, 5 * k);
            FillR(g, r, 10 * k, Color.FromArgb(238, 26, 28, 34));
            StrokeR(g, r, 10 * k, Color.FromArgb(60, 255, 255, 255), 1f);
            float bw = 34 * k, bh = 30 * k, gp = 6 * k;
            float bx = r.X + 8 * k, by = r.Y + (r.Height - bh) / 2f;
            for (int i = 0; i < 6; i++)
            {
                RectangleF br = new RectangleF(bx + i * (bw + gp), by, bw, bh);
                bool on = (i == sel), hv = (i == hover);
                if (on) FillR(g, br, 7 * k, Color.FromArgb(70, 130, 235));
                else if (hv) FillR(g, br, 7 * k, Color.FromArgb(52, 255, 255, 255));
                Color ic = Color.White;
                using (Pen p = new Pen(ic, 1.7f * k))
                {
                    RectangleF d = new RectangleF(br.X + 8 * k, br.Y + 8 * k, br.Width - 16 * k, br.Height - 16 * k);
                    switch (i)
                    {
                        case 0: g.DrawRectangle(p, d.X, d.Y - 1 * k, d.Width * 0.6f, d.Height + 2 * k); break;
                        case 1:
                            g.DrawLine(p, d.Left, d.Bottom, d.Right, d.Top);
                            using (SolidBrush b = new SolidBrush(ic))
                                g.FillPolygon(b, new PointF[] { new PointF(d.Right, d.Top), new PointF(d.Right - d.Width * 0.45f, d.Top + d.Height * 0.1f), new PointF(d.Right - d.Width * 0.1f, d.Top + d.Height * 0.45f) });
                            break;
                        case 2: g.DrawRectangle(p, d.X, d.Y, d.Width, d.Height); break;
                        case 3:
                            using (SolidBrush b = new SolidBrush(ic))
                            {
                                float hw = d.Width / 2f, hh = d.Height / 2f;
                                g.FillRectangle(b, d.Left, d.Top, hw - 1, hh - 1);
                                g.FillRectangle(b, d.Left + hw + 1, d.Top, hw - 1, hh - 1);
                                g.FillRectangle(b, d.Left, d.Top + hh + 1, hw - 1, hh - 1);
                                g.FillRectangle(b, d.Left + hw + 1, d.Top + hh + 1, hw - 1, hh - 1);
                            }
                            break;
                        case 4: TxtC(g, "T", 14f * k, FontStyle.Bold, ic, br); break;
                        case 5: TxtC(g, "字", 14f * k, FontStyle.Bold, ic, br); break;
                    }
                }
                bx += bw + gp;
            }
            bx += 6 * k;
            using (Pen p = new Pen(Color.FromArgb(70, 255, 255, 255), 1f)) g.DrawLine(p, bx, r.Y + 8 * k, bx, r.Bottom - 8 * k);
            bx += 10 * k;
            Color[] cols = { Color.FromArgb(232, 86, 110), Color.FromArgb(240, 176, 72), Color.FromArgb(72, 182, 128), Color.FromArgb(96, 150, 240) };
            for (int i = 0; i < 4; i++)
            {
                g.FillEllipse(new SolidBrush(cols[i]), bx + i * (22 * k) + 4 * k, by + 9 * k, 14 * k, 14 * k);
            }
            bx += 4 * 22 * k + 12 * k;
            Txt(g, "A-", 13f * k, FontStyle.Bold, Color.White, bx, by + 6 * k);
            Txt(g, "A+", 13f * k, FontStyle.Bold, Color.White, bx + 34 * k, by + 6 * k);
        }

        // ---- 取字窗口 ----
        static void PaintOcr(Graphics g, RectangleF r, float k, int phase, string en, string zh, float a)
        {
            if (a <= 0.01f) return;
            float slide = (1f - Eu(a)) * 24 * k;
            r = new RectangleF(r.X + slide, r.Y + slide * 0.4f, r.Width, r.Height);
            int al = (int)(255 * a);
            Shadow(g, r, 8 * k, (int)(150 * a), 5 * k);
            FillR(g, r, 8 * k, Color.FromArgb(al, 250, 251, 252));
            FillR(g, new RectangleF(r.X, r.Y, r.Width, 30 * k), 8 * k, Color.FromArgb(al, 238, 242, 248));
            Txt(g, "SnapWheel 取字", 12.5f * k, FontStyle.Bold, Color.FromArgb(al, 46, 54, 68), r.X + 12 * k, r.Y + 8 * k);
            Txt(g, phase == 0 ? "取字中…" : ("认出来 " + en.Replace(" ", "").Replace("\n", "").Length + " 个字"),
                14f * k, FontStyle.Bold, Color.FromArgb(al, 34, 40, 52), r.X + 14 * k, r.Y + 40 * k);
            string state = phase == 0 ? "正在识别这块区域的文字" :
                           phase == 1 ? "原文已复制到剪贴板；要用译文点下面的「翻译」" :
                           phase == 2 ? "翻译中…（用 MyMemory 免费接口，要联网）" :
                           phase == 3 ? "译文已复制" : "复制好了，可以直接粘贴";
            Txt(g, state, 11f * k, FontStyle.Regular, Color.FromArgb(al, 120, 130, 146), r.X + 14 * k, r.Y + 62 * k);
            float pw = r.Width - 28 * k;
            RectangleF src = new RectangleF(r.X + 14 * k, r.Y + 90 * k, pw, 74 * k);
            Txt(g, "原文", 11f * k, FontStyle.Bold, Color.FromArgb(al, 96, 104, 118), src.X, src.Y - 16 * k);
            FillR(g, src, 5 * k, Color.FromArgb(al, 242, 244, 247));
            StrokeR(g, src, 5 * k, Color.FromArgb(al, 226, 230, 236), 1f);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(al, 44, 50, 62)))
            {
                Font f = PF(12f * k, FontStyle.Regular);
                string[] ls = en.Split('\n');
                for (int i = 0; i < ls.Length; i++) g.DrawString(ls[i], f, b, src.X + 10 * k, src.Y + 8 * k + i * 17 * k);
            }
            RectangleF dst = new RectangleF(r.X + 14 * k, r.Y + 190 * k, pw, 74 * k);
            Txt(g, "译文", 11f * k, FontStyle.Bold, Color.FromArgb(al, 96, 104, 118), dst.X, dst.Y - 16 * k);
            FillR(g, dst, 5 * k, Color.FromArgb(al, 242, 244, 247));
            StrokeR(g, dst, 5 * k, Color.FromArgb(al, 226, 230, 236), 1f);
            if (zh != null && zh.Length > 0)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(al, 30, 90, 60)))
                {
                    Font f = PF(12.5f * k, FontStyle.Bold);
                    string[] ls = zh.Split('\n');
                    for (int i = 0; i < ls.Length; i++) g.DrawString(ls[i], f, b, dst.X + 10 * k, dst.Y + 8 * k + i * 18 * k);
                }
            else if (phase == 2) Txt(g, "翻译中…", 12f * k, FontStyle.Regular, Color.FromArgb(al, 150, 158, 172), dst.X + 10 * k, dst.Y + 10 * k);
            float by = r.Bottom - 40 * k;
            string[] bs = { "翻译成中文", "复制原文", "复制译文", "关闭" };
            float x = r.X + 14 * k;
            for (int i = 0; i < bs.Length; i++)
            {
                float w = (i == 0 ? 104 * k : 78 * k);
                RectangleF br = new RectangleF(x, by, w, 28 * k);
                bool hot = (i == 0 && (phase == 1 || phase == 2)) || (i == 2 && phase >= 3);
                FillR(g, br, 5 * k, Color.FromArgb(al, hot ? 58 : 238, hot ? 110 : 241, hot ? 200 : 245));
                StrokeR(g, br, 5 * k, Color.FromArgb(al, hot ? 30 : 220, hot ? 70 : 226, hot ? 150 : 234), 1.2f * k);
                TxtC(g, bs[i], 11.5f * k, i == 0 || i == 2 ? FontStyle.Bold : FontStyle.Regular,
                    Color.FromArgb(al, hot ? 255 : 70, hot ? 255 : 78, hot ? 255 : 92), br);
                x += w + 8 * k;
            }
        }

        // ---- 托盘右键菜单 ----
        static readonly string[] TrayItems = { "导入图片…", "新手引导", "显示/隐藏轮盘", "关掉所有贴图", "取字：识别剪贴板里的图", "撤销上一次删除", "管理 Wheel…", "设置…", "打开项目主页", "-", "退出" };
        static void PaintTrayMenu(Graphics g, RectangleF r, float k, int hover)
        {
            Shadow(g, r, 6 * k, 170, 4 * k);
            FillR(g, r, 6 * k, Color.FromArgb(246, 32, 34, 40));
            StrokeR(g, r, 6 * k, Color.FromArgb(70, 255, 255, 255), 1f);
            float y = r.Y + 6 * k;
            for (int i = 0; i < TrayItems.Length; i++)
            {
                if (TrayItems[i] == "-")
                {
                    using (Pen p = new Pen(Color.FromArgb(70, 255, 255, 255), 1f)) g.DrawLine(p, r.X + 8 * k, y + 5 * k, r.Right - 8 * k, y + 5 * k);
                    y += 11 * k;
                    continue;
                }
                RectangleF ir = new RectangleF(r.X + 4 * k, y, r.Width - 8 * k, 26 * k);
                if (i == hover) FillR(g, ir, 4 * k, Color.FromArgb(210, 70, 120, 220));
                Txt(g, TrayItems[i], 12f * k, i == hover ? FontStyle.Bold : FontStyle.Regular, Color.FromArgb(240, 244, 250), ir.X + 10 * k, ir.Y + 3 * k);
                y += 26 * k;
            }
        }

        // ---- 贴图（PinForm 的样子：白边 + 右上角 ✕） ----
        static void PaintPin(Graphics g, RectangleF r, Bitmap img, float k, float glowI, float glowX, float glowY, float glowW)
        {
            Shadow(g, r, 3 * k, 190, 5 * k);
            FillR(g, new RectangleF(r.X - 3 * k, r.Y - 3 * k, r.Width + 6 * k, r.Height + 6 * k), 3 * k, Color.FromArgb(252, 252, 253));
            InterpolationMode im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, r);
            g.InterpolationMode = im;
            StrokeR(g, new RectangleF(r.X - 3 * k, r.Y - 3 * k, r.Width + 6 * k, r.Height + 6 * k), 3 * k, Color.FromArgb(190, 70, 74, 84), 1f);
            float cs = 16 * k;
            RectangleF cr = new RectangleF(r.Right - cs + 3 * k, r.Y - 3 * k, cs, cs);
            FillR(g, cr, 3 * k, Color.FromArgb(228, 58, 60, 68));
            using (Pen p = new Pen(Color.White, 1.8f * k))
            { g.DrawLine(p, cr.X + 5 * k, cr.Y + 5 * k, cr.Right - 5 * k, cr.Bottom - 5 * k); g.DrawLine(p, cr.Right - 5 * k, cr.Y + 5 * k, cr.X + 5 * k, cr.Bottom - 5 * k); }
            if (glowI > 0.02f)
            {
                RectangleF gr = new RectangleF(r.X + glowX, r.Y + glowY, glowW, 22 * k);
                FillR(g, gr, 5 * k, Color.FromArgb((int)(90 * glowI), 255, 226, 120));
                StrokeR(g, gr, 5 * k, Color.FromArgb((int)(230 * glowI), 250, 190, 60), 1.8f * k);
            }
        }

        // ---- 光标 / 鼠标 / 点击涟漪 ----
        static void Cursor(Graphics g, float x, float y, float k)
        {
            PointF[] pts = new PointF[] {
                new PointF(x, y), new PointF(x, y + 19 * k), new PointF(x + 5 * k, y + 14 * k),
                new PointF(x + 8.4f * k, y + 21.3f * k), new PointF(x + 11.8f * k, y + 19.6f * k),
                new PointF(x + 8.4f * k, y + 12.3f * k), new PointF(x + 14.6f * k, y + 11.8f * k) };
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillPolygon(b, pts);
            using (Pen p = new Pen(Color.FromArgb(230, 26, 30, 38), 1.5f * k)) g.DrawPolygon(p, pts);
        }
        static void MouseGlyph(Graphics g, float x, float y, float k, int btn, float press)
        {
            RectangleF m = new RectangleF(x, y, 22 * k, 34 * k);
            FillR(g, m, 11 * k, Color.FromArgb(238, 250, 251, 253));
            StrokeR(g, m, 11 * k, Color.FromArgb(230, 60, 66, 78), 1.4f * k);
            Color hot = Color.FromArgb(90, 150, 240);
            using (Pen p = new Pen(Color.FromArgb(180, 80, 88, 100), 1.2f * k))
            {
                g.DrawLine(p, m.X + m.Width / 2f, m.Y + 2 * k, m.X + m.Width / 2f, m.Y + 14 * k);
                g.DrawLine(p, m.X + 2 * k, m.Y + 14 * k, m.Right - 2 * k, m.Y + 14 * k);
            }
            RectangleF seg = btn == 0 ? new RectangleF(m.X + 2 * k, m.Y + 2 * k, m.Width / 2f - 3 * k, 12 * k)
                          : btn == 2 ? new RectangleF(m.X + m.Width / 2f + 1 * k, m.Y + 2 * k, m.Width / 2f - 3 * k, 12 * k)
                          : new RectangleF(m.X + m.Width / 2f - 3 * k, m.Y + 2 * k, 6 * k, 12 * k);
            FillR(g, seg, 3 * k, Color.FromArgb((int)(120 + 135 * press), hot));
        }
        static void Ripple(Graphics g, float x, float y, float t)
        {
            float r = 14 + 54 * Eu(t);
            int a = (int)(190 * (1 - t));
            if (a <= 3) return;
            using (Pen p = new Pen(Color.FromArgb(a, 255, 255, 255), 2.4f))
                g.DrawEllipse(p, x - r, y - r, r * 2, r * 2);
        }
        static void Keycaps(Graphics g, float cx, float cy, float k, string[] keys, int pressIdx, float a)
        {
            float kw = 46 * k, kh = 42 * k, gp = 10 * k;
            float total = keys.Length * kw + (keys.Length - 1) * (gp + 14 * k);
            float x = cx - total / 2f, y = cy - kh / 2f;
            for (int i = 0; i < keys.Length; i++)
            {
                bool dn = (i == pressIdx);
                RectangleF r = new RectangleF(x, y + (dn ? 3 * k : 0), kw, kh);
                FillR(g, new RectangleF(r.X, r.Y + 4 * k, r.Width, r.Height), 7 * k, Color.FromArgb((int)(160 * a), 0, 0, 0));
                FillR(g, r, 7 * k, Color.FromArgb((int)((dn ? 200 : 245) * a), 246, 248, 252));
                StrokeR(g, r, 7 * k, Color.FromArgb((int)(230 * a), 120, 130, 148), 1.5f * k);
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(230 * a), 40, 48, 62)))
                {
                    StringFormat sf = new StringFormat(); sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(keys[i], PF(13f * k, FontStyle.Bold), b, r, sf); sf.Dispose();
                }
                x += kw;
                if (i < keys.Length - 1)
                {
                    TxtC(g, "+", 16f * k, FontStyle.Bold, Color.FromArgb((int)(220 * a), 235, 240, 250), new RectangleF(x, y, gp + 14 * k, kh));
                    x += gp + 14 * k;
                }
            }
        }
        static void Badge(Graphics g, RectangleF r, float k, string idx, string text, float a, bool done)
        {
            if (a <= 0.01f) return;
            int al = (int)(250 * a);
            Color bg = done ? Color.FromArgb(al, 62, 176, 118) : Color.FromArgb(al, 46, 104, 200);
            FillR(g, r, r.Height / 2f, bg);
            float cd = r.Height - 10 * k;
            FillR(g, new RectangleF(r.X + 5 * k, r.Y + 5 * k, cd, cd), cd / 2f, Color.FromArgb((int)(230 * a), 255, 255, 255));
            TxtC(g, done ? "✓" : idx, (done ? 13f : 12f) * k, FontStyle.Bold, done ? Color.FromArgb(al, 40, 150, 96) : Color.FromArgb(al, 46, 104, 200), new RectangleF(r.X + 5 * k, r.Y + 5 * k, cd, cd));
            Txt(g, text, 13f * k, FontStyle.Bold, Color.FromArgb(al, 255, 255, 255), r.X + cd + 14 * k, r.Y + (r.Height - 17 * k) / 2f);
        }

        // ==================== 字幕带 ====================
        static void Band(Graphics g, string main, string sub, float a)
        {
            if (a <= 0.01f) return;
            float y0 = SH;
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(0, y0 - 26, W, 26 + BANDH),
                Color.FromArgb(0, 8, 10, 14), Color.FromArgb((int)(240 * a), 8, 10, 14), 90f))
                g.FillRectangle(lg, 0, y0 - 26, W, 26);
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(240 * a), 8, 10, 14))) g.FillRectangle(b, 0, y0, W, BANDH);
            if (main == null || main.Length == 0) return;
            float mpx = VERT ? 27f : 23f;
            float spx = VERT ? 17f : 14f;
            RectangleF mr = new RectangleF(W * 0.05f, y0 + 8, W * 0.9f, sub != null && sub.Length > 0 ? BANDH * 0.60f : BANDH - 12);
            WrapC(g, main, mpx, FontStyle.Bold, Color.FromArgb((int)(252 * a), 255, 255, 255), mr);
            if (sub != null && sub.Length > 0)
            {
                RectangleF sr = new RectangleF(W * 0.05f, y0 + BANDH * 0.60f, W * 0.9f, BANDH * 0.36f);
                WrapC(g, sub, spx, FontStyle.Regular, Color.FromArgb((int)(225 * a), 150, 200, 255), sr);
            }
        }

        // ==================== 合成 ====================
        static Bitmap _scr;
        static float _curX, _curY, _curK = 1f, _curA;
        static int _mouseBtn = -1; static float _mousePress; static bool _mouseShow;
        static float _rippleT = 999f;

        static void Scene(Graphics g, Action<Graphics> content, Action<Graphics> front, string cap, string sub, float capA, float cam)
        {
            using (Graphics sg = Graphics.FromImage(_scr))
            {
                sg.SmoothingMode = SmoothingMode.AntiAlias;
                sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                sg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                sg.Clear(Color.FromArgb(255, 18, 22, 30));
                DesktopBase(sg);
                if (content != null) content(sg);
                DrawWheelLayer(sg, 0, RTask.Top, _wfW * WheelScaleNow);
                if (front != null) front(sg);
                if (_curA > 0.02f)
                {
                    if (_mouseShow) MouseGlyph(sg, _curX + 6 * _curK, _curY + 4 * _curK, _curK * 1.1f, _mouseBtn, _mousePress);
                    if (_rippleT < 1f) Ripple(sg, _curX, _curY, _rippleT);
                    Cursor(sg, _curX, _curY, _curK);
                }
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.FromArgb(255, 8, 10, 14));
            if (cam >= 0.999f) g.DrawImage(_scr, new RectangleF(0, 0, W, SH));
            else
            {
                float cw = W * cam, ch = SH * cam;
                float cx = (W - cw) / 2f, cy = (SH - ch) / 2f;
                RectangleF dr = new RectangleF(cx, cy, cw, ch);
                using (GraphicsPath gp = Rnd(dr, 6f))
                {
                    for (int i = 8; i >= 1; i--)
                        using (Pen p = new Pen(Color.FromArgb(10, 0, 0, 0), i * 2f)) g.DrawPath(p, gp);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 96, 132, 200))) g.DrawPath(new Pen(Color.FromArgb(120, 120, 160, 230), 1.4f), gp);
                }
                g.DrawImage(_scr, dr);
            }
            Band(g, cap, sub, capA);
        }

        static float WheelScaleNow = 0.78f;
        static float WSz() { return _wfW * WheelScaleNow; }   // 视频里轮盘窗口的边长（像素）

        // 主屏幕上的轮盘缩放（658 逻辑窗口 -> 视频里 515px）
        static float MainWheelScale() { return (VERT ? 470f : 515f) / _wfW; }

        // ==================== 主流程 ====================
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            string verifyOnly = null;
            bool probe = false;
            foreach (string a in args)
            {
                if (a == "--vertical" || a == "-v") VERT = true;
                else if (a == "--verify") verifyOnly = "";
                else if (a == "--probe") probe = true;
                else if (a.StartsWith("--verify=")) verifyOnly = a.Substring(9);
            }
            InitLayout();
            if (verifyOnly != null)
            {
                Verify(verifyOnly.Length > 0 ? verifyOnly : DefaultOut());
                return;
            }
            if (probe) { Probe(); return; }
            Setup();
            string outp = DefaultOut();
            string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_video_tmp.avi");
            Console.WriteLine("render: " + W + "x" + H + " " + FPS + "fps " + TOTAL + " frames ...");
            List<byte[]> frames = new List<byte[]>();
            Bitmap frame = new Bitmap(W, H, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(frame))
            {
                DateTime t0 = DateTime.Now;
                for (int i = 0; i < TOTAL; i++)
                {
                    RenderFrame(i, g);
                    frames.Add(Encode(frame, JQ));
                    if (i % 60 == 0 || i == TOTAL - 1)
                        Console.WriteLine("  frame " + (i + 1) + "/" + TOTAL + "  " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s");
                }
            }
            frame.Dispose();
            long total = 0; for (int i = 0; i < frames.Count; i++) total += frames[i].Length;
            Console.WriteLine("jpeg q" + JQ + " total " + (total / 1048576.0).ToString("0.0") + " MB");
            int q = JQ;
            while (total > BUDGET && q > 45)
            {
                q -= 10;
                Console.WriteLine("re-encode at q" + q + " ...");
                for (int i = 0; i < frames.Count; i++)
                {
                    using (Bitmap b = Decode(frames[i])) frames[i] = Encode(b, q);
                }
                total = 0; for (int i = 0; i < frames.Count; i++) total += frames[i].Length;
                Console.WriteLine("  -> " + (total / 1048576.0).ToString("0.0") + " MB");
            }
            Directory.CreateDirectory("docs");
            AviWriter.WriteMjpegAvi(tmp, frames, W, H, FPS);
            if (File.Exists(outp)) File.Delete(outp);
            File.Move(tmp, outp);
            Console.WriteLine("wrote " + Path.GetFullPath(outp) + "  " + (new FileInfo(outp).Length / 1048576.0).ToString("0.0") + " MB  (jpeg q" + q + ")");
            Verify(outp);
        }

        static string DefaultOut()
        {
            return VERT ? Path.Combine("docs", "demo-vertical.avi") : Path.Combine("docs", "demo.avi");
        }

        // 抽样渲染若干帧拼成一张联系表（只看画面对不对，不做编码）
        static void Probe()
        {
            Setup();
            int[] idx = { 12, 45, 62, 100, 168, 204, 222, 252, 280, 306, 340, 386, 414, 452, 480, 512, 548, 585 };
            string dir = Path.Combine(Path.GetTempPath(), "snapwheel-video-probe");
            Directory.CreateDirectory(dir);
            Console.WriteLine("wheel form size: " + _wfW + "x" + _wfH + "  wheelScale=" + MainWheelScale().ToString("0.###"));
            {
                Bitmap f1 = new Bitmap(W, H, PixelFormat.Format24bppRgb);
                using (Graphics fg = Graphics.FromImage(f1))
                {
                    int[] full = { 252, 340, 430 };
                    for (int i = 0; i < full.Length; i++)
                    {
                        try { RenderFrame(full[i], fg); }
                        catch (Exception rex)
                        {
                            Exception e2 = rex;
                            if (e2 is TargetInvocationException && e2.InnerException != null) e2 = e2.InnerException;
                            Console.WriteLine("    RENDER FAILED #" + full[i] + ": " + e2.GetType().FullName + " hr=" + e2.HResult);
                            Console.WriteLine("      stack: " + e2.StackTrace);
                        }
                        f1.Save(Path.Combine(dir, "full-" + full[i] + ".png"), ImageFormat.Png);
                        Console.WriteLine("  full frame #" + full[i] + " -> " + Path.Combine(dir, "full-" + full[i] + ".png"));
                    }
                }
                f1.Dispose();
            }
            int cols = VERT ? 4 : 3, sc = VERT ? 2 : 2;
            int cw = W / sc, ch = H / sc, lab = 22;
            int rows = (idx.Length + cols - 1) / cols;
            using (Bitmap sheet = new Bitmap(cols * cw, rows * (ch + lab), PixelFormat.Format24bppRgb))
            {
                using (Graphics sg = Graphics.FromImage(sheet))
                {
                    sg.Clear(Color.FromArgb(40, 42, 48));
                    sg.SmoothingMode = SmoothingMode.AntiAlias;
                    Bitmap frame = new Bitmap(W, H, PixelFormat.Format24bppRgb);
                    using (Graphics fg = Graphics.FromImage(frame))
                    {
                        for (int i = 0; i < idx.Length; i++)
                        {
                            RenderFrame(idx[i], fg);
                            int cx = (i % cols) * cw, cy = (i / cols) * (ch + lab);
                            sg.DrawImage(frame, new Rectangle(cx, cy + lab, cw, ch));
                            sg.DrawString("#" + idx[i] + "  " + (idx[i] / (double)FPS).ToString("0.0") + "s",
                                PF(15, FontStyle.Bold), Brushes.White, cx + 4, cy + 2);
                        }
                    }
                    frame.Dispose();
                }
                string f2 = Path.Combine(dir, VERT ? "sheet-vertical.png" : "sheet.png");
                sheet.Save(f2, ImageFormat.Png);
                Console.WriteLine("probe sheet -> " + f2);
            }
        }

        static void Setup()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_video");
            Directory.CreateDirectory(tmp);
            Settings.OverridePath = Path.Combine(tmp, "settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(tmp, "wheels.ini");
            Err.OverridePath = Path.Combine(tmp, "error.log");

            _set = new Settings();
            _set.SaveToDisk = false;
            _set.UiScale = 100;              // 固定 1.0，不跟真实 DPI 走
            _set.IntroSeen = true;
            _set.NubHintDone = true;
            _set.UndoHintDone = true;
            _set.AnnotHintDone = true;
            _set.PinHintDone = true;
            _mgr = new WheelManager(_set);
            _st = _mgr.ActiveStore;
            _st.SaveToDisk = false;
            _wf = new WheelForm(_mgr, _set);
            _wfW = _wf.Width; _wfH = _wf.Height;

            _shot = MakeShotA();
            _shot2 = MakeShotB();
            _shot3 = MakeShotC();

            _dlFiles = new string[26];
            for (int i = 0; i < 26; i++) _dlFiles[i] = "Screenshot(" + (26 + i) + ").png";

            _scr = new Bitmap(W, SH, PixelFormat.Format32bppPArgb);
        }

        static byte[] Encode(Bitmap b, int q)
        {
            ImageCodecInfo jpg = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) { jpg = c; break; }
            if (jpg == null) throw new Exception("no jpeg encoder");
            EncoderParameters ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)q);
            using (MemoryStream ms = new MemoryStream())
            {
                b.Save(ms, jpg, ep);
                ep.Dispose();
                return ms.ToArray();
            }
        }
        static Bitmap Decode(byte[] jpg)
        {
            using (MemoryStream ms = new MemoryStream(jpg))
            using (Image im = Image.FromStream(ms))
                return new Bitmap(im);
        }

        // ==================== 逐帧渲染 ====================
        static void ResetWheelState()
        {
            _wDropActive = _wDropExternal = false; _wDropCount = 0;
            _wDragItem = null; _wDragProg = 0f;
            _wDelItem = null; _wDelProg = 0f;
            _wHover = -1; _wHoverScale = 1f;
            _wIntro = false; _wIntroT = 1f;
            WheelScaleNow = MainWheelScale();
            _curK = VERT ? 1.15f : 1f;
            _curA = 1f; _mouseShow = false; _mouseBtn = -1; _mousePress = 0f;
            _curX = RScreen.Width / 2f; _curY = RScreen.Height / 2f;      // 兜底，免得第一帧光标卡在角落
            _rippleT = 999f;
        }

        static void RenderFrame(int fi, Graphics g)
        {
            double t = fi / (double)FPS;
            ResetWheelState();
            if (t < 3.0) S1(g, t);
            else if (t < 10.0) S2(g, t);
            else if (t < 13.0) S3(g, t);
            else if (t < 14.0) S3b(g, t);
            else if (t < 19.0) S4(g, t);
            else if (t < 24.0) S5(g, t);
            else if (t < 29.0) S6(g, t);
            else if (t < 33.0) S7(g, t);
            else if (t < 38.0) S8(g, t);
            else S9(g, t);
        }

        // 消息与状态（跨场景保持：图一直在环上）
        static List<Msg> ChatMsgs(double t)
        {
            List<Msg> L = new List<Msg>();
            L.Add(new Msg { Text = "周报我这边先合一下", Mine = false, T = (float)Seg(t, 0.7, 1.1) });
            L.Add(new Msg { Text = "把刚才那张图发我一下", Mine = false, T = (float)Seg(t, 1.0, 1.4), Key = true });
            if (t < 3.0) L.Add(new Msg { Text = "好，我找找…", Mine = true, T = (float)Seg(t, 2.2, 2.8) });
            if (t >= 18.2)
            {
                L.Add(new Msg { Img = _shot, Mine = true, T = (float)Seg(t, 18.2, 18.9) });
                L.Add(new Msg { Text = "找到了，这张", Mine = true, T = (float)Seg(t, 18.9, 19.3) });
                L.Add(new Msg { Text = "收到，谢谢", Mine = false, T = (float)Seg(t, 19.4, 19.9) });
            }
            return L;
        }

        // ---------- 场景 1：周五 17:30，群里要图 ----------
        static void S1(Graphics g, double t)
        {
            bool chatFront = t > 1.5;
            float flash = (t > 0.35 && t < 1.8) ? (0.5f + 0.5f * (float)Math.Sin((t - 0.35) * 9.0)) : 0f;
            string typing = t > 2.1 ? Part("好，我找找…", (float)Seg(t, 2.1, 2.8)) : null;
            // 光标位置先算好（画的时候要用，不能等画完再赋值）
            PointF ca = new PointF(RDoc.X + RDoc.Width * 0.45f, RDoc.Y + RDoc.Height * 0.55f);
            PointF cb = new PointF(RChat.X + RChat.Width * 0.42f, RChat.Bottom - 34 * KUI);
            PointF cc = Pth(new PointF(W - 40, SH - 60), ca, (float)Seg(t, 0.15, 1.1));
            if (t > 1.5) cc = Pth(ca, cb, (float)Seg(t, 1.7, 2.4));
            _curX = cc.X; _curY = cc.Y;
            Scene(g,
                delegate(Graphics gg)
                {
                    PaintDoc(gg, RDoc, KUI, "季度汇报.docx", !chatFront, new string[] {
                        "· 交付时间：9 月 12 日 18:00", "· 负责同学：小何", "· 本周进展：接口联调完成", "· 下周：验收 + 复盘", "", "（做到一半，被群里叫住了）" }, null, null);
                    PaintChat(gg, RChat, KUI, ChatMsgs(t), chatFront, typing);
                    Taskbar(gg, "17:30", 2, flash);
                },
                null,
                "周五 17:30 —— 把刚才那张图发我一下", null, 1f, 1f);
        }

        // ---------- 场景 2：左右分屏对比 ----------
        static void S2(Graphics g, double t)
        {
            double u = t - 3.0;                       // 0..7
            float inP = (float)Seg(u, 0.0, 0.45);
            RectangleF L, R;
            if (VERT) { L = new RectangleF(8, 6, W - 16, (SH - 22) / 2f); R = new RectangleF(8, 6 + (SH - 22) / 2f + 10, W - 16, (SH - 22) / 2f); }
            else { L = new RectangleF(6, 6, (W - 18) / 2f, SH - 12); R = new RectangleF(6 + (W - 18) / 2f + 6, 6, (W - 18) / 2f, SH - 12); }
            float kL = Math.Min(L.Width / 470f, L.Height / 438f);
            float kR = Math.Min(R.Width / 470f, R.Height / 438f);
            float dx = (1 - Eu(inP)) * 60f;
            _curA = 0f;                               // 分屏里不画光标（两边各自的演示里已经画了）

            // 7 步，每步 0.93 秒
            double[] stepT = new double[7];
            for (int i = 0; i < 7; i++) stepT[i] = 0.45 + i * 0.933;
            int step = 0;
            for (int i = 6; i >= 0; i--) if (u >= stepT[i]) { step = i; break; }
            float sp = (float)Seg(u, stepT[step], stepT[step] + 0.933);

            Scene(g,
                delegate(Graphics gg)
                {
                    RectangleF Lp = new RectangleF(L.X - dx, L.Y, L.Width, L.Height);
                    RectangleF Rp = new RectangleF(R.X + dx, R.Y, R.Width, R.Height);
                    g.ResetClip();
                    g.SetClip(new RectangleF(0, 0, W, SH));
                    LeftPanel(gg, Lp, kL, step, sp, u);
                    RightPanel(gg, Rp, kR, u);
                    g.ResetClip();
                },
                null,
                "主流截图：7 步 / 12 秒", "还得先想\"存哪儿\"", (float)Seg(u, 0.1, 0.5), 1f);
        }

        static void PanelFrame(Graphics g, RectangleF P, string title, Color col, string right, float k)
        {
            FillR(g, P, 8 * k, Color.FromArgb(16, 20, 28));
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(P.X, P.Y, P.Width, P.Height),
                Color.FromArgb(52, 74, 112), Color.FromArgb(20, 26, 40), 62f))
                g.FillRectangle(lg, P.X, P.Y, P.Width, P.Height);
            float hh = 30 * k;
            FillR(g, new RectangleF(P.X, P.Y, P.Width, hh), 8 * k, Color.FromArgb(238, 24, 28, 36));
            using (SolidBrush b = new SolidBrush(col)) g.FillRectangle(b, P.X + 12 * k, P.Y + hh / 2f - 6 * k, 12 * k, 12 * k);
            Txt(g, title, 14f * k, FontStyle.Bold, Color.White, P.X + 32 * k, P.Y + 6 * k);
            if (right != null)
            {
                float w = TW(g, right, 12f * k, FontStyle.Bold) + 20 * k;
                RectangleF rr = new RectangleF(P.Right - w - 10 * k, P.Y + 5 * k, w, hh - 10 * k);
                FillR(g, rr, rr.Height / 2f, Color.FromArgb(70, col));
                TxtC(g, right, 12f * k, FontStyle.Bold, Color.White, rr);
            }
            StrokeR(g, P, 8 * k, Color.FromArgb(70, 255, 255, 255), 1f);
        }

        // 左：主流方式的 7 步
        static void LeftPanel(Graphics g, RectangleF P, float k, int step, float sp, double u)
        {
            PanelFrame(g, P, "主流截图", Color.FromArgb(226, 96, 110), "第 " + (step + 1) + "/7 步", k);
            float hh = 30 * k;
            RectangleF body = new RectangleF(P.X + 8 * k, P.Y + hh + 6 * k, P.Width - 16 * k, P.Height - hh - 40 * k);
            g.SetClip(body);
            float bk = k * 0.72f;
            // 迷你桌面：聊天窗口 + 文档窗口
            RectangleF mc = new RectangleF(body.X + body.Width * 0.55f, body.Y + body.Height * 0.30f, body.Width * 0.42f, body.Height * 0.56f);
            RectangleF md = new RectangleF(body.X + body.Width * 0.04f, body.Y + body.Height * 0.06f, body.Width * 0.45f, body.Height * 0.5f);
            PaintDoc(g, md, bk, "季度汇报.docx", false, new string[] { "· 交付时间：9 月 12 日 18:00", "· 负责同学：小何" }, null, null);
            List<Msg> mm = new List<Msg>();
            mm.Add(new Msg { Text = "把刚才那张图发我一下", Mine = false, T = 1f, Key = true });
            if (step >= 6) mm.Add(new Msg { Img = _shot, Mine = true, T = Eu(sp * 2.2f) });
            PaintChat(g, mc, bk, mm, true, null);
            string[] names = { "按 Win+Shift+S", "框选要截的区域", "右下角弹通知", "点通知打开截图工具", "点「保存」", "选文件夹（存哪儿？）", "开资源管理器拖进聊天框" };
            switch (step)
            {
                case 0:
                    Keycaps(g, body.X + body.Width / 2f, body.Y + body.Height / 2f, bk * 1.15f,
                        new string[] { "Win", "Shift", "S" }, (int)(sp * 3f) % 3, 1f);
                    break;
                case 1:
                    {
                        float pr = Eu((float)Seg(sp, 0.15, 0.85));
                        RectangleF selr = new RectangleF(body.X + body.Width * 0.14f, body.Y + body.Height * 0.14f,
                            body.Width * 0.62f * pr, body.Height * 0.56f * pr);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                        {
                            g.FillRectangle(b, body.X, body.Y, body.Width, selr.Top - body.Y);
                            g.FillRectangle(b, body.X, selr.Bottom, body.Width, body.Bottom - selr.Bottom);
                            g.FillRectangle(b, body.X, selr.Top, selr.Left - body.X, selr.Height);
                            g.FillRectangle(b, selr.Right, selr.Top, body.Right - selr.Right, selr.Height);
                        }
                        using (Pen p = new Pen(Color.FromArgb(0, 174, 255), 2f)) g.DrawRectangle(p, selr.X, selr.Y, selr.Width, selr.Height);
                        Cursor(g, selr.Right, selr.Bottom, bk);
                    }
                    break;
                case 2:
                    PaintNotif(g, new RectangleF(body.Right - 214 * bk, body.Bottom - 118 * bk, 206 * bk, 110 * bk), bk,
                        "截图工具", "截图已复制到剪贴板", "需要先保存到文件", _shot, Eu(sp * 2f));
                    break;
                case 3:
                    PaintSnipTool(g, new RectangleF(body.X + body.Width * 0.10f, body.Y + body.Height * 0.08f, body.Width * 0.66f, body.Height * 0.78f), bk, _shot, 0f);
                    Cursor(g, body.X + body.Width * 0.60f, body.Y + body.Height * 0.78f, bk);
                    break;
                case 4:
                    PaintSaveDlg(g, new RectangleF(body.X + body.Width * 0.06f, body.Y + body.Height * 0.06f, body.Width * 0.78f, body.Height * 0.84f), bk,
                        "Screenshot(37).png", -1, sp > 0.75f ? 1f : 0f);
                    Cursor(g, body.X + body.Width * 0.70f, body.Y + body.Height * 0.82f, bk);
                    if (sp > 0.75f) Ripple(g, body.X + body.Width * 0.70f, body.Y + body.Height * 0.82f, (float)Seg(sp, 0.75, 1.0));
                    break;
                case 5:
                    PaintSaveDlg(g, new RectangleF(body.X + body.Width * 0.06f, body.Y + body.Height * 0.06f, body.Width * 0.78f, body.Height * 0.84f), bk,
                        "Screenshot(37).png", sp > 0.3f ? 2 : -1, 0f);
                    Cursor(g, body.X + body.Width * 0.16f, body.Y + body.Height * 0.30f, bk);
                    TxtC(g, "到底存哪个文件夹？", 13f * bk, FontStyle.Bold, Color.FromArgb(255, 214, 120),
                        new RectangleF(body.X, body.Y - 2 * bk, body.Width, 20 * bk));
                    break;
                case 6:
                    {
                        RectangleF ex = new RectangleF(body.X + body.Width * 0.02f, body.Y + body.Height * 0.02f, body.Width * 0.6f, body.Height * 0.8f);
                        g.SetClip(body);
                        FillR(g, ex, 6 * bk, Color.FromArgb(250, 250, 252));
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 196, 84))) g.FillRectangle(b, ex.X, ex.Y, ex.Width, 18 * bk);
                        Txt(g, "下载", 10f * bk, FontStyle.Bold, Color.FromArgb(40, 46, 58), ex.X + 6 * bk, ex.Y + 3 * bk);
                        for (int i = 0; i < 5; i++)
                        {
                            float y = ex.Y + 26 * bk + i * 15 * bk;
                            if (y > ex.Bottom - 8 * bk) break;
                            FillR(g, new RectangleF(ex.X + 6 * bk, y, 9 * bk, 11 * bk), 2 * bk, Color.FromArgb(226, 232, 240));
                            Txt(g, "Screenshot(" + (36 - i) + ").png", 8.5f * bk, FontStyle.Regular, Color.FromArgb(80, 88, 100), ex.X + 20 * bk, y - 1 * bk);
                        }
                        float pr = Eu(sp);
                        PointF a = new PointF(ex.X + ex.Width * 0.5f, ex.Y + ex.Height * 0.5f);
                        PointF b2 = new PointF(mc.X + mc.Width * 0.5f, mc.Bottom - 18 * bk);
                        PointF c = Pth(a, b2, pr);
                        float iw = 66 * bk, ih = iw * _shot.Height / (float)_shot.Width;
                        FillR(g, new RectangleF(c.X - iw / 2f + 3, c.Y - ih / 2f + 4, iw, ih), 3 * bk, Color.FromArgb(120, 0, 0, 0));
                        g.DrawImage(_shot, new RectangleF(c.X - iw / 2f, c.Y - ih / 2f, iw, ih));
                        Cursor(g, c.X, c.Y, bk);
                    }
                    break;
            }
            g.ResetClip();
            // 顶部步骤名 + 底部进度
            RectangleF nr = new RectangleF(P.X + 10 * k, P.Y + hh + 8 * k, P.Width - 20 * k, 24 * k);
            FillR(g, nr, 5 * k, Color.FromArgb(170, 226, 96, 110));
            Txt(g, (step + 1) + ". " + names[step], 12.5f * k, FontStyle.Bold, Color.White, nr.X + 8 * k, nr.Y + 3 * k);
            float pb = P.Bottom - 22 * k;
            RectangleF track = new RectangleF(P.X + 12 * k, pb, P.Width - 24 * k, 8 * k);
            FillR(g, track, 4 * k, Color.FromArgb(90, 255, 255, 255));
            float fill = (float)((step + sp) / 7.0);
            FillR(g, new RectangleF(track.X, track.Y, Math.Max(4 * k, track.Width * fill), track.Height), 4 * k, Color.FromArgb(226, 96, 110));
            Txt(g, "已用 " + (fill * 12.0).ToString("0.0") + " 秒", 11.5f * k, FontStyle.Bold, Color.FromArgb(255, 200, 200), track.X, track.Y - 18 * k);
            Txt(g, "7 步 · 12 秒", 11.5f * k, FontStyle.Bold, Color.FromArgb(255, 255, 255), track.Right - TW(g, "7 步 · 12 秒", 11.5f * k, FontStyle.Bold), track.Y - 18 * k);
        }

        // 右：SnapWheel 的 2 步（3 秒内做完，然后一直等着左边）
        static void RightPanel(Graphics g, RectangleF P, float k, double u)
        {
            bool done = u > 2.95;
            PanelFrame(g, P, "SnapWheel", Color.FromArgb(96, 170, 255), done ? "✓ 3 秒搞定" : "第 " + (u < 1.6 ? 1 : 2) + "/2 步", k);
            float hh = 30 * k;
            RectangleF body = new RectangleF(P.X + 8 * k, P.Y + hh + 6 * k, P.Width - 16 * k, P.Height - hh - 40 * k);
            g.SetClip(body);
            float bk = k * 0.72f;
            RectangleF mc = new RectangleF(body.X + body.Width * 0.52f, body.Y + body.Height * 0.34f, body.Width * 0.46f, body.Height * 0.52f);
            List<Msg> mm = new List<Msg>();
            mm.Add(new Msg { Text = "把刚才那张图发我一下", Mine = false, T = 1f, Key = true });
            if (u > 2.75) mm.Add(new Msg { Img = _shot, Mine = true, T = Eu((float)Seg(u, 2.75, 3.2)) });
            PaintChat(g, mc, bk, mm, true, null);
            // 迷你轮盘
            int items = u < 1.5 ? 0 : 1;
            if (_st.Items.Count != items)
            {
                if (items > _st.Items.Count) { _st.Add(_shot); ClearEnter(); }
                else if (_st.Items.Count > 0) _st.Items.RemoveAt(_st.Items.Count - 1);
            }
            float ws = Math.Min(body.Width, body.Height) * 0.78f;
            float ax = body.X + 4 * k, ay = body.Bottom - 4 * k;
            // 框选
            if (u > 0.35 && u < 1.45)
            {
                float pr = Eu((float)Seg(u, 0.35, 1.25));
                RectangleF selr = new RectangleF(body.X + body.Width * 0.10f, body.Y + body.Height * 0.16f, body.Width * 0.72f * pr, body.Height * 0.62f * pr);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                {
                    g.FillRectangle(b, body.X, body.Y, body.Width, selr.Top - body.Y);
                    g.FillRectangle(b, body.X, selr.Bottom, body.Width, body.Bottom - selr.Bottom);
                    g.FillRectangle(b, body.X, selr.Top, selr.Left - body.X, selr.Height);
                    g.FillRectangle(b, selr.Right, selr.Top, body.Right - selr.Right, selr.Height);
                }
                using (Pen p = new Pen(Color.FromArgb(0, 174, 255), 2f)) g.DrawRectangle(p, selr.X, selr.Y, selr.Width, selr.Height);
            }
            if (u < 0.35) Keycaps(g, body.X + body.Width / 2f, body.Y + body.Height * 0.42f, bk * 1.1f, new string[] { "Ctrl", "Shift", "S" }, -1, (float)Seg(u, 0.05, 0.3));
            DrawWheelLayer(g, ax, ay, ws);
            PointF c0 = CardPos(ax, ay, ws, 0);
            // 图飞进环 / 拖进聊天
            if (u >= 1.25 && u < 1.45)
            {
                float pr = Eu((float)Seg(u, 1.25, 1.45));
                float iw = Lerp(body.Width * 0.72f, 70 * bk, pr), ih = iw * _shot.Height / (float)_shot.Width;
                PointF a = new PointF(body.X + body.Width * 0.46f, body.Y + body.Height * 0.46f);
                PointF c = Pth(a, c0, pr);
                g.DrawImage(_shot, new RectangleF(c.X - iw / 2f, c.Y - ih / 2f, iw, ih));
            }
            if (u >= 1.75 && u < 2.75)
            {
                float pr = Eu((float)Seg(u, 1.75, 2.6));
                PointF a = c0, b2 = new PointF(mc.X + mc.Width * 0.5f, mc.Bottom - 16 * bk);
                PointF c = Pth(a, b2, pr);
                float iw = Lerp(72 * bk, 92 * bk, pr), ih = iw * _shot.Height / (float)_shot.Width;
                FillR(g, new RectangleF(c.X - iw / 2f + 3, c.Y - ih / 2f + 4, iw, ih), 3 * bk, Color.FromArgb(140, 0, 0, 0));
                g.DrawImage(_shot, new RectangleF(c.X - iw / 2f, c.Y - ih / 2f, iw, ih));
                using (Pen p = new Pen(Color.FromArgb(120, 170, 255), 1.6f * bk)) g.DrawRectangle(p, c.X - iw / 2f, c.Y - ih / 2f, iw, ih);
                Cursor(g, c.X + iw / 2f - 6 * bk, c.Y + ih / 2f - 4 * bk, bk);
            }
            g.ResetClip();
            RectangleF nr = new RectangleF(P.X + 10 * k, P.Y + hh + 8 * k, P.Width - 20 * k, 24 * k);
            FillR(g, nr, 5 * k, Color.FromArgb(170, 46, 120, 210));
            Txt(g, done ? "✓ 已经发出去了" : (u < 1.6 ? "1. Ctrl+Shift+S 框选" : "2. 拖进聊天框"), 12.5f * k, FontStyle.Bold, Color.White, nr.X + 8 * k, nr.Y + 3 * k);
            float pb = P.Bottom - 22 * k;
            RectangleF track = new RectangleF(P.X + 12 * k, pb, P.Width - 24 * k, 8 * k);
            FillR(g, track, 4 * k, Color.FromArgb(90, 255, 255, 255));
            float fill = (float)Math.Min(1.0, u / 3.0);
            FillR(g, new RectangleF(track.X, track.Y, Math.Max(4 * k, track.Width * fill), track.Height), 4 * k, Color.FromArgb(72, 182, 128));
            Txt(g, "2 步 · " + Math.Min(3.0, u).ToString("0.0") + " 秒", 11.5f * k, FontStyle.Bold, done ? Color.FromArgb(150, 245, 190) : Color.White, track.X, track.Y - 18 * k);
            if (done)
                Txt(g, "✓ 已完成", 11.5f * k, FontStyle.Bold, Color.FromArgb(150, 245, 190), track.Right - TW(g, "✓ 已完成", 11.5f * k, FontStyle.Bold), track.Y - 18 * k);
        }

        // ---------- 场景 3：下载文件夹里全是同名文件 ----------
        static void S3(Graphics g, double t)
        {
            double u = t - 10.0;
            float q = (float)Seg(u, 1.5, 2.3);
            string query = u > 1.5 ? Part("刚才那张图", q) : "";
            float noRes = (float)Seg(u, 2.35, 2.6);
            float scroll = u < 1.0 ? Lerp(0, 6, Eu((float)Seg(u, 0.35, 0.95))) : Lerp(6, 9, Eu((float)Seg(u, 1.0, 1.5)));
            if (noRes > 0.5f) scroll = 0;
            int hover = u < 1.5 ? 4 : -1;
            RectangleF ex = VERT ? new RectangleF(14, 14, W - 28, SH - 120) : new RectangleF(40, 22, W - 120, SH - 80);
            Scene(g,
                delegate(Graphics gg)
                {
                    PaintExplorer(gg, ex, KUI, scroll, hover, query, noRes);
                    Taskbar(gg, "17:31", -1, 0f);
                },
                null,
                "最尴尬的是 —— 你没保存，那张图根本不在", null, (float)Seg(u, 0.1, 0.45), 1f);
            PointF a = new PointF(ex.X + ex.Width * 0.35f, ex.Y + ex.Height * 0.45f);
            PointF b = new PointF(ex.Right - 90 * KUI, ex.Y + 30 * KUI);
            PointF c2 = new PointF(ex.X + ex.Width * 0.45f, ex.Bottom - 30 * KUI);
            float p = (float)Seg(u, 0.3, 0.9);
            PointF c = p < 0.5f ? Pth(a, b, p * 2f) : Pth(b, c2, (p - 0.5f) * 2f);
            _curX = c.X; _curY = c.Y;
        }

        // ---------- 场景 3b：过渡，Ctrl+Shift+S ----------
        static void S3b(Graphics g, double t)
        {
            double u = t - 13.0;
            float dim = 0.25f + 0.3f * (float)u;
            float kc = 0.8f + 0.35f * Eu((float)Seg(u, 0.05, 0.5));
            Scene(g,
                delegate(Graphics gg)
                {
                    PaintDoc(gg, RDoc, KUI, "季度汇报.docx", true, new string[] { "· 交付时间：9 月 12 日 18:00", "· 负责同学：小何" }, null, null);
                    PaintChat(gg, RChat, KUI, ChatMsgs(t), false, null);
                    Taskbar(gg, "17:31", -1, 0f);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(140 * dim), 0, 0, 0))) g.FillRectangle(b, 0, 0, W, SH);
                },
                delegate(Graphics gg)
                {
                    Keycaps(gg, W / 2f, SH * 0.46f, kc * 1.5f, new string[] { "Ctrl", "Shift", "S" }, -1, 1f);
                    TxtC(gg, "换个做法 —— 截图后不落地，直接进环", 16f * KUI, FontStyle.Bold, Color.FromArgb(230, 240, 250),
                        new RectangleF(0, SH * 0.62f, W, 30 * KUI));
                },
                null, null, (float)Seg(t, 13.0, 13.35) > 0.5f ? 0f : 1f - (float)Seg(t, 13.0, 13.4), 1f);
        }

        // ---------- 场景 4：SnapWheel 2 步 ----------
        static void S4(Graphics g, double t)
        {
            double u = t - 14.0;                       // 0..5
            RectangleF sel = VERT ? new RectangleF(18, 44, W - 36, 470) : new RectangleF(246, 74, 662, 300);
            float dim = 1f - (float)Seg(u, 2.5, 3.1);
            // 框选阶段
            bool capturing = u < 2.0;
            bool itemIn = u >= 1.45;
            if (itemIn && _st.Items.Count == 0) { _st.Add(_shot); }
            if (itemIn) SetEnter(_st.Items[0], (float)Seg(u, 1.45, 1.9));
            // 拖出
            bool dragging = u >= 2.6 && u < 3.6;
            if (dragging) { _wDragItem = _st.Items[0]; _wDragProg = Lerp(0f, 0.75f, (float)Seg(u, 2.6, 3.5)); }
            bool overChat = u >= 3.4 && u < 3.7;
            if (overChat) { _wDropActive = true; _wDropCount = 1; }
            if (u >= 2.4 && u < 3.6) { _wHover = 0; _wHoverScale = 1f + 0.13f * Eu((float)Seg(u, 2.4, 2.7)); }
            if (u >= 1.5 && u < 2.4) _wToast = "已加入 1 张图片";
            _wToastAge = (float)Seg(u, 1.5, 1.7) * 0.8f + (float)Seg(u, 2.1, 2.3) * 1.5f;
            if (u >= 1.5 && u < 2.4) _wToast = "已加入 1 张图片"; else _wToast = "";

            float cam = 1f;
            Scene(g,
                delegate(Graphics gg)
                {
                    PaintDoc(gg, RDoc, KUI, "季度汇报.docx", true, new string[] { "· 交付时间：9 月 12 日 18:00", "· 负责同学：小何", "· 本周进展：接口联调完成" }, null, null);
                    PaintChat(gg, RChat, KUI, ChatMsgs(t), u > 2.8, null);
                    Taskbar(gg, "17:32", -1, 0f);
                    if (capturing)
                    {
                        float pr = Eu((float)Seg(u, 0.55, 1.35));
                        RectangleF r = new RectangleF(sel.X, sel.Y, sel.Width * pr, sel.Height * pr);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(125 * dim), 0, 0, 0)))
                        {
                            gg.FillRectangle(b, 0, 0, W, r.Top);
                            gg.FillRectangle(b, 0, r.Bottom, W, SH - r.Bottom);
                            gg.FillRectangle(b, 0, r.Top, r.Left, r.Height);
                            gg.FillRectangle(b, r.Right, r.Top, W - r.Right, r.Height);
                        }
                        using (Pen p = new Pen(Color.FromArgb(0, 174, 255), 2f)) gg.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                        Txt(gg, (int)(r.Width) + " × " + (int)(r.Height), 13f * KUI, FontStyle.Bold, Color.FromArgb(0, 174, 255), r.X, r.Y - 22 * KUI);
                    }
                    else if (dim > 0.01f)
                        using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(125 * dim), 0, 0, 0))) gg.FillRectangle(b, 0, 0, W, SH);
                },
                delegate(Graphics gg)
                {
                    if (u < 0.55)
                    {
                        Keycaps(gg, W / 2f, SH * 0.45f, 1.35f * KUI, new string[] { "Ctrl", "Shift", "S" }, (int)(u * 6f) % 3, 1f);
                    }
                    // 拖拽的影子
                    if (dragging)
                    {
                        PointF c0 = CardPos(0, RTask.Top, WSz(), 0);
                        PointF dst = new PointF(RChat.X + RChat.Width * 0.45f, RChat.Bottom - 30 * KUI);
                        float pr = Eu((float)Seg(u, 2.6, 3.55));
                        PointF c = Pth(c0, dst, pr);
                        float iw = 200 * KUI * (VERT ? 0.8f : 1f), ih = iw * _shot.Height / (float)_shot.Width;
                        FillR(gg, new RectangleF(c.X - iw / 2f + 5, c.Y - ih / 2f + 6, iw, ih), 4, Color.FromArgb(150, 0, 0, 0));
                        gg.DrawImage(_shot, new RectangleF(c.X - iw / 2f, c.Y - ih / 2f, iw, ih));
                        using (Pen p = new Pen(Color.FromArgb(130, 170, 255), 2f)) gg.DrawRectangle(p, c.X - iw / 2f, c.Y - ih / 2f, iw, ih);
                        if (overChat)
                        {
                            using (Pen p = new Pen(Color.FromArgb(255, 150, 220, 160), 3f))
                                gg.DrawRectangle(p, RChat.X + 4, RChat.Y + 4, RChat.Width - 8, RChat.Height - 8);
                            TxtC(gg, "松手就发出去了", 16f * KUI, FontStyle.Bold, Color.FromArgb(255, 160, 235, 180),
                                new RectangleF(RChat.X, RChat.Y - 34 * KUI, RChat.Width, 26 * KUI));
                        }
                    }
                    // 步骤徽章
                    float b1 = (float)Seg(u, 0.5, 0.9), b2 = (float)Seg(u, 2.5, 2.9);
                    float bw = VERT ? W - 40 : 300 * KUI, bx = VERT ? 20 : 24 * KUI, by = VERT ? 282 : 16 * KUI;
                    Badge(gg, new RectangleF(bx, by, bw, 34 * KUI), KUI, "①", "Ctrl+Shift+S 框选", b1, u > 1.9);
                    Badge(gg, new RectangleF(bx, by + 44 * KUI, bw, 34 * KUI), KUI, "②", "从环上拖进聊天框", b2, u > 3.7);
                    if (u > 3.8)
                    {
                        RectangleF t2 = new RectangleF(VERT ? 20 : W - 220 * KUI, VERT ? 326 : 16 * KUI, 196 * KUI, 34 * KUI);
                        FillR(gg, t2, 17 * KUI, Color.FromArgb(235, 62, 176, 118));
                        TxtC(gg, "✓ 用时 3 秒", 14f * KUI, FontStyle.Bold, Color.White, t2);
                    }
                },
                "SnapWheel：2 步 / 3 秒", "中间不需要\"保存\"这个动作", (float)Seg(u, 0.15, 0.5), cam);
            // 光标
            PointF p0 = new PointF(sel.X + sel.Width * 1.02f, sel.Y + sel.Height * 1.02f);
            PointF p1 = new PointF(RChat.X + RChat.Width * 0.45f, RChat.Bottom - 30 * KUI);
            PointF p2 = new PointF(RChat.X + RChat.Width * 0.7f, RChat.Bottom - 34 * KUI);
            float cc = (float)Seg(u, 0.55, 1.35);
            if (u < 2.4) { PointF q = Pth(p0, CardPos(0, RTask.Top, WSz(), 0), (float)Seg(u, 1.5, 2.2)); _curX = q.X; _curY = q.Y; }
            else if (u < 3.6) { PointF q = Pth(CardPos(0, RTask.Top, WSz(), 0), p1, (float)Seg(u, 2.6, 3.55)); _curX = q.X + 12; _curY = q.Y + 10; }
            else { PointF q = Pth(p1, p2, (float)Seg(u, 3.6, 4.0)); _curX = q.X; _curY = q.Y; }
            if (overChat) _rippleT = (float)Seg(u, 3.5, 3.9);
        }

        // ---------- 场景 5：中键钉图 ----------
        static void S5(Graphics g, double t)
        {
            double u = t - 19.0;                        // 0..5
            if (_st.Items.Count == 0) _st.Add(_shot);
            SetEnter(_st.Items[0], 1f);
            PointF c0 = CardPos(0, RTask.Top, WSz(), 0);
            _wHover = 0; _wHoverScale = 1f + 0.12f * Eu((float)Seg(u, 0.05, 0.45));
            bool pinFly = u >= 1.0 && u < 1.7;
            float pinP = Eu((float)Seg(u, 1.0, 1.6));
            if (u >= 1.35 && u < 3.4) _wToast = "已贴到屏幕上（双击它或按 Esc 关掉）";
            _wToastAge = (float)Seg(u, 1.35, 1.55) * 0.7f + (float)Seg(u, 2.7, 2.9) * 1.6f;
            if (!(u >= 1.35 && u < 3.4)) _wToast = "";

            RectangleF pinned = RPin;
            float glowI = (float)Seg(u, 2.0, 2.4);
            float row = u < 3.0 ? 0 : 1;
            Scene(g,
                delegate(Graphics gg)
                {
                    PaintChat(gg, RChat, KUI, ChatMsgs(t), false, null);
                    PaintDoc(gg, RDoc, KUI, "季度汇报.docx", true, new string[] { "· 交付时间：", "· 负责同学：小何" }, null, null);
                    // 填表：一边看贴图一边把数字填进去
                    string typed = u < 2.9 ? "" : Part("9 月 12 日 18:00", (float)Seg(u, 2.9, 4.2));
                    if (typed.Length > 0)
                        Txt(gg, typed, 12.5f * KUI, FontStyle.Bold, Color.FromArgb(28, 70, 150), RDoc.X + 18 * KUI + TW(gg, "· 交付时间：", 12.5f * KUI, FontStyle.Regular), RDoc.Y + 26 * KUI + 44 * KUI);
                    if (u > 3.0)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 100, 116)))
                            gg.FillRectangle(b, RDoc.X + 18 * KUI + TW(gg, "· 交付时间：", 12.5f * KUI, FontStyle.Regular) + TW(gg, typed, 12.5f * KUI, FontStyle.Bold) + 1,
                                RDoc.Y + 26 * KUI + 46 * KUI, 1.6f, 15 * KUI);
                    }
                    if (u > 4.2 && u < 4.6)
                    {
                        string s2 = Part("小何", (float)Seg(u, 4.2, 4.5));
                        Txt(gg, s2, 12.5f * KUI, FontStyle.Bold, Color.FromArgb(28, 70, 150), RDoc.X + 18 * KUI + TW(gg, "· 负责同学：", 12.5f * KUI, FontStyle.Regular), RDoc.Y + 26 * KUI + 66 * KUI);
                    }
                    Taskbar(gg, "17:33", -1, 0f);
                },
                delegate(Graphics gg)
                {
                    if (pinFly)
                    {
                        float iw = Lerp(150, pinned.Width, pinP), ih = iw * _shot.Height / (float)_shot.Width;
                        PointF c = Pth(c0, new PointF(pinned.X + pinned.Width / 2f, pinned.Y + pinned.Height / 2f), pinP);
                        gg.DrawImage(_shot, new RectangleF(c.X - iw / 2f, c.Y - ih / 2f, iw, ih));
                    }
                    else if (u >= 1.7)
                    {
                        PaintPin(gg, pinned, _shot, KUI, glowI * (u < 4.5 ? 1f : 0f), 16 * KUI, 34 * KUI + row * 38 * KUI, pinned.Width - 8 * KUI);
                        RectangleF lr = new RectangleF(pinned.X, pinned.Y - 30 * KUI, 190 * KUI, 24 * KUI);
                        FillR(gg, lr, 12 * KUI, Color.FromArgb(220, 28, 32, 40));
                        TxtC(gg, "钉在屏幕上的参考图", 12.5f * KUI, FontStyle.Bold, Color.FromArgb(255, 236, 160), lr);
                    }
                },
                "中键钉图：边看边改，不用来回切窗口", null, (float)Seg(u, 0.1, 0.45), 1f);

            PointF a = new PointF(RChat.X - 40, RChat.Y + 200);
            if (u < 0.6) { PointF q = Pth(a, c0, (float)Seg(u, 0.0, 0.55)); _curX = q.X; _curY = q.Y; }
            else if (u < 1.0) { _curX = c0.X; _curY = c0.Y; _mouseShow = true; _mouseBtn = 1; _mousePress = (float)Seg(u, 0.62, 0.8); }
            else
            {
                float p = (float)Seg(u, 1.9, 4.6);
                _curX = pinned.X + 60 + 120 * Eu(p);
                _curY = pinned.Y + 44 + row * 38 * KUI + 10 * (float)Math.Sin(p * 6);
                _curA = 1f;
            }
            if (u >= 0.8 && u < 1.05) _rippleT = (float)Seg(u, 0.8, 1.05);
        }

        // ---------- 场景 6：取字 + 翻译 ----------
        static RectangleF OcrToolbarRect(RectangleF sel)
        {
            float tw = 14 * 34 * KUI + 40 * KUI;
            RectangleF tr = new RectangleF(sel.X, sel.Bottom + 14 * KUI, tw, 46 * KUI);
            if (tr.Right > W - 10) tr = new RectangleF(W - 10 - tw, tr.Y, tw, tr.Height);
            if (tr.Bottom > SH - 10) tr = new RectangleF(tr.X, sel.Y - 60 * KUI, tw, tr.Height);
            return tr;
        }
        static readonly string OcrEn = "Delivery date: Sep 12, 18:00\nOwner: Xiao He\nStatus: report sent";
        static readonly string OcrZh = "交付时间：9 月 12 日 18:00\n负责人：小何\n状态：报告已发出";
        static void S6(Graphics g, double t)
        {
            double u = t - 24.0;                        // 0..5
            bool cap = u < 2.6;
            RectangleF sel = VERT ? new RectangleF(46, 92, W - 96, 150) : new RectangleF(344, 92, 396, 150);
            RectangleF ocrR = VERT ? new RectangleF(30, 300, W - 60, 300) : new RectangleF(430, 74, 430, 300);
            float dim = cap ? (float)Seg(u, 0.25, 0.7) : 0f;
            float toolA = (float)Seg(u, 0.95, 1.3);
            float boxP = (float)Seg(u, 1.5, 2.2);
            float ocrA = (float)Seg(u, 2.5, 3.0) * (1f - (float)Seg(u, 4.65, 4.95));
            int phase = u < 2.9 ? 0 : (u < 3.35 ? 1 : (u < 3.75 ? 2 : (u < 4.3 ? 3 : 4)));
            // 取到的图：这一段的成品最后会进环
            if (u >= 4.6 && _st.Items.Count == 1) { _st.Add(_shot2); }
            if (u >= 4.6 && _st.Items.Count >= 2) SetEnter(_st.Items[1], (float)Seg(u, 4.6, 5.0));
            if (u >= 4.65) { _wToast = "已加入 1 张图片"; _wToastAge = (float)Seg(u, 4.65, 4.85) * 0.6f; }

            Scene(g,
                delegate(Graphics gg)
                {
                    PaintChat(gg, RChat, KUI, ChatMsgs(t), false, null);
                    PaintTable(gg, RTable, KUI, true, new string[] {
                        "Delivery date:  Sep 12, 18:00", "Owner:  Xiao He", "Status:  report sent", "Note:  check the sheet" }, (float)Seg(u, 3.4, 4.0));
                    Taskbar(gg, "17:35", -1, 0f);
                    if (dim > 0.01f)
                    {
                        float pr = Eu((float)Seg(u, 0.3, 1.0));
                        RectangleF r = new RectangleF(sel.X, sel.Y, sel.Width * pr, sel.Height * pr);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(120 * dim), 0, 0, 0)))
                        {
                            gg.FillRectangle(b, 0, 0, W, r.Top);
                            gg.FillRectangle(b, 0, r.Bottom, W, SH - r.Bottom);
                            gg.FillRectangle(b, 0, r.Top, r.Left, r.Height);
                            gg.FillRectangle(b, r.Right, r.Top, W - r.Right, r.Height);
                        }
                        using (Pen p = new Pen(Color.FromArgb(0, 174, 255), 2f)) gg.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                    }
                },
                delegate(Graphics gg)
                {
                    // 工具条
                    if (toolA > 0.01f)
                    {
                        RectangleF tr = OcrToolbarRect(sel);
                        PaintToolbar(gg, tr, KUI, u >= 1.45 ? 5 : -1, u > 1.15 && u < 1.45 ? 5 : -1);
                        if (u >= 1.4 && u < 1.7)
                        {
                            PointF bp = new PointF(tr.X + 8 * KUI + 5 * (34 + 6) * KUI + 17 * KUI, tr.Y + 23 * KUI);
                            Ripple(gg, bp.X, bp.Y, (float)Seg(u, 1.4, 1.7));
                        }
                    }
                    // 取字的框
                    if (boxP > 0.02f && u < 2.7)
                    {
                        RectangleF br = new RectangleF(sel.X + 16, sel.Y + 8, (sel.Width - 32) * boxP, (sel.Height - 16) * 0.85f);
                        using (Pen p = new Pen(Color.FromArgb(255, 120, 200, 255), 2f))
                        { p.DashStyle = DashStyle.Dash; gg.DrawRectangle(p, br.X, br.Y, br.Width, br.Height); }
                        Txt(gg, "圈住要认的文字", 13f * KUI, FontStyle.Bold, Color.FromArgb(255, 200, 235, 255), br.X, br.Bottom + 6 * KUI);
                    }
                    if (ocrA > 0.01f)
                    {
                        PaintOcr(gg, ocrR, KUI, phase, OcrEn, phase >= 3 ? OcrZh : null, ocrA);
                    }
                },
                "截图里的字，直接变成可复制的文本 + 译文", null, (float)Seg(u, 0.1, 0.45), 1f);

            // 光标
            RectangleF trr = OcrToolbarRect(sel);
            PointF toolBtn = new PointF(trr.X + 8 * KUI + 5 * (34 + 6) * KUI + 17 * KUI, trr.Y + 23 * KUI);
            PointF p1 = new PointF(sel.X + 30, sel.Y + 20);
            PointF p2 = new PointF(sel.X + sel.Width - 30, sel.Y + sel.Height * 0.8f);
            PointF o1 = VERT ? new PointF(96, 574) : new PointF(496, 348);
            PointF o2 = VERT ? new PointF(281, 574) : new PointF(681, 348);
            PointF o3 = VERT ? new PointF(300, 660) : new PointF(660, 210);
            if (u < 0.9) { PointF q = Pth(p2, toolBtn, (float)Seg(u, 0.15, 0.85)); _curX = q.X; _curY = q.Y; }
            else if (u < 1.5) { _curX = toolBtn.X; _curY = toolBtn.Y; }
            else if (u < 2.4) { PointF q = Pth(p1, p2, (float)Seg(u, 1.5, 2.25)); _curX = q.X; _curY = q.Y; }
            else if (u < 3.3) { PointF q = Pth(p2, o1, (float)Seg(u, 2.5, 3.2)); _curX = q.X; _curY = q.Y; }
            else if (u < 4.0) { PointF q = Pth(o1, o2, (float)Seg(u, 3.3, 3.95)); _curX = q.X; _curY = q.Y; }
            else if (u < 4.6) { PointF q = Pth(o2, o3, (float)Seg(u, 4.05, 4.5)); _curX = q.X; _curY = q.Y; }
            else { PointF q = Pth(o3, new PointF(W / 2f, SH * 0.5f), (float)Seg(u, 4.65, 5.0)); _curX = q.X; _curY = q.Y; }
            if (u > 1.4 && u < 1.75) _rippleT = (float)Seg(u, 1.4, 1.75);
            if (u > 3.5 && u < 3.85) _rippleT = (float)Seg(u, 3.5, 3.85);
            if (u > 4.25 && u < 4.6) _rippleT = (float)Seg(u, 4.25, 4.6);
        }

        // ---------- 场景 7：删错了就撤销 ----------
        static void S7(Graphics g, double t)
        {
            double u = t - 29.0;                        // 0..4
            if (_st.Items.Count < 2) { _st.Add(_shot2); ClearEnter(); }
            if (_st.Items.Count < 3 && u >= 0.05) { _st.Add(_shot3); }
            // 新图滑进来
            if (_st.Items.Count >= 3) SetEnter(_st.Items[2], (float)Seg(u, 0.05, 0.45));
            if (u >= 0.06 && u < 1.0) { _wToast = "已加入 1 张图片"; _wToastAge = (float)Seg(u, 0.06, 0.26) * 0.6f; }
            else if (u >= 1.15 && u < 3.0) { _wToast = "已删掉这张 —— 托盘右键「撤销上一次删除」可以找回来"; _wToastAge = (float)Seg(u, 1.15, 1.35) * 0.7f + (float)Seg(u, 2.3, 2.5) * 1.6f; }
            else if (u >= 3.0) { _wToast = "已放回 1 张到「项目」"; _wToastAge = (float)Seg(u, 3.0, 3.2) * 0.6f; }
            else _wToast = "";
            // 删除动画
            bool del = u >= 1.15 && u < 1.55;
            if (_st.Items.Count >= 3)
            {
                if (del) { _wDelItem = _st.Items[2]; _wDelProg = Lerp(0f, 0.9f, (float)Seg(u, 1.15, 1.5)); }
                if (u >= 1.5 && _st.Items.Count >= 3) _st.Items.RemoveAt(2);
            }
            // 撤销：图回到环上
            bool undoT = u >= 3.0;
            if (undoT && _st.Items.Count == 2) { _st.Add(_shot3); }
            if (undoT && _st.Items.Count >= 3) SetEnter(_st.Items[2], (float)Seg(u, 3.0, 3.45));
            float menuA = (float)Seg(u, 1.9, 2.2) * (1f - (float)Seg(u, 2.75, 2.95));
            RectangleF tray = new RectangleF(RTask.Right - 96 * (VERT ? 1f : 0.9f), RTask.Y + 3, 26, RTask.Height - 6);
            RectangleF menu = new RectangleF(tray.Right - 200, RTask.Y - 11 * 26 - 16, 200, 11 * 26 + 12);
            if (VERT) menu = new RectangleF(RTask.Right - 210, RTask.Y - 11 * 26 - 16, 200, 11 * 26 + 12);
            int hover = (u >= 2.25) ? 5 : -1;
            if (undoT && u < 3.05) hover = 5;

            Scene(g,
                delegate(Graphics gg)
                {
                    PaintDoc(gg, RDoc, KUI, "季度汇报.docx", false, new string[] { "· 交付时间：9 月 12 日 18:00", "· 负责同学：小何" }, null, null);
                    PaintChat(gg, RChat, KUI, ChatMsgs(t), true, null);
                    Taskbar(gg, "17:36", -1, 0f);
                },
                delegate(Graphics gg)
                {
                    if (menuA > 0.01f) PaintTrayMenu(gg, menu, 1f, hover);
                },
                "删错了？撤销一下", null, (float)Seg(u, 0.1, 0.45), 1f);

            PointF c2 = CardPos(0, RTask.Top, WSz(), 2);
            if (u < 0.5) { _curX = RDoc.X + 200; _curY = RDoc.Y + 160; }
            else if (u < 1.15) { PointF q = Pth(new PointF(RDoc.X + 200, RDoc.Y + 160), c2, (float)Seg(u, 0.5, 1.1)); _curX = q.X; _curY = q.Y; }
            else if (u < 1.9) { _curX = c2.X + 10; _curY = c2.Y + 8; _mouseShow = true; _mouseBtn = 2; _mousePress = (float)Seg(u, 1.2, 1.45); }
            else
            {
                PointF q = Pth(c2, new PointF(tray.X + 10, tray.Y + 14), (float)Seg(u, 1.9, 2.3));
                _curX = q.X; _curY = q.Y;
                if (u >= 2.0 && u < 2.6) { _mouseShow = true; _mouseBtn = 2; _mousePress = (float)Seg(u, 2.05, 2.25); }
                if (u >= 2.25 && u < 2.75)
                {
                    PointF q2 = Pth(new PointF(tray.X + 10, tray.Y + 14), new PointF(menu.X + 90, menu.Y + 6 + 5 * 26 + 13), (float)Seg(u, 2.25, 2.5));
                    _curX = q2.X; _curY = q2.Y;
                }
            }
            if (u > 1.35 && u < 1.7) _rippleT = (float)Seg(u, 1.35, 1.7);
            if (u > 2.15 && u < 2.5) _rippleT = (float)Seg(u, 2.15, 2.5);
            if (u > 2.55 && u < 2.9) _rippleT = (float)Seg(u, 2.55, 2.9);
        }

        // ---------- 场景 8：图依次拖进文档 + 镜头拉远 ----------
        static void S8(Graphics g, double t)
        {
            double u = t - 33.0;                        // 0..5
            List<Bitmap> pics = new List<Bitmap>();
            int n = _st.Items.Count;
            for (int i = 0; i < n; i++)
            {
                double at = 0.3 + i * 1.0;
                if (u >= at + 0.55) pics.Add(_st.Items[i].Image);
            }
            int cur = (int)Math.Floor((u - 0.1) / 1.0);
            bool dragging = cur >= 0 && cur < n && u > 0.1 + cur * 1.0 + 0.1 && u < 0.1 + cur * 1.0 + 0.75;
            if (dragging) { _wDragItem = _st.Items[cur]; _wDragProg = Lerp(0f, 0.7f, (float)Seg(u, 0.1 + cur * 1.0 + 0.1, 0.1 + cur * 1.0 + 0.7)); }
            float cam = 1f - 0.13f * Eu((float)Seg(u, 3.1, 4.6));
            RectangleF docR = VERT ? new RectangleF(26, 320, W - 52, 300) : new RectangleF(300, 20, 302, 386);

            Scene(g,
                delegate(Graphics gg)
                {
                    PaintChat(gg, RChat, KUI, ChatMsgs(t), false, null);
                    PaintDoc(gg, docR, KUI, "季度汇报.docx", true, new string[] { "· 交付时间：9 月 12 日 18:00", "· 负责同学：小何", "· 附件：" }, pics, null);
                    Taskbar(gg, "17:38", -1, 0f);
                },
                delegate(Graphics gg)
                {
                    if (dragging)
                    {
                        PointF from = CardPos(0, RTask.Top, WSz(), cur);
                        float px = docR.X + 22 + (pics.Count % 2) * (docR.Width * 0.5f);
                        float py = docR.Bottom - 90;
                        PointF to = new PointF(px + 60, py + 20);
                        PointF c = Pth(from, to, (float)Seg(u, 0.1 + cur * 1.0 + 0.1, 0.1 + cur * 1.0 + 0.75));
                        float iw = 180 * KUI, ih = iw * _shot.Height / (float)_shot.Width;
                        FillR(gg, new RectangleF(c.X - iw / 2f + 5, c.Y - ih / 2f + 6, iw, ih), 4, Color.FromArgb(150, 0, 0, 0));
                        gg.DrawImage(_st.Items[cur].Image, new RectangleF(c.X - iw / 2f, c.Y - ih / 2f, iw, ih));
                        using (Pen p = new Pen(Color.FromArgb(130, 170, 255), 2f)) gg.DrawRectangle(p, c.X - iw / 2f, c.Y - ih / 2f, iw, ih);
                    }
                    if (u > 4.0)
                        TxtC(gg, "环上的图，拖出去就用 —— 图还留在环上", 15f * KUI, FontStyle.Bold, Color.FromArgb(235, 240, 250),
                            new RectangleF(0, SH * 0.30f, W, 26 * KUI));
                },
                "它解决的不是\"怎么截图\"，是\"截完之后怎么用\"", null, (float)Seg(u, 0.1, 0.45), cam);

            PointF from2 = CardPos(0, RTask.Top, WSz(), Math.Max(0, Math.Min(n - 1, cur)));
            if (dragging)
            {
                float px = docR.X + 22 + (pics.Count % 2) * (docR.Width * 0.5f);
                float py = docR.Bottom - 70;
                PointF q = Pth(from2, new PointF(px + 60, py + 20), (float)Seg(u, 0.1 + cur * 1.0 + 0.1, 0.1 + cur * 1.0 + 0.75));
                _curX = q.X + 40; _curY = q.Y + 40;
            }
            else { _curX = docR.X + docR.Width * 0.7f; _curY = docR.Bottom - 50; }
        }

        // ---------- 场景 9：定格 + 片尾 ----------
        static void S9(Graphics g, double t)
        {
            double u = t - 38.0;                        // 0..2
            float dark = Eu((float)Seg(u, 0.05, 0.7));
            float nameA = Eu((float)Seg(u, 0.5, 1.0));
            float tagA = Eu((float)Seg(u, 0.85, 1.3));
            float featA = Eu((float)Seg(u, 1.2, 1.7));
            _curA = 1f - (float)Seg(u, 0.1, 0.5);
            _curX = RDoc.X + RDoc.Width * 0.7f; _curY = RDoc.Bottom - 50;
            Scene(g,
                delegate(Graphics gg)
                {
                    PaintDoc(gg, RDoc, KUI, "季度汇报.docx", true, new string[] { "· 交付时间：9 月 12 日 18:00", "· 负责同学：小何", "· 附件：" }, new List<Bitmap> { _shot, _shot2, _shot3 }, null);
                    PaintChat(gg, RChat, KUI, ChatMsgs(t), false, null);
                    Taskbar(gg, "17:40", -1, 0f);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(225 * dark), 6, 8, 14))) gg.FillRectangle(b, 0, 0, W, SH);
                },
                delegate(Graphics gg)
                {
                    float cy = SH * 0.36f;
                    DrawRingGlyph(gg, new RectangleF(W / 2f - 60 * KUI, cy - 96 * KUI, 120 * KUI, 120 * KUI), 1f);
                    TxtC(gg, "SnapWheel 快照轮环", 34f * KUI, FontStyle.Bold, Color.FromArgb((int)(255 * nameA), 255, 255, 255),
                        new RectangleF(0, cy + 24 * KUI, W, 44 * KUI));
                    TxtC(gg, "截完自动滑进环里 · 拖出去就用", 18f * KUI, FontStyle.Regular, Color.FromArgb((int)(235 * tagA), 150, 200, 255),
                        new RectangleF(0, cy + 74 * KUI, W, 30 * KUI));
                    if (featA > 0.01f)
                    {
                        RectangleF fr = new RectangleF(W / 2f - 250 * KUI, cy + 122 * KUI, 500 * KUI, 40 * KUI);
                        FillR(gg, fr, 20 * KUI, Color.FromArgb((int)(70 * featA), 120, 170, 255));
                        TxtC(gg, "绿色免安装 · 200KB · 零依赖 · Windows 10/11", 15f * KUI, FontStyle.Bold,
                            Color.FromArgb((int)(245 * featA), 225, 238, 255), fr);
                    }
                },
                "SnapWheel 快照轮环 · 截完自动滑进环里，拖出去就用", "绿色免安装 · 200KB · 零依赖 · Windows 10/11",
                (float)Seg(u, 0.2, 0.6), 1f);
        }

        // ==================== 校验（解码回读） ====================
        static void Verify(string path)
        {
            Console.WriteLine("---- verify " + Path.GetFullPath(path) + " ----");
            byte[] b = File.ReadAllBytes(path);
            Console.WriteLine("file size: " + (b.Length / 1048576.0).ToString("0.00") + " MB (" + b.Length + " bytes)");
            string[] need = { "RIFF", "AVI ", "MJPG", "movi", "idx1", "avih", "strh", "strf" };
            for (int i = 0; i < need.Length; i++)
            {
                bool found = IndexOf(b, Encoding.ASCII.GetBytes(need[i]), 0) >= 0;
                Console.WriteLine("  fourcc " + need[i].PadRight(5) + " : " + (found ? "OK" : "MISSING"));
            }
            if (Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "AVI ")
                throw new Exception("not an AVI");
            long riffSize = BitConverter.ToUInt32(b, 4);
            int avihOff = IndexOf(b, Encoding.ASCII.GetBytes("avih"), 0);
            long total = BitConverter.ToUInt32(b, avihOff + 8 + 16);
            int microPer = BitConverter.ToInt32(b, avihOff + 8);
            int aw = BitConverter.ToInt32(b, avihOff + 8 + 32);
            int ah = BitConverter.ToInt32(b, avihOff + 8 + 36);
            int strhOff = IndexOf(b, Encoding.ASCII.GetBytes("strh"), 0);
            int scale = BitConverter.ToInt32(b, strhOff + 8 + 20);
            int rate = BitConverter.ToInt32(b, strhOff + 8 + 24);
            int length = BitConverter.ToInt32(b, strhOff + 8 + 32);
            string handler = Encoding.ASCII.GetString(b, strhOff + 8 + 4, 4);

            // 走 movi 里的 00dc
            int moviOff = IndexOf(b, Encoding.ASCII.GetBytes("movi"), 0);
            List<long> off = new List<long>(); List<int> sz = new List<int>();
            long p = moviOff + 4;
            while (p + 8 <= b.Length)
            {
                string ck = Encoding.ASCII.GetString(b, (int)p, 4);
                if (ck != "00dc" && ck != "00db") break;
                int cs = BitConverter.ToInt32(b, (int)p + 4);
                off.Add(p - moviOff); sz.Add(cs);                   // idx1 里记的偏移（相对 'movi' 四字码）
                p += 8 + cs + (cs & 1);
            }
            // idx1 交叉核对
            int idxOff = IndexOf(b, Encoding.ASCII.GetBytes("idx1"), (int)(moviOff + 4));
            int idxN = idxOff >= 0 ? BitConverter.ToInt32(b, idxOff + 4) / 16 : 0;
            int badIdx = 0;
            if (idxOff >= 0) for (int i = 0; i < idxN && i < off.Count; i++)
            {
                long o = BitConverter.ToUInt32(b, idxOff + 8 + i * 16 + 8);
                int s = BitConverter.ToInt32(b, idxOff + 8 + i * 16 + 12);
                if (o != off[i] || s != sz[i]) badIdx++;
            }
            Console.WriteLine("frames in movi: " + off.Count + " | avih total: " + total + " | strh length: " + length + " | idx1 entries: " + idxN + " | idx1 mismatch: " + badIdx);
            Console.WriteLine("resolution: " + aw + "x" + ah + " | fps: " + (rate / (double)scale).ToString("0.###") + " | microSecPerFrame: " + microPer);
            Console.WriteLine("duration: " + (total / (rate / (double)scale)).ToString("0.00") + " s | stream handler: " + handler + " | riff size field: " + (riffSize + 8) + " (file " + b.Length + ")");
            long sum = 0; for (int i = 0; i < sz.Count; i++) sum += sz[i];
            Console.WriteLine("jpeg bytes total: " + (sum / 1048576.0).ToString("0.00") + " MB | avg per frame: " + (sum / Math.Max(1, sz.Count) / 1024) + " KB | max: " + (MaxOf(sz) / 1024) + " KB");

            string dir = Path.Combine(Path.GetTempPath(), "snapwheel-video-verify" + (path.Contains("vertical") ? "-vertical" : ""));
            Directory.CreateDirectory(dir);
            int[] pick = { 0, off.Count / 2, off.Count - 2 };
            string[] tag = { "first", "middle", "second-last" };
            for (int i = 0; i < pick.Length; i++)
            {
                int fi = pick[i];
                byte[] jpg = new byte[sz[fi]];
                Buffer.BlockCopy(b, (int)(off[fi] + moviOff + 8), jpg, 0, sz[fi]);
                using (Bitmap bm = Decode(jpg))
                {
                    // 抽样几个像素，确认不是花屏（全黑/全同色 = 有问题）
                    Color c1 = bm.GetPixel(bm.Width / 8, bm.Height / 8);
                    Color c2 = bm.GetPixel(bm.Width / 2, bm.Height / 2);
                    Color c3 = bm.GetPixel(bm.Width * 7 / 8, bm.Height * 3 / 4);
                    string f = Path.Combine(dir, "frame-" + (fi + 1).ToString("000") + "-" + tag[i] + ".png");
                    bm.Save(f, ImageFormat.Png);
                    Console.WriteLine("  frame #" + (fi + 1) + " (" + tag[i] + ") " + bm.Width + "x" + bm.Height + " jpeg " + (sz[fi] / 1024) + " KB -> " + f +
                        "  px(" + c1.R + "," + c1.G + "," + c1.B + ")/(" + c2.R + "," + c2.G + "," + c2.B + ")/(" + c3.R + "," + c3.G + "," + c3.B + ")");
                }
            }
            // 全帧解码自检
            int ok = 0, bad = 0;
            for (int i = 0; i < off.Count; i++)
            {
                try
                {
                    byte[] jpg = new byte[sz[i]];
                    Buffer.BlockCopy(b, (int)(off[i] + moviOff + 8), jpg, 0, sz[i]);
                    using (Bitmap bm = Decode(jpg)) { if (bm.Width == aw && bm.Height == ah) ok++; else bad++; }
                }
                catch { bad++; }
            }
            Console.WriteLine("decode all frames: ok=" + ok + " bad=" + bad);
            Console.WriteLine("verify dir: " + dir);
        }
        static int MaxOf(List<int> l) { int m = 0; for (int i = 0; i < l.Count; i++) if (l[i] > m) m = l[i]; return m; }
        static int IndexOf(byte[] hay, byte[] needle, int from)
        {
            for (int i = from; i <= hay.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }

    // ==================== MJPEG / AVI 封装 ====================
    static class AviWriter
    {
        static uint FCC(string s) { return (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24)); }

        public static void WriteMjpegAvi(string path, List<byte[]> frames, int w, int h, int fps)
        {
            int maxSz = 0; long sum = 0;
            for (int i = 0; i < frames.Count; i++) { maxSz = Math.Max(maxSz, frames[i].Length); sum += frames[i].Length; }
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                long riff = fs.Position;
                bw.Write(FCC("RIFF")); bw.Write((uint)0); bw.Write(FCC("AVI "));

                long hdrl = fs.Position;
                bw.Write(FCC("LIST")); bw.Write((uint)0); bw.Write(FCC("hdrl"));
                // avih
                bw.Write(FCC("avih")); bw.Write((uint)56);
                bw.Write((uint)(1000000 / fps));                       // dwMicroSecPerFrame
                bw.Write((uint)(maxSz * fps));                         // dwMaxBytesPerSec
                bw.Write((uint)0);                                     // dwPaddingGranularity
                bw.Write((uint)0x10);                                  // AVIF_HASINDEX
                bw.Write((uint)frames.Count);                          // dwTotalFrames
                bw.Write((uint)0);                                     // dwInitialFrames
                bw.Write((uint)1);                                     // dwStreams
                bw.Write((uint)maxSz);                                 // dwSuggestedBufferSize
                bw.Write((uint)w); bw.Write((uint)h);
                for (int i = 0; i < 4; i++) bw.Write((uint)0);
                // LIST strl
                long strl = fs.Position;
                bw.Write(FCC("LIST")); bw.Write((uint)0); bw.Write(FCC("strl"));
                bw.Write(FCC("strh")); bw.Write((uint)56);
                bw.Write(FCC("vids")); bw.Write(FCC("MJPG"));
                bw.Write((uint)0);                                     // dwFlags
                bw.Write((ushort)0); bw.Write((ushort)0);              // wPriority, wLanguage
                bw.Write((uint)0);                                     // dwInitialFrames
                bw.Write((uint)1);                                     // dwScale
                bw.Write((uint)fps);                                   // dwRate
                bw.Write((uint)0);                                     // dwStart
                bw.Write((uint)frames.Count);                          // dwLength
                bw.Write((uint)maxSz);                                 // dwSuggestedBufferSize
                bw.Write((uint)0xFFFFFFFF);                            // dwQuality
                bw.Write((uint)0);                                     // dwSampleSize
                bw.Write((short)0); bw.Write((short)0); bw.Write((short)w); bw.Write((short)h);   // rcFrame
                bw.Write(FCC("strf")); bw.Write((uint)40);
                bw.Write((uint)40); bw.Write((int)w); bw.Write((int)h);
                bw.Write((ushort)1); bw.Write((ushort)24);
                bw.Write(FCC("MJPG"));                                 // biCompression
                bw.Write((uint)(w * h * 3));                           // biSizeImage
                bw.Write((int)0); bw.Write((int)0);
                bw.Write((uint)0); bw.Write((uint)0);
                PatchSize(bw, fs, strl);
                // LIST odml / dmlh：让部分播放器知道总帧数（不是每个都读 idx1）
                long odml = fs.Position;
                bw.Write(FCC("LIST")); bw.Write((uint)0); bw.Write(FCC("odml"));
                bw.Write(FCC("dmlh")); bw.Write((uint)4); bw.Write((uint)frames.Count);
                PatchSize(bw, fs, odml);
                PatchSize(bw, fs, hdrl);

                // LIST movi
                long movi = fs.Position;
                bw.Write(FCC("LIST")); bw.Write((uint)0); bw.Write(FCC("movi"));
                long moviData = fs.Position;
                long[] offs = new long[frames.Count]; int[] szs = new int[frames.Count];
                for (int i = 0; i < frames.Count; i++)
                {
                    offs[i] = fs.Position - moviData + 4;              // 相对 'movi' 四字码
                    szs[i] = frames[i].Length;
                    bw.Write(FCC("00dc")); bw.Write((uint)frames[i].Length);
                    bw.Write(frames[i]);
                    if ((frames[i].Length & 1) != 0) bw.Write((byte)0);
                }
                PatchSize(bw, fs, movi);
                // idx1
                bw.Write(FCC("idx1")); bw.Write((uint)(16 * frames.Count));
                for (int i = 0; i < frames.Count; i++)
                {
                    bw.Write(FCC("00dc"));
                    bw.Write((uint)0x10);                              // AVIIF_KEYFRAME
                    bw.Write((uint)offs[i]);
                    bw.Write((uint)szs[i]);
                }
                long end = fs.Position;
                fs.Position = riff + 4; bw.Write((uint)(end - riff - 8));
                fs.Position = end;
                bw.Flush();
            }
        }
        static void PatchSize(BinaryWriter bw, FileStream fs, long listPos)
        {
            long cur = fs.Position;
            fs.Position = listPos + 4;
            bw.Write((uint)(cur - listPos - 8));
            fs.Position = cur;
        }
    }
}
