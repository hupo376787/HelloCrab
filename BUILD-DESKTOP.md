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


## GitHub Actions 自动构建

仓库中的 `.github/workflows/build-desktop.yml` 会在以下情况运行：

- 代码推送到 `main` 分支；
- 在 GitHub 的 Actions 页面手动点击运行。

工作流会分别生成 Windows x64/ARM64、Linux x64/ARM64、macOS Intel/Apple Silicon 六个平台的产物。当前工作流明确安装 .NET 10 SDK，不依赖 Runner 偶然预装的 SDK；Linux 和 macOS 发布前会恢复 Shell 脚本执行权限，并始终通过 `bash` 调用脚本，避免从 Windows 提交后出现退出代码 126。

如不希望每次推送都自动构建，可删除工作流中：

```yaml
push:
  branches: [main]
```

保留 `workflow_dispatch` 后，仍可在 Actions 页面手动构建。
