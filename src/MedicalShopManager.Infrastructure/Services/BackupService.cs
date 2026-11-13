using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Services;

/// <summary>
/// Backup and restore service using SQLite VACUUM INTO snapshots and zip archives with integrity verification.
/// </summary>
public class BackupService : IBackupService
{
    private readonly DatabaseManager _db;
    private readonly IAuditService _auditService;
    private readonly ILogger<BackupService> _logger;
    private readonly string _backupDirectory;

    public string BackupDirectory => _backupDirectory;

    public BackupService(
        DatabaseManager db,
        IAuditService auditService,
        ILogger<BackupService> logger,
        string? backupDirectory = null)
    {
        _db = db;
        _auditService = auditService;
        _logger = logger;

        _backupDirectory = !string.IsNullOrWhiteSpace(backupDirectory)
            ? backupDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MedicalShopManager", "Backups");
        Directory.CreateDirectory(_backupDirectory);
    }

    public async Task<string> CreateBackupAsync(string? targetDirectory = null)
    {
        var outputDir = !string.IsNullOrWhiteSpace(targetDirectory) ? targetDirectory : _backupDirectory;
        Directory.CreateDirectory(outputDir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var tempDbFile = Path.Combine(outputDir, $"MedicalShop_backup_temp_{timestamp}.db");
        var zipFile = Path.Combine(outputDir, $"MedicalShop_Backup_{timestamp}.zip");

        _logger.LogInformation("Creating online SQLite backup snapshot...");

        // Use SQLite's VACUUM INTO for online, consistent point-in-time snapshot
        using (var conn = _db.CreateNewConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM INTO @Target;";
            cmd.Parameters.AddWithValue("@Target", tempDbFile);
            await cmd.ExecuteNonQueryAsync();
        }

        // Verify temp DB integrity before archiving
        var isValid = await VerifyDatabaseFileIntegrityAsync(tempDbFile);
        if (!isValid)
        {
            File.Delete(tempDbFile);
            throw new InvalidOperationException("Backup failed integrity check before compression.");
        }

        // Compress into ZIP archive
        using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(tempDbFile, "MedicalShop.db", CompressionLevel.Optimal);
        }

        // Clean up temporary uncompressed DB file
        File.Delete(tempDbFile);

        await _auditService.LogAsync("CreateBackup", "System", null, null,
            $"{{\"File\":\"{Path.GetFileName(zipFile)}\",\"Path\":\"{zipFile}\"}}", "Manual or automated backup created");

        _logger.LogInformation("Backup created successfully: {Path}", zipFile);
        return zipFile;
    }

    public async Task RestoreBackupAsync(string backupPath)
    {
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("Backup archive not found", backupPath);

        _logger.LogWarning("Restoring database from backup: {BackupPath}", backupPath);

        var tempDir = Path.Combine(Path.GetTempPath(), $"MSM_Restore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var extractedDb = Path.Combine(tempDir, "MedicalShop.db");
            if (backupPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(backupPath, tempDir);
            }
            else
            {
                File.Copy(backupPath, extractedDb, overwrite: true);
            }

            if (!File.Exists(extractedDb))
                throw new InvalidOperationException("Backup archive did not contain a valid MedicalShop.db database.");

            var isValid = await VerifyDatabaseFileIntegrityAsync(extractedDb);
            if (!isValid)
                throw new InvalidOperationException("The backup database failed SQLite integrity check.");

            await EnsureBackupSchemaIsKnownAsync(extractedDb);

            // Pre-restore safety copy through SQLite, so commits still in the -wal file are included.
            var safetyCopy = $"{_db.DatabasePath}.prerestore.{DateTime.Now:yyyyMMdd_HHmmss}";
            using (var live = _db.CreateNewConnection())
            {
                using var cmd = live.CreateCommand();
                cmd.CommandText = "VACUUM INTO @Target;";
                cmd.Parameters.AddWithValue("@Target", safetyCopy);
                await cmd.ExecuteNonQueryAsync();
            }

            // Copy the backup's pages into the live database with SQLite's online backup API instead of overwriting
            // the file: a file copy under an open WAL database lets SQLite replay the old -wal onto the restored
            // file. The backup step is atomic, so a failure leaves the current data untouched.
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = extractedDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                source.Open();
                using var live = _db.CreateNewConnection();
                source.BackupDatabase(live);
            }

            // A backup taken by an older version is upgraded to the current schema straight away.
            await _db.InitializeAsync();

            await _auditService.LogAsync("RestoreBackup", "System", null, null,
                JsonSerializer.Serialize(new { RestoredFrom = backupPath, SafetyCopy = safetyCopy }), "Database restored from backup");

            _logger.LogInformation("Database restored successfully from {Path}", backupPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    public Task<IReadOnlyList<string>> GetAvailableBackupsAsync()
    {
        if (!Directory.Exists(_backupDirectory))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var files = Directory.GetFiles(_backupDirectory, "MedicalShop_Backup_*.zip")
            .OrderByDescending(f => File.GetCreationTimeUtc(f))
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public Task CleanOldBackupsAsync(int retentionDays = 30)
    {
        if (!Directory.Exists(_backupDirectory))
            return Task.CompletedTask;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var files = Directory.GetFiles(_backupDirectory, "MedicalShop_Backup_*.zip");

        foreach (var file in files)
        {
            var creation = File.GetCreationTimeUtc(file);
            if (creation < cutoff)
            {
                try
                {
                    File.Delete(file);
                    _logger.LogInformation("Deleted old backup: {File}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete old backup: {File}", file);
                }
            }
        }

        return Task.CompletedTask;
    }

    public async Task<bool> VerifyBackupIntegrityAsync(string backupPath)
    {
        if (!File.Exists(backupPath))
            return false;

        var tempDir = Path.Combine(Path.GetTempPath(), $"MSM_Verify_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var targetDb = Path.Combine(tempDir, "MedicalShop.db");
            if (backupPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(backupPath, tempDir);
            }
            else
            {
                File.Copy(backupPath, targetDb, overwrite: true);
            }

            if (!File.Exists(targetDb))
                return false;

            return await VerifyDatabaseFileIntegrityAsync(targetDb);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify backup integrity: {Path}", backupPath);
            return false;
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Refuses a backup made by a newer build: it may record migrations this build does not know, and running on
    /// that schema would misread or corrupt data.
    /// </summary>
    private async Task EnsureBackupSchemaIsKnownAsync(string dbFilePath)
    {
        var known = await _db.GetAppliedVersionsAsync(); // the live database has every migration of this build
        var cs = new SqliteConnectionStringBuilder { DataSource = dbFilePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Version FROM SchemaMigrations;";
        var unknown = new List<int>();
        try
        {
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var version = reader.GetInt32(0);
                if (!known.Contains(version)) unknown.Add(version);
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException("The file is not a Medical Shop Manager database (no schema history).", ex);
        }

        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"This backup was made by a newer version of Medical Shop Manager (schema v{unknown.Max()}). Update the app before restoring it.");
    }

    private static async Task<bool> VerifyDatabaseFileIntegrityAsync(string dbFilePath)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbFilePath,
                Mode = SqliteOpenMode.ReadOnly,
                // Windows keeps a pooled connection's file locked, which breaks the temp-file delete after backup.
                Pooling = false
            }.ToString();

            using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = await cmd.ExecuteScalarAsync();

            return result?.ToString()?.Equals("ok", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }
}
