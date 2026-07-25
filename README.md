# HelloCrab

基于 Avalonia、Playwright、AI与 FFmpeg 的跨平台桌面采集器，以及 Android、iOS、Browser 远程控制端。  
**Made By ChatGPT & Vincent with ❤**

> 截至 2026-07-20，HelloCrab 的共同开发已分布在至少三个连续的长对话中，累计经历至少 80 轮需求确认、问题排查、运行反馈与源码迭代。实际协作内容还包括早期的跨平台架构、远程控制、站点解析和下载流程讨论，因此真实轮次可能更多。

## 共同开发历程

HelloCrab 不是一次性生成的示例项目，而是在 Vincent 持续提出真实使用问题、验证运行结果并反馈细节后，由 ChatGPT 与 Vincent 共同逐步打磨出来的。主要迭代包括：

- 从桌面采集器扩展为 Avalonia 跨平台架构，并增加 Android、iOS、Browser 远程控制端；
- 持续完善抖音、TikTok、Pinterest、快手、小红书、微博、美篇、Instagram、哔哩哔哩等站点的作者主页、分页接口、详情数据和媒体解析；
- 修复微博滚动、分页延时、页面切换、无头模式、登录状态、历史记录和下载统计等实际运行问题；
- 增加按日文件日志、下载历史排序、作者存储统计、完成通知和新下载/更新作品的差异化提示；
- 使用 ffprobe 检测视频音轨，并通过 FFmpeg 为无声视频合并背景音乐，同时支持 Windows 后台自动下载安装 FFmpeg；
- 使用 YoloDotNet 与 YOLO11 ONNX 模型实现可选的人像检测，支持 10%～95% 置信度滑块（默认 60%），并通过后台检测队列降低对图片下载流程的阻塞；
- 优化 Android 端滚动流畅度、移动端布局、远程状态同步、主题切换和高负载功能的风险说明；
- 为采集任务、后台检测、临时文件、异常退出恢复和跨作者切换补充更清晰的状态边界与安全保护。

每一项功能都来自真实需求、日志、截图、异常信息和反复验证。这个项目也记录了人和 AI 通过持续对话共同完成软件设计、实现、排查与改进的过程。

## 项目结构

```text
src/
├─ HelloCrab.Core/
│  ├─ Models/                         数据模型
│  ├─ ViewModels/                     桌面主界面 ViewModel
│  ├─ Views/                          桌面共享界面
│  ├─ Services/
│  │  ├─ Browser/                     浏览器自动化公共接口
│  │  ├─ Crawling/                    采集编排
│  │  ├─ Downloading/                 下载、索引、无声音频修复流程
│  │  ├─ History/                     下载历史
│  │  ├─ Images/                      图片缓存
│  │  ├─ Media/                       FFmpeg/ffprobe 公共接口
│  │  ├─ Platform/                    平台外壳公共接口
│  │  ├─ Settings/                    settings.json
│  │  └─ Localization/                JSON 语言包扫描、回退与动态切换
│  ├─ Languages/                      简体中文、英文、日文语言包
│  ├─ Sites/
│  │  ├─ Bilibili/                    B站作者分页、DASH最高画质与音视频合并
│  │  ├─ Douyin/                      抖音解析器与滚动策略
│  │  ├─ Instagram/                   Instagram GraphQL、视频、单图与轮播解析器
│  │  ├─ TikTok/                      TikTok item_list、分页与最高分辨率解析器
│  │  ├─ Pinterest/                   Pinterest Pin、画板分页、最高质量图片与视频解析器
│  │  ├─ Kuaishou/                    快手主站与 Live 站解析器
│  │  ├─ Xiaohongshu/                 小红书首屏、分页与详情解析器
│  │  ├─ Meipian/                      美篇专栏、分页与 ARTICLE_DETAIL 解析器
│  │  └─ ISiteAdapter.cs              平台适配接口
│  ├─ Contracts/                      远程 API DTO
│  └─ Remote/                         Android/iOS/Browser 共用远程界面
│
├─ HelloCrab.Desktop/
│  ├─ Windows/                         Windows 文件管理器适配
│  ├─ Linux/                           Linux 文件管理器适配
│  ├─ macOS/                           macOS Finder 适配
│  ├─ Playwright/                      浏览器自动化实现
│  ├─ Chromium/                        Chromium 安装实现
│  ├─ FFmpeg/                          FFmpeg/ffprobe 实现
│  ├─ Platform/                        桌面平台选择器
│  ├─ Remote/                          可启停的 HTTP 远程服务器
│  ├─ Assets/                          各桌面系统图标
│  └─ Program.cs / App.axaml           桌面入口
│
├─ HelloCrab.Android/         只运行远程控制界面
├─ HelloCrab.iOS/             只运行远程控制界面
└─ HelloCrab.Browser/         只运行远程控制界面
```

