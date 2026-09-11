#requires -Version 5.1
param(
    [ValidateSet('all', 'desktop', 'browser', 'android')]
    [string]$Target = 'all',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version = '2.1.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Root 'artifacts'
$PublishPlatform = Join-Path $PSScriptRoot 'publish-platform.ps1'

function Export-AndroidArtifacts {
    $packageName = "HelloCrab-Android-$Version"
    $packageDirectory = Join-Path (Join-Path $Artifacts '.staging') $packageName

    if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
        throw "Android 临时输出目录不存在：$packageDirectory"
    }

    $packages = @(
        Get-ChildItem -LiteralPath $packageDirectory -File |
            Where-Object { $_.Extension.ToLowerInvariant() -in @('.apk', '.aab') }
    )
    if ($packages.Count -eq 0) {
        throw "没有在 $packageDirectory 中找到 APK 或 AAB。"
    }

    New-Item -Path $Artifacts -ItemType Directory -Force | Out-Null

    foreach ($package in $packages) {
        $destination = Join-Path $Artifacts $package.Name
        Copy-Item -LiteralPath $package.FullName -Destination $destination -Force
        Write-Host "Android 输出：$destination" -ForegroundColor Green
    }

    # one-click-publish 不再保留 Android ZIP；APK/AAB 直接位于 artifacts 根目录。
    $archivePath = Join-Path $Artifacts "$packageName.zip"
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
}

function Invoke-Target {
    param([Parameter(Mandatory)][string]$Name)

    Write-Host ''
    Write-Host "=== $Name ===" -ForegroundColor Yellow
    & $PublishPlatform -Target $Name -Configuration $Configuration -Version $Version

    if ($Name -eq 'android') {
        Export-AndroidArtifacts
    }
}

function Publish-Desktop {
    foreach ($rid in @(
        'win-x64', 'win-arm64',
        'linux-x64', 'linux-arm64',
        'osx-x64', 'osx-arm64')) {
        Invoke-Target $rid
    }
}

switch ($Target) {
    'desktop' { Publish-Desktop }
    'browser' { Invoke-Target 'browser' }
    'android' { Invoke-Target 'android' }
    'all' {
        Publish-Desktop
        Invoke-Target 'android'
        Invoke-Target 'browser'
    }
}

Write-Host ''
Write-Host "Publish finished. Artifacts: $Artifacts" -ForegroundColor Green
