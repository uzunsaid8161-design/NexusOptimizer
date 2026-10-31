using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography.X509Certificates;
using NexusOptimizer.Models;

namespace NexusOptimizer.Services;

/// <summary>Low-frequency, read-only process sampling. Process termination is never automatic.</summary>
public sealed class ProcessManagerService : IDisposable
{
    private readonly Dictionary<int, PreviousProcessSample> _previousProcesses = [];
    private readonly ConcurrentDictionary<string, (string Publisher, string Signature)> _fileMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly PerformanceCounter? _diskCounter;
    private readonly List<PerformanceCounter> _networkCounters = [];
    private ulong _previousIdle, _previousKernel, _previousUser;
    private DateTime _previousSystemSample = DateTime.UtcNow;

    private sealed record PreviousProcessSample(TimeSpan Cpu, long ReadBytes, long WriteBytes, DateTime At, DateTime? Start);

    public ProcessManagerService()
    {
        try
        {
            _diskCounter = new PerformanceCounter("PhysicalDisk", "Disk Bytes/sec", "_Total", true);
            _diskCounter.NextValue();
        }
        catch { _diskCounter = null; }

        try
        {
            foreach (string instance in new PerformanceCounterCategory("Network Interface").GetInstanceNames())
            {
                try
                {
                    var counter = new PerformanceCounter("Network Interface", "Bytes Total/sec", instance, true);
                    counter.NextValue();
                    _networkCounters.Add(counter);
                }
                catch { }
            }
        }
        catch { }
    }

    public ProcessSystemSnapshot Sample(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime now = DateTime.UtcNow;
        ulong idle = 0, kernel = 0, user = 0;
        double cpu = ReadCpu(ref idle, ref kernel, ref user);
        _previousIdle = idle;
        _previousKernel = kernel;
        _previousUser = user;
        _previousSystemSample = now;

        var list = new List<ProcessSnapshot>();
        var liveIds = new HashSet<int>();
        int applications = 0;
        foreach (Process process in Process.GetProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (process)
            {
                try
                {
                    int id = process.Id;
                    liveIds.Add(id);
                    string name = process.ProcessName;
                    long memory = 0;
                    TimeSpan cpuTime = TimeSpan.Zero;
                    DateTime? start = null;
                    string? path = null;
                    int threads = 0;
                    try { memory = process.WorkingSet64; } catch { }
                    try { cpuTime = process.TotalProcessorTime; } catch { }
                    try { start = process.StartTime; } catch { }
                    try { path = process.MainModule?.FileName; } catch { }
                    try { threads = process.Threads.Count; } catch { }
                    try { if (process.MainWindowHandle != IntPtr.Zero) applications++; } catch { }

                    long read = 0, written = 0;
                    bool hasIo = false;
                    try
                    {
                        if (GetProcessIoCounters(process.Handle, out var counters))
                        {
                            read = unchecked((long)counters.ReadTransferCount);
                            written = unchecked((long)counters.WriteTransferCount);
                            hasIo = true;
                        }
                    }
                    catch { }

                    double cpuRate = 0, readRate = 0, writeRate = 0;
                    if (_previousProcesses.TryGetValue(id, out var previous) && previous.Start == start)
                    {
                        double elapsed = Math.Max(0.05, (now - previous.At).TotalSeconds);
                        cpuRate = Math.Clamp((cpuTime - previous.Cpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100d, 0, 100);
                        if (hasIo)
                        {
                            readRate = Math.Max(0, read - previous.ReadBytes) / elapsed;
                            writeRate = Math.Max(0, written - previous.WriteBytes) / elapsed;
                        }
                    }
                    _previousProcesses[id] = new PreviousProcessSample(cpuTime, read, written, now, start);
                    var metadata = GetMetadata(path);
                    list.Add(new ProcessSnapshot(id, 0, name, cpuRate, memory, readRate, writeRate,
                        "Läuft", path, metadata.Publisher, metadata.Signature, start, threads, IsProtectedProcess(id, name, path)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    // An individual process disappearing or denying access must not stop the refresh.
                }
            }
        }

        foreach (int id in _previousProcesses.Keys.Where(id => !liveIds.Contains(id)).ToArray()) _previousProcesses.Remove(id);
        double totalGb = 0, availableGb = 0;
        try
        {
            var memory = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref memory))
            {
                totalGb = memory.TotalPhysical / 1024d / 1024 / 1024;
                availableGb = memory.AvailablePhysical / 1024d / 1024 / 1024;
            }
        }
        catch { }

        double disk = SafeCounter(_diskCounter);
        double network = 0;
        foreach (var counter in _networkCounters) network += SafeCounter(counter);
        return new ProcessSystemSnapshot(list, cpu, Math.Max(0, totalGb - availableGb), totalGb, disk, network, applications, now);
    }

    private double ReadCpu(ref ulong idle, ref ulong kernel, ref ulong user)
    {
        try
        {
            if (!GetSystemTimes(out var i, out var k, out var u)) return 0;
            idle = i.Value; kernel = k.Value; user = u.Value;
            if (_previousKernel == 0) return 0;
            ulong idleDelta = idle - _previousIdle;
            ulong totalDelta = (kernel - _previousKernel) + (user - _previousUser);
            return totalDelta == 0 ? 0 : Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100);
        }
        catch { return 0; }
    }

    private (string Publisher, string Signature) GetMetadata(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("Nicht verfügbar", "Zugriff nicht verfügbar");
        return _fileMetadata.GetOrAdd(path, static file =>
        {
            try
            {
                using var certificate = X509Certificate.CreateFromSignedFile(file);
                using var cert2 = new X509Certificate2(certificate);
                string publisher = cert2.GetNameInfo(X509NameType.SimpleName, false);
                return (string.IsNullOrWhiteSpace(publisher) ? cert2.Subject : publisher, "Zertifikat eingebettet · Vertrauensstatus ungeprüft");
            }
            catch (System.Security.Cryptography.CryptographicException) { return ("Nicht signiert", "Keine Signatur gefunden"); }
            catch (Exception) { return ("Nicht verfügbar", "Signatur nicht lesbar"); }
        });
    }

    private static bool IsProtectedProcess(int pid, string name, string? path)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return true;
        string key = name.ToLowerInvariant();
        string[] protectedNames = ["system", "registry", "smss", "csrss", "wininit", "services", "lsass", "winlogon", "svchost", "fontdrvhost", "dwm", "secure system", "memory compression", "sihost", "audiodg", "MsMpEng", "NisSrv", "SecurityHealthService", "ekrn", "egui", "avguard", "avastsvc", "avgsvc", "bdagent", "vsserv", "mcshield", "mfemms", "sophos", "savservice", "ccsvchst", "avp", "avpui"];
        if (protectedNames.Contains(key, StringComparer.OrdinalIgnoreCase)) return true;
        string value = path ?? "";
        return value.Contains("\\Windows\\System32\\", StringComparison.OrdinalIgnoreCase) &&
               protectedNames.Contains(Path.GetFileNameWithoutExtension(value), StringComparer.OrdinalIgnoreCase);
    }

    public string GetProcessRelations(int processId)
    {
        var processes = new Dictionary<int, (int Parent, string Name)>();
        var services = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name FROM Win32_Process");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    int id = Convert.ToInt32(item["ProcessId"] ?? 0);
                    int parent = Convert.ToInt32(item["ParentProcessId"] ?? 0);
                    processes[id] = (parent, Convert.ToString(item["Name"]) ?? "Prozess");
                }
            }
        }
        catch (Exception ex) { return "Prozessbaum nicht verfügbar: " + ex.Message; }

        if (!processes.TryGetValue(processId, out var root)) return "Prozess wurde inzwischen beendet.";
        var lines = new List<string> { $"{root.Name} (PID {processId})" };
        AddChildren(processId, 1, new HashSet<int> { processId });
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT DisplayName FROM Win32_Service WHERE ProcessId = {processId}");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item) services.Add(Convert.ToString(item["DisplayName"]) ?? "Dienst");
            }
        }
        catch { }
        if (services.Count > 0) lines.Add("Dienste: " + string.Join(", ", services));
        return string.Join(Environment.NewLine, lines);

        void AddChildren(int parentId, int depth, HashSet<int> visited)
        {
            if (depth > 8) return;
            foreach (var child in processes.Where(pair => pair.Value.Parent == parentId).OrderBy(pair => pair.Value.Name))
            {
                if (!visited.Add(child.Key)) continue;
                lines.Add($"{new string(' ', depth * 2)}↳ {child.Value.Name} (PID {child.Key})");
                AddChildren(child.Key, depth + 1, visited);
            }
        }
    }

    private static double SafeCounter(PerformanceCounter? counter)
    {
        try { return Math.Max(0, counter?.NextValue() ?? 0); } catch { return 0; }
    }

    public void Dispose()
    {
        _diskCounter?.Dispose();
        foreach (var counter in _networkCounters) counter.Dispose();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; public readonly ulong Value => ((ulong)High << 32) | Low; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
