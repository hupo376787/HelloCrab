# 各平台一键打包发布

## 产物

统一输出到：

```text
artifacts/
```

默认版本号为 `1.0.0`，文件示例：

```text
HelloCrab-Desktop-win-x64-1.0.0.zip
HelloCrab-Desktop-linux-arm64-1.0.0.zip
HelloCrab-Desktop-osx-arm64-1.0.0.zip
HelloCrab-Browser-1.0.0.zip
HelloCrab-Android-1.0.0.zip
HelloCrab-iOS-1.0.0.zip
```

Android 汇总 ZIP 内包含生成的 APK 和 AAB。iOS 汇总 ZIP 内包含签名后的 IPA。

## 单个平台

PowerShell：

```powershell
./scripts/publish-platform.ps1 -Target win-x64
./scripts/publish-platform.ps1 -Target linux-arm64
./scripts/publish-platform.ps1 -Target osx-arm64
./scripts/publish-platform.ps1 -Target browser
./scripts/publish-platform.ps1 -Target android
./scripts/publish-platform.ps1 -Target ios
```

指定版本号：

```powershell
./scripts/publish-platform.ps1 -Target win-x64 -Version 1.2.0
```

Bash：

```bash
./scripts/publish-platform.sh linux-x64 Release 1.0.0
./scripts/publish-platform.sh osx-arm64 Release 1.0.0
./scripts/publish-platform.sh browser Release 1.0.0
./scripts/publish-platform.sh android Release 1.0.0
./scripts/publish-platform.sh ios Release 1.0.0
```

## 一次发布全部可用平台

PowerShell：

```powershell
./scripts/publish-all-platforms.ps1 -Version 1.0.0
```

默认包括：

```text
win-x64, win-arm64
linux-x64, linux-arm64
osx-x64, osx-arm64
browser, android
```

在 macOS 上会额外包含 iOS。在 Windows 上通过配对 Mac 构建 iOS 时：

```powershell
./scripts/publish-all-platforms.ps1 -IncludeIos
```

Bash：

```bash
./scripts/publish-all-platforms.sh Release 1.0.0
```

强制包含 iOS：

```bash
INCLUDE_IOS=1 ./scripts/publish-all-platforms.sh Release 1.0.0
```

批量脚本会继续处理其他平台，最后汇总失败目标并返回非零退出代码。

## 环境要求

基础环境：

```bash
dotnet --list-sdks
```

需要 .NET 10 SDK。额外 workload：

```bash
dotnet workload install wasm-tools
dotnet workload install android
dotnet workload install ios
```

脚本不会自动安装 workload，只会执行 `dotnet workload restore`，避免未经确认修改开发环境。

### Android

Android Release 同时请求：

```text
APK + AAB
```

还需要可用的 Android SDK、Java/JDK 和签名配置。未配置正式签名时，生成结果只适合测试，不能直接提交应用商店。

### iOS

iOS IPA 需要 Apple 证书和 Provisioning Profile。macOS 可设置：

```bash
export HELLOCRAB_IOS_CODESIGN_KEY='Apple Distribution: Company (TEAMID)'
export HELLOCRAB_IOS_PROVISION='ProvisioningProfileName'
```

Windows 远程构建还可设置：

```powershell
$env:HELLOCRAB_IOS_SERVER_ADDRESS = '192.168.1.10'
$env:HELLOCRAB_IOS_SERVER_USER = 'mac-user'
$env:HELLOCRAB_IOS_SERVER_PASSWORD = 'password'
$env:HELLOCRAB_IOS_REMOTE_DOTNET_ROOT = '/Users/mac-user/Library/Caches/Xamarin/XMA/SDKs/dotnet/'
```

密码可以省略，以便使用已保存的 SSH 密钥。

### macOS 桌面版

脚本生成 `.app` 并压缩。在 macOS 主机上优先使用 `ditto`，以保留 bundle 元数据。正式公开分发仍需代码签名、公证；需要 DMG 时可在 macOS 上继续运行：

```bash
hdiutil create -volname HelloCrab -srcfolder artifacts/.staging/HelloCrab-Desktop-osx-arm64-1.0.0/HelloCrab.app -ov HelloCrab.dmg
```

### Linux

桌面发布为自包含文件。目标系统仍需要 Avalonia 使用的图形和字体系统库。现有发布目录中会附带桌面快捷方式模板和安装脚本。

## 修改默认版本号

`one-click-publish` 中的快捷入口默认写为 `1.0.0`。发布新版本时，可以：

1. 直接使用命令行传入 `-Version`；或
2. 批量替换快捷入口中的 `1.0.0`。

版本号只用于压缩包名称和移动端显示版本，不会自动修改源码中的程序集版本。


## 发布脚本自检

Windows 可先双击 `one-click-publish/Validate-scripts.bat`，它会使用 PowerShell 自带解析器检查全部 `.ps1` 文件，并检查 BAT 引用路径。

### Android APK + AAB 参数说明

脚本使用 `-p:AndroidPackageFormats=apk%3Baab`。`%3B` 是 MSBuild 对分号的转义，
可避免 PowerShell/MSBuild 把 `aab` 误解析为额外开关。最终仍会同时生成 APK 和 AAB。
