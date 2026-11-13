using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Stock;

/// <summary>Search, filter and sort for the stock list ("Stock on hand").</summary>
public sealed record StockListQuery
{
    public string? Search { get; init; }
    public StockFilter Filter { get; init; } = StockFilter.All;
    public DrugSchedule? Schedule { get; init; }
    public string? Manufacturer { get; init; }
    public StockSort Sort { get; init; } = StockSort.NameAsc;
    /// <summary>Days ahead that count as "near expiry" (setting Stock.NearExpiryDays, default 90).</summary>
    public int NearExpiryDays { get; init; } = StockRules.DefaultNearExpiryDays;
    /// <summary>Include inactive / discontinued items.</summary>
    public bool IncludeInactive { get; init; }
}

/// <summary>One card in the stock list. Quantities are units, money is paisa (rates per pack).</summary>
public sealed record StockListItem
{
    public long MedicineId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Manufacturer { get; init; }
    public string? Composition { get; init; }
    public string? Category { get; init; }
    public string? Barcode { get; init; }
    public DrugSchedule Schedule { get; init; }
    public string? PackLabel { get; init; }
    public int UnitsPerPack { get; init; } = 1;
    public string? RackLocation { get; init; }
    public string? HsnCode { get; init; }
    public decimal GstRatePercent { get; init; }
    public int MinStock { get; init; }
    public bool IsActive { get; init; } = true;

    /// <summary>Sellable units: active, unexpired batches.</summary>
    public long StockUnits { get; init; }
    /// <summary>Units sitting in expired batches (not sellable).</summary>
    public long ExpiredUnits { get; init; }
    public int BatchCount { get; init; }
    /// <summary>MRP per pack of the FEFO batch (else the item's default MRP).</summary>
    public long MrpPaisa { get; init; }
    public DateTime? NearestExpiry { get; init; }
    /// <summary>Σ sellable units × net purchase rate (ex-GST).</summary>
    public long StockValuePaisa { get; init; }
    /// <summary>Σ sellable units × MRP.</summary>
    public long MrpValuePaisa { get; init; }
    public bool HasNearExpiry { get; init; }

    public bool IsInStock => StockUnits > 0;
    public bool IsLowStock => StockUnits > 0 && StockUnits <= MinStock;
    public bool HasExpiredStock => ExpiredUnits > 0;
    public string StockDisplay => PackQuantity.Format(StockUnits, Math.Max(UnitsPerPack, 1));
    public string ScheduleLabel => Schedule.ToLabel();
    public bool IsPrescription => Schedule.RequiresPrescription();
    public string PackDisplay => string.IsNullOrWhiteSpace(PackLabel) ? $"{UnitsPerPack}'s" : PackLabel!;
    public string ExpiryLabel => NearestExpiry?.ToString("MM/yy") ?? "-";
    public string ManufacturerLabel => string.IsNullOrWhiteSpace(Manufacturer) ? "-" : Manufacturer!;
    public string BatchCountLabel => BatchCount == 1 ? "1 batch" : $"{BatchCount} batches";
    public decimal Mrp => MrpPaisa / 100m;
    public decimal StockValue => StockValuePaisa / 100m;
    public string StockPill => IsInStock ? (IsLowStock ? "Low Stock" : "In Stock") : "Stock Out";
}

/// <summary>Totals over a stock list (header "Stock on hand" value).</summary>
public sealed record StockSummary
{
    public int ItemCount { get; init; }
    public int InStockCount { get; init; }
    public int OutOfStockCount { get; init; }
    public int LowStockCount { get; init; }
    public long StockValuePaisa { get; init; }
    public long MrpValuePaisa { get; init; }
    public decimal StockValue => StockValuePaisa / 100m;
    public decimal MrpValue => MrpValuePaisa / 100m;
}

public sealed record StockListResult(IReadOnlyList<StockListItem> Items, StockSummary Summary);

