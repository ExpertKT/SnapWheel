# SnapWheel v0.3.5

**日期**：2026-09-12

## 本次更新
- **紧急修复崩溃**：拖图经过轮盘时高亮色算成 Color.FromArgb(255, 120, 194, 279)（蓝色分量 255+24 越界），抛 ArgumentException 弹「.NET Framework 未处理异常」直接把程序打死 —— 现在夹到 255，并把两个高亮色改成命名常量
- **加装全局兜底**：Application.ThreadException + AppDomain.UnhandledException 全部接住，只写日志（%APPDATA%\SnapWheel\error.log）+ 托盘气泡提醒，程序继续运行，不再弹框退出
- 轮盘绘制 / 截图遮罩绘制的每一帧都单独 try-catch：画错一帧最多那一帧不好看，绝不连累整个程序
- 新增两个可复跑的测试：tests\render-smoke.cs（绘制状态矩阵 25 项）、tests\io-test.cs（格式/导入 54 项）