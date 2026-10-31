using System.Windows;
using System.Windows.Controls;
using NexusOptimizer.ViewModels;

namespace NexusOptimizer.Views;

public partial class ProcessManagerView : UserControl
{
    public ProcessManagerView() => InitializeComponent();
    private void OnLoaded(object sender, RoutedEventArgs e) => (DataContext as ProcessManagerViewModel)?.StartMonitoring();
    private void OnUnloaded(object sender, RoutedEventArgs e) => (DataContext as ProcessManagerViewModel)?.StopMonitoring();
}
