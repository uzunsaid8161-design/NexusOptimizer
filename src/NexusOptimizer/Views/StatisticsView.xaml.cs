using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using NexusOptimizer.ViewModels;

namespace NexusOptimizer.Views;

public partial class StatisticsView : UserControl
{
    public StatisticsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not StatisticsViewModel vm) return;
        vm.FpsHistory.CollectionChanged += OnHistoryChanged;
        FpsSparkline.Clear();
        foreach (double value in vm.FpsHistory) FpsSparkline.Push(value);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is StatisticsViewModel vm)
            vm.FpsHistory.CollectionChanged -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
            foreach (double value in e.NewItems)
                FpsSparkline.Push(value);
    }
}
