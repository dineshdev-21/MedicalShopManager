using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Modules.Stock;

/// <summary>Read side of the Stock workspace plus item settings and bulk updates (parameterised SQL only).</summary>
public interface IStockRepository
{
    /// <summary>Every item with all of its batches (active and inactive batches; inactive items only when asked).</summary>
    Task<IReadOnlyList<StockItemSnapshot>> GetSnapshotsAsync(bool includeInactive);

    Task<StockItemSnapshot?> GetSnapshotAsync(long medicineId);

    /// <summary>Active items sharing the composition of <paramref name="medicineId"/> (excluding it).</summary>
    Task<IReadOnlyList<StockItemSnapshot>> GetSubstituteSnapshotsAsync(long medicineId);

    Task<IReadOnlyList<string>> GetManufacturersAsync();
    Task<IReadOnlyList<ItemPurchaseRow>> GetPurchasesAsync(long medicineId, int limit);
    Task<IReadOnlyList<ItemSaleRow>> GetSalesAsync(long medicineId, int limit);

    /// <summary>All stock movements of the item, oldest first.</summary>
    Task<IReadOnlyList<StockMovement>> GetMovementsAsync(long medicineId);

    Task<IReadOnlyList<ItemDistributorRow>> GetDistributorsAsync(long medicineId);

    /// <summary>Module-owned settings row (StockItemSettings); null when never saved.</summary>
    Task<StockItemExtras?> GetExtrasAsync(long medicineId);

    /// <summary>Writes the F4 settings: the Medicines columns and the StockItemSettings row, in one transaction.</summary>
    Task<bool> SaveSettingsAsync(StockItemSettings settings);

    /// <summary>Applies the non-null fields to every listed item in one transaction; returns the rows updated.</summary>
    Task<int> BulkUpdateAsync(StockBulkUpdate update);

    /// <summary>Id of an item with this name and manufacturer (case-insensitive), or null.</summary>
    Task<long?> FindMedicineAsync(string name, string? manufacturer);

    Task<bool> BatchExistsAsync(long medicineId, string batchNumber);
}
