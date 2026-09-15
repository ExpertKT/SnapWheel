using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace SnapWheel
{
    // 后悔药：删掉 / 清空的图，能一键撤回。
    //
    // 为什么不做"回收站"：
    //   回收站要建目录、要维护索引、要加一个管理窗口，还得考虑"留多久 / 留多少张"。
    //   而删除本来就是个手一抖的动作 —— 要的只是"刚删错，马上能找回来"。
    //   所以这里只在**工作内存**里多留几个引用（图本来就在轮盘内存里，不额外解码、不拷文件），
    //   托盘一句「撤销上一次删除」就放回去。退出程序即清空 —— 这是刻意的：
    //   需要长期保管的东西不该靠"删除"来存着，回收站目录无限长大反而是新问题。
    static class Undo
    {
        public const int MaxBatches = 8;                     // 最多记最近 8 次删除动作
        public const long MaxBytes = 96L * 1024 * 1024;      // 或者最多 96MB 像素，先到先算

        public class Shot
        {
            public Bitmap Image;
            public string FilePath;      // 原来落盘在哪（文件已经删了，这里只作记录/排错）
        }

        class Batch
        {
            public List<Shot> Shots = new List<Shot>();
            public Wheel Wheel;          // 从哪个盘删的（那个盘还在就放回它）
            public string WheelName = "";
            public bool ClearAll;        // true = 整盘清空，false = 删掉某几张
            public long Bytes;
        }

        static readonly List<Batch> _stack = new List<Batch>();

        public static bool CanUndo { get { return _stack.Count > 0; } }
        public static int BatchCount { get { return _stack.Count; } }

        public static int ItemCount
        {
            get { int n = 0; for (int i = 0; i < _stack.Count; i++) n += _stack[i].Shots.Count; return n; }
        }

        // 上一次删除大概是什么（给提示文字用），没有可撤销的返回 ""
        public static string LastDesc
        {
            get
            {
                if (_stack.Count == 0) return "";
                Batch b = _stack[_stack.Count - 1];
                string what = b.ClearAll ? Lang.T("清空的 ", "Cleared ") : "删掉的 ";
                int n = b.Shots.Count;
                return what + n + Lang.T(" 张", " item(s)") + (string.IsNullOrEmpty(b.WheelName) ? "" : "（「" + b.WheelName + "」）");
            }
        }

        // 记一次删除。items 是刚被删掉的那些（图还在内存里，这里只留引用）
        public static void Push(Wheel w, IList<StoreItem> items, bool clearAll)
        {
            try
            {
                if (items == null || items.Count == 0) return;
                Batch b = new Batch();
                b.Wheel = w;
                b.ClearAll = clearAll;
                try { b.WheelName = w != null ? w.Name : ""; } catch { }
                for (int i = 0; i < items.Count; i++)
                {
                    StoreItem it = items[i];
                    if (it == null || it.Image == null) continue;
                    Shot sh = new Shot();
                    sh.Image = it.Image;
                    sh.FilePath = it.FilePath;
                    b.Shots.Add(sh);
                    try { b.Bytes += (long)it.Image.Width * it.Image.Height * 4; } catch { }
                }
                if (b.Shots.Count == 0) return;
                _stack.Add(b);
                Trim();
            }
            catch (Exception ex) { try { Err.Log("Undo.Push", ex); } catch { } }
        }

        static void Trim()
        {
            while (_stack.Count > MaxBatches) _stack.RemoveAt(0);
            long total = 0;
            for (int i = 0; i < _stack.Count; i++) total += _stack[i].Bytes;
            while (_stack.Count > 1 && total > MaxBytes)
            {
                total -= _stack[0].Bytes;
                _stack.RemoveAt(0);
            }
        }

        // 撤回上一次：把图放回轮盘（原盘还在就放回原盘），返回放回去的张数。
        public static int UndoLast(WheelManager mgr, out string wheelName)
        {
            wheelName = "";
            if (_stack.Count == 0) return 0;
            Batch b = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            try
            {
                Wheel target = b.Wheel;
                if (mgr != null && (target == null || !mgr.Wheels.Contains(target))) target = mgr.ActiveWheel;
                if (target == null) return 0;
                wheelName = target.Name;
                int n = 0;
                for (int i = 0; i < b.Shots.Count; i++)
                {
                    try
                    {
                        // Store.Add 自己会拷一份、并在开启落盘时重新存成文件（原文件在删除时已经没了）
                        target.Store.Add(b.Shots[i].Image);
                        n++;
                    }
                    catch (Exception ex) { try { Err.Log("Undo.Add", ex); } catch { } }
                }
                return n;
            }
            catch (Exception ex) { try { Err.Log("Undo.UndoLast", ex); } catch { } return 0; }
        }

        public static void Clear() { _stack.Clear(); }
    }
}
