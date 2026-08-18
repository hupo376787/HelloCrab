#requires -Version 5.1
param(
    [ValidateSet('all', 'desktop', 'browser', 'android')]
    [string]$Target = 'all',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version = '1.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Root 'artifacts'
$PublishPlatform = Join-Path $PSScriptRoot 'publish-platform.ps1'

function Invoke-Target {
    param([Parameter(Mandatory)][string]$Name)

    Write-Host ''
    Write-Host "=== $Name ===" -ForegroundColor Yellow
    & $PublishPlatform -Target $Name -Configuration $Configuration -Version $Version
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
