#requires -Version 5.1
param(
    [ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$isWindowsHost = $env:OS -eq 'Windows_NT'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/HelloCrab.Desktop/HelloCrab.Desktop.csproj'
$outputRoot = Join-Path $root "publish/desktop/$Runtime"
$rawOutput = $outputRoot

if (Test-Path $outputRoot) {
    Remove-Item $outputRoot -Recurse -Force
}
New-Item $outputRoot -ItemType Directory -Force | Out-Null

if ($Runtime.StartsWith('osx-')) {
    $rawOutput = Join-Path $outputRoot 'raw'
}

$publishArguments = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-o', $rawOutput,
    '-p:UseAppHost=true',
    '-p:PublishSingleFile=false'
)


& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，运行时：$Runtime，退出代码：$LASTEXITCODE"
}

if ($Runtime.StartsWith('osx-')) {
    $app = Join-Path $outputRoot 'HelloCrab.app'
    $macOs = Join-Path $app 'Contents/MacOS'
    $resources = Join-Path $app 'Contents/Resources'
    New-Item $macOs -ItemType Directory -Force | Out-Null
    New-Item $resources -ItemType Directory -Force | Out-Null

    Copy-Item (Join-Path $rawOutput '*') $macOs -Recurse -Force
    Copy-Item (Join-Path $root 'packaging/macos/Info.plist') `
        (Join-Path $app 'Contents/Info.plist') -Force
    Copy-Item (Join-Path $root 'src/HelloCrab.Desktop/Assets/app-icon.icns') `
        (Join-Path $resources 'app-icon.icns') -Force

    Remove-Item $rawOutput -Recurse -Force
    Write-Host "macOS app bundle: $app"
    if ($isWindowsHost) {
        Write-Warning '从 Windows 生成的 .app 复制到 macOS 后，请执行 chmod +x HelloCrab.app/Contents/MacOS/HelloCrab。建议在 macOS 或 CI 的 macOS runner 上发布。'
    }
}
elseif ($Runtime.StartsWith('linux-')) {
    $assets = Join-Path $outputRoot 'Assets'
    New-Item $assets -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $root 'src/HelloCrab.Desktop/Assets/app-icon.png') `
        (Join-Path $assets 'app-icon.png') -Force
    Copy-Item (Join-Path $root 'packaging/linux/HelloCrab.desktop') `
        (Join-Path $outputRoot 'HelloCrab.desktop.template') -Force
    Write-Host "Linux executable: $(Join-Path $outputRoot 'HelloCrab')"
}
else {
    Write-Host "Windows executable: $(Join-Path $outputRoot 'HelloCrab.exe')"
}

Write-Host "Published to $outputRoot"
Write-Host "Chromium 请在目标机器中通过程序的‘安装 Chromium’按钮安装。"