依赖方向：

```text
Desktop ────────> Core
Android ────────> Core(Remote)
iOS ───────────> Core(Remote)
Browser ───────> Core(Remote)

Core 不引用 Microsoft.Playwright、FFmpeg 可执行程序或桌面系统 API。
```

## 程序目录中的便携数据

桌面端运行后，会把需要随程序一起移动的数据直接保存在 exe 根目录：

```text
HelloCrab/
├─ HelloCrab.exe
├─ browser-profile/       Playwright Chromium 登录状态、Cookie 和站点数据
├─ image-cache/           作者头像、作品封面等界面图片缓存
├─ Download/              默认下载目录
├─ History.json           下载历史
├─ settings.json          程序设置
├─ Languages/             可复制和编辑的 JSON 界面语言包
├─ Logs/                  按日日志
├─ Models/                YOLO ONNX 模型
└─ ffmpeg/bin/            自动安装的 ffmpeg.exe 与 ffprobe.exe
```

首次使用此版本且程序目录中还没有 `browser-profile` 时，会尝试从旧目录
`%LOCALAPPDATA%\HelloCrab\browser-profile` 复制已有登录状态。确认新目录登录正常后，旧目录可自行备份或删除。
`image-cache` 属于可重新生成的数据，可在桌面端“下载设置”中点击“清空图片缓存”。
作者目录中的 `crawler-index.json` 现在使用稳定的无版本完成键；旧版带版本前缀的记录在首次读取时会自动迁移并从文件中移除。

## 桌面端与远程端的职责

### Desktop：完整采集主机

Windows、Linux、macOS 共用 `HelloCrab.Desktop`：

- 抖音、TikTok、Pinterest、快手、小红书、微博、美篇、Instagram、哔哩哔哩使用 Playwright Chromium 登录与页面采集；
- 微博支持普通视频和图文混排视频，优先从 `media_info.playback_list` 选择 4K/2K/1080p 等最高分辨率 MP4；
- 捕获作品接口；
- 自动滚动作者主页；
- 下载视频、图集、封面、音乐；
- Instagram 捕获 `/graphql/query` 作者时间线，支持单图、Reels 和图片/视频混合轮播；无文案作品仅按发布时间命名；
- TikTok 捕获 `/api/post/item_list/` 作者作品接口，使用 `hasMore/cursor` 配合页面滚动连续加载，并从 `video.bitrateInfo[].PlayAddr` 选择像素尺寸最大的档位；
- Pinterest 从作者/画板接口收集 Pin ID，使用 `bookmark` 连续滚动，再读取每个 `/pin/{id}/` 文档中的 `__PWS_INITIAL_PROPS__`，从 `blocks.video.video_list`、故事页、轮播和图片节点选择最高质量媒体；
- Pinterest HLS 由程序自身下载 m3u8、密钥和视频分片，再让 FFmpeg 仅合并本地文件，避免 FFmpeg 未继承浏览器/系统代理时出现 CDN 连接失败；系统 HTTP 请求失败时还会切换到 Playwright 浏览器上下文请求通道；
- 哔哩哔哩捕获 `/x/space/wbi/arc/search` 作者分页，逐条解析视频页 `window.__playinfo__.data.dash`，选择最高分辨率/帧率视频与最高码率音频并使用 FFmpeg 合并；
- 可选开启“检测视频是否有声音”：使用 `ffprobe` 检查音轨；
- 检测到无音轨视频且作品提供背景音乐时，下载临时音频并由 `ffmpeg` 合并；
- Windows 可在设置区点击“下载 FFmpeg”，后台访问 gyan.dev 构建页并自动安装到程序目录；
- 保存下载历史、作者索引和设置；
- 浏览器登录数据保存在 exe 同目录的 `browser-profile`，头像和封面缓存保存在 `image-cache`；
- 下载设置区提供“清空图片缓存”按钮，只清理 `image-cache`，不影响作品、历史、设置或登录状态；
- 正常结束、手动停止或异常中止后，重新统计本次作者已落盘作品数和目录总大小；
- 可选开放远程 HTTP 服务器。


