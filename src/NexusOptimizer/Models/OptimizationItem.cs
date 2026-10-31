using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusOptimizer.Models;

public partial class OptimizationItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Risk { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool IsAvailable { get; set; } = true;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;
}
