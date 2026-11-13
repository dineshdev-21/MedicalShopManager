using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Backup and restore service.
/// </summary>
public interface IBackupService
{
    Task<string> CreateBackupAsync(string? targetDirectory = null);
    Task RestoreBackupAsync(string backupPath);
    Task<IReadOnlyList<string>> GetAvailableBackupsAsync();
    Task CleanOldBackupsAsync(int retentionDays = 30);
    Task<bool> VerifyBackupIntegrityAsync(string backupPath);
    string BackupDirectory { get; }
}
