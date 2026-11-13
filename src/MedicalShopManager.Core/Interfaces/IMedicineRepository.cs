using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IMedicineRepository : IRepository<Medicine>
{
    Task<IReadOnlyList<Medicine>> SearchAsync(string query);
    Task<Medicine?> GetByBarcodeAsync(string barcode);
    Task<IReadOnlyList<Medicine>> GetLowStockAsync();
    Task<IReadOnlyList<Medicine>> GetByStatusAsync(MedicineStatus status);
    Task<IReadOnlyList<Medicine>> GetByCategoryAsync(string category);
    Task<IReadOnlyList<string>> GetCategoriesAsync();
    Task<Medicine?> GetWithBatchesAsync(long id);
}
