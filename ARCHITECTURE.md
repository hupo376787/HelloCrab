# Architecture

## 依赖边界

```text
HelloCrab.Core
  ├─ 领域模型、ViewModel、桌面共享 Views
  ├─ 下载与采集流程
  ├─ 抖音/快手/小红书/微博/美篇/Instagram/哔哩哔哩浏览器适配器
  ├─ IBrowserAutomationService
  ├─ IMediaProcessor
  ├─ IPlatformShellService
  └─ RemoteApp 与远程 DTO

HelloCrab.Desktop -> HelloCrab.Core
  ├─ Playwright/PlaywrightBrowserService : IBrowserAutomationService
  ├─ FFmpeg/FfmpegMediaService : IMediaProcessor
  ├─ FFmpeg/GyanFfmpegInstallerService : IFfmpegInstallerService
  ├─ Platform/PlatformShellService : IPlatformShellService
  └─ Remote/RemoteApiHostService

HelloCrab.Android -> HelloCrab.Core.Remote.RemoteApp
HelloCrab.iOS     -> HelloCrab.Core.Remote.RemoteApp
HelloCrab.Browser -> HelloCrab.Core.Remote.RemoteApp
```

## 远程服务器生命周期

```text
应用启动
  └─ 读取 settings.json 的 RemoteApiEnabled
       ├─ true  -> 启动 Kestrel/WebApplication，监听 0.0.0.0:端口
       └─ false -> 不创建监听服务

用户切换开关
  ├─ 开启 -> SetEnabledAsync(true) -> StartAsync
  └─ 关闭 -> SetEnabledAsync(false) -> StopAsync + DisposeAsync
```

生命周期操作由 `SemaphoreSlim` 串行化，避免快速切换时重复绑定同一个端口。