/// <summary>One row of the BATCHES tab.</summary>
public sealed record StockBatchRow
{
    public long BatchId { get; init; }
    public string BatchNumber { get; init; } = string.Empty;
    public DateTime ExpiryDate { get; init; }
    public int UnitsPerPack { get; init; } = 1;
    public long QuantityUnits { get; init; }
    public long MrpPaisa { get; init; }
    public long PurchaseRatePaisa { get; init; }
    /// <summary>Purchase rate after the batch discount, ex-GST (Localwell NET RATE).</summary>
    public long NetRatePaisa { get; init; }
    /// <summary>Landed cost per pack ex-GST spread over free units (batch_net_purchase_rate); margin basis.</summary>
    public long CostPaisa { get; init; }
    public long SaleRatePaisa { get; init; }
    public long PtrPaisa { get; init; }
    public decimal GstRatePercent { get; init; }
    public decimal MarginPercent { get; init; }
    public BatchStockStatus Status { get; init; }
    public string ExpiryHint { get; init; } = string.Empty;
    public long? SupplierId { get; init; }

    public string PackDisplay => $"{UnitsPerPack}'s";
    public string ExpiryLabel => ExpiryDate.ToString("MM/yy");
    public string StockDisplay => PackQuantity.Format(QuantityUnits, Math.Max(UnitsPerPack, 1));
    public string StatusLabel => Status switch
    {
        BatchStockStatus.NearExpiry => "Near Expiry",
        BatchStockStatus.OutOfStock => "Stock Out",
        BatchStockStatus.InStock => "In Stock",
        _ => Status.ToString()
    };
    public decimal Mrp => MrpPaisa / 100m;
    public decimal PurchaseRate => PurchaseRatePaisa / 100m;
    public decimal NetRate => NetRatePaisa / 100m;
    public decimal SaleRate => SaleRatePaisa / 100m;
}

/// <summary>Header of the item detail pane.</summary>
public sealed record StockItemHeader
{
    public long MedicineId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Manufacturer { get; init; }
    public string? Composition { get; init; }
    public DrugSchedule Schedule { get; init; }
    public string PackDisplay { get; init; } = string.Empty;
    public string? RackLocation { get; init; }
    public DateTime? Expiry { get; init; }
    public long MrpPaisa { get; init; }
    public long PurchaseRatePaisa { get; init; }
    public long NetRatePaisa { get; init; }
    public long SaleRatePaisa { get; init; }
    public decimal MarginPercent { get; init; }
    public decimal GstRatePercent { get; init; }
    public string? HsnCode { get; init; }
    public long StockUnits { get; init; }
    public int UnitsPerPack { get; init; } = 1;
    public bool IsActive { get; init; } = true;

    public string ScheduleLabel => Schedule.ToLabel();
    public bool IsPrescription => Schedule.RequiresPrescription();
    public string ExpiryLabel => Expiry?.ToString("MM/yy") ?? "-";
    public string LocationLabel => string.IsNullOrWhiteSpace(RackLocation) ? "-" : RackLocation!;
    public string StockDisplay => PackQuantity.Format(StockUnits, Math.Max(UnitsPerPack, 1));
    public decimal Mrp => MrpPaisa / 100m;
    public decimal PurchaseRate => PurchaseRatePaisa / 100m;
    public decimal NetRate => NetRatePaisa / 100m;
    public decimal SaleRate => SaleRatePaisa / 100m;
}

public sealed record StockItemDetail(StockItemHeader Header, IReadOnlyList<StockBatchRow> Batches);

