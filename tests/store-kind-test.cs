using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SnapWheel
{
    // v1.3.0：一格知道自己是什么（图 / 文字 / 文件）＋ 拖进来的文件「留一份 / 移进来」。
    // 盯的是**盘上真的发生了什么**，不是内存里的字段：留一份时原文件还在；移进来时原文件
    // 消失且回收站条目 +1；拿不到回收站（没有环目录 / 网络盘）时**绝不删原文件**。
    static class StoreKindTest
    {
        static int pass, fail;
        static string tmp;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + "   " + detail); }
        }

        static string Write(string name, string text)
        {
            string p = Path.Combine(tmp, name);
            File.WriteAllText(p, text, new UTF8Encoding(false));
            return p;
        }

        static Store NewStore()
        {
            Store s = new Store();
            s.SaveToDisk = true;
            s.Dir = Path.Combine(tmp, "ring");
            return s;
        }

        // 回收站里有多少件 —— 「移进来」和「永久删除」的区别只有这里看得出来
        static int BinCount()
        {
            object sh = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            object ns = sh.GetType().InvokeMember("Namespace", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { 10 });
            object it = ns.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, ns, null);
            return (int)it.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, it, null);
        }

        static void Main()
        {
            tmp = Path.Combine(Path.GetTempPath(), "swkind_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try { Run(); }
            catch (Exception ex) { Check("没有异常跑完", false, ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(tmp, true); } catch { } }
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        static void Run()
        {
            // ---------- 文字格 ----------
            Store s = NewStore();
            StoreItem t = s.AddText("第一行标题\n第二行内容");
            Check("文字格：种类是文字", t != null && t.Kind == CellKind.Text, t == null ? "AddText 返回 null" : t.Kind.ToString());
            Check("文字格：正文原样", t != null && t.Text == "第一行标题\n第二行内容", t == null ? "" : t.Text);
            Check("文字格：名字取第一行", t != null && t.Name == "第一行标题", t == null ? "" : t.Name);
            Check("文字格：落到盘上是 .txt 且内容一致",
                  t != null && t.FilePath != null && t.FilePath.EndsWith(".txt") && File.Exists(t.FilePath) && File.ReadAllText(t.FilePath, Encoding.UTF8) == t.Text,
                  t == null ? "" : "FilePath=" + t.FilePath);
            Check("文字格：没有位图（画的时候按种类现画）", t != null && t.Image == null, "");

            StringBuilder big = new StringBuilder();
            for (int i = 0; i < 6000; i++) big.Append('字');
            StoreItem b = s.AddText(big.ToString());
            Check("文字格：超长被截到 MaxTextLen", b != null && b.Text.Length == s.MaxTextLen, b == null ? "" : b.Text.Length.ToString());

            string ef = s.EnsureFile(t);
            Check("文字格：EnsureFile 给得出可读的 .txt", ef != null && File.Exists(ef) && File.ReadAllText(ef, Encoding.UTF8) == t.Text, "" + ef);

            // ---------- 文件格：留一份 ----------
            string src = Write("报告-留一份.txt", "hello");
            Store s2 = NewStore();
            string note;
            StoreItem f = s2.AddFile(src, false, out note);
            Check("文件格：种类是文件", f != null && f.Kind == CellKind.File, f == null ? "AddFile 返回 null" : f.Kind.ToString());
            Check("文件格：原名进 Name", f != null && f.Name == "报告-留一份.txt", f == null ? "" : f.Name);
            Check("留一份：原文件还在原地", File.Exists(src), src);
            Check("留一份：副本在环目录里、内容一致", f != null && f.FilePath != src && File.Exists(f.FilePath) && File.ReadAllText(f.FilePath) == "hello", f == null ? "" : f.FilePath);
            Check("留一份：说明不空", note != null && note.Length > 0, note);

            // ---------- 文件格：移进来（原文件进回收站）----------
            string src2 = Write("报告-移进来.txt", "move me");
            Store s3 = NewStore();
            int before = BinCount();
            StoreItem m = s3.AddFile(src2, true, out note);
            int after = BinCount();
            Check("移进来：原文件不在原地了", !File.Exists(src2), src2);
            Check("移进来：进了回收站（条目 +1，不是永久删）", after == before + 1, before + " -> " + after);
            Check("移进来：环里那份内容一致", m != null && m.FilePath != null && File.Exists(m.FilePath) && File.ReadAllText(m.FilePath) == "move me", m == null ? "" : "" + m.FilePath);

            // ---------- 拿不到回收站时绝不删原文件 ----------
            string src3 = Write("报告-没有目录.txt", "keep me");
            Store s4 = new Store();          // Dir 空 = 环还没有自己的目录
            s4.SaveToDisk = false;
            StoreItem r = s4.AddFile(src3, true, out note);
            Check("没有环目录时：原文件没动", File.Exists(src3), src3);
            Check("没有环目录时：这一格引用原文件", r != null && r.FilePath == src3, r == null ? "" : "" + r.FilePath);
            Check("没有环目录时：说明里讲了", note != null && note.Length > 0, note);

            // ---------- 同名不覆盖 ----------
            string src4 = Write("同名.txt", "one");
            Store s5 = NewStore();
            StoreItem a1 = s5.AddFile(src4, false, out note);
            StoreItem a2 = s5.AddFile(src4, false, out note);
            Check("同名第二份不覆盖第一份",
                  a1 != null && a2 != null && a1.FilePath != a2.FilePath && File.Exists(a1.FilePath) && File.Exists(a2.FilePath),
                  (a1 == null ? "?" : a1.FilePath) + " / " + (a2 == null ? "?" : a2.FilePath));

            // ---------- 拖进来的东西：能当图就是图，当不了就是文件格 ----------
            Store s6 = NewStore();
            StoreItem d = s6.ImportDropped(src4, false, out note);
            Check("拖进来的普通文件 -> 文件格（1.2 是直接拒收）", d != null && d.Kind == CellKind.File, d == null ? "null" : d.Kind.ToString());
            string png = Path.Combine(tmp, "图.png");
            using (Bitmap bmp = new Bitmap(20, 10)) bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            StoreItem di = s6.ImportDropped(png, false, out note);
            Check("拖进来的图片 -> 还是图片格", di != null && di.Kind == CellKind.Image && di.Image != null && di.Image.Width == 20, di == null ? "null" : di.Kind.ToString());

            // ---------- 回收站资格 ----------
            Check("回收站：本地盘上的文件算可用", Recycle.Available(src4), src4);
            Check("回收站：不存在的文件算不可用", !Recycle.Available(Path.Combine(tmp, "没这个文件.txt")), "");

            // ---------- 纸格上那几行字怎么排（1.3.0 画层，纯函数）----------
            // 这是"文字格"唯一能在这里钉住的部分：排版是算出来的，不是画出来的。
            // 为什么必须折行、必须收省略号：不折行的话一个字都看不见（16-DrawKit 那条教训），
            // 不收省略号的话用户以为"我就选了这两行"，其实后面还有一大段。
            Labeling();
            FileColors();
            Ownership();
            FileLock();

            // ---------- 一个环收什么（takes）----------
            Takes();
        }

        // 1.3.0 的 takes：环声明自己收什么，拖放 / 剪贴板自动收纳都问 Accepts()。
        // 盯两件事：判据本身；以及改过之后重开还记得（老格式那行也不能读崩）。
        static void Takes()
        {
            Wheel w = new Wheel();
            Check("收什么：默认什么都收",
                  w.Takes == Wheel.TakesAny && w.Accepts(CellKind.Image) && w.Accepts(CellKind.Text) && w.Accepts(CellKind.File),
                  w.TakesName());
            w.Takes = Wheel.TakesImage;
            Check("收什么：只收图片时文字和文件都不收",
                  w.Accepts(CellKind.Image) && !w.Accepts(CellKind.Text) && !w.Accepts(CellKind.File), w.TakesName());
            w.Takes = Wheel.TakesText;
            Check("收什么：只收文字时图片和文件都不收",
                  w.Accepts(CellKind.Text) && !w.Accepts(CellKind.Image) && !w.Accepts(CellKind.File), w.TakesName());

            string meta = Path.Combine(tmp, "wheels.ini");
            string set = Path.Combine(tmp, "settings.ini");
            Settings.OverridePath = set;
            WheelManager.OverrideMetaPath = meta;
            try
            {
                Settings s = new Settings();
                s.Dir = Path.Combine(tmp, "ring");
                WheelManager m = new WheelManager(s);
                m.Wheels[0].Name = "笔记";
                m.Wheels[0].Takes = Wheel.TakesText;
                m.Save();

                WheelManager m2 = new WheelManager(s);
                Check("收什么：改过之后重开还记得（落盘那一列真的写了）",
                      m2.Wheels[0].Takes == Wheel.TakesText && m2.Wheels[0].Accepts(CellKind.Text) && !m2.Wheels[0].Accepts(CellKind.Image),
                      m2.Wheels[0].Takes + " / " + m2.Wheels[0].Name);

                // 1.3.0 之前写的那行只有 3 段（没有 takes）：读回来必须是"什么都收"，轮盘也不能丢
                File.WriteAllLines(meta, new string[] { "active=0", "wheel=deadbeef|老轮盘|1" }, new UTF8Encoding(false));
                WheelManager m3 = new WheelManager(s);
                Check("收什么：老版本那行只有 3 段，读回来是「什么都收」（不炸也不丢轮盘）",
                      m3.Wheels.Count == 1 && m3.Wheels[0].Name == "老轮盘" &&
                      m3.Wheels[0].Accepts(CellKind.Image) && m3.Wheels[0].Accepts(CellKind.Text),
                      m3.Wheels.Count + " 个轮盘");
            }
            finally
            {
                WheelManager.OverrideMetaPath = null;
                Settings.OverridePath = null;
            }
        }

        static void Labeling()
        {
            using (Bitmap bmp = new Bitmap(8, 8))
            using (Graphics g = Graphics.FromImage(bmp))
            using (Font f = new Font("Microsoft YaHei UI", 9f))
            {
                List<string> l1 = WheelForm.CellLines(g, f, "第一行\n第二行", 400f, 5);
                Check("纸格：换行符真的分成两行", l1.Count == 2 && l1[0] == "第一行" && l1[1] == "第二行",
                      string.Join("|", l1.ToArray()));

                string longOne = "这是一段很长的文字用来撑满整行好触发真正的折行处理看看它会不会把字切到框外去";
                List<string> l2 = WheelForm.CellLines(g, f, longOne, 120f, 20);
                bool wider = true;
                using (StringFormat sf = StringFormat.GenericTypographic)
                    for (int i = 0; i < l2.Count; i++)
                        if (g.MeasureString(l2[i], f, new PointF(0, 0), sf).Width > 120f) wider = false;
                Check("纸格：长句折成多行，每行都不超宽", l2.Count > 1 && wider,
                      l2.Count + " 行：" + string.Join("|", l2.ToArray()));

                List<string> l3 = WheelForm.CellLines(g, f, longOne, 120f, 2);
                float lastW = 0f;
                using (StringFormat sf3 = StringFormat.GenericTypographic)
                    if (l3.Count > 0) lastW = g.MeasureString(l3[l3.Count - 1], f, new PointF(0, 0), sf3).Width;
                Check("纸格：框里放不下时最多 maxLines 行、末行收省略号、而且没顶出框外",
                      l3.Count == 2 && l3[1].EndsWith("…") && lastW <= 120f,
                      string.Join("|", l3.ToArray()) + "  末行宽 " + lastW.ToString("0.0"));

                Check("纸格：空文字不炸也一行不画", WheelForm.CellLines(g, f, "", 120f, 3).Count == 0, "");
                Check("纸格：宽度为 0 时宁可一行不画", WheelForm.CellLines(g, f, "字", 0f, 3).Count == 0, "");
            }
        }

        // 文件格的种类色（用户实测反馈：不同文件类型辨识度不高，要颜色差别）。
        // 钉两件事：① 同一类文件给同一个色、不同类给不同色（不然"颜色差别"等于没做）；
        //          ② 认不出来的后缀一律回 PaperSub —— 宁可"就是个文件"，也不要瞎猜一个颜色出来。
        static void FileColors()
        {
            Check("文件格配色：同一类（pdf / docx / txt）同色、大小写无关",
                  WheelForm.FileKindColor("报告.pdf") == WheelForm.FileKindColor("D:\\x\\A.DOCX")
                  && WheelForm.FileKindColor("a.docx") == WheelForm.FileKindColor("b.txt"),
                  WheelForm.FileKindColor("报告.pdf") + " vs " + WheelForm.FileKindColor("A.DOCX"));

            Color[] kinds = new Color[] {
                WheelForm.FileKindColor("a.pdf"), WheelForm.FileKindColor("b.xlsx"),
                WheelForm.FileKindColor("c.zip"), WheelForm.FileKindColor("d.mp3"),
                WheelForm.FileKindColor("e.exe"), WheelForm.FileKindColor("f.ppt")
            };
            bool distinct = true;
            for (int i = 0; i < kinds.Length && distinct; i++)
                for (int j = i + 1; j < kinds.Length; j++)
                    if (kinds[i] == kinds[j]) { distinct = false; break; }
            Check("文件格配色：六类文件颜色两两不同",
                  distinct && kinds[0] != WheelForm.PaperSub, kinds[0] + " " + kinds[1] + " " + kinds[2] + " " + kinds[3] + " " + kinds[4] + " " + kinds[5]);

            Check("文件格配色：认不出来的后缀 / 没后缀 / null 都回纸灰，不瞎猜",
                  WheelForm.FileKindColor("x.qqq") == WheelForm.PaperSub
                  && WheelForm.FileKindColor("没有后缀") == WheelForm.PaperSub
                  && WheelForm.FileKindColor(null) == WheelForm.PaperSub,
                  WheelForm.FileKindColor("x.qqq") + " / " + WheelForm.FileKindColor("没有后缀"));

            Color tinted = WheelForm.Tint(WheelForm.PaperBg, WheelForm.FileKindColor("c.zip"), 0.12f);
            Check("文件格配色：纸底染了种类色（不等于原来的纸，透明度不许动）",
                  tinted != WheelForm.PaperBg && tinted.A == WheelForm.PaperBg.A
                  && WheelForm.Tint(WheelForm.PaperBg, Color.Black, 0f) == WheelForm.PaperBg,
                  tinted + " vs " + WheelForm.PaperBg);
        }

        // 「这一格的文件是不是我们的」—— 这是**唯一**决定"删格子时删不删盘上那个文件"的东西。
        // 实测丢过数据：拿不到回收站时原来会把副本删掉、把格子改回引用原文件，
        // 之后删格子就顺着 FilePath 把用户桌面的原件永久删了（回收站里都找不回来）。
        static void Ownership()
        {
            string src = Write("自有-留一份.txt", "one");
            Store s = NewStore();
            string note;
            StoreItem f = s.AddFile(src, false, out note);
            Check("所有权：复制进来的副本算自己的（删格子时该删它）", f != null && f.Owned, f == null ? "null" : "Owned=" + f.Owned);

            string src2 = Write("别人的原件.txt", "two");
            Store s2 = new Store();              // Dir 空 => 只能引用原文件
            s2.SaveToDisk = false;
            StoreItem r = s2.AddFile(src2, false, out note);
            Check("所有权：引用来的不算自己的", r != null && !r.Owned && r.FilePath == src2, r == null ? "null" : "Owned=" + r.Owned + " " + r.FilePath);

            Check("删格子：引用来的原件**不动**（这就是那次丢数据）",
                  !s2.DropFile(r) && File.Exists(src2), src2);
            Check("删格子：自己的副本删掉", s.DropFile(f) && !File.Exists(f.FilePath), f == null ? "" : f.FilePath);

            // 没存盘时不许往"环目录"里写：CopyIn 必须跟 WriteText / AddCore 一个规矩。
            // 不然测试（ui-shot）会往用户真实的环目录里塞文件 —— 我就干过一次。
            Store s3 = new Store();
            s3.SaveToDisk = false;
            s3.Dir = Path.Combine(tmp, "ring-nosave");
            string src3 = Write("不该被复制.txt", "three");
            StoreItem n = s3.AddFile(src3, false, out note);
            Check("存盘关掉时：不往环目录里拷贝，只引用原文件",
                  n != null && !n.Owned && n.FilePath == src3 && !File.Exists(Path.Combine(s3.Dir, "不该被复制.txt")),
                  n == null ? "null" : "" + n.FilePath);
        }

        // 「移进来」曾经**永远做不到**：我们"试着当图片读一次"时（普通文件都要过这一趟）
        // 把用户的原文件锁住了 —— 紧接着的「送回收站」必然报"另一个程序正在使用此文件"。
        // 根因是 WIC 的 BitmapDecoder.Create(Uri…) 自己开文件流、解码失败时也不关。
        // 这里盯的就是那个锁：读过一次之后，文件必须还能被送进回收站。
        static void FileLock()
        {
            string a = Write("锁-普通文件.txt", "hello");
            Bitmap ba = ImageIO.Load(a);                      // 普通文件：GDI 失败 -> 走 WIC
            if (ba != null) ba.Dispose();
            string err;
            bool moved = Recycle.Send(a, out err);
            Check("读过一次（没读成图）之后，普通文件还能送回收站（「移进来」失败的真凶）",
                  moved && !File.Exists(a), "err=" + err);

            string psrc = Path.Combine(tmp, "锁-真图片.png");
            using (Bitmap b = new Bitmap(16, 16)) b.Save(psrc, System.Drawing.Imaging.ImageFormat.Png);
            Bitmap bb = ImageIO.Load(psrc);
            Check("读过一次真图片：读得出来", bb != null && bb.Width == 16, bb == null ? "null" : "" + bb.Width);
            if (bb != null) bb.Dispose();
            moved = Recycle.Send(psrc, out err);
            Check("读过一次真图片之后，原图还能送回收站", moved && !File.Exists(psrc), "err=" + err);

            // 整条路：拖进来的真实入口（先试着当图片读一次）+ 移进来 = 原件真的进回收站
            string c = Write("锁-整条路.txt", "move me");
            Store s = NewStore();
            string note;
            StoreItem it = s.ImportDropped(c, true, out note);
            Check("整条路：拖进来的普通文件开了「移进来」之后，原件真的没了（进回收站）",
                  it != null && !File.Exists(c) && it.Owned && File.Exists(it.FilePath),
                  it == null ? "null" : "note=" + note + " 原件还在=" + File.Exists(c));
        }
    }
}
