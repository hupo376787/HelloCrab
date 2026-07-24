#requires -Version 5.1
param(
    [ValidateSet(
        'win-x64', 'win-arm64',
        'linux-x64', 'linux-arm64',
        'osx-x64', 'osx-arm64',
        'browser', 'android', 'ios-simulator', 'ios')]
    [string]$Target = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version = '1.0.0',

    [string]$CodesignKey = $env:HELLOCRAB_IOS_CODESIGN_KEY,
    [string]$CodesignProvision = $env:HELLOCRAB_IOS_PROVISION,
    [string]$CodesignEntitlements = $env:HELLOCRAB_IOS_ENTITLEMENTS,

    [string]$ServerAddress = $env:HELLOCRAB_IOS_SERVER_ADDRESS,
    [string]$ServerUser = $env:HELLOCRAB_IOS_SERVER_USER,
    [string]$ServerPassword = $env:HELLOCRAB_IOS_SERVER_PASSWORD,
    [string]$RemoteDotNetRoot = $env:HELLOCRAB_IOS_REMOTE_DOTNET_ROOT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $root 'artifacts'
$stagingRoot = Join-Path $artifactsRoot '.staging'
$isWindowsHost = $env:OS -eq 'Windows_NT'
$isMacHost = $false

if (-not $isWindowsHost) {
    $unameCommand = Get-Command uname -ErrorAction SilentlyContinue
    if ($null -ne $unameCommand) {
        $isMacHost = ((& $unameCommand.Source -s) -eq 'Darwin')
    }
}

function Assert-Command {
    param([Parameter(Mandatory)][string]$Name)

    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "未找到命令：$Name。请先安装并加入 PATH。"
    }
}

function Invoke-External {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host ''
    $displayArguments = $Arguments -join ' '
    Write-Host ("> {0} {1}" -f $FilePath, $displayArguments) -ForegroundColor Cyan
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "命令执行失败，退出代码：$LASTEXITCODE"
    }
}

function Set-CompatibleXcodeForIos26 {
    if (-not $isMacHost) {
        return
    }

    $developerDirectory = $env:DEVELOPER_DIR
    $xcodeApp = $env:HELLOCRAB_XCODE_PATH

    if (-not [string]::IsNullOrWhiteSpace($xcodeApp)) {
        $developerDirectory = Join-Path $xcodeApp 'Contents/Developer'
    }

    if ([string]::IsNullOrWhiteSpace($developerDirectory) -or
        -not (Test-Path -LiteralPath $developerDirectory -PathType Container)) {
        $candidates = @(
            '/Applications/Xcode_26.0.1.app',
            '/Applications/Xcode_26.0.app')

        $candidates += Get-ChildItem -LiteralPath '/Applications' -Directory -Filter 'Xcode_26.0*.app' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName }

        foreach ($candidate in ($candidates | Select-Object -Unique)) {
            $candidateDeveloperDirectory = Join-Path $candidate 'Contents/Developer'
            if (Test-Path -LiteralPath $candidateDeveloperDirectory -PathType Container) {
                $xcodeApp = $candidate
                $developerDirectory = $candidateDeveloperDirectory
                break
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($developerDirectory) -or
        -not (Test-Path -LiteralPath $developerDirectory -PathType Container)) {
        $installed = Get-ChildItem -LiteralPath '/Applications' -Directory -Filter 'Xcode*.app' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName }
        $installedText = if ($installed) { $installed -join [Environment]::NewLine } else { '（未找到）' }
        throw ("未找到与 net10.0-ios26.0 匹配的 Xcode 26.0。" + [Environment]::NewLine +
            "请安装 Xcode 26.0，或通过 HELLOCRAB_XCODE_PATH 指定其 .app 路径。" + [Environment]::NewLine +
            "当前已安装的 Xcode：" + [Environment]::NewLine + $installedText)
    }

    $env:DEVELOPER_DIR = $developerDirectory
    $versionOutput = & xcodebuild -version
    if ($LASTEXITCODE -ne 0) {
        throw "执行 xcodebuild -version 失败，退出代码：$LASTEXITCODE"
    }

    $versionLine = @($versionOutput)[0]
    if (-not $versionLine.StartsWith('Xcode 26.0', [StringComparison]::Ordinal)) {
        throw ("当前选择的是 $versionLine，但 net10.0-ios26.0 需要 Xcode 26.0。" + [Environment]::NewLine +
            "DEVELOPER_DIR=$developerDirectory" + [Environment]::NewLine +
            '请通过 HELLOCRAB_XCODE_PATH 指向 Xcode 26.0。')
    }

    Write-Host "iOS 构建使用：$versionLine" -ForegroundColor Green
    Write-Host "DEVELOPER_DIR=$developerDirectory"
}