/// <summary>PURCHASES tab row (one purchase line of this item).</summary>
public sealed record ItemPurchaseRow
{
    public long PurchaseId { get; init; }
    public DateTime PurchaseDate { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string SupplierName { get; init; } = string.Empty;
    public string BatchNumber { get; init; } = string.Empty;
    public DateTime ExpiryDate { get; init; }
    public long Quantity { get; init; }
    public long FreeQuantity { get; init; }
    /// <summary>Purchase rate as recorded on the purchase line.</summary>
    public long RatePaisa { get; init; }
    public long MrpPaisa { get; init; }
    public decimal GstRatePercent { get; init; }
    public long LineTotalPaisa { get; init; }
    public int Status { get; init; }
    /// <summary>Pack size of the received batch (1 when the batch is gone or was entered per unit).</summary>
    public int UnitsPerPack { get; init; } = 1;

    public string StatusLabel => Status switch { 0 => "Draft", 1 => "Confirmed", 2 => "Cancelled", _ => "-" };
    public string QuantityDisplay => PackQuantity.Format(Quantity, Math.Max(UnitsPerPack, 1));
    public string FreeDisplay => FreeQuantity == 0 ? "-" : PackQuantity.Format(FreeQuantity, Math.Max(UnitsPerPack, 1));
    public string ExpiryLabel => ExpiryDate.ToString("MM/yy");
    public decimal Rate => RatePaisa / 100m;
    public decimal Mrp => MrpPaisa / 100m;
    public decimal LineTotal => LineTotalPaisa / 100m;
}

/// <summary>SALES tab row (one sale line of this item).</summary>
public sealed record ItemSaleRow
{
    public long SaleId { get; init; }
    public DateTime InvoiceDate { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string BatchNumber { get; init; } = string.Empty;
    public long Quantity { get; init; }
    public long UnitPricePaisa { get; init; }
    public decimal DiscountPercent { get; init; }
    public long LineTotalPaisa { get; init; }
    /// <summary>Pack size of the sold batch (1 when unknown).</summary>
    public int UnitsPerPack { get; init; } = 1;

    public string QuantityDisplay => PackQuantity.Format(Quantity, Math.Max(UnitsPerPack, 1));
    public decimal UnitPrice => UnitPricePaisa / 100m;
    public decimal LineTotal => LineTotalPaisa / 100m;
}

/// <summary>TIMELINE tab row: one stock movement with the item-level running balance.</summary>
public sealed record StockTimelineEntry
{
    public long MovementId { get; init; }
    public DateTime Date { get; init; }
    public MovementType MovementType { get; init; }
    public string BatchNumber { get; init; } = string.Empty;
    public long QuantityIn { get; init; }
    public long QuantityOut { get; init; }
    public long BatchBalanceAfter { get; init; }
    public long RunningBalance { get; init; }
    public string? Reference { get; init; }
    public string? Reason { get; init; }

    public long SignedQuantity => QuantityIn - QuantityOut;
    public string MovementLabel => StockRules.MovementLabel(MovementType);
}

/// <summary>SUBSTITUTE tab row: another item with the same composition.</summary>
public sealed record SubstituteItem
{
    public long MedicineId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Manufacturer { get; init; }
    public string? PackLabel { get; init; }
    public int UnitsPerPack { get; init; } = 1;
    public long StockUnits { get; init; }
    public long MrpPaisa { get; init; }
    public string? RackLocation { get; init; }

    public string StockDisplay => PackQuantity.Format(StockUnits, Math.Max(UnitsPerPack, 1));
    public decimal Mrp => MrpPaisa / 100m;
    public bool IsInStock => StockUnits > 0;
}

/// <summary>DISTRIBUTOR tab row: a supplier who supplied this item, with the last rate.</summary>
public sealed record ItemDistributorRow
{
    public long SupplierId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public int PurchaseCount { get; init; }
    public long TotalUnits { get; init; }
    public DateTime? LastPurchaseDate { get; init; }
    public long LastRatePaisa { get; init; }

    public decimal LastRate => LastRatePaisa / 100m;
}

/// <summary>Item settings (F4). Rack, stock limits, discount cap, schedule and loose sale live on Medicines; the rest in StockItemSettings.</summary>
public sealed record StockItemSettings
{
    public long MedicineId { get; init; }
    public string? RackLocation { get; init; }
    public int MinStock { get; init; }
    public int MaxStock { get; init; }
    public decimal? MaxDiscountPercent { get; init; }
    public decimal MinDiscountPercent { get; init; }
    public DrugSchedule Schedule { get; init; }
    public bool AllowLooseSale { get; init; } = true;
    public bool IsDiscountLocked { get; init; }
    public bool IgnoreForShortage { get; init; }
}

/// <summary>Module-owned extra item fields (table StockItemSettings).</summary>
public sealed record StockItemExtras(long MedicineId, decimal MinDiscountPercent, bool IsDiscountLocked, bool IgnoreForShortage);

/// <summary>"+ Add New Batch": an opening-stock batch for an existing item.</summary>
public sealed record NewBatchRequest
{
    public long MedicineId { get; init; }
    public string BatchNumber { get; init; } = string.Empty;
    /// <summary>Expiry month and year as entered (MM/YY); stored as the month's last day.</summary>
    public int ExpiryMonth { get; init; }
    public int ExpiryYear { get; init; }
    public int UnitsPerPack { get; init; } = 1;
    public long Packs { get; init; }
    public long LooseUnits { get; init; }
    public long MrpPaisa { get; init; }
    public long PurchaseRatePaisa { get; init; }
    public long PtrPaisa { get; init; }
    public long SaleRatePaisa { get; init; }
    public decimal GstPercent { get; init; }
    public decimal DiscountPercent { get; init; }
    public long? SupplierId { get; init; }
    public DateTime EntryDate { get; init; } = DateTime.Today;
}

/// <summary>A posted stock adjustment voucher.</summary>
public sealed class StockAdjustment
{
    public long Id { get; set; }
    public string AdjustmentNumber { get; set; } = string.Empty;
    public DateTime AdjustmentDate { get; set; } = DateTime.Today;
    public StockAdjustmentReason Reason { get; set; }
    public string? Notes { get; set; }
    /// <summary>Signed value of the adjustment at purchase rate (negative = stock written down).</summary>
    public long TotalValuePaisa { get; set; }
    public long? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<StockAdjustmentLine> Lines { get; set; } = new();