### Android、iOS、Browser：远程控制器

这三个项目不引用 Playwright，也不在手机或 WebAssembly 内下载作品。它们通过 HTTP 调用正在运行的桌面主机，用于：

- 查看状态、进度、日志和历史；
- 修改桌面主机下载设置；
- 安装 Chromium、打开浏览器；
- 开始或停止采集。


## JSON 多语言

桌面主界面左侧设置区提供独立的“语言”卡片。程序启动时自动扫描运行目录下的 `Languages` 文件夹，并内置以下语言包：

```text
zh-CN.json   简体中文
en-US.json   English
ja-JP.json   日本語
```

新增语言不需要修改或重新编译程序：复制任意一个 JSON 文件，修改顶层 `code`、`displayName`、`sortOrder` 和 `strings`，保存到 `Languages` 文件夹，再点击界面的“重新扫描语言包”。自定义语言包缺少的键会自动回退到简体中文；JSON 语法错误或格式占位符错误不会阻止程序启动。

语言包结构示例：

```json
{
  "code": "fr-FR",
  "displayName": "Français",
  "sortOrder": 30,
  "strings": {
    "Browser.Open": "Ouvrir le navigateur"
  }
}
```

Windows、Linux、macOS 桌面版共用同一套语言加载逻辑。程序目录不可写时，语言目录自动回退到当前用户的应用数据目录，实际路径会显示在语言设置卡片中。

## “开放远程控制服务器”开关

桌面端设置区新增开关：

```text
开放远程控制服务器
```

行为如下：

- 开启：立即在 `0.0.0.0:<端口>` 启动 HTTP 服务；
- 关闭：立即执行 `StopAsync`，释放监听端口；
- 关闭后，Android、iOS、Browser 连健康检查都无法请求；
- 状态、端口和访问令牌显示在桌面界面；
- 开关状态写入 `settings.json`，下次启动自动恢复（Windows 默认程序目录；macOS/Linux 在用户应用数据目录）；
- 除 `/api/health` 外，请求需要 `X-SMC-Token`。

相关设置：

```json
{
  "remoteApiEnabled": false,
  "remoteApiPort": 5088,
  "remoteApiToken": "自动生成的随机令牌"
}
```

新安装默认关闭远程服务器，默认端口为 `5088`。局域网客户端填写桌面端显示的地址和令牌。关闭开关后，即使客户端保存了地址和令牌，也无法连接，因为服务器进程已经停止监听。

## 打开解决方案

只开发和编译桌面采集器：

```text
HelloCrab.Desktop.slnx
```

各平台过滤解决方案：

```text
HelloCrab.Desktop.slnx
HelloCrab.Browser.slnx
HelloCrab.Android.slnx
HelloCrab.iOS.slnx
```

完整解决方案包含 Android 和 iOS；没有对应 workload 的电脑不应直接生成完整解决方案。

## HotAvalonia 热重载

桌面启动项目和包含 AXAML 控件的 Core 项目都已引用：

