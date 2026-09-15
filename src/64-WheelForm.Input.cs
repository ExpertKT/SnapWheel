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
    partial class WheelForm
    {

        // 作废这次长按（红色渐变退回，完全不会触发退出）
        public void CancelCloseHold()
        {
            if (!_closeHold && !_closeLong) return;
            _closeHold = false;
            _closeLong = false;
            try { Capture = false; } catch { }
        }


        // 按下时把矩形按比例缩小（以中心为基准）
        static Rectangle Shrink(Rectangle r, float k)
        {
            if (k <= 0.001f) return r;
            float s = 1f - 0.10f * k;
            int w = (int)Math.Round(r.Width * s), h = (int)Math.Round(r.Height * s);
            return new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        }


        int HitTest(Point p)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < _store.Items.Count; i++)
            {
                float phi = ItemPhi(i);
                if (phi < _phiMin - 0.45f || phi > _phiMax + 0.45f) continue;
                if (EnterProgress(i) < 0.5f) continue;
                if (_store.Items[i] == _dragOutItem) continue;
                RectangleF rr = DrawnRect(i);
                RectangleF hit = new RectangleF(rr.X - 2, rr.Y - 2, rr.Width + 4, rr.Height + 4);
                if (hit.Contains(p))
                {
                    PointF c = ItemCenter(i);
                    float d = (float)Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y));
                    if (d < bestD) { bestD = d; best = i; }
                }
            }
            return best;
        }


        PointF KeyCenter()
        {
            Rectangle r = KeyRect();
            return new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        }


        // 上=新建 右=下一个 左=上一个 下=删除
        int SectorAt(PointF p)
        {
            PointF c = KeyCenter();
            float dx = p.X - c.X, dy = p.Y - c.Y;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            if (d < 18f) return -1;
            if (Math.Abs(dy) > Math.Abs(dx)) return dy < 0 ? 0 : 2;
            return dx > 0 ? 1 : 3;
        }


        // 真正把一张图从轮盘拿走：内存 + 硬盘文件。
        // 关键：图片列表是靠"扫描保存目录"恢复的，只删内存不删文件，下次启动它又回来了。
        // 右键单击/双击删除进入点。上一张还在播删除动画就先把它真正删掉：
        // 连点/来回点时"正在删的那张"只有一个，后来者会覆盖前者，否则两张都删不掉。
        // （单独抽成方法是为了能直接测这条行为 —— 这正是之前测试漏掉的四个 bug 之一）
        void BeginDelete(StoreItem it)
        {
            if (_deletingItem != null)
            {
                StoreItem prev = _deletingItem;
                _deletingItem = null; _deleteProg = 0f;
                RemoveItem(prev, true);
            }
            _deletingItem = it;        // 这张交给 AnimTick 播完动画再删
            _delIdx = _store.Items.IndexOf(it);   // 记住删的是第几张（见 AnimTick 里的视口跟进）
            _deleteProg = 0f;
        }


        // 万能键圆盘松手时执行对应分区的动作（动作由用户在设置里自定义）
        void DoKeyAction(int sector)
        {            string a = _settings.KeyActionAt(sector);
            try
            {
                switch (a)
                {
                    case "new": CreateWheel(); break;
                    case "next": SwitchWheel(1); break;
                    case "prev": SwitchWheel(-1); break;
                    case "delete": _delConfirm = true; _delConfirmAt = DateTime.Now; break;  // 原地进入左右两半确认
                    case "shot": if (CaptureRequested != null) CaptureRequested(this, EventArgs.Empty); break;
                    case "collapse": DismissWheel(); break;
                    case "folder":
                        try { if (!Directory.Exists(_settings.Dir)) Directory.CreateDirectory(_settings.Dir); System.Diagnostics.Process.Start(_settings.Dir); } catch { }
                        break;
                    case "settings": if (SettingsRequested != null) SettingsRequested(this, EventArgs.Empty); break;
                    case "clear": ClearCurrentWheel(); break;
                    case "paste":
                        try { IDataObject dob = Clipboard.GetDataObject(); if (dob != null) ImportBitmap(dob); } catch { }
                        break;
                    default: break;   // "none"：什么都不做
                }
            }
            catch (Exception ex) { Err.Log("DoKeyAction", ex); }
        }


        // 三个小按钮（截图 / 设置 / 收起）：**紧贴万能键的左上方、沿一小段弧排布**（0.6.0 用户要求）。
        // 上一版理解错了 —— 我把它当成"绕屏幕角的同心弧"，结果排到了缩略图弧那一侧，
        // 看着就是"靠着 wheel"而不是"靠着万能键"。
        // 正解是**以万能键自己为圆心**的一段小弧：
        //   · 半径 = 万能键半径 + 按钮半径 + 6 → 紧贴万能键外缘；
        //   · 方位固定取屏幕的**左上方**（屏幕坐标 225°：x 负、y 负），
        //     三个按钮在它两侧各偏 40°，于是"围绕万能键的左上方"排成一小段弧。
        Rectangle BtnRect(int order)
        {
#if NO_KEY
            // 无万能键变体：没有可"围绕"的键，退回"贴屏幕角一竖列"（0.2 线冻的是行为，不是这行字）
            PointF c = Center();
            int bx = (int)((Sx() > 0) ? c.X + 10 : c.X - 40);
            float dist = 150f + order * 40f;
            float by = c.Y + Sy() * dist;
            if (Sy() > 0) by -= 30f;
            return new Rectangle(bx, (int)by, 30, 30);
#else
            const int s = 30;
            Rectangle k = KeyRect();
            if (k.Width < 8 || k.Height < 8) return Rectangle.Empty;      // 保险：拿不到键就不排
            float kcx = k.X + k.Width / 2f, kcy = k.Y + k.Height / 2f;
            float rad = k.Width / 2f + s / 2f + 22f;      // 留出万能键那圈外发光的位置（用户反馈：贴太近把光盖住了）
            double baseA = Math.PI * 1.11;                               // 约 200°：比正左上更靠左一点，整体往下挪（用户反馈太靠上）
            double stepA = (s + 8f) / rad;   // 角度间隔按弧长反推：按钮直径 + 8px 间隙
                                              // （原来写死 40°，半径 83px 时弧长就有 107px，三个按钮散得脱节）
            double a = baseA + stepA * (order - 1);
            int x = (int)Math.Round(kcx + rad * Math.Cos(a));
            int y = (int)Math.Round(kcy + rad * Math.Sin(a));
            return new Rectangle(x - s / 2, y - s / 2, s, s);
#endif
        }


        PointF HintPos(SizeF sz)
        {
            // 计数标签放到环外侧的 45° 对角线上，彻底离开万能键和角上的按钮
            PointF c = Center();
            float d = EffR() + 78f;
            float dx = d * 0.7071f, dy = d * 0.7071f;
            float bx = (Sx() > 0) ? c.X + dx : c.X - dx;
            float by = (Sy() > 0) ? c.Y + dy : c.Y - dy;
            return new PointF(bx - sz.Width / 2f, by - sz.Height / 2f);
        }


        bool OverContent(Point p)
        {
            // 贴边把手：收起态只有它可点；展开态它也算内容（不然点它会穿透到桌面）
            if (NubSingleMode())
            {
                if (NubOutRect().Contains(p)) return true;
            }
            else if (_collapsed) return NubOutRect().Contains(p);
            if (!NubSingleMode() && NubInRect().Contains(p)) return true;
            if (HitTest(p) >= 0) return true;
            if (CloseButtonRect().Contains(p)) return true;
            if (GearButtonRect().Contains(p)) return true;
            if (ShootButtonRect().Contains(p)) return true;
            if (KeyRect().Contains(p)) return true;
            // 名字药丸也要算"内容"，否则点它会直接穿透到桌面（改名点不动的根因）
            RectangleF np = NamePillRect();
            if (!np.IsEmpty)
            {
                RectangleF hit = np;
                if (_nameHover) hit = new RectangleF(np.X, np.Y, np.Width + 96f, np.Height);   // 连右边的提示一起点
                if (hit.Contains(p)) return true;
            }
            if (_menuOpen) return true;
            PointF c = Center();
            float d = (float)Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y));
            return Math.Abs(d - _R) < _thumb * 0.75f;
        }


        void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            Cursor.Current = Cursors.Arrow;            // keep the normal pointer (no odd drag cursor)
            if (_dragOutItem != null && _dragOutProg < 1f)
            {
                _dragOutProg = Math.Min(1f, _dragOutProg + 0.16f);   // pull-out collapse during the drag
                Render();
            }
            if (_proxy.Visible) _proxy.MoveTo(OffsetPt());
        }


        Point OffsetPt()
        {
            Point p = Cursor.Position;
            return new Point(p.X + 18, p.Y + 18);
        }


        // 一次拖拽里 DragOver 会疯狂触发，扫文件夹太浪费：同一次拖拽只算一次，
        // 就算 DataObject 每次都换了新壳，也至少 400ms 才重算一次
        List<string> DropCandidates(IDataObject data)
        {
            if (data == null) return null;
            if (ReferenceEquals(data, _dropCacheKey)) return _dropCacheFiles;
            if (_dropCacheFiles != null && (DateTime.Now - _dropCacheAt).TotalMilliseconds < 400) return _dropCacheFiles;
            List<string> r = null;
            try
            {
                if (!data.GetDataPresent(DragFmt) && data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] paths = data.GetData(DataFormats.FileDrop) as string[];
                    r = ImageIO.Collect(paths, 50);
                }
            }
            catch { }
            _dropCacheKey = data; _dropCacheFiles = r; _dropCacheAt = DateTime.Now;
            return r;
        }


        void ClearDropCache() { _dropCacheKey = null; _dropCacheFiles = null; _dropCacheAt = DateTime.MinValue; }

        // 拖进来的到底是啥：文件（含文件夹）还是直接一张位图（网页/其它程序里拖出来的图）
        static bool HasBitmapData(IDataObject data)
        {
            if (data == null) return false;
            try { return data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib); }
            catch { return false; }
        }


        // 外部图片：整个窗口范围内都接收（不再要求必须正好压在图/环上 —— 太严格会让人以为坏了）
        void OnDragOverWheel(object sender, DragEventArgs e)
        {
            bool mine = false, ext = false; int n = 0;
            try { mine = e.Data != null && e.Data.GetDataPresent(DragFmt); } catch { }
            if (!mine)
            {
                List<string> fs = DropCandidates(e.Data);
                if (fs != null && fs.Count > 0) { ext = true; n = fs.Count; }
                else if (HasBitmapData(e.Data)) { ext = true; n = 1; }
            }
            if (mine)
            {
                bool over = OverContent(ToLogicalPt(PointToClient(new Point(e.X, e.Y))));
                e.Effect = over ? DragDropEffects.Move : DragDropEffects.None;
                if (over != _dropActive || _dropExternal || _dropCount != 0)
                { _dropActive = over; _dropExternal = false; _dropCount = 0; Render(); }
            }
            else
            {
                e.Effect = ext ? DragDropEffects.Copy : DragDropEffects.None;
                if (!_dropActive || !_dropExternal || n != _dropCount)
                { _dropActive = ext; _dropExternal = ext; _dropCount = n; Render(); }
            }
        }


        void OnDragDropWheel(object sender, DragEventArgs e)
        {
            _dropActive = false; _dropExternal = false;
            bool mine = false;
            try { mine = e.Data != null && e.Data.GetDataPresent(DragFmt); } catch { }
            if (mine)
            {
                bool over = OverContent(ToLogicalPt(PointToClient(new Point(e.X, e.Y))));
                if (over) { _returnedToWheel = true; e.Effect = DragDropEffects.Move; }
                else e.Effect = DragDropEffects.None;
            }
            else
            {
                List<string> fs = DropCandidates(e.Data);
                if (fs != null && fs.Count > 0) { e.Effect = DragDropEffects.Copy; ImportFiles(fs); }
                else if (HasBitmapData(e.Data))
                {
                    e.Effect = DragDropEffects.Copy;
                    ImportBitmap(e.Data);
                }
                else e.Effect = DragDropEffects.None;
            }
            ClearDropCache();
            Render();
        }


        // 直接拖过来的一张位图（不是文件）：网页、看图软件、聊天窗口里拖出来的图都走这里
        public void ImportBitmap(IDataObject data)
        {
            Bitmap bmp = null;
            try
            {
                object o = null;
                try { o = data.GetData(DataFormats.Bitmap); } catch { }
                if (o == null) { try { o = data.GetData(DataFormats.Dib); } catch { } }
                if (o is Bitmap) bmp = new Bitmap((Bitmap)o);
                else if (o is Image) bmp = new Bitmap((Image)o);
                else if (o is Stream)
                {
                    using (Stream s = (Stream)o)
                    {
                        long pos = 0; try { pos = s.Position; } catch { }
                        try { using (Image im = Image.FromStream(s)) bmp = new Bitmap(im); }
                        catch { try { s.Position = pos; using (Image im2 = Image.FromStream(s)) bmp = new Bitmap(im2); } catch { } }
                    }
                }
                else if (o is byte[])
                {
                    byte[] raw = (byte[])o;
                    try { using (MemoryStream ms = new MemoryStream(raw)) using (Image im = Image.FromStream(ms)) bmp = new Bitmap(im); }
                    catch { }
                }
            }
            catch { bmp = null; }
            if (bmp == null) { ShowToast("这张图读不出来"); Render(); return; }
            bmp = ImageIO.Fit(bmp, ImageIO.MaxDim);
            StoreItem it = _store.AddCore(bmp, ImageIO.ExtFor(bmp));
            _enterT0[it] = DateTime.Now;
            FollowNewest();                       // 视口跟到最新那张（设置里可关）
            _hover = -1; _enlarged = -1;
            ShowToast("已加入 1 张图片");
            Render();
        }


        // 剪贴板里出现图片就自动收进轮盘（可关）；**自己写的图不收**（见 SelfClipboard），并做去重
        void OnClipboardChanged()
        {
            if (!_settings.ClipboardImport) return;
            if (_dragOutItem != null) return;                                   // 正在拖出，别掺和

            // 第一道（便宜、精确）：剪贴板序号还是我们自己写完那一下 —— 就是我们自己刚写进去的那张，
            // 直接跳过。**连图都不读**：读一张 1600x1000 实测 ~10ms，正好落在"缩略图滑入"的帧上。
            // 跳过时把手里的指纹记进 _lastClipFp：系统对"写一次剪贴板"可能通知不止一次，
            // 第二条通知来的时候登记已经清掉了，靠这条去重才不会又收一张。
            try
            {
                string mine;
                if (SelfClipboard.TakeBySequence(out mine)) { if (mine != null) _lastClipFp = mine; return; }
            }
            catch { }

            Bitmap copy = null;
            string fp = "";
            try
            {
                if (!Clipboard.ContainsImage()) return;
                using (Image im = Clipboard.GetImage())
                {
                    if (im == null || im.Width < 2 || im.Height < 2) return;
                    fp = SelfClipboard.Fingerprint(im);
                    // 第二道（兜底）：序号对不上时（剪贴板被别的程序动过，或者序号读不到）
                    // 再按"图长什么样"比一次 —— 这一步在下面"导入外部图"那条路上本来就要读图，不额外花钱。
                    if (SelfClipboard.IsOurs(fp)) { _lastClipFp = fp; return; }
                    if (fp == _lastClipFp) return;                              // 同一张图，不重复收
                    copy = new Bitmap(im);
                }
            }
            catch { return; }                                                   // 剪贴板被别人占着，忽略这次
            if (copy == null) return;
            try
            {
                copy = ImageIO.Fit(copy, ImageIO.MaxDim);
                StoreItem it = _store.AddCore(copy, ImageIO.ExtFor(copy));
                _lastClipFp = fp;
                if (it != null)
                {
                    _enterT0[it] = DateTime.Now;
                    FollowNewest();               // 视口跟到最新那张（设置里可关）
                    _hover = -1; _enlarged = -1;
                    if (_collapsed && _settings.ShowBalloon) Err.Notify("已从剪贴板收进 1 张图");
                    else ShowToast("已从剪贴板收进 1 张图");
                    if (Visible) Render();
                }
            }
            catch { }
        }


        // 便宜的指纹挪到 SelfClipboard（写剪贴板那边也要用同一个，才能比出"是不是自己写的那张"）
        static string Fingerprint(Image im) { return SelfClipboard.Fingerprint(im); }


        // 把外部图片收进当前 wheel（失败的单张跳过，不打断其它）
        public void ImportFiles(List<string> files)
        {
            if (files == null || files.Count == 0) return;
            int ok = 0, bad = 0;
            for (int i = 0; i < files.Count; i++)
            {
                StoreItem it = null;
                try { it = _store.Import(files[i]); } catch { }
                if (it == null) { bad++; continue; }
                _enterT0[it] = DateTime.Now.AddSeconds(ok * 0.07);    // 依次滑入
                ok++;
            }
            FollowNewest();                                           // 视口跟到最后一张（设置里可关）
            _hover = -1; _enlarged = -1;
            if (ok > 0) ShowToast("已加入 " + ok + " 张图片" + (bad > 0 ? "（" + bad + " 张读不了）" : ""));
            else ShowToast("这些文件读不出图片");
            Render();
        }


        protected override void OnMouseMove(MouseEventArgs e)
        {
            _forceDraw = true;      // 省电模式下，鼠标动这一下也要立刻重绘
            _lastActive = DateTime.Now;
            e = LogicalArgs(e);

            if (_keyDown)
            {
                if (_settings.SwitchMode == "swipe")
                {
                    // 长按后左右滑动切换 wheel
                    if ((DateTime.Now - _keyDownAt).TotalMilliseconds > 240)
                    {
                        int dx = e.X - _swipeStart.X;
                        if (Math.Abs(dx) > 70)
                        {
                            SwitchWheel(dx > 0 ? 1 : -1);
                            _swipeStart = e.Location;      // 允许连续滑动
                        }
                    }
                }
                else if (_menuOpen)
                {
                    int s2 = SectorAt(e.Location);
                    if (s2 != _sector) { _sector = s2; Render(); }
                }
                return;
            }

            bool ch = CloseButtonRect().Contains(e.Location);
            bool gh = GearButtonRect().Contains(e.Location);
            bool sh2 = ShootButtonRect().Contains(e.Location);
            int hh = HitTest(e.Location);
            bool need = false;
            if (hh != _hover) { _hover = hh; need = true; }
            if (ch != _closeHover) { _closeHover = ch; need = true; }
            if (gh != _gearHover) { _gearHover = gh; need = true; }
            if (sh2 != _shootHover) { _shootHover = sh2; need = true; }
            if (need) Render();
            if (_maybeDrag && _dragIndex >= 0)
            {
                // 更明确的手感：按下后移动超过 10px 才算拖动（避免误触发/判定飘忽）
                if (Math.Abs(e.X - _mouseDownPt.X) > 10 || Math.Abs(e.Y - _mouseDownPt.Y) > 10)
                {
                    _maybeDrag = false; _enlarged = -1; _holdIndex = -1; Render();
                    StartDragOut(_dragIndex);
                }
            }
        }


        protected override void OnMouseDown(MouseEventArgs e)
        {
            _forceDraw = true;      // 按下要立刻有反馈
            _lastActive = DateTime.Now;
            e = LogicalArgs(e);

            // 删除确认态：摇杆已分成左右两半，点哪边执行哪边
            if (_delConfirm)
            {
                Rectangle krd = KeyRect();
                if (krd.Contains(e.Location))
                {
                    if (e.X < krd.X + krd.Width / 2f) _delConfirm = false;      // 左半 = 取消
                    else { _delConfirm = false; DeleteWheel(); }                // 右半 = 确认删除
                }
                else _delConfirm = false;                                       // 点别处 = 取消
                Render();
                return;
            }

            if (e.Button == MouseButtons.Left && KeyRect().Contains(e.Location))
            {
                _keyDown = true; _keyDownAt = DateTime.Now;
                _menuOpen = false; _menuT = 0f; _sector = -1;
                _swipeStart = e.Location;
                return;
            }
            // 点 Wheel 名药丸 -> 直接改名
            if (e.Button == MouseButtons.Left && NamePillRect().Contains(e.Location))
            {
                RenameWheel();
                return;
            }
            if (e.Button == MouseButtons.Left && ShootButtonRect().Contains(e.Location))
            {
                _shootPend = true; _shootHold = true; _pendingBtn = "shoot"; _pendingAt = DateTime.Now;
                Render();
                return;
            }
            if (e.Button == MouseButtons.Left && GearButtonRect().Contains(e.Location))
            {
                _gearPend = true; _gearHold = true; _pendingBtn = "gear"; _pendingAt = DateTime.Now;
                Render();
                return;
            }
            // 贴边把手：拉出 / 收起（动画中也能点，随时可掉头）
            if (e.Button == MouseButtons.Left && NubOutRect().Contains(e.Location))
            {
                if (_collapsed)
                {
                    if (CanExpandByNub()) ExpandWheel();
                }
                else if (_collapsing)
                {
                    // 收起动画正走在半路上：这时候点把手应当是"我改主意了，拉回来"。
                    // 以前这里会再收一次（等于没反应），用户看到的就是"收起到后半程没法立马展开"。
                    ExpandWheel();
                }
                else if (NubSingleMode()) CollapseWheel();   // 单把手模式：同一个把手负责收起
                return;
            }
            if (!NubSingleMode() && e.Button == MouseButtons.Left && !_collapsed && NubInRect().Contains(e.Location))
            {
                if (_collapsing) ExpandWheel(); else CollapseWheel();
                return;
            }

            if (e.Button == MouseButtons.Left && CloseButtonRect().Contains(e.Location))
            {
                // 短按 = 关掉轮盘；长按（0.65s）= 变红，松手直接退出 SnapWheel
                _closeHold = true;
                _closeDownAt = DateTime.Now;
                _closeLong = false;
                _closeHoldP = 0f;
                try { Capture = true; } catch { }     // 捕获鼠标：手抖移出按钮也不会漏掉 MouseUp
                _pendingBtn = "";                 // 不走那个 110ms 延迟
                Render();
                return;
            }
            int hh = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && hh >= 0)
            {
                _maybeDrag = true; _dragIndex = hh; _mouseDownPt = e.Location;
                _holdIndex = hh; _holdStart = DateTime.Now;
            }
            else if (e.Button == MouseButtons.Right && hh >= 0)
            {
                bool single = (_settings.DeleteMode == "single");
                bool dbl = false;
                if (!single)
                {
                    dbl = (_lastRightIndex == hh && (DateTime.Now - _lastRightClick).TotalMilliseconds < 450);
                    _lastRightClick = DateTime.Now; _lastRightIndex = hh;
                }
                if (single || dbl)
                {
                    BeginDelete(hh >= 0 && hh < _store.Items.Count ? _store.Items[hh] : null);
                    _enlarged = -1; _hover = -1;
                    Render();
                }
            }
            else if (e.Button == MouseButtons.Middle && hh >= 0 && hh < _store.Items.Count)
            {
                // 中键：把这张图贴（钉）到屏幕上。左键已经用来拖出去、右键用来删、双击用来复制，
                // 中键是唯一还空着的"点一下立刻做点什么"。
                StoreItem it = _store.Items[hh];
                if (it != null && it.Image != null && PinRequested != null)
                {
                    try { PinRequested(it.Image, PointToScreen(e.Location)); ShowToast("已贴到屏幕上（双击它或按 Esc 关掉）"); }
                    catch (Exception ex) { Err.Log("PinRequested", ex); }
                }
            }
        }


        protected override void OnMouseUp(MouseEventArgs e)
        {
            _forceDraw = true;
            e = LogicalArgs(e);
            if (_keyDown)
            {
                _keyDown = false;
                if (_menuOpen)
                {
                    int s2 = SectorAt(e.Location);
                    if (s2 >= 0) DoKeyAction(s2);
                    // 这里别再 _menuT = 0，否则圆盘是"啪"地消失；留给 AnimTick 收缩淡出
                    _menuOpen = false; _sector = -1;
                }
                Render();
                return;
            }
            if (_closeHold)
            {
                try { Capture = false; } catch { }
                bool wasLong = _closeLong || _closeHoldP >= 0.999f;
                _closeHold = false; _closeLong = false; _closeHoldP = 0f;
                if (wasLong) { ShowToast("正在退出 SnapWheel…"); Render(); if (ExitRequested != null) ExitRequested(this, EventArgs.Empty); return; }
                HideWheel();          // 短按：直接关掉轮盘（不是收起）
                return;
            }
            _gearHold = _shootHold = false;
            _maybeDrag = false; _dragIndex = -1; _holdIndex = -1;
            if (_enlarged >= 0) { _enlarged = -1; Render(); }
        }


        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            e = LogicalArgs(e);
            int hh = HitTest(e.Location);
            if (hh >= 0 && _store.Items[hh].Image != null)
            {
                // 0.6.0：双击缩略图 = 用系统默认看图程序**打开原图**（用户要求）。
                // 图可能只在内存里（没开存盘），先 EnsureFile 落一份到磁盘再打开。
                try
                {
                    string fpath = _store.EnsureFile(_store.Items[hh]);
                    if (!string.IsNullOrEmpty(fpath) && System.IO.File.Exists(fpath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fpath) { UseShellExecute = true });
                        return;      // 打开了就别再顺手复制一次
                    }
                }
                catch { }
                // 拿不到文件（或打开失败）就退回原来的行为：复制到剪贴板
                // 登记一下"这张剪贴板是我们自己写的"，别让剪贴板监听把它当成"用户复制的新图"又收一遍；
                // 写完记下剪贴板序号，监听那边就能用最便宜的判据直接跳过（不必把图读回来比指纹）
                SelfClipboard.Note(_store.Items[hh].Image);
                try { Clipboard.SetImage(_store.Items[hh].Image); SelfClipboard.NoteSequence(); } catch { }
            }
        }


        // 管理员模式下拖出去：Windows 的 UIPI 会拦掉跨权限拖拽，DoDragDrop 只会返回 None，
        // 用户看到的就是"拖了半天，图又弹回来了"。以前只有一条开机气泡（很多人把气泡关了，
        // 比如本机设置里 ShowBalloon=0），所以这里在真正拖不动的那一刻直接说清楚：
        // 先 toast 一句，再弹一次说明（每次运行只弹一次）。
        void NotifyAdminDragBlocked()
        {
            if (!Elev.Is) return;
            if (_adminTipShown)
            {
                ShowToast("管理员模式：拖拽被 Windows 拦着（托盘右键 → 管理员模式说明）");
                return;
            }
            _adminTipShown = true;
            ShowToast("管理员模式：拖不出去，是 Windows 拦的");
            // 拖拽结束后再弹：这一刻还在鼠标事件/DoDragDrop 的调用栈里，直接弹模态框容易打架
            if (AdminHelpRequested != null)
                try { BeginInvoke(new MethodInvoker(delegate { try { AdminHelpRequested(this, EventArgs.Empty); } catch { } })); }
                catch { }
        }


        // 组装"拖出去"的载荷。抽成单独方法是为了能测到底给了哪些格式 ——
        // v0.4.8 把"文件格式"默认关掉了，结果拖到资源管理器 / 只吃文件的程序直接放不进去，
        // 用户报的"缩略图拖出去放不了"就是它。少给一个格式 = 少一半能被接收的地方。
        internal DataObject BuildDragData(StoreItem it)
        {
            DataObject data = new DataObject();
            string file = null;
            try { file = _store.EnsureFile(it); } catch { }
            // 图片格式：拖到微信 / Word / PS 这类接受图片的地方，直接就是一张图
            try { data.SetData(DataFormats.Bitmap, true, it.Image); } catch { }
            // 文件格式：拖到桌面 / 资源管理器 / 只认文件的程序全靠它
            if (_settings.DragOutAsFile && file != null)
            {
                try { data.SetData(DataFormats.FileDrop, new string[] { file }); } catch { }
            }
            try { data.SetData(DragFmt, 1); } catch { }        // 标记成"轮盘自己的拖拽"，别当成外部导入
            return data;
        }

        void StartDragOut(int index)
        {
            if (index < 0 || index >= _store.Items.Count) return;
            StoreItem it = _store.Items[index];
            DataObject data = BuildDragData(it);

            _maybeDrag = false; _dragIndex = -1; _holdIndex = -1;
            _dragOutItem = it; _dragOutProg = 0f;
            _enlarged = -1; _hover = -1;

            // follow-preview appears immediately, then we enter the drag at once (the pull-out
            // collapse plays DURING the drag, driven from GiveFeedback -> no start-up lag)
            try { _proxy.ShowFor(it.Image, OffsetPt()); } catch { }
            Render();

            DragDropEffects eff = DragDropEffects.None;
            try
            {
                eff = DoDragDrop(data, DragDropEffects.Copy);
            }
            catch { }
            finally { _proxy.Hide(); }

            bool taken = (eff != DragDropEffects.None) && !_returnedToWheel;
            bool returned = _returnedToWheel;
            // 记下来给 [Frame] 日志用：下次"拖不出去"时，日志里能直接看出是没被接收还是被拦了
            try
            {
                string[] fmts = data.GetFormats(false);
                _lastDragInfo = "给了 " + fmts.Length + " 种格式(" + string.Join("/", fmts) + ") 结果=" + eff +
                                (returned ? " 拖回了轮盘" : "") + (_settings.DragOutAsFile ? "" : " 未带文件格式");
            }
            catch { _lastDragInfo = "结果=" + eff; }
            if (!taken && !returned) NotifyAdminDragBlocked();
            _returnedToWheel = false;
            _dragOutItem = null;
            _dragOutProg = 0f;
            if (taken)
            {
                // 拖出去是 Copy 语义：默认**留一份**在环上（随时能再拖一次，或拖给第二个窗口）。
                // 想要"拖出去即从环上移走"的话，设置第 1 页那个开关关掉即可。
                if (_settings.KeepAfterDragOut)
                {
                    ShowToast("已拖出（环上还留着一份）");
                }
                else
                {
                    _store.Items.Remove(it);
                    _thumbCache.Remove(it);
                    _enterT0.Remove(it);
                    _scales.Clear();
                    if (_targetOffset > MaxOffset()) _targetOffset = MaxOffset();
                    if (_targetOffset < MinOffset()) _targetOffset = MinOffset();
                    if (_offset > _targetOffset) _offset = _targetOffset;
                }
            }
            else if (returned)
            {
                // 拖回轮盘：和刚截完图一样，重新播一次缩略图滑入动画
                _scales.Remove(_store.Items.IndexOf(it));
                MarkNew(it);
                FollowNewest();
                ShowToast("已放回「" + _mgr.ActiveWheel.Name + "」");
            }
            _hover = -1;
            Render();
        }


        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _forceDraw = true;      // 滚轮翻图要立刻动，别等下一 tick
            _lastActive = DateTime.Now;
            _targetOffset -= e.Delta / 120;
            if (_targetOffset < MinOffset()) _targetOffset = MinOffset();
            if (_targetOffset > MaxOffset()) _targetOffset = MaxOffset();
        }
    }
}
