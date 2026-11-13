using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IExcelSyncQueueRepository
{
    Task EnqueueAsync(ExcelSyncQueueItem item);
    Task EnqueueAsync(SyncEntityType entityType, long? EntityId = null);
    Task<IReadOnlyList<ExcelSyncQueueItem>> GetPendingAsync(int limit = 100);
    Task MarkCompletedAsync(long id);
    Task MarkFailedAsync(long id, string errorMessage);
    Task<int> GetPendingCountAsync();
    Task<DateTime?> GetLastSuccessfulSyncTimeAsync();
}
