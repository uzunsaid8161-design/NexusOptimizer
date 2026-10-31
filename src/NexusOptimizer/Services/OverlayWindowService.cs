using System.Windows;
using NexusOptimizer.ViewModels;
using NexusOptimizer.Views;

namespace NexusOptimizer.Services;

public sealed class OverlayWindowService
{
    private OverlayWindow? _window;

    public void Show(OverlayViewModel viewModel, Action closed)
    {
        if (_window is { IsVisible: true }) return;

        _window = new OverlayWindow { DataContext = viewModel };
        if (Application.Current.MainWindow is { } owner) _window.Owner = owner;
        _window.Closed += (_, _) =>
        {
            _window = null;
            closed();
        };
        _window.Show();
    }

    public void Hide() => _window?.Close();
}
