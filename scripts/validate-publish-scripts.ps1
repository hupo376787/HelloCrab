#requires -Version 5.1
param(
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$parseFailures = New-Object System.Collections.Generic.List[string]

Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File | ForEach-Object {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $_.FullName,
        [ref]$tokens,
        [ref]$errors) | Out-Null

    foreach ($parseError in $errors) {
        $parseFailures.Add((
            '{0}:{1}:{2} {3}' -f
            $_.Name,
            $parseError.Extent.StartLineNumber,
            $parseError.Extent.StartColumnNumber,
            $parseError.Message))
    }
}

$unsafeMsBuildArguments = New-Object System.Collections.Generic.List[string]
Get-ChildItem -LiteralPath $PSScriptRoot -File |
    Where-Object { $_.Extension -in @('.ps1', '.sh') } |
    ForEach-Object {
        $content = Get-Content -LiteralPath $_.FullName -Raw
        if ($content -match 'AndroidPackageFormats=apk[;]aab') {
            $unsafeMsBuildArguments.Add((
                '{0} 包含未转义的 AndroidPackageFormats 分号；请使用 apk%3Baab。' -f $_.Name))
        }
    }

$missingTargets = New-Object System.Collections.Generic.List[string]
Get-ChildItem -LiteralPath (Join-Path $root 'one-click-publish') -Filter '*.bat' -File |
    ForEach-Object {
        $content = Get-Content -LiteralPath $_.FullName -Raw
        $matches = [regex]::Matches($content, '-File\s+"%~dp0\.\.\\scripts\\([^\"]+\.ps1)"')
        foreach ($match in $matches) {
            $target = Join-Path $PSScriptRoot $match.Groups[1].Value
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                $missingTargets.Add(('{0} -> {1}' -f $_.Name, $target))
            }
        }
    }

if ($parseFailures.Count -gt 0 -or $missingTargets.Count -gt 0 -or $unsafeMsBuildArguments.Count -gt 0) {
    foreach ($failure in $parseFailures) {
        Write-Host "PowerShell 语法错误：$failure" -ForegroundColor Red
    }
    foreach ($failure in $missingTargets) {
        Write-Host "BAT 引用的脚本不存在：$failure" -ForegroundColor Red
    }
    foreach ($failure in $unsafeMsBuildArguments) {
        Write-Host "MSBuild 参数错误：$failure" -ForegroundColor Red
    }
    exit 1
}

if (-not $Quiet) {
    Write-Host '发布脚本检查通过。' -ForegroundColor Green
}
