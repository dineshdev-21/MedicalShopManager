using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IStockMovementRepository : IRepository<StockMovement>
{
    Task<IReadOnlyList<StockMovement>> GetByMedicineAsync(long medicineId);
    Task<IReadOnlyList<StockMovement>> GetByBatchAsync(long batchId);
    Task<IReadOnlyList<StockMovement>> GetByDateRangeAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<StockMovement>> GetByTypeAsync(MovementType type);
}
