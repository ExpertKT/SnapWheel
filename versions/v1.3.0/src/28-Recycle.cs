using System;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace SnapWheel
{
    // 把文件送进回收站 ——「移进来」要用（见 DIRECTIONS §4.1.2）。
    //
    // 为什么用 Microsoft.VisualBasic：它是 .NET Framework 自带的程序集（不是第三方包），
    // 一行就能拿到"可恢复的删除"，省掉自己写 SHFileOperationW / IFileOperation 那套 COM 互操作。
    // 代价只是 csc 命令行多一个 /r:Microsoft.VisualBasic.dll（和 OCR 用 WinRT 桥接程序集是同一性质）。
    // 已在真 Win7 x86 + .NET 4.0 上实测：Microsoft.VisualBasic 10.0.0.0，送回收站后回收站条目 +1。
    //
    // 刻意**不**把"不是本地固定盘"当成"删掉就行"：网络盘 / 回收站被关掉的卷上，
    // 底层的 FOF_ALLOWUNDO 会静默变成永久删除。那种位置 Available 直接返回 false，
    // 调用方（Store.AddFile）据此**不动原文件**。
    static class Recycle
    {
        public static bool Available(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;
                return new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch { return false; }
        }

        // 送进回收站。返回 false 时 error 里有原因，并且**文件一定还在原地**。
        public static bool Send(string path, out string error)
        {
            error = "";
            if (!File.Exists(path)) { error = Lang.T("文件不在了", "The file is gone"); return false; }
            if (!Available(path))
            {
                error = Lang.T("这个位置拿不到回收站（网络盘，或回收站被关掉的卷）", "No Recycle Bin for that location (network drive, or the Bin is disabled on that volume)");
                return false;
            }
            try
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                if (File.Exists(path)) { error = Lang.T("送回收站失败", "Could not send it to the Recycle Bin"); return false; }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
