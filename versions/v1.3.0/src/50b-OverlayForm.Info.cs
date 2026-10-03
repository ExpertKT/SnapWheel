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
    // 截图浮层：右上角信息面板（宽高输入、角度显示）（从 50-OverlayForm.cs 拆出，纯搬移，行为不变）。
    partial class OverlayForm
    {
        // 把"贴屏幕右上角"换算成浮层客户坐标（纯计算，方便测）
        internal static Rectangle InfoPanelRect(Rectangle virtualScreen, Rectangle screen, int panelW, int panelH)
        {
            int margin = 20;
            int x = (screen.Right - virtualScreen.Left) - panelW - margin;
            int y = (screen.Top - virtualScreen.Top) + 18;
            // 夹进这块屏幕里（别压出屏幕边）
            int minX = screen.Left - virtualScreen.Left, minY = screen.Top - virtualScreen.Top;
            int maxX = (screen.Right - virtualScreen.Left) - panelW, maxY = (screen.Bottom - virtualScreen.Top) - panelH;
            if (x < minX) x = minX;
            if (x > maxX) x = maxX;
            if (y < minY) y = minY;
            if (y > maxY) y = maxY;
            return new Rectangle(x, y, panelW, panelH);
        }

        void BuildInfoPanel()
        {
            _infoPanel = new BufferedPanel();
            _panelW = (int)(400 * _k);
            _panelH = (int)(40 * _k);
            _infoPanel.BackColor = Color.FromArgb(210, 18, 20, 24);
            Controls.Add(_infoPanel);
            Panel panel = _infoPanel;
            PlaceInfoPanel();

            Label l1 = new Label(); l1.Text = Lang.T("宽", "W"); l1.ForeColor = Color.White;
            l1.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            l1.Bounds = new Rectangle((int)(10 * _k), (int)(10 * _k), (int)(20 * _k), (int)(22 * _k)); panel.Controls.Add(l1);
            _inW = new TextBox(); _inW.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            _inW.Bounds = new Rectangle((int)(32 * _k), (int)(8 * _k), (int)(66 * _k), (int)(24 * _k));
            _inW.BackColor = Color.FromArgb(38, 40, 46); _inW.ForeColor = Color.White;
            _inW.BorderStyle = BorderStyle.FixedSingle; _inW.TextAlign = HorizontalAlignment.Center;
            panel.Controls.Add(_inW);

            Label l2 = new Label(); l2.Text = Lang.T("高", "H"); l2.ForeColor = Color.White;
            l2.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            l2.Bounds = new Rectangle((int)(108 * _k), (int)(10 * _k), (int)(20 * _k), (int)(22 * _k)); panel.Controls.Add(l2);
            _inH = new TextBox(); _inH.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            _inH.Bounds = new Rectangle((int)(130 * _k), (int)(8 * _k), (int)(66 * _k), (int)(24 * _k));
            _inH.BackColor = Color.FromArgb(38, 40, 46); _inH.ForeColor = Color.White;
            _inH.BorderStyle = BorderStyle.FixedSingle; _inH.TextAlign = HorizontalAlignment.Center;
            panel.Controls.Add(_inH);

            RoundButton apply = new RoundButton();
            apply.Text = Lang.T("应用", "Apply"); apply.Size = new Size((int)(58 * _k), (int)(26 * _k)); apply.Location = new Point((int)(204 * _k), (int)(7 * _k));
            apply.Fill = Color.FromArgb(0, 122, 204); apply.FillHover = Color.FromArgb(0, 140, 232);
            apply.Font = new Font("Microsoft YaHei UI", 9f * _k, FontStyle.Bold);
            apply.Click += new EventHandler(delegate(object o, EventArgs e2) { ApplySizeFromBoxes(); });
            panel.Controls.Add(apply);

            RoundButton reset = new RoundButton();
            reset.Text = Lang.T("角度归零", "Reset angle"); reset.Size = new Size((int)(84 * _k), (int)(26 * _k)); reset.Location = new Point((int)(268 * _k), (int)(7 * _k));
            reset.Fill = Color.FromArgb(70, 74, 84); reset.FillHover = Color.FromArgb(92, 98, 110);
            reset.Font = new Font("Microsoft YaHei UI", 9f * _k);
            reset.Click += new EventHandler(delegate(object o, EventArgs e2) { _ang = 0f; Invalidate(); SyncInfo(); });
            panel.Controls.Add(reset);

            _lblAngle = new Label();
            _lblAngle.ForeColor = Color.FromArgb(170, 176, 186);
            _lblAngle.Font = new Font("Microsoft YaHei UI", 9.5f * _k);
            _lblAngle.Bounds = new Rectangle((int)(10 * _k), (int)(34 * _k), (int)(380 * _k), (int)(20 * _k));
            panel.Controls.Add(_lblAngle);
            panel.Height = (int)(58 * _k);

            _inW.KeyDown += new KeyEventHandler(OnBoxKey);
            _inH.KeyDown += new KeyEventHandler(OnBoxKey);
        }

        void ApplySizeFromBoxes()
        {
            int w, h;
            if (!int.TryParse(_inW.Text.Trim(), out w)) w = (int)Math.Round(_sz.Width);
            if (!int.TryParse(_inH.Text.Trim(), out h)) h = (int)Math.Round(_sz.Height);
            w = Math.Max(2, Math.Min(_vs.Width, w));
            h = Math.Max(2, Math.Min(_vs.Height, h));
            float r = EffRatio();
            if (r > 0f) h = Math.Max(2, (int)Math.Round(w / r));
            if (!_hasSel) { _hasSel = true; _c = new PointF(_vs.Width / 2f, _vs.Height / 2f); }
            _sz = new SizeF(w, h);
            ClampCenter();
            SyncInfo();
            Invalidate();
        }

        void SyncInfo()
        {
            if (!_inW.Focused) _inW.Text = ((int)Math.Round(_sz.Width)).ToString();
            if (!_inH.Focused) _inH.Text = ((int)Math.Round(_sz.Height)).ToString();
            string a = ((int)Math.Round(_ang * 180f / (float)Math.PI)).ToString();
            _lblAngle.Text = Lang.T("角度 ", "Angle ") + a + "°" + (_locked ? Lang.T("　·　比例已锁定", " · aspect locked") : "") + (_hasSel ? "" : Lang.T("　·　拖拽以框选", " · drag to select"));
        }

        // 把信息面板摆到"当前这块屏幕"的右上角；**被选区盖住时挪到选区外面**（上 → 下）。
        // 关键是"没被盖住就别动"：拖选区的时候位置一直变，面板跟着跳会很晕。
        void PlaceInfoPanel()
        {
            if (_infoPanel == null) return;
            Point refPt = _hasSel ? new Point((int)_c.X, (int)_c.Y) : Point.Empty;
            Rectangle scr = ScreenFor(_vs, refPt, _hasSel);
            Rectangle want = InfoPanelRect(_vs, scr, _panelW, _panelH);

            if (_hasSel)
            {
                RectangleF sb = SelBounds();
                Rectangle cur = new Rectangle(_infoPanel.Left, _infoPanel.Top, _panelW, _panelH);
                // 现在的位置没被盖住 → 保持不变
                if (cur.Width > 0 && !cur.IntersectsWith(Rectangle.Round(sb))) { _panelBounds = cur; return; }
                int cl = scr.Left - _vs.Left, ct = scr.Top - _vs.Top, cb = scr.Bottom - _vs.Top;
                int above = (int)sb.Top - _panelH - 10;
                int below = (int)sb.Bottom + 10;
                if (above >= ct + 8) want.Y = above;
                else if (below + _panelH <= cb - 8) want.Y = below;
                else { _panelBounds = cur; return; }      // 上下都没地方：保持原位（配合工具条变淡，不至于太挡）
                if (want.X + _panelW > scr.Right - _vs.Left - 12) want.X = scr.Right - _vs.Left - 12 - _panelW;
                int minX = scr.Left - _vs.Left + 12;
                if (want.X < minX) want.X = minX;
            }
            if (_infoPanel.Bounds != want)
            {
                _infoPanel.Bounds = want;
                try { Invalidate(); } catch { }
            }
            _panelBounds = want;
        }

    }
}
