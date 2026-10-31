using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace NexusOptimizer.Services;

public sealed record MemoryReleaseResult(
    double AvailableBeforeGb,
    double AvailableAfterGb,
    double FreedGb,
    bool StandbyPurged,
    bool Elevated,
    string Message);

/// <summary>
/// Gibt Arbeitsspeicher frei - so weit es ohne Kernel-Treiber und ohne Tricks möglich ist.
///
/// Was real passiert:
///  1. Working Set des eigenen Prozesses trimmen (EmptyWorkingSet) - immer erlaubt.
///  2. Standby-Liste leeren (NtSetSystemInformation / MemoryPurgeStandbyList) - dafür
///     braucht es SeProfileSingleProcessPrivilege, also Administratorrechte.
///
/// Was bewusst NICHT passiert: Prozesse anderer Anwendungen werden nicht angefasst,
/// es wird kein Speicher "reserviert" und keine Zahl geschätzt. Der Erfolg wird über
/// GlobalMemoryStatusEx vor/nach gemessen und ehrlich als Delta ausgegeben.
/// </summary>
public sealed class MemoryOptimizer
{
    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryPurgeStandbyList = 4;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const string ProfilePrivilege = "SeProfileSingleProcessPrivilege";

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

    public MemoryReleaseResult Release()
    {
        double before = GetAvailableGb();
        bool elevated = IsElevated;
        bool standby = false;

        try
        {
            if (elevated && TryEnablePrivilege(ProfilePrivilege))
                standby = PurgeStandbyList();
        }
        catch { /* Privileg fehlt: kein Grund abzubrechen */ }

        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }

        Thread.Sleep(350);
        double after = GetAvailableGb();
        double freed = Math.Max(0, after - before);

        string message = standby
            ? $"Standby-Liste geleert, {freed:0.00} GB zusätzlich verfügbar."
            : elevated
                ? $"Working Set getrimmt, {freed:0.00} GB verfügbar. Standby-Bereinigung wurde vom System abgelehnt."
                : $"Nur eigener Prozess getrimmt ({freed:0.00} GB). Für die Standby-Liste sind Adminrechte nötig.";

        return new MemoryReleaseResult(before, after, freed, standby, elevated, message);
    }

    private static double GetAvailableGb()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return 0;
        return status.ullAvailPhys / 1073741824.0;
    }

    private static bool PurgeStandbyList()
    {
        try
        {
            IntPtr command = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(command, MemoryPurgeStandbyList);
            try
            {
                uint status = NtSetSystemInformation(SystemMemoryListInformation, command, sizeof(int));
                return status == 0;
            }
            finally { Marshal.FreeHGlobal(command); }
        }
        catch { return false; }
    }

    private static bool TryEnablePrivilege(string privilege)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out IntPtr token))
            return false;

        try
        {
            if (!LookupPrivilegeValue(null, privilege, out long luid)) return false;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = 0x00000002 // SE_PRIVILEGE_ENABLED
            };

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
