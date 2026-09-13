# SnapWheel v0.5.0

**日期**：2026-09-13

## 本次更新
截图标注四件套（箭头/方框/马赛克/文字+拖动改字号+文字底）+ 取字 OCR（工具选取、自动放大 2 倍、翻译）+ 贴图到屏幕 + 一堆老 bug 修复（拖出放不了、副屏面板、启动动画曲线、毛玻璃卡 UI）

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `src\` —— 完整源码（0.4.9 起按类型拆分；WheelForm 是 5 个 partial）
- `SnapWheel.cs` —— 同一份源码合并成的单文件（方便直接编译/搜索）
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe src\*.cs
```