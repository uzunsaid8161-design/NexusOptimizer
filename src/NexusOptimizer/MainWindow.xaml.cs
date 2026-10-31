using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using NexusOptimizer.ViewModels;
using NexusOptimizer.Views;

namespace NexusOptimizer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DashboardView _dashboardView;
    private readonly OptimizerView _optimizerView;

    public MainWindow(MainViewModel viewModel, DashboardViewModel dashboardViewModel, OptimizerViewModel optimizerViewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _dashboardView = new DashboardView { DataContext = dashboardViewModel };
        _optimizerView = new OptimizerView { DataContext = optimizerViewModel };
        DataContext = _viewModel;
        PageHost.Content = _dashboardView;

        Loaded += OnWindowLoaded;

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SelectedPage))
                SwitchPage(_viewModel.SelectedPage);
        };
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var motion = Services.MotionService.Current;
        if (!motion.Enabled) return;

        Opacity = 1;
        var dur = motion.T("Motion.Normal", TimeSpan.FromMilliseconds(200));
        var ease = motion.E("Easing.Emphasized", new QuinticEase { EasingMode = EasingMode.EaseOut });

        RootGrid.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(dur)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop },
            HandoffBehavior.SnapshotAndReplace);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.98, 1, new Duration(dur)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop },
            HandoffBehavior.SnapshotAndReplace);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.98, 1, new Duration(dur)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop },
            HandoffBehavior.SnapshotAndReplace);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(16, 0, new Duration(dur)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void SwitchPage(int index)
    {
        if (index == 0) { PageHost.Show(_dashboardView); return; }
        if (index == 2) { PageHost.Show(_optimizerView); return; }

        PageHost.Show(new EmptyPageView
        {
            DataContext = new EmptyPageViewModel(_viewModel.PageTitle, "Dieser Bereich wird in der nächsten Stufe umgesetzt.")
        });
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen) ExitFullscreen();
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    // ---------- Vollbild ----------

    private bool _isFullscreen;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private Rect _boundsBeforeFullscreen;
    private WindowChrome? _windowedChrome;

    private static readonly WindowChrome FullscreenChrome = new()
    {
        CaptionHeight = 0,
        ResizeBorderThickness = new Thickness(0),
        GlassFrameThickness = new Thickness(0),
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false
    };

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen) return;

        _stateBeforeFullscreen = WindowState;
        _boundsBeforeFullscreen = new Rect(Left, Top, Width, Height);
        _windowedChrome ??= WindowChrome.GetWindowChrome(this);

        WindowState = WindowState.Normal;
        WindowChrome.SetWindowChrome(this, FullscreenChrome);
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        _isFullscreen = true;
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen) return;

        Topmost = false;
        ResizeMode = ResizeMode.CanResize;
        if (_windowedChrome != null) WindowChrome.SetWindowChrome(this, _windowedChrome);
        Left = _boundsBeforeFullscreen.X;
        Top = _boundsBeforeFullscreen.Y;
        Width = _boundsBeforeFullscreen.Width;
        Height = _boundsBeforeFullscreen.Height;
        WindowState = _stateBeforeFullscreen;
        _isFullscreen = false;
    }

    private void OnOpenMotion(object sender, RoutedEventArgs e)
    {
        var window = new MotionPlaygroundWindow { Owner = this };
        window.Show();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSidebarEnter(object sender, MouseEventArgs e) => AnimateSidebar(true);

    private void OnSidebarLeave(object sender, MouseEventArgs e) => AnimateSidebar(false);

    private void AnimateSidebar(bool expand)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Sidebar.BeginAnimation(WidthProperty,
            new DoubleAnimation(expand ? 236 : 76, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });

        var labels = new[] { NavLabel0, NavLabel1, NavLabel2, NavLabel3, NavLabel4 };
        foreach (var label in labels)
        {
            if (expand) label.Visibility = Visibility.Visible;

            var fade = new DoubleAnimation(expand ? 1 : 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
            if (!expand)
            {
                fade.Completed += (_, _) =>
                {
                    foreach (var item in labels) item.Visibility = Visibility.Collapsed;
                };
            }
            label.BeginAnimation(OpacityProperty, fade);
        }
    }
}
