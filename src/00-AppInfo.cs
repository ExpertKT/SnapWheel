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
        public const string Version = "0.9.3";   // 完整版：修工具条图标（A-/A+/撤销）+ 引导与传递模式收尾 + 进稳定期
#endif
        public const string Author = "exper7";
        public const string Name = "SnapWheel";
        public const string CnName = "快照轮环";        // 正式中文名（0.4.7 起）
        public const string Repo = "ExpertKT/SnapWheel";  // 自动更新检查用
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
