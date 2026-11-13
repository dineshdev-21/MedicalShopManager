using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Medicine management service.
/// </summary>
public interface IMedicineService
{
    Task<Medicine?> GetByIdAsync(long id);
    Task<Medicine?> GetWithBatchesAsync(long id);
    Task<IReadOnlyList<Medicine>> SearchAsync(string query);
    Task<Medicine?> GetByBarcodeAsync(string barcode);
    Task<IReadOnlyList<Medicine>> GetAllAsync();
    Task<IReadOnlyList<Medicine>> GetLowStockAsync();
    Task<IReadOnlyList<string>> GetCategoriesAsync();
    Task<long> CreateAsync(Medicine medicine);
    Task<bool> UpdateAsync(Medicine medicine);
    Task<bool> DeactivateAsync(long id);
}
