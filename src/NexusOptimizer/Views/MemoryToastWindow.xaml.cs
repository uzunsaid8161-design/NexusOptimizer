using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Media;
using NexusOptimizer.Services;

namespace NexusOptimizer.Views;

public partial class MemoryToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private readonly MemoryReleaseResult _result;

    public MemoryToastWindow(MemoryReleaseResult result)
    {
        InitializeComponent();
        _result = result;
        TitleText.Text = result.TargetGb > 0
            ? result.TargetReached ? $"{result.TargetGb:0}-GB-ZIEL ERREICHT" : $"{result.TargetGb:0}-GB-ZIEL NICHT ERREICHT"
            : "SANFTE RAM-FREIGABE";
        ResultText.Text = result.TargetGb > 0
            ? result.TargetReached ? "Zusätzlicher Speicher ist verfügbar" : "Nur erreichbare Hintergrundspeicher wurden getrimmt"
            : "NexusOptimizer-Arbeitsspeicher bereinigt";
        TitleText.Foreground = result.TargetGb > 0 && !result.TargetReached ? new SolidColorBrush(Color.FromRgb(251, 191, 36)) : new SolidColorBrush(Color.FromRgb(230, 237, 243));
        AmountText.Text = $"{result.FreedGb:0.00} GB";
        string detail = $"{result.WorkingSetFreedMb:N0} MB Working Set · {result.AvailableAfterGb:0.0} GB verfügbar";
        DetailText.Text = result.TargetGb > 0 && !result.TargetReached ? detail + " · kein Programm geschlossen" : detail;
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            long style = GetWindowLongPtrW(handle, GwlExStyle).ToInt64();
            SetWindowLongPtrW(handle, GwlExStyle, new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
        }
        catch { }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 8, workArea.Right - Width - 20);
        Top = Math.Max(workArea.Top + 8, workArea.Bottom - Height - 20);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        ToastTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });

        double fraction = _result.TargetGb > 0 ? Math.Clamp(_result.FreedGb / _result.TargetGb, 0, 1) : 1;
        ProgressFill.BeginAnimation(WidthProperty,
            new DoubleAnimation(0, ProgressTrack.ActualWidth * fraction, TimeSpan.FromMilliseconds(800)) { EasingFunction = ease });
        _ = CloseAfterAnimationAsync();
    }

    private async Task CloseAfterAnimationAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(3600));
        if (!IsVisible) return;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        ToastTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, 18, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr newStyle);
}
