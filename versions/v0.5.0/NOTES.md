# SnapWheel v0.5.0

**日期**：2026-09-13

## 本次更新
贴图到屏幕（图钉，中键点缩略图）+ 截图标注四件套（箭头/方框/马赛克/文字，四色、撤销、确认时合成进图片）

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `src\` —— 完整源码（0.4.9 起按类型拆分；WheelForm 是 5 个 partial）
- `SnapWheel.cs` —— 同一份源码合并成的单文件（方便直接编译/搜索）
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe src\*.cs
```