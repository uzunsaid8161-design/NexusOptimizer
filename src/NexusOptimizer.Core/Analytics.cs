namespace NexusOptimizer.Core;

public readonly record struct FrametimeSample(double Milliseconds, long TimestampTicks);

public enum StutterKind
{
    None,
    Micro,
    Hang
}

/// <summary>
/// Ruckelerkennung: eine Frametime gilt als Ruckler, wenn sie mehr als das
/// <see cref="Factor"/>-fache des gleitenden Medians der letzten Fenstergröße beträgt.
/// Median statt Mittelwert, weil einzelne Hänger den Mittelwert unbrauchbar machen.
/// </summary>
public sealed class StutterDetector
{
    private readonly RingBuffer<double> _window;
    private readonly double[] _sortScratch;
    private readonly double _factor;

    public StutterDetector(int windowSize = 120, double factor = 2.0)
    {
        if (windowSize < 5) throw new ArgumentOutOfRangeException(nameof(windowSize));
        if (factor <= 1) throw new ArgumentOutOfRangeException(nameof(factor));

        _window = new RingBuffer<double>(windowSize);
        _sortScratch = new double[windowSize];
        _factor = factor;
    }

    public int Count => _window.Count;

    public double RollingMedianMs
    {
        get
        {
            if (_window.Count == 0) return 0;
            _window.CopyTo(_sortScratch);
            var slice = _sortScratch.AsSpan(0, _window.Count);
            slice.Sort();
            int mid = _window.Count / 2;
            return _window.Count % 2 == 0 ? (slice[mid - 1] + slice[mid]) / 2.0 : slice[mid];
        }
    }

    /// <summary>Klassifiziert die Frametime gegen das Fenster, danach wird sie eingefügt.</summary>
    public StutterKind Classify(double frametimeMs)
    {
        double median = RollingMedianMs;
        StutterKind kind = StutterKind.None;

        if (median > 0 && frametimeMs > median * _factor)
            kind = frametimeMs >= 200 ? StutterKind.Hang : StutterKind.Micro;
        else if (median == 0 && frametimeMs >= 200)
            kind = StutterKind.Hang;

        _window.Add(frametimeMs);
        return kind;
    }

    public void Reset() => _window.Clear();
}

public readonly record struct SessionMetrics(
    DateTimeOffset StartedUtc,
    double DurationSeconds,
    long Frames,
    double MeanFps,
    double MedianFps,
    double P1LowFps,
    double P01LowFps,
    double P95Ms,
    double P99Ms,
    double StandardDeviationMs,
    long MicroStutters,
    long Hangs,
    double StuttersPerMinute,
    double FramesOverBudgetPercent,
    double StabilityIndex,
    long DiscardedFrames);

/// <summary>
/// Sammelt Frametimes einer Session und berechnet alle Kennzahlen aus echten Messwerten.
///
/// Stabilitätsindex (0-100): 100 = perfekt gleichmäßig. Gewichtung aus
/// Variationskoeffizient (45 %), Anteil Frames über Budget (35 %) und
/// Ruckelrate (20 %). Bewusst linear und nachvollziehbar, kein ML.
/// </summary>
public sealed class SessionAggregator
{
    private readonly FrametimeHistogram _histogram;
    private readonly StutterDetector _stutterDetector;
    private readonly object _sync = new();

    private DateTimeOffset? _startedUtc;
    private DateTimeOffset _lastTimestamp;
    private long _frames;
    private long _microStutters;
    private long _hangs;
    private long _overBudget;
    private double _sumMs;
    private double _sumSquares;
    private double _budgetMs = 1000.0 / 60.0;

    public SessionAggregator(FrametimeHistogram? histogram = null, StutterDetector? stutterDetector = null)
    {
        _histogram = histogram ?? new FrametimeHistogram();
        _stutterDetector = stutterDetector ?? new StutterDetector();
    }

    public double BudgetMs
    {
        get => _budgetMs;
        set => _budgetMs = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public void Add(FrametimeSample sample)
    {
        if (sample.Milliseconds <= 0) return;

        lock (_sync)
        {
            _startedUtc ??= DateTimeOffset.UtcNow;
            _lastTimestamp = DateTimeOffset.UtcNow;
            _frames++;
            _sumMs += sample.Milliseconds;
            _sumSquares += sample.Milliseconds * sample.Milliseconds;

            if (sample.Milliseconds > BudgetMs) _overBudget++;

            var kind = _stutterDetector.Classify(sample.Milliseconds);
            if (kind == StutterKind.Micro) _microStutters++;
            else if (kind == StutterKind.Hang) _hangs++;

            _histogram.Add(sample.Milliseconds);
        }
    }

    public SessionMetrics Build()
    {
        lock (_sync)
        {
            var snapshot = _histogram.Snapshot();
            double duration = _startedUtc is null ? 0 : Math.Max(0.001, (_lastTimestamp - _startedUtc.Value).TotalSeconds);
            double meanFps = snapshot.MeanMs > 0 ? 1000.0 / snapshot.MeanMs : 0;
            double cv = snapshot.MeanMs > 0 ? StandardDeviation() / snapshot.MeanMs : 0;
            double overBudgetShare = _frames == 0 ? 0 : (double)_overBudget / _frames;
            double stuttersPerMinute = duration > 0 ? (_microStutters + _hangs) / duration * 60.0 : 0;
            double stutterNorm = Math.Min(1.0, stuttersPerMinute / 60.0);

            double penalty = 0.45 * Math.Min(1.0, cv) + 0.35 * overBudgetShare + 0.20 * stutterNorm;
            double stability = Math.Clamp((1.0 - penalty) * 100.0, 0, 100);

            return new SessionMetrics(
                StartedUtc: _startedUtc ?? DateTimeOffset.UtcNow,
                DurationSeconds: duration,
                Frames: _frames,
                MeanFps: meanFps,
                MedianFps: 1000.0 / Math.Max(0.0001, snapshot.P50Ms),
                P1LowFps: snapshot.P1LowFps,
                P01LowFps: snapshot.P01LowFps,
                P95Ms: snapshot.P95Ms,
                P99Ms: snapshot.P99Ms,
                StandardDeviationMs: StandardDeviation(),
                MicroStutters: _microStutters,
                Hangs: _hangs,
                StuttersPerMinute: stuttersPerMinute,
                FramesOverBudgetPercent: overBudgetShare * 100.0,
                StabilityIndex: stability,
                DiscardedFrames: snapshot.Discarded);
        }
    }

    private double StandardDeviation()
    {
        if (_frames == 0) return 0;
        double mean = _sumMs / _frames;
        double variance = Math.Max(0, _sumSquares / _frames - mean * mean);
        return Math.Sqrt(variance);
    }

    public void Reset()
    {
        lock (_sync)
        {
            _histogram.Reset();
            _stutterDetector.Reset();
            _startedUtc = null;
            _frames = 0;
            _microStutters = 0;
            _hangs = 0;
            _overBudget = 0;
            _sumMs = 0;
            _sumSquares = 0;
        }
    }
}
