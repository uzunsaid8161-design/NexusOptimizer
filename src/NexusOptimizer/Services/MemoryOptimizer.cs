using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace NexusOptimizer.Services;

public sealed record MemoryReleaseResult(
    double AvailableBeforeGb,
    double AvailableAfterGb,
    double FreedGb,
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
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const string ProfilePrivilege = "SeProfileSingleProcessPrivilege";

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm",
        "dwm", "fontdrvhost", "sihost", "ctfmon"
    };

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

    public MemoryReleaseResult Release(bool intensive)
    {
        double availableBefore = GetAvailableGb();
        bool elevated = IsElevated;
        bool standby = false;

        if (elevated && TryEnablePrivilege(ProfilePrivilege))
            standby = PurgeStandbyList();

        int trimmed = 0;
        long workingSetFreedBytes = 0;

        TrimCurrentProcess();

        if (intensive)
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id <= 4 || Protected.Contains(process.ProcessName)) continue;
                        if (process.Id == Environment.ProcessId) continue;

                        long before = process.WorkingSet64;
                        if (!EmptyWorkingSet(process.Handle)) continue;

                        process.Refresh();
                        long after = process.WorkingSet64;
                        if (before > after)
                        {
                            workingSetFreedBytes += before - after;
                            trimmed++;
                        }
                    }
                    catch
                    {
                        // Zugriff verweigert (Systemprozess, andere Session): überspringen.
                    }
                }
            }
        }

        Thread.Sleep(400);

        double availableAfter = GetAvailableGb();
        double freedGb = Math.Max(0, availableAfter - availableBefore);
        long workingSetFreedMb = workingSetFreedBytes / (1024 * 1024);

        string message = intensive
            ? $"Intensiv: {trimmed} Prozesse getrimmt, {workingSetFreedMb} MB Working Set gelöst, RAM frei {availableBefore:0.0} → {availableAfter:0.0} GB." +
              (standby ? " Standby-Liste geleert." : elevated ? " Standby-Bereinigung abgelehnt." : " Standby-Liste nur mit Adminrechten.")
            : $"Sanft: eigener Prozess getrimmt, RAM frei {availableBefore:0.0} → {availableAfter:0.0} GB." +
              (standby ? " Standby-Liste geleert." : " Standby-Liste nur mit Adminrechten – dafür „Intensiv“ oder App als Administrator starten.");

        return new MemoryReleaseResult(
            availableBefore, availableAfter, freedGb, workingSetFreedMb, trimmed, standby, elevated, message);
    }

    private static void TrimCurrentProcess()
    {
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
    }

    private static double GetAvailableGb()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return 0;
        return status.ullAvailPhys / 1073741824.0;
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
            if (!LookupPrivilegeValue(null, privilege, out long luid)) return false;

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
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    private static extern uint NtSetSystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
