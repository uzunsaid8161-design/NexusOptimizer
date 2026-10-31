using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NexusOptimizer.Services;

namespace NexusOptimizer.Controls;

internal sealed class RippleAdorner : Adorner
{
    private readonly Ellipse _ellipse;
    private readonly VisualCollection _children;
    private readonly Point _origin;

    public RippleAdorner(UIElement owner, Point origin, Brush brush) : base(owner)
    {
        _origin = origin;
        IsHitTestVisible = false;
        _ellipse = new Ellipse
        {
            Width = 180,
            Height = 180,
            Fill = brush,
            Opacity = 0.4,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.1, 0.1)
        };
        _children = new VisualCollection(this) { _ellipse };
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _ellipse.Measure(constraint);
        return base.MeasureOverride(constraint);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _ellipse.Arrange(new Rect(_origin.X - 90, _origin.Y - 90, 180, 180));
        return finalSize;
    }

    public void Play(TimeSpan duration, Action onDone)
    {
        if (_ellipse.RenderTransform is ScaleTransform scale)
        {
            var grow = new DoubleAnimation(0.1, 2.6, new Duration(duration)) { FillBehavior = FillBehavior.Stop };
            grow.Completed += (_, _) => onDone();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone(), HandoffBehavior.SnapshotAndReplace);
        }

        _ellipse.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.42, 0, new Duration(duration)) { FillBehavior = FillBehavior.Stop },
            HandoffBehavior.SnapshotAndReplace);
    }
}

