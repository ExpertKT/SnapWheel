# SnapWheel v0.4.7

**日期**：2026-09-12

## 本次更新
修复长按提示条被万能键遮住：挪到轮盘左下角提示带并最后绘制（永远在最上层），同时让开计数胶囊

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```