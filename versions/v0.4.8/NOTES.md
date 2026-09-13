# SnapWheel v0.4.8

**日期**：2026-09-13

> ⚠️ **这份快照是当天 11:44 的构建，不含当天 12:29 的「圆盘动作名改完不变」修复**，
> 也不含之后的帧耗时统计与行为测试。v0.4.8 的最终源码与产物在仓库根目录
> （`SnapWheel.cs` / `build\`，git 提交 58cee15）。本目录保持原样是为了不改历史归档。

## 本次更新
体验与工程优化：默认展开、万能键四分区可自定义、把手用途提示、长按140ms、拖出不再落地成文件、设置高级折叠、新手引导改场景化、自动更新检查、毛玻璃后台线程、一键安装器、自签名流程

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```