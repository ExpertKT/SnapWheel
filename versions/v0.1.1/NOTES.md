# SnapWheel v0.1.1

**日期**：2026-09-11

## 本次更新
首个正式版本：截图轮盘完整功能（热键框选、比例锁定、圆环库、拖出/放回、动画、四角位置、开机自启）

## 文件
- SnapWheel.exe —— 可执行文件（绿色免安装）
- SnapWheel.cs —— 完整源码
- snapwheel.ico —— 图标

## 编译
```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```