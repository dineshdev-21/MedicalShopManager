using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Modules.Stock;

/// <summary>
/// The Stock workspace: stock list, item detail tabs, F4 settings, opening batches, stock adjustments and bulk
/// updates. Rule violations throw <see cref="StockValidationException"/> with a message for the user.
/// </summary>
public interface IStockService
{
    /// <summary>NearExpiryDays setting (default <see cref="StockRules.DefaultNearExpiryDays"/>).</summary>
    Task<int> GetNearExpiryDaysAsync();

    Task<StockListResult> GetStockListAsync(StockListQuery query);
    Task<IReadOnlyList<string>> GetManufacturersAsync();

    Task<StockItemDetail?> GetItemDetailAsync(long medicineId, bool showZeroBatches);
    Task<IReadOnlyList<ItemPurchaseRow>> GetPurchasesAsync(long medicineId);
    Task<IReadOnlyList<ItemSaleRow>> GetSalesAsync(long medicineId);
    Task<IReadOnlyList<StockTimelineEntry>> GetTimelineAsync(long medicineId);
    Task<IReadOnlyList<SubstituteItem>> GetSubstitutesAsync(long medicineId);
    Task<IReadOnlyList<ItemDistributorRow>> GetDistributorsAsync(long medicineId);
    Task<IReadOnlyList<MedicineBatch>> GetBatchesAsync(long medicineId);

    Task<StockItemSettings?> GetSettingsAsync(long medicineId);
    Task SaveSettingsAsync(StockItemSettings settings);

    /// <summary>"+ Add New Batch": validates and posts an opening-stock batch; returns the batch id.</summary>
    Task<long> AddBatchAsync(NewBatchRequest request);

    Task<string> PeekAdjustmentNumberAsync(DateTime date);
    Task<StockAdjustment> PostAdjustmentAsync(StockAdjustmentRequest request);
    Task<IReadOnlyList<StockAdjustment>> GetAdjustmentsAsync(DateTime from, DateTime to);
    Task<StockAdjustment?> GetAdjustmentAsync(long id);

    Task<StockBulkResult> BulkUpdateAsync(StockBulkUpdate update);

    /// <summary>Remembered "Show Zero" toggle of the BATCHES tab (setting Stock.ShowZeroBatches).</summary>
    Task<bool> GetShowZeroBatchesAsync();
    Task SetShowZeroBatchesAsync(bool value);
}
