@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo ============================================================
echo HelloCrab 一键打包发布
echo ============================================================
echo 将发布：
echo   Windows x64 / ARM64
echo   Linux x64 / ARM64
echo   macOS x64 / ARM64
echo   Browser WebAssembly
echo   Android APK + AAB
echo.
echo iOS 不包含在此一键发布流程中。
echo 所有产物输出到 artifacts 目录。
echo.

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\validate-publish-scripts.ps1" -Quiet
if errorlevel 1 goto :validation_failed

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish-all-platforms.ps1" -Configuration Release
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="0" (
    echo ============================================================
    echo 发布完成。产物目录：%~dp0artifacts
    echo ============================================================
) else (
    echo ============================================================
    echo 发布未全部成功，错误代码：%EXIT_CODE%
    echo 请查看上方日志定位失败的平台。
    echo ============================================================
)
goto :finish

:validation_failed
set "EXIT_CODE=%ERRORLEVEL%"
echo.
echo 发布脚本检查失败，错误代码：%EXIT_CODE%

:finish
echo.
pause
exit /b %EXIT_CODE%