```xml
<PackageReference Include="Avalonia.Markup.Xaml.Loader" Version="12.1.0" PrivateAssets="All" Publish="True" />
<PackageReference Include="HotAvalonia" Version="3.1.3" PrivateAssets="All" Publish="True" />
```

正常以 Debug 方式启动桌面项目后，保存 AXAML 文件即可触发界面热重载。首次打开新项目需要先完成 NuGet 还原。

## 运行桌面端

```bash
dotnet run --project src/HelloCrab.Desktop/HelloCrab.Desktop.csproj
```

首次运行后，可以点击界面中的“安装 Chromium”。程序默认安装到：

```text
程序目录/chromium/
```

启动浏览器时会优先查找该便携目录；若不存在或安装不完整，再兼容查找 Playwright 原来的默认缓存目录。Windows 通常为：

```text
%LOCALAPPDATA%\ms-playwright
# 通常展开为 C:\Users\<用户名>\AppData\Local\ms-playwright
```

因此旧安装不需要删除，新安装也可以随 HelloCrab 文件夹一起移动。

“检测视频是否有声音”默认关闭。Windows 桌面端可以点击设置区的“下载 FFmpeg”，程序会后台访问
`https://www.gyan.dev/ffmpeg/builds/`，下载 latest release essentials ZIP，并安装到
`程序目录/ffmpeg/bin/`。关闭音轨检测时不会调用 ffmpeg 或 ffprobe。

FFmpeg 工具查找顺序：

```text
程序目录/ffmpeg(.exe)、ffprobe(.exe)
程序目录/ffmpeg/
程序目录/ffmpeg/bin/
程序目录/tools/ffmpeg/
程序目录/tools/ffmpeg/bin/
系统 PATH
```

## 各平台一键打包发布

项目已提供统一发布入口，会自动清理旧目录、执行 `dotnet publish`、收集平台产物并压缩到 `artifacts/`：

```powershell
./scripts/publish-platform.ps1 -Target win-x64 -Version 1.0.0
./scripts/publish-platform.ps1 -Target browser -Version 1.0.0
./scripts/publish-platform.ps1 -Target android -Version 1.0.0
```

```bash
./scripts/publish-platform.sh linux-x64 Release 1.0.0
./scripts/publish-platform.sh osx-arm64 Release 1.0.0
./scripts/publish-platform.sh ios Release 1.0.0
```

一次构建全部可用目标：

```powershell
./scripts/publish-all-platforms.ps1 -Version 1.0.0
```

```bash
./scripts/publish-all-platforms.sh Release 1.0.0
```

Windows 可直接双击 `one-click-publish/*.bat`，macOS 可双击 `.command`。Android 会同时收集 APK 与 AAB；iOS 需要 macOS/Xcode 或配对 Mac，并需要有效签名。完整说明见 [PUBLISH-ALL-PLATFORMS.md](PUBLISH-ALL-PLATFORMS.md)。

## 发布桌面端

PowerShell：

```powershell
./scripts/publish-desktop.ps1 -Runtime win-x64
./scripts/publish-desktop.ps1 -Runtime linux-x64
./scripts/publish-desktop.ps1 -Runtime osx-arm64
```

Bash：

```bash
./scripts/publish-desktop.sh linux-x64 Release
./scripts/publish-desktop.sh osx-arm64 Release
```

一次发布六个 RID：

```powershell
./scripts/publish-all-desktop.ps1
```

```bash
./scripts/publish-all-desktop.sh
```

支持：

```text
win-x64, win-arm64
linux-x64, linux-arm64
osx-x64, osx-arm64
```

发布结果：

```text
Windows: publish/desktop/win-x64/HelloCrab.exe
Linux:   publish/desktop/linux-x64/HelloCrab
macOS:   publish/desktop/osx-arm64/HelloCrab.app
```

macOS 正式分发仍需在 macOS 上完成签名与公证。Linux 目标机器需要安装 Chromium/Avalonia 所需系统库。

## Browser 远程端

