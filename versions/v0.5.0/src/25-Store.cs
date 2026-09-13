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
    class StoreItem
    {
        public Bitmap Image;
        public string FilePath;
    }

    class Store
    {
        public readonly List<StoreItem> Items = new List<StoreItem>();
        public bool SaveToDisk = false;
        public string Dir = "";
        public int MaxCount = 50;
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
                }
                catch { }
            }
            Items.Add(it);
            while (Items.Count > MaxCount && Items.Count > 0) Items.RemoveAt(0);
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

        public string EnsureFile(StoreItem it)
        {
            if (it == null || it.Image == null) return null;
            if (it.FilePath != null && File.Exists(it.FilePath)) return it.FilePath;
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "snapwheel_" + Guid.NewGuid().ToString("N") + ".png");
                it.Image.Save(tmp, ImageFormat.Png);
                it.FilePath = tmp;
                return tmp;
            }
            catch { return null; }
        }
    }
}
