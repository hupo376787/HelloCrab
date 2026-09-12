using System.Text;
using Avalonia.Threading;
using HelloCrab.Core.Contracts;
using HelloCrab.Core.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HelloCrab.Desktop.Remote;

/// <summary>
/// Registers the plain-text remote batch-download endpoint.
/// Authentication and CORS are provided by RemoteApiHostService's existing middleware.
/// </summary>
internal static class RemoteBatchDownloadEndpoint
{
    public static void Map(WebApplication app, MainWindowViewModel viewModel)
    {
        app.MapPost("/api/batch-download", async (HttpContext context) =>
        {
            using var reader = new StreamReader(
                context.Request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync();

            return await InvokeOnUiAsync(() =>
                viewModel.QueueRemoteManualBatchCapture(content));
        });
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
}
