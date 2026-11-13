using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface ISaleItemRepository
{
    Task<IReadOnlyList<SaleItem>> GetBySaleIdAsync(long saleId);
    Task InsertAsync(SaleItem item);
    Task InsertRangeAsync(IEnumerable<SaleItem> items);
}