    /// <summary>Line count from a list query that did not load <see cref="Lines"/>.</summary>
    public int LineCountHint { get; set; }

    public string ReasonLabel => Reason.ToLabel();
    public decimal TotalValue => TotalValuePaisa / 100m;
    public int LineCount => Lines.Count > 0 ? Lines.Count : LineCountHint;
}

public sealed class StockAdjustmentLine
{
    public long Id { get; set; }
    public long AdjustmentId { get; set; }
    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public long BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    /// <summary>Signed units: negative removes stock.</summary>
    public long QuantityChange { get; set; }
    public long BalanceBefore { get; set; }
    public long BalanceAfter { get; set; }
    public int UnitsPerPack { get; set; } = 1;
    /// <summary>Purchase rate per pack used for the value.</summary>
    public long RatePaisa { get; set; }
    public long ValuePaisa { get; set; }

    public string QuantityDisplay => PackQuantity.Format(QuantityChange, Math.Max(UnitsPerPack, 1));
    public decimal Value => ValuePaisa / 100m;
}

/// <summary>Request to post an adjustment; every line is a signed unit change on one batch.</summary>
public sealed record StockAdjustmentRequest
{
    public DateTime AdjustmentDate { get; init; } = DateTime.Today;
    public StockAdjustmentReason Reason { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<StockAdjustmentLineRequest> Lines { get; init; } = Array.Empty<StockAdjustmentLineRequest>();
}

public sealed record StockAdjustmentLineRequest(long BatchId, long QuantityChange);

/// <summary>Bulk "set" of item settings for many items. A null member leaves that field unchanged.</summary>
public sealed record StockBulkUpdate
{
    public IReadOnlyList<long> MedicineIds { get; init; } = Array.Empty<long>();
    public decimal? GstRatePercent { get; init; }
    public string? HsnCode { get; init; }
    public string? RackLocation { get; init; }
    public int? MinStock { get; init; }
    public int? MaxStock { get; init; }
    public decimal? MaxDiscountPercent { get; init; }
    public DrugSchedule? Schedule { get; init; }
    public string? Category { get; init; }

    public bool HasChanges => GstRatePercent != null || HsnCode != null || RackLocation != null || MinStock != null
        || MaxStock != null || MaxDiscountPercent != null || Schedule != null || Category != null;
}

/// <summary>Outcome of a bulk operation: how many succeeded and why the others failed.</summary>
public sealed record StockBulkResult(int Succeeded, IReadOnlyList<string> Errors)
{
    public int Failed => Errors.Count;
}

/// <summary>Printable stock statement (filtered stock list).</summary>
public sealed record StockStatement(string Title, DateTime GeneratedAt, IReadOnlyList<StockListItem> Items, StockSummary Summary);

/// <summary>An item master row with all of its batches (the raw input of the stock aggregates in <see cref="StockRules"/>).</summary>
public sealed record StockItemSnapshot(Models.Medicine Medicine, IReadOnlyList<Models.MedicineBatch> Batches);

/// <summary>Parameter for opening the Stock Adjustment page pre-filled with one batch (from the BATCHES tab).</summary>
public sealed record StockAdjustmentPrefill(long MedicineId, long? BatchId);
