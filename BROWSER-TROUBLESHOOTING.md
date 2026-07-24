# Browser 远程控制端排查

## 启动方式

```powershell
dotnet workload install wasm-tools
dotnet clean .\src\HelloCrab.Browser\HelloCrab.Browser.csproj
dotnet restore .\src\HelloCrab.Browser\HelloCrab.Browser.csproj
dotnet run --project .\src\HelloCrab.Browser\HelloCrab.Browser.csproj
```

浏览器项目只提供远程控制界面。Playwright、FFmpeg 和文件下载仍运行在 `HelloCrab.Desktop`。

## 浏览器登录数据目录

桌面采集器现在使用 exe 同目录的：

```text
browser-profile
```

首次启动且该目录为空时，程序会尝试从旧的
`%LOCALAPPDATA%\HelloCrab\browser-profile` 复制登录状态。请勿在 HelloCrab 或 Playwright Chromium 运行期间手动移动、覆盖或删除该目录。
界面中的“清空图片缓存”只处理 `image-cache`，不会删除浏览器登录状态。

## Chromium 安装与查找目录

程序内“安装 Chromium”默认写入 exe 同目录的：

```text
chromium
```

浏览器启动时的查找顺序：

1. `程序目录/chromium/`；
2. Playwright 原来的系统缓存目录，Windows 通常为 `%LOCALAPPDATA%\ms-playwright`；
3. `PLAYWRIGHT_BROWSERS_PATH` 指向的外部目录。

如果程序放在 `Program Files` 等普通用户不可写目录，便携安装会失败。请把程序放到可写目录，或确保该目录具有写权限。

## 一直停在“正在加载”

本版本已进行三项处理：

1. `HelloCrab.Core` 与 Desktop 均统一为 `net10.0`，Browser 直接引用同一 .NET 10 平台系列的共享库。
2. HotAvalonia 只保留在桌面启动项目，不再进入 Browser 依赖图。
3. Browser 按 `WebGL2 → WebGL1 → Software2D` 顺序回退，避免 WebGL 不可用时无法启动。

如仍失败，页面会直接显示异常。也可以按 `F12` 打开开发者工具，在 `Console` 中复制第一条红色错误。

## 中文全部显示为方块

Avalonia Browser 通过 Skia 绘制界面，不能直接使用浏览器或 Windows 中安装的“微软雅黑”等系统字体。Inter 字体也不包含中文字形，因此会显示为方块。

本项目仅在 `HelloCrab.Browser` 中引用：

```xml
<PackageReference Include="Quick.AvaloniaFonts.SourceHanSansCN.Slim" Version="1.0.0" />
```

Browser 启动时直接调用字体包提供的 `.WithFont_SourceHanSansCN()`，将思源黑体设为默认字体。不能只手写字体的 `avares://` URI：在 WASM 裁剪阶段，如果代码没有直接引用字体程序集，字体资源可能不会进入最终发布产物。修改后必须删除 Browser 与 Core 的 `bin/obj` 并重新还原，单纯刷新页面仍可能使用旧的 WASM 静态资源。

## 清理旧缓存

修改包版本或目标框架后，请先关闭浏览器和调试进程，再删除：

- `src\HelloCrab.Browser\bin`
- `src\HelloCrab.Browser\obj`
- `src\HelloCrab.Core\bin`
- `src\HelloCrab.Core\obj`

随后重新执行 `dotnet restore` 和 `dotnet run`。

## `System.DllNotFoundException: libSkiaSharp`

本项目已在 Browser 项目中指定：

```xml
<RuntimeIdentifier>browser-wasm</RuntimeIdentifier>
<WasmBuildNative>true</WasmBuildNative>
```

仍需要本机安装与当前 .NET SDK 对应的 WebAssembly Build Tools：

```powershell
dotnet workload install wasm-tools
dotnet workload update
```

如果从 Visual Studio 启动，还要打开 **Visual Studio Installer → 修改 → 单个组件**，安装与 SDK 对应的 **.NET 10.0 WebAssembly Build Tools**。安装后必须删除 `HelloCrab.Browser` 和 `HelloCrab.Core` 的 `bin/obj`，再重新还原和生成；仅刷新网页不会补回缺失的 `libSkiaSharp`。

## 输入令牌后提示 `net_http_operation_started`

该文本是 .NET `HttpClient` 的资源键，含义是：客户端已经发出过请求，随后又尝试修改同一个 `HttpClient` 的 `BaseAddress` 或默认请求头。常见触发流程是第一次未填写令牌就点击连接，随后补填令牌再次连接。

当前版本不再修改已使用过的 `HttpClient`。地址和令牌保存为连接配置快照，每个请求单独创建绝对 URI 和 `X-SMC-Token` 请求头，因此首次连接失败后可以直接修改令牌或主机地址并重新连接。

连接地址规则：

- Browser 与桌面采集器运行在同一台电脑：`http://127.0.0.1:5088`；
- 手机或其他电脑：必须填写桌面端“远程控制服务器”区域显示的局域网地址，例如 `http://192.168.1.20:5088`；
- `127.0.0.1` 始终表示当前打开网页的设备，在手机上填写它不会连接到电脑；
- 桌面端必须先打开“远程控制服务器”开关，并确认状态显示“运行中”。

## `JsonSerializerIsReflectionDisabled`

Browser/WASM 发布会裁剪运行时反射元数据。远程 API 客户端必须使用
`RemoteJsonContext` 生成的 `JsonTypeInfo<T>` 重载，不能使用无上下文的
`ReadFromJsonAsync<T>()` 或 `JsonContent.Create(value)`。

修复后请删除 Browser/Core 的 `bin`、`obj` 并重新构建，避免继续加载旧 WASM。

## Saved host, token, or theme does not restore

Browser preferences are scoped to the current origin. `http://localhost:...`, `http://127.0.0.1:...`, and a LAN-IP URL are different origins and therefore have separate `localStorage` values. Private browsing or browser policies can also disable local storage. Connect once from the desired origin to save the host and token there.
