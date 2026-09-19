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
    static class AppInfo
    {
#if NO_KEY
        public const string Version = "0.2.22";   // 变体：多 Wheel + 框选缩放/锁定（无万能键）★ 0.2 线最终版
#else
        public const string Version = "0.9.8";   // 完整版：把手提示改平滑淡入（原来是 0.98 阈值的硬切）+ 禁缓存遗漏
#endif
        public const string Author = "exper7";
        public const string Name = "SnapWheel";
        public const string CnName = "快照轮环";        // 正式中文名（0.4.7 起）
        public const string Repo = "ExpertKT/SnapWheel";  // 自动更新检查用

        // ---------- 版本比较（全项目唯一一份）----------
        // 为什么必须只有一份：两处都要它 ——
        //   ① 更新检查：「远端这个版本比本地新吗」；
        //   ② 引导窗口：「这条说明的加入版本，比用户上次看过的那版新吗」—— 新的才标【新】。
        // 两处各写一份迟早会不一致（这正是反例 #1「度量与绘制同源」的同一个形状）。
        // 所以放在最底层的 AppInfo 里，Update 也回头来调这里。
        public static int[] ParseVer(string s)
        {
            int[] r = new int[3];
            if (string.IsNullOrEmpty(s)) return r;
            s = s.TrimStart('v', 'V');
            string[] parts = s.Split('.', '-', '+');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                int v = 0;
                int.TryParse(parts[i], System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out v);
                r[i] = v;
            }
            return r;
        }

        /// <summary>a 是不是比 b 新（"0.9.4" 比 "0.9.3" 新 → true）。</summary>
        public static bool IsNewer(string a, string b)
        {
            int[] x = ParseVer(a), y = ParseVer(b);
            for (int i = 0; i < 3; i++)
            {
                if (x[i] != y[i]) return x[i] > y[i];
            }
            return false;
        }
    }

    static class Elev
    {
        static readonly bool _on = Detect();

        // 测试用：强制指定是不是管理员（null = 按真实权限判断）。
        // 不然"管理员模式下会怎样"这段逻辑永远只能在管理员进程里手测。
        public static bool? ForceForTest = null;

        public static bool Is { get { return ForceForTest.HasValue ? ForceForTest.Value : _on; } }

        static bool Detect()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // 用 explorer 拉起自己 → 拿到普通权限（explorer 是 Medium 完整性级别）。
        // 只管启动，退出当前实例由调用方决定（否则弹框还挂在一个正在退出的进程上）。
        public static bool RelaunchNormal()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Application.ExecutablePath + "\""); return true; }
            catch { return false; }
        }
    }
}
