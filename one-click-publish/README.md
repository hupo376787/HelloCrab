# HelloCrab 一键发布入口

这些文件只是便捷入口，真正逻辑位于：

```text
scripts/publish-platform.ps1
scripts/publish-platform.sh
scripts/publish-all-platforms.ps1
scripts/publish-all-platforms.sh
```

## Windows 双击

| 文件 | 产物 |
|---|---|
| `Windows-x64.bat` | Windows x64 桌面版 ZIP |
| `Windows-arm64.bat` | Windows ARM64 桌面版 ZIP |
| `Browser.bat` | Browser/WASM 静态站点 ZIP |
| `Android.bat` | APK、AAB 及汇总 ZIP |
| `iOS-remote.bat` | 通过配对 Mac 生成 IPA |
| `All-supported.bat` | 六个桌面 RID、Browser、Android |

## Linux

```bash
./Linux-x64.sh
./Linux-arm64.sh
./Browser.sh
./Android.sh
./All-supported.sh
```

## macOS 双击

Finder 中双击 `.command` 文件：

```text
macOS-arm64.command
iOS.command
Browser.command
Android.command
All-supported.command
```

首次从压缩包解压后若系统移除了执行权限，可运行：

```bash
chmod +x one-click-publish/*.command one-click-publish/*.sh scripts/*.sh
```

所有最终压缩包统一输出到项目根目录的 `artifacts/`。


## 发布脚本自检

Windows 可先双击 `one-click-publish/Validate-scripts.bat`，它会使用 PowerShell 自带解析器检查全部 `.ps1` 文件，并检查 BAT 引用路径。
