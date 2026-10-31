using NexusOptimizer.Core;
using Xunit;

namespace NexusOptimizer.Core.Tests;

public class FrametimeHistogramTests
{
    private const double SixtyHz = 1000.0 / 60.0;

    [Fact]
    public void ConstantSixtyFps_ProducesSixtyFpsMetrics()
    {
        var histogram = new FrametimeHistogram();
        for (int i = 0; i < 1000; i++) histogram.Add(SixtyHz);

        var snapshot = histogram.Snapshot();

        Assert.Equal(1000, snapshot.Count);
        Assert.Equal(SixtyHz, snapshot.P50Ms, 1);
        Assert.InRange(1000.0 / snapshot.P50Ms, 59.5, 60.5);
        Assert.InRange(snapshot.P1LowFps, 59.5, 60.5);
        Assert.Equal(0, snapshot.Discarded);
    }

    [Fact]
    public void Percentiles_MatchSortedReference_WithinBucketResolution()
    {
        var random = new Random(42);
        var samples = new List<double>();
        var histogram = new FrametimeHistogram();

        for (int i = 0; i < 20_000; i++)
        {
            // realistische Verteilung: 8-30 ms, gelegentliche Spitzen
            double ms = 8 + random.NextDouble() * 22;
            if (i % 997 == 0) ms = 60 + random.NextDouble() * 40;
            samples.Add(ms);
            histogram.Add(ms);
        }

        samples.Sort();

        foreach (double percentile in new[] { 0.5, 0.95, 0.99, 0.999 })
        {
            double expected = samples[(int)Math.Floor(percentile * (samples.Count - 1))];
            double actual = histogram.PercentileMs(percentile);
            Assert.InRange(actual, expected - 0.2, expected + 0.2);
        }
    }

    [Fact]
    public void OnePercentLow_IsInverseOfFrametimePercentile_NotFpsPercentile()
    {
        // 990 gute Frames + 10 schlechte => die schlechtesten 1 % sind die 33-ms-Frames
        var histogram = new FrametimeHistogram();
        for (int i = 0; i < 990; i++) histogram.Add(SixtyHz);
        for (int i = 0; i < 10; i++) histogram.Add(33.0);

        var snapshot = histogram.Snapshot();

        Assert.InRange(snapshot.P1LowFps, 1000.0 / 33.0 - 0.5, 1000.0 / 33.0 + 0.5);
        Assert.InRange(snapshot.P01LowFps, 1000.0 / 33.0 - 0.5, 1000.0 / 33.0 + 0.5);
        Assert.InRange(snapshot.P50Ms, 16.0, 17.0);
    }

    [Fact]
    public void ValuesAboveMaxBucket_AreDiscardedAndCounted()
    {
        var histogram = new FrametimeHistogram(bucketMs: 0.1, maxMs: 500);
        histogram.Add(16.7);
        histogram.Add(5000);   // Suspend-Artefakt
        histogram.Add(double.NaN);
        histogram.Add(-1);

        var snapshot = histogram.Snapshot();

        Assert.Equal(1, snapshot.Count);
        Assert.Equal(1, snapshot.Discarded);
    }

    [Fact]
    public void EmptyHistogram_ReturnsZeroInsteadOfThrowing()
    {
        var histogram = new FrametimeHistogram();
        var snapshot = histogram.Snapshot();

        Assert.Equal(0, snapshot.Count);
        Assert.Equal(0, snapshot.P1LowFps);
        Assert.Equal(0, snapshot.P99Ms);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var histogram = new FrametimeHistogram();
        for (int i = 0; i < 100; i++) histogram.Add(20);
        histogram.Reset();

        Assert.Equal(0, histogram.Count);
        Assert.Equal(0, histogram.MeanMs);
    }
}

public class StutterDetectorTests
{
    [Fact]
    public void SteadyFrames_ProduceNoStutters()
    {
        var detector = new StutterDetector();
        for (int i = 0; i < 200; i++)
            Assert.Equal(StutterKind.None, detector.Classify(16.7));
    }

    [Fact]
    public void DoubleMedian_IsClassifiedAsStutter()
    {
        var detector = new StutterDetector();
        for (int i = 0; i < 120; i++) detector.Classify(16.7);

        Assert.Equal(StutterKind.Micro, detector.Classify(45.0));
        Assert.Equal(StutterKind.Hang, detector.Classify(300.0));
    }

    [Fact]
    public void RollingMedian_IgnoresSingleSpike()
    {
        var detector = new StutterDetector();
        for (int i = 0; i < 119; i++) detector.Classify(16.7);
        detector.Classify(500);

        Assert.InRange(detector.RollingMedianMs, 16.0, 17.0);
    }
}

public class SessionAggregatorTests
{
    private const double SixtyHz = 1000.0 / 60.0;

    [Fact]
    public void StableSession_HasHighStabilityIndex()
    {
        var aggregator = new SessionAggregator();
        for (int i = 0; i < 600; i++)
            aggregator.Add(new FrametimeSample(SixtyHz, i * 16_677));

        var metrics = aggregator.Build();

        Assert.Equal(600, metrics.Frames);
        Assert.InRange(metrics.MeanFps, 59, 61);
        Assert.InRange(metrics.MedianFps, 59, 61);
        Assert.InRange(metrics.StabilityIndex, 90, 100);
        Assert.Equal(0, metrics.MicroStutters);
        Assert.Equal(0, metrics.Hangs);
    }

    [Fact]
    public void UnstableSession_HasLowerStabilityIndexThanStableOne()
    {
        var stable = new SessionAggregator();
        var unstable = new SessionAggregator();
        var random = new Random(7);

        for (int i = 0; i < 600; i++)
        {
            stable.Add(new FrametimeSample(SixtyHz, i));
            double jitter = 8 + random.NextDouble() * 45;           // 8-53 ms
            unstable.Add(new FrametimeSample(jitter, i));
        }

        Assert.True(unstable.Build().StabilityIndex < stable.Build().StabilityIndex);
    }

    [Fact]
    public void FramesOverBudget_AreCounted()
    {
        var aggregator = new SessionAggregator { BudgetMs = SixtyHz };
        for (int i = 0; i < 90; i++) aggregator.Add(new FrametimeSample(10, i));
        for (int i = 0; i < 10; i++) aggregator.Add(new FrametimeSample(30, i));

        var metrics = aggregator.Build();

        Assert.InRange(metrics.FramesOverBudgetPercent, 9.9, 10.1);
    }

    [Fact]
    public void StuttersPerMinute_IsFiniteForShortSessions()
    {
        var aggregator = new SessionAggregator();
        for (int i = 0; i < 130; i++) aggregator.Add(new FrametimeSample(SixtyHz, i));
        aggregator.Add(new FrametimeSample(400, 130));

        var metrics = aggregator.Build();

        Assert.Equal(1, metrics.Hangs);
        Assert.True(double.IsFinite(metrics.StuttersPerMinute));
    }
}

public class RingBufferTests
{
    [Fact]
    public void KeepsLastItemsInOrder()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4);

        Assert.Equal(3, buffer.Count);
        Assert.Equal(2, buffer[0]);
        Assert.Equal(4, buffer[2]);
    }

    [Fact]
    public void Clear_ResetsCount()
    {
        var buffer = new RingBuffer<int>(5);
        buffer.Add(1);
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
    }
}
