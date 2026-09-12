using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace HelloCrab.Core.Views
{
    public partial class MainWindow
    {
        private static readonly IDisposable GitHubHomeLinkDataContextHandler =
            StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
                Dispatcher.UIThread.Post(
                    window.InstallGitHubHomeLink,
                    DispatcherPriority.Loaded));

        private int _githubHomeLinkInstallAttempts;

        private void InstallGitHubHomeLink()
        {
            var github = this.GetLogicalDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(GitHubHomeLinkBehavior.IsGitHubHomeText);

            if (github is null)
            {
                if (_githubHomeLinkInstallAttempts++ < 32)
                {
                    Dispatcher.UIThread.Post(
                        InstallGitHubHomeLink,
                        DispatcherPriority.Background);
                }

                return;
            }

            _githubHomeLinkInstallAttempts = 0;
            GitHubHomeLinkBehavior.MakeClickable(github);
        }
    }
}

namespace HelloCrab.Core.Remote.Views
{
    public partial class RemoteMainView
    {
        private static readonly IDisposable RemoteGitHubHomeLinkDataContextHandler =
            StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
                Dispatcher.UIThread.Post(
                    view.InstallRemoteGitHubHomeLink,
                    DispatcherPriority.Loaded));

        private int _remoteGitHubHomeLinkInstallAttempts;

        private void InstallRemoteGitHubHomeLink()
        {
            var github = this.GetLogicalDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(GitHubHomeLinkBehavior.IsGitHubHomeText);

            if (github is null)
            {
                // RemoteMainView.AboutPolish creates the footer at runtime, so allow it to finish
                // before wiring the hyperlink behavior.
                if (_remoteGitHubHomeLinkInstallAttempts++ < 32)
                {
                    Dispatcher.UIThread.Post(
                        InstallRemoteGitHubHomeLink,
                        DispatcherPriority.Background);
                }

                return;
            }

            _remoteGitHubHomeLinkInstallAttempts = 0;
            GitHubHomeLinkBehavior.MakeClickable(github);
        }
    }
}

internal static class GitHubHomeLinkBehavior
{
    internal const string Url = "https://github.com/hupo376787/HelloCrab";
    private const string InstalledClass = "githubHomeHyperlink";

    internal static bool IsGitHubHomeText(TextBlock textBlock)
    {
        return textBlock.Text?.Contains(Url, StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static void MakeClickable(TextBlock textBlock)
    {
        if (textBlock.Classes.Contains(InstalledClass))
            return;

        textBlock.Classes.Add(InstalledClass);
        textBlock.Foreground = new SolidColorBrush(Color.Parse("#FF3B82F6"));
        textBlock.TextDecorations = TextDecorations.Underline;
        textBlock.Cursor = new Cursor(StandardCursorType.Hand);
        textBlock.PointerReleased += GitHubTextBlock_PointerReleased;
    }

    private static async void GitHubTextBlock_PointerReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        if (sender is not Control control)
            return;

        e.Handled = true;

        try
        {
            var topLevel = TopLevel.GetTopLevel(control);
            if (topLevel is not null)
                await topLevel.Launcher.LaunchUriAsync(new Uri(Url));
        }
        catch
        {
            // Opening an external browser is best-effort. Keep the About UI usable even when
            // the platform rejects external URI launching.
        }
    }
}
