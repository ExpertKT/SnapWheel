# SnapWheel v0.4.7

**日期**：2026-09-12

## 本次更新
新增「显示托盘气泡提示」开关；新增「收起展开速度」独立调节；新增「只用一个把手」模式（适合任务栏自动隐藏）；收起后 0.65s 冷却防误触；截图流程收起动画再提速

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```