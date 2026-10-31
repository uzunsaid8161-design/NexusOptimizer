using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using NexusOptimizer.Services;

namespace NexusOptimizer.Behaviors;

/// <summary>
/// Deklarative Animationen: ui:Animations.Entry="Stagger" | "Pop" | "Fade" | "Slide",
/// ui:Animations.Hover="Card", ui:Animations.Press="True".
/// </summary>
public static class Animations
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.RegisterAttached(
        "Entry", typeof(string), typeof(Animations), new PropertyMetadata(null, OnEntryChanged));

    public static readonly DependencyProperty EntryIndexProperty = DependencyProperty.RegisterAttached(
        "EntryIndex", typeof(int), typeof(Animations), new PropertyMetadata(0));

    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover", typeof(string), typeof(Animations), new PropertyMetadata(null, OnHoverChanged));

    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Animations), new PropertyMetadata(false, OnPressChanged));

    public static void SetEntry(DependencyObject element, string? value) => element.SetValue(EntryProperty, value);
    public static string? GetEntry(DependencyObject element) => (string?)element.GetValue(EntryProperty);

    public static void SetEntryIndex(DependencyObject element, int value) => element.SetValue(EntryIndexProperty, value);
    public static int GetEntryIndex(DependencyObject element) => (int)element.GetValue(EntryIndexProperty);

    public static void SetHover(DependencyObject element, string? value) => element.SetValue(HoverProperty, value);
    public static string? GetHover(DependencyObject element) => (string?)element.GetValue(HoverProperty);

    public static void SetPress(DependencyObject element, bool value) => element.SetValue(PressProperty, value);
    public static bool GetPress(DependencyObject element) => (bool)element.GetValue(PressProperty);

    private static void OnEntryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (element.IsLoaded) PlayEntry(element);
        else element.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        element.Loaded -= OnLoaded;
        PlayEntry(element);
    }

    private static void PlayEntry(FrameworkElement element)
    {
        var motion = MotionService.Current;
        switch (GetEntry(element))
        {
            case "Fade":
                motion.FadeIn(element);
                break;
            case "Slide":
                motion.SlideIn(element, motion.V("Motion.Offset.M", 16));
                break;
            case "Stagger":
                motion.PopIn(element, TimeSpan.FromSeconds(motion.V("Motion.Stagger.Step", 0.035) * GetEntryIndex(element)));
                break;
            case "Pop":
                motion.PopIn(element);
                break;
        }
    }

    // ---------- Hover ----------

    private static void OnHoverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.MouseEnter -= OnMouseEnter;
        element.MouseLeave -= OnMouseLeave;
        if (e.NewValue is string mode && mode == "Card")
        {
            element.MouseEnter += OnMouseEnter;
            element.MouseLeave += OnMouseLeave;
        }
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var motion = MotionService.Current;
        if (!motion.Enabled) return;

        var (scale, translate) = MotionService.EnsureTransform(element);
        var glow = EnsureGlow(element);
        var dur = motion.T("Motion.Fast", TimeSpan.FromMilliseconds(140));
        var ease = motion.E("Easing.Standard", new CubicEase { EasingMode = EasingMode.EaseOut });

        double hoverScale = motion.V("Motion.Scale.Hover", 1.015);
        Animate(scale, ScaleTransform.ScaleXProperty, hoverScale, dur, ease);
        Animate(scale, ScaleTransform.ScaleYProperty, hoverScale, dur, ease);
        Animate(translate, TranslateTransform.YProperty, -2, dur, ease);
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(1, new Duration(dur)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.RenderTransform is not TransformGroup group || group.Children.Count < 2) return;

        var motion = MotionService.Current;
        if (!motion.Enabled) return;

        var scale = (ScaleTransform)group.Children[0];
        var translate = (TranslateTransform)group.Children[1];
        var glow = EnsureGlow(element);
        var dur = motion.T("Motion.Fast", TimeSpan.FromMilliseconds(140));
        var ease = motion.E("Easing.Exit", new CubicEase { EasingMode = EasingMode.EaseIn });

        Animate(scale, ScaleTransform.ScaleXProperty, 1, dur, ease);
        Animate(scale, ScaleTransform.ScaleYProperty, 1, dur, ease);
        Animate(translate, TranslateTransform.YProperty, 0, dur, ease);
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0, new Duration(dur)) { FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    private static DropShadowEffect EnsureGlow(FrameworkElement element)
    {
        if (element.Effect is DropShadowEffect existing && existing.Color == Color.FromRgb(0x22, 0xD3, 0xEE))
            return existing;

        var glow = new DropShadowEffect
        {
            Color = Color.FromRgb(0x22, 0xD3, 0xEE),
            BlurRadius = 22,
            ShadowDepth = 0,
            Opacity = 0
        };
        element.Effect = glow;
        return glow;
    }

    // ---------- Press ----------

    private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.NewValue is true)
        {
            element.PreviewMouseLeftButtonDown += OnPressDown;
            element.PreviewMouseLeftButtonUp += OnPressUpMouse;
            element.MouseLeave += OnPressUpLeave;
        }
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e) => Press(sender, true);

    private static void OnPressUpMouse(object sender, MouseButtonEventArgs e) => Press(sender, false);

    private static void OnPressUpLeave(object sender, MouseEventArgs e) => Press(sender, false);

    private static void Press(object sender, bool down)
    {
        if (sender is not FrameworkElement element) return;
        var motion = MotionService.Current;
        if (!motion.Enabled) return;

        var (scale, _) = MotionService.EnsureTransform(element);
        double target = down ? motion.V("Motion.Scale.Press", 0.985) : 1.0;
        var dur = motion.T("Motion.Instant", TimeSpan.FromMilliseconds(80));
        var ease = motion.E("Easing.Standard", new CubicEase { EasingMode = EasingMode.EaseOut });

        Animate(scale, ScaleTransform.ScaleXProperty, target, dur, ease);
        Animate(scale, ScaleTransform.ScaleYProperty, target, dur, ease);
    }

    private static void Animate(DependencyObject target, DependencyProperty property, double to, TimeSpan duration, IEasingFunction easing)
    {
        var animation = new DoubleAnimation(to, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        (target as IAnimatable)?.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
