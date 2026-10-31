using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusOptimizer.Controls;

namespace NexusOptimizer.Services;

public enum MotionLevel
{
    Off,
    Reduced,
    Full
}

/// <summary>
/// Zentrales Animationssystem. Basiswerte werden immer auf den Endzustand gesetzt,
/// animiert wird mit FillBehavior.Stop – dadurch kann nichts „kleben“ und jede
/// Animation ist per SnapshotAndReplace unterbrechbar.
/// </summary>
public sealed class MotionService
{
    public static MotionService Current { get; } = new();

    public MotionLevel Level { get; set; } = MotionLevel.Full;

    /// <summary>Globale Geschwindigkeit (1.0 = Standard, 2.0 = doppelt so schnell).</summary>
    public double SpeedFactor { get; set; } = 1.0;

    public bool SystemAllowsMotion => SystemParameters.ClientAreaAnimation && SystemParameters.MenuAnimation;

    public bool IsRemoteSession => SystemParameters.IsRemoteSession;

    public MotionLevel EffectiveLevel =>
        !SystemAllowsMotion || IsRemoteSession && Level == MotionLevel.Full ? MotionLevel.Reduced : Level;

    public bool Enabled => EffectiveLevel != MotionLevel.Off;

    public TimeSpan Scale(TimeSpan value) =>
        TimeSpan.FromMilliseconds(value.TotalMilliseconds * Math.Clamp(SpeedFactor, 0.25, 4.0));

    public TimeSpan T(string key, TimeSpan fallback)
    {
        if (Application.Current?.TryFindResource(key) is Duration duration && duration.HasTimeSpan)
            return Scale(duration.TimeSpan);
        return Scale(fallback);
    }

    public double V(string key, double fallback) =>
        Application.Current?.TryFindResource(key) is double value ? value : fallback;

    public IEasingFunction E(string key, IEasingFunction fallback) =>
        Application.Current?.TryFindResource(key) as IEasingFunction ?? fallback;

