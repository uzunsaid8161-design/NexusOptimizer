using NexusOptimizer.Core.Fps;
using Xunit;

namespace NexusOptimizer.Core.Tests;

public class PresentEventPipelineTests
{
    private const double TicksPerMs = 10_000; // 10 MHz für deterministische Tests

    private static PresentEvent Event(double ms, uint pid = 100, bool dwm = false, long luid = 0) =>
        new((long)(ms * TicksPerMs), pid, luid, dwm);

    private static PresentEventPipeline Pipeline(params uint[] ignored) =>
        new(new HashSet<uint>(ignored), dedupeMs: 0.5, outlierMs: 1000, ticksPerMillisecond: TicksPerMs);

    [Fact]
    public void FirstEvent_HasNoFrametime_AndIsNotAccepted()
    {
        var pipeline = Pipeline();

        Assert.False(pipeline.TryAccept(Event(0), out _));
        Assert.True(pipeline.TryAccept(Event(16.6), out var frame));
        Assert.Equal(16.6, frame.FrametimeMs, 3);
    }

    [Fact]
    public void DuplicateWithinHalfMillisecond_IsDropped()
    {
        var pipeline = Pipeline();
        pipeline.TryAccept(Event(0), out _);
        pipeline.TryAccept(Event(16.0), out _);

        bool accepted = pipeline.TryAccept(Event(16.2), out _);

        Assert.False(accepted);
        Assert.Equal(1, pipeline.DuplicatesDropped);
    }

    [Fact]
    public void EventAfterDedupeWindow_IsAccepted()
    {
        var pipeline = Pipeline();
        pipeline.TryAccept(Event(0), out _);
        pipeline.TryAccept(Event(16.0), out _);

        Assert.True(pipeline.TryAccept(Event(16.8), out var frame));
        Assert.Equal(0.8, frame.FrametimeMs, 3);
    }

    [Fact]
    public void DwmEvents_AreIgnored()
    {
        var pipeline = Pipeline();
        pipeline.TryAccept(Event(0, dwm: true), out _);

        Assert.Equal(1, pipeline.IgnoredEvents);
    }

    [Fact]
    public void IgnoredProcessList_FiltersOverlayAndLauncher()
    {
        var pipeline = Pipeline(ignored: 777);
        pipeline.TryAccept(Event(0, pid: 777), out _);
        pipeline.TryAccept(Event(16, pid: 777), out _);

        Assert.Equal(2, pipeline.IgnoredEvents);
    }

    [Fact]
    public void FramesAreTrackedPerProcess()
    {
        var pipeline = Pipeline();
        pipeline.TryAccept(Event(0, pid: 1), out _);
        pipeline.TryAccept(Event(0, pid: 2), out _);

        Assert.True(pipeline.TryAccept(Event(16, pid: 1), out var first));
        Assert.True(pipeline.TryAccept(Event(33, pid: 2), out var second));

        Assert.Equal(1u, first.ProcessId);
        Assert.Equal(33 - 0, second.FrametimeMs, 3);
    }

    [Fact]
    public void OutlierAboveOneSecond_IsCountedButNotEmitted()
    {
        var pipeline = Pipeline();
        pipeline.TryAccept(Event(0), out _);

        Assert.False(pipeline.TryAccept(Event(1500), out _));
        Assert.Equal(1, pipeline.OutliersDropped);

        // nach dem Ausreißer wird normal weitergemessen
        Assert.True(pipeline.TryAccept(Event(1516), out var frame));
        Assert.Equal(16, frame.FrametimeMs, 3);
    }
}

public class FrameRateMeterTests
{
    private const double TicksPerMs = 10_000;

    [Fact]
    public void OneSecondWindow_CountsFramesCorrectly()
    {
        var meter = new FrameRateMeter(ticksPerMillisecond: TicksPerMs);

        for (int i = 1; i <= 60; i++)
            meter.Add(new AcceptedFrame((long)(i * 16.6 * TicksPerMs), 1, 16.6));

        Assert.InRange(meter.CurrentFps, 59, 60);
        Assert.InRange(meter.SmoothFps, 59, 61);
    }

    [Fact]
    public void SmoothFps_AveragesLastFrames()
    {
        var meter = new FrameRateMeter(smoothingFrames: 120, ticksPerMillisecond: TicksPerMs);

        for (int i = 0; i < 240; i++)
            meter.Add(new AcceptedFrame((long)(i * 16.6 * TicksPerMs), 1, i < 120 ? 33.3 : 16.6));

        // die letzten 120 Frames sind schnell -> SmoothFps nahe 60
        Assert.InRange(meter.SmoothFps, 55, 61);
    }

    [Fact]
    public void MaxFrametime_IsTracked()
    {
        var meter = new FrameRateMeter(ticksPerMillisecond: TicksPerMs);
        meter.Add(new AcceptedFrame(10, 1, 10));
        meter.Add(new AcceptedFrame(20, 1, 45));

        Assert.Equal(45, meter.MaxFrametimeMs, 3);
    }
}

public class FrameCaptureEngineTests
{
    [Fact]
    public void Synthetic60Fps_ProducesAboutSixtyFramesPerSecond()
    {
        var engine = new FrameCaptureEngine();
        var events = SyntheticPresentSource.Create(fps: 60, seconds: 3);

        foreach (var presentEvent in events) engine.Accept(presentEvent);

        Assert.InRange(events.Count, 179, 181);
        Assert.InRange(engine.Meter.SmoothFps, 59, 61);
    }

    [Fact]
    public void Duplicates_InSyntheticStream_AreRemoved()
    {
        var engine = new FrameCaptureEngine();
        var events = SyntheticPresentSource.Create(fps: 60, seconds: 2, duplicateEveryNFrames: 5);

        int accepted = events.Count(e => engine.Accept(e));

        Assert.True(accepted < events.Count);
        Assert.True(engine.Pipeline.DuplicatesDropped > 0);
    }

    [Fact]
    public void Batches_AreEmittedPerSecond_ThroughChannel()
    {
        var engine = new FrameCaptureEngine();
        var events = SyntheticPresentSource.Create(fps: 60, seconds: 4);

        foreach (var presentEvent in events) engine.Accept(presentEvent);

        int batches = 0;
        while (engine.TryReadBatch(out var batch))
        {
            batches++;
            Assert.InRange(batch.Frames, 50, 70);
            Assert.True(batch.Fps > 0);
        }

        Assert.InRange(batches, 1, 4);
    }

    [Fact]
    public void Spikes_AreVisibleInBatchMax()
    {
        var engine = new FrameCaptureEngine();
        var events = SyntheticPresentSource.Create(fps: 60, seconds: 3, spikeEveryNFrames: 60, spikeMs: 45);

        foreach (var presentEvent in events) engine.Accept(presentEvent);

        double max = 0;
        while (engine.TryReadBatch(out var batch)) max = Math.Max(max, batch.MaxFrametimeMs);

        Assert.True(max >= 40, $"Max-Frametime war {max}");
    }
}
