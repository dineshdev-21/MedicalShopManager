using System.Globalization;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Stock;

namespace MedicalShopManager.Infrastructure.Modules.Stock;

/// <summary>
/// Opening-stock / item-master CSV import. Preview parses and validates without writing. Import then creates each
/// missing item through <see cref="IMedicineService"/> (item-master validation and composition linking) and each
/// opening batch through <see cref="IStockService.AddBatchAsync"/> (one transaction per batch with its movement).
/// A failing line is reported and skipped; the lines before it stay imported.
/// </summary>
public sealed class StockImportService : IStockImportService
{
    public const int MaxRows = 10000;

    private readonly IStockRepository _stock;
    private readonly IStockService _stockService;
    private readonly IMedicineService _medicines;
    private readonly IAppSettingsRepository _settings;
    private readonly IAuditService _audit;
    private readonly ILogger<StockImportService> _logger;

    public StockImportService(IStockRepository stock, IStockService stockService, IMedicineService medicines,
        IAppSettingsRepository settings, IAuditService audit, ILogger<StockImportService> logger)
    {
        _stock = stock;
        _stockService = stockService;
        _medicines = medicines;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public IReadOnlyList<StockImportColumn> SuggestMapping(string csvText)
    {
        var records = StockCsv.Parse(csvText);
        return records.Count == 0 ? Array.Empty<StockImportColumn>() : StockCsv.SuggestMapping(records[0]);
    }

    public async Task<StockImportPreview> PreviewAsync(string csvText, IReadOnlyList<StockImportColumn> mapping)
    {
        var records = StockCsv.Parse(csvText);
        var fileErrors = new List<string>();
        if (records.Count < 2) fileErrors.Add("The file has no data rows below the header.");
        if (records.Count - 1 > MaxRows) fileErrors.Add($"The file has {records.Count - 1} rows; import at most {MaxRows} at a time.");
        fileErrors.AddRange(StockCsv.ValidateMapping(mapping));
        if (fileErrors.Count > 0) return new StockImportPreview(mapping, Array.Empty<StockImportRow>(), fileErrors);

        var rows = StockCsv.BuildRows(records.Skip(1).ToList(), mapping, await DefaultGstAsync());
        StockCsv.FlagDuplicateBatches(rows);

        var existing = new Dictionary<string, (long Id, int UnitsPerPack)?>();
        foreach (var row in rows)
        {
            (long Id, int UnitsPerPack)? item = null;
            if (row.Name.Length > 0)
            {
                var key = StockCsv.ItemKey(row.Name, row.Manufacturer);
                if (!existing.TryGetValue(key, out item))
                {
                    item = await FindItemAsync(row.Name, row.Manufacturer);
                    existing[key] = item;
                }
            }

            // A row without its own pack size takes the existing item's (else 1), so "Qty" stays in packs.
            StockCsv.ApplyPackSize(row, item?.UnitsPerPack ?? 1);
            if (!row.IsValid) continue;
            row.ExistingMedicineId = item?.Id;
            if (item is { } found && row.HasBatch && await _stock.BatchExistsAsync(found.Id, row.BatchNumber!))
                row.Errors.Add($"Batch {row.BatchNumber} already exists for this item.");
        }
        return new StockImportPreview(mapping, rows, fileErrors);
    }

    private async Task<(long Id, int UnitsPerPack)?> FindItemAsync(string name, string? manufacturer)
    {
        if (await _stock.FindMedicineAsync(name, manufacturer) is not { } id) return null;
        var snapshot = await _stock.GetSnapshotAsync(id);
        return (id, Math.Max(snapshot?.Medicine.UnitsPerPack ?? 1, 1));
    }

    public async Task<StockImportResult> ImportAsync(StockImportPreview preview)
    {
        if (!preview.CanImport) throw new StockValidationException("Nothing to import: fix the errors in the preview first.");

        var created = new Dictionary<string, long>();
        var errors = new List<string>();
        int items = 0, batches = 0, skipped = preview.InvalidCount;

        foreach (var row in preview.Rows.Where(r => r.IsValid))
        {
            try
            {
                var key = StockCsv.ItemKey(row.Name, row.Manufacturer);
                var medicineId = row.ExistingMedicineId ?? (created.TryGetValue(key, out var id) ? id : (long?)null);
                if (medicineId == null)
                {
                    medicineId = await _medicines.CreateAsync(ToMedicine(row));
                    created[key] = medicineId.Value;
                    items++;
                }

                if (row.HasBatch)
                {
                    await _stockService.AddBatchAsync(new NewBatchRequest
                    {
                        MedicineId = medicineId.Value,
                        BatchNumber = row.BatchNumber!,
                        ExpiryMonth = row.ExpiryMonth ?? 0,
                        ExpiryYear = row.ExpiryYear ?? 0,
                        UnitsPerPack = Math.Max(row.UnitsPerPack, 1),
                        Packs = row.Packs,
                        LooseUnits = row.LooseUnits,
                        MrpPaisa = row.MrpPaisa,
                        PurchaseRatePaisa = row.PurchaseRatePaisa,
                        PtrPaisa = row.PtrPaisa,
                        SaleRatePaisa = row.SaleRatePaisa,
                        GstPercent = row.GstPercent,
                        EntryDate = DateTime.Today
                    });
                    batches++;
                }
            }
            catch (Exception ex) when (ex is StockValidationException or ArgumentException or InvalidOperationException)
            {
                skipped++;
                errors.Add($"Line {row.LineNumber} ({row.Name}): {ex.Message.Split(" (Parameter")[0]}");
            }
        }

        await _audit.LogAsync("StockImport", "Medicine", null, null,
            $"{{\"ItemsCreated\":{items},\"BatchesCreated\":{batches},\"RowsSkipped\":{skipped}}}", "Opening stock / item master CSV import");
        _logger.LogInformation("Stock import: {Items} items, {Batches} batches, {Skipped} rows skipped", items, batches, skipped);
        return new StockImportResult(items, batches, skipped, errors);
    }

    private async Task<decimal> DefaultGstAsync()
    {
        var raw = await _settings.GetValueAsync("DefaultGstRate");
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var gst) && gst is >= 0 and <= StockRules.MaxGstPercent
            ? gst
            : 12m;
    }

    internal static Medicine ToMedicine(StockImportRow row) => new()
    {
        Name = row.Name.Trim(),
        Manufacturer = row.Manufacturer,
        Composition = row.Composition,
        Category = row.Category,
        PackLabel = row.PackLabel ?? $"{Math.Max(row.UnitsPerPack, 1)}'s",
        UnitsPerPack = Math.Max(row.UnitsPerPack, 1),
        HsnCode = row.HsnCode,
        GstRatePercent = row.GstPercent,
        Schedule = row.Schedule,
        Barcode = row.Barcode,
        RackLocation = row.RackLocation,
        MinStock = row.MinStock,
        MaxStock = row.MaxStock,
        DefaultMrpPaisa = row.MrpPaisa,
        DefaultSellingPricePaisa = row.SaleRatePaisa > 0 ? row.SaleRatePaisa : row.MrpPaisa
    };
}
