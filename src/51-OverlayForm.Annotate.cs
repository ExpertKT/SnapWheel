using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;

namespace SnapWheel
{
    // 截图标注：箭头 / 方框 / 马赛克 / 文字（roadmap 里"加标注"那条 ——
    // 有了它，截完在轮盘里就能直接圈重点，不用再拖去别的软件）。
    //
    // 坐标：全部用浮层的客户坐标（= 截图位图自己的坐标，shot 画在 0,0），
    // 所以预览和"确认时合成进图片"可以共用同一套画法 —— 所见即所得。
    //
    // 交互：
    //   工具条在选区左下角（放不下就翻到上方）：选择 / 箭头 / 方框 / 马赛克 / 文字 | 颜色 ×4 | 文字底 | A- A+ | 撤销
    //   快捷键：Esc 取消截图（选中图元时先取消选中）、Enter 确认、Ctrl+Z 撤销、A 箭头、R 方框、
    //           M 马赛克、T 文字、V 选择、1~4 颜色、B 文字底、Del 删除选中、[ ] 改字号
    //   选中一个图元后：拖动 = 移动，滚轮 = 改字号（文字）/ 粗细（其它），Del = 删除
    partial class OverlayForm
    {
        enum AnnotKind { Select = 0, Arrow = 1, Rect = 2, Mosaic = 3, Text = 4, Ocr = 5, Emoji = 6 }

        class Shape
        {
            public AnnotKind Kind;
            public PointF A, B;          // 箭头/方框/马赛克 = 起止点；文字 = A 是位置
            public string Text;
            public Color Color;
            public float W = 3f;
            public float Size = 20f;     // 文字字号（会再乘 _k）
            public Bitmap Cache;         // 马赛克结果缓存（每帧重算太贵）
            public Rectangle CacheRect;
        }

        static readonly Color[] AnnotColors = {
            Color.FromArgb(238, 70, 90),    // 红（默认，圈重点最常用）
            Color.FromArgb(250, 176, 42),   // 黄
            Color.FromArgb(0, 150, 240),    // 蓝
            Color.FromArgb(26, 28, 34)      // 黑
        };

        readonly List<Shape> _shapes = new List<Shape>();
        // 重做栈：撤销时把弹出的图元放这儿，重做时再拿回来。
        // 规则和所有编辑器一样：**一旦提交了新图元，重做栈就清空** ——
        // 否则"撤销 → 画新的 → 重做"会把一条早就作废的旧线重新贴回来。
        readonly List<Shape> _redo = new List<Shape>();
        Shape _drawing = null;               // 正在拖的那一个（松手才进 _shapes）
        Shape _sel = null;                   // 当前选中的图元（可拖动/改字号/删除）
        Shape _dragShape = null;             // 正在拖动的图元
        PointF _dragFromShape;
        AnnotKind _tool = AnnotKind.Select;
        Color _annotColor = AnnotColors[0];
        TextBox _textBox = null;
        Rectangle _toolRect = Rectangle.Empty;      // 工具条整体
        Rectangle[] _toolBtns = new Rectangle[0];   // 每个按钮的位置（含颜色点、撤销）
        int _toolHover = -1;
        Rectangle _introRect = Rectangle.Empty;     // 首次教程面板

        const int BtnW = 34;                 // 都会被 _k 缩放
        const int BtnH = 30;
        const int Gap = 6;
        const int IdxBg = 6 + 4;             // 工具 6 个（选择/箭头/方框/马赛克/文字/取字），颜色点占 6..9
        const int IdxSizeDown = IdxBg + 1;
        const int IdxSizeUp = IdxBg + 2;
        const int IdxUndo = IdxBg + 3;
        const int IdxRedo = IdxBg + 4;     // 0.9.10：重做（撤销的反向，只有撤销一直很别扭）
        const int IdxLong = IdxBg + 5;      // 0.6.0：滚动长截图（拿当前选区当抓帧区域，不再走托盘)
        const int IdxSave = IdxBg + 6;     // 0.7.0：另存为（把当前框选含标注存到指定位置）
        const int IdxEmoji = IdxBg + 7;    // 0.7.0：贴 emoji（弹面板选一个，插入后可拖可缩放）
        const int IdxPin = IdxBg + 8;      // 1.0.0：贴图（框完直接钉到屏幕上，同时照常进轮环）
        // internal 而不是私有：测试要按它算工具条几何。**别再在测试里抄一份数字** ——
        // ui-probe 里原来硬编码着 n=18，这次加一个按钮就直接过期了（纯函数测试会继续绿，但测的是旧几何）。
        internal const int BtnCount = IdxBg + 9;





