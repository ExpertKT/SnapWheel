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
    // 输入：拖放与导入（OLE 拖放回调、导入图片/文件、剪贴板）（从 64-WheelForm.Input.cs 拆出，纯搬移，行为不变）。
    partial class WheelForm
    {
        void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            Cursor.Current = Cursors.Arrow;            // keep the normal pointer (no odd drag cursor)
            if (_dragOutItem != null)
            {
                // 两种模式在拖拽中的画法**必须不同**，否则至少有一种在骗人：
                //   留一份（默认）：图没走 → 画成"提起来"（放大一点、不缩小）
                //   移走          ：图会走 → 照旧一路缩小到看不见
                if (_settings.KeepAfterDragOut)
                {
                    if (_dragLift < 1f) { _dragLift = Math.Min(1f, _dragLift + 0.16f); Render(); }
                }
                else if (_dragOutProg < 1f)
                {
                    _dragOutProg = Math.Min(1f, _dragOutProg + 0.16f);   // pull-out collapse during the drag
                    Render();
                }
            }
            if (_proxy.Visible) _proxy.MoveTo(OffsetPt());
        }

        // 拖进来的到底是啥：文件（含文件夹）还是直接一张位图（网页/其它程序里拖出来的图）
        static bool HasBitmapData(IDataObject data)
        {
            if (data == null) return false;
            try { return data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib); }
            catch { return false; }
        }

        // 拖进来的是一段文字（网页里选中一段、记事本里选中一段）。1.3.0 起它变成**文字格**。
        static bool HasTextData(IDataObject data)
        {
            if (data == null) return false;
            try
            {
                if (!data.GetDataPresent(DataFormats.UnicodeText) && !data.GetDataPresent(DataFormats.Text)) return false;
                // 只有文字才算：Windows 的"虚拟文件"和资源管理器也会顺手塞一段文本，
                // 那种情况必须优先按文件走（见 OnDragDropWheel 的判定顺序）
                return !data.GetDataPresent(DataFormats.FileDrop) && !HasBitmapData(data);
            }
            catch { return false; }
        }

        // 这个环收不收这种格子（1.3.0 的 takes）。不收的时候必须**说一句**：
        // 静默丢掉是最让人困惑的结果 —— 用户只会以为拖放坏了。
        bool Refuses(CellKind k, out string why)
        {
            why = "";
            Wheel w = _mgr.ActiveWheel;
            if (w == null || w.Accepts(k)) return false;
            why = Lang.T("这个环只收", "This ring only takes ") + w.TakesLabel();
            return true;
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
                else if (HasTextData(e.Data)) { ext = true; n = 1; }   // 1.3.0：拖进来的一段字也是一格
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
                else if (HasTextData(e.Data))
                {
                    e.Effect = DragDropEffects.Copy;
                    ImportText(e.Data);
                }
                else e.Effect = DragDropEffects.None;
            }
            ClearDropCache();
            Render();
        }

        // 直接拖过来的一张位图（不是文件）：网页、看图软件、聊天窗口里拖出来的图都走这里
        public void ImportBitmap(IDataObject data)
        {
            string why;
            if (Refuses(CellKind.Image, out why)) { ShowToast(why); Render(); return; }   // 这个环不收图片
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
            if (bmp == null) { ShowToast(Lang.T("这张图读不出来", "This image could not be read")); Render(); return; }
            bmp = ImageIO.Fit(bmp, ImageIO.MaxDim);
            StoreItem it = _store.AddCore(bmp, ImageIO.ExtFor(bmp));
            _enterT0[it] = DateTime.Now;
            FollowNewest();                       // 视口跟到最新那张（设置里可关）
            _hover = -1; _enlarged = -1;
            ShowToast(Lang.T("已加入 1 张图片", "Added 1 image"));
            Usage.Ev("DropIn", "拖进来 1 张");
            Render();
        }

        // 剪贴板里出现图片就自动收进轮盘（可关）；**自己写的图不收**（见 SelfClipboard），并做去重
        void OnClipboardChanged()
        {
            if (!_settings.ClipboardImport) return;
            if (!_mgr.ActiveWheel.Accepts(CellKind.Image)) return;              // 这个环不收图片，别自动往里塞
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
                    if (_collapsed && _settings.ShowBalloon) Err.Notify(Lang.T("已从剪贴板收进 1 张图", "Collected 1 image from the clipboard"));
                    else ShowToast(Lang.T("已从剪贴板收进 1 张图", "Collected 1 image from the clipboard"));
                    Usage.Ev("ClipboardIn");
                    if (Visible) Render();
                }
            }
            catch { }
        }

        // 把外部文件收进当前 wheel（1.3.0：能当图的当图，读不出来的就是**文件格**；单条失败不打断其它）
        public void ImportFiles(List<string> files)
        {
            if (files == null || files.Count == 0) return;
            int ok = 0, img = 0, file = 0, bad = 0, refused = 0;
            string note = "";
            for (int i = 0; i < files.Count; i++)
            {
                // 这个环声明不收这一种就先拦下（按扩展名预判）：别先把文件搬进来（"移进来"还会动原文件）再反悔
                CellKind guess = ImageIO.IsImageExt(files[i]) ? CellKind.Image : CellKind.File;
                string no;
                if (Refuses(guess, out no)) { refused++; continue; }
                StoreItem it = null;
                string n = "";
                try { it = _store.ImportDropped(files[i], _settings.MoveInOnDrop, out n); } catch { }
                if (it == null) { bad++; continue; }
                if (it.Kind == CellKind.Image) img++; else file++;
                if (!string.IsNullOrEmpty(n)) note = n;               // 最后一条：多是"没能移进来"这种要明说的
                _enterT0[it] = DateTime.Now.AddSeconds(ok * 0.07);    // 依次滑入
                ok++;
            }
            FollowNewest();                                           // 视口跟到最后一张（设置里可关）
            _hover = -1; _enlarged = -1;
            if (ok > 0)
            {
                // 混着文件时不能说"张" —— 用户拖进来一个 PDF，回一句"已加入 1 张图片"是在骗他
                string msg = (file == 0)
                    ? Lang.T("已加入 ", "Added ") + ok + Lang.T(" 张图片", " image(s)")
                    : Lang.T("已加入 ", "Added ") + ok + Lang.T(" 格（图片 ", " cell(s) (image ") + img +
                      Lang.T(" / 文件 ", " / file ") + file + "）";
                if (bad > 0) msg += "（" + bad + Lang.T(" 个没能收进来）", " could not be added)");
                if (refused > 0) msg += "（" + refused + Lang.T(" 个不是这个环收的）", " not what this ring takes)");
                // 文件的结论必须说出来（用户实测反馈：拖进来的文件"移进来"没反应，其实是他不知道有这个开关）：
                // 要么说清"原件进了回收站"，要么说清"留了一份、原件还在 + 去哪儿打开移进来"。
                if (!string.IsNullOrEmpty(note)) msg += " · " + note;
                ShowToast(msg);
            }
            else if (refused > 0)
                ShowToast(Lang.T("这个环只收", "This ring only takes ") + _mgr.ActiveWheel.TakesLabel() +
                          "（" + refused + Lang.T(" 个没收）", " skipped)"));
            else ShowToast(Lang.T("这些文件收不进来", "None of these files could be added"));
            Usage.Ev("DropIn", "拖进来 " + ok + "（图 " + img + " / 文件 " + file + " / 失败 " + bad + " / 不收 " + refused + "）");
            Render();
        }

        // 拖进来的一段文字（网页里选中的、记事本里选中的）→ 文字格。
        // 1.3.0 只收"用户自己拖进来的"；剪贴板里的文字留给 1.8 文字环。
        public void ImportText(IDataObject data)
        {
            string why;
            if (Refuses(CellKind.Text, out why)) { ShowToast(why); Render(); return; }    // 这个环不收文字
            string t = null;
            try { if (data.GetDataPresent(DataFormats.UnicodeText)) t = data.GetData(DataFormats.UnicodeText) as string; } catch { }
            if (string.IsNullOrEmpty(t)) { try { t = data.GetData(DataFormats.Text) as string; } catch { } }
            if (string.IsNullOrEmpty(t))
            {
                ShowToast(Lang.T("这段字读不出来", "That text could not be read"));
                Render(); return;
            }
            bool tooLong = (t.Length > _store.MaxTextLen);
            StoreItem it = null;
            try { it = _store.AddText(t); } catch { }
            if (it == null)
            {
                ShowToast(Lang.T("这段字是空的", "That text was empty"));
                Render(); return;
            }
            _enterT0[it] = DateTime.Now;
            FollowNewest();
            _hover = -1; _enlarged = -1;
            ShowToast(Lang.T("已加入 1 段文字", "Added 1 piece of text") +
                      (tooLong ? Lang.T("（太长，只留了前 5000 字）", " (too long, kept the first 5000)") : ""));
            Usage.Ev("DropIn", "拖进来 1 段文字");
            Render();
        }

    }
}
