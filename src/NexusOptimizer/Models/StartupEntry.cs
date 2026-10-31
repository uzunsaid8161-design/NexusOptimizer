using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusOptimizer.Models;

public partial class StartupEntry : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Location { get; init; }
    public required bool CanManage { get; init; }
    public required string Availability { get; init; }
    public string RegistryHive { get; init; } = string.Empty;
    public string RegistryView { get; init; } = string.Empty;
    public string RegistryPath { get; init; } = string.Empty;
    public string RegistryValue { get; init; } = string.Empty;
    public bool IsFileEntry { get; init; }

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isBusy;
}
