#requires -Version 5.1
param(
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/HelloCrab.Browser/HelloCrab.Browser.csproj'
$output = Join-Path $root 'publish/browser'

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}
New-Item $output -ItemType Directory -Force | Out-Null

& dotnet workload restore $project
if ($LASTEXITCODE -ne 0) {
    throw "Browser workload restore 失败，退出代码：$LASTEXITCODE"
}

& dotnet publish $project -c $Configuration -o $output
if ($LASTEXITCODE -ne 0) {
    throw "Browser publish 失败，退出代码：$LASTEXITCODE"
}

Write-Host "Browser remote published to $output"
