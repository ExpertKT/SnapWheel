# SnapWheel v0.4.7

**日期**：2026-09-12

## 本次更新
收起把手钉在环的两个端点（卷轴两端）并适配各种分辨率/缩放；展开收起用同一缓动与时长且开头即动；关闭/设置/截图三钮按下有反馈；点截图先播收起动画、截完再播拉出动画；设置新增还原默认；修复已释放图片崩溃

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```