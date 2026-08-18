@echo off
chcp 65001 >nul
setlocal EnableExtensions
cd /d "%~dp0"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\validate-publish-scripts.ps1" -Quiet
if errorlevel 1 goto :validation_failed

:menu
cls
echo ============================================================
echo HelloCrab 一键打包发布
echo ============================================================
echo.
echo   [1] Windows       x64 + ARM64
echo   [2] Linux         x64 + ARM64
echo   [3] macOS         x64 + ARM64
echo   [4] Browser       WebAssembly
echo   [5] Android       APK + AAB
echo   [6] 全部打包       Windows + Linux + macOS + Browser + Android
echo   [0] 退出
echo.
echo iOS 不包含在本发布菜单中。
echo 所有产物统一输出到 artifacts 目录。
echo.
choice /C 1234560 /N /M "请选择 [1-6,0]: "

if errorlevel 7 goto :exit
if errorlevel 6 goto :publish_all
if errorlevel 5 goto :publish_android
if errorlevel 4 goto :publish_browser
if errorlevel 3 goto :publish_macos
if errorlevel 2 goto :publish_linux
if errorlevel 1 goto :publish_windows

:publish_windows
call :publish_pair win-x64 win-arm64 "Windows x64 / ARM64"
goto :finish_publish

:publish_linux
call :publish_pair linux-x64 linux-arm64 "Linux x64 / ARM64"
goto :finish_publish

:publish_macos
call :publish_pair osx-x64 osx-arm64 "macOS x64 / ARM64"
goto :finish_publish

:publish_browser
call :publish_target browser "Browser WebAssembly"
goto :finish_publish

:publish_android
call :publish_target android "Android APK + AAB"
goto :finish_publish

:publish_all
echo.
echo ============================================================
echo 开始全部打包
echo ============================================================
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish-all-platforms.ps1" -Configuration Release
set "EXIT_CODE=%ERRORLEVEL%"
goto :show_result

:publish_pair
set "PAIR_TARGET_1=%~1"
set "PAIR_TARGET_2=%~2"
set "PAIR_NAME=%~3"
echo.
echo ============================================================
echo 开始打包：%PAIR_NAME%
echo ============================================================
call :run_target "%PAIR_TARGET_1%"
if errorlevel 1 exit /b 1
call :run_target "%PAIR_TARGET_2%"
exit /b %ERRORLEVEL%

:publish_target
set "SINGLE_TARGET=%~1"
set "SINGLE_NAME=%~2"
echo.
echo ============================================================
echo 开始打包：%SINGLE_NAME%
echo ============================================================
call :run_target "%SINGLE_TARGET%"
exit /b %ERRORLEVEL%

:run_target
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish-platform.ps1" -Target "%~1" -Configuration Release
exit /b %ERRORLEVEL%

:finish_publish
set "EXIT_CODE=%ERRORLEVEL%"

:show_result
echo.
if "%EXIT_CODE%"=="0" (
    echo ============================================================
    echo 打包完成。产物目录：%~dp0artifacts
    echo ============================================================
) else (
    echo ============================================================
    echo 打包失败，错误代码：%EXIT_CODE%
    echo 请查看上方日志定位问题。
    echo ============================================================
)
echo.
choice /C M0 /N /M "按 M 返回菜单，按 0 退出: "
if errorlevel 2 goto :exit_with_code
goto :menu

:validation_failed
set "EXIT_CODE=%ERRORLEVEL%"
echo.
echo 发布脚本检查失败，错误代码：%EXIT_CODE%
echo.
pause
exit /b %EXIT_CODE%

:exit_with_code
exit /b %EXIT_CODE%

:exit
exit /b 0
