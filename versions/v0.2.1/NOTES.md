# SnapWheel v0.2.1

**构建方式**：`csc /define:NO_KEY`（**不含万能键**）

## 本版定义（按你的划分）
> **没有万能键，但有框选缩放 + 比例锁定** → v0.2.x

## 内容
与 v0.3.1 **完全相同**，仅**移除万能键（摇杆圆盘）**：
- 多 Wheel（多项目）、8 色主题、切换过渡动画、Wheel 管理界面；
- 框选：**四角缩放（对角固定）**、比例锁定（连体锁定键）、旋转键 + 旋转输出；
- **右上角尺寸输入面板**（宽/高 + 应用）与**角度归零**按钮；
- 本版修复了 v0.2.0 的缩放「只能拖一点」与「乱飞」问题（同 v0.3.1 的两处修复）。

## 文件
- `SnapWheel.exe` —— 本版可执行文件（无万能键）
- `SnapWheel.cs` —— 完整源码（`NO_KEY` 开关区分 0.2.x / 0.3.x）
- `snapwheel.ico` —— 图标

## 编译（本版）
```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ /define:NO_KEY /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```
