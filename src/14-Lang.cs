using System;
using System.Globalization;

namespace SnapWheel
{
    // ==================== 界面语言（0.6.0 第一轮 i18n） ====================
    // 设计取舍：**不做 key → 文案 的字典**，而是 Lang.T("中文", "Chinese") 就地双写。
    //   · 好处：改造一处只需把字符串包一层，不用先在字典里登记、也不会出现"键对不上"；
    //     翻译和代码在同一个地方，读代码时能立刻看到两种语言，漏翻一眼就能发现。
    //   · 代价：字符串在源码里出现两次（可接受 —— 这个程序本来就是单文件、零依赖的路线）。
    //
    // ⚠️ 语言**在启动时确定**，切换后需要重启生效：界面文字散布在几十个窗口/绘制代码里，
    //    运行时热切换要重建所有已打开的窗口，风险远大于收益。设置里会明确提示"重启后生效"。
    static class Lang
    {
        static string _cur = "zh";

        public static void Init(string code)
        {
            _cur = (code != null && code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ? "en" : "zh";
        }

        public static string Cur { get { return _cur; } }
        public static bool En { get { return _cur == "en"; } }

        // 界面文字：En 为 true 时取英文
        public static string T(string zh, string en)
        {
            return _cur == "en" ? en : zh;
        }

        // 系统语言猜一个默认值（首次运行、用户还没选过时用）
        public static string Guess()
        {
            try
            {
                string n = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                return n == "zh" ? "zh" : "en";
            }
            catch { return "zh"; }
        }
    }
}
