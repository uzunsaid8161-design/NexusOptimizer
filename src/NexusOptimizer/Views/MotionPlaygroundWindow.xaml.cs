using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NexusOptimizer.Controls;
using NexusOptimizer.Services;

namespace NexusOptimizer.Views;

public partial class MotionPlaygroundWindow : Window
{
    private readonly Random _random = new(7);

    public MotionPlaygroundWindow()
    {
        InitializeComponent();
        for (int i = 0; i < 40; i++) Spark.Push(20 + _random.NextDouble() * 60);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnFade(object sender, RoutedEventArgs e) => MotionService.Current.FadeIn(DemoCard);

    private void OnSlide(object sender, RoutedEventArgs e) => MotionService.Current.SlideIn(DemoCard, 24);

    private void OnPop(object sender, RoutedEventArgs e) => MotionService.Current.PopIn(DemoCard);

    private void OnStagger(object sender, RoutedEventArgs e)
    {
        var items = StaggerList.Children.OfType<FrameworkElement>().ToList();
        MotionService.Current.StaggerIn(items);
    }

    private void OnPulse(object sender, RoutedEventArgs e) => MotionService.Current.Pulse(DemoCard, 1.06);

    private void OnShake(object sender, RoutedEventArgs e)
    {
        MotionService.Current.Shake(DemoCard);
        Toasts.Show("Fehler-Shake ausgelöst", false);
    }

    private void OnRipple(object sender, RoutedEventArgs e)
    {
        MotionService.Current.Ripple(RippleTarget, new Point(RippleTarget.ActualWidth / 2, RippleTarget.ActualHeight / 2));
    }

    private void OnToast(object sender, RoutedEventArgs e) => Toasts.Show("Gespeichert – Toast mit Slide + Fade");

    private void OnCount(object sender, RoutedEventArgs e) => Number.Value = _random.Next(80, 240);

    private void OnRing(object sender, RoutedEventArgs e) => Ring.Value = _random.Next(20, 100);

    private void OnSpark(object sender, RoutedEventArgs e) => Spark.Push(20 + _random.NextDouble() * 80);

    private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LevelBox.SelectedIndex < 0 || MotionService.Current is null) return;
        MotionService.Current.Level = LevelBox.SelectedIndex switch
        {
            0 => MotionLevel.Full,
            1 => MotionLevel.Reduced,
            _ => MotionLevel.Off
        };
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedText is null) return;
        MotionService.Current.SpeedFactor = e.NewValue;
        SpeedText.Text = e.NewValue.ToString("0.0") + "×";
    }
}
