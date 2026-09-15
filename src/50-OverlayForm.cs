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
    partial class OverlayForm : Form
    {
        Bitmap _shot;
        Bitmap _dimmed;
        Rectangle _vs;
        float _k = 1f;             // 截图浮层的缩放（高 DPI 屏上按钮/手柄/字号都要放大）

        // 选区模型：中心 + 尺寸 + 旋转角（弧度），支持旋转
        PointF _c;
        SizeF _sz;
        float _ang = 0f;
        bool _hasSel;
        // 0.6.0：浮层工具条上的「长图」按钮 —— 点它就带着当前选区去跑滚动长截图（不再走托盘）
        public bool WantLongShot = false;
        public Rectangle LongShotRegion = Rectangle.Empty;

        bool _dragging;      // 新建选区
        Point _start;
        bool _moving;
        PointF _moveStartC;
        int _resizeCorner = -1;    // 0..3 左上/右上/右下/左下
        bool _rotating;
        float _rotGrab = 0f;

        // 比例
        float _ratio = 0f;          // 来自比例胶囊；0 = 自由
        bool _locked = false;       // 锁定键状态
        float _lockedRatio = 0f;

        // 比例胶囊
        struct Chip { public string Label; public float Ratio; public Rectangle Rect; }
        Chip[] _chips;
        Rectangle _toggleRect;
        bool _chipsOpen = false;
        float _chipsT = 0f;
        Timer _anim;
        int[] _chipW;
        int _toggleW = 92;   // 会被 PlaceChips 按 _k 覆盖
        Rectangle _panelBounds;

        public Bitmap Result;

        // 0.5.0 起：浮层也拿得到设置（可以为 null —— 测试里就不传）。
        // 用它做两件事：第一次用的时候在工具条旁边弹一次"能标注"的提示；记住文字要不要白底。
        internal Settings _set;
        bool _textBg = true;             // 标注文字是否带白底（可在工具条上切换）
        bool _annotHint = false;         // 首次提示：还没展示过就亮一下
        DateTime _annotHintAt = DateTime.MinValue;

        public OverlayForm(Rectangle virtualScreen, Bitmap shot) : this(virtualScreen, shot, null) { }

        public OverlayForm(Rectangle virtualScreen, Bitmap shot, Settings settings)
        {
            _set = settings;
            _annotHint = (settings != null && !settings.AnnotHintDone);
            _textBg = (settings == null) || settings.TextBg;
            _annotHintAt = DateTime.Now;
            _vs = virtualScreen;
            _shot = shot;
            try { _k = Math.Max(0.75f, Math.Min(3f, Native.DpiScaleOf(IntPtr.Zero))); } catch { _k = 1f; }
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = _vs;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            _dimmed = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(_dimmed))
            {
                g.DrawImageUnscaled(shot, 0, 0);
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    g.FillRectangle(dim, 0, 0, shot.Width, shot.Height);
            }
            MeasureChips(); PlaceChips();
            BuildInfoPanel();
            _anim = new Timer();
            _anim.Interval = 15;
            _anim.Tick += new EventHandler(AnimTick);
            _anim.Start();
        }

        // 右上角信息面板：可输入宽高、角度归零
        TextBox _inW, _inH;
        Label _lblAngle;
        Panel _infoPanel;
        int _panelW = 0, _panelH = 0;

        // 当前该贴在"哪块屏幕"上：有选区就用选区中心那块，没有就用鼠标所在那块。
        // 以前这里是整个虚拟屏幕（_vs）的右边缘 —— 副屏在右边时，虚拟屏幕的右边缘就是副屏，
        // 这块面板和比例胶囊就会跑到副屏上去（用户报的"老 bug"）。
        internal static Rectangle ScreenFor(Rectangle virtualScreen, Point clientPoint, bool hasPoint)
        {
            try
            {
                // 客户坐标 -> 屏幕坐标（浮层左上角 = 虚拟屏幕左上角），再问系统那块屏幕
                Point screenPt = hasPoint ? new Point(virtualScreen.Left + clientPoint.X, virtualScreen.Top + clientPoint.Y)
                                          : Cursor.Position;
                return Screen.FromPoint(screenPt).Bounds;
            }
            catch { return virtualScreen; }
        }

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
            _infoPanel = new Panel();
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

        void OnBoxKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { ApplySizeFromBoxes(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { Cancel(); }
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

        void AnimTick(object sender, EventArgs e)
        {
            float tgt = _chipsOpen ? 1f : 0f;
            if (Math.Abs(_chipsT - tgt) < 0.002f) { _chipsT = tgt; _anim.Stop(); Invalidate(); return; }
            _chipsT += (tgt - _chipsT) * 0.26f;
            Invalidate();
        }

        // ---------- 选区几何 ----------
        PointF AxisU() { return new PointF((float)Math.Cos(_ang), (float)Math.Sin(_ang)); }
        PointF AxisV() { return new PointF((float)-Math.Sin(_ang), (float)Math.Cos(_ang)); }

        PointF[] Corners()
        {
            PointF u = AxisU(), v = AxisV();
            float hw = _sz.Width / 2f, hh = _sz.Height / 2f;
            return new PointF[] {
                new PointF(_c.X - u.X*hw - v.X*hh, _c.Y - u.Y*hw - v.Y*hh),   // 左上
                new PointF(_c.X + u.X*hw - v.X*hh, _c.Y + u.Y*hw - v.Y*hh),   // 右上
                new PointF(_c.X + u.X*hw + v.X*hh, _c.Y + u.Y*hw + v.Y*hh),   // 右下
                new PointF(_c.X - u.X*hw + v.X*hh, _c.Y - u.Y*hw + v.Y*hh)    // 左下
            };
        }

        bool InsideSel(PointF p)
        {
            if (!_hasSel) return false;
            PointF u = AxisU(), v = AxisV();
            float dx = p.X - _c.X, dy = p.Y - _c.Y;
            float du = dx * u.X + dy * u.Y, dv = dx * v.X + dy * v.Y;
            return Math.Abs(du) <= _sz.Width / 2f + 2 && Math.Abs(dv) <= _sz.Height / 2f + 2;
        }

        RectangleF SelBounds()
        {
            PointF[] cs = Corners();
            float minx = cs[0].X, maxx = cs[0].X, miny = cs[0].Y, maxy = cs[0].Y;
            for (int i = 1; i < 4; i++)
            {
                if (cs[i].X < minx) minx = cs[i].X;
                if (cs[i].X > maxx) maxx = cs[i].X;
                if (cs[i].Y < miny) miny = cs[i].Y;
                if (cs[i].Y > maxy) maxy = cs[i].Y;
            }
            return new RectangleF(minx, miny, maxx - minx, maxy - miny);
        }

        // 旋转键：在“上边中点”外侧；锁定键：连在旋转键外侧
        PointF RotateHandlePos()
        {
            PointF v = AxisV();
            return new PointF(_c.X - v.X * (_sz.Height / 2f + 30f * _k), _c.Y - v.Y * (_sz.Height / 2f + 30f * _k));
        }
        PointF LockHandlePos()
        {
            PointF v = AxisV();
            return new PointF(_c.X - v.X * (_sz.Height / 2f + 62f * _k), _c.Y - v.Y * (_sz.Height / 2f + 62f * _k));
        }

        float EffRatio()
        {
            if (_locked && _lockedRatio > 0.01f) return _lockedRatio;
            return _ratio;
        }

        // 用“外接矩形”夹取（不是外接圆），保证选区很大时仍然能自由移动
        void ClampCenter()
        {
            RectangleF bb = SelBounds();
            float hw = bb.Width / 2f, hh = bb.Height / 2f;
            if (hw * 2f > _vs.Width) _c.X = _vs.Width / 2f;
            else { if (_c.X < hw) _c.X = hw; if (_c.X > _vs.Width - hw) _c.X = _vs.Width - hw; }
            if (hh * 2f > _vs.Height) _c.Y = _vs.Height / 2f;
            else { if (_c.Y < hh) _c.Y = hh; if (_c.Y > _vs.Height - hh) _c.Y = _vs.Height - hh; }
        }

        // ---------- 比例胶囊 ----------
        void MeasureChips()
        {
            string[] labels = { Lang.T("自由", "Free"), "1:1", "16:9", "9:16", "4:3", "3:4", "21:9" };
            _chipW = new int[labels.Length];
            using (Font f = new Font("Microsoft YaHei UI", 10f * _k))
            using (Graphics g = CreateGraphics())
                for (int i = 0; i < labels.Length; i++)
                    _chipW[i] = (int)g.MeasureString(labels[i], f).Width + (int)(22 * _k);
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
                rowY = (int)bb.Bottom + (int)(14 * _k);
                // 展开后会变宽、而且和工具栏抢同一条位置（都在选区下方）—— 重叠时往下让开，
                // 否则一展开就把工具栏盖住（用户反馈"比例的展开会遮挡工具栏"）。
                    // 避开的是**工具栏** _toolRect，不是 _panelBounds —— 后者是比例胶囊自己上一帧的矩形，
                    // 拿它比较等于没比（用户反馈比例 bug 没修复）。
                    if (_toolRect.Width > 0 && rowY + h > _toolRect.Top && rowY < _toolRect.Bottom + (int)(8 * _k))
                        rowY = _toolRect.Bottom + (int)(8 * _k);
                if (rowY + h > cb - 10) rowY = (int)bb.Top - h - (int)(40 * _k);
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
                        TextRenderer.DrawText(g, c.Label, f, r, Color.FromArgb(al, 255, 255, 255),
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                Rectangle tr = _toggleRect;
                using (GraphicsPath p = Gfx.Round(tr, 9f))
                using (SolidBrush b = new SolidBrush(_chipsOpen ? Color.FromArgb(225, 0, 122, 204) : Color.FromArgb(185, 22, 24, 28)))
                    g.FillPath(b, p);
                using (GraphicsPath p2 = Gfx.Round(tr, 9f))
                using (Pen pen = new Pen(Color.FromArgb(130, 255, 255, 255), 1.2f))
                    g.DrawPath(pen, p2);
                TextRenderer.DrawText(g, _chipsOpen ? Lang.T("比例 ▼", "▼") : Lang.T("比例 ▶", "▶"), f, tr, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // ---------- 绘制 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintOverlay(e); }
            catch (Exception ex) { Err.Log("OverlayForm.OnPaint", ex); }
        }

        void PaintOverlay(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.CompositingMode = CompositingMode.SourceCopy;
            if (_dimmed != null) g.DrawImageUnscaled(_dimmed, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;

            if (_hasSel && _sz.Width > 1 && _sz.Height > 1)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                PointF[] cs = Corners();
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddPolygon(cs);
                    // 选区内显示原图（未变暗）
                    if (_shot != null)
                    {
                        g.SetClip(path);
                        g.DrawImageUnscaled(_shot, 0, 0);
                        g.ResetClip();
                    }
                    using (Pen p = new Pen(Color.FromArgb(0, 174, 255), 2f)) g.DrawPath(p, path);
                }

                // 四角缩放手柄
                foreach (PointF p in cs)
                {
                    using (SolidBrush b = new SolidBrush(Color.White))
                        g.FillRectangle(b, p.X - 4.5f, p.Y - 4.5f, 9, 9);
                    using (Pen bp = new Pen(Color.FromArgb(0, 174, 255), 1.6f))
                        g.DrawRectangle(bp, p.X - 4.5f, p.Y - 4.5f, 9, 9);
                }

                // 旋转键 + 连体锁定键
                PointF rh = RotateHandlePos(), lh = LockHandlePos();
                using (Pen line = new Pen(Color.FromArgb(160, 255, 255, 255), 1.2f))
                {
                    PointF topMid = new PointF((cs[0].X + cs[1].X) / 2f, (cs[0].Y + cs[1].Y) / 2f);
                    g.DrawLine(line, topMid, rh);
                    g.DrawLine(line, rh, lh);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 22, 24, 28)))
                    g.FillEllipse(b, rh.X - 13, rh.Y - 13, 26, 26);
                using (Pen p = new Pen(Color.White, 1.6f))
                {
                    // 旋转图标：圆弧 + 箭头
                    g.DrawArc(p, rh.X - 6.5f, rh.Y - 6.5f, 13, 13, 40, 250);
                    g.DrawLine(p, rh.X + 3.4f, rh.Y - 7.6f, rh.X + 7.2f, rh.Y - 4.4f);
                    g.DrawLine(p, rh.X + 7.2f, rh.Y - 4.4f, rh.X + 2.6f, rh.Y - 3.4f);
                }
                using (SolidBrush b = new SolidBrush(_locked ? Color.FromArgb(240, 0, 122, 204) : Color.FromArgb(225, 22, 24, 28)))
                    g.FillEllipse(b, lh.X - 13, lh.Y - 13, 26, 26);
                using (Pen p = new Pen(Color.White, 1.6f))
                {
                    // 锁图标
                    g.DrawRectangle(p, lh.X - 5f, lh.Y - 1f, 10f, 9f);
                    g.DrawArc(p, lh.X - 3.5f, lh.Y - 7f, 7f, 8f, 180, 180);
                }

                // 尺寸/角度标签
                RectangleF bb2 = SelBounds();
                string txt = ((int)Math.Round(_sz.Width)) + " x " + ((int)Math.Round(_sz.Height));
                if (Math.Abs(_ang) > 0.001f) txt += "   " + ((int)Math.Round(_ang * 180f / Math.PI)) + "°";
                if (_locked) txt += "   🔒";
                using (Font f = new Font("Segoe UI", 9.5f * _k))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(205, 0, 0, 0)))
                using (SolidBrush fg = new SolidBrush(Color.White))
                {
                    SizeF szl = g.MeasureString(txt, f);
                    float tx = bb2.Left, ty = bb2.Top - szl.Height - 6;
                    if (ty < 4) ty = bb2.Bottom + 6;
                    g.FillRectangle(bg, tx, ty, szl.Width + 10, szl.Height + 3);
                    g.DrawString(txt, f, fg, tx + 5, ty + 1);
                }

                string hint = Lang.T("双击保存　·　拖角缩放　·　拖圆点旋转　·　Esc 取消", "Double-click save · corners resize · dot rotate · Esc cancel");
                using (Font f2 = new Font("Microsoft YaHei UI", 10f * _k))
                using (SolidBrush fg2 = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                using (SolidBrush bg2 = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                {
                    SizeF sz2 = g.MeasureString(hint, f2);
                    float hx = bb2.Left;
                    float hy = bb2.Top - sz2.Height - 36;
                    if (hy < 4) hy = bb2.Bottom + 30;
                    g.FillRectangle(bg2, hx, hy, sz2.Width + 8, sz2.Height + 4);
                    g.DrawString(hint, f2, fg2, hx + 4, hy + 2);
                }

                // 标注：裁在选区里画（所见即所得），工具条最后画、不受裁剪影响
                GraphicsState st = g.Save();
                using (GraphicsPath cp = new GraphicsPath())
                {
                    cp.AddPolygon(cs);
                    g.SetClip(cp, CombineMode.Intersect);
                    DrawAnnotationShapes(g);
                }
                g.Restore(st);
            }
            PlaceToolbar();
            UpdateToolAlpha();              // 鼠标不在附近就把工具条变淡（不挡画面）
            PaintToolbar(g, _toolAlpha);
            PaintShapeSelection(g);
            PaintIntroPanel(g);
            PaintOcrBusy(g);
            DrawChips(g);
        }

        // 滚轮：选中了标注图元就调它的大小（文字改字号），否则不动
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (AnnotWheel(e)) return;
            base.OnMouseWheel(e);
        }

        // ---------- 交互 ----------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Cancel(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (AnnotMouseDown(e)) return;

            if (_toggleRect.Contains(e.Location)) { _chipsOpen = !_chipsOpen; _anim.Start(); Invalidate(); return; }
            if (_chipsOpen && _chipsT > 0.5f && _chips != null)
            {
                int shift = (int)((1f - _chipsT) * 26f);
                for (int i = 0; i < _chips.Length; i++)
                {
                    Rectangle r = new Rectangle(_chips[i].Rect.X - shift, _chips[i].Rect.Y, _chips[i].Rect.Width, _chips[i].Rect.Height);
                    if (!r.Contains(e.Location)) continue;
                    _ratio = _chips[i].Ratio;
                    _locked = (_ratio > 0f);
                    _lockedRatio = _ratio;
                    ApplyRatioToSel();
                    SyncInfo();
                    Invalidate();
                    return;
                }
            }

            if (_hasSel)
            {
                // 锁定键
                PointF lh = LockHandlePos();
                if (Dist(e.Location, lh) < 15f * _k)
                {
                    _locked = !_locked;
                    if (_locked) { _lockedRatio = _sz.Height > 1 ? _sz.Width / _sz.Height : 1f; _ratio = 0f; }
                    else { _lockedRatio = 0f; _ratio = 0f; }
                    SyncInfo();
                    Invalidate();
                    return;
                }
                // 旋转键
                PointF rh = RotateHandlePos();
                if (Dist(e.Location, rh) < 15f * _k)
                {
                    _rotating = true;
                    _rotGrab = (float)Math.Atan2(e.Y - _c.Y, e.X - _c.X) - _ang;
                    return;
                }
                // 四角：取“最近的那个角”（小选区四角会重叠，取第一个会老是抓到左上角）
                PointF[] cs = Corners();
                int bestC = -1; float bestD = 11f * _k;
                for (int i = 0; i < 4; i++)
                {
                    float d = Dist(e.Location, cs[i]);
                    if (d < bestD) { bestD = d; bestC = i; }
                }
                if (bestC >= 0) { _resizeCorner = bestC; return; }
                // 内部拖动
                if (InsideSel(e.Location)) { _moving = true; _moveStartC = _c; _start = e.Location; return; }
            }

            // 新建选区
            _dragging = true;
            _start = e.Location;
            _hasSel = false;
            Invalidate();
        }

        static float Dist(PointF a, PointF b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        void ApplyRatioToSel()
        {
            float r = EffRatio();
            if (r <= 0f || !_hasSel || _sz.Width < 4) return;
            float w = _sz.Width;
            _sz = new SizeF(w, w / r);
            ClampCenter();
        }

        void InvalidateForSelection(Rectangle a, Rectangle b)
        {
            Rectangle oldPanel = _panelBounds;
            Rectangle dirty = Rectangle.Union(a, b);
            PlaceChips();
            dirty = Rectangle.Union(dirty, Rectangle.Union(oldPanel, _panelBounds));
            dirty.Inflate(90, 90);
            Invalidate(dirty);
        }

        Rectangle DirtyRect()
        {
            RectangleF bb = _hasSel ? SelBounds() : RectangleF.Empty;
            Rectangle r = new Rectangle((int)bb.Left - 80, (int)bb.Top - 100, (int)bb.Width + 160, (int)bb.Height + 200);
            if (r.Width < 1) r = new Rectangle(0, 0, _vs.Width, _vs.Height);
            return r;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (AnnotMouseMove(e)) return;
            RefreshToolAlpha();          // 靠近/离开工具条时变实/变淡（浮层不常重绘，得主动请求）
            // 关键保护：如果左键其实没按住，立刻清掉所有拖拽状态，
            // 否则“在选区外松开鼠标”后，后续移动会继续缩放/旋转 -> 乱飞
            if ((Control.MouseButtons & MouseButtons.Left) == 0)
            {
                if (_rotating || _resizeCorner >= 0 || _moving || _dragging)
                {
                    _rotating = false; _resizeCorner = -1; _moving = false; _dragging = false;
                }
            }

            if (_rotating)
            {
                Rectangle old = DirtyRect();
                float want = (float)Math.Atan2(e.Y - _c.Y, e.X - _c.X) - _rotGrab;
                if (Math.Abs(want - _ang) > 0.0005f) { _ang = want; }
                InvalidateForSelection(old, DirtyRect());
                return;
            }
            if (_resizeCorner >= 0)
            {
                Rectangle old = DirtyRect();
                ResizeTo(e.Location);
                InvalidateForSelection(old, DirtyRect());
                return;
            }
            if (_moving)
            {
                Rectangle old = DirtyRect();
                _c = new PointF(_moveStartC.X + (e.X - _start.X), _moveStartC.Y + (e.Y - _start.Y));
                ClampCenter();
                InvalidateForSelection(old, DirtyRect());
                return;
            }
            if (_dragging)
            {
                Rectangle old = DirtyRect();
                float x1 = Math.Min(_start.X, e.X), y1 = Math.Min(_start.Y, e.Y);
                float x2 = Math.Max(_start.X, e.X), y2 = Math.Max(_start.Y, e.Y);
                float w = Math.Max(2f, x2 - x1), h = Math.Max(2f, y2 - y1);
                float r = EffRatio();
                if (r > 0f) { if (w / h > r) h = w / r; else w = h * r; }
                _c = new PointF(x1 + w / 2f, y1 + h / 2f);
                _sz = new SizeF(w, h);
                _ang = 0f;
                _hasSel = true;
                InvalidateForSelection(old, DirtyRect());
            }
        }

        // 按住某个角缩放：对角绝对不动；比例锁定按比例；只在屏内限制尺寸（绝不移动固定角）
        void ResizeTo(PointF mouse)
        {
            if (!_hasSel) return;
            PointF u = AxisU(), v = AxisV();
            PointF[] cs = Corners();
            PointF fx = cs[(_resizeCorner + 2) % 4];        // 对角：固定不动
            // 被拖的角相对于固定角，在局部坐标系里的方向（左/上两个角是负的）。
            // 之前漏了这一步：从“左上/右上/左下”任何一个角拖，算出来的 du/dv 是负的，
            // 一被夹到 6 就整块塌掉再按错误中心乱跳 —— 这就是“飞走”的根因。
            float su = (_resizeCorner == 1 || _resizeCorner == 2) ? 1f : -1f;
            float sv = (_resizeCorner == 2 || _resizeCorner == 3) ? 1f : -1f;

            // 鼠标点先夹进屏幕（到边即停）
            float mx = mouse.X, my = mouse.Y;
            if (mx < 0f) mx = 0f; if (mx > _vs.Width) mx = _vs.Width;
            if (my < 0f) my = 0f; if (my > _vs.Height) my = _vs.Height;

            float dx = mx - fx.X, dy = my - fx.Y;
            float du = (dx * u.X + dy * u.Y) * su;          // 乘符号 -> 四个角拖出来都是正尺寸
            float dv = (dx * v.X + dy * v.Y) * sv;
            if (du < 6f) du = 6f;
            if (dv < 6f) dv = 6f;
            float r = EffRatio();
            if (r > 0f) { if (du / dv > r) dv = du / r; else du = dv * r; }

            // 固定角在屏内时：再求一个“以固定角为锚点整体缩放”的最大系数 t，
            // 保证（旋转后的）外接矩形永远不出屏 —— 旋转状态下光夹鼠标点是挡不住的
            bool fxInside = fx.X >= -0.5f && fx.X <= _vs.Width + 0.5f && fx.Y >= -0.5f && fx.Y <= _vs.Height + 0.5f;
            if (fxInside)
            {
                float[] ea = { 0f, su, su, 0f };
                float[] eb = { 0f, 0f, sv, sv };
                float t = 1f;
                for (int k = 0; k < 4; k++)
                {
                    float ex = ea[k] * du * u.X + eb[k] * dv * v.X;
                    float ey = ea[k] * du * u.Y + eb[k] * dv * v.Y;
                    if (ex > 0.001f) { float q = (_vs.Width - fx.X) / ex; if (q < t) t = q; }
                    else if (ex < -0.001f) { float q = (0f - fx.X) / ex; if (q < t) t = q; }
                    if (ey > 0.001f) { float q = (_vs.Height - fx.Y) / ey; if (q < t) t = q; }
                    else if (ey < -0.001f) { float q = (0f - fx.Y) / ey; if (q < t) t = q; }
                }
                float tMin = Math.Max(6f / du, 6f / dv);
                if (t < tMin) t = tMin;
                if (t > 1f) t = 1f;
                du *= t; dv *= t;
            }

            _sz = new SizeF(du, dv);
            _c = new PointF(fx.X + u.X * su * du / 2f + v.X * sv * dv / 2f,
                            fx.Y + u.Y * su * du / 2f + v.Y * sv * dv / 2f);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (AnnotMouseUp(e)) return;
            bool wasRotating = _rotating;
            _moving = false; _resizeCorner = -1; _rotating = false;
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                if (!_hasSel || _sz.Width < 3 || _sz.Height < 3) { _hasSel = false; Invalidate(); }
            }
            // 旋转结束后把（能塞下的）选区收进屏幕，避免转到边角后整块跑到屏外找不回来
            if (wasRotating && _hasSel)
            {
                RectangleF bb = SelBounds();
                float hw = bb.Width / 2f, hh = bb.Height / 2f;
                bool ch = false;
                if (hw * 2f <= _vs.Width)
                {
                    if (_c.X < hw) { _c.X = hw; ch = true; }
                    if (_c.X > _vs.Width - hw) { _c.X = _vs.Width - hw; ch = true; }
                }
                if (hh * 2f <= _vs.Height)
                {
                    if (_c.Y < hh) { _c.Y = hh; ch = true; }
                    if (_c.Y > _vs.Height - hh) { _c.Y = _vs.Height - hh; ch = true; }
                }
                if (ch) Invalidate();
            }
            SyncInfo();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            // 选了标注工具时双击是在画东西（比如连点两下画两个方框），别把它当成"确认"
            if (_tool != AnnotKind.Select) return;
            if (_hasSel && InsideSel(e.Location)) Confirm();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // 正在打字：回车只把这段字落下去，绝不确认整张截图。
            // （以前这里把键"让给输入框"，结果回车漏到下面的"回车=确认截图"分支 ——
            //   用户打个字按回车，截图当场被确认并进了轮盘，非常懵。）
            if (_textBox != null)
            {
                if (e.KeyCode == Keys.Enter) { EndText(true); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Escape) { EndText(false); e.SuppressKeyPress = true; return; }
                return;                       // 其它键交给输入框
            }
            if (_annotHint) { _annotHint = false; Invalidate(); }   // 按任意键 = 开始用（键本身照常生效）
            if (AnnotKey(e)) return;
            if (e.KeyCode == Keys.Escape) Cancel();
            else if (e.KeyCode == Keys.Enter && _hasSel) Confirm();
        }

        void Confirm()
        {
            if (_shot == null || !_hasSel) { Close(); return; }
            EndText(true);                       // 还在输入框里的文字也算数
            if (_set != null) { try { _set.TextBg = _textBg; _set.Save(); } catch { } }   // 记住Lang.T("文字底", "Text background")的选择
            Result = CropSelection(true);

            // 顺手把这张图放进剪贴板。"截完立刻粘一次"（Win+Shift+S 之后 Ctrl+V）是最高频的用法，
            // 以前截完只在环上，要粘得先从角落把图拖出去 —— 比系统截图慢一步。
            // 关掉设置就不碰剪贴板。整件事（登记 + 位图拷贝 + STA 工作线程里的 PNG 编码与 OLE flush）
            // 都在 SelfClipboard 里：**绝不能在 UI 线程上写**，那 80ms 正好落在"缩略图滑入"的帧上。
            if ((_set == null) || _set.CopyOnCapture) SelfClipboard.BeginWrite(Result);

            DialogResult = DialogResult.OK;
            Close();
        }

        // 按当前选区裁一张图（withAnnotations=false 时只裁原图）
        internal Bitmap CropSelection(bool withAnnotations)
        {
            int w = Math.Max(1, (int)Math.Round(_sz.Width));
            int h = Math.Max(1, (int)Math.Round(_sz.Height));
            Bitmap crop = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(crop))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TranslateTransform(w / 2f, h / 2f);
                g.RotateTransform(-_ang * 180f / (float)Math.PI);
                g.TranslateTransform(-_c.X, -_c.Y);
                g.DrawImageUnscaled(_shot, 0, 0);
                if (withAnnotations) DrawAnnotationShapes(g);   // 标注用同一套坐标和变换画进去 —— 所见即所得
            }
            return crop;
        }

        void Cancel() { Result = null; DialogResult = DialogResult.Cancel; Close(); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 定时器必须停掉：窗口关了它还每 15ms 醒一次，白烧 CPU（测试里越跑越慢就是这么来的）
            try { if (_anim != null) { _anim.Stop(); _anim.Dispose(); _anim = null; } } catch { }
            DisposeAnnotationCaches();
            if (_shot != null) { _shot.Dispose(); _shot = null; }
            if (_dimmed != null) { _dimmed.Dispose(); _dimmed = null; }
            base.OnFormClosed(e);
        }

        // 有些路径（测试、异常退出）是直接 Dispose 的，不经过 OnFormClosed，这里再兜一次
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { if (_anim != null) { _anim.Stop(); _anim.Dispose(); _anim = null; } } catch { } }
            base.Dispose(disposing);
        }
    }

    class DragProxyForm : Form
    {
        Bitmap _bmp;
        const int BOX = 120;
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        public DragProxyForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Size = new Size(BOX + 24, BOX + 24);
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return cp; }
        }

        public void ShowFor(Bitmap img, Point at)
        {
            int w = Width, h = Height;
            if (_bmp == null || _bmp.Width != w || _bmp.Height != h)
            {
                if (_bmp != null) _bmp.Dispose();
                _bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            }
            using (Graphics g = Graphics.FromImage(_bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                RectangleF box = new RectangleF(12, 12, BOX, BOX);
                using (GraphicsPath bgp = Gfx.Round(box, 14f))
                {
                    using (SolidBrush bb = new SolidBrush(Color.FromArgb(244, 26, 28, 33))) g.FillPath(bb, bgp);
                    if (img != null)
                    {
                        g.SetClip(bgp);
                        RectangleF fit = Gfx.FitContain(img.Size, new RectangleF(box.X + 4, box.Y + 4, box.Width - 8, box.Height - 8));
                        g.DrawImage(img, fit);
                        g.ResetClip();
                    }
                    using (Pen bp = new Pen(Color.FromArgb(235, 255, 255, 255), 1.6f)) g.DrawPath(bp, bgp);
                }
            }
            Location = at;
            IntPtr hh = Handle;                       // create the window first
            if (!Visible) Show();
            Native.PushLayered(this, _bmp);           // content ready (no flash at a wrong spot)
        }

        public void MoveTo(Point at)
        {
            if (Location == at) return;
            Location = at;
            if (_bmp != null) Native.PushLayered(this, _bmp);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _bmp != null) { try { _bmp.Dispose(); } catch { } _bmp = null; }
            base.Dispose(disposing);
        }
    }
}
