using System.Windows.Controls;

namespace NexusOptimizer.Views;

public partial class EmptyPageView : UserControl
{
    public EmptyPageView() => InitializeComponent();
}

public sealed class EmptyPageViewModel
{
    public EmptyPageViewModel(string title, string message)
    {
        Title = title;
        Message = message;
    }

    public string Title { get; }
    public string Message { get; }
}
