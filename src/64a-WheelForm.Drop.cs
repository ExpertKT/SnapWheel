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
            if (_dragOutItem != null && _dragOutProg < 1f)
            {
                _dragOutProg = Math.Min(1f, _dragOutProg + 0.16f);   // pull-out collapse during the drag
                Render();
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
            if (bmp == null) { ShowToast(Lang.T("这张图读不出来", "This image could not be read")); Render(); return; }
            bmp = ImageIO.Fit(bmp, ImageIO.MaxDim);
            StoreItem it = _store.AddCore(bmp, ImageIO.ExtFor(bmp));
            _enterT0[it] = DateTime.Now;
            FollowNewest();                       // 视口跟到最新那张（设置里可关）
            _hover = -1; _enlarged = -1;
            ShowToast(Lang.T("已加入 1 张图片", "Added 1 image"));
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
                    if (_collapsed && _settings.ShowBalloon) Err.Notify(Lang.T("已从剪贴板收进 1 张图", "Collected 1 image from the clipboard"));
                    else ShowToast(Lang.T("已从剪贴板收进 1 张图", "Collected 1 image from the clipboard"));
                    if (Visible) Render();
                }
            }
            catch { }
        }

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
            if (ok > 0) ShowToast(Lang.T("已加入 ", "Added ") + ok + Lang.T(" 张图片", " image(s)") + (bad > 0 ? "（" + bad + Lang.T(" 张读不了）", " unreadable)") : ""));
            else ShowToast(Lang.T("这些文件读不出图片", "None of these files could be read as images"));
            Render();
        }

    }
}
