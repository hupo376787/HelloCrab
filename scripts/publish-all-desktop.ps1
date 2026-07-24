#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$runtimes = @('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')
foreach ($runtime in $runtimes) {
    & (Join-Path $PSScriptRoot 'publish-desktop.ps1') -Runtime $runtime -Configuration Release
}
