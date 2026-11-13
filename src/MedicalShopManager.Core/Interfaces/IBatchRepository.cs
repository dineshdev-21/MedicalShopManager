using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IBatchRepository : IRepository<MedicineBatch>
{
    Task<IReadOnlyList<MedicineBatch>> GetByMedicineIdAsync(long medicineId);
    Task<IReadOnlyList<MedicineBatch>> GetSellableBatchesAsync(long medicineId);
    Task<IReadOnlyList<MedicineBatch>> GetNearExpiryAsync(int daysThreshold = 90);
    Task<IReadOnlyList<MedicineBatch>> GetExpiredAsync();
    Task<MedicineBatch?> GetByBatchNumberAsync(long medicineId, string batchNumber);
    Task<bool> UpdateQuantityAsync(long batchId, int newQuantity);
    
    /// <summary>
    /// Gets sellable batches ordered by expiry date (FEFO).
    /// </summary>
    Task<IReadOnlyList<MedicineBatch>> GetFefoOrderedBatchesAsync(long medicineId);
}
