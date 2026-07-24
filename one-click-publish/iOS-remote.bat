@echo off
chcp 65001 >nul
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\validate-publish-scripts.ps1" -Quiet
if errorlevel 1 goto :validation_failed
rem 从 Windows 发布 iOS 前，请先配置下面这些环境变量，或在系统环境变量中配置：
rem set HELLOCRAB_IOS_SERVER_ADDRESS=192.168.1.10
rem set HELLOCRAB_IOS_SERVER_USER=mac-user
rem set HELLOCRAB_IOS_SERVER_PASSWORD=
rem set HELLOCRAB_IOS_CODESIGN_KEY=Apple Distribution: Company (TEAMID)
rem set HELLOCRAB_IOS_PROVISION=ProvisioningProfileName
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\publish-platform.ps1" -Target ios -Configuration Release -Version 1.0.0
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
