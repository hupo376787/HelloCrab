using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HelloCrab.Core.Contracts;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Desktop.Remote;

/// <summary>
/// 只在桌面主机开启。Android、iOS 与 Browser 项目通过此 HTTP API
/// 远程查看和控制桌面端的 Playwright 采集任务。
/// </summary>
public sealed class RemoteApiHostService : IAsyncDisposable
{
    private const string TokenHeader = "X-SMC-Token";

    private readonly MainWindowViewModel _viewModel;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WebApplication? _application;

    public RemoteApiHostService(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    public bool IsRunning => _application is not null;

    public async Task SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
                await StartCoreAsync(cancellationToken);
            else
                await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_application is not null)
        {
            _viewModel.SetRemoteApiLocalizedStatus("Remote.Status.RunningPort", _viewModel.RemoteApiPort);
            return;
        }

        WebApplication? app = null;
        try
        {
            _viewModel.SetRemoteApiLocalizedStatus("Remote.Status.Starting");

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://0.0.0.0:{_viewModel.RemoteApiPort}");
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy => policy
                    .AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod());
            });

            app = builder.Build();

            // Private Network Access 头必须在 CORS 中间件之前写入，因为 CORS
            // 可能直接完成 OPTIONS 预检而不再调用后续中间件。
            app.Use(async (context, next) =>
            {
                if (string.Equals(
                        context.Request.Headers["Access-Control-Request-Private-Network"].FirstOrDefault(),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
                }

                await next();
            });

            app.UseCors();
            app.Use(async (context, next) =>
            {
                if (HttpMethods.IsOptions(context.Request.Method)
                    || context.Request.Path == "/api/health")
                {
                    await next();
                    return;
                }

                var suppliedToken = context.Request.Headers[TokenHeader].FirstOrDefault();
                if (!CryptographicEquals(suppliedToken, _viewModel.RemoteApiToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(
                        RemoteCommandResult.Fail(_viewModel.Localize("Remote.Api.TokenInvalid")),
                        context.RequestAborted);
                    return;
                }

                await next();
            });

            app.MapGet("/api/health", () => new RemoteHealthDto());
            app.MapGet("/api/snapshot", () => InvokeOnUiAsync(_viewModel.CreateRemoteSnapshot));
            app.MapGet("/api/current-cover", async (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
                var image = await InvokeOnUiAsync(_viewModel.CreateRemoteCoverPng);
                return image is { Length: > 0 }
                    ? Results.File(image, "image/png")
                    : Results.NotFound();
            });
            app.MapGet("/api/history/{historyId:int}/avatar", async (int historyId, HttpContext context) =>
            {
                // 头像通过桌面主机代理，Browser/WASM 不直接访问第三方 CDN，
                // 从而避免 CORS、Referer 与临时 URL 失效造成的空头像。
                context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";

                // 先使用桌面列表已经加载的头像；若桌面头像仍在异步加载，
                // 不能直接返回 404，应继续按 History.json 的 HeadUrl 获取。
                var image = await InvokeOnUiAsync(
                    () => _viewModel.CreateRemoteHistoryAvatarPng(historyId));
                if (image is not { Length: > 0 })
                {
                    var headUrl = await InvokeOnUiAsync(
                        () => _viewModel.GetRemoteHistoryAvatarUrl(historyId));
                    image = await _viewModel.DownloadRemoteHistoryAvatarPngAsync(
                        headUrl,
                        context.RequestAborted);
                }

                return image is { Length: > 0 }
                    ? Results.File(image, "image/png")
                    : Results.NotFound();
            });
            app.MapPut("/api/settings", async (RemoteSettingsDto settings) =>
            {
                await InvokeOnUiAsync(() =>
                    _viewModel.ApplyRemoteSettingsAsync(settings));
                return RemoteCommandResult.Ok(_viewModel.Localize("Remote.Api.SettingsSaved"));
            });
            app.MapPost("/api/actions/{action}", (string action) => ExecuteActionAsync(action));

            await app.StartAsync(cancellationToken);
            _application = app;

            var addresses = GetLanAddresses()
                .Select(address => $"http://{address}:{_viewModel.RemoteApiPort}")
                .ToArray();
            var addressText = addresses.Length == 0
                ? $"http://127.0.0.1:{_viewModel.RemoteApiPort}"
                : string.Join("、", addresses);

            _viewModel.SetRemoteApiLocalizedStatus("Remote.Status.RunningAt", addressText);
            _viewModel.AddRemoteLocalizedLog("Remote.Log.Started", addressText);
            _viewModel.AddRemoteLocalizedLog("Remote.Log.Token", _viewModel.RemoteApiToken);
        }
        catch (Exception ex)
        {
            if (app is not null)
                await app.DisposeAsync();

            _viewModel.SetRemoteApiLocalizedStatus("Remote.Status.StartFailed", ex.Message);
            _viewModel.AddRemoteLocalizedLog("Remote.Log.StartFailed", ex.Message);
        }
    }

    private async Task StopCoreAsync()
    {
        var application = Interlocked.Exchange(ref _application, null);
        if (application is null)
        {
            _viewModel.SetRemoteApiLocalizedStatus("Remote.Status.Stopped");
            return;
        }

        _viewModel.SetRemoteApiLocalizedStatus("Remote.Status.Stopping");
        try
        {
            await application.StopAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await application.DisposeAsync();
        }

        _viewModel.SetRemoteApiLocalizedStatus("Remote.Status.Stopped");
        _viewModel.AddRemoteLocalizedLog("Remote.Log.Stopped");
    }

    private async Task<RemoteCommandResult> ExecuteActionAsync(string action)
    {
        try
        {
            switch (action.Trim().ToLowerInvariant())
            {
                case "install-chromium":
                    return await StartAsyncCommandAsync(
                        _viewModel.InstallChromiumCommand,
                        _viewModel.Localize("Remote.Api.InstallChromiumAccepted"));

                case "open-browser":
                    return await StartAsyncCommandAsync(
                        _viewModel.OpenBrowserCommand,
                        _viewModel.Localize("Remote.Api.OpenBrowserAccepted"));

                case "start":
                    return await StartAsyncCommandAsync(
                        _viewModel.StartCaptureCommand,
                        _viewModel.Localize("Remote.Api.StartCaptureAccepted"));

                case "stop":
                    await InvokeOnUiAsync(() =>
                    {
                        if (!_viewModel.StopCaptureCommand.CanExecute(null))
                            throw new InvalidOperationException(_viewModel.Localize("Remote.Api.NoCaptureRunning"));

                        _viewModel.StopCaptureCommand.Execute(null);
                    });
                    return RemoteCommandResult.Ok(_viewModel.Localize("Remote.Api.StopAccepted"));

                case "open-download-folder":
                    await InvokeOnUiAsync(() =>
                    {
                        if (!_viewModel.OpenDownloadFolderCommand.CanExecute(null))
                            throw new InvalidOperationException(_viewModel.Localize("Remote.Api.CannotOpenFolder"));

                        _viewModel.OpenDownloadFolderCommand.Execute(null);
                    });
                    return RemoteCommandResult.Ok(_viewModel.Localize("Remote.Api.FolderOpened"));

                default:
                    return RemoteCommandResult.Fail(_viewModel.Localize("Remote.Api.UnknownAction", action));
            }
        }
        catch (Exception ex)
        {
            return RemoteCommandResult.Fail(ex.Message);
        }
    }

    private async Task<RemoteCommandResult> StartAsyncCommandAsync(
        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command,
        string acceptedMessage)
    {
        await InvokeOnUiAsync(() =>
        {
            if (!command.CanExecute(null))
                throw new InvalidOperationException(_viewModel.Localize("Remote.Api.ActionUnavailable"));

            _ = command.ExecuteAsync(null);
        });

        return RemoteCommandResult.Ok(acceptedMessage);
    }

    private static Task InvokeOnUiAsync(Action action)
        => InvokeOnUiAsync(() =>
        {
            action();
            return true;
        });

    private static Task InvokeOnUiAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    private static Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return Task.FromResult(action());

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    private static bool CryptographicEquals(string? left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            return false;

        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
               && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static IEnumerable<string> GetLanAddresses()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork
                    || IPAddress.IsLoopback(address.Address))
                {
                    continue;
                }

                yield return address.Address.ToString();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }
}
