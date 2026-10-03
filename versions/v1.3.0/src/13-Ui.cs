using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapWheel
{
    // ======================= DPI 缩放（0.5.3 修订 / 0.6.0 全面铺开） =======================
    // 为什么需要它：本程序是 per-monitor DPI aware 的，所以 150% 缩放下**字体是按 DPI 放大渲染的**，
    // 而代码里写死的像素（位置 / 尺寸 / 行高 / 边距）不会跟着变 —— 结果就是文字比格子大：
    // 长句被裁掉右半边、按钮被挤出窗口、说明文字只剩一行。用户报的「凡是涉及界面的都显示不全」
    // 就是这个根因（设置窗口在 0.5.3 里已经单独修过，其余对话框当时没跟上）。
    //
    // 约定（和 75-SettingsForm 里那套一模一样，**别再发明第二套**）：
    //   · **长度类**（x / y / 宽 / 高 / 行高 / 边距 / 按钮尺寸）一律过 S() 乘 K；
    //   · **字体磅值不乘** —— GDI+ 已经按 DPI 渲染过一遍，再乘就是双倍放大；
    //   · 说明性文字的 Label 尽量交给 Wrap()：AutoSize + MaximumSize(宽, 0) 让它自己折行、
    //     自己报 PreferredHeight。这比"把高度算准"可靠得多 —— 文字长一点、缩放换一档都不会裁。
    static class Ui
    {
        static float _k = -1f;

        // 测试/截图工具用：强行指定缩放系数（0 = 按真实 DPI）
        public static float ForceKForTest = 0f;

        public static float K
        {
            get
            {
                if (_k < 0f) _k = Calc();
                return _k;
            }
        }

        static float Calc()
        {
            float k;
            try { k = ForceKForTest > 0f ? ForceKForTest : Native.DpiScaleOf(IntPtr.Zero); }
            catch { k = 1f; }
            if (!(k >= 1f)) k = 1f;      // 小于 100% 不缩：缩了字更小、反而更看不清
            if (k > 3f) k = 3f;
            return k;
        }

        public static void ResetCacheForTest() { _k = -1f; }

        public static int S(int v) { return (int)Math.Round(v * K); }
        public static int S(float v) { return (int)Math.Round(v * K); }
        public static Size Sz(int w, int h) { return new Size(S(w), S(h)); }
        public static Point Pt(int x, int y) { return new Point(S(x), S(y)); }
        public static Padding Pad(int all) { return new Padding(S(all)); }
        public static Padding Pad(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }

        // 把一段说明文字变成"自己会折行、自己报高度"的标签：
        // maxW 是它允许占的最大宽度（**已经乘过 K 的物理像素**，传窗体的内容宽）。
        public static Label Wrap(Label l, int maxW)
        {
            l.AutoSize = true;
            l.MaximumSize = new Size(maxW, 0);
            return l;
        }

        // 单行不折行的标签（标题这类）：AutoSize 就够了，宽度也别卡死
        public static Label OneLine(Label l)
        {
            l.AutoSize = true;
            return l;
        }

        // 一行文字在给定宽度下要占多高（AutoSize=false 的 Label 想手动排时用）
        public static int TextH(Control c, string text, int w)
        {
            try
            {
                Size s = TextRenderer.MeasureText(text, c.Font, new Size(w, int.MaxValue),
                                                  TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                return Math.Max(c.Font.Height, s.Height);
            }
            catch { return c.Font.Height; }
        }
    }
}
