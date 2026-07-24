# 桌面端跨平台编译

Desktop 与 Core 均以 `net10.0` 为目标框架。生成或发布前请确认已安装 .NET 10 SDK：

```bash
dotnet --list-sdks
```


## 为什么不能只看 OutputType

`OutputType` 只描述程序集/应用宿主的类型，并不决定目标系统。目标系统由 `RuntimeIdentifier`（RID）和 `dotnet publish -r` 决定。

本项目 Desktop 工程统一使用 `WinExe`：

- Windows 启动时不会附带控制台窗口；
- `WinExe` 不会把 Avalonia Desktop 限制为 Windows，Linux 和 macOS 仍由发布 RID 决定；
- 所有平台都启用 `UseAppHost=true`，因此会生成目标系统可直接启动的原生 AppHost。

## 单个平台发布

```bash
# Linux x64
dotnet publish src/HelloCrab.Desktop/HelloCrab.Desktop.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:UseAppHost=true -o publish/desktop/linux-x64

# macOS Apple Silicon
dotnet publish src/HelloCrab.Desktop/HelloCrab.Desktop.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -p:UseAppHost=true -o publish/desktop/osx-arm64/raw

# Windows x64
dotnet publish src/HelloCrab.Desktop/HelloCrab.Desktop.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:UseAppHost=true -o publish/desktop/win-x64
```

推荐直接使用 `scripts/publish-desktop.ps1` 或 `scripts/publish-desktop.sh`，因为脚本还会完成 macOS `.app` 封装和 Linux 启动文件处理。

## 输出文件

```text
Windows: HelloCrab.exe
Linux:   HelloCrab
macOS:   HelloCrab.app/Contents/MacOS/HelloCrab
```

## 注意

- Playwright Chromium 需要在每台目标机器上单独安装；程序内按钮默认安装到 `程序目录/chromium/`，启动时优先使用该目录，找不到时再兼容 `%LOCALAPPDATA%\ms-playwright` 等系统默认缓存。
- “检测视频是否有声音”默认关闭；开启时需要 FFmpeg/ffprobe。
- Windows 可在程序内点击“下载 FFmpeg”自动安装到 `程序目录/ffmpeg/bin/`；也可手动放入约定目录或系统 PATH。
- macOS 正式分发需要代码签名和公证。
- Linux 目标机需要 Avalonia/Chromium 所需的系统图形库。
## 自动压缩发布包

若需要直接得到可分发 ZIP，而不是仅生成 `publish/` 目录，使用：

```powershell
./scripts/publish-platform.ps1 -Target win-x64 -Version 1.0.0
```

```bash
./scripts/publish-platform.sh linux-x64 Release 1.0.0
```

最终文件位于 `artifacts/`。Browser、Android 和 iOS 的一键脚本及签名说明见 `PUBLISH-ALL-PLATFORMS.md`。


## GitHub Actions 自动构建与 Release

仓库中的 `.github/workflows/build-all-platforms.yml` 会在以下情况运行：

- 代码推送到 `main` 分支；
- 推送 `v*` 版本标签；
- 在 GitHub Actions 页面手动运行。

工作流包含：

```text
Windows x64 / ARM64
Linux x64 / ARM64
macOS Intel / Apple Silicon
Browser WebAssembly
Android APK + AAB
iOS Simulator Apple Silicon
可选的签名 iOS IPA
```

普通 `main` 提交只生成 Actions Artifacts；推送 `v1.0.0` 等标签时，会等待全部平台成功，然后创建或更新对应 Release，并上传全部包和 `SHA256SUMS.txt`。

Linux/macOS 脚本始终通过 Bash 运行并恢复执行权限；工作流明确安装 .NET 10，Android 使用 Java 17，iOS 使用 `macos-26` Runner。完整使用方法见 `GITHUB-ACTIONS-RELEASE.md`。