        // 命中图元：从后往前找（后画的在上层）
        Shape HitShape(PointF p)
        {
            for (int i = _shapes.Count - 1; i >= 0; i--)
            {
                Shape s = _shapes[i];
                RectangleF r = ShapeBounds(s);
                if (!IsTextLike(s))
                {
                    float pad = Math.Max(6f, s.W * _k + 3f);
                    r.Inflate(pad, pad);
                }
                if (r.Contains(p)) return s;
            }
            return null;
        }

        void SelectShape(Shape s)
        {
            if (_sel == s) return;
            _sel = s;
            Invalidate();
        }

        void MoveShape(Shape s, float dx, float dy)
        {
            s.A = new PointF(s.A.X + dx, s.A.Y + dy);
            if (!IsTextLike(s)) s.B = new PointF(s.B.X + dx, s.B.Y + dy);
            if (s.Cache != null) { try { s.Cache.Dispose(); } catch { } s.Cache = null; }   // 马赛克跟着挪，得重算
        }

        // 滚轮/按钮调大小：文字改字号，其它改线条粗细
        void ResizeShape(Shape s, float delta)
        {
            if (s == null) return;
            if (IsTextLike(s))
            {
                s.Size = Math.Max(9f, Math.Min(160f, s.Size + delta * 2f));
                _textSize = s.Size;          // 下一个新文字也用这个大小
            }
            else
                s.W = Math.Max(1f, Math.Min(24f, s.W + delta * 0.4f));
            Invalidate();
        }

        // ---------- 工具条布局 ----------
        // 一条硬规则：**工具条绝不压住选区**（压住就是在挡你要截的内容）。
        // 四个方向依次试，全试不到才允许压一点：
        //   1 选区下方  2 选区上方  3 选区右侧（竖排）  4 选区左侧（竖排）
        // 以前只试上下两个方向，选区一高（比如竖着截一整条）就只能压在截图上 ——
        // 结果就是"工具栏挡住了截图区域"。
        int _toolAlpha = 255;        // 鼠标不在附近时自动变淡（不挡内容），靠近就完全不透明
        bool _toolVertical = false;  // 贴在选区左右两侧时改成竖排
        bool _toolOverlap = false;   // 实在没地方、只能压住选区（这时画得更透）






        // 鼠标一动就调一次：只有真的需要变淡/变实才重绘
        void RefreshToolAlpha()
        {
            if (UpdateToolAlpha()) Invalidate();
        }


        // 文字类图元（文字 / emoji）：都按"位置 + 字号"描述，命中与拖动逻辑相同
        static bool IsTextLike(Shape s) { return s.Kind == AnnotKind.Text || s.Kind == AnnotKind.Emoji; }

