using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;

namespace MedicalShopManager.App.ViewModels;

/// <summary>
/// Excel data management ViewModel for triggering manual sync, rebuilding workbooks,
/// and viewing live synchronization queue metrics.
/// </summary>
public partial class ExcelManagementViewModel : BaseViewModel
{
    private readonly IExcelSyncService _excelSyncService;

    [ObservableProperty] private string _workbookPath = string.Empty;
    [ObservableProperty] private string _lastSyncTime = "Never";
    [ObservableProperty] private string _syncStatusText = "Synchronized";
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private bool _isWorkbookLocked;

    public ExcelManagementViewModel(IExcelSyncService excelSyncService)
    {
        _excelSyncService = excelSyncService;
        _excelSyncService.SyncStatusChanged += OnSyncStatusChanged;
    }

    private void OnSyncStatusChanged(object? sender, SyncStatusChangedEventArgs e)
    {
        PendingCount = e.PendingCount;
        SyncStatusText = e.Message ?? e.Status.ToString();
        LastSyncTime = e.LastSyncTime?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "Never";
        IsWorkbookLocked = e.Status == SyncStatus.Locked;
    }

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            WorkbookPath = _excelSyncService.WorkbookPath;
            PendingCount = _excelSyncService.PendingCount;
            IsSyncing = _excelSyncService.IsSyncing;
            LastSyncTime = _excelSyncService.LastSyncTime?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "Never";
            IsWorkbookLocked = await _excelSyncService.IsWorkbookLockedAsync();

            if (IsWorkbookLocked)
            {
                SyncStatusText = "Workbook is currently open in Excel (Locked)";
            }
            else if (PendingCount > 0)
            {
                SyncStatusText = $"{PendingCount} changes pending synchronization";
            }
            else
            {
                SyncStatusText = "All data synchronized with Excel";
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to load Excel sync status: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            await _excelSyncService.ProcessQueueAsync();
            SetStatus("Synchronization completed successfully.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetError($"Sync failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RebuildWorkbookAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            await _excelSyncService.RebuildWorkbookAsync();
            SetStatus("Workbook completely rebuilt from SQLite database with all 14 worksheets.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetError($"Rebuild failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
