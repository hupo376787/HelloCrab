import { dotnet } from './_framework/dotnet.js'

function showStartupError(error) {
  console.error('HelloCrab Browser startup failed:', error);

  const splash = document.querySelector('.avalonia-splash');
  if (!splash) {
    return;
  }

  const details = error?.stack ?? error?.message ?? String(error);
  splash.classList.add('error');
  splash.textContent = `远程控制端启动失败\n\n${details}\n\n请按 F12 打开 Console，并复制第一条红色错误。`;
}

function installNativePasteFallback(exports) {
  const pasteText =
    exports?.HelloCrab?.Browser?.BrowserPasteInterop?.PasteText ??
    exports?.BrowserPasteInterop?.PasteText;

  if (typeof pasteText !== 'function') {
    console.warn('HelloCrab Browser paste fallback is unavailable.');
    return;
  }

  // Avalonia TextBox paste normally goes through navigator.clipboard. Browser clipboard reads can
  // be blocked when the page is served from a plain-HTTP LAN/Tailscale address. A real Ctrl+V still
  // emits a DOM paste event whose clipboardData is available, so forward that text into the focused
  // Avalonia TextBox before Avalonia's default clipboard handler runs.
  document.addEventListener('paste', event => {
    const text = event.clipboardData?.getData('text/plain');
    if (typeof text !== 'string') {
      return;
    }

    try {
      if (pasteText(text) === true) {
        event.preventDefault();
        event.stopImmediatePropagation();
      }
    } catch (error) {
      console.error('HelloCrab Browser paste fallback failed:', error);
    }
  }, true);
}

try {
  if (typeof window === 'undefined') {
    throw new Error('Expected to be running in a browser');
  }

  const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

  const config = dotnetRuntime.getConfig();
  const exports = await dotnetRuntime.getAssemblyExports(config.mainAssemblyName);
  installNativePasteFallback(exports);

  await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
} catch (error) {
  showStartupError(error);
}
