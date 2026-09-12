# SnapWheel v0.4.7

**日期**：2026-09-12

## 本次更新
修复动画收尾闪现（出场延迟改成按比例，保证结束前全部到位）；两个把手交叉淡入淡出并从屏幕边滑出；截图流程动画提速（收起约 0.27s、拉出约 0.6s）

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```