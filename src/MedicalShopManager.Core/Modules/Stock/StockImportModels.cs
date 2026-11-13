using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Stock;

/// <summary>One column of the uploaded file and the field it is mapped to.</summary>
public sealed record StockImportColumn(int Index, string Header, StockImportField Field);

/// <summary>
/// One parsed and validated row of an opening-stock / item-master CSV. Money is paisa per pack, quantities units.
/// <see cref="Errors"/> block the row; <see cref="Warnings"/> do not.
/// </summary>
public sealed class StockImportRow
{
    /// <summary>1-based line in the file (the header is line 1).</summary>
    public int LineNumber { get; init; }

    public string Name { get; init; } = string.Empty;
    public string? Manufacturer { get; init; }
    public string? Composition { get; init; }
    public string? Category { get; init; }
    public string? PackLabel { get; init; }
    /// <summary>Pack size from the file, or (when the row gives none) the existing item's, set by <see cref="StockCsv.ApplyPackSize"/>.</summary>
    public int UnitsPerPack { get; set; } = 1;
    /// <summary>True when the row itself has a units-per-pack or pack-label value.</summary>
    public bool PackSizeGiven { get; init; }
    public string? HsnCode { get; init; }
    public decimal GstPercent { get; init; }
    public DrugSchedule Schedule { get; init; }
    public string? Barcode { get; init; }
    public string? RackLocation { get; init; }
    public int MinStock { get; init; }
    public int MaxStock { get; init; }

    public string? BatchNumber { get; init; }
    public int? ExpiryMonth { get; init; }
    public int? ExpiryYear { get; init; }
    public long MrpPaisa { get; init; }
    public long PurchaseRatePaisa { get; init; }
    public long PtrPaisa { get; init; }
    public long SaleRatePaisa { get; init; }
    public long Packs { get; init; }
    public long LooseUnits { get; init; }

    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>Set by the import service when an item with the same name and manufacturer already exists.</summary>
    public long? ExistingMedicineId { get; set; }

    /// <summary>True when the row carries opening stock (a batch), not only item-master data.</summary>
    public bool HasBatch => !string.IsNullOrWhiteSpace(BatchNumber);
    public bool IsValid => Errors.Count == 0;
    public bool CreatesItem => ExistingMedicineId == null;

    public string ActionLabel => !IsValid ? "Error" : (CreatesItem ? "New item" : "Existing item") + (HasBatch ? " + batch" : string.Empty);
    public string ExpiryLabel => ExpiryMonth is { } m && ExpiryYear is { } y ? $"{m:00}/{y % 100:00}" : "-";
    public string QuantityLabel => HasBatch ? $"{Packs}" + (LooseUnits > 0 ? $" + {LooseUnits}" : string.Empty) : "-";
    public decimal Mrp => MrpPaisa / 100m;
    public string MessagesLabel => string.Join("; ", Errors.Concat(Warnings));

    /// <summary>Opening units = packs × pack size + loose.</summary>
    public long Units => HasBatch ? new PackQuantity(Packs, LooseUnits).ToUnits(Math.Max(UnitsPerPack, 1)) : 0;
}

/// <summary>Validated preview of an import before anything is written.</summary>
public sealed record StockImportPreview(IReadOnlyList<StockImportColumn> Columns, IReadOnlyList<StockImportRow> Rows, IReadOnlyList<string> FileErrors)
{
    public int ValidCount => Rows.Count(r => r.IsValid);
    public int InvalidCount => Rows.Count(r => !r.IsValid);
    public int NewItemCount => Rows.Where(r => r.IsValid && r.CreatesItem).Select(r => StockCsv.ItemKey(r.Name, r.Manufacturer)).Distinct().Count();
    public int BatchCount => Rows.Count(r => r.IsValid && r.HasBatch);
    public bool CanImport => FileErrors.Count == 0 && ValidCount > 0;
}

/// <summary>Outcome of an import: what was written and which lines failed.</summary>
public sealed record StockImportResult(int ItemsCreated, int BatchesCreated, int RowsSkipped, IReadOnlyList<string> Errors);
