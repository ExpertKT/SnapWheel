# SnapWheel v0.5.1

**日期**：2026-09-13

## 本次更新
撤销删除（后悔药）：托盘「撤销上一次删除」把刚删掉/刚清空的那批图放回原盘，最近 8 次都记得住；刻意不做回收站（不建目录、不改索引、退出即清空）。并修掉「窗口关掉后动画定时器还在空转」—— 同一个缺陷让行为测试从 331.7 秒降到 8.7 秒。

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `src\` —— 完整源码（0.4.9 起按类型拆分；WheelForm 是 5 个 partial）
- `SnapWheel.cs` —— 同一份源码合并成的单文件（方便直接编译/搜索）
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe src\*.cs
```