using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnapWheel
{
    // 诊断模式：轮盘上每个元素都画出**自己的名字和边框**。
    //
    // 为什么要有这个东西：
    //   用户报「某个地方很生硬」的时候，我这边拿到的只有一句文字描述，
    //   而界面上有十几个长得差不多的元素 —— 三个圆按钮、名字药丸、计数胶囊、
    //   提示条、空态提示、两个把手、缩略图、万能键、环……
    //   于是我只能在代码里找一个"看起来像是问题"的硬切，改完发版，来回三次。
    //
    //   打开它、截一张图发过来，就不用再猜了。
    //
    // 做法上有一条是刻意的：**名字和矩形是元素在画自己的时候顺手记下来的**，
    // 而不是我另写一份几何去推算。后者迟早会和真实绘制对不上 ——
    // 那正是这个项目反复踩过的「度量与绘制不同源」（反例 #1）。
    partial class WheelForm
    {
        readonly List<KeyValuePair<string, RectangleF>> _diag =
            new List<KeyValuePair<string, RectangleF>>();

        Font _diagFont;

        // 元素在画自己的时候调一下（不占任何开销：关着的时候第一行就返回）
        void Diag(string name, RectangleF r)
        {
            if (_settings == null || !_settings.DiagMode) return;
            if (r.Width <= 0.5f || r.Height <= 0.5f) return;
            _diag.Add(new KeyValuePair<string, RectangleF>(name, r));
        }

        void DiagClear()
        {
            if (_settings != null && _settings.DiagMode) _diag.Clear();
        }

        // 画在最后（主通道，不进缓存层）
        void DrawDiag(Graphics g)
        {
            if (_settings == null || !_settings.DiagMode || _diag.Count == 0) return;

            if (_diagFont == null) _diagFont = new Font("Microsoft YaHei UI", 7f, FontStyle.Bold);
            Font f = _diagFont;

            // 鼠标下的元素：高亮 + 把名字顶在最上面
            Point mp = ToLogicalPt(PointToClient(Cursor.Position));
            int hot = -1;
            for (int i = _diag.Count - 1; i >= 0; i--)        // 从后往前 = 从上层往下层找
                if (_diag[i].Value.Contains(mp)) { hot = i; break; }

            SmoothingMode oldSm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                for (int i = 0; i < _diag.Count; i++)
                {
                    if (i == hot) continue;
                    RectangleF r = _diag[i].Value;
                    using (Pen p = new Pen(Color.FromArgb(150, 0, 220, 220), 1f))
                        g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                    DrawTag(g, f, _diag[i].Key, r, Color.FromArgb(190, 0, 90, 100));
                }

                if (hot >= 0)
                {
                    RectangleF r = _diag[hot].Value;
                    using (Pen p = new Pen(Color.FromArgb(255, 255, 200, 0), 2f))
                        g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                    DrawTag(g, f, "★ " + _diag[hot].Key, r, Color.FromArgb(235, 190, 120, 0));
                }

                // 左上角一行小字：怎么用
                using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 255, 230, 120)))
                    g.DrawString(Lang.T("诊断模式：鼠标停在哪，那个元素就高亮并报出自己的名字。截图发我即可。",
                                        "Diagnostics: hover an element to highlight it and show its name. Send me a screenshot."),
                                 f, b, 14f, 12f);
            }
            finally { g.SmoothingMode = oldSm; }
        }

        // 名字标签：优先贴在框的右边，贴不下就放里面 —— 别跑出窗口
        void DrawTag(Graphics g, Font f, string text, RectangleF r, Color bg)
        {
            SizeF ts = g.MeasureString(text, f);
            float w = ts.Width + 6f, h = ts.Height + 2f;
            float x = r.Right + 3f, y = r.Top;
            if (x + w > LogicalSize().Width - 2f) x = Math.Max(2f, r.X - w - 3f);
            if (y + h > LogicalSize().Height - 2f) y = Math.Max(2f, r.Bottom - h);

            using (SolidBrush b = new SolidBrush(bg))
                g.FillRectangle(b, x, y, w, h);
            using (SolidBrush tb = new SolidBrush(Color.White))
                g.DrawString(text, f, tb, x + 3f, y + 1f);
        }
    }
}
