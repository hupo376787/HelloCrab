@echo off
chcp 65001 >nul
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\validate-publish-scripts.ps1" -Quiet
if errorlevel 1 goto :validation_failed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\publish-platform.ps1" -Target win-x64 -Configuration Release -Version 1.0.0
set EXIT_CODE=%ERRORLEVEL%
echo.
if not "%EXIT_CODE%"=="0" echo 发布失败，错误代码：%EXIT_CODE%
goto :finish

:validation_failed
set "EXIT_CODE=%ERRORLEVEL%"
echo.
echo 发布脚本检查失败，错误代码：%EXIT_CODE%

:finish
pause
exit /b %EXIT_CODE%
