using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Inventory and batch management service.
/// </summary>
public interface IInventoryService
{
    Task<IReadOnlyList<MedicineBatch>> GetBatchesAsync(long medicineId);
    Task<IReadOnlyList<MedicineBatch>> GetSellableBatchesFefoAsync(long medicineId);
    Task<IReadOnlyList<MedicineBatch>> GetNearExpiryBatchesAsync(int daysThreshold = 90);
    Task<IReadOnlyList<MedicineBatch>> GetExpiredBatchesAsync();
    
    /// <summary>
    /// Allocates stock from batches using FEFO. Returns allocated batch/quantity pairs.
    /// Throws if insufficient stock.
    /// </summary>
    Task<List<(MedicineBatch Batch, int Quantity)>> AllocateStockFefoAsync(long medicineId, int quantityNeeded);
    
    Task AdjustStockAsync(long batchId, int newQuantity, string reason, long userId);
    Task WriteOffStockAsync(long batchId, int quantity, MovementType type, string reason, long userId);
    Task RecordMovementAsync(StockMovement movement);
    Task<IReadOnlyList<StockMovement>> GetMovementsAsync(long medicineId);
}
