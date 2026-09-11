using System.Runtime.InteropServices.JavaScript;
using HelloCrab.Core.Remote.Views;

namespace HelloCrab.Browser;

/// <summary>
/// Receives the browser's native ClipboardEvent text. This is used as a Ctrl+V fallback when the
/// page is served over plain HTTP and navigator.clipboard is unavailable because the page isn't a
/// secure context.
/// </summary>
internal static class BrowserPasteInterop
{
    [JSExport]
    internal static bool PasteText(string text)
        => BrowserTextInputPasteBridge.TryPaste(text);
}
