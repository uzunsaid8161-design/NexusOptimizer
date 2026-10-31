using System.IO;
using Microsoft.Data.Sqlite;

namespace NexusOptimizer.Services;

public sealed class BackupService
{
    public string DatabasePath => AppPaths.DatabaseFile;
    public string BackupDirectory => Path.Combine(AppPaths.Root, "backups");

    public string CreateDatabaseBackup(string destinationPath)
    {
        string target = Path.GetFullPath(destinationPath);
        if (string.Equals(target, Path.GetFullPath(DatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der Sicherungspfad darf nicht die aktive Datenbank sein.");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) throw new IOException("Die Zieldatei existiert bereits.");

        using var source = Open(DatabasePath, SqliteOpenMode.ReadOnly);
        using var destination = Open(target, SqliteOpenMode.ReadWriteCreate);
        ValidateDatabase(source);
        source.BackupDatabase(destination);
        return target;
    }

    public string RestoreDatabaseBackup(string backupPath)
    {
        string sourcePath = Path.GetFullPath(backupPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Die Sicherungsdatei wurde nicht gefunden.", sourcePath);
        if (string.Equals(sourcePath, Path.GetFullPath(DatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die aktive Datenbank kann nicht als Sicherung wiederhergestellt werden.");

        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(AppPaths.Root);
        string safetyCopy = Path.Combine(BackupDirectory, $"vor-wiederherstellung-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        string stagingPath = DatabasePath + ".restore-" + Guid.NewGuid().ToString("N");

        try
        {
            using (var source = Open(sourcePath, SqliteOpenMode.ReadOnly))
            {
                ValidateDatabase(source);
                using var staging = Open(stagingPath, SqliteOpenMode.ReadWriteCreate);
                source.BackupDatabase(staging);
            }

            using (var current = Open(DatabasePath, SqliteOpenMode.ReadWrite))
            {
                using var checkpoint = current.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }

            File.Copy(DatabasePath, safetyCopy, overwrite: false);
            SqliteConnection.ClearAllPools();
            File.Replace(stagingPath, DatabasePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return safetyCopy;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void ValidateDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Settings', 'Sessions', 'FpsSamples', 'Optimizations')";
        if (Convert.ToInt32(command.ExecuteScalar()) != 4)
            throw new InvalidDataException("Die Datei enthält kein vollständiges NexusOptimizer-Datenbankschema.");
    }
}
