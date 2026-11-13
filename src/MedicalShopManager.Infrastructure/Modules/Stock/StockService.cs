using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Stock;

namespace MedicalShopManager.Infrastructure.Modules.Stock;

/// <summary>
/// Stock workspace service: aggregates through <see cref="StockRules"/>, validates every change, posts through the
/// transactional repositories, then writes the audit trail and queues the Excel sync.
/// </summary>
public sealed class StockService : IStockService
{
    public const string NearExpiryDaysKey = "NearExpiryDays";
    public const string ShowZeroBatchesKey = "Stock.ShowZeroBatches";
    private const int TabRowLimit = 200;

    private readonly IStockRepository _stock;
    private readonly IStockAdjustmentRepository _postings;
    private readonly IAppSettingsRepository _settings;
    private readonly IAuditService _audit;
    private readonly IAuthService _auth;
    private readonly IDocumentNumberService _numbers;
    private readonly IExcelSyncQueueRepository _excelQueue;
    private readonly ILogger<StockService> _logger;
    private readonly TimeProvider _clock;

    public StockService(IStockRepository stock, IStockAdjustmentRepository postings, IAppSettingsRepository settings,
        IAuditService audit, IAuthService auth, IDocumentNumberService numbers, IExcelSyncQueueRepository excelQueue,
        ILogger<StockService> logger, TimeProvider? clock = null)
    {
        _stock = stock;
        _postings = postings;
        _settings = settings;
        _audit = audit;
        _auth = auth;
        _numbers = numbers;
        _excelQueue = excelQueue;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Today => _clock.GetLocalNow().Date;

    // ───────────────────────── Settings ─────────────────────────

    public async Task<int> GetNearExpiryDaysAsync()
    {
        var raw = await _settings.GetValueAsync(NearExpiryDaysKey);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 730
            ? days
            : StockRules.DefaultNearExpiryDays;
    }

    public async Task<bool> GetShowZeroBatchesAsync() => await _settings.GetValueAsync(ShowZeroBatchesKey) == "1";

    public Task SetShowZeroBatchesAsync(bool value) =>
        _settings.SetValueAsync(ShowZeroBatchesKey, value ? "1" : "0", "Stock: show zero-quantity batches in the BATCHES tab");

    // ───────────────────────── Reads ─────────────────────────

    public async Task<StockListResult> GetStockListAsync(StockListQuery query)
    {
        var today = Today;
        var snapshots = await _stock.GetSnapshotsAsync(query.IncludeInactive);
        var items = snapshots.Select(s => StockRules.Summarize(s.Medicine, s.Batches, today, query.NearExpiryDays));
        return StockRules.Apply(items, query);
    }

    public Task<IReadOnlyList<string>> GetManufacturersAsync() => _stock.GetManufacturersAsync();

    public async Task<StockItemDetail?> GetItemDetailAsync(long medicineId, bool showZeroBatches)
    {
        var snapshot = await _stock.GetSnapshotAsync(medicineId);
        if (snapshot == null) return null;
        var today = Today;
        var nearDays = await GetNearExpiryDaysAsync();
        return new StockItemDetail(
            StockRules.BuildHeader(snapshot.Medicine, snapshot.Batches, today),
            StockRules.BatchRows(snapshot.Batches, today, nearDays, showZeroBatches));
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetBatchesAsync(long medicineId) =>
        (await _stock.GetSnapshotAsync(medicineId))?.Batches ?? Array.Empty<MedicineBatch>();

    public Task<IReadOnlyList<ItemPurchaseRow>> GetPurchasesAsync(long medicineId) => _stock.GetPurchasesAsync(medicineId, TabRowLimit);

    public Task<IReadOnlyList<ItemSaleRow>> GetSalesAsync(long medicineId) => _stock.GetSalesAsync(medicineId, TabRowLimit);

    public async Task<IReadOnlyList<StockTimelineEntry>> GetTimelineAsync(long medicineId) =>
        StockRules.BuildTimeline(await _stock.GetMovementsAsync(medicineId));

    public async Task<IReadOnlyList<SubstituteItem>> GetSubstitutesAsync(long medicineId)
    {
        var today = Today;
        var snapshots = await _stock.GetSubstituteSnapshotsAsync(medicineId);
        return snapshots
            .Select(s => StockRules.Summarize(s.Medicine, s.Batches, today, StockRules.DefaultNearExpiryDays))
            .OrderByDescending(i => i.IsInStock)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new SubstituteItem
            {
                MedicineId = i.MedicineId,
                Name = i.Name,
                Manufacturer = i.Manufacturer,
                PackLabel = i.PackDisplay,
                UnitsPerPack = i.UnitsPerPack,
                StockUnits = i.StockUnits,
                MrpPaisa = i.MrpPaisa,
                RackLocation = i.RackLocation
            })
            .ToList();
    }

    public Task<IReadOnlyList<ItemDistributorRow>> GetDistributorsAsync(long medicineId) => _stock.GetDistributorsAsync(medicineId);

    // ───────────────────────── Item settings (F4) ─────────────────────────

    public async Task<StockItemSettings?> GetSettingsAsync(long medicineId)
    {
        var snapshot = await _stock.GetSnapshotAsync(medicineId);
        if (snapshot == null) return null;
        var extras = await _stock.GetExtrasAsync(medicineId);
        var m = snapshot.Medicine;
        return new StockItemSettings
        {
            MedicineId = m.Id,
            RackLocation = m.RackLocation,
            MinStock = m.MinStock,
            MaxStock = m.MaxStock,
            MaxDiscountPercent = m.MaxDiscountPercent,
            MinDiscountPercent = extras?.MinDiscountPercent ?? 0m,
            Schedule = m.Schedule,
            AllowLooseSale = m.AllowLooseSale,
            IsDiscountLocked = extras?.IsDiscountLocked ?? false,
            IgnoreForShortage = extras?.IgnoreForShortage ?? false
        };
    }

    public async Task SaveSettingsAsync(StockItemSettings settings)
    {
        if (StockRules.ValidateSettings(settings) is { } error) throw new StockValidationException(error);
        var before = await GetSettingsAsync(settings.MedicineId) ?? throw new StockValidationException("The item no longer exists.");
        if (!await _stock.SaveSettingsAsync(settings)) throw new StockValidationException("The item no longer exists.");

        await _audit.LogAsync("UpdateStockSettings", "Medicine", settings.MedicineId,
            JsonSerializer.Serialize(before), JsonSerializer.Serialize(settings), "Item settings (F4)");
        await _excelQueue.EnqueueAsync(SyncEntityType.Medicine, settings.MedicineId);
    }

    // ───────────────────────── Opening batch ─────────────────────────

    public async Task<long> AddBatchAsync(NewBatchRequest request)
    {
        var snapshot = await _stock.GetSnapshotAsync(request.MedicineId) ?? throw new StockValidationException("Choose the item for the new batch.");
        if (StockRules.ValidateNewBatch(request, snapshot.Medicine.AllowLooseSale) is { } error) throw new StockValidationException(error);

        var batch = StockRules.ToBatch(request);
        var batchId = await _postings.InsertOpeningBatchAsync(batch, _auth.CurrentUser?.Id);
        batch.Id = batchId;

        await _audit.LogAsync("AddOpeningBatch", "MedicineBatch", batchId, null, JsonSerializer.Serialize(new
        {
            batch.MedicineId,
            Medicine = snapshot.Medicine.Name,
            batch.BatchNumber,
            Expiry = batch.ExpiryDate.ToString("yyyy-MM"),
            Units = batch.QuantityRemaining,
            batch.UnitsPerPack,
            batch.MrpPaisa,
            batch.PurchaseRatePaisa,
            ValuePaisa = StockRules.StockValuePaisa(batch)
        }), "Opening stock");
        await _excelQueue.EnqueueAsync(SyncEntityType.Batch, batchId);
        await _excelQueue.EnqueueAsync(SyncEntityType.Medicine, batch.MedicineId);
        _logger.LogInformation("Opening batch {Batch} added to medicine {MedicineId}: {Units} units", batch.BatchNumber, batch.MedicineId, batch.QuantityRemaining);
        return batchId;
    }

    // ───────────────────────── Adjustments ─────────────────────────

    public Task<string> PeekAdjustmentNumberAsync(DateTime date) => _numbers.PeekNextAsync(DocumentType.StockAdjustment, date);

    public async Task<StockAdjustment> PostAdjustmentAsync(StockAdjustmentRequest request)
    {
        if (StockRules.ValidateAdjustmentRequest(request, Today) is { } error) throw new StockValidationException(error);

        var user = _auth.CurrentUser;
        var adjustment = await _postings.PostAdjustmentAsync(request, user?.Id, user?.DisplayName ?? user?.Username);

        await _audit.LogAsync("StockAdjustment", "StockAdjustment", adjustment.Id, null, JsonSerializer.Serialize(new
        {
            adjustment.AdjustmentNumber,
            Reason = adjustment.Reason.ToString(),
            adjustment.TotalValuePaisa,
            Lines = adjustment.Lines.Select(l => new { l.BatchId, l.BatchNumber, l.MedicineName, l.QuantityChange, l.BalanceBefore, l.BalanceAfter, l.ValuePaisa })
        }), adjustment.Notes ?? adjustment.ReasonLabel);

        foreach (var line in adjustment.Lines)
        {
            await _excelQueue.EnqueueAsync(SyncEntityType.Batch, line.BatchId);
            await _excelQueue.EnqueueAsync(SyncEntityType.StockMovement, line.BatchId);
        }
        foreach (var medicineId in adjustment.Lines.Select(l => l.MedicineId).Distinct())
            await _excelQueue.EnqueueAsync(SyncEntityType.Medicine, medicineId);

        _logger.LogInformation("Stock adjustment {Number} posted: {Lines} line(s), value {Value} paisa",
            adjustment.AdjustmentNumber, adjustment.Lines.Count, adjustment.TotalValuePaisa);
        return adjustment;
    }

    public Task<IReadOnlyList<StockAdjustment>> GetAdjustmentsAsync(DateTime from, DateTime to) => _postings.GetAdjustmentsAsync(from, to);

    public Task<StockAdjustment?> GetAdjustmentAsync(long id) => _postings.GetAdjustmentAsync(id);

    // ───────────────────────── Bulk update ─────────────────────────

    public async Task<StockBulkResult> BulkUpdateAsync(StockBulkUpdate update)
    {
        if (StockRules.ValidateBulkUpdate(update) is { } error) throw new StockValidationException(error);

        var ids = update.MedicineIds.Distinct().ToList();
        var updated = await _stock.BulkUpdateAsync(update with { MedicineIds = ids });
        await _audit.LogAsync("BulkUpdateStockSettings", "Medicine", null, null, JsonSerializer.Serialize(new
        {
            Items = ids.Count,
            update.GstRatePercent,
            update.HsnCode,
            update.RackLocation,
            update.MinStock,
            update.MaxStock,
            update.MaxDiscountPercent,
            Schedule = update.Schedule?.ToString(),
            update.Category
        }), $"Bulk update of {updated} item(s)");
        foreach (var id in ids)
            await _excelQueue.EnqueueAsync(SyncEntityType.Medicine, id);

        var missing = ids.Count - updated;
        return new StockBulkResult(updated, missing > 0 ? new[] { $"{missing} selected item(s) no longer exist." } : Array.Empty<string>());
    }
}
