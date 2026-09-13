# SnapWheel v0.2.20

**日期**：2026-09-13

## 本次更新
贴图到屏幕 + 截图标注四件套 + 取字（OCR，系统自带引擎，零依赖）+ 毛玻璃抓屏移到后台（点开/收起不再卡一下）

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `src\` —— 完整源码（0.4.9 起按类型拆分；WheelForm 是 5 个 partial）
- `SnapWheel.cs` —— 同一份源码合并成的单文件（方便直接编译/搜索）
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /define:NO_KEY /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe src\*.cs
```