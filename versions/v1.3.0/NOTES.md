# SnapWheel v1.3.0

**日期**：2026-10-03

## 本次更新
环上支持图片、文字、文件格；拖放收纳与移进来；按内容绘制材质；轮盘收取类型可选；玻璃背景持续刷新

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `src\` —— 完整源码（0.4.9 起按类型拆分；WheelForm 是 5 个 partial）
- `SnapWheel.cs` —— 同一份源码合并成的单文件（方便直接编译/搜索）
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe src\*.cs
```