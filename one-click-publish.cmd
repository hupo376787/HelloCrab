@echo off
setlocal
cd /d "%~dp0"

rem Windows entry point. The actual publish logic lives in scripts\one-click-publish.ps1.
rem If arguments are passed on the command line, forward them directly.
if not "%~1"=="" goto run_args

echo.
echo HelloCrab Publish
echo =================
echo [1] Desktop       ^(Windows / Linux / macOS, x64 + ARM64^)
echo [2] Android       ^(APK + AAB^)
echo [3] Browser       ^(WebAssembly^)
echo [4] All targets   ^(Desktop + Android + Browser^)
echo [0] Cancel
echo.
set /p "CHOICE=Select target: "

if "%CHOICE%"=="1" set "PUBLISH_ARGS=-Target desktop"
if "%CHOICE%"=="2" set "PUBLISH_ARGS=-Target android"
if "%CHOICE%"=="3" set "PUBLISH_ARGS=-Target browser"
if "%CHOICE%"=="4" set "PUBLISH_ARGS=-Target all"
if "%CHOICE%"=="0" exit /b 0
if not defined PUBLISH_ARGS (
  echo Invalid selection.
  pause
  exit /b 2
)
goto run_selected

:run_args
set "PUBLISH_ARGS=%*"

:run_selected
where pwsh >nul 2>&1
if %errorlevel%==0 (
  pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\one-click-publish.ps1" %PUBLISH_ARGS%
) else (
  powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\one-click-publish.ps1" %PUBLISH_ARGS%
)

if errorlevel 1 (
  echo.
  echo Publish failed.
  pause
  exit /b 1
)

echo.
echo Publish completed.
echo Output: %~dp0artifacts
echo.
pause
