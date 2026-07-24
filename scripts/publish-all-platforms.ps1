#requires -Version 5.1
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0',
    [switch]$IncludeIos
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$targets = @(
    'win-x64', 'win-arm64',
    'linux-x64', 'linux-arm64',
    'osx-x64', 'osx-arm64',
    'browser', 'android')

$isWindowsHost = $env:OS -eq 'Windows_NT'
$isMacHost = $false
if (-not $isWindowsHost) {
    $unameCommand = Get-Command uname -ErrorAction SilentlyContinue
    if ($null -ne $unameCommand) {
        $isMacHost = ((& $unameCommand.Source -s) -eq 'Darwin')
    }
}

if ($isMacHost -or $IncludeIos) {
    $targets += 'ios'
}

$script = Join-Path $PSScriptRoot 'publish-platform.ps1'
$failures = New-Object System.Collections.Generic.List[string]

foreach ($target in $targets) {
    Write-Host ''
    Write-Host '============================================================' -ForegroundColor DarkCyan
    Write-Host "开始发布：$target" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor DarkCyan

    try {
        & $script -Target $target -Configuration $Configuration -Version $Version
    }
    catch {
        $failures.Add("$target：$($_.Exception.Message)")
        Write-Warning "发布失败：$target"
    }
}

Write-Host ''
Write-Host "发布产物目录：$(Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts')"

if ($failures.Count -gt 0) {
    Write-Warning '以下目标发布失败：'
    foreach ($failure in $failures) {
        Write-Warning "  $failure"
    }
    exit 1
}

Write-Host '全部目标发布完成。' -ForegroundColor Green
