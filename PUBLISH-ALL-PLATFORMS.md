# 各平台一键打包发布

## 最简单的发布方式

Windows 上直接双击项目根目录的：

```text
one-click-publish.cmd
```

启动后会先执行发布脚本自检，然后显示平台选择菜单：

```text
[1] Windows       x64 + ARM64
[2] Linux         x64 + ARM64
[3] macOS         x64 + ARM64
[4] Browser       WebAssembly
[5] Android       APK + AAB
[6] 全部打包       Windows + Linux + macOS + Browser + Android
[0] 退出
```

选择 Windows、Linux 或 macOS 时，会一次生成该平台的 x64 和 ARM64 两个桌面包；也可以只构建 Browser、Android，或选择“全部打包”。iOS 不包含在这个发布菜单中。所有产物统一输出到：

```text
artifacts/
```

默认版本号为 `1.0.0`。

## 命令行发布

需要单独发布某个平台时，可继续使用底层脚本：

```powershell
./scripts/publish-platform.ps1 -Target win-x64
./scripts/publish-platform.ps1 -Target linux-arm64
./scripts/publish-platform.ps1 -Target osx-arm64
./scripts/publish-platform.ps1 -Target browser
./scripts/publish-platform.ps1 -Target android
```

指定版本号：

```powershell
./scripts/publish-platform.ps1 -Target win-x64 -Version 1.2.0
```

一次发布全部一键目标：

```powershell
./scripts/publish-all-platforms.ps1 -Configuration Release -Version 1.0.0
```

Bash 入口仍保留给 GitHub Actions、Linux 和 macOS 构建环境：

```bash
./scripts/publish-platform.sh linux-x64 Release 1.0.0
./scripts/publish-platform.sh osx-arm64 Release 1.0.0
./scripts/publish-platform.sh browser Release 1.0.0
./scripts/publish-platform.sh android Release 1.0.0
```

## 环境要求

基础环境：

```bash
dotnet --list-sdks
```

需要 .NET 10 SDK。Browser 和 Android 还需要对应 workload：

```bash
dotnet workload install wasm-tools
dotnet workload install android
```

脚本不会自动安装 workload，只会执行 `dotnet workload restore`，避免未经确认修改开发环境。

### Android

Android Release 同时请求：

```text
APK + AAB
```

还需要可用的 Android SDK、Java/JDK 和签名配置。未配置正式签名时，生成结果只适合测试，不能直接提交应用商店。

### macOS 桌面版

脚本生成 `.app` 并压缩。在 macOS 主机上优先使用 `ditto`，以保留 bundle 元数据。正式公开分发仍需代码签名、公证。

### Linux

桌面发布为自包含文件。目标系统仍需要 Avalonia 使用的图形和字体系统库。

## 发布脚本自检

`one-click-publish.cmd` 会自动先运行：

```powershell
./scripts/validate-publish-scripts.ps1
```

无需再单独双击旧的 `Validate-scripts.bat`。

## GitHub Actions 与 Release

仓库的 `.github/workflows/build-all-platforms.yml` 自动构建桌面端、Browser 和 Android。iOS 已从 GitHub Actions 构建中移除。推送 `v*` 标签时，会把这些产物和 SHA256 校验文件发布到同一个 GitHub Release。
