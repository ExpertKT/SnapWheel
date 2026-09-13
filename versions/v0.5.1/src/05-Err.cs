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

        public static void Log(string where, Exception ex)
        {
            try
            {
                lock (_lock)
                {
                    string s = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]  " +
                               (ex == null ? "(null)" : ex.GetType().Name + ": " + ex.Message) + "\r\n" +
                               (ex == null ? "" : ex.StackTrace) + "\r\n\r\n";
                    File.AppendAllText(LogPath(), s, Encoding.UTF8);
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

        // 同一个地方短期内只提示一次，避免刷屏
        public static bool ShouldNotify()
        {
            DateTime now = DateTime.Now;
            if ((now - _last).TotalSeconds < 30) return false;
            _last = now;
            return true;
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
