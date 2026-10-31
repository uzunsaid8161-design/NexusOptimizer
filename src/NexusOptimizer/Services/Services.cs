using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using Serilog;

namespace NexusOptimizer.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusOptimizer");

    public static string Logs => Path.Combine(Root, "logs");
    public static string DatabaseFile => Path.Combine(Root, "nexus.db");
}

/// <summary>
/// Misst echte Compositor-Frames über CompositionTarget.Rendering.
/// Stufe 1 der FPS-Erfassung; ETW (DXGI/D3D9) folgt in Stufe 4.
/// </summary>
public sealed class FpsSampler : IDisposable
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _frames;
    private readonly List<double> _frameTimes = new();

    public double CurrentFps { get; private set; }
    public int FramesLastSecond { get; private set; }
    public double AverageFrameTimeMs { get; private set; }

    public event Action<double>? FpsUpdated;

    public FpsSampler()
    {
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _frames++;
        double elapsed = _watch.Elapsed.TotalMilliseconds;
        if (elapsed < 1000) return;

        CurrentFps = _frames * 1000.0 / elapsed;
        FramesLastSecond = _frames;
        AverageFrameTimeMs = CurrentFps > 0 ? 1000.0 / CurrentFps : 0;

        _frameTimes.Add(AverageFrameTimeMs);
        if (_frameTimes.Count > 20000) _frameTimes.RemoveAt(0);

        _frames = 0;
        _watch.Restart();
        FpsUpdated?.Invoke(CurrentFps);
    }

    public double LowPercentile(double fraction)
    {
        if (_frameTimes.Count == 0) return 0;
        var sorted = new List<double>(_frameTimes);
        sorted.Sort();
        int index = (int)Math.Floor((sorted.Count - 1) * (1.0 - fraction));
        index = Math.Clamp(index, 0, sorted.Count - 1);
        double worstFrame = sorted[index];
        return worstFrame > 0 ? 1000.0 / worstFrame : 0;
    }

    public void Dispose() => CompositionTarget.Rendering -= OnRendering;
}

public sealed class SystemSample
{
    public double CpuPercent { get; init; }
    public double GpuPercent { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public double RamPercent => RamTotalGb > 0 ? RamUsedGb / RamTotalGb * 100.0 : 0;
}

/// <summary>Sammelt CPU/GPU/RAM im Hintergrundthread (keine UI-Blockade).</summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly PerformanceCounter? _cpu;
    private readonly PerformanceCounter? _cpuAlt;
    private readonly PerformanceCounter? _ramFree;
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private DateTime _gpuRefresh = DateTime.MinValue;
    private double _ramTotalGb;

    public SystemSample Current { get; private set; } = new();
    public event Action<SystemSample>? Updated;

    public SystemMonitor()
    {
        try { _cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true); _cpu.NextValue(); } catch { }
        try { _cpuAlt = new PerformanceCounter("Processor", "% Processor Time", "_Total", true); _cpuAlt.NextValue(); } catch { }
        try { _ramFree = new PerformanceCounter("Memory", "Available MBytes", true); _ramFree.NextValue(); } catch { }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (System.Management.ManagementObject o in searcher.Get())
                _ramTotalGb = Convert.ToDouble(o["TotalPhysicalMemory"]) / 1073741824.0;
        }
        catch (Exception ex) { Log.Warning(ex, "RAM-Gesamt nicht lesbar"); }

        _thread = new Thread(Loop) { IsBackground = true, Name = "NexusSystemMonitor" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                double cpu = -1;
                if (_cpu != null) { try { cpu = _cpu.NextValue(); } catch { } }
                if (cpu < 0 && _cpuAlt != null) { try { cpu = _cpuAlt.NextValue(); } catch { } }

                double freeMb = -1;
                if (_ramFree != null) { try { freeMb = _ramFree.NextValue(); } catch { } }

                var sample = new SystemSample
                {
                    CpuPercent = Math.Clamp(cpu < 0 ? 0 : cpu, 0, 100),
                    GpuPercent = ReadGpu(),
                    RamTotalGb = _ramTotalGb,
                    RamUsedGb = freeMb < 0 ? 0 : Math.Max(0, _ramTotalGb - freeMb / 1024.0)
                };

                Current = sample;
                Updated?.Invoke(sample);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "System-Sampling fehlgeschlagen");
            }

            Thread.Sleep(1000);
        }
    }

    private double ReadGpu()
    {
        try
        {
            if ((DateTime.Now - _gpuRefresh).TotalSeconds > 15)
            {
                _gpuRefresh = DateTime.Now;
                _gpuCounters.Clear();
                var category = new PerformanceCounterCategory("GPU Engine");
                foreach (var instance in category.GetInstanceNames())
                {
                    if (instance.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        counter.NextValue();
                        _gpuCounters[instance] = counter;
                    }
                    catch { }
                }
            }

            double sum = 0;
            foreach (var counter in _gpuCounters.Values)
            {
                try { sum += counter.NextValue(); } catch { }
            }
            return Math.Clamp(sum, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>SQLite-Speicher für Sessions, FPS-Samples, Optimierungen und Einstellungen.</summary>
public sealed class Database
{
    private string ConnectionString => $"Data Source={AppPaths.DatabaseFile}";

    public void Initialize()
    {
        Directory.CreateDirectory(AppPaths.Root);
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Sessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GameName TEXT NOT NULL,
                StartedUtc TEXT NOT NULL,
                EndedUtc TEXT,
                FramesTotal INTEGER NOT NULL DEFAULT 0,
                AvgFps REAL NOT NULL DEFAULT 0,
                MinFps REAL NOT NULL DEFAULT 0,
                MaxFps REAL NOT NULL DEFAULT 0,
                P1Low REAL NOT NULL DEFAULT 0,
                P01Low REAL NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS FpsSamples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL,
                TimestampUtc TEXT NOT NULL,
                Fps REAL NOT NULL,
                FrameTimeMs REAL NOT NULL,
                FOREIGN KEY(SessionId) REFERENCES Sessions(Id));
            CREATE TABLE IF NOT EXISTS Optimizations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Key TEXT NOT NULL,
                Description TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                PreviousValue TEXT,
                CreatedUtc TEXT NOT NULL,
                Reverted INTEGER NOT NULL DEFAULT 0);
            """;
        cmd.ExecuteNonQuery();
        Log.Information("Datenbank bereit: {Path}", AppPaths.DatabaseFile);
    }

    public string? GetSetting(string key)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Settings(Key, Value) VALUES($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void RecordOptimization(string key, string description, bool enabled, string previousValue)
    {
        try
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Optimizations(Key, Description, Enabled, PreviousValue, CreatedUtc, Reverted)
                VALUES($k, $d, $e, $p, $c, 0)
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$d", description);
            cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$p", previousValue);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("s"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Optimierung konnte nicht protokolliert werden");
        }
    }
}
