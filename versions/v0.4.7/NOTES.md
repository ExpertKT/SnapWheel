# SnapWheel v0.4.7

**日期**：2026-09-12

## 本次更新
中文名正式更名「快照轮环」；关闭键改为直接关轮盘、长按变红松手退出程序；展开/收起速度可分别调节（默认收起快一档）；去掉收起冷却（两个方向随时可点）；动画主进度改线性，展开收起时间对称

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```