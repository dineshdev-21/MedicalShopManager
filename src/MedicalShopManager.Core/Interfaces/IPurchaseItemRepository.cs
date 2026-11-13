using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IPurchaseItemRepository
{
    Task<IReadOnlyList<PurchaseItem>> GetByPurchaseIdAsync(long purchaseId);
    Task InsertAsync(PurchaseItem item);
    Task InsertRangeAsync(IEnumerable<PurchaseItem> items);
}
