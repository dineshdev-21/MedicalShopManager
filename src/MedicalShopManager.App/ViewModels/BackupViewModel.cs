using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.App.ViewModels;

/// <summary>Backup and restore management ViewModel.</summary>
public partial class BackupViewModel : BaseViewModel
{
    private readonly IBackupService _backupService;

    [ObservableProperty] private ObservableCollection<string> _availableBackups = new();
    [ObservableProperty] private string? _selectedBackup;
    [ObservableProperty] private string _backupDirectory = string.Empty;

    public BackupViewModel(IBackupService backupService)
    {
        _backupService = backupService;
    }

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            BackupDirectory = _backupService.BackupDirectory;
            var files = await _backupService.GetAvailableBackupsAsync();
            AvailableBackups.Clear();
            foreach (var f in files)
                AvailableBackups.Add(f);
        }
        catch (Exception ex)
        {
            SetError($"Failed to load backups: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            var zipPath = await _backupService.CreateBackupAsync();
            SetStatus($"Backup created successfully: {System.IO.Path.GetFileName(zipPath)}");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetError($"Backup creation failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync(string? backupPath)
    {
        var target = backupPath ?? SelectedBackup;
        if (string.IsNullOrEmpty(target))
        {
            SetError("Please select a backup archive to restore.");
            return;
        }

        IsLoading = true;
        ClearStatus();
        try
        {
            await _backupService.RestoreBackupAsync(target);
            SetStatus($"Database restored from {System.IO.Path.GetFileName(target)}. Restarting components recommended.");
        }
        catch (Exception ex)
        {
            SetError($"Restore failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
