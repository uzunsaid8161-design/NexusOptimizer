using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace NexusOptimizer.Services;

public sealed record TempFileCandidate(string Path, long SizeBytes, DateTime LastWriteUtc);
public sealed record TempCleanupResult(int MovedToRecycleBin, long BytesMoved, int Skipped, string Message);

public sealed class CleanerService
{
    private static readonly TimeSpan MinimumAge = TimeSpan.FromDays(7);
    private const int MaxFiles = 10_000;

    public string TempPath => Path.GetTempPath();

    public IReadOnlyList<TempFileCandidate> Scan()
    {
        var result = new List<TempFileCandidate>();
        var folders = new Stack<string>();
        var root = TempPath;
        if (Directory.Exists(root)) folders.Push(root);
        DateTime cutoff = DateTime.UtcNow - MinimumAge;

        while (folders.Count > 0 && result.Count < MaxFiles)
        {
            string folder = folders.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(folder).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (string path in entries)
            {
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        folders.Push(path);
                        continue;
                    }

                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc <= cutoff)
                        result.Add(new TempFileCandidate(info.FullName, info.Length, info.LastWriteTimeUtc));
                    if (result.Count >= MaxFiles) break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }

        return result.OrderByDescending(x => x.SizeBytes).ToList();
    }

    public TempCleanupResult MoveToRecycleBin(IReadOnlyList<TempFileCandidate> candidates)
    {
        int moved = 0;
        int skipped = 0;
        long bytes = 0;
        DateTime cutoff = DateTime.UtcNow - MinimumAge;

        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate.Path) || File.GetLastWriteTimeUtc(candidate.Path) > cutoff)
                {
                    skipped++;
                    continue;
                }

                long length = new FileInfo(candidate.Path).Length;
                FileSystem.DeleteFile(candidate.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                moved++;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                skipped++;
            }
        }

        string message = $"{moved} Dateien in den Papierkorb verschoben · {FormatBytes(bytes)}" +
                         (skipped > 0 ? $" · {skipped} belegt oder nicht zugänglich übersprungen" : string.Empty);
        return new TempCleanupResult(moved, bytes, skipped, message);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = Math.Max(0, bytes);
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.0} {units[unit]}";
    }
}
