using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace SnapWheel
{
    // 重启之后，环上的格子还回不回得来（1.3.0 加了文字格和文件格之后才有的问题）。
    //
    // 为什么必须单独盯这一条：`90-App.cs:36` 的 LoadImagesFromDisk 是**按目录扫文件名前缀**
    // 重建环的，所以"哪些格子能回来"只由文件名前缀决定。图片格（snap_*）一直有人扫；
    // 1.3.0 的文字格（text_*）当时没人扫 —— 于是那一格重启就没了，而 text_*.txt 还占着盘，
    // 没有任何代码路径能再删掉它（界面里已经没有东西指着它了）。
    // 一个套件级的教训：**「落盘」和「回读」是两件事，只测前者等于没测**。
    // （store-kind-test 里原来那条"落盘回读"测的是 takes 那一列，不是格子。）
    //
    // 【本版已修】文件格那半：副本原来用的是**用户原名**（报告.pdf），环目录里认不出哪一份是
    // 复制进来的、哪一份是用户自己放进去的 —— 认错了就会把用户的文件当自己的删掉。
    // 现在 CopyIn 落的副本一律带前缀（`file_<时间戳>_<原名>`），重启只认领带前缀的；
    // 用户自己放进目录的文件没有前缀，永远不会被认领，也就不会被撤格子时删掉。
    static class RingRestartTest
    {
        static int pass, fail;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK]   " + name); }
            else { fail++; Console.WriteLine("  [FAIL] " + name + "   " + detail); }
        }

        static void Main()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "swrestart_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            Settings.OverridePath = Path.Combine(tmp, "settings.ini");
            WheelManager.OverrideMetaPath = Path.Combine(tmp, "wheels.ini");
            try { Run(tmp); }
            catch (Exception ex) { Check("没有异常跑完", false, ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(tmp, true); } catch { } }
            Console.WriteLine("通过 {0} / 失败 {1}", pass, fail);
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        static void Run(string tmp)
        {
            Settings s = Settings.Load();
            s.Dir = Path.Combine(tmp, "rings");
            s.SaveToDisk = true;
            s.MoveInOnDrop = false;          // 只测"留一份"那条路，不碰回收站
            WheelManager.OverrideMetaPath = Path.Combine(tmp, "wheels.ini");

            string orig = Path.Combine(tmp, "报告.pdf");
            File.WriteAllText(orig, "%PDF-1.4 假装是个 pdf");
            string words = "这是一段拖进来的文字，重启之后它还应该在环上。";

            WheelManager m = new WheelManager(s);
            Store st = m.Wheels[0].Store;
            st.Add(new Bitmap(8, 8));
            StoreItem txt = st.AddText(words);
            string note;
            StoreItem fil = st.AddFile(orig, false, out note);
            Check("拖进来三格：图 / 文字 / 文件", st.Items.Count == 3, "Items=" + st.Items.Count);
            if (txt == null) { Check("文字格建出来了", false, "AddText 返回 null"); return; }
            string textPath = txt.FilePath;
            string copyPath = fil == null ? null : fil.FilePath;

            // ---- 重启（跟 90-App.cs:36 那一行一模一样）
            WheelManager m2 = new WheelManager(s);
            m2.LoadImagesFromDisk();
            Store st2 = m2.Wheels[0].Store;

            List<string> kinds = new List<string>();
            foreach (StoreItem it in st2.Items) kinds.Add(it.Kind.ToString());
            Console.WriteLine("      重启后环上：" + string.Join(" / ", kinds.ToArray()));

            StoreItem back = null;
            foreach (StoreItem it in st2.Items) if (it.Kind == CellKind.Text) back = it;

            Check("文字格重启后回来了", back != null,
                  "环上 " + st2.Items.Count + " 格：" + string.Join("/", kinds.ToArray()));
            Check("回来的是同一段字（自己写的 BOM 被吃掉了）",
                  back != null && back.Text == words, back == null ? "没有文字格" : "[" + back.Text + "]");
            Check("名字跟进来时一样（Title 是从正文算的）",
                  back != null && back.Name == txt.Name,
                  (back == null ? "?" : back.Name) + " / " + txt.Name);
            Check("指向的还是盘上那个 text_*.txt",
                  back != null && textPath != null &&
                  string.Equals(back.FilePath, textPath, StringComparison.OrdinalIgnoreCase),
                  back == null ? "?" : back.FilePath);
            Check("认得出是自己写的（Owned = true，撤格子时该跟着删）",
                  back != null && back.Owned, back == null ? "?" : back.Owned.ToString());

            if (back != null) st2.DropFile(back);
            Check("撤掉文字格之后盘上那份 text_*.txt 也清掉了（不再留孤儿）",
                  textPath != null && !File.Exists(textPath), "文件还在：" + textPath);

            Console.WriteLine("      [已知] 副本名 = " + Path.GetFileName(copyPath) +
                              "；原件还在原地 = " + File.Exists(orig));

            StoreItem backFile = null;
            foreach (StoreItem it in st2.Items) if (it.Kind == CellKind.File) backFile = it;

            Check("文件格重启后也回来了（回读跟落盘是一对）", backFile != null,
                  "环上 " + st2.Items.Count + " 格：" + string.Join("/", kinds.ToArray()));
            Check("回来的文件格指向盘上那份副本",
                  backFile != null && copyPath != null &&
                  string.Equals(backFile.FilePath, copyPath, StringComparison.OrdinalIgnoreCase),
                  backFile == null ? "?" : backFile.FilePath);
            Check("名字是用户原来的文件名（不是 file_<时间戳>_ 那一串）",
                  backFile != null && backFile.Name == "报告.pdf",
                  backFile == null ? "?" : backFile.Name);
            Check("认得出是自己复制的（Owned = true，撤格子时该跟着删）",
                  backFile != null && backFile.Owned, backFile == null ? "?" : backFile.Owned.ToString());

            if (backFile != null) st2.DropFile(backFile);
            Check("撤掉文件格：副本清了、用户的原文件没动",
                  copyPath != null && !File.Exists(copyPath) && File.Exists(orig),
                  "副本还在 = " + File.Exists(copyPath) + "，原件还在 = " + File.Exists(orig));

            // 用户自己往环目录里放的文件（没有 file_ 前缀）不许被认领 —— 认领就等于以后会删它
            string mine = Path.Combine(Path.GetDirectoryName(copyPath), "我自己放的.txt");
            File.WriteAllText(mine, "这不是环复制的，撤格子不许碰它");
            WheelManager m3 = new WheelManager(s);
            m3.LoadImagesFromDisk();
            Check("用户自己放进目录的文件不会被当成环的格子",
                  !Contains(m3.Wheels[0].Store, mine), "被认领了：" + mine);
            m3.Wheels[0].Store.ClearOwnedFiles();
            Check("清空环自有文件之后，用户自己那个文件还在",
                  File.Exists(mine), "被删了：" + mine);
        }

        static bool Contains(Store st, string path)
        {
            if (path == null) return false;
            foreach (StoreItem it in st.Items)
                if (string.Equals(it.FilePath, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
