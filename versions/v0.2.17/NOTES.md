# SnapWheel v0.2.17

**日期**：2026-09-12

## 本次更新
新增收起状态（贴边把手在环两端点外侧，彩虹拉出/收起，速度一致且随时可掉头）；剪贴板复制图片自动收进轮盘；毛玻璃定时刷新；设置新增「还原默认」；修复已释放图片导致崩溃

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /define:NO_KEY /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```