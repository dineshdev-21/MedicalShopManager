using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IPurchaseRepository : IRepository<Purchase>
{
    Task<Purchase?> GetWithItemsAsync(long id);
    Task<IReadOnlyList<Purchase>> GetBySupplierAsync(long supplierId);
    Task<IReadOnlyList<Purchase>> GetByDateRangeAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<Purchase>> GetByStatusAsync(PurchaseStatus status);
    Task<long> InsertWithItemsAsync(Purchase purchase);
    Task<bool> UpdateStatusAsync(long purchaseId, PurchaseStatus status);
}
