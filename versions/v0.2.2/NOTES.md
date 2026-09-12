# SnapWheel v0.2.2

**日期**：2026-09-11

## 本次更新
- 同 v0.3.2 的全部修复（缩放边缘回弹），无万能键变体（多 Wheel + 缩放/锁定）

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```