using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NexusOptimizer.Models;

namespace NexusOptimizer.Services;

public sealed record StartupChangeResult(bool Success, string Message);

/// <summary>Lists Windows startup entries and keeps reversible copies before disabling them.</summary>
public sealed class StartupManagerService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _manifestPath = Path.Combine(AppPaths.Root, "startup-disabled.json");

    private sealed record DisabledEntry(
        string Id, string Kind, string Hive, string View, string RegistryPath,
        string ValueName, string ValueKind, string Value, string OriginalPath, string BackupPath);

    public bool IsAdmin { get; } = CheckAdmin();

    public IReadOnlyList<StartupEntry> Scan()
    {
        var disabled = LoadManifest();
        var entries = new Dictionary<string, StartupEntry>(StringComparer.OrdinalIgnoreCase);
        var activeRegistryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRegistryValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ScanRegistry(entries, activeRegistryIds, seenRegistryValues, RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU", "64-bit", RunKey, "Run");
        ScanRegistry(entries, activeRegistryIds, seenRegistryValues, RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU", "32-bit", RunKey, "Run");
        ScanRegistry(entries, activeRegistryIds, seenRegistryValues, RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM", "64-bit", RunKey, "Run");
        ScanRegistry(entries, activeRegistryIds, seenRegistryValues, RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM", "32-bit", RunKey, "Run");
        ScanRegistry(entries, activeRegistryIds, seenRegistryValues, RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU", "64-bit", RunOnceKey, "RunOnce");
        ScanRegistry(entries, activeRegistryIds, seenRegistryValues, RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU", "32-bit", RunOnceKey, "RunOnce");
        ScanRegistry(entries, activeRegistryIds, seenRegistryValues, RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM", "64-bit", RunOnceKey, "RunOnce");
        ScanRegistry(entries, activeRegistryIds, seenRegistryValues, RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM", "32-bit", RunOnceKey, "RunOnce");

        ScanStartupFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Benutzer-Autostart");
        ScanStartupFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Autostart aller Benutzer");

        int staleSnapshots = disabled.RemoveAll(x => entries.ContainsKey(x.Id) || activeRegistryIds.Contains(x.Id));
        if (staleSnapshots > 0)
        {
            try { SaveManifest(disabled); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Veraltete Autostart-Sicherungen konnten nicht bereinigt werden"); }
        }

        foreach (var item in disabled)
        {
            entries[item.Id] = item.Kind == "file"
                ? CreateEntry(item.Id, Path.GetFileName(item.OriginalPath), item.OriginalPath,
                    "Autostart-Ordner (deaktiviert)", false, IsManageableFolder(item.OriginalPath), isFileEntry: true)
                : CreateEntry(item.Id, item.ValueName, Expand(item.Value),
                    $"{item.Hive} {item.RegistryPath} (deaktiviert)", false,
                    item.Hive == "HKCU" || IsAdmin, registryHive: item.Hive,
                    registryView: item.View, registryPath: item.RegistryPath, registryValue: item.Value);
        }

        return entries.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public StartupChangeResult SetEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            var snapshots = LoadManifest();
            var snapshot = snapshots.FirstOrDefault(x => string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase));

            if (enabled)
            {
                if (snapshot is null)
                    return new(false, "Die Sicherung wurde nicht gefunden. Der Eintrag blieb unverändert.");

                var restored = Restore(snapshot);
                if (!restored.Success) return restored;
                snapshots.RemoveAll(x => x.Id == snapshot.Id);
                SaveManifest(snapshots);
                return new(true, $"{entry.Name} wurde aktiviert und startet künftig mit Windows.");
            }

            if (snapshot is not null)
                return new(false, "Dieser Eintrag ist bereits deaktiviert.");

            if (entry.IsFileEntry)
            {
                var moved = DisableFile(entry);
                if (moved is null) return new(false, "Die Datei wurde nicht geändert; sie konnte nicht gesichert werden.");
                snapshots.Add(moved);
                try { SaveManifest(snapshots); }
                catch
                {
                    if (File.Exists(moved.BackupPath) && !File.Exists(moved.OriginalPath))
                        File.Move(moved.BackupPath, moved.OriginalPath);
                    throw;
                }
            }
            else
            {
                var saved = DisableRegistry(entry);
                if (!saved.Success || saved.Snapshot is null) return new(false, saved.Message);
            }

            return new(true, $"{entry.Name} wurde deaktiviert. Die Sicherung liegt lokal und kann hier wiederhergestellt werden.");
        }
        catch (UnauthorizedAccessException)
        {
            return new(false, "Windows verweigert den Zugriff. Starte NexusOptimizer als Administrator, falls dieser Systemeintrag geändert werden soll.");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Autostart-Änderung für {Entry} fehlgeschlagen", entry.Name);
            return new(false, "Änderung fehlgeschlagen: " + ex.Message);
        }
    }

    private void ScanRegistry(Dictionary<string, StartupEntry> entries, HashSet<string> activeIds,
        HashSet<string> seenValues, RegistryHive hive, RegistryView view, string hiveName, string viewName,
        string registryPath, string label)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(registryPath);
            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                try
                {
                    if (key.GetValueKind(name) is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                    string raw = key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? string.Empty;
                    string id = MakeId("registry", hiveName, viewName, registryPath, name);
                    activeIds.Add(id);
                    string signature = MakeId("registry-value", hiveName, registryPath, name, key.GetValueKind(name).ToString(), raw);
                    if (!seenValues.Add(signature)) continue;

                    bool manageable = hive == RegistryHive.CurrentUser || IsAdmin;
                    entries[id] = CreateEntry(id, name, Expand(raw),
                        $"{hiveName} · {viewName} · {label}", true, manageable,
                        registryHive: hiveName, registryView: view == RegistryView.Registry32 ? "Registry32" : "Registry64",
                        registryPath: registryPath, registryValue: raw);
                }
                catch (Exception ex) { Serilog.Log.Debug(ex, "Autostart-Registrywert übersprungen: {Name}", name); }
            }
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Autostart-Registry konnte nicht gelesen werden: {Hive}/{View}", hiveName, viewName); }
    }

    private void ScanStartupFolder(Dictionary<string, StartupEntry> entries, string folder, string label)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsStartupFile(path)) continue;
                string id = MakeId("file", Path.GetFullPath(path));
                if (entries.ContainsKey(id)) continue;

                entries[id] = CreateEntry(id, Path.GetFileNameWithoutExtension(path), path, label, true,
                    label == "Benutzer-Autostart" || IsAdmin, isFileEntry: true);
            }
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Autostart-Ordner konnte nicht gelesen werden: {Folder}", folder); }
    }

    private (bool Success, string Message, DisabledEntry? Snapshot) DisableRegistry(StartupEntry entry)
    {
        string hiveName = entry.RegistryHive;
        string viewName = entry.RegistryView;
        RegistryHive hive = hiveName == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        RegistryView view = viewName == "Registry32" ? RegistryView.Registry32 : RegistryView.Registry64;

        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(entry.RegistryPath, writable: true);
        if (key is null || !key.GetValueNames().Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            return (false, "Der Registry-Eintrag wurde inzwischen geändert. Bitte aktualisiere die Liste.", null);

        RegistryValueKind kind = key.GetValueKind(entry.Name);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            return (false, "Dieser Registry-Typ wird aus Sicherheitsgründen nicht verändert.", null);

        string value = key.GetValue(entry.Name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? string.Empty;
        if (!string.Equals(value, entry.RegistryValue, StringComparison.Ordinal))
            return (false, "Der Registry-Eintrag wurde seit dem Einlesen geändert. Bitte aktualisiere die Liste.", null);

        string id = MakeId("registry", hiveName, viewName == "Registry32" ? "32-bit" : "64-bit", entry.RegistryPath, entry.Name);
        var snapshot = new DisabledEntry(id, "registry", hiveName, viewName, entry.RegistryPath, entry.Name, kind.ToString(), value, string.Empty, string.Empty);
        var manifest = LoadManifest();
        manifest.RemoveAll(x => x.Id == id);
        manifest.Add(snapshot);
        SaveManifest(manifest);
        try { key.DeleteValue(entry.Name, throwOnMissingValue: false); }
        catch
        {
            manifest.RemoveAll(x => x.Id == snapshot.Id);
            SaveManifest(manifest);
            throw;
        }
        return (true, string.Empty, snapshot);
    }

    private DisabledEntry? DisableFile(StartupEntry entry)
    {
        if (!File.Exists(entry.Command)) return null;
        string original = Path.GetFullPath(entry.Command);
        string originalFolder = Path.GetDirectoryName(original)!;
        string backupFolder = Path.Combine(originalFolder, ".NexusOptimizerDisabled");
        Directory.CreateDirectory(backupFolder);
        try { File.SetAttributes(backupFolder, File.GetAttributes(backupFolder) | FileAttributes.Hidden); } catch { }

        string backup = Path.Combine(backupFolder, Path.GetFileName(original));
        if (File.Exists(backup)) backup = Path.Combine(backupFolder, Guid.NewGuid().ToString("N") + Path.GetExtension(original));
        File.Move(original, backup);
        return new DisabledEntry(MakeId("file", original), "file", string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, original, backup);
    }

    private StartupChangeResult Restore(DisabledEntry snapshot)
    {
        if (snapshot.Kind == "file")
        {
            if (!File.Exists(snapshot.BackupPath)) return new(false, "Die gesicherte Datei fehlt. Sie wurde nicht überschrieben.");
            if (File.Exists(snapshot.OriginalPath)) return new(false, "Am ursprünglichen Ort liegt bereits eine Datei. Verschiebe sie zuerst manuell.");
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot.OriginalPath)!);
            File.Move(snapshot.BackupPath, snapshot.OriginalPath);
            return new(true, string.Empty);
        }

        return RestoreRegistry(snapshot)
            ? new(true, string.Empty)
            : new(false, "Der Registry-Eintrag konnte nicht wiederhergestellt werden. Die Sicherung bleibt erhalten.");
    }

    private static bool RestoreRegistry(DisabledEntry snapshot)
    {
        if (!Enum.TryParse(snapshot.ValueKind, out RegistryValueKind valueKind)) return false;
        RegistryHive hive = snapshot.Hive == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        RegistryView view = snapshot.View == "Registry32" ? RegistryView.Registry32 : RegistryView.Registry64;
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.CreateSubKey(snapshot.RegistryPath, writable: true);
        if (key is null || key.GetValueNames().Contains(snapshot.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
        key.SetValue(snapshot.ValueName, snapshot.Value, valueKind);
        return true;
    }

    private static StartupEntry CreateEntry(string id, string name, string command, string location, bool enabled, bool canManage,
        string registryHive = "", string registryView = "", string registryPath = "", string registryValue = "", bool isFileEntry = false)
    {
        string availability = canManage ? "Änderungen werden vor dem Speichern gesichert." : "Nur lesbar; zum Ändern sind Administratorrechte erforderlich.";
        string displayCommand = command;
        try
        {
            string? exe = GetExecutablePath(command);
            if (exe is not null && File.Exists(exe))
            {
                var info = FileVersionInfo.GetVersionInfo(exe!);
                if (!string.IsNullOrWhiteSpace(info.CompanyName)) availability = canManage
                    ? $"{info.CompanyName} · Änderungen werden gesichert."
                    : $"{info.CompanyName} · Nur lesbar; Administratorrechte erforderlich.";
            }
        }
        catch { }

        return new StartupEntry { Id = id, Name = name, Command = displayCommand, Location = location,
            IsEnabled = enabled, CanManage = canManage, Availability = availability,
            RegistryHive = registryHive, RegistryView = registryView, RegistryPath = registryPath,
            RegistryValue = registryValue, IsFileEntry = isFileEntry };
    }

    private List<DisabledEntry> LoadManifest()
    {
        if (!File.Exists(_manifestPath)) return new();
        try { return JsonSerializer.Deserialize<List<DisabledEntry>>(File.ReadAllText(_manifestPath)) ?? new(); }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Autostart-Sicherungen konnten nicht gelesen werden");
            return new();
        }
    }

    private void SaveManifest(List<DisabledEntry> entries)
    {
        Directory.CreateDirectory(AppPaths.Root);
        string temporary = _manifestPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, _manifestPath, overwrite: true);
    }

    private bool IsManageableFolder(string path)
    {
        string userFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return path.StartsWith(userFolder, StringComparison.OrdinalIgnoreCase) || IsAdmin;
    }

    private static bool IsStartupFile(string path) =>
        new[] { ".lnk", ".url", ".exe", ".bat", ".cmd", ".ps1" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string? GetExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string candidate = command.Trim();
        if (candidate.StartsWith('"'))
        {
            int closingQuote = candidate.IndexOf('"', 1);
            if (closingQuote > 1) candidate = candidate[1..closingQuote];
        }
        else
        {
            int exeEnd = candidate.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd >= 0) candidate = candidate[..(exeEnd + 4)];
        }
        candidate = Environment.ExpandEnvironmentVariables(candidate.Trim());
        return Path.IsPathRooted(candidate) ? candidate : null;
    }

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value);
    private static string MakeId(params string[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))))[..24];

    private static bool CheckAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
