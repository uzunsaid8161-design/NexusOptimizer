using System.Windows.Controls;
using System.Windows.Input;
using NexusOptimizer.ViewModels;

namespace NexusOptimizer.Views;

public partial class DisplayControlView : UserControl
{
    public DisplayControlView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as DisplayControlViewModel)?.EnsureLoaded();
    }

    private async void OnBrightnessSliderReleased(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DisplayControlViewModel viewModel && viewModel.CanAdjustBrightness)
            await viewModel.ApplyBrightnessFromSliderAsync();
    }
}
