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
    // 一格是什么种类。1.3.0 起环上不再是"一格一张图"：拖什么进来，那一格就是什么（DIRECTIONS §4.1）。
    // Image = 截图 / 拖进来的图；Text = 拖进来的一段文字；File = 拖进来的一个文件（图片以外）。
    enum CellKind { Image, Text, File }

    class StoreItem
    {
        public CellKind Kind = CellKind.Image;
        // 图格：这张图。文字格 / 文件格没有位图（画的时候按 Kind 现画）——
        // 想拿"这格的位图"之前先看 Kind，别直接 Image.Width（那些地方全在 60/61/64/90 里，见 DIRECTIONS §4.1）。
        public Bitmap Image;
        public string FilePath;   // 拖出去 / 落盘用的那个文件（文字格 = 落到盘上的 .txt）
        public string Text = "";  // 文字格的内容
        public string Name = "";  // 文件格 / 文字格显示用的名字
        // 这一格的 FilePath 是不是**环自己的文件**（在环目录里，我们自己复制/写出来的）：
        // 只有 Owned 的才允许删。引用来的（拖进来的原文件、「移进来」没成功时引用的那个）
        // 绝不能动 —— 删格子时顺着 FilePath 删下去，删掉的就是用户的原件（真丢过数据）。
        public bool Owned;
    }

    class Store
    {
        public readonly List<StoreItem> Items = new List<StoreItem>();
        public bool SaveToDisk = false;
        public string Dir = "";            // 环自己的目录：截图、文字格、移进来的文件都放这儿
        public bool MoveInOnDrop = false;  // 拖进来的文件：「留一份」(false) / 「移进来」(true)
        public int MaxCount = 50;
        public int MaxTextLen = 5000;      // 文字格最多收这么多字（别把一整本书拖进来）
        int _seq = 0;

        public StoreItem Add(Bitmap bmp) { return AddCore(bmp, ".png"); }

        public StoreItem AddCore(Bitmap bmp, string ext)
        {
            StoreItem it = new StoreItem();
            // 存自己的副本：调用方（测试 / 截图流程 / 剪贴板）之后释放原图都不该影响轮盘，
            // 否则会拿着一个"已释放的 Image"去读宽高 -> ArgumentException
            try { it.Image = new Bitmap(bmp); } catch { it.Image = bmp; }
            if (SaveToDisk && Dir.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    string f = Path.Combine(Dir, "snap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + (_seq++) + ext);
                    ImageIO.SaveAs(bmp, f);
                    it.FilePath = f;
                    it.Owned = true;
                }
                catch { }
            }
            Items.Add(it);
            Trim();
            return it;
        }

        // 从外部文件导入：解码 -> 落盘 -> 入列。失败返回 null（不抛）
        public StoreItem Import(string path)
        {
            Bitmap b = ImageIO.Load(path);
            if (b == null) return null;
            try { return AddCore(b, ImageIO.ExtFor(b)); }
            catch { try { b.Dispose(); } catch { } return null; }
        }

        // 拖进来的一个文件：能当图读就当图（1.2 的老行为），读不出来就当一个**文件格**。
        // 1.2 是直接报「这些文件读不出图片」把 PDF / zip / 文档全挡在门外，这一版不再挡。
        public StoreItem ImportDropped(string path, bool moveIn, out string note)
        {
            note = "";
            Bitmap b = ImageIO.Load(path);
            if (b != null)
            {
                try { return AddCore(b, ImageIO.ExtFor(b)); }
                catch { try { b.Dispose(); } catch { } return null; }
            }
            return AddFile(path, moveIn, out note);
        }

        // 文字格：内容就是那段文字（1.3.0 只收用户主动拖进来的，剪贴板文字留给 1.8 文字环）
        public StoreItem AddText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (text.Length > MaxTextLen) text = text.Substring(0, MaxTextLen);
            StoreItem it = new StoreItem();
            it.Kind = CellKind.Text;
            it.Text = text;
            it.Name = Title(text);
            WriteText(it);
            Items.Add(it);
            Trim();
            return it;
        }

        // 重启时把 WriteText 落下的 text_*.txt 读回成文字格（30-Wheel 的 LoadImagesFromDisk 调它）。
        // 不带 Trim()：加载是"把已经在那里的东西摆回来"，中途 Trim 会把刚读回来的前面几格挤掉。
        // 文件是环自己写的（前缀 text_ 只有 WriteText 会产出），所以 Owned = true。
        public StoreItem AddTextFromDisk(string path)
        {
            try
            {
                string text = File.ReadAllText(path);      // 自己写过 BOM，这里自动认
                if (string.IsNullOrEmpty(text)) return null;
                if (text.Length > MaxTextLen) text = text.Substring(0, MaxTextLen);
                StoreItem it = new StoreItem();
                it.Kind = CellKind.Text;
                it.Text = text;
                it.Name = Title(text);
                it.FilePath = path;
                it.Owned = true;
                Items.Add(it);
                return it;
            }
            catch { return null; }
        }

        // 重启时把 CopyIn 落下的 file_*_原名 读回成文件格（30-Wheel 的 LoadImagesFromDisk 调它）。
        // 显示名用**用户原来的文件名**，界面上不该出现 file_20260928_... 那一串。
        // 只认前缀形状对得上的（OriginalNameOfCopy 认不出就返回 null）：这份是环自己复制进来的，
        // 所以 Owned = true；用户自己放进目录的文件没有前缀，永远不会被这里认领、也就不会被删。
        public StoreItem AddFileFromDisk(string path)
        {
            try
            {
                string orig = OriginalNameOfCopy(path);
                if (string.IsNullOrEmpty(orig) || !File.Exists(path)) return null;
                StoreItem it = new StoreItem();
                it.Kind = CellKind.File;
                it.Name = orig;
                it.FilePath = path;
                it.Owned = true;
                Items.Add(it);
                return it;
            }
            catch { return null; }
        }

        // 文件格。moveIn=true 时「移进来」：**先复制进环自己的目录，再把原文件送回收站**。
        // 顺序不能反 —— 复制失败（磁盘满 / 没权限）时，原文件必须还在。
        public StoreItem AddFile(string path, bool moveIn, out string note)
        {
            note = "";
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            StoreItem it = new StoreItem();
            it.Kind = CellKind.File;
            it.Name = Path.GetFileName(path);

            string own = CopyIn(path);
            if (own == null)
            {
                // 环还没有自己的目录（设置里存盘关掉 / 没填目录）：只能引用原文件，那就**一定不去动它**
                it.FilePath = path;
                note = moveIn
                    ? Lang.T("环还没有自己的目录，这一格先引用原文件（原文件没动）", "The ring has no folder of its own, so this cell references the original (which was left alone)")
                    : Lang.T("环还没有自己的目录，这一格引用原文件", "The ring has no folder of its own, so this cell references the original");
                Items.Add(it);
                Trim();
                return it;
            }

            it.FilePath = own;
            it.Owned = true;   // 副本在环目录里，是我们自己的
            if (!moveIn)
            {
                note = Lang.T("已留一份到环自己的目录（要从原地移走：设置第 1 页勾「移进来」）",
                              "Kept a copy in the ring's own folder (to move the original in: tick \"Move dropped files in\" on page 1 of Settings)");
            }
            else
            {
                string err;
                if (Recycle.Send(path, out err))
                {
                    note = Lang.T("已移进来（原文件在回收站里，可恢复）", "Moved in (the original is in the Recycle Bin and can be restored)");
                }
                else
                {
                    // 拿不到回收站：**保留这一份副本**（格子依然指向环目录里的副本）。
                    // 这里原来是"把副本删掉、改成引用原文件" —— 那样删格子时就会顺着 FilePath
                    // 把用户的原件永久删掉（回收站里也找不回来），实测就这么丢过文件。
                    // 原件本来就没动，留着副本最多是多占一份空间，比丢数据强得多。
                    Err.Note("Import", "送回收站失败，原件没动，环里留了一份副本：" + path + " → " + own +
                                       "  原因：" + (string.IsNullOrEmpty(err) ? "(没给出原因)" : err));
                    // 原因照抄给用户（他要能一眼看到为什么、也能把它发回来），但掐一下长度：
                    // 提示条再宽也只能占一屏，超长的原文留在 error.log 里。
                    string why = (err ?? "").Trim();
                    if (why.Length > 60) why = why.Substring(0, 60) + "…";
                    note = Lang.T("原件没动 —— 环里留了一份副本（" + why + "）",
                                  "The original was left alone - a copy was kept in the ring (" + why + ")");
                }
            }
            Items.Add(it);
            Trim();
            return it;
        }

        // 从环里撤掉一格：**只有环自己的文件才删**。引用来的（用户的原件）一概不动 ——
        // 这里是整个程序里唯一删文件的地方，60-WheelForm 的单删和批量删都必须走它。
        // 返回 true = 真删掉了一个我们自己的文件。
        public bool DropFile(StoreItem it)
        {
            if (it == null || !it.Owned) return false;
            string p = it.FilePath;
            if (string.IsNullOrEmpty(p)) return false;
            try
            {
                if (!File.Exists(p)) return false;
                File.Delete(p);
                return true;
            }
            catch (Exception ex) { Err.Log("DropFile", ex); return false; }
        }

        // 撤掉整个环之前调用：只删**我们自己写出来的**文件，引用来的原件一概不动。
        // 走的就是 DropFile 那道闸 —— 别在别处再写一遍 File.Delete。
        public void ClearOwnedFiles()
        {
            for (int i = 0; i < Items.Count; i++) DropFile(Items[i]);
        }

        public string EnsureFile(StoreItem it)
        {
            if (it == null) return null;
            if (it.Kind == CellKind.File)
                return (it.FilePath != null && File.Exists(it.FilePath)) ? it.FilePath : null;
            if (it.Kind == CellKind.Text)
            {
                if (it.FilePath != null && File.Exists(it.FilePath)) return it.FilePath;
                try
                {
                    // 临时文件不记进 FilePath：那不是"这一格东西的家"
                    string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_" + Guid.NewGuid().ToString("N") + ".txt");
                    File.WriteAllText(tmp, it.Text, new UTF8Encoding(true));
                    return tmp;
                }
                catch { return null; }
            }
            if (it.Image == null) return null;
            if (it.FilePath != null && File.Exists(it.FilePath)) return it.FilePath;
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_" + Guid.NewGuid().ToString("N") + ".png");
                it.Image.Save(tmp, ImageFormat.Png);
                it.FilePath = tmp;
                it.Owned = true;   // 这个临时文件也是我们写的，撤格子时该跟着删
                return tmp;
            }
            catch { return null; }
        }

        // 文字格也落一份到盘上：跟图片格一样，"这一格东西在哪"永远有答案（拖出去 / 拖进别的程序都用它）
        void WriteText(StoreItem it)
        {
            if (!SaveToDisk || Dir.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Dir);
                string f = Path.Combine(Dir, "text_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + (_seq++) + ".txt");
                // 带 BOM：Win7 记事本不认无 BOM 的 UTF-8（会显示乱码）
                File.WriteAllText(f, it.Text, new UTF8Encoding(true));
                it.FilePath = f;
                it.Owned = true;
            }
            catch { }
        }

        // 复制进环自己的目录，返回副本路径；没有目录 / 复制失败返回 null。
        // 副本名 = file_<19位时间戳>_<用户原名>：前缀是"这份是环自己复制的"的唯一标记，
        // 重启时靠它把文件格认领回来（不加标记就分不清哪份是复制进来的、哪份是用户自己放进
        // 目录的 —— 认错了就会把用户的文件当自己的删掉，见 tests\ring-restart-test.cs）。
        // 同名不覆盖（覆盖别人的东西是不可逆的错；时间戳到毫秒，撞上再加 -2 / -3）。
        string CopyIn(string path)
        {
            // 跟 WriteText / AddCore 一个规矩：存盘关掉就当"环没有自己的目录"。
            // 原来这里只看 Dir.Length，于是 SaveToDisk=false 的测试（ui-shot）也会往用户真实的
            // 环目录里塞文件 —— 我自己就被这个坑过一次（往用户的 1\ 里留了 14 个种子文件）。
            if (!SaveToDisk || Dir.Length == 0) return null;
            try
            {
                Directory.CreateDirectory(Dir);
                string name = Path.GetFileName(path);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string dst = CopyName(stamp, name, 0);
                int n = 2;
                while (File.Exists(dst)) dst = CopyName(stamp, name, n++);
                File.Copy(path, dst, false);
                return dst;
            }
            catch { return null; }
        }

        internal const string CopyPrefix = "file_";

        // 副本名的形状：file_ + 19 位时间戳 +（可选 "-序号"）+ "_" + 原文件名
        string CopyName(string stamp, string name, int dup)
        {
            return Path.Combine(Dir, CopyPrefix + stamp + (dup > 1 ? "-" + dup : "") + "_" + name);
        }

        // 从环目录里的副本名反推用户原来的文件名；不是我们复制的（没有前缀 / 形状不对）返回 null。
        internal static string OriginalNameOfCopy(string copyPath)
        {
            string n = Path.GetFileName(copyPath);
            if (string.IsNullOrEmpty(n) || !n.StartsWith(CopyPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            string rest = n.Substring(CopyPrefix.Length);
            if (rest.Length < 21) return null;                  // 时间戳 19 位 + '_' + 至少 1 个字
            int i = 19;
            if (rest[i] == '-')                                 // 撞名时的时间戳后缀，跳过去
            {
                int j = i + 1;
                while (j < rest.Length && rest[j] >= '0' && rest[j] <= '9') j++;
                if (j >= rest.Length || rest[j] != '_') return null;
                return rest.Substring(j + 1);
            }
            return rest[i] == '_' ? rest.Substring(i + 1) : null;
        }

        static string Title(string text)
        {
            int i = 0;
            while (i < text.Length && (text[i] == '\r' || text[i] == '\n' || text[i] == ' ' || text[i] == '\t')) i++;
            int j = i;
            while (j < text.Length && text[j] != '\r' && text[j] != '\n') j++;
            string line = text.Substring(i, j - i).Trim();
            if (line.Length == 0) line = text.Trim();
            if (line.Length > 40) line = line.Substring(0, 40) + "…";
            return line;
        }

        void Trim()
        {
            while (Items.Count > MaxCount && Items.Count > 0) Items.RemoveAt(0);
        }
    }
}
