using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Excel workbook synchronization service.
/// </summary>
public interface IExcelSyncService
{
    /// <summary>Full path to the current workbook.</summary>
    string WorkbookPath { get; }
    
    /// <summary>Last time the workbook was successfully synchronized.</summary>
    DateTime? LastSyncTime { get; }
    
    /// <summary>Number of pending sync operations.</summary>
    int PendingCount { get; }
    
    /// <summary>Whether a sync operation is currently in progress.</summary>
    bool IsSyncing { get; }
    
    Task InitializeWorkbookAsync();
    Task SyncAllAsync(CancellationToken ct = default);
    Task SyncEntityAsync(SyncEntityType entityType, long? entityId = null, CancellationToken ct = default);
    Task EnqueueSyncAsync(SyncEntityType entityType, long? entityId = null);
    Task ProcessQueueAsync(CancellationToken ct = default);
    Task RebuildWorkbookAsync(CancellationToken ct = default);
    Task<bool> IsWorkbookLockedAsync();
    Task ChangeWorkbookPathAsync(string newPath);
    
    event EventHandler<SyncStatusChangedEventArgs>? SyncStatusChanged;
}

public class SyncStatusChangedEventArgs : EventArgs
{
    public SyncStatus Status { get; init; }
    public string? Message { get; init; }
    public int PendingCount { get; init; }
    public DateTime? LastSyncTime { get; init; }
}
