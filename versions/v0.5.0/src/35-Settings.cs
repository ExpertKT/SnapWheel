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
    class Settings
    {
        public bool SaveToDisk = false;
        public string Dir = "";
        public int MaxCount = 50;
        public bool AutoHide = false;
        public int AutoHideSeconds = 8;
        public bool ShowWheelOnStart = true;
        public bool AlwaysOnTop = true;
        public string Hotkey = "Ctrl+Shift+S";
        public int ThumbSize = 96;      // nominal thumbnail long side
        public int Radius = 300;        // ring radius from the screen corner
        public int Slots = 5;           // how many cards visible on the arc
        public int LabelSize = 16;      // index label font size (px)
        public string Corner = "BL";    // BL / BR / TL / TR - which screen corner the ring docks to
        public bool AutoStart = false;  // launch at logon (HKCU Run)
        public string DeleteMode = "double";  // "double" right-click to delete, or "single"
        public string SwitchMode = "radial";  // "radial" (万能键圆盘) or "swipe" (长按滑动切换)
        public int PeekPercent = 240;         // 长按放大：百分比（100 = 原大小）
        public bool IntroSeen = false;        // 是否看过新手引导
        public bool IntroAnim = true;         // 启动时播开启动画
        // ---- 外观风格（新拟态 + 扁平化 + 毛玻璃）----
        public string UiStyle = "neu";        // neu=新拟态+毛玻璃(默认) / flat=纯扁平 / solid=高对比不透明
        public int GlassPercent = 40;         // 玻璃面板不透明度 20..100
        public int CardRadius = 14;           // 卡片圆角（占最小边的百分比）0..30
        public int ShadowPercent = 55;        // 阴影强度 0..100
        public int AnimSpeed = 100;           // 动画速度 %（70 慢 / 100 标准 / 140 快）
        public int AccentIndex = -1;          // -1=跟随每个 Wheel 自己的颜色；0..7=全局统一主题色
        public bool ShowNameLabel = true;     // 显示 Wheel 名称药丸
        public bool ShowCountLabel = true;    // 显示图片计数药丸
        public int UiScale = 0;               // 界面缩放 %：0=自动（按显示器 DPI），60..250
        public bool CollapseMode = false;     // 收起态：像贴边小球一样缩到屏幕边上，留个可点的小把手（默认展开）
        public bool ClipboardImport = true;   // 剪贴板里出现图片时自动收进轮盘
        public bool GlassRefresh = true;      // 定时重抓玻璃底，避免轮盘挂久了糊的是旧桌面
        public bool ShowBalloon = true;       // 托盘气泡提示（关掉就不再弹右下角通知）
        public int ExpandSpeed = 100;         // 展开动画速度 %（越大越快；独立于整体动画速度）
        public int CollapseSpeed = 150;       // 收起动画速度 %（默认"快"一档，收起要干脆）
        public bool NubSingle = false;        // 只用一个把手：左边那个点一下展开、再点一下收起（底部不占地方）
        public bool DragOutAsFile = false;    // 拖出时是否同时提供"文件"格式（关掉就不会往桌面落地成文件）
        public bool CheckUpdate = true;       // 启动时检查 GitHub 有没有新版本
        public bool NubHintDone = false;      // 把手用途提示是否已经自动展示过
        public bool AnnotHintDone = false;    // 截图标注（工具条）的首次提示是否已展示过
        public bool PinHintDone = false;      // 贴图（中键）的首次提示是否已展示过
        public string GuideSeenVersion = "";  // 上一次自动弹出新手引导/更新说明时的版本号
        public bool TextBg = true;            // 标注文字默认带白底（可关，见截图工具条上的"文字底"）
        public string KeyActions = "new,next,delete,prev";  // 万能键四分区动作：上,右,下,左
        public int Rev = 0;                   // 配置版本号（用于默认值迁移）

        // 测试用：把配置文件指到临时路径（null = 正常的 %APPDATA%\SnapWheel）。
        // 有了它，测试才能做"存盘 -> 重新读回来"的往返验证，又不会覆盖用户真实的设置。
        public static string OverridePath = null;

        static string FilePath()
        {
            if (OverridePath != null) return OverridePath;
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapWheel");
            try { Directory.CreateDirectory(d); } catch { }
            return Path.Combine(d, "settings.ini");
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            s.Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapWheel");
            try
            {
                string f = FilePath();
                if (File.Exists(f))
                {
                    foreach (string line in File.ReadAllLines(f))
                    {
                        string[] kv = line.Split(new char[] { '=' }, 2);
                        if (kv.Length != 2) continue;
                        string k = kv[0].Trim(), v = kv[1].Trim();
                        if (k == "SaveToDisk") s.SaveToDisk = (v == "1");
                        else if (k == "Dir" && v.Length > 0) s.Dir = v;
                        else if (k == "MaxCount") { int n; if (int.TryParse(v, out n)) s.MaxCount = n; }
                        else if (k == "AutoHide") s.AutoHide = (v == "1");
                        else if (k == "AutoHideSeconds") { int n; if (int.TryParse(v, out n)) s.AutoHideSeconds = n; }
                        else if (k == "ShowWheelOnStart") s.ShowWheelOnStart = (v == "1");
                        else if (k == "AlwaysOnTop") s.AlwaysOnTop = (v == "1");
                        else if (k == "Hotkey" && v.Length > 0) s.Hotkey = v;
                        else if (k == "ThumbSize") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 260) s.ThumbSize = n; }
                        else if (k == "Radius") { int n; if (int.TryParse(v, out n) && n >= 120 && n <= 700) s.Radius = n; }
                        else if (k == "Slots") { int n; if (int.TryParse(v, out n) && n >= 2 && n <= 12) s.Slots = n; }
                        else if (k == "LabelSize") { int n; if (int.TryParse(v, out n) && n >= 8 && n <= 40) s.LabelSize = n; }
                        else if (k == "Corner" && (v == "BL" || v == "BR" || v == "TL" || v == "TR")) s.Corner = v;
                        else if (k == "AutoStart") s.AutoStart = (v == "1");
                        else if (k == "DeleteMode" && (v == "single" || v == "double")) s.DeleteMode = v;
                        else if (k == "SwitchMode" && (v == "radial" || v == "swipe")) s.SwitchMode = v;
                        else if (k == "PeekPercent") { int n; if (int.TryParse(v, out n) && n >= 120 && n <= 500) s.PeekPercent = n; }
                        else if (k == "IntroSeen") s.IntroSeen = (v == "1");
                        else if (k == "IntroAnim") s.IntroAnim = (v == "1");
                        else if (k == "UiStyle" && (v == "neu" || v == "flat" || v == "solid")) s.UiStyle = v;
                        else if (k == "GlassPercent") { int n; if (int.TryParse(v, out n) && n >= 20 && n <= 100) s.GlassPercent = n; }
                        else if (k == "CardRadius") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 30) s.CardRadius = n; }
                        else if (k == "ShadowPercent") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 100) s.ShadowPercent = n; }
                        else if (k == "AnimSpeed") { int n; if (int.TryParse(v, out n) && n >= 50 && n <= 200) s.AnimSpeed = n; }
                        else if (k == "AccentIndex") { int n; if (int.TryParse(v, out n) && n >= -1 && n <= 7) s.AccentIndex = n; }
                        else if (k == "ShowNameLabel") s.ShowNameLabel = (v == "1");
                        else if (k == "ShowCountLabel") s.ShowCountLabel = (v == "1");
                        else if (k == "UiScale") { int n; if (int.TryParse(v, out n) && n >= 0 && n <= 250) s.UiScale = n; }
                        else if (k == "CollapseMode") s.CollapseMode = (v == "1");
                        else if (k == "ClipboardImport") s.ClipboardImport = (v == "1");
                        else if (k == "GlassRefresh") s.GlassRefresh = (v == "1");
                        else if (k == "ShowBalloon") s.ShowBalloon = (v == "1");
                        else if (k == "ExpandSpeed") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 250) s.ExpandSpeed = n; }
                        else if (k == "CollapseSpeed") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 250) s.CollapseSpeed = n; }
                        else if (k == "RingSpeed") { int n; if (int.TryParse(v, out n) && n >= 40 && n <= 250) { s.ExpandSpeed = n; s.CollapseSpeed = n; } }   // 兼容旧配置
                        else if (k == "NubSingle") s.NubSingle = (v == "1");
                        else if (k == "DragOutAsFile") s.DragOutAsFile = (v == "1");
                        else if (k == "CheckUpdate") s.CheckUpdate = (v == "1");
                        else if (k == "NubHintDone") s.NubHintDone = (v == "1");
                        else if (k == "AnnotHintDone") s.AnnotHintDone = (v == "1");
                        else if (k == "PinHintDone") s.PinHintDone = (v == "1");
                        else if (k == "GuideSeenVersion") s.GuideSeenVersion = v;
                        else if (k == "TextBg") s.TextBg = (v == "1");
                        else if (k == "KeyActions" && v.Length > 0) s.KeyActions = v;
                        else if (k == "Rev") { int n; if (int.TryParse(v, out n)) s.Rev = n; }
                    }
                }
            }
            catch { }

            // ---- 配置迁移 ----
            // 只补一个版本标记。默认值（例如"收起态默认关"）只影响「全新安装」，
            // 绝不覆盖老用户自己的选择 —— 上一版会强制改，把明明开着收起的人给关掉了。
            s.Rev = 2;

            return s;
        }

        // 把 src 的每个设置字段拷到 dst（用于"还原默认设置"）
        public static void CopyInto(Settings src, Settings dst)
        {
            FieldInfo[] fs = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fs.Length; i++)
            {
                try { fs[i].SetValue(dst, fs[i].GetValue(src)); } catch { }
            }
        }

        // ---------- 万能键四个分区能绑的动作 ----------
        // 顺序 = 分区顺序：0=上 1=右 2=下 3=左（和 SectorAt 一致）
        public static readonly string[] KeyActionIds =
        {
            "new", "next", "delete", "prev", "shot", "collapse", "folder", "settings", "paste", "clear", "none"
        };

        public static string KeyActionName(string id)
        {
            switch (id)
            {
                case "new": return "新建轮盘";
                case "next": return "下一个轮盘";
                case "prev": return "上一个轮盘";
                case "delete": return "删除当前轮盘";
                case "shot": return "截图";
                case "collapse": return "收起轮盘";
                case "folder": return "打开保存文件夹";
                case "settings": return "打开设置";
                case "paste": return "从剪贴板收一张";
                case "clear": return "清空这一盘（保留轮盘）";
                default: return "不设置";
            }
        }

        // 取某个分区的动作 id；配置损坏时回落到出厂默认
        public string KeyActionAt(int sector)
        {
            string[] a = (KeyActions ?? "").Split(',');
            if (sector >= 0 && sector < a.Length)
            {
                string id = a[sector].Trim();
                for (int i = 0; i < KeyActionIds.Length; i++) if (KeyActionIds[i] == id) return id;
            }
            string[] def = { "new", "next", "delete", "prev" };
            return (sector >= 0 && sector < 4) ? def[sector] : "none";
        }

        public void SetKeyAction(int sector, string id)
        {
            string[] a = (KeyActions ?? "").Split(',');
            string[] def = { "new", "next", "delete", "prev" };
            string[] outv = new string[4];
            for (int i = 0; i < 4; i++) outv[i] = (i < a.Length && a[i].Trim().Length > 0) ? a[i].Trim() : def[i];
            if (sector >= 0 && sector < 4) outv[sector] = id;
            KeyActions = string.Join(",", outv);
        }

        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("SaveToDisk=" + (SaveToDisk ? "1" : "0"));
                lines.Add("Dir=" + Dir);
                lines.Add("MaxCount=" + MaxCount);
                lines.Add("AutoHide=" + (AutoHide ? "1" : "0"));
                lines.Add("AutoHideSeconds=" + AutoHideSeconds);
                lines.Add("ShowWheelOnStart=" + (ShowWheelOnStart ? "1" : "0"));
                lines.Add("AlwaysOnTop=" + (AlwaysOnTop ? "1" : "0"));
                lines.Add("Hotkey=" + Hotkey);
                lines.Add("ThumbSize=" + ThumbSize);
                lines.Add("Radius=" + Radius);
                lines.Add("Slots=" + Slots);
                lines.Add("LabelSize=" + LabelSize);
                lines.Add("Corner=" + Corner);
                lines.Add("AutoStart=" + (AutoStart ? "1" : "0"));
                lines.Add("DeleteMode=" + DeleteMode);
                lines.Add("SwitchMode=" + SwitchMode);
                lines.Add("PeekPercent=" + PeekPercent);
                lines.Add("IntroSeen=" + (IntroSeen ? "1" : "0"));
                lines.Add("IntroAnim=" + (IntroAnim ? "1" : "0"));
                lines.Add("UiStyle=" + UiStyle);
                lines.Add("GlassPercent=" + GlassPercent);
                lines.Add("CardRadius=" + CardRadius);
                lines.Add("ShadowPercent=" + ShadowPercent);
                lines.Add("AnimSpeed=" + AnimSpeed);
                lines.Add("AccentIndex=" + AccentIndex);
                lines.Add("ShowNameLabel=" + (ShowNameLabel ? "1" : "0"));
                lines.Add("ShowCountLabel=" + (ShowCountLabel ? "1" : "0"));
                lines.Add("UiScale=" + UiScale);
                lines.Add("CollapseMode=" + (CollapseMode ? "1" : "0"));
                lines.Add("ClipboardImport=" + (ClipboardImport ? "1" : "0"));
                lines.Add("GlassRefresh=" + (GlassRefresh ? "1" : "0"));
                lines.Add("ShowBalloon=" + (ShowBalloon ? "1" : "0"));
                lines.Add("ExpandSpeed=" + ExpandSpeed);
                lines.Add("CollapseSpeed=" + CollapseSpeed);
                lines.Add("NubSingle=" + (NubSingle ? "1" : "0"));
                lines.Add("DragOutAsFile=" + (DragOutAsFile ? "1" : "0"));
                lines.Add("CheckUpdate=" + (CheckUpdate ? "1" : "0"));
                lines.Add("NubHintDone=" + (NubHintDone ? "1" : "0"));
                lines.Add("AnnotHintDone=" + (AnnotHintDone ? "1" : "0"));
                lines.Add("PinHintDone=" + (PinHintDone ? "1" : "0"));
                lines.Add("GuideSeenVersion=" + (GuideSeenVersion ?? ""));
                lines.Add("TextBg=" + (TextBg ? "1" : "0"));
                lines.Add("KeyActions=" + KeyActions);
                Rev = 2;                       // 配置格式版本：写了它以后就不再被默认值迁移覆盖
                lines.Add("Rev=" + Rev);
                File.WriteAllLines(FilePath(), lines.ToArray());
            }
            catch { }
        }
    }

    static class HotkeyUtil
    {
        public static readonly string[] Names = { "Ctrl+Shift+S", "Ctrl+Shift+A", "Ctrl+Alt+A", "Alt+Shift+A", "Ctrl+Shift+X" };

        public static bool TryParse(string name, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrEmpty(name)) return false;
            string[] parts = name.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= Native.MOD_CONTROL;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= Native.MOD_SHIFT;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= Native.MOD_ALT;
                else if (p.Length == 1)
                {
                    char c = char.ToUpperInvariant(p[0]);
                    if (c >= 'A' && c <= 'Z') vk = (uint)c;
                }
            }
            return mods != 0 && vk != 0;
        }
    }

    static class AutoRun
    {
        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string NAME = "SnapWheel";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                    return k != null && k.GetValue(NAME) != null;
            }
            catch { return false; }
        }

        public static void Apply(bool enable)
        {
            try
            {
                if (enable)
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                        k.SetValue(NAME, "\"" + Application.ExecutablePath + "\"");
                }
                else
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                        k.DeleteValue(NAME, false);
                }
            }
            catch { }
        }
    }
}
