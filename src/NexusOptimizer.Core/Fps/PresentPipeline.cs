using System.Diagnostics;
using System.Threading.Channels;

namespace NexusOptimizer.Core.Fps;

/// <summary>Rohes Present-Event aus ETW (DXGI/D3D9) oder aus einer anderen Quelle.</summary>
public readonly record struct PresentEvent(long TimestampTicks, uint ProcessId, long AdapterLuid, bool IsDwm = false);

/// <summary>Ein akzeptierter Frame nach Dedupe und Filterung.</summary>
public readonly record struct AcceptedFrame(long TimestampTicks, uint ProcessId, double FrametimeMs);

/// <summary>Ein Sekunden-Batch, wie ihn die UI bekommt.</summary>
public sealed record FrameBatch(
    DateTimeOffset SecondUtc,
    uint ProcessId,
    int Frames,
    double Fps,
    double SmoothFps,
    double MaxFrametimeMs);

/// <summary>
/// Filterkette für Present-Events. Bewusst klein und ohne ETW-Abhängigkeit,
/// damit sie mit synthetischen Daten exakt getestet werden kann.
///
/// Regeln:
///  - DWM- und Overlay-Prozesse werden verworfen (sonst zählt der Desktop mit)
///  - Duplikate: gleicher Prozess im Abstand von weniger als 0,5 ms
///  - Ausreißer ab 1000 ms gelten als Suspend-Artefakt und werden nur gezählt
/// </summary>
public sealed class PresentEventPipeline
{
    private readonly double _dedupeMs;
    private readonly double _outlierMs;
    private readonly HashSet<uint> _ignoredProcesses;
    private readonly Dictionary<uint, long> _lastTicks = new();
    private readonly double _ticksPerMs;

    public PresentEventPipeline(
        HashSet<uint>? ignoredProcesses = null,
        double dedupeMs = 0.5,
        double outlierMs = 1000,
        double ticksPerMillisecond = 0)
    {
        _ignoredProcesses = ignoredProcesses ?? new HashSet<uint>();
        _dedupeMs = dedupeMs;
        _outlierMs = outlierMs;
        _ticksPerMs = ticksPerMillisecond > 0 ? ticksPerMillisecond : Stopwatch.Frequency / 1000.0;
    }

    public long IgnoredEvents { get; private set; }
    public long DuplicatesDropped { get; private set; }
    public long OutliersDropped { get; private set; }

    public bool TryAccept(in PresentEvent presentEvent, out AcceptedFrame frame)
    {
        frame = default;

        if (presentEvent.IsDwm || _ignoredProcesses.Contains(presentEvent.ProcessId))
        {
            IgnoredEvents++;
            return false;
        }

        if (_lastTicks.TryGetValue(presentEvent.ProcessId, out long last))
        {
            double deltaMs = (presentEvent.TimestampTicks - last) / _ticksPerMs;

            if (deltaMs < _dedupeMs)
            {
                DuplicatesDropped++;
                return false;
            }

            if (deltaMs >= _outlierMs)
            {
                OutliersDropped++;
                _lastTicks[presentEvent.ProcessId] = presentEvent.TimestampTicks;
                return false;
            }

            frame = new AcceptedFrame(presentEvent.TimestampTicks, presentEvent.ProcessId, deltaMs);
            _lastTicks[presentEvent.ProcessId] = presentEvent.TimestampTicks;
            return true;
        }

        _lastTicks[presentEvent.ProcessId] = presentEvent.TimestampTicks;
        return false; // erster Frame hat keine Frametime
    }

    public void Reset()
    {
        _lastTicks.Clear();
        IgnoredEvents = 0;
        DuplicatesDropped = 0;
        OutliersDropped = 0;
    }
}

/// <summary>
/// Berechnet FPS aus einem rollierenden 1-Sekunden-Fenster und zusätzlich eine
/// geglättete Momentan-Framerate über die letzten 120 Frames.
/// </summary>
public sealed class FrameRateMeter
{
    private readonly Queue<long> _windowTicks = new();
    private readonly RingBuffer<double> _lastFrames;
    private readonly double _ticksPerMs;

    public FrameRateMeter(int smoothingFrames = 120, double ticksPerMillisecond = 0)
    {
        _lastFrames = new RingBuffer<double>(smoothingFrames);
        _ticksPerMs = ticksPerMillisecond > 0 ? ticksPerMillisecond : Stopwatch.Frequency / 1000.0;
    }

    public double CurrentFps { get; private set; }
    public double SmoothFps { get; private set; }
    public double MaxFrametimeMs { get; private set; }

