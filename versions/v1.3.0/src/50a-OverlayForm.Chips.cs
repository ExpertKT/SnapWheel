using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnapWheel
{
    // 截图浮层：比例胶囊（摆位、测量、绘制）（从 50-OverlayForm.cs 拆出，纯搬移，行为不变）。
    partial class OverlayForm
    {
        // ---------- 比例胶囊 ----------

        // 胶囊这一行的**纵向落位**（纯计算，离线可测：tests\ui-probe.cs 拿一块假的 1024×768 屏幕直接调它）。
        // 抽出来的理由和 ToolbarRect 一样：屏幕一大一小结果完全不同，
        // 本机 1067 高的屏上下都塞得下，小屏那种撞法永远看不到。
        internal static int ChipRowY(RectangleF sel, int h, Rectangle tool, int ct, int cb, float k)
        {
            int rowY = (int)sel.Bottom + (int)(14 * k);
            // 展开后会变宽、而且和工具栏抢同一条位置（都在选区下方）—— 重叠时往下让开，
            // 否则一展开就把工具栏盖住（用户反馈"比例的展开会遮挡工具栏"）。
            // 避开的是**工具栏** tool，不是胶囊自己上一帧的矩形 —— 拿它比较等于没比（用户反馈比例 bug 没修复）。
            if (HitsToolY(rowY, h, tool, k)) rowY = tool.Bottom + (int)(8 * k);
            // 下面塞不下 → 挪到选区上方
            if (rowY + h > cb - 10) rowY = (int)sel.Top - h - (int)(40 * k);
            // **挪到上面之后必须再查一次**（0.9.10 修）：
            // 屏幕矮的时候工具栏自己也只能摆在选区上方（它下面同样塞不下），胶囊正好落进它里面。
            // CI 的 1024×768 上必现 —— 胶囊 338..370 vs 工具条 356..398。
            // 之前只在"往下让"那条路上查了工具栏，往上挪这条路上没查，于是绕了一圈又撞回去。
            if (HitsToolY(rowY, h, tool, k)) rowY = tool.Top - h - (int)(8 * k);
            return rowY;
        }

        // 这一行会不会撞上工具栏（上下各留 8×k 的缝）
        static bool HitsToolY(int y, int h, Rectangle tool, float k)
        {
            return tool.Width > 0 && y + h > tool.Top && y < tool.Bottom + (int)(8 * k);
        }

        void MeasureChips()
        {
            string[] labels = { Lang.T("自由", "Free"), "1:1", "16:9", "9:16", "4:3", "3:4", "21:9" };
            _chipW = new int[labels.Length];
            using (Font f = new Font("Microsoft YaHei UI", 10f * _k))
            using (Graphics g = CreateGraphics())
                for (int i = 0; i < labels.Length; i++)
                    _chipW[i] = (int)g.MeasureString(labels[i], f).Width + (int)(22 * _k);
        }

        void PlaceChips()
        {
            string[] labels = { Lang.T("自由", "Free"), "1:1", "16:9", "9:16", "4:3", "3:4", "21:9" };
            float[] ratios = { 0f, 1f, 16f / 9f, 9f / 16f, 4f / 3f, 3f / 4f, 21f / 9f };
            if (_chipW == null) MeasureChips();
            _toggleW = (int)Math.Round(92 * _k);
            int h = (int)Math.Round(32 * _k), gap = (int)Math.Round(8 * _k);
            int chipsW = 0;
            for (int i = 0; i < _chipW.Length; i++) chipsW += _chipW[i] + gap;
            chipsW -= gap;
            int totalW = _toggleW + gap + chipsW;

            int rowX, rowY;
            // 一直贴"当前这块屏幕"（而不是整个虚拟屏幕）—— 双屏时胶囊才不会卡在两屏中间
            Rectangle scr = ScreenFor(_vs, _hasSel ? new Point((int)_c.X, (int)_c.Y) : Point.Empty, _hasSel);
            int cl = scr.Left - _vs.Left, ct = scr.Top - _vs.Top;      // 这块屏幕在客户坐标里的左上角
            int cr = scr.Right - _vs.Left, cb = scr.Bottom - _vs.Top;
            if (_hasSel)
            {
                RectangleF bb = SelBounds();
                rowX = (int)bb.Left;
                rowY = ChipRowY(bb, h, _toolRect, ct, cb, _k);
            }
            else
            {
                rowX = cl + (scr.Width - totalW) / 2;
                rowY = cb - h - (int)(44 * _k);
            }
            if (rowX < cl + 10) rowX = cl + 10;
            if (rowX + totalW > cr - 10) rowX = cr - 10 - totalW;
            if (rowY < ct + 10) rowY = ct + 10;
            if (rowY + h > cb - 10) rowY = cb - 10 - h;

            _toggleRect = new Rectangle(rowX, rowY, _toggleW, h);
            int x = rowX + _toggleW + gap;
            _chips = new Chip[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                _chips[i].Label = labels[i];
                _chips[i].Ratio = ratios[i];
                _chips[i].Rect = new Rectangle(x, rowY, _chipW[i], h);
                x += _chipW[i] + gap;
            }
            _panelBounds = new Rectangle(rowX, rowY, totalW, h);
        }

        void DrawChips(Graphics g)
        {
            // 没框选就没有比例可设：不显示胶囊，免得按钮悬在半空（原来按展开后的总宽居中，收起时按钮偏左）
            if (!_hasSel) { _toggleRect = Rectangle.Empty; _panelBounds = Rectangle.Empty; return; }
            PlaceChips();
            PlaceInfoPanel();
            if (_chips == null) return;
            using (Font f = new Font("Microsoft YaHei UI", 10f * _k))
            {
                int shift = (int)((1f - _chipsT) * 26f);
                int al = (int)(255 * _chipsT);
                if (_chipsT > 0.01f)
                {
                    foreach (Chip c in _chips)
                    {
                        bool act = (_ratio > 0f && Math.Abs(c.Ratio - _ratio) < 0.001f) || (c.Ratio == 0f && _ratio == 0f && !_locked);
                        Rectangle r = new Rectangle(c.Rect.X - shift, c.Rect.Y, c.Rect.Width, c.Rect.Height);
                        using (GraphicsPath p = Gfx.Round(r, 8f))
                        using (SolidBrush b = new SolidBrush(act
                            ? Color.FromArgb((int)(235 * _chipsT), 0, 122, 204)
                            : Color.FromArgb((int)(185 * _chipsT), 22, 24, 28)))
                            g.FillPath(b, p);
                        using (GraphicsPath p2 = Gfx.Round(r, 8f))
                        using (Pen pen = new Pen(Color.FromArgb((int)((act ? 255 : 120) * _chipsT), 255, 255, 255), 1.2f))
                            g.DrawPath(pen, p2);
                        // 用 DrawString（GDI+）而不是 TextRenderer：GDI 不认半透明色，alpha 被忽略，
                        // 收起时字不会渐隐、到某一帧直接消失（用户反馈"没有动画过渡"）。
                        StringFormat sfC = new StringFormat();
                        sfC.Alignment = StringAlignment.Center;
                        sfC.LineAlignment = StringAlignment.Center;
                        using (SolidBrush tb = new SolidBrush(Color.FromArgb(al, 255, 255, 255)))
                            g.DrawString(c.Label, f, tb, new RectangleF(r.X, r.Y, r.Width, r.Height), sfC);
                    }
                }
                Rectangle tr = _toggleRect;
                using (GraphicsPath p = Gfx.Round(tr, 9f))
                using (SolidBrush b = new SolidBrush(_chipsOpen ? Color.FromArgb(225, 0, 122, 204) : Color.FromArgb(185, 22, 24, 28)))
                    g.FillPath(b, p);
                using (GraphicsPath p2 = Gfx.Round(tr, 9f))
                using (Pen pen = new Pen(Color.FromArgb(130, 255, 255, 255), 1.2f))
                    g.DrawPath(pen, p2);
                // ⚠️ 这里必须用 DrawString（GDI+），**不能**用 TextRenderer（GDI）：
                //   实测在 2560x1440 的目标位图上，TextRenderer.DrawText 单次要 9.2ms，
                //   而 DrawString 只要 0.015ms —— 相差约 600 倍。原因：GDI 的 DrawText 会沿
                //   着整个目标表面处理裁剪区域，位图越大越慢；GDI+ 与目标大小无关。
                //   这一处就是"比例动画卡顿"的真正元凶（DrawChips 整体 8.2ms 几乎全在这）。
                StringFormat sfT = new StringFormat();
                sfT.Alignment = StringAlignment.Center;
                sfT.LineAlignment = StringAlignment.Center;
                using (SolidBrush tbT = new SolidBrush(Color.White))
                    g.DrawString(_chipsOpen ? Lang.T("比例 ▼", "▼") : Lang.T("比例 ▶", "▶"), f, tbT,
                        new RectangleF(tr.X, tr.Y, tr.Width, tr.Height), sfT);
            }
        }

    }
}