/// <summary>Zahl mit Count-Up, tabellarischen Ziffern und ohne Layout-Sprung.</summary>
public class AnimatedNumber : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AnimatedNumber),
        new PropertyMetadata(0d, (d, e) => ((AnimatedNumber)d).OnValueChanged((double)e.OldValue, (double)e.NewValue)));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(AnimatedNumber),
        new PropertyMetadata(0d, (d, e) => ((AnimatedNumber)d).UpdateText()));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(AnimatedNumber),
        new PropertyMetadata("0,0", (d, _) => ((AnimatedNumber)d).UpdateText()));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Format
    {
        get => (string)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    private double DisplayValue
    {
        get => (double)GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    public AnimatedNumber()
    {
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        UpdateText();
    }

    public void AnimateTo(double target)
    {
        if (!MotionService.Current.Enabled)
        {
            DisplayValue = target;
            return;
        }

        var animation = new DoubleAnimation(DisplayValue, target,
            new Duration(MotionService.Current.T("Motion.Normal", TimeSpan.FromMilliseconds(200))))
        {
            EasingFunction = MotionService.Current.E("Easing.Emphasized", new QuinticEase { EasingMode = EasingMode.EaseOut }),
            FillBehavior = FillBehavior.Stop
        };

        animation.Completed += (_, _) => SetCurrentValue(DisplayValueProperty, target);
        BeginAnimation(DisplayValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnValueChanged(double oldValue, double newValue) => AnimateTo(newValue);

    private void UpdateText() => Text = DisplayValue.ToString(Format, CultureInfo.CurrentCulture);
}

/// <summary>Ring-Chart mit animiertem Arc-Fill und synchroner Zahl.</summary>
public class AnimatedRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AnimatedRing),
        new PropertyMetadata(0d, (d, e) => ((AnimatedRing)d).AnimateTo((double)e.NewValue)));

    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(AnimatedRing),
        new PropertyMetadata(0d, (d, _) => ((AnimatedRing)d).InvalidateVisual()));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(AnimatedRing),
        new PropertyMetadata(10d, (d, _) => ((AnimatedRing)d).InvalidateVisual()));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private void AnimateTo(double target)
    {
        double normalized = Math.Clamp(target / 100.0, 0, 1);
        if (!MotionService.Current.Enabled)
        {
            Progress = normalized;
            return;
        }

        var animation = new DoubleAnimation(Progress, normalized,
            new Duration(MotionService.Current.T("Motion.Ring", TimeSpan.FromMilliseconds(400))))
        {
            EasingFunction = MotionService.Current.E("Easing.Emphasized", new QuinticEase { EasingMode = EasingMode.EaseOut }),
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => SetCurrentValue(ProgressProperty, normalized);
        BeginAnimation(ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        double thickness = Thickness;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2 - thickness / 2;

        var track = new Pen(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), thickness);
        dc.DrawEllipse(null, track, center, radius, radius);

        double sweep = Progress * 360.0;
        if (sweep <= 0.1) return;

        var start = PointOnCircle(center, radius, -90);
        var end = PointOnCircle(center, radius, -90 + sweep);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();

        var accent = new Pen(new LinearGradientBrush(
            Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x8B, 0x5C, 0xF6), 45), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        dc.DrawGeometry(null, accent, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        double rad = angleDegrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}

/// <summary>Sparkline: neuer Punkt schiebt sich ein, alter gleitet links raus.</summary>
public class Sparkline : FrameworkElement
{
    private readonly List<double> _values = new();
    public int Capacity { get; set; } = 60;
    public Brush Stroke { get; set; } = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));

    private static readonly DependencyProperty ShiftProperty = DependencyProperty.Register(
        nameof(Shift), typeof(double), typeof(Sparkline),
        new PropertyMetadata(0d, (d, _) => ((Sparkline)d).InvalidateVisual()));

    private double Shift
    {
        get => (double)GetValue(ShiftProperty);
        set => SetValue(ShiftProperty, value);
    }

    public void Push(double value)
    {
        _values.Add(value);
        if (_values.Count > Capacity) _values.RemoveAt(0);

        if (MotionService.Current.Enabled)
        {
            var animation = new DoubleAnimation(1, 0,
                new Duration(MotionService.Current.T("Motion.Fast", TimeSpan.FromMilliseconds(140))))
            {
                EasingFunction = MotionService.Current.E("Easing.Standard", new CubicEase { EasingMode = EasingMode.EaseOut }),
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (_, _) => SetCurrentValue(ShiftProperty, 0d);
            BeginAnimation(ShiftProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        _values.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;

        double max = Math.Max(_values.Max(), 1);
        double step = ActualWidth / Math.Max(1, _values.Count - 1);
        double offset = Shift * step;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < _values.Count; i++)
            {
                double x = i * step - offset;
                double y = ActualHeight - Math.Clamp(_values[i] / max, 0, 1) * (ActualHeight - 4) - 2;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();

        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
}

/// <summary>Host für Seitenwechsel mit CrossFade.</summary>
public class CrossFadeHost : ContentControl
{
    public void Show(FrameworkElement next) => MotionService.Current.CrossFade(this, next);
}

/// <summary>Button mit Ripple am Klickpunkt.</summary>
public class RippleButton : Button
{
    private Point _lastPoint;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _lastPoint = e.GetPosition(this);
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnClick()
    {
        MotionService.Current.Ripple(this, _lastPoint, new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)));
        base.OnClick();
    }
}

/// <summary>Toasts: gleiten von rechts ein, verschwinden nach 4 s.</summary>
public class ToastHost : StackPanel
{
    public ToastHost()
    {
        Orientation = Orientation.Vertical;
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Right;
        Margin = new Thickness(0, 0, 24, 24);
        IsHitTestVisible = false;
    }

    public void Show(string message, bool success = true)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0E, 0x16, 0x22)),
            BorderBrush = new SolidColorBrush(success ? Color.FromRgb(0x34, 0xD3, 0x99) : Color.FromRgb(0xF8, 0x71, 0x71)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(0, 6, 0, 0),
            Child = new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 12 },
            Opacity = 1
        };

        var (_, translate) = MotionService.EnsureTransform(border);
        translate.X = 0;
        Children.Add(border);

        if (MotionService.Current.Enabled)
        {
            var dur = MotionService.Current.T("Motion.Normal", TimeSpan.FromMilliseconds(200));
            var ease = MotionService.Current.E("Easing.Emphasized", new QuinticEase { EasingMode = EasingMode.EaseOut });
            border.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(dur)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, new Duration(dur)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(1, 0, new Duration(MotionService.Current.T("Motion.Normal", TimeSpan.FromMilliseconds(200))))
            { FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) => Children.Remove(border);
            border.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        };
        timer.Start();
    }
}
