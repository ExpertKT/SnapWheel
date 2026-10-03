using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SnapWheel
{
    // ==================== 自动更新（0.8.1） ====================
    //
    // 设计目标：**不花一分钱**，也不需要代码签名或自己的服务器。
    //   ① 检查：匿名 GET GitHub 的 releases/latest 接口（限流 60 次/小时/IP，够用）
    //   ② 下载：把 zip 存到 %TEMP%\SnapWheel-update\
    //   ③ 安装：用 `SnapWheel.exe --apply-update <目录>` 启动"更新器模式"的自己 ——
    //      等主进程退出 → 覆盖文件 → 重新启动主程序 → 自己退出
    //
    // 为什么这样能在没有签名的情况下工作：**升级是"覆盖已有文件"，不触发 SmartScreen**；
    // 只有首次下载的全新文件才会提示，而那是用户主动下载的。
    //
    // 失败一律**静默**：国内经常连不上 GitHub，检查更新绝不能变成打扰。
    // 设置里有开关（默认开），托盘菜单里也有手动的"检查更新"。

    static class Update
    {
        public const string ApiUrl = "https://api.github.com/repos/ExpertKT/SnapWheel/releases/latest";
        public const string ReleasesPage = "https://github.com/ExpertKT/SnapWheel/releases/latest";

        public static string UpdateDir { get { return Path.Combine(Path.GetTempPath(), "SnapWheel-update"); } }

        // 找到的更新（null 表示没有新版、或检查失败）
        public class Found
        {
            public string Version;     // 例如 "0.8.1"
            public string ZipUrl;      // 对应 exe 的 zip 下载地址
        }

        // ---------- 比较版本号 ----------
        // 只比较数字部分，容忍 "v" 前缀和 "-beta" 之类的后缀。
        // 实现搬到了 AppInfo —— 引导窗口也要用同一套判断（「这条说明比你看过的那版新吗」），
        // 两处各写一份迟早会不一致。这里保留一个转发：调用点写法不变，逻辑永远只有一份。
        public static bool IsNewer(string remote, string local)
        {
            return AppInfo.IsNewer(remote, local);
        }

        // ---------- 检查 ----------
        // 同步执行（在后台线程里调）。任何异常都返回 null，绝不打扰用户。
        public static Found Check()
        {
            try
            {
                string json = Http(ApiUrl, 15000);
                if (string.IsNullOrEmpty(json)) return null;

                string tag = JsonString(json, "tag_name");
                if (string.IsNullOrEmpty(tag)) return null;
                if (!IsNewer(tag, AppInfo.Version)) return null;

                // 找 assets 里文件名包含 "full" 的 zip（完整版）；没有就退回第一个 zip
                string url = null, fallback = null;
                int idx = 0;
                while (true)
                {
                    int p = json.IndexOf("browser_download_url", idx, StringComparison.Ordinal);
                    if (p < 0) break;
                    string u = JsonStringAt(json, p);
                    idx = p + 20;
                    if (string.IsNullOrEmpty(u)) continue;
                    if (u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fallback == null) fallback = u;
                        if (u.IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0) { url = u; break; }
                    }
                }
                if (url == null) url = fallback;
                if (url == null) return null;

                Found f = new Found();
                f.Version = tag.TrimStart('v', 'V');
                f.ZipUrl = url;
                return f;
            }
            catch { return null; }
        }

        // ---------- 下载并解压 ----------
        // 返回解压后的目录；失败返回 null。
        public static string Download(Found f, Action<int> progress)
        {
            try
            {
                if (Directory.Exists(UpdateDir)) { try { Directory.Delete(UpdateDir, true); } catch { } }
                Directory.CreateDirectory(UpdateDir);
                string zip = Path.Combine(UpdateDir, "update.zip");

                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "SnapWheel/" + AppInfo.Version);
                    if (progress != null)
                    {
                        wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                        {
                            try { progress(e.ProgressPercentage); } catch { }
                        };
                    }
                    wc.DownloadFile(new Uri(f.ZipUrl), zip);
                }

                string outDir = Path.Combine(UpdateDir, "files");
                Directory.CreateDirectory(outDir);
                ZipExtract(zip, outDir);
                try { File.Delete(zip); } catch { }

                // zip 里可能有一层目录，往下找到含 exe 的那一层
                string exeDir = FindExeDir(outDir, 0);
                return exeDir ?? outDir;
            }
            catch { return null; }
        }

        static string FindExeDir(string dir, int depth)
        {
            if (depth > 3 || !Directory.Exists(dir)) return null;
            if (Directory.GetFiles(dir, "SnapWheel*.exe").Length > 0) return dir;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string r = FindExeDir(sub, depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        // ---------- 更新器模式 ----------
        // 以 `--apply-update <目录> <等待的进程id>` 启动自己：主进程退出后覆盖文件并重启。
        public static bool IsApplyMode(string[] args)
        {
            return args != null && args.Length >= 2 && args[0] == "--apply-update";
        }

        public static void RunApply(string[] args)
        {
            string src = args[1];
            int waitPid = 0;
            if (args.Length >= 3) int.TryParse(args[2], out waitPid);

            // 等主进程退出（最多 30 秒）
            if (waitPid > 0)
            {
                try
                {
                    Process p = Process.GetProcessById(waitPid);
                    p.WaitForExit(30000);
                }
                catch { }
            }
            else
            {
                Thread.Sleep(1200);
            }
            Thread.Sleep(400);      // 再给文件系统一点时间

            string me = Assembly.GetEntryAssembly().Location;
            string myDir = Path.GetDirectoryName(me);
            string myName = Path.GetFileName(me);
            int copied = 0;

            try
            {
                foreach (string f in Directory.GetFiles(src))
                {
                    string name = Path.GetFileName(f);
                    string dst = Path.Combine(myDir, name);
                    // 正在运行的自己是锁住的，跳过（更新器自己就是它）
                    if (string.Equals(name, myName, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Copy(f, dst, true); copied++; } catch { }
                }
            }
            catch { }

            // 重启主程序
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(me);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = myDir;
                Process.Start(psi);
            }
            catch { }

            Environment.Exit(copied > 0 ? 0 : 1);
        }

        // ---------- 内置的极简 HTTP + JSON + ZIP ----------
        // 只有几百行的小项目，不值得为此引入第三方库；够用就行。
        public static string Http(string url, int timeoutMs)
        {
            try
            {
                // .NET 4.0 默认只开 TLS 1.0，GitHub 会直接拒绝连接 —— 必须显式开 TLS 1.2。
                // （这条是从项目里原有的检查更新代码学来的，漏了它检查会一直失败。）
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "SnapWheel/" + AppInfo.Version;
                req.Accept = "application/vnd.github+json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch { return null; }
        }

        // 从 json 里取 "key": "value"
        static string JsonString(string json, string key)
        {
            int p = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (p < 0) return null;
            return JsonStringAt(json, p);
        }

        // 从 p（某个 key 或字段名出现的位置）往后找第一个字符串值
        static string JsonStringAt(string json, int p)
        {
            int colon = json.IndexOf(':', p);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'u' && i + 5 < json.Length)
                    {
                        int cp = 0;
                        if (int.TryParse(json.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                            sb.Append((char)cp);
                        i += 4;
                    }
                    else sb.Append(n);
                    i += 2;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // 解压：先试系统自带的 ZipFile（.NET 4.5+，用反射调用所以不需要编译期引用）；
        // 拿不到就用自己的解析器 —— 这一点是测试逼出来的：
        // 项目编译目标是 .NET 4.0，反射 Type.GetType 找不到 System.IO.Compression.FileSystem，
        // 如果没有兜底，自动更新到了用户机器上会直接失败。
        static void ZipExtract(string zip, string outDir)
        {
            try
            {
                Type t = Type.GetType("System.IO.Compression.ZipFile, System.IO.Compression.FileSystem");
                if (t != null)
                {
                    System.Reflection.MethodInfo m = t.GetMethod("ExtractToDirectory", new Type[] { typeof(string), typeof(string) });
                    if (m != null) { m.Invoke(null, new object[] { zip, outDir }); return; }
                }
            }
            catch { }
            ZipExtractManual(zip, outDir);
        }

        // 自己解析 ZIP：读"中央目录"拿到每个条目的准确信息，再用 DeflateStream 解压。
        // 只做"解压"这一个用途，所以不需要支持加密、ZIP64、多卷这些特性 —— 够用就好。
        static void ZipExtractManual(string zip, string outDir)
        {
            byte[] d = File.ReadAllBytes(zip);
            // 从尾部往前找 EOCD（End Of Central Directory）签名 PK\x05\x06
            int eocd = -1;
            int low = Math.Max(0, d.Length - 65558);
            for (int i = d.Length - 22; i >= low; i--)
            {
                if (d[i] == 0x50 && d[i+1] == 0x4b && d[i+2] == 0x05 && d[i+3] == 0x06) { eocd = i; break; }
            }
            if (eocd < 0) throw new Exception("不是有效的 zip 文件");

            int count = U16(d, eocd + 10);
            int cd = (int)U32(d, eocd + 16);
            int p = cd;

            for (int i = 0; i < count; i++)
            {
                if (p + 46 > d.Length) break;
                if (!(d[p] == 0x50 && d[p+1] == 0x4b && d[p+2] == 0x01 && d[p+3] == 0x02)) break;   // PK\x01\x02
                int method  = U16(d, p + 10);
                long csize  = U32(d, p + 20);
                int nlen    = U16(d, p + 28);
                int elen    = U16(d, p + 30);
                int clen    = U16(d, p + 32);
                long lho    = U32(d, p + 42);
                string name = Encoding.UTF8.GetString(d, p + 46, nlen);

                p += 46 + nlen + elen + clen;

                if (string.IsNullOrEmpty(name) || name.EndsWith("/") || name.EndsWith("\\")) continue;

                // 本地文件头里的名字/扩展区长度可能和中央目录不同，必须重新读一遍
                int lnlen = U16(d, (int)lho + 26);
                int lelen = U16(d, (int)lho + 28);
                int ds = (int)lho + 30 + lnlen + lelen;
                if (ds < 0 || ds > d.Length) continue;

                // 防目录穿越：把 .. 和绝对路径都挡掉
                string safe = name.Replace('\\', '/');
                if (safe.StartsWith("/") || safe.Contains("..")) continue;
                string dst = Path.Combine(outDir, safe.Replace('/', Path.DirectorySeparatorChar));
                string dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);

                if (method == 0)      // 存储（未压缩）
                {
                    using (FileStream o = File.Create(dst)) o.Write(d, ds, (int)csize);
                }
                else if (method == 8) // deflate（最常用）
                {
                    using (MemoryStream ms = new MemoryStream(d, ds, (int)csize, false))
                    using (System.IO.Compression.DeflateStream z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
                    using (FileStream o = File.Create(dst))
                        z.CopyTo(o);
                }
                else continue;   // 其它压缩方式不支持（我们的发行包不会用到）
            }
        }

        static int U16(byte[] d, int i)
        {
            if (i < 0 || i + 2 > d.Length) return 0;
            return d[i] | (d[i+1] << 8);
        }
        static uint U32(byte[] d, int i)
        {
            if (i < 0 || i + 4 > d.Length) return 0;
            return (uint)(d[i] | (d[i+1] << 8) | (d[i+2] << 16) | (d[i+3] << 24));
        }
    }
}
