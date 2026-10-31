namespace NexusOptimizer.Core;

/// <summary>
/// Histogramm der Frametimes mit 0,01-ms-Buckets.
///
/// Entscheidung: Perzentile werden aus einem Histogramm berechnet, nicht aus einem
/// sortierten Array. Bei einer 3-Stunden-Session mit 300 FPS liegen ~3,2 Mio. Samples an;
/// ein sortiertes Array kostet ~26 MB und das Sortieren blockiert. Das Histogramm hat
/// eine feste Größe (Standard: 0-1000 ms => 100.000 Buckets, ~400 KB) und liefert
/// Perzentile innerhalb von 0,01 ms.
///
/// 1 %- und 0,1 %-Low werden NICHT als Perzentil der FPS-Liste berechnet, sondern als
/// FPS, die sich aus dem Mittel der schlechtesten 1 % bzw. 0,1 % Frametimes ergibt.
/// Das entspricht der Definition von PresentMon/CapFrameX und ist der fachlich
/// korrekte Weg: ein Perzentil der FPS-Werte gewichtet Ausreißer falsch.
/// </summary>
public sealed class FrametimeHistogram
{
    public const double DefaultBucketMs = 0.01;
    public const double DefaultMaxMs = 1000.0;

    private readonly double _bucketMs;
    private readonly int[] _buckets;
    private readonly object _sync = new();

    private long _count;
    private double _sumMs;
    private double _minMs = double.MaxValue;
    private double _maxMs;
    private long _discarded;

    public FrametimeHistogram(double bucketMs = DefaultBucketMs, double maxMs = DefaultMaxMs)
    {
        if (bucketMs <= 0) throw new ArgumentOutOfRangeException(nameof(bucketMs));
        if (maxMs <= bucketMs) throw new ArgumentOutOfRangeException(nameof(maxMs));

        _bucketMs = bucketMs;
        _buckets = new int[(int)Math.Ceiling(maxMs / bucketMs)];
    }

    public long Count => Interlocked.Read(ref _count);
    public long Discarded => Interlocked.Read(ref _discarded);
    public double MeanMs => _count == 0 ? 0 : _sumMs / _count;
    public double MinMs => _count == 0 ? 0 : _minMs;
    public double MaxMs => _maxMs;

    /// <summary>Fügt eine Frametime hinzu. Ausreißer über maxMs werden nicht verfälscht,
    /// sondern separat gezählt (Suspend-/Hänger-Artefakte).</summary>
    public void Add(double frametimeMs)
    {
        if (double.IsNaN(frametimeMs) || frametimeMs <= 0) return;

        if (frametimeMs >= _buckets.Length * _bucketMs)
        {
            Interlocked.Increment(ref _discarded);
            return;
        }

        int index = (int)(frametimeMs / _bucketMs);
        lock (_sync)
        {
            _buckets[index]++;
            _count++;
            _sumMs += frametimeMs;
            if (frametimeMs < _minMs) _minMs = frametimeMs;
            if (frametimeMs > _maxMs) _maxMs = frametimeMs;
        }
    }

    /// <summary>Repräsentativer Frametime-Wert eines Buckets (Buckettmitte).</summary>
    private double BucketValue(int index) => (index + 0.5) * _bucketMs;

    /// <summary>Frametime-Perzentil in Millisekunden (Nearest-Rank, Wert aus der Bucketmitte).</summary>
    public double PercentileMs(double percentile)
    {
        if (percentile is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(percentile));

        lock (_sync)
        {
            if (_count == 0) return 0;

            long rank = (long)Math.Ceiling(percentile * _count) - 1;
            if (rank < 0) rank = 0;

            long cumulative = 0;
            for (int i = 0; i < _buckets.Length; i++)
            {
                int bucketCount = _buckets[i];
                if (bucketCount == 0) continue;

                cumulative += bucketCount;
                if (cumulative > rank) return BucketValue(i);
            }

            return _maxMs;
        }
    }

    /// <summary>
    /// Mittelwert der schlechtesten <paramref name="fraction"/> der Frames in ms.
    /// Grundlage für 1 %- und 0,1 %-Lows; läuft von oben durch die Buckets.
    /// </summary>
    public double AverageWorstMs(double fraction)
    {
        if (fraction is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(fraction));

        lock (_sync)
        {
            if (_count == 0) return 0;

            long target = Math.Max(1, (long)Math.Round(_count * fraction));
            long taken = 0;
            double sum = 0;

            for (int i = _buckets.Length - 1; i >= 0 && taken < target; i--)
            {
                int bucketCount = _buckets[i];
                if (bucketCount == 0) continue;

                long take = Math.Min(bucketCount, target - taken);
                sum += take * BucketValue(i);
                taken += take;
            }

            return taken == 0 ? 0 : sum / taken;
        }
    }

    /// <summary>FPS-Perzentil als Kehrwert der Frametime.</summary>
    public double PercentileFps(double percentile)
    {
        double ms = PercentileMs(percentile);
        return ms <= 0 ? 0 : 1000.0 / ms;
    }

    public double MedianMs => PercentileMs(0.5);

    public FrametimeHistogramSnapshot Snapshot()
    {
        lock (_sync)
        {
            double p1LowMs = AverageWorstMs(0.01);
            double p01LowMs = AverageWorstMs(0.001);

            return new FrametimeHistogramSnapshot(
                Count: _count,
                Discarded: _discarded,
                MeanMs: _count == 0 ? 0 : _sumMs / _count,
                MinMs: _count == 0 ? 0 : _minMs,
                MaxMs: _maxMs,
                P50Ms: PercentileMs(0.5),
                P95Ms: PercentileMs(0.95),
                P99Ms: PercentileMs(0.99),
                P999Ms: PercentileMs(0.999),
                P1LowFps: p1LowMs > 0 ? 1000.0 / p1LowMs : 0,
                P01LowFps: p01LowMs > 0 ? 1000.0 / p01LowMs : 0);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            Array.Clear(_buckets);
            _count = 0;
            _sumMs = 0;
            _minMs = double.MaxValue;
            _maxMs = 0;
            _discarded = 0;
        }
    }
}

public readonly record struct FrametimeHistogramSnapshot(
    long Count,
    long Discarded,
    double MeanMs,
    double MinMs,
    double MaxMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double P999Ms,
    double P1LowFps,
    double P01LowFps);
