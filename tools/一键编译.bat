@echo off
chcp 65001 >nul
title SnapWheel 编译
cd /d "%~dp0.."

echo.
echo   ============================================
echo    SnapWheel 一键编译
echo   ============================================
echo.
echo    会做的事：编译两条产品线 -^> 跑全部测试 -^> 打包分发 zip
echo    产物都在 build\ 目录里，编完会停在这里让你看结果
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Test -Package

echo.
echo   --------------------------------------------
echo    提示：产物在项目目录的 build\ 里
echo          想发布新版本请双击 tools\发布新版本.bat
echo   --------------------------------------------
echo.
pause
