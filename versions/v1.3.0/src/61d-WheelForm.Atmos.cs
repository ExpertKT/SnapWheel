using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnapWheel
{
    // 轮盘的"气氛"层（v1.0）：让已有的东西活起来的那几件事。
    //
    // 它们有一个共同点：**都不新增任何功能**，只是在已有的东西上补节奏和反馈。
    // 单独放一个文件，是因为它们的性质一样 —— 都是"跟着时间走的标量 + 一处绘制"，
    // 而 61-WheelForm.Draw.cs 已经够长了。
    //
    // 里面所有的"随时间变化"都遵守同一条纪律：**有始有终**。
    // 空转满帧那两次事故（45fps）都是"永远差一点点"的形状，
    // 所以这里每个标量都必须能**真正走到终点**，并且终点上有吸附。
    partial class WheelForm
    {
        // ==================== ① 环的厚度：内容越多越粗 ====================
        // 空环最细，堆满最粗。只到 1.55 倍就封顶 —— 再粗就变成"一个游泳圈"压在缩略图底下了。
        // 纯函数（取数量），所以测试能直接喂数字验，不用去摆真实状态。
        internal static float RingThickOf(int count, int slots)
        {
            int sl = slots < 2 ? 2 : slots;
            float f = (float)count / sl;
            if (f > 1f) f = 1f;
            if (f < 0f) f = 0f;
            return 1f + 0.55f * f;
        }
        float RingThick() { return RingThickOf(_store == null ? 0 : _store.Items.Count, _slots); }

        // ==================== ② 时间感：早上偏暖、深夜自己暗下去 ====================
        // 纯函数（参数是"几点"），所以测试能把 24 个小时全跑一遍，而不用去改系统时间。
        //   DayDim  ：深夜整块暗下去多少（加在整体不透明度上）
        //   DayWarm ：早上偏暖的程度（0 = 不偏，1 = 最暖）
        // 取值刻意保守 —— 用户的第一反应不该是"轮盘怎么变淡了"，而该是"晚上看着舒服"。
        internal static float DayDimOf(int hour)
        {
            if (hour >= 9 && hour < 21) return 0f;                 // 白天完全不动
            if (hour >= 21) return (hour - 21) / 5f * 0.15f;        // 21 → 02 慢慢暗到 0.15
            if (hour >= 6) return (9 - hour) / 3f * 0.09f;          // 06 → 09 从 0.09 回到 0
            return 0.15f;                                           // 深夜最暗
        }
        internal static float DayWarmOf(int hour)
        {
            if (hour < 6 || hour >= 20) return 0f;
            if (hour <= 10) return (hour - 6) / 4f;                 // 06 → 10 暖起来
            if (hour <= 16) return 1f;                              // 10 → 16 最暖
            return 1f - (hour - 16) / 4f;                           // 16 → 20 暖意退掉
        }
        float DayDim() { return _settings != null && !_settings.DayMood ? 0f : DayDimOf(DateTime.Now.Hour); }
        float DayWarm() { return _settings != null && !_settings.DayMood ? 0f : DayWarmOf(DateTime.Now.Hour); }

        // 把主题色往暖里偏一点（早上）。用插值而不是替换，白天/晚上都还是原来的主题色。
        Color DayTint(Color c)
        {
            float w = DayWarm();
            if (w <= 0.01f) return c;
            int r = c.R + (int)((255 - c.R) * 0.22f * w);
            int g = c.G + (int)((196 - c.G) * 0.10f * w);
            int b = c.B - (int)(c.B * 0.16f * w);
            if (r > 255) r = 255; if (g > 255) g = 255; if (b < 0) b = 0;
            return Color.FromArgb(c.A, r, g, b);
        }

        // ==================== ③ 新来的那一格有微光 ====================
        // 亮 2.2 秒，再用 2.6 秒冷下去 —— 抬眼就知道"哪张是刚截的"，不用去数。
        internal const double FreshHoldSec = 2.2, FreshCoolSec = 2.6;
        internal static float FreshGlowAt(double ageSec)
        {
            if (ageSec < 0) return 0f;
            if (ageSec <= FreshHoldSec) return 1f;
            if (ageSec >= FreshHoldSec + FreshCoolSec) return 0f;
            float t = (float)((ageSec - FreshHoldSec) / FreshCoolSec);
            return 1f - t * t * (3f - 2f * t);        // smoothstep 冷下去
        }
        float FreshGlow(int i)
        {
            if (i < 0 || i >= _store.Items.Count) return 0f;
            DateTime t0;
            if (!_freshT0.TryGetValue(_store.Items[i], out t0)) return 0f;
            return FreshGlowAt((DateTime.Now - t0).TotalSeconds);
        }
        // 还有没有"正在冷却的微光"（有的话这一帧必须画，而且这一层不能用缓存）
        bool FreshActive()
        {
            foreach (System.Collections.Generic.KeyValuePair<StoreItem, DateTime> kv in _freshT0)
                if ((DateTime.Now - kv.Value).TotalSeconds < FreshHoldSec + FreshCoolSec + 0.05) return true;
            return false;
        }
        // 微光总强度（进层签名用）：只把"还有没有微光"区别出来就够，不必精确到每一格
        double FreshGlowSum()
        {
            double s = 0;
            foreach (System.Collections.Generic.KeyValuePair<StoreItem, DateTime> kv in _freshT0)
                s += FreshGlowAt((DateTime.Now - kv.Value).TotalSeconds);
            return s;
        }

        // ==================== ④ 涟漪 ====================
        // 加进来一张新图时，从那一格扩散开一圈淡淡的光 —— "东西进来了"这件事有了形状。
        // 做成独立标量（不是每格一份），所以同时来一堆图时也只有一圈，不会糊成一团。
        float _rippleT = 1f;
        DateTime _rippleAt = DateTime.MinValue;
        PointF _rippleAt2 = PointF.Empty;
        float _rippleRad = 80f;

        internal void StartRipple(PointF center)
        {
            if (_settings != null && !_settings.Ripple) return;
            _rippleAt2 = center;
            _rippleT = 0f;
            _rippleAt = DateTime.Now;
            float ls = Math.Max(LogicalSize().Width, LogicalSize().Height);
            _rippleRad = ls * 0.42f;
        }

        // 由 AnimTick 推动；returns true 表示"这一帧还得画"
        bool RippleTick()
        {
            if (_rippleT >= 1f) return false;
            _rippleT += (float)((DateTime.Now - _rippleAt).TotalSeconds / 0.62f);
            if (_rippleT >= 1f || DateTime.Now < _rippleAt) _rippleT = 1f;
            _rippleAt = DateTime.Now;
            return true;
        }

        void DrawRipple(Graphics g, float a)
        {
            if (_rippleT >= 1f || a <= 2f) return;
            float t = _rippleT;
            float e = 1f - (1f - t) * (1f - t);                 // ease-out：一开始快，后面慢下来
            float r = _rippleRad * e;
            if (r < 3f) return;
            int alpha = (int)(70 * (1f - t) * (1f - t) * a / 255f);
            if (alpha < 3) return;
            Color ac = DayTint(_accentCur);
            using (Pen p = new Pen(Color.FromArgb(alpha, ac.R, ac.G, ac.B), 3.6f * (1f - t * 0.6f)))
                g.DrawEllipse(p, _rippleAt2.X - r, _rippleAt2.Y - r, r * 2f, r * 2f);
            // 里面再一圈更淡的，看起来才像水波而不是一个圆圈
            int alpha2 = (int)(38 * (1f - t) * (1f - t) * a / 255f);
            if (alpha2 >= 3)
                using (Pen p = new Pen(Color.FromArgb(alpha2, ac.R, ac.G, ac.B), 2.2f))
                {
                    float r2 = r * 0.72f;
                    g.DrawEllipse(p, _rippleAt2.X - r2, _rippleAt2.Y - r2, r2 * 2f, r2 * 2f);
                }
        }

        // ==================== ⑤ 环的影子 ====================
        // 让环"浮"在桌面上而不是画在上面。和缩略图的阴影同一套路：柔和的漫射阴影。
        void DrawRingShadow(Graphics g, GraphicsPath track, float rr, float a)
        {
            if (_settings != null && !_settings.RingShadow) return;
            if (StyleFlatOnly() || _settings.ShadowPercent <= 8) return;
            int sa = (int)(ShadowA(70) * 0.85f);
            if (sa < 4 || a <= 2f) return;
            // GraphicsState 在 .NET 4.0 里**没实现 IDisposable**（只有 Save/Restore），
            // 所以这里不能写 using —— 必须 try/finally 保证还原。
            GraphicsState st = g.Save();
            try
            {
                g.TranslateTransform(2.5f, 4.5f);              // 光从左上来，影子往右下走
                using (Pen sp = new Pen(Color.FromArgb((int)(sa * a / 255f), 0, 0, 0), 16f + 6f * (RingThick() - 1f) / 0.55f))
                { sp.StartCap = LineCap.Round; sp.EndCap = LineCap.Round; g.DrawPath(sp, track); }
            }
            finally { g.Restore(st); }
        }
    }
}
