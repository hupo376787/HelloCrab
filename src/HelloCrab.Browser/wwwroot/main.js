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

try {
  if (typeof window === 'undefined') {
    throw new Error('Expected to be running in a browser');
  }

  const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

  const config = dotnetRuntime.getConfig();
  await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
} catch (error) {
  showStartupError(error);
}
