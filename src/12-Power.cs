using System;
using System.Windows.Forms;
namespace SnapWheel
{
    // ============================ 省电模式（0.5.3） ============================
    // 用户的笔记本长期在电池上跑，这个程序有两处持续耗电：
    //   ① 毛玻璃底**每 3.5 秒**抓屏 + 模糊一次（几十毫秒 CPU，实测抓屏同步要 12~16ms，模糊在后台线程）；
    //   ② 动画定时器 15ms 一帧的持续重绘（有动画时每帧都要重画整窗）。
    // 省电模式（设置里默认开）：**只在电池供电时**做两件事 —— 暂停"定时重抓玻璃底"、把重绘**隔帧**一次。
    //
    // 电池侦测：`SystemInformation.PowerStatus.PowerLineStatus == Offline`（在电池上）。
    //   ⚠️ 结果**缓存 5 秒**：这个属性每次都要问系统，绝不能每帧/每次 tick 去调；
    //      缓存后"插电 / 拔电"最多 5 秒内生效（用户感知不到，也不会白烧 CPU）。
    //   ⚠️ 测试用 `ForceOnBatteryForTest`（照 `Elev.ForceForTest` 的路子）：不然"电池下会怎样"
    //      永远只能在真拔了电的机器上手测。
    static class Power
    {
        public static bool? ForceOnBatteryForTest = null;   // true=当电池，false=当插电，null=按真实状态

        static bool _onBattery;
        static DateTime _at = DateTime.MinValue;
        const double CacheSec = 5.0;

        public static bool OnBattery()
        {
            if (ForceOnBatteryForTest.HasValue) return ForceOnBatteryForTest.Value;
            if ((DateTime.Now - _at).TotalSeconds < CacheSec) return _onBattery;
            bool v;
            try { v = (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline); }
            catch { v = false; }                 // 读不到就按"插电"处理：宁可费点电，也别把功能省没了
            _onBattery = v;
            _at = DateTime.Now;
            return v;
        }

        // 测试/工具用：把缓存作废（换完状态立刻生效，不用等 5 秒）
        public static void ResetCacheForTest()
        {
            _at = DateTime.MinValue;
        }
    }
}