    public static (ScaleTransform Scale, TranslateTransform Translate) EnsureTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group &&
            group.Children.Count >= 2 &&
            group.Children[0] is ScaleTransform scale &&
            group.Children[1] is TranslateTransform translate)
            return (scale, translate);

        var newScale = new ScaleTransform(1, 1);
        var newTranslate = new TranslateTransform(0, 0);
        var host = new TransformGroup();
        host.Children.Add(newScale);
        host.Children.Add(newTranslate);
        element.RenderTransform = host;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return (newScale, newTranslate);
    }

    private static void Animate(DependencyObject target, DependencyProperty property, double from, double to,
        TimeSpan duration, IEasingFunction? easing, TimeSpan delay)
    {
        var animation = new DoubleAnimation(from, to, new Duration(duration))
        {
            EasingFunction = easing,
            BeginTime = delay,
            FillBehavior = FillBehavior.Stop
        };

        if (target is IAnimatable animatable)
            animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    // ---------- Einblenden ----------

    public void FadeIn(FrameworkElement element, TimeSpan? duration = null, TimeSpan? delay = null)
    {
        element.Opacity = 1;
        if (!Enabled) return;
        Animate(element, UIElement.OpacityProperty, 0, 1, duration ?? T("Motion.Normal", TimeSpan.FromMilliseconds(200)),
            E("Easing.Standard", new CubicEase { EasingMode = EasingMode.EaseOut }), delay ?? TimeSpan.Zero);
    }

    public void SlideIn(FrameworkElement element, double fromOffset = 16, TimeSpan? duration = null)
    {
        var (_, translate) = EnsureTransform(element);
        translate.Y = 0;
        element.Opacity = 1;
        if (!Enabled) return;

        var easing = E("Easing.Standard", new CubicEase { EasingMode = EasingMode.EaseOut });
        var dur = duration ?? T("Motion.Normal", TimeSpan.FromMilliseconds(200));
        Animate(translate, TranslateTransform.YProperty, fromOffset, 0, dur, easing, TimeSpan.Zero);
        Animate(element, UIElement.OpacityProperty, 0, 1, dur, easing, TimeSpan.Zero);
    }

    public void PopIn(FrameworkElement element, TimeSpan? delay = null)
    {
        var (scale, translate) = EnsureTransform(element);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.Y = 0;
        element.Opacity = 1;
        if (!Enabled) return;

        var dur = T("Motion.Normal", TimeSpan.FromMilliseconds(200));
        var startScale = V("Motion.Scale.PopStart", 0.92);
        var ease = E("Easing.Overshoot", new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 });
        var begin = delay ?? TimeSpan.Zero;

        Animate(scale, ScaleTransform.ScaleXProperty, startScale, 1, dur, ease, begin);
        Animate(scale, ScaleTransform.ScaleYProperty, startScale, 1, dur, ease, begin);
        Animate(element, UIElement.OpacityProperty, 0, 1, dur, ease, begin);
        Animate(translate, TranslateTransform.YProperty, 12, 0, dur, ease, begin);
    }

    public void StaggerIn(IEnumerable<FrameworkElement> items, int maxStagger = 8)
    {
        int index = 0;
        foreach (var item in items)
        {
            if (index >= maxStagger)
            {
                PopIn(item, TimeSpan.Zero);
            }
            else
            {
                double stepSeconds = V("Motion.Stagger.Step", 0.035);
                PopIn(item, TimeSpan.FromSeconds(stepSeconds * index));
            }
            index++;
        }
    }

    // ---------- Zahlen ----------

    public void CountUp(AnimatedNumber target, double value) => target.AnimateTo(value);

    // ---------- Feedback ----------

    public void Pulse(FrameworkElement element, double peak = 1.06, TimeSpan? duration = null)
    {
        var (scale, _) = EnsureTransform(element);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        if (!Enabled) return;

        var dur = duration ?? T("Motion.Fast", TimeSpan.FromMilliseconds(140));
        var half = TimeSpan.FromMilliseconds(dur.TotalMilliseconds / 2);
        var ease = E("Easing.Standard", new CubicEase { EasingMode = EasingMode.EaseOut });

        var sx = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop, Duration = new Duration(dur) };
        sx.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(half), ease));
        sx.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(dur), ease));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sx.Clone(), HandoffBehavior.SnapshotAndReplace);
    }

    public void Shake(FrameworkElement element)
    {
        var (_, translate) = EnsureTransform(element);
        translate.X = 0;
        if (!Enabled) return;

        var dur = T("Motion.Slow", TimeSpan.FromMilliseconds(320));
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop, Duration = new Duration(dur) };
        double[] offsets = { 0, -6, 6, -5, 4, 0 };
        for (int i = 0; i < offsets.Length; i++)
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(dur.TotalSeconds * i / (offsets.Length - 1)))));

        translate.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public void Ripple(UIElement host, Point origin, Brush? brush = null)
    {
        if (!Enabled) return;
        if (AdornerLayer.GetAdornerLayer(host) is not AdornerLayer layer) return;

        var ripple = new RippleAdorner(host, origin, brush ?? Brushes.White);
        layer.Add(ripple);
        var dur = T("Motion.Slower", TimeSpan.FromMilliseconds(480));
        ripple.Play(dur, () => layer.Remove(ripple));
    }

    // ---------- Seitenwechsel ----------

    public void CrossFade(ContentControl host, FrameworkElement next, double slide = 20)
    {
        var previous = host.Content as UIElement;
        if (!Enabled || previous is null)
        {
            host.Content = next;
            return;
        }

        var easeOut = E("Easing.Exit", new CubicEase { EasingMode = EasingMode.EaseIn });
        var fadeOut = new DoubleAnimation(1, 0, new Duration(T("Motion.Fast", TimeSpan.FromMilliseconds(140))))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.Stop
        };

        fadeOut.Completed += (_, _) =>
        {
            previous.Opacity = 1;
            host.Content = next;

            var (_, translate) = EnsureTransform(next);
            translate.Y = 0;
            next.Opacity = 1;

            var easeIn = E("Easing.Emphasized", new QuinticEase { EasingMode = EasingMode.EaseOut });
            var dur = T("Motion.Normal", TimeSpan.FromMilliseconds(200));
            Animate(next, UIElement.OpacityProperty, 0, 1, dur, easeIn, TimeSpan.Zero);
            Animate(translate, TranslateTransform.YProperty, slide, 0, dur, easeIn, TimeSpan.Zero);
        };

        previous.BeginAnimation(UIElement.OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Cached-Effekt nur setzen, wenn die Karte bewegt wird (GPU-freundlich).</summary>
    public static void Cache(FrameworkElement element)
    {
        element.CacheMode = new BitmapCache(1.0);
    }
}
