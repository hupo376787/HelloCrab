#requires -Version 5.1
param(
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/HelloCrab.Browser/HelloCrab.Browser.csproj'
$output = Join-Path $root 'publish/browser'
$rawOutput = Join-Path $root 'publish/browser-raw'

foreach ($path in @($output, $rawOutput)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
New-Item -Path $output -ItemType Directory -Force | Out-Null
New-Item -Path $rawOutput -ItemType Directory -Force | Out-Null

& dotnet workload restore $project
if ($LASTEXITCODE -ne 0) {
    throw "Browser workload restore 失败，退出代码：$LASTEXITCODE"
}

& dotnet publish $project -c $Configuration -o $rawOutput
if ($LASTEXITCODE -ne 0) {
    throw "Browser publish 失败，退出代码：$LASTEXITCODE"
}

$wwwroot = Join-Path $rawOutput 'wwwroot'
$staticRoot = if (Test-Path -LiteralPath (Join-Path $wwwroot 'index.html') -PathType Leaf) {
    $wwwroot
}
elseif (Test-Path -LiteralPath (Join-Path $rawOutput 'index.html') -PathType Leaf) {
    $rawOutput
}
else {
    throw "Browser publish 输出中未找到 index.html：$rawOutput"
}

Get-ChildItem -LiteralPath $staticRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $output -Recurse -Force
}

Remove-Item -LiteralPath $rawOutput -Recurse -Force
Write-Host "Browser static site published to $output" -ForegroundColor Green
