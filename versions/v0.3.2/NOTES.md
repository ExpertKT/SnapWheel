# SnapWheel v0.3.2

**日期**：2026-09-11

## 本次更新
- 修复拖拽缩放时贴到屏幕边缘的「回弹抽搐」：改为先把鼠标点夹进屏幕再算尺寸
- 万能键「删除」改为左右两半确认：摇杆左半=取消（绿）/ 右半=确认删除（红），鼠标所在半会高亮，点别处或 10 秒无操作自动取消

## 文件
- `SnapWheel.exe` —— 可执行文件（绿色免安装）
- `SnapWheel.cs` —— 完整源码
- `snapwheel.ico` —— 图标

## 编译
```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ /target:winexe /win32icon:snapwheel.ico /out:SnapWheel.exe SnapWheel.cs
```