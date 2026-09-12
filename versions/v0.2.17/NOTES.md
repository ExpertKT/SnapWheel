# SnapWheel v0.2.17

**日期**：2026-09-12

## 本次更新
长按关闭键加入后悔机制：变红后把鼠标挪开即作废（红色渐变退回），松手不退出；长按过程中显示「松手就退出 · 移开则取消」提示；build.ps1 增加 -Deploy（先停旧实例再复制并核对字节数）

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /define:NO_KEY /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```