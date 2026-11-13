using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Modules.Stock;

/// <summary>Stock postings: opening batches and numbered stock adjustments, each in one SQLite transaction.</summary>
public interface IStockAdjustmentRepository
{
    /// <summary>
    /// Inserts an opening-stock batch and its OpeningStock movement atomically. Throws
    /// <see cref="StockValidationException"/> when the item already has a batch with that number.
    /// </summary>
    Task<long> InsertOpeningBatchAsync(MedicineBatch batch, long? userId);

    /// <summary>
    /// Allocates the StockAdjustment number, re-reads every batch balance, applies the signed changes, writes the
    /// movements and the voucher, and commits; any failure rolls everything back (the number is not consumed).
    /// </summary>
    Task<StockAdjustment> PostAdjustmentAsync(StockAdjustmentRequest request, long? userId, string? userName);

    Task<IReadOnlyList<StockAdjustment>> GetAdjustmentsAsync(DateTime from, DateTime to);
    Task<StockAdjustment?> GetAdjustmentAsync(long id);
}
