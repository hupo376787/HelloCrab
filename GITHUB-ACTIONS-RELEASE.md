# GitHub Actions 全平台构建与 Release

工作流文件：

```text
.github/workflows/build-all-platforms.yml
```

## 自动构建范围

每次推送到 `main`，或者在 Actions 页面手动运行时，会并行构建：

```text
Windows x64 / ARM64
Linux x64 / ARM64
macOS Intel / Apple Silicon
Browser WebAssembly
Android APK + AAB
iOS Simulator Apple Silicon
```

构建产物会保存在对应 Actions 运行记录的 Artifacts 中，默认保留 14 天。

## 自动创建 Release

只有推送 `v*` 标签才会创建 GitHub Release，例如：

```powershell
git tag v1.0.0
git push origin v1.0.0
```

工作流会等待桌面端、Browser、Android 和 iOS 全部构建完成，然后：

1. 下载所有 Job 生成的 Artifact；
2. 汇总全部 ZIP；
3. 生成 `SHA256SUMS.txt`；
4. 创建 `HelloCrab v1.0.0` Release；
5. 将全部平台产物一起上传。

同名 Release 已存在时，会覆盖其中同名附件，不会重复创建 Release。

## iOS 默认产物

没有配置 Apple 签名信息时，工作流仍会成功构建：

```text
HelloCrab-iOS-Simulator-arm64-<版本>.zip
```

这是未签名的 Apple Silicon iOS Simulator `.app`，只能在 Xcode Simulator 中测试，不能直接安装到实体 iPhone 或 iPad。

HelloCrab 的 iOS 项目使用版本浮动的 `net10.0-ios`。iOS Job 会临时生成 `global.json`，把构建环境固定为：

```text
.NET SDK:       10.0.203
Workload set:   10.0.203.1
Xcode:          26.4.x
iOS Simulator:  26.4
```

该 workload set 是与 Xcode 26.4 配套发布的版本。GitHub `macos-26` Runner 当前提供 Xcode 26.4.1 和 iOS 26.4 Simulator runtime。工作流会优先寻找：

```text
/Applications/Xcode_26.4.1.app
/Applications/Xcode_26.4.app
```

工作流通过 `DEVELOPER_DIR` 只影响当前 Job，不会永久修改 Runner 的全局 `xcode-select` 设置。构建前还会执行 `xcrun simctl list runtimes`，确认 iOS 26.4 Simulator runtime 可用；缺失时会直接输出清晰错误。

## 生成可安装的签名 IPA

在仓库的：

```text
Settings → Secrets and variables → Actions
```

新增以下 Repository secrets：

```text
IOS_CERTIFICATE_BASE64
IOS_CERTIFICATE_PASSWORD
IOS_PROVISION_PROFILE_BASE64
IOS_CODESIGN_KEY
IOS_CODESIGN_PROVISION
```

说明：

- `IOS_CERTIFICATE_BASE64`：Apple Distribution `.p12` 文件的 Base64 内容；
- `IOS_CERTIFICATE_PASSWORD`：导出 `.p12` 时设置的密码；
- `IOS_PROVISION_PROFILE_BASE64`：`.mobileprovision` 文件的 Base64 内容；
- `IOS_CODESIGN_KEY`：证书完整名称，例如 `Apple Distribution: Company (TEAMID)`；
- `IOS_CODESIGN_PROVISION`：Provisioning Profile 的名称。

Windows PowerShell 生成 Base64：

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('distribution.p12')) |
  Set-Content certificate-base64.txt

[Convert]::ToBase64String([IO.File]::ReadAllBytes('profile.mobileprovision')) |
  Set-Content profile-base64.txt
```

五项 Secret 全部存在时，iOS Job 会额外生成：

```text
HelloCrab-iOS-<版本>.zip
```

ZIP 内包含签名后的 IPA。任何一项 Secret 缺失时，会跳过 IPA，但 Simulator 包仍会正常生成。

## 普通 push 与版本发布的区别

普通 push：

```text
push main → 全平台构建 → Actions Artifacts
```

推送版本标签：

```text
push v1.0.0 → 全平台构建 → Actions Artifacts → GitHub Release
```

因此，日常提交不会不断创建 Release。
