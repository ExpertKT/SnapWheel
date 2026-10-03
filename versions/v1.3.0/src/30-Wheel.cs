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
    static class Palette
    {
        public static readonly string[] Names = { Lang.T("蓝", "Blue"), Lang.T("红", "Red"), Lang.T("琥珀", "Amber"), Lang.T("绿", "Green"), Lang.T("紫", "Purple"), Lang.T("青", "Teal"), Lang.T("橙", "Orange"), Lang.T("灰", "Grey") };
        public static readonly Color[] Colors = {
            Color.FromArgb(0, 122, 204),
            Color.FromArgb(232, 86, 110),
            Color.FromArgb(247, 166, 35),
            Color.FromArgb(46, 184, 114),
            Color.FromArgb(139, 108, 240),
            Color.FromArgb(0, 176, 185),
            Color.FromArgb(236, 120, 60),
            Color.FromArgb(120, 132, 150)
        };
        public static Color Get(int i)
        {
            int n = Colors.Length;
            int k = i % n; if (k < 0) k += n;
            return Colors[k];
        }
    }

    class Wheel
    {
        public string Id;
        public string Name;
        public int ColorIndex;
        // 一个环声明它**收什么**（§4.2 里那份声明文本的 takes 字段）。
        // 0=什么都收（默认，跟 1.3.0 之前一样）1=只收图片 2=只收文字。
        // 为什么没有"只收文件"：文件本来就是兜底那一种（认不出是图就当文件收），
        // 一个"只收文件"的环在用户脑子里就等于"什么都收"，多一个选项只多一个困惑。
        public int Takes = 0;
        public const int TakesAny = 0, TakesImage = 1, TakesText = 2;
        public Store Store = new Store();
        public Wheel() { Id = Guid.NewGuid().ToString("N").Substring(0, 8); Name = Lang.T("项目", "Project"); ColorIndex = 0; }
        public Color Accent { get { return Palette.Get(ColorIndex); } }

        // 这种格子收不收 —— 收进来之前唯一的判据（拖放、剪贴板自动收纳都问这一个地方）
        public bool Accepts(CellKind k)
        {
            if (Takes == TakesImage) return k == CellKind.Image;
            if (Takes == TakesText) return k == CellKind.Text;
            return true;
        }

        // 界面用语：改名字那个小窗口里给用户看的三档
        public string TakesName()
        {
            if (Takes == TakesImage) return Lang.T("只收图片", "images only");
            if (Takes == TakesText) return Lang.T("只收文字", "text only");
            return Lang.T("什么都收", "anything");
        }

        // 拒收时那句话里的名词：「这个环只收图片」
        public string TakesLabel()
        {
            if (Takes == TakesImage) return Lang.T("图片", "images");
            if (Takes == TakesText) return Lang.T("文字", "text");
            return "";
        }
    }

    class WheelManager
    {
        public readonly List<Wheel> Wheels = new List<Wheel>();
        public int Active = 0;
        Settings _s;

        public WheelManager(Settings s)
        {
            _s = s;
            Load();
            if (Wheels.Count == 0) { New(); }
            if (Active < 0 || Active >= Wheels.Count) Active = 0;
            ApplySettings();
            Save();          // ensure wheels.ini exists (also on first run)
        }

        public Wheel ActiveWheel { get { return Wheels[Active]; } }
        public Store ActiveStore { get { return Wheels[Active].Store; } }
        public Color Accent { get { return Wheels[Active].Accent; } }

        // 测试用：把轮盘清单指到临时路径（null = 正常 %APPDATA%\SnapWheel）。
        // 测试跑一遍不该把用户真实的轮盘列表/图片目录冲掉。
        public static string OverrideMetaPath = null;

        static string MetaPath()
        {
            if (OverrideMetaPath != null) return OverrideMetaPath;
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapWheel");
            try { Directory.CreateDirectory(d); } catch { }
            return Path.Combine(d, "wheels.ini");
        }

        public static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = Lang.T("项目", "Project");
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < bad.Length; i++) name = name.Replace(bad[i], '_');
            return name.Trim();
        }

        public void ApplySettings()
        {
            for (int i = 0; i < Wheels.Count; i++)
            {
                Store st = Wheels[i].Store;
                st.SaveToDisk = _s.SaveToDisk;
                st.MaxCount = _s.MaxCount;
                st.MoveInOnDrop = _s.MoveInOnDrop;
                // 环自己的目录**一直有**（不只是"要存截图"的时候）：1.3.0 起文字格、移进来的文件都放这儿。
                // SaveToDisk 只管截图要不要写进去（见 Store.AddCore）。
                st.Dir = !string.IsNullOrEmpty(_s.Dir)
                    ? Path.Combine(_s.Dir, SafeName(Wheels[i].Name))
                    : "";
            }
        }

        public Wheel New()
        {
            Wheel w = new Wheel();
            w.Name = UnusedName();
            w.ColorIndex = Wheels.Count % Palette.Colors.Length;
            Wheels.Add(w);
            ApplySettings();
            return w;
        }

        // 名字里的序号取「当前没人用」的最小值。
        // 原来是 Wheels.Count + 1 —— 删掉中间一个环再新建就会撞名
        // （三个环 项目1/项目2/项目3，删掉 项目2 之后 count=2，下一个又叫 项目3），
        // 而环的目录名是按显示名生成的：撞名 = 两个环共用同一个目录 = 删一个连带删掉另一个的图。
        public string UnusedName()
        {
            for (int n = 1; ; n++)
            {
                string candidate = Lang.T("项目", "Project") + n;
                bool taken = false;
                for (int j = 0; j < Wheels.Count; j++)
                    if (Wheels[j].Name == candidate) { taken = true; break; }
                if (!taken) return candidate;
            }
        }

        public void Remove(int i)
        {
            if (Wheels.Count <= 1) { Wheels.Clear(); New(); Active = 0; return; }   // never run out of wheels
            Wheel w = Wheels[i];
            // 只删**我们自己写出来的**文件（走 DropFile 那道闸，引用来的原件一概不动），
            // 目录本身等它空了、而且没别的环在用时再删。
            // 为什么不能直接 Directory.Delete(w.Store.Dir, true)：目录名是按显示名生成的，
            // 两个环同名就共用一个目录，递归删会把**另一个环**的图一起删掉（实测过，真丢数据）。
            w.Store.ClearOwnedFiles();
            try
            {
                if (!string.IsNullOrEmpty(w.Store.Dir) && Directory.Exists(w.Store.Dir)
                    && Directory.GetFileSystemEntries(w.Store.Dir).Length == 0
                    && !DirSharedWithOther(w, i))
                    Directory.Delete(w.Store.Dir, false);
            }
            catch { }
            Wheels.RemoveAt(i);
            if (Active >= Wheels.Count) Active = Wheels.Count - 1;
            if (Active < 0) Active = 0;
        }

        // 有没有别的环也在用这个目录（两个环同名就会共用）
        bool DirSharedWithOther(Wheel w, int skip)
        {
            for (int j = 0; j < Wheels.Count; j++)
                if (j != skip && Wheels[j].Store.Dir == w.Store.Dir) return true;
            return false;
        }

        public void Next() { if (Wheels.Count > 0) Active = (Active + 1) % Wheels.Count; }
        public void Prev() { if (Wheels.Count > 0) Active = (Active - 1 + Wheels.Count) % Wheels.Count; }

        // 把 src 的每个设置字段拷到 dst（用于"还原默认设置"）
        public static void CopyInto(Settings src, Settings dst)
        {
            FieldInfo[] fs = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fs.Length; i++)
            {
                try { fs[i].SetValue(dst, fs[i].GetValue(src)); } catch { }
            }
        }

        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("active=" + Active);
                for (int i = 0; i < Wheels.Count; i++)
                    lines.Add("wheel=" + Wheels[i].Id + "|" + Wheels[i].Name + "|" + Wheels[i].ColorIndex + "|" + Wheels[i].Takes);
                File.WriteAllLines(MetaPath(), lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

        void Load()
        {
            try
            {
                string f = MetaPath();
                if (!File.Exists(f)) return;
                foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                {
                    if (line.StartsWith("active=")) { int a; if (int.TryParse(line.Substring(7), out a)) Active = a; }
                    else if (line.StartsWith("wheel="))
                    {
                        string[] p = line.Substring(6).Split('|');
                        if (p.Length < 3) continue;
                        Wheel w = new Wheel();
                        w.Id = p[0];
                        w.Name = p[1];
                        int ci; if (int.TryParse(p[2], out ci)) w.ColorIndex = ci;
                        // 第 4 段是 takes，1.3.0 才有的字段 —— 老的可能只有 3 段，读不到就当"什么都收"
                        if (p.Length > 3) { int tk; if (int.TryParse(p[3], out tk)) w.Takes = tk; }
                        Wheels.Add(w);
                    }
                }
            }
            catch { }
        }

        // load saved shots for every wheel when running in disk mode
        public void LoadImagesFromDisk()
        {
            if (!_s.SaveToDisk || string.IsNullOrEmpty(_s.Dir)) return;
            for (int i = 0; i < Wheels.Count; i++)
            {
                Store st = Wheels[i].Store;
                if (string.IsNullOrEmpty(st.Dir) || !Directory.Exists(st.Dir)) continue;
                List<string> files = new List<string>();
                try
                {
                    files.AddRange(Directory.GetFiles(st.Dir, "snap_*.png"));
                    files.AddRange(Directory.GetFiles(st.Dir, "snap_*.jpg"));
                    // 1.3.0 的文字格也是环自己落的盘（text_*.txt），不回读的话它重启就没了，
                    // 而文件还占着 —— 界面永远清不掉它（这一条原来只在"已知缺口"外面）。
                    files.AddRange(Directory.GetFiles(st.Dir, "text_*.txt"));
                    // 文件格的副本（CopyIn 落下的 file_<时间戳>_<原名>）。只扫这个前缀：
                    // 用户自己放进目录的文件没有前缀，认领它就是拿用户的东西当自己的（会删掉它）。
                    files.AddRange(Directory.GetFiles(st.Dir, Store.CopyPrefix + "*"));
                }
                catch { }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                for (int k = 0; k < files.Count; k++)
                {
                    try
                    {
                        if (Path.GetFileName(files[k]).StartsWith("text_", StringComparison.OrdinalIgnoreCase))
                        {
                            st.AddTextFromDisk(files[k]);      // 文字格：内容在文件里，不用加载器
                            continue;
                        }
                        if (Path.GetFileName(files[k]).StartsWith(Store.CopyPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            st.AddFileFromDisk(files[k]);      // 文件格：名字就是它，不用读内容
                            continue;
                        }
                        Bitmap b = ImageIO.Load(files[k]);     // 走统一加载器，ico/jpeg/…都能回读
                        if (b == null) continue;
                        StoreItem it = new StoreItem();
                        it.Image = b;
                        it.FilePath = files[k];
                        it.Owned = true;   // 这些 snap_* 是环自己写的，撤格子时该跟着删
                        st.Items.Add(it);
                    }
                    catch { }
                }
                while (st.Items.Count > st.MaxCount) st.Items.RemoveAt(0);
            }
        }
    }
}