function Reset-Directory {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -Path $Path -ItemType Directory -Force | Out-Null
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "发布目录不存在：$Source"
    }

    Reset-Directory -Path $Destination
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function New-ZipArchive {
    param(
        [Parameter(Mandatory)][string]$PackageDirectory,
        [Parameter(Mandatory)][string]$ArchivePath,
        [switch]$PreferMacDitto
    )

    if (Test-Path -LiteralPath $ArchivePath) {
        Remove-Item -LiteralPath $ArchivePath -Force
    }

    if ($PreferMacDitto -and $isMacHost -and $null -ne (Get-Command ditto -ErrorAction SilentlyContinue)) {
        Invoke-External -FilePath 'ditto' -Arguments @(
            '-c', '-k', '--sequesterRsrc', '--keepParent',
            $PackageDirectory,
            $ArchivePath)
        return
    }

    $zipCommand = Get-Command zip -ErrorAction SilentlyContinue
    if ($null -ne $zipCommand) {
        $parent = Split-Path -Parent $PackageDirectory
        $name = Split-Path -Leaf $PackageDirectory
        Push-Location $parent
        try {
            Invoke-External -FilePath $zipCommand.Source -Arguments @('-q', '-r', $ArchivePath, $name)
        }
        finally {
            Pop-Location
        }
        return
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $PackageDirectory,
        $ArchivePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $true)
}

function Complete-Package {
    param(
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][string]$SourceDirectory,
        [switch]$PreferMacDitto
    )

    New-Item -Path $artifactsRoot -ItemType Directory -Force | Out-Null
    New-Item -Path $stagingRoot -ItemType Directory -Force | Out-Null

    $packageDirectory = Join-Path $stagingRoot $PackageName
    Copy-DirectoryContents -Source $SourceDirectory -Destination $packageDirectory

    $archivePath = Join-Path $artifactsRoot "$PackageName.zip"
    New-ZipArchive `
        -PackageDirectory $packageDirectory `
        -ArchivePath $archivePath `
        -PreferMacDitto:$PreferMacDitto

    Write-Host ''
    Write-Host "打包完成：$archivePath" -ForegroundColor Green
    return $archivePath
}

function Get-UniquePackages {
    param(
        [Parameter(Mandatory)][string]$SearchRoot,
        [Parameter(Mandatory)][string[]]$Extensions
    )

    if (-not (Test-Path -LiteralPath $SearchRoot -PathType Container)) {
        return @()
    }

    $files = Get-ChildItem -LiteralPath $SearchRoot -Recurse -File |
        Where-Object { $Extensions -contains $_.Extension.ToLowerInvariant() }

    return @(
        $files |
            Group-Object Name |
            ForEach-Object {
                $_.Group |
                    Sort-Object Length, LastWriteTimeUtc -Descending |
                    Select-Object -First 1
            })
}

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
    throw 'Version 只能包含字母、数字、点、下划线和连字符。'
}

Assert-Command -Name 'dotnet'
Reset-Directory -Path $stagingRoot

if ($Target -in @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')) {
    $desktopScript = Join-Path $PSScriptRoot 'publish-desktop.ps1'
    & $desktopScript -Runtime $Target -Configuration $Configuration

    $source = Join-Path $root "publish/desktop/$Target"
    $packageName = "HelloCrab-Desktop-$Target-$Version"
    $preferMacDitto = $Target.StartsWith('osx-')
    Complete-Package `
        -PackageName $packageName `
        -SourceDirectory $source `
        -PreferMacDitto:$preferMacDitto | Out-Null
}
elseif ($Target -eq 'browser') {
    $browserScript = Join-Path $PSScriptRoot 'publish-browser.ps1'
    & $browserScript -Configuration $Configuration

    $source = Join-Path $root 'publish/browser'
    Complete-Package `
        -PackageName "HelloCrab-Browser-$Version" `
        -SourceDirectory $source | Out-Null
}
elseif ($Target -eq 'android') {
    $project = Join-Path $root 'src/HelloCrab.Android/HelloCrab.Android.csproj'
    $framework = 'net10.0-android36.0'

    $buildOutput = Join-Path (Split-Path -Parent $project) "bin/$Configuration/$framework"
    if (Test-Path -LiteralPath $buildOutput) {
        Remove-Item -LiteralPath $buildOutput -Recurse -Force
    }

    Invoke-External -FilePath 'dotnet' -Arguments @('workload', 'restore', $project)
    Invoke-External -FilePath 'dotnet' -Arguments @(
        'publish', $project,
        '-c', $Configuration,
        '-f', $framework,
        "-p:ApplicationDisplayVersion=$Version",
        # MSBuild treats ';' as a list separator on the command line. Escape it as %3B
        # so apk and aab stay inside one property value on PowerShell 5.1 and 7+.
        '-p:AndroidPackageFormats=apk%3Baab')

    $searchRoot = Join-Path (Split-Path -Parent $project) "bin/$Configuration/$framework"
    $packages = Get-UniquePackages -SearchRoot $searchRoot -Extensions @('.apk', '.aab')
    if ($packages.Count -eq 0) {
        throw "没有在 $searchRoot 中找到 APK 或 AAB。"
    }

    $packageName = "HelloCrab-Android-$Version"
    $packageDirectory = Join-Path $stagingRoot $packageName
    Reset-Directory -Path $packageDirectory
    foreach ($package in $packages) {
        Copy-Item -LiteralPath $package.FullName -Destination $packageDirectory -Force
        Write-Host "已收集：$($package.FullName)"
    }

    $archivePath = Join-Path $artifactsRoot "$packageName.zip"
    New-ZipArchive -PackageDirectory $packageDirectory -ArchivePath $archivePath
    Write-Host ''
    Write-Host "打包完成：$archivePath" -ForegroundColor Green
}
elseif ($Target -eq 'ios-simulator') {
    if (-not $isMacHost) {
        throw 'iOS Simulator 构建必须在 macOS 上运行。'
    }

    $project = Join-Path $root 'src/HelloCrab.iOS/HelloCrab.iOS.csproj'
    $framework = 'net10.0-ios26.0'
    $runtime = 'iossimulator-arm64'

    Set-CompatibleXcodeForIos26
    $buildOutput = Join-Path (Split-Path -Parent $project) "bin/$Configuration/$framework"
    if (Test-Path -LiteralPath $buildOutput) {
        Remove-Item -LiteralPath $buildOutput -Recurse -Force
    }

    Invoke-External -FilePath 'dotnet' -Arguments @('workload', 'restore', $project)
    Invoke-External -FilePath 'dotnet' -Arguments @(
        'build', $project,
        '-c', $Configuration,
        '-f', $framework,
        "-p:RuntimeIdentifier=$runtime",
        '-p:EnableCodeSigning=false',
        "-p:ApplicationDisplayVersion=$Version")

    $searchRoot = Join-Path (Split-Path -Parent $project) "bin/$Configuration/$framework/$runtime"
    $app = Get-ChildItem -LiteralPath $searchRoot -Directory -Filter '*.app' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $app) {
        throw "没有在 $searchRoot 中找到 iOS Simulator .app。"
    }

    $packageName = "HelloCrab-iOS-Simulator-arm64-$Version"
    $packageDirectory = Join-Path $stagingRoot $packageName
    Reset-Directory -Path $packageDirectory
    Copy-Item -LiteralPath $app.FullName -Destination $packageDirectory -Recurse -Force

    $readme = @(
        'This package is an unsigned Apple Silicon iOS Simulator build.',
        'It is intended for testing with Xcode Simulator and cannot be installed on a physical iPhone or iPad.',
        'Configure the GitHub iOS signing secrets to also produce a signed IPA for physical devices.'
    ) -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $packageDirectory 'README.txt') -Value $readme -Encoding UTF8

    $archivePath = Join-Path $artifactsRoot "$packageName.zip"
    New-ZipArchive -PackageDirectory $packageDirectory -ArchivePath $archivePath -PreferMacDitto
    Write-Host ''
    Write-Host "打包完成：$archivePath" -ForegroundColor Green
}
else {
    $project = Join-Path $root 'src/HelloCrab.iOS/HelloCrab.iOS.csproj'
    $framework = 'net10.0-ios26.0'

    if (-not $isMacHost -and [string]::IsNullOrWhiteSpace($ServerAddress)) {
        $messageLines = @(
            'iOS 发布需要 macOS + Xcode。若从 Windows 远程构建，请设置：',
            'HELLOCRAB_IOS_SERVER_ADDRESS、HELLOCRAB_IOS_SERVER_USER，',
            '以及可选的 HELLOCRAB_IOS_SERVER_PASSWORD、HELLOCRAB_IOS_REMOTE_DOTNET_ROOT。')
        throw ($messageLines -join [Environment]::NewLine)
    }

    if ($isMacHost) {
        Set-CompatibleXcodeForIos26
    }
    $buildOutput = Join-Path (Split-Path -Parent $project) "bin/$Configuration/$framework"
    if (Test-Path -LiteralPath $buildOutput) {
        Remove-Item -LiteralPath $buildOutput -Recurse -Force
    }

    Invoke-External -FilePath 'dotnet' -Arguments @('workload', 'restore', $project)

    $arguments = @(
        'publish', $project,
        '-c', $Configuration,
        '-f', $framework,
        '-p:RuntimeIdentifier=ios-arm64',
        '-p:ArchiveOnBuild=true',
        "-p:ApplicationDisplayVersion=$Version")

    if (-not [string]::IsNullOrWhiteSpace($CodesignKey)) {
        $arguments += "-p:CodesignKey=$CodesignKey"
    }
    if (-not [string]::IsNullOrWhiteSpace($CodesignProvision)) {
        $arguments += "-p:CodesignProvision=$CodesignProvision"
    }
    if (-not [string]::IsNullOrWhiteSpace($CodesignEntitlements)) {
        $arguments += "-p:CodesignEntitlements=$CodesignEntitlements"
    }

    if (-not $isMacHost) {
        $arguments += "-p:ServerAddress=$ServerAddress"
        if (-not [string]::IsNullOrWhiteSpace($ServerUser)) {
            $arguments += "-p:ServerUser=$ServerUser"
        }
        if (-not [string]::IsNullOrWhiteSpace($ServerPassword)) {
            $arguments += "-p:ServerPassword=$ServerPassword"
        }
        if (-not [string]::IsNullOrWhiteSpace($RemoteDotNetRoot)) {
            $arguments += "-p:_DotNetRootRemoteDirectory=$RemoteDotNetRoot"
        }
        $arguments += '-p:TcpPort=58181'
    }

    Invoke-External -FilePath 'dotnet' -Arguments $arguments

    $searchRoot = Join-Path (Split-Path -Parent $project) "bin/$Configuration/$framework"
    $packages = Get-UniquePackages -SearchRoot $searchRoot -Extensions @('.ipa')
    if ($packages.Count -eq 0) {
        throw "没有在 $searchRoot 中找到 IPA。请检查 Apple 证书和 Provisioning Profile。"
    }

    $packageName = "HelloCrab-iOS-$Version"
    $packageDirectory = Join-Path $stagingRoot $packageName
    Reset-Directory -Path $packageDirectory
    foreach ($package in $packages) {
        Copy-Item -LiteralPath $package.FullName -Destination $packageDirectory -Force
        Write-Host "已收集：$($package.FullName)"
    }

    $archivePath = Join-Path $artifactsRoot "$packageName.zip"
    New-ZipArchive -PackageDirectory $packageDirectory -ArchivePath $archivePath -PreferMacDitto
    Write-Host ''
    Write-Host "打包完成：$archivePath" -ForegroundColor Green
}