HelloCrab 全部项目统一使用 .NET 10 平台系列：Desktop 与 Core 为 `net10.0`，Browser 为 `net10.0-browser`，Android 为 `net10.0-android36.0`，iOS 为 `net10.0-ios`。编译任意项目都需要安装 .NET 10 SDK；Browser、Android 和 iOS 还需要安装对应 workload。

Browser 端直接使用思源黑体 Slim 作为默认字体，确保字体程序集和中文字符资源真正进入 WASM 发布产物。首次还原会从 NuGet 下载字体包。

```bash
dotnet --list-sdks
dotnet workload install wasm-tools
dotnet restore src/HelloCrab.Browser/HelloCrab.Browser.csproj
dotnet run --project src/HelloCrab.Browser/HelloCrab.Browser.csproj
```

发布：

```bash
./scripts/publish-browser.sh
```

浏览器页面连接局域网 HTTP 主机时，浏览器的 Private Network Access、安全上下文或混合内容策略可能要求通过 HTTPS 部署远程页面。桌面服务器关闭时，Browser 端会显示连接失败。

## Android

Android 项目目标为 `net10.0-android36.0`，对应 Avalonia.Android 12.1.0。

```bash
dotnet workload install android
dotnet workload restore src/HelloCrab.Android/HelloCrab.Android.csproj
dotnet build src/HelloCrab.Android/HelloCrab.Android.csproj
```

## iOS

iOS 项目目标为版本浮动的 `net10.0-ios`，对应 Avalonia.iOS 12.1.0。CI 会固定 .NET SDK、iOS workload set 和 Xcode 组合，避免 Runner 更新后出现 SDK 与模拟器运行时不匹配。

```bash
dotnet workload install ios
dotnet workload restore src/HelloCrab.iOS/HelloCrab.iOS.csproj
dotnet build src/HelloCrab.iOS/HelloCrab.iOS.csproj
```

iOS 的编译、签名和真机安装需要 macOS 与 Xcode。

GitHub Actions 的 iOS 构建固定使用 .NET SDK `10.0.203`、workload set `10.0.203.1` 与 Xcode 26.4.x。该组合对应 Runner 中已安装的 iOS 26.4 Simulator runtime。发布脚本通过当前进程的 `DEVELOPER_DIR` 使用 Xcode，不会永久修改 macOS 的全局 Xcode 选择。Xcode 安装在自定义位置时，可先设置 `HELLOCRAB_XCODE_PATH`。

### 下载历史

- 历史文件固定为可执行文件同目录下的 `History.json`。
- 首次运行会兼容迁移旧的 `download-history.json`。
- 最近发生下载的作者自动移动到列表第一项；仍可手动拖动排序。

## 远程端重新连接

远程客户端允许在首次连接失败后直接修改主机地址或访问令牌并再次连接，不再修改已发出请求的 `HttpClient.BaseAddress`，因此不会出现 `net_http_operation_started`。同机浏览器可使用 `http://127.0.0.1:5088`；手机或其他电脑必须使用桌面端显示的局域网 IP 地址。

## 本次远程端与手机端调整

- Android 与 iOS 共用 `HelloCrab.Core.Remote` 的远程客户端和界面；桌面 API 监听 `0.0.0.0:<端口>`。
- Android Manifest 已声明 `INTERNET`，并允许访问局域网 HTTP 主机；iOS 已声明本地网络用途并允许本地 HTTP。
- 手机原生端会拒绝 `127.0.0.1`、`::1` 和 `localhost`，必须填写桌面端“远程控制服务器”状态中显示的局域网地址。
- 网页端和 Android/iOS 顶部提供独立的亮色/暗色切换，不会修改桌面采集主机的主题设置。
- 桌面端远程访问令牌右侧提供“复制”按钮。
- settings v3 将“文件名中添加作品 ID”迁移为默认关闭；旧版 settings.json 首次加载时也会执行一次迁移。

手机仍需与桌面主机网络互通，并在 Windows 防火墙弹窗中允许 HelloCrab 访问专用网络。手机访问地址示例：

```text
http://192.168.1.20:5088
```

### 自定义远程访问令牌

桌面端“远程控制”区域的访问令牌可直接修改。输入 4–64 位字母、数字或 `. _ @ -`，点击“保存”后立即生效并写入 `settings.json`。网页和手机端需要使用新令牌重新连接。



## Remote UI updates

- 桌面端自定义访问令牌使用紧凑保存图标按钮，不再显示复制按钮。
- Browser、Android、iOS 连接成功后自动读取桌面主机设置；轮询不会覆盖尚未保存的远程编辑。
- 远程端使用亮/暗渐变背景，并为连接、启动、停止和保存操作使用更醒目的渐变按钮。

## Remote controller local preferences

The Browser, Android, and iOS remote controllers remember their own connection settings independently from the desktop crawler settings:

- remote host address;
- remote access token;
- light/dark controller theme.

The Browser stores these values in `localStorage` for the current site origin. Android and iOS store them in the application's private local-data directory. The values are restored before the remote view is shown on the next launch. The Android/iOS host action buttons and counters use a two-column phone layout.


## 定时自动下载

桌面端“采集策略”区域后提供“定时自动下载”设置。界面使用 `ScheduleEditor 1.0.0`，内部通过 FluentScheduler 在当前应用进程中执行计划：

```powershell
Install-Package ScheduleEditor -Version 1.0.0
```

- 支持每 N 分钟、每 N 小时、每天、每周、每月和 Cron。
- 定时任务固定使用无头浏览器在后台运行，不受主界面手动采集的“无头模式”开关影响；正常情况下不会弹出浏览器窗口。
- 登录状态失效时会临时显示浏览器窗口供用户重新登录，登录完成后恢复无头模式。
- 到点后按照 `History.json` 中的显示顺序，逐个打开作者主页并调用现有重新采集流程。
- 配置保存到 settings.json 同目录的 `scheduled-download.json`。
- 软件重新启动后会自动读取配置、恢复调度，并在日志中显示下次执行时间。
- 定时任务只在 HelloCrab 运行期间执行；软件关闭、电脑关机或休眠期间不会执行。
- 定时批处理运行时，原“停止采集”按钮会停止当前作者，并取消后续历史任务。
- HelloCrab 注入自己的 `IFluentScheduleService` 运行时；每天、每周和每月的
  固定时间触发统一使用 FluentScheduler 6 的 `Everyday().At(...)`，避免
  `Every(1).Days().At(...)` 引发 `Use Everyday instead.` 异常。

- 安装 Chromium 时界面实时显示 Playwright 当前组件的下载百分比。确认使用程序目录中的便携 Chromium 后，可在关闭程序后删除旧的 `ms-playwright` 缓存。

- 下载历史支持按作者名字、作者 ID、平台名称或平台 ID 实时搜索，清空搜索词后恢复全部记录。

- FFmpeg 下载显示百分比、已下载/总大小和速度；PushPlus 标题使用 `HelloCrab(昵称)下载完成…` 格式。

- 支持设置作品媒体总下载速度上限（MB/s），0 表示不限速。


## GitHub Actions 全平台发布

`.github/workflows/build-all-platforms.yml` 会构建桌面端、Browser、Android 和 iOS。推送 `v*` 标签后，会把所有平台产物与 SHA256 校验文件一起发布到 GitHub Release。iOS 默认生成无需签名的 Simulator 包，配置 Apple 签名 Secrets 后还会生成 IPA。详见 `GITHUB-ACTIONS-RELEASE.md`。

## 免责声明

本项目仅供学习、研究与技术交流使用，不得用于任何违法违规用途。使用者应自行遵守所在地法律法规、目标平台的服务条款，以及与著作权、隐私权和数据使用相关的规定。

使用者因下载、安装、运行、修改、分发或以其他方式使用本项目而产生的任何直接或间接后果，均由使用者自行判断并承担责任。在适用法律允许的最大范围内，作者及项目贡献者不对由此造成的任何损失、纠纷、账号限制、数据损坏或其他后果承担责任。使用本项目即表示使用者已阅读、理解并接受本免责声明。
