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
        if (e.NewValue is string mode && (mode is "Card" or "Control" or "Nav"))
        {
            element.MouseEnter += OnMouseEnter;
            element.MouseLeave += OnMouseLeave;
        }
    }

    /// <summary>Replays declared entrance motion when a cached page is visited again.</summary>
    public static void ReplayEntries(DependencyObject root)
    {
        if (!MotionService.Current.Enabled) return;
        Visit(root);

        static void Visit(DependencyObject node)
        {
            if (node is FrameworkElement element && GetEntry(element) is not null)
                PlayEntry(element);
            try
            {
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++) Visit(VisualTreeHelper.GetChild(node, i));
            }
            catch { }
        }
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var motion = MotionService.Current;
        string mode = GetHover(element) ?? "Card";
        var (scale, translate) = MotionService.EnsureTransform(element);
        var dur = motion.T(mode == "Card" ? "Motion.Normal" : "Motion.Fast",
            mode == "Card" ? TimeSpan.FromMilliseconds(190) : TimeSpan.FromMilliseconds(150));
        var ease = motion.E("Easing.Overshoot", new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 });
        double targetScale = motion.Enabled ? HoverScale(element, mode, motion) : 1;
        double lift = motion.Enabled ? mode == "Card" ? -3 : mode == "Nav" ? -1 : -1.5 : 0;

        AnimateToState(scale, ScaleTransform.ScaleXProperty, targetScale, dur, ease, motion.Enabled);
        AnimateToState(scale, ScaleTransform.ScaleYProperty, targetScale, dur, ease, motion.Enabled);
        AnimateToState(translate, TranslateTransform.YProperty, lift, dur, ease, motion.Enabled);
        if (mode != "Nav")
        {
            var glow = EnsureGlow(element);
            AnimateToState(glow, DropShadowEffect.OpacityProperty, motion.Enabled ? mode == "Card" ? 0.78 : 0.58 : 0,
                dur, ease, motion.Enabled);
        }
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var motion = MotionService.Current;
        string mode = GetHover(element) ?? "Card";
        var (scale, translate) = MotionService.EnsureTransform(element);
        var dur = motion.T(mode == "Card" ? "Motion.Normal" : "Motion.Fast",
            mode == "Card" ? TimeSpan.FromMilliseconds(180) : TimeSpan.FromMilliseconds(135));
        var ease = motion.E("Easing.Exit", new CubicEase { EasingMode = EasingMode.EaseIn });
        AnimateToState(scale, ScaleTransform.ScaleXProperty, 1, dur, ease, motion.Enabled);
        AnimateToState(scale, ScaleTransform.ScaleYProperty, 1, dur, ease, motion.Enabled);
        AnimateToState(translate, TranslateTransform.YProperty, 0, dur, ease, motion.Enabled);
        if (mode != "Nav")
            AnimateToState(EnsureGlow(element), DropShadowEffect.OpacityProperty, 0, dur, ease, motion.Enabled);
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

    private static double HoverScale(FrameworkElement element, string mode, MotionService motion) => mode switch
    {
        "Nav" => motion.V("Motion.Scale.NavHover", 1.06),
        "Control" => motion.V("Motion.Scale.ControlHover", 1.045),
        _ => motion.V("Motion.Scale.Hover", 1.035)
    };

    private static void AnimateToState(DependencyObject target, DependencyProperty property, double value,
        TimeSpan duration, IEasingFunction easing, bool animate)
    {
        double from = (double)target.GetValue(property);
        if (target is IAnimatable animatable) animatable.BeginAnimation(property, null, HandoffBehavior.SnapshotAndReplace);
        target.SetValue(property, value);
        if (!animate || duration <= TimeSpan.Zero || Math.Abs(from - value) < 0.0005 || target is not IAnimatable animationTarget) return;

        var animation = new DoubleAnimation(from, value, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        animationTarget.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
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
        double target = !motion.Enabled ? 1 : down
            ? motion.V("Motion.Scale.Press", 0.985)
            : element.IsMouseOver && GetHover(element) is string mode ? HoverScale(element, mode, motion) : 1.0;
        var dur = motion.T("Motion.Instant", TimeSpan.FromMilliseconds(80));
        var ease = motion.E("Easing.Standard", new CubicEase { EasingMode = EasingMode.EaseOut });

        AnimateToState(scale, ScaleTransform.ScaleXProperty, target, dur, ease, motion.Enabled);
        AnimateToState(scale, ScaleTransform.ScaleYProperty, target, dur, ease, motion.Enabled);
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
