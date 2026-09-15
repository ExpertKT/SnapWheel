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
    static class Err
    {
        static readonly object _lock = new object();
        static DateTime _last = DateTime.MinValue;

        // 日志上限：超了就转存成 error.log.1（只留一代，上一代直接删）。
        // 之前是只增不减 —— [Frame] 慢帧诊断每 10 秒就可能写一行，挂久了日志能涨到几 MB，
        // 真出问题时反而不好翻。512KB 足够装下最近几百条，翻的时候一眼看到头。
        public static long MaxBytes = 512 * 1024;

        // 测试用：把日志指到临时文件（null = 正常的 %APPDATA%\SnapWheel\error.log）。
        // 否则跑一次 -Test，[Frame] 这些诊断行会混进用户真实日志里，
        // 以后分析"慢半拍"时分不清哪些是测试造出来的。
        public static string OverridePath = null;

        public static string LogPath()
        {
            if (OverridePath != null) return OverridePath;
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapWheel");
            try { Directory.CreateDirectory(d); } catch { }
            return Path.Combine(d, "error.log");
        }

        // 超过上限就把当前日志挪成 .1（新的一代从空文件重新开始）
        static void RotateIfNeeded(string path)
        {
            try
            {
                if (MaxBytes <= 0) return;
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;
                string old = path + ".1";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                File.Move(path, old);
            }
            catch { }
        }

        public static void Log(string where, Exception ex)
        {
            try
            {
                lock (_lock)
                {
                    string s = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]  " +
                               (ex == null ? "(null)" : ex.GetType().Name + ": " + ex.Message) + "\r\n" +
                               (ex == null ? "" : ex.StackTrace) + "\r\n\r\n";
                    string p = LogPath();
                    RotateIfNeeded(p);
                    File.AppendAllText(p, s, Encoding.UTF8);
                }
            }
            catch { }
            try
            {
                if (Notify != null && ShouldNotify())
                    Notify(where + "：" + (ex == null ? "未知错误" : ex.Message));
            }
            catch { }
        }

        public static Action<string> Notify;      // 由 AppCtx 挂上气泡提示

        // 不抛异常、不弹气泡，只往日志里记一段（给性能报告这类"不是错误"的诊断用）
        public static void Note(string where, string text)
        {
            try
            {
                lock (_lock)
                {
                    string p = LogPath();
                    RotateIfNeeded(p);
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]  " +
                        text + "\r\n\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        // 同一个地方短期内只提示一次，避免刷屏
        public static bool ShouldNotify()
        {
            DateTime now = DateTime.Now;
            if ((now - _last).TotalSeconds < 30) return false;
            _last = now;
            return true;
        }
    }

    // 分段耗时：想知道"这一帧的 20ms 花在哪"，就得把一帧拆开计时。
    // 默认关（用户机器上不该为诊断付代价）：跑基准时设环境变量 SNAPWHEEL_PERF=1 打开。
    // 用法：using (Perf.Section("环")) { ... } —— 关着时就是一个布尔判断，几乎零成本。
    static class Perf
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("SNAPWHEEL_PERF") == "1";

        static readonly object _lock = new object();
        static readonly Dictionary<string, double> _sum = new Dictionary<string, double>();
        static readonly Dictionary<string, int> _cnt = new Dictionary<string, int>();
        static readonly List<string> _order = new List<string>();

        public struct Scope : IDisposable
        {
            readonly string _name;
            readonly long _t0;
            public Scope(string name)
            {
                _name = name;
                _t0 = On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            }
            public void Dispose()
            {
                if (!On) return;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                Add(_name, (now - _t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
        }

        public static Scope Section(string name) { return new Scope(name); }

        static void Add(string n, double ms)
        {
            lock (_lock)
            {
                if (!_sum.ContainsKey(n)) { _sum[n] = 0; _cnt[n] = 0; _order.Add(n); }
                _sum[n] += ms; _cnt[n]++;
            }
        }

        public static void Reset()
        {
            lock (_lock) { _sum.Clear(); _cnt.Clear(); _order.Clear(); }
        }

        // 把每个分段平均多少次写进日志（跑完基准调一次）
        public static void Report(string title)
        {
            if (!On) return;
            lock (_lock)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("分段耗时 — ").Append(title).Append("\r\n");
                for (int i = 0; i < _order.Count; i++)
                {
                    string n = _order[i];
                    sb.Append("  ").Append(n.PadRight(14))
                      .Append((_sum[n] / _cnt[n]).ToString("0.00")).Append("ms   ×").Append(_cnt[n]).Append("\r\n");
                }
                Err.Note("Perf", sb.ToString());
            }
        }
    }

    static class FrameStats
    {
        public const double SlowMs = 25.0;
        const int MaxLines = 60;               // 一次运行最多写 60 行，避免日志失控

        static readonly object _lock = new object();
        static long _frames, _slow;
        static double _sum, _max;
        static string _maxState = "";
        static DateTime _windowStart = DateTime.Now;
        static int _lines;

        public static void Sample(double ms, string state)
        {
            try
            {
                lock (_lock)
                {
                    _frames++; _sum += ms;
                    if (ms > SlowMs)
                    {
                        _slow++;
                        if (ms > _max) { _max = ms; _maxState = state; }
                    }
                    if ((DateTime.Now - _windowStart).TotalSeconds >= 10) FlushLocked();
                }
            }
            catch { }
        }

        // 每 10 秒结算一次：这 10 秒内有慢帧才写一行
        static void FlushLocked()
        {
            if (_slow > 0 && _lines < MaxLines)
            {
                _lines++;
                try
                {
                    Err.Log("Frame", new Exception(
                        "最近10秒 " + _frames + " 帧，慢帧(>" + (int)SlowMs + "ms) " + _slow +
                        " 帧，平均 " + (_frames > 0 ? (_sum / _frames).ToString("0.0") : "0.0") + "ms，最慢 " +
                        _max.ToString("0.0") + "ms | 最慢帧状态: " + _maxState));
                }
                catch { }
            }
            _frames = 0; _slow = 0; _sum = 0; _max = 0; _maxState = ""; _windowStart = DateTime.Now;
        }
    }
}
