using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HelloCrab.Core.Remote.Services;
using HelloCrab.Core.Remote.ViewModels;
using HelloCrab.Core.Remote.Views;

namespace HelloCrab.Core.Remote;

public partial class RemoteApp : Application
{
    private RemoteMainViewModel? _viewModel;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var viewModel = _viewModel ??= new RemoteMainViewModel(new RemoteCrawlerClient());

        if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            // Avalonia 12 Android creates a view for each Activity instance.
            activityLifetime.MainViewFactory = () => new RemoteMainView
            {
                DataContext = viewModel
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Browser and iOS still use the single-view lifetime.
            singleView.MainView = new RemoteMainView
            {
                DataContext = viewModel
            };
        }

        if (ApplicationLifetime is IControlledApplicationLifetime controlled)
        {
            controlled.Exit += async (_, _) =>
            {
                if (_viewModel is not null)
                    await _viewModel.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
