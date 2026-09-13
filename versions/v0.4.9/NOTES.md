# SnapWheel v0.4.9

**日期**：2026-09-13

## 本次更新
源码拆分（单文件 → src\ 19 个文件，WheelForm 拆 5 个 partial）+ 管理员权限下的拖放引导 + GitHub Actions CI

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `src\` —— 完整源码（0.4.9 起按类型拆分；WheelForm 是 5 个 partial）
- `SnapWheel.cs` —— 同一份源码合并成的单文件（方便直接编译/搜索）
- `snapwheel.ico` —— 图标

## 编译
```
csc /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe src\*.cs
```