using System;
using System.IO;
using System.Text;

namespace SnapWheel
{
    // 本地使用计数：**只写在本机**，不上传、不联网、不收集内容。
    //
    // 为什么需要它：
    //   这个项目最近几个"往哪走"的方向都是**靠想定的**，然后被真实数据否掉 ——
    //   我提议"把搬运做到极致"，而日志显示传递模式（就是那个方向）用户自己用了 12 次就再没打开过。
    //   与其继续猜第二轮，不如让程序如实记一周，用表说话。
    //
    // 三条硬规矩：
    //   ① **默认关**。要显式打开（托盘右键 →「记录本地使用统计」）才会写一行。
    //   ② 只记**事件名和计数**（"长截图发生了一次"），**不记内容**：不记截图内容、
    //      不记窗口标题、不记文件名、不记你在哪儿用了它。看到这份文件也还原不出你干了什么。
    //   ③ **绝不影响功能**。任何一步失败都静默吞掉 —— 统计是给人看的，不是程序的一部分。
    static class Usage
    {
        public static bool On;                       // 由设置驱动
        public static string OverridePath = null;    // 测试用

        static readonly object _lock = new object();
        static bool _wroteHeader;

        const long MaxBytes = 512 * 1024;            // 超了就轮转成 .1

        public static string Path
        {
            get
            {
                if (!string.IsNullOrEmpty(OverridePath)) return OverridePath;
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return System.IO.Path.Combine(dir, "SnapWheel", "usage-log.tsv");
            }
        }

        /// <summary>记一件事。ev 是事件名（英文、稳定），detail 是可选的一点点上下文（数字为主）。</summary>
        public static void Ev(string ev, string detail)
        {
            if (!On) return;
            try
            {
                lock (_lock)
                {
                    string p = Path;
                    string dir = System.IO.Path.GetDirectoryName(p);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    try { if (File.Exists(p) && new FileInfo(p).Length > MaxBytes) File.Move(p, p + ".1"); }
                    catch { }

                    StringBuilder sb = new StringBuilder();
                    if (!_wroteHeader && !File.Exists(p))
                    {
                        sb.Append("# SnapWheel 本地使用统计（只在本机，不上传）\n");
                        sb.Append("# 格式：时间 \t 事件 \t 细节\n");
                        sb.Append("# 打开/关闭：托盘右键 →「记录本地使用统计」\n");
                        _wroteHeader = true;
                    }
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    sb.Append('\t').Append(ev);
                    sb.Append('\t').Append(detail == null ? "" : detail.Replace('\t', ' ').Replace('\n', ' '));
                    sb.Append('\n');

                    File.AppendAllText(p, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch { }   // 统计绝不能影响功能
        }

        public static void Ev(string ev) { Ev(ev, ""); }
    }
}
