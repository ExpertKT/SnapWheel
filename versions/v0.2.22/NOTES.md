# SnapWheel v0.2.22

**日期**：2026-09-14

## 本次更新
设置界面改轮盘式分页（四页）+ 构造提速

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `src\` —— 完整源码（0.4.9 起按类型拆分；WheelForm 是 5 个 partial）
- `SnapWheel.cs` —— 同一份源码合并成的单文件（方便直接编译/搜索）
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /define:NO_KEY /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe src\*.cs
```