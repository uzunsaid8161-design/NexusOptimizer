using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace NexusOptimizer.Services;

public sealed record MemoryReleaseResult(
    double AvailableBeforeGb,
    double AvailableAfterGb,
    double FreedGb,
    double TargetGb,
    bool TargetReached,
    long WorkingSetFreedMb,
    int ProcessesTrimmed,
    bool StandbyPurged,
    bool Elevated,
    string Message);

/// <summary>
/// Gibt Arbeitsspeicher frei - so weit es ohne Kernel-Treiber möglich ist.
///
/// Zwei Stufen:
///  - sanft:  eigener Prozess (EmptyWorkingSet) + Standby-Liste (nur mit Adminrechten)
///  - intensiv: zusätzlich die Working Sets aller erreichbaren Benutzerprozesse
///
/// Fachlicher Hintergrund, den die UI auch sagen muss: "Freier RAM" ist unter Windows
/// kein Qualitätsmerkmal. Getrimmte Seiten landen im Standby/Pagefile und müssen beim
/// nächsten Zugriff nachgeladen werden - bei Spielen kann das kurzzeitig ruckeln.
/// Deshalb ist der intensive Modus bewusst ein zweiter, klar benannter Knopf.
///
/// Gemessen wird das echte Delta: freies RAM über GlobalMemoryStatusEx und die Summe
/// der gelösten Working-Set-Bytes der tatsächlich getrimmten Prozesse.
/// </summary>
public sealed class MemoryOptimizer
{
    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryPurgeStandbyList = 4;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryInformation = 0x0400;
    private const string ProfilePrivilege = "SeProfileSingleProcessPrivilege";
    private const long Mb = 1024L * 1024L;
    private const long Gb = 1024L * 1024L * 1024L;

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "explorer", "MsMpEng", "SearchIndexer",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm",
        "dwm", "fontdrvhost", "sihost", "ctfmon"
    };

    private sealed record ProcessCandidate(Process Process, long WorkingSetBytes);

    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public MemoryReleaseResult Release(bool intensive, double targetGb = 2.0)
    {
        targetGb = intensive ? Math.Clamp(targetGb, 0.25, 32.0) : 0;
        long availableBeforeBytes = GetAvailableBytes();
        double availableBefore = availableBeforeBytes / (double)Gb;
        bool elevated = IsElevated;
        bool standby = false;

        if (elevated && TryEnablePrivilege(ProfilePrivilege))
            standby = PurgeStandbyList();

        int trimmed = 0;
        long workingSetFreedBytes = 0;

        if (!intensive)
        {
            workingSetFreedBytes = TrimCurrentProcess();
            trimmed = workingSetFreedBytes > 0 ? 1 : 0;
        }

        if (intensive)
        {
            int foregroundProcessId = GetForegroundProcessId();
            var candidates = new List<ProcessCandidate>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id <= 4 || process.Id == Environment.ProcessId || process.Id == foregroundProcessId ||
                        Protected.Contains(process.ProcessName))
                    {
                        process.Dispose();
                        continue;
                    }
                    long workingSet = process.WorkingSet64;
                    if (workingSet < 8 * Mb) { process.Dispose(); continue; }
                    candidates.Add(new ProcessCandidate(process, workingSet));
                }
                catch { process.Dispose(); }
            }

            try
            {
                foreach (var candidate in candidates.OrderByDescending(x => x.WorkingSetBytes))
                {
                    try
                    {
                        IntPtr handle = OpenProcess(ProcessSetQuota | ProcessQueryInformation, false, candidate.Process.Id);
                        if (handle == IntPtr.Zero) continue;
                        try
                        {
                            if (!EmptyWorkingSet(handle)) continue;
                            candidate.Process.Refresh();
                            long after = candidate.Process.WorkingSet64;
                            if (candidate.WorkingSetBytes > after)
                            {
                                workingSetFreedBytes += candidate.WorkingSetBytes - after;
                                trimmed++;
                            }
                        }
                        finally { CloseHandle(handle); }
                    }
                    catch { /* Process exited or access denied: continue with other user processes. */ }

                    if (GetAvailableBytes() - availableBeforeBytes >= targetGb * Gb) break;
                    Thread.Sleep(35);
                }
            }
            finally
            {
                foreach (var candidate in candidates) candidate.Process.Dispose();
            }
        }

        Thread.Sleep(300);

        long availableAfterBytes = GetAvailableBytes();
        double availableAfter = availableAfterBytes / (double)Gb;
        double freedGb = Math.Max(0, availableAfter - availableBefore);
        bool targetReached = !intensive || availableAfterBytes - availableBeforeBytes >= targetGb * Gb;
        long workingSetFreedMb = workingSetFreedBytes / Mb;

        string message = intensive
            ? targetReached
                ? $"2-GB-Ziel erreicht: {freedGb:0.00} GB mehr verfügbar. {trimmed} Hintergrundprozesse getrimmt ({workingSetFreedMb} MB Working Set). Es wurden keine Apps geschlossen."
                : $"2-GB-Ziel nicht erreicht: {freedGb:0.00} GB mehr verfügbar; {workingSetFreedMb} MB Working Set aus {trimmed} Prozessen getrimmt. Windows ließ nicht genug weitere Hintergrundspeicherbereiche sicher frei. Es wurden keine Apps geschlossen."
            : $"Sanft abgeschlossen: {freedGb:0.00} GB mehr verfügbar. {workingSetFreedBytes / Mb} MB NexusOptimizer-Working-Set bereinigt; andere Programme blieben unberührt.";
        if (standby) message += " Standby-Cache wurde zusätzlich bereinigt.";

        return new MemoryReleaseResult(
            availableBefore, availableAfter, freedGb, targetGb, targetReached, workingSetFreedMb, trimmed, standby, elevated, message);
    }

    private static long GetAvailableBytes()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? checked((long)status.ullAvailPhys) : 0;
    }

    private static long TrimCurrentProcess()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            long before = process.WorkingSet64;
            if (!EmptyWorkingSet(process.Handle)) return 0;
            process.Refresh();
            return Math.Max(0, before - process.WorkingSet64);
        }
        catch { return 0; }
    }

    private static int GetForegroundProcessId()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out uint processId);
        return unchecked((int)processId);
    }

    private static bool PurgeStandbyList()
    {
        IntPtr command = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(command, MemoryPurgeStandbyList);
        try
        {
            return NtSetSystemInformation(SystemMemoryListInformation, command, sizeof(int)) == 0;
        }
        finally { Marshal.FreeHGlobal(command); }
    }

    private static bool TryEnablePrivilege(string privilege)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out IntPtr token))
            return false;

        try
        {
            if (!LookupPrivilegeValue(null, privilege, out LUID luid)) return false;

            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = 0x00000002 };
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("ntdll.dll")]
    private static extern uint NtSetSystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
