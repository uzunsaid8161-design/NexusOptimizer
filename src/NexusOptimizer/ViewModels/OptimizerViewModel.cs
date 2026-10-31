using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusOptimizer.Models;
using NexusOptimizer.Services;

namespace NexusOptimizer.ViewModels;

public partial class OptimizerViewModel : ObservableObject
{
    private readonly OptimizerService _service;

    public ObservableCollection<OptimizationItem> Items { get; }

    [ObservableProperty] private string _modeText = string.Empty;
    [ObservableProperty] private string _summary = "Bereit.";

    public OptimizerViewModel(OptimizerService service)
    {
        _service = service;
        Items = new ObservableCollection<OptimizationItem>(service.Items);
        ModeText = service.IsAdmin
            ? "Administrator – alle Tweaks verfügbar"
            : "Standardmodus – System-Tweaks benötigen Adminrechte";

        foreach (var item in Items)
        {
            item.IsAvailable = !item.NeedsAdmin || service.IsAdmin;
            item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private async void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OptimizationItem.IsEnabled)) return;
        if (sender is not OptimizationItem item) return;

        item.IsBusy = true;
        item.Status = "Wird angewendet …";
        string result = await Task.Run(() => _service.Apply(item, item.IsEnabled));
        item.Status = result;
        item.IsBusy = false;
        Summary = item.Title + ": " + result;
    }

    [RelayCommand]
    private void ApplyAll()
    {
        foreach (var item in Items)
        {
            if (item.IsAvailable && !item.IsEnabled)
                item.IsEnabled = true;
        }
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var item in Items)
        {
            if (item.IsEnabled)
                item.IsEnabled = false;
        }

        Summary = "Alle Änderungen zurückgesetzt (soweit ohne Adminrechte möglich).";
    }
}