        // 0.7.0：另存为 —— 把当前框选（含标注）存到用户指定的位置。这一张仍然留在轮盘里。
        void SaveAs()
        {
            if (!_hasSel || _vs.Width < 4 || _vs.Height < 4) return;
            Usage.Ev("SaveAs");
            Bitmap bmp = CropSelection(true);      // true = 标注一起合成进去
            if (bmp == null) return;
            try
            {
                using (System.Windows.Forms.SaveFileDialog d = new System.Windows.Forms.SaveFileDialog())
                {
                    d.Title = Lang.T("另存为", "Save as");
                    d.Filter = "PNG|*.png|JPEG|*.jpg|BMP|*.bmp";
                    d.FileName = "SnapWheel_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    string ext = (Path.GetExtension(d.FileName) ?? "").ToLowerInvariant();
                    if (ext == ".jpg" || ext == ".jpeg")
                    {
                        // JPEG 不支持透明：先铺白底，否则透明区会变黑
                        using (Bitmap flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
                        {
                            using (Graphics gg = Graphics.FromImage(flat)) { gg.Clear(Color.White); gg.DrawImage(bmp, 0, 0); }
                            flat.Save(d.FileName, ImageFormat.Jpeg);
                        }
                    }
                    else if (ext == ".bmp") bmp.Save(d.FileName, ImageFormat.Bmp);
                    else bmp.Save(d.FileName, ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Lang.T("保存失败：", "Save failed: ") + ex.Message, AppInfo.Name);
            }
            finally { bmp.Dispose(); }
        }

        // 0.7.0：贴 emoji —— 弹面板选一个，插到选区中心；之后和文字一样可拖动、可缩放、可删除。
        // 面板是**非模态**的：模态窗口不会失去激活，"点到外面就关"那条就永远不触发（第一版栽在这）。
        // 0.7.0：贴符号 —— 弹面板选一个（面板顶部可选颜色）；之后和文字一样可拖动、可缩放、可删除。
        // 面板是非模态的：模态窗口不会失去激活，"点到外面就关"那条就永远不触发。
        void PickEmoji()
        {
            Rectangle r = _toolBtns[IdxEmoji];
            Point sp = PointToScreen(new Point(r.Left, r.Bottom + 6));
            SymbolPicker.Popup(this, sp, _k, _annotColor, delegate(string g, Color c)
            {
                Shape s = new Shape();
                s.Kind = AnnotKind.Emoji;
                s.Text = g;
                s.Color = c;                                   // 面板里选的颜色
                s.Size = Math.Max(24f, _textSize * 1.4f);
                s.A = new PointF(_hasSel ? _c.X : _vs.Width / 2f, _hasSel ? _c.Y : _vs.Height / 2f);
                s.B = s.A;
                Commit(s);
                _sel = s;
                _tool = AnnotKind.Select;
                Invalidate();
            });
        }


        // 马赛克：先把这块缩小，再放大回原来的大小（放大用最近邻 → 变成色块）。
        // 返回的位图尺寸 == 请求的矩形，所以画的时候直接贴在 r 的左上角就行。
        static Bitmap MosaicOf(Bitmap src, Rectangle r)
        {
            if (src == null || r.Width < 2 || r.Height < 2) return null;
            Rectangle clip = Rectangle.Intersect(r, new Rectangle(0, 0, src.Width, src.Height));
            if (clip.Width < 2 || clip.Height < 2) return null;
            int block = Math.Max(6, (int)Math.Round(Math.Min(clip.Width, clip.Height) / 12f));
            int sw = Math.Max(1, clip.Width / block), sh = Math.Max(1, clip.Height / block);
            try
            {
                Bitmap big = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
                using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, sw, sh), clip, GraphicsUnit.Pixel);
                    }
                    using (Graphics g = Graphics.FromImage(big))
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.SmoothingMode = SmoothingMode.None;
                        g.DrawImage(small, new Rectangle(0, 0, r.Width, r.Height));
                    }
                }
                return big;
            }
            catch { return null; }
        }

        // 关掉浮层时把马赛克缓存放掉（不然每画一次就漏一块内存）
        void DisposeAnnotationCaches()
        {
            for (int i = 0; i < _shapes.Count; i++)
                if (_shapes[i].Cache != null) { try { _shapes[i].Cache.Dispose(); } catch { } _shapes[i].Cache = null; }
            if (_drawing != null && _drawing.Cache != null) { try { _drawing.Cache.Dispose(); } catch { } _drawing.Cache = null; }
        }


        static RectangleF Inset(Rectangle r, float pad)
        {
            return new RectangleF(r.X + pad, r.Y + pad, Math.Max(2, r.Width - pad * 2), Math.Max(2, r.Height - pad * 2));
        }



        // ---------- 鼠标 / 键盘钩子（由 OverlayForm 主文件调进来） ----------
        bool AnnotMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return false;

            if (_annotHint)
            {
                bool inPanel = _introRect.Contains(e.Location);
                _annotHint = false;
                Invalidate();
                if (inPanel) return true;        // 点面板本身：就当作Lang.T("我知道了", "Got it")
            }

            if (ToolbarVisible())
            {
                for (int i = 0; i < _toolBtns.Length; i++)
                {
                    if (!_toolBtns[i].Contains(e.Location)) continue;
                    if (i < 6) { EndText(true); _tool = (AnnotKind)i; }
                    else if (i < 6 + AnnotColors.Length) { _annotColor = AnnotColors[i - 6]; }
                    else if (i == IdxBg) { _textBg = !_textBg; SaveTextBg(); }
                    else if (i == IdxSizeDown) { if (_sel != null) ResizeShape(_sel, -1f); else SetNextTextSize(_textSize - 2f); }
                    else if (i == IdxSizeUp) { if (_sel != null) ResizeShape(_sel, 1f); else SetNextTextSize(_textSize + 2f); }
                    else if (i == IdxLong)
                    {
                        // 0.6.0：把当前选区交给 App 去跑滚动长截图（只拼这一块，不再抓整屏）
                        WantLongShot = true;
                        LongShotRegion = ScreenFor(_vs, new Point((int)_c.X, (int)_c.Y), _hasSel);
                        DialogResult = DialogResult.OK;
                        Close();
                    }

                    else if (i == IdxPin)
                    {
                        // 1.0.0：框完直接贴到屏幕上，**同时照常存进轮环**。
                        //
                        // ⚠️ 这里必须调 Confirm()，不能自己写一句 DialogResult=OK; Close();
                        //    第一版我照抄了上面「长图」那条出口（那句是给"交给 App 去跑另一件事"用的），
                        //    结果 `Result` 是空的 —— 因为 Result 只在 Confirm() 里由 CropSelection 生成。
                        //    那样图既不会进轮环、也不会进剪贴板，正好把用户要的"自动保存到轮环"弄没了。
                        //    走 Confirm() 就等于"用户按了确定"，只是额外带一个"钉上去"的意图。
                        WantPin = true;
                        // 钉在**选区中心**：用户框哪儿，图就出现在哪儿。
                        //
                        // ⚠️ 别用 ScreenFor() 来算这个 —— 它是"**选区在哪块屏幕上**"（返回那块屏幕的
                        //    Bounds），上面「长图」要的是那个（它要在整块屏上抓帧）。
                        //    第一版我拿它的中心当坐标，结果不管框哪儿都钉到**显示器正中央**。
                        //    这里要的是选区自己的位置：浮层的客户坐标 + 虚拟屏幕左上角 = 屏幕坐标
                        //    （和 ScreenFor 内部换算用的是同一条约定：浮层左上角 = 虚拟屏幕左上角）。
                        PinAt = new Point(_vs.Left + (int)_c.X, _vs.Top + (int)_c.Y);
                        Confirm();
                    }
                    else if (i == IdxSave) { SaveAs(); Invalidate(); return true; }
                    else if (i == IdxEmoji) { PickEmoji(); Invalidate(); return true; }
                    else if (i == IdxRedo) Redo();
                    else Undo();
                    Invalidate();
                    return true;
                }
                if (_toolRect.Contains(e.Location)) return true;   // 点在工具条空白处：别当成长按选图
            }

            // 点到已有的图元上：选中它并准备拖动（选择工具、文字工具都支持）
            Shape hit = HitShape(e.Location);
            if (hit != null && (_tool == AnnotKind.Select || _tool == AnnotKind.Text))
            {
                EndText(true);
                SelectShape(hit);
                _dragShape = hit;
                _dragFromShape = e.Location;
                return true;
            }
            if (hit != null && hit.Kind == AnnotKind.Text && _tool != AnnotKind.Select)
            {
                EndText(true);
                SelectShape(hit);
                _dragShape = hit;
                _dragFromShape = e.Location;
                return true;
            }

            if (_tool == AnnotKind.Select)
            {
                SelectShape(null);
                return false;                  // 交回给原来的框选/移动逻辑
            }
            if (!_hasSel) return false;
            if (!InsideSel(e.Location))
            {
                // 选了标注工具还点到选区外：什么都不做。
                // 否则会落回"新建选区"，把刚画的标注全丢掉（画错一笔就白干，太气人）。
                // 想重新框选按 V（或 Esc 重来）。
                return true;
            }
            if (_textBox != null) EndText(true);

            if (_tool == AnnotKind.Text) { BeginText(e.Location); return true; }

            if (_tool == AnnotKind.Ocr)
            {
                // 取字工具：拖一个框圈住要认的文字（框小=只是想认整块选区）
                _drawing = new Shape();
                _drawing.Kind = AnnotKind.Ocr;
                _drawing.A = e.Location;
                _drawing.B = e.Location;
                SelectShape(null);
                Invalidate();
                return true;
            }

            _drawing = new Shape();
            _drawing.Kind = _tool;
            _drawing.A = e.Location;
            _drawing.B = e.Location;
            _drawing.Color = _annotColor;
            _drawing.W = 3f;
            SelectShape(null);
            Invalidate();
            return true;
        }

        bool AnnotMouseMove(MouseEventArgs e)
        {
            RefreshToolAlpha();          // 靠近/离开工具条时变实/变淡
            if (_dragShape != null)
            {
                MoveShape(_dragShape, e.Location.X - _dragFromShape.X, e.Location.Y - _dragFromShape.Y);
                _dragFromShape = e.Location;
                Invalidate();
                return true;
            }

            int h = -1;
            if (ToolbarVisible())
                for (int i = 0; i < _toolBtns.Length; i++) if (_toolBtns[i].Contains(e.Location)) { h = i; break; }
            if (h != _toolHover) { _toolHover = h; Invalidate(); }

            if (_drawing == null) return false;
            _drawing.B = e.Location;
            Invalidate();
            return true;
        }

        bool AnnotMouseUp(MouseEventArgs e)
        {
            if (_dragShape != null) { _dragShape = null; Invalidate(); return true; }
            if (_drawing == null) return false;
            Shape s = _drawing;
            _drawing = null;
            if (s.Kind == AnnotKind.Ocr)
            {
                // 取字：不去动 _shapes（它不是标注，不该被画进成品图）
                RectangleF rc = RectOf(s.A, s.B);
                Invalidate();
                DoOcrRegion(rc);
                return true;
            }
            RectangleF r = RectOf(s.A, s.B);
            bool ok = (s.Kind == AnnotKind.Arrow) || (r.Width >= 4 && r.Height >= 4);
            if (ok) { Commit(s); _annotHint = false; }
            Invalidate();
            return true;
        }

        // 滚轮：选中了图元就改大小（文字改字号），没选中就还给主逻辑
        internal bool AnnotWheel(MouseEventArgs e)
        {
            if (_sel == null) return false;
            ResizeShape(_sel, e.Delta > 0 ? 1f : -1f);
            return true;
        }

        // 返回 true = 这个键已经被标注逻辑用掉了
        bool AnnotKey(KeyEventArgs e)
        {
            if (_textBox != null) return false;        // 正在打字：键都归输入框

            bool ctrl = (e.Modifiers & Keys.Control) == Keys.Control;
            bool shift = (e.Modifiers & Keys.Shift) == Keys.Shift;
            // 撤销 / 重做：Ctrl+Z 与 Ctrl+Y 是 Windows 上的通用约定，
            // Ctrl+Shift+Z 是另一派约定（Mac / 很多编辑器），两个都收，不让用户去猜。
            if (ctrl && e.KeyCode == Keys.Z) { if (shift) Redo(); else Undo(); return true; }
            if (ctrl && e.KeyCode == Keys.Y) { Redo(); return true; }
            if (ctrl) return false;

            switch (e.KeyCode)
            {
                case Keys.V: _tool = AnnotKind.Select; break;
                case Keys.A: _tool = AnnotKind.Arrow; break;
                case Keys.R: _tool = AnnotKind.Rect; break;
                case Keys.M: _tool = AnnotKind.Mosaic; break;
                case Keys.T: _tool = AnnotKind.Text; break;
                case Keys.O: _tool = AnnotKind.Ocr; break;      // O = 取字（OCR）
                case Keys.B: _textBg = !_textBg; SaveTextBg(); break;
                case Keys.OemOpenBrackets: ResizeShape(_sel, -1f); return true;
                case Keys.OemCloseBrackets: ResizeShape(_sel, 1f); return true;
                case Keys.Delete:
                case Keys.Back:
                    if (_sel != null)
                    {
                        _shapes.Remove(_sel);
                        if (_sel.Cache != null) { try { _sel.Cache.Dispose(); } catch { } }
                        _sel = null;
                        Invalidate();
                        return true;
                    }
                    return false;
                case Keys.Escape:
                    if (_sel != null) { _sel = null; Invalidate(); return true; }   // 先取消选中，再按一次才是取消截图
                    return false;
                case Keys.D1: case Keys.NumPad1: _annotColor = AnnotColors[0]; break;
                case Keys.D2: case Keys.NumPad2: _annotColor = AnnotColors[1]; break;
                case Keys.D3: case Keys.NumPad3: _annotColor = AnnotColors[2]; break;
                case Keys.D4: case Keys.NumPad4: _annotColor = AnnotColors[3]; break;
                default: return false;
            }
            Invalidate();
            return true;
        }


        void SaveTextBg()
        {
            if (_set == null) return;
            try { _set.TextBg = _textBg; _set.Save(); } catch { }
        }

        // 取字（OCR）：识别整块选区里的文字。
        // 更准的用法是选「字」工具拖一个框（DoOcrRegion）—— 框小一点、只圈文字，识别率明显更好。
        internal void DoOcr()
        {
            if (!_hasSel || _shot == null || _sz.Width < 4 || _sz.Height < 4) return;
            EndText(true);
            Bitmap crop = null;
            try { crop = CropSelection(false); } catch { }
            if (crop != null) StartOcrAsync(crop);
        }

        // 拖出来的框里取字：从**原图**（不带标注）裁这一块去认，框越贴合文字越准
        internal void DoOcrRegion(RectangleF rect)
        {
            if (!_hasSel || _shot == null) return;
            Rectangle rc = ToRect(rect);
            if (rc.Width < 10 || rc.Height < 10) { DoOcr(); return; }      // 只是点了一下：认整块选区
            rc = Rectangle.Intersect(rc, new Rectangle(0, 0, _shot.Width, _shot.Height));
            if (rc.Width < 4 || rc.Height < 4) return;
            EndText(true);
            try
            {
                Bitmap crop = new Bitmap(rc.Width, rc.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(crop)) g.DrawImageUnscaled(_shot, -rc.Left, -rc.Top);
                StartOcrAsync(crop);
            }
            catch (Exception ex) { Err.Log("OcrCrop", ex); }
        }

        bool _ocrBusy = false;

        // 取字丢到后台线程去做 —— 以前是同步跑的：界面整整卡 100~300ms、鼠标变等待圈、
        // 还没有任何反馈，用户当然觉得"性能垃圾"。现在轮盘照常能用，识别完结果框自己弹出来。
        void StartOcrAsync(Bitmap crop)
        {
            if (crop == null) return;
            if (_ocrBusy) { try { crop.Dispose(); } catch { } return; }     // 上一次还没完，直接忽略这一次
            _ocrBusy = true;

            // 先在 UI 线程把像素拷出来：后台线程就完全不碰 GDI 位图了
            byte[] px = null; int pw = 0, ph = 0;
            try { px = Ocr.PixelsOf(crop, out pw, out ph); } catch { px = null; }
            if (px == null) { try { crop.Dispose(); } catch { } _ocrBusy = false; return; }
            try { crop.Dispose(); } catch { }        // 像素到手，位图就可以扔了

            Invalidate();                            // 让Lang.T("取字中…", "Recognising…")立刻显示出来
            byte[] data = px; int w = pw, h = ph;
            System.Threading.Thread th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate()
            {
                string err = null, txt = null;
                try { txt = Ocr.RecognizePixels(data, w, h, out err); }
                catch (Exception ex) { err = ex.Message; }
                Usage.Ev("Ocr", err != null ? ("失败:" + err) : ("认出 " + (txt == null ? 0 : txt.Trim().Length) + " 字"));
                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        _ocrBusy = false;
                        ShowOcrResult(txt, err);
                    }));
                }
                catch { _ocrBusy = false; }
            }));
            th.IsBackground = true;
            th.Start();
        }

        void ShowOcrResult(string txt, string err)
        {
            if (txt == null)
            {
                try
                {
                    MessageBox.Show(this, err ?? Lang.T("识别失败了", "Recognition failed"), Lang.T("取字", "OCR"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
                return;
            }
            try
            {
                bool wasTop = TopMost;
                TopMost = false;
                using (OcrForm of = new OcrForm(txt))
                {
                    of.TopMost = true;
                    of.ShowDialog(this);
                }
                TopMost = wasTop;
            }
            catch (Exception ex) { Err.Log("OcrForm", ex); }
        }

        void Undo()
        {
            if (_shapes.Count == 0) return;
            Shape last = _shapes[_shapes.Count - 1];
            _shapes.RemoveAt(_shapes.Count - 1);
            if (_sel == last) _sel = null;
            // 注意：**不要**在这里 Dispose(last.Cache)。
            // 马赛克的 Cache 是那张算好的马赛克位图，重做时要原样拿回来；
            // 撤了就释放的话，重做出来的马赛克会是一片空白。
            _redo.Add(last);
            Invalidate();
        }

        // 本地统计用：这次标注用了几个图元、分别是哪些工具。
        // 记"用了哪些工具"而不只是"用了几个" —— 后者回答不了"该往标注里补什么"。
        public string ShapeCount()
        {
            if (_shapes.Count == 0) return "0";
            Dictionary<string, int> c = new Dictionary<string, int>();
            for (int i = 0; i < _shapes.Count; i++)
            {
                string k = _shapes[i].Kind.ToString();
                if (!c.ContainsKey(k)) c[k] = 0;
                c[k]++;
            }
            StringBuilder sb = new StringBuilder(_shapes.Count.ToString());
            foreach (KeyValuePair<string, int> kv in c) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            return sb.ToString();
        }

        void Redo()
        {
            if (_redo.Count == 0) return;
            Shape s = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _shapes.Add(s);
            _sel = s;
            Invalidate();
        }

        // 提交一个新图元：重做链到此为止（见 _redo 的说明）
        void Commit(Shape s)
        {
            _shapes.Add(s);
            ClearRedo();
        }

        void ClearRedo()
        {
            for (int i = 0; i < _redo.Count; i++)
            {
                Shape s = _redo[i];
                if (s.Cache != null) { try { s.Cache.Dispose(); } catch { } }
            }
            _redo.Clear();
        }

        // ---------- 文字工具 ----------
        void BeginText(Point at)
        {
            EndText(true);
            _textBox = new TextBox();
            _textBox.Font = new Font("Microsoft YaHei UI", Math.Max(9f, _textSize * _k), FontStyle.Bold);
            _textBox.ForeColor = _annotColor;
            _textBox.BackColor = Color.White;      // 不能给带透明度的颜色，WinForms 控件不支持
            _textBox.BorderStyle = BorderStyle.FixedSingle;
            _textBox.Location = new Point(at.X, Math.Max(0, at.Y));
            _textBox.Width = (int)(170 * _k);
            _textBox.KeyDown += new KeyEventHandler(delegate(object o, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; EndText(true); }
                else if (ke.KeyCode == Keys.Escape) { ke.SuppressKeyPress = true; EndText(false); }
            });
            // 点到别处（比如去点工具条）也要把字落下，不然打好的字会莫名其妙丢掉
            _textBox.Leave += new EventHandler(delegate(object o, EventArgs e2) { EndText(true); });
            Controls.Add(_textBox);
            _textBox.Focus();
        }

        // 新文字用的字号：跟着上一个文字走（改过一次就不用每次再调）
        float _textSize = 20f;

        // commit=true 且非空 -> 落成一个文字标注，并自动选中它（接着就能拖动/改字号）
        void EndText(bool commit)
        {
            if (_textBox == null) return;
            TextBox tb = _textBox;
            _textBox = null;
            string txt = tb.Text;
            Point at = tb.Location;
            try { Controls.Remove(tb); tb.Dispose(); } catch { }
            if (commit && !string.IsNullOrEmpty(txt))
            {
                Shape s = new Shape();
                s.Kind = AnnotKind.Text;
                s.A = new PointF(at.X, at.Y);
                s.Text = txt;
                s.Color = _annotColor;
                s.Size = _textSize;
                Commit(s);
                _sel = s;                       // 画完就选中：可以直接拖 / 滚轮改大小
                _annotHint = false;
            }
            Invalidate();
        }

        // 选了字号后，下一个新文字也用它
        internal void SetNextTextSize(float size)
        {
            _textSize = Math.Max(9f, Math.Min(160f, size));
            if (_sel != null && _sel.Kind == AnnotKind.Text)
            {
                _sel.Size = _textSize;
                Invalidate();
            }
        }
    }
}
