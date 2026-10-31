using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NexusOptimizer.Services;
using NexusOptimizer.ViewModels;

namespace NexusOptimizer.Views;

public partial class DashboardView : UserControl
{
    private DashboardViewModel? _viewModel;

    public DashboardView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _viewModel = DataContext as DashboardViewModel;
            if (_viewModel != null)
                _viewModel.FpsHistory.CollectionChanged += OnHistoryChanged;
            Redraw();
            StartLiveDot();
        };
        SizeChanged += (_, _) => Redraw();
    }

    private void StartLiveDot()
    {
        if (!MotionService.Current.Enabled) return;

        var cycle = new Duration(TimeSpan.FromSeconds(2));
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        LiveDotScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 1.15, cycle) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        LiveDotScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 1.15, cycle) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        LiveDot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0.35, cycle) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        if (_viewModel == null) return;

        double width = ChartCanvas.ActualWidth;
        double height = ChartCanvas.ActualHeight;
        if (width < 20 || height < 20) return;

        var values = _viewModel.FpsHistory;
        if (values.Count < 2) return;

        double max = Math.Max(60, values.Max() * 1.15);
        double stepX = width / Math.Max(1, values.Count - 1);

        var points = new PointCollection();
        for (int i = 0; i < values.Count; i++)
        {
            double x = i * stepX;
            double y = height - Math.Clamp(values[i] / max, 0, 1) * height;
            points.Add(new Point(x, y));
        }

        FpsPolyline.Points = points;

        var area = new PointCollection(points) { new Point(width, height), new Point(0, height) };
        AreaPolygon.Points = area;
    }
}
