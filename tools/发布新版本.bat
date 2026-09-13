@echo off
chcp 65001 >nul
title SnapWheel 发布新版本
cd /d "%~dp0.."

echo.
echo   ============================================
echo    SnapWheel 发布新版本
echo   ============================================
echo.
echo    会做的事：改版本号 -^> 编译两条线 -^> 归档到 versions\
echo    （只动本地，不会推到 GitHub）
echo.
echo    源码里当前的版本号：
findstr /C:"public const string Version" "src\00-AppInfo.cs"
echo.

set VER=
set /p VER=    完整版新版本号（直接回车 = 不改，例如 0.4.7）:
set NK=
set /p NK=     无万能键版新版本号（回车 = 自动算，例如 0.2.17）:
set NOTE=
set /p NOTE=   这次改了什么（一句话，会写进更新记录）:

echo.
echo   --------------------------------------------
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0record-version.ps1" -Version "%VER%" -NoKeyVersion "%NK%" -Note "%NOTE%"

echo.
echo   --------------------------------------------
echo    提示：本地归档完成，但**还没推到 GitHub**
echo          要推送请先跟 AI 确认，或自己执行：
echo            git add -A
echo            git commit -m "说明"
echo            git push
echo   --------------------------------------------
echo.
pause