    public void Add(in AcceptedFrame frame)
    {
        _windowTicks.Enqueue(frame.TimestampTicks);
        TrimWindow(frame.TimestampTicks);

        _lastFrames.Add(frame.FrametimeMs);
        if (frame.FrametimeMs > MaxFrametimeMs) MaxFrametimeMs = frame.FrametimeMs;

        CurrentFps = _windowTicks.Count;
        SmoothFps = MeanFps();
    }

    private void TrimWindow(long now)
    {
        double oneSecond = 1000.0 * _ticksPerMs;
        while (_windowTicks.Count > 0 && now - _windowTicks.Peek() > oneSecond)
            _windowTicks.Dequeue();
    }

    private double MeanFps()
    {
        if (_lastFrames.Count == 0) return 0;
        double sum = 0;
        for (int i = 0; i < _lastFrames.Count; i++) sum += _lastFrames[i];
        double meanMs = sum / _lastFrames.Count;
        return meanMs > 0 ? 1000.0 / meanMs : 0;
    }

    public void Reset()
    {
        _windowTicks.Clear();
        _lastFrames.Clear();
        CurrentFps = 0;
        SmoothFps = 0;
        MaxFrametimeMs = 0;
    }
}

/// <summary>
/// Verbindet Pipeline und Meter und schreibt pro Sekunde einen Batch in einen Channel.
/// Die UI liest ausschließlich aus dem Channel - kein UI-Zugriff aus dem Event-Pfad.
/// </summary>
public sealed class FrameCaptureEngine
{
    private readonly PresentEventPipeline _pipeline;
    private readonly FrameRateMeter _meter;
    private readonly Channel<FrameBatch> _channel = Channel.CreateUnbounded<FrameBatch>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private long _secondStartTicks;
    private int _framesInSecond;
    private uint _lastProcessId;

    public FrameCaptureEngine(PresentEventPipeline? pipeline = null, FrameRateMeter? meter = null)
    {
        _pipeline = pipeline ?? new PresentEventPipeline();
        _meter = meter ?? new FrameRateMeter();
    }

    public ChannelReader<FrameBatch> Batches => _channel.Reader;

    public bool Accept(in PresentEvent presentEvent)
    {
        if (!_pipeline.TryAccept(presentEvent, out var frame)) return false;

        _meter.Add(frame);
        _framesInSecond++;
        _lastProcessId = frame.ProcessId;

        if (_secondStartTicks == 0) _secondStartTicks = frame.TimestampTicks;

        double ticksPerMs = Stopwatch.Frequency / 1000.0;
        if (frame.TimestampTicks - _secondStartTicks >= 1000 * ticksPerMs)
        {
            FlushBatch();
            _secondStartTicks = frame.TimestampTicks;
            _framesInSecond = 0;
        }

        return true;
    }

    private void FlushBatch()
    {
        var batch = new FrameBatch(
            DateTimeOffset.UtcNow,
            _lastProcessId,
            _framesInSecond,
            _meter.CurrentFps,
            _meter.SmoothFps,
            _meter.MaxFrametimeMs);

        _channel.Writer.TryWrite(batch);
    }

    public bool TryReadBatch(out FrameBatch batch) => _channel.Reader.TryRead(out batch!);

    public PresentEventPipeline Pipeline => _pipeline;
    public FrameRateMeter Meter => _meter;
}

/// <summary>
/// Synthetische Present-Quelle: ersetzt ein Spiel, damit die Pipeline ohne GPU,
/// ohne Adminrechte und deterministisch getestet werden kann.
/// </summary>
public static class SyntheticPresentSource
{
    public static IReadOnlyList<PresentEvent> Create(
        double fps = 60,
        double seconds = 5,
        double jitterPercent = 0,
        int spikeEveryNFrames = 0,
        double spikeMs = 40,
        uint processId = 4242,
        int duplicateEveryNFrames = 0,
        int seed = 1234)
    {
        var random = new Random(seed);
        double ticksPerMs = Stopwatch.Frequency / 1000.0;
        double baseMs = 1000.0 / fps;
        long ticks = 1_000_000;
        var events = new List<PresentEvent>((int)(fps * seconds));

        int total = (int)(fps * seconds);
        for (int i = 0; i < total; i++)
        {
            double ms = baseMs;
            if (jitterPercent > 0)
                ms += baseMs * (random.NextDouble() * 2 - 1) * (jitterPercent / 100.0);

            if (spikeEveryNFrames > 0 && i > 0 && i % spikeEveryNFrames == 0)
                ms = spikeMs;

            ticks += (long)(ms * ticksPerMs);
            events.Add(new PresentEvent(ticks, processId, 0, false));

            if (duplicateEveryNFrames > 0 && i > 0 && i % duplicateEveryNFrames == 0)
                events.Add(new PresentEvent(ticks + (long)(0.1 * ticksPerMs), processId, 0, false));
        }

        return events;
    }
}
