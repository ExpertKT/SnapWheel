using System;

namespace SnapWheel
{
    // 手感常量（v0.5.2）：悬停/按下的数值以前散落在各个绘制分支里（166/172/206/240/252…），
    // 调一次要翻好几个地方，而且各处强弱不一致 —— 用户的原话是"点下去的反馈较弱"。
    // 统一放这里：① 悬停明显亮起来 ② 按下沉下去 + 更亮 ③ 松开自动回弹（进度值由 AnimTick 推）。
    static class UiFeel
    {
        // 表面亮度（玻璃/主色底的不透明度百分比，最后会乘玻璃设置）
        public const int SurfaceIdle = 166;     // 常态
        public const int SurfaceHover = 252;    // 悬停：明显亮一档
        public const int SurfacePress = 255;    // 按下：最亮

        // 图标/文字
        public const int IconIdle = 236;
        public const int IconHover = 255;

        // 按下时往下沉多少逻辑像素（松开回弹由动画进度负责）
        public const float SinkPx = 2f;

        // 毛玻璃面板上的主色底（截图按钮那类实心按钮）
        public const int SolidIdle = 190;
        public const int SolidHover = 240;
    }
}