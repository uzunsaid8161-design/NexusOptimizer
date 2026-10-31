using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using NexusOptimizer.Behaviors;
using NexusOptimizer.Services;
using NexusOptimizer.ViewModels;
using NexusOptimizer.Views;

namespace NexusOptimizer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DashboardView _dashboardView;
    private readonly StatisticsView _statisticsView;
    private readonly OptimizerView _optimizerView;
    private readonly StartupManagerView _startupManagerView;
    private readonly BackupView _backupView;
    private readonly CleanerView _cleanerView;
    private readonly OverlayView _overlayView;
    private readonly SettingsView _settingsView;
    private readonly DisplayControlView _displayControlView;
    private readonly ProcessManagerView _processManagerView;
    private readonly DisplayControlViewModel _displayControlViewModel;
    private MemoryToastWindow? _memoryToast;
    private FrameworkElement? _currentPage;
    private int _activePageIndex;
    private int _pageTransitionVersion;
    private readonly HashSet<FrameworkElement> _visitedPages = [];

    public MainWindow(MainViewModel viewModel, DashboardViewModel dashboardViewModel, StatisticsViewModel statisticsViewModel, OptimizerViewModel optimizerViewModel,
        StartupManagerViewModel startupManagerViewModel, BackupViewModel backupViewModel, CleanerViewModel cleanerViewModel,
        OverlayViewModel overlayViewModel, SettingsViewModel settingsViewModel, DisplayControlViewModel displayControlViewModel,
        ProcessManagerViewModel processManagerViewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _dashboardView = new DashboardView { DataContext = dashboardViewModel };
        _statisticsView = new StatisticsView { DataContext = statisticsViewModel };
        _optimizerView = new OptimizerView { DataContext = optimizerViewModel };
        _startupManagerView = new StartupManagerView { DataContext = startupManagerViewModel };
        _backupView = new BackupView { DataContext = backupViewModel };
        _cleanerView = new CleanerView { DataContext = cleanerViewModel };
        _overlayView = new OverlayView { DataContext = overlayViewModel };
        _settingsView = new SettingsView { DataContext = settingsViewModel };
        _displayControlViewModel = displayControlViewModel;
        _displayControlView = new DisplayControlView { DataContext = displayControlViewModel };
        _processManagerView = new ProcessManagerView { DataContext = processManagerViewModel };
        DataContext = _viewModel;
        PageHost.Content = _dashboardView;
        _currentPage = _dashboardView;
        _visitedPages.Add(_dashboardView);

        Loaded += OnWindowLoaded;
        Closing += (_, _) => _displayControlViewModel.RollbackPendingOnClose();
        dashboardViewModel.MemoryReleaseCompleted += ShowMemoryToast;

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
        FrameworkElement next = GetPage(index);
        if (ReferenceEquals(next, _currentPage)) return;

        int version = ++_pageTransitionVersion;
        int previousIndex = _activePageIndex;
        var previous = _currentPage;
        var motion = MotionService.Current;
        bool animate = motion.Enabled && previous?.IsLoaded == true;
        bool fullMotion = motion.EffectiveLevel == MotionLevel.Full;
        double direction = index >= previousIndex ? 1 : -1;

        if (!animate)
        {
            SetCurrentPage(next, index);
            PlayPageEntry(next, direction, motion, animate: motion.Enabled);
            return;
        }

        ResetPageVisual(previous!);
        var (_, exitTranslate) = MotionService.EnsureTransform(previous!);
        var duration = motion.T(fullMotion ? "Motion.Normal" : "Motion.Fast",
            fullMotion ? TimeSpan.FromMilliseconds(210) : TimeSpan.FromMilliseconds(100));
        var easing = motion.E("Easing.Exit", new CubicEase { EasingMode = EasingMode.EaseIn });
        var fadeOut = new DoubleAnimation(1, 0, new Duration(duration)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        fadeOut.Completed += (_, _) =>
        {
            if (version != _pageTransitionVersion) return;
            ResetPageVisual(previous!);
            SetCurrentPage(next, index);
            PlayPageEntry(next, direction, motion, animate: true);
        };
        previous!.BeginAnimation(OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
        if (fullMotion)
        {
            var exitX = new DoubleAnimation(0, -direction * 12, new Duration(duration)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
            exitTranslate.BeginAnimation(TranslateTransform.XProperty, exitX, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private FrameworkElement GetPage(int index) => index switch
    {
        0 => _dashboardView,
        1 => _statisticsView,
        2 => _optimizerView,
        3 => _overlayView,
        4 => _settingsView,
        5 => _startupManagerView,
        6 => _backupView,
        7 => _cleanerView,
        8 => _displayControlView,
        9 => _processManagerView,
        _ => new EmptyPageView { DataContext = new EmptyPageViewModel(_viewModel.PageTitle, "Dieser Bereich wird in der nächsten Stufe umgesetzt.") }
    };

    private void SetCurrentPage(FrameworkElement page, int index)
    {
        bool revisit = _visitedPages.Contains(page);
        PageHost.Content = page;
        _currentPage = page;
        _activePageIndex = index;
        _visitedPages.Add(page);
        if (revisit)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (ReferenceEquals(PageHost.Content, page)) Animations.ReplayEntries(page);
            }));
        }
    }

    private static void ResetPageVisual(FrameworkElement page)
    {
        page.BeginAnimation(OpacityProperty, null);
        page.Opacity = 1;
        var (scale, translate) = MotionService.EnsureTransform(page);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        scale.ScaleX = scale.ScaleY = 1;
        translate.X = translate.Y = 0;
    }

    private static void PlayPageEntry(FrameworkElement page, double direction, MotionService motion, bool animate)
    {
        ResetPageVisual(page);
        if (!animate) return;

        var (scale, translate) = MotionService.EnsureTransform(page);
        var duration = motion.T(motion.EffectiveLevel == MotionLevel.Full ? "Motion.Normal" : "Motion.Fast",
            motion.EffectiveLevel == MotionLevel.Full ? TimeSpan.FromMilliseconds(210) : TimeSpan.FromMilliseconds(100));
        var easing = motion.E("Easing.Emphasized", new QuinticEase { EasingMode = EasingMode.EaseOut });
        var fade = new DoubleAnimation(0, 1, new Duration(duration)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        page.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

        if (motion.EffectiveLevel == MotionLevel.Full)
        {
            var slide = new DoubleAnimation(direction * 22, 0, new Duration(duration)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
            translate.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
            var zoomX = new DoubleAnimation(0.992, 1, new Duration(duration)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
            var zoomY = zoomX.Clone();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoomX, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoomY, HandoffBehavior.SnapshotAndReplace);
        }
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

    private void ShowMemoryToast(MemoryReleaseResult result)
    {
        if (_memoryToast?.IsVisible == true) _memoryToast.Close();
        _memoryToast = new MemoryToastWindow(result);
        _memoryToast.Show();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSidebarEnter(object sender, MouseEventArgs e) => AnimateSidebar(true);

    private void OnSidebarLeave(object sender, MouseEventArgs e) => AnimateSidebar(false);

    private void AnimateSidebar(bool expand)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Sidebar.BeginAnimation(WidthProperty,
            new DoubleAnimation(expand ? 236 : 76, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });

        var labels = new[] { NavLabel0, NavLabel1, NavLabel2, NavLabel3, NavLabel4, NavLabel5, NavLabel6, NavLabel7, NavLabel8, NavLabel9 };
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
