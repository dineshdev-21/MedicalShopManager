using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.Stock;

/// <summary>
/// Why a stock adjustment was made. Stored as INTEGER in StockAdjustments.Reason; never renumber.
/// </summary>
public enum StockAdjustmentReason
{
    Damaged = 1,
    Expired = 2,
    Lost = 3,
    Found = 4,
    Correction = 5,
    Sample = 6
}

/// <summary>Stock list quick filters (left pane chips of the Stock workspace).</summary>
public enum StockFilter
{
    All,
    InStock,
    OutOfStock,
    LowStock,
    NearExpiry,
    Expired
}

/// <summary>Sort orders for the stock list.</summary>
public enum StockSort
{
    NameAsc,
    NameDesc,
    StockHighToLow,
    StockLowToHigh,
    ValueHighToLow,
    ExpirySoonest
}

/// <summary>Tabs of the item detail pane, in display order (Localwell Product Detail).</summary>
public enum StockDetailTab
{
    Batches,
    Purchases,
    Sales,
    Timeline,
    Substitute,
    BranchStock,
    Distributor
}

/// <summary>Display status of one batch in the BATCHES tab.</summary>
public enum BatchStockStatus
{
    InStock,
    NearExpiry,
    Expired,
    OutOfStock,
    Inactive
}

/// <summary>Columns an opening-stock / item-master CSV can be mapped to.</summary>
public enum StockImportField
{
    Ignore,
    Name,
    Manufacturer,
    Composition,
    Category,
    PackLabel,
    UnitsPerPack,
    HsnCode,
    GstPercent,
    Schedule,
    Barcode,
    RackLocation,
    MinStock,
    MaxStock,
    BatchNumber,
    Expiry,
    Mrp,
    PurchaseRate,
    Ptr,
    SaleRate,
    QuantityPacks,
    LooseUnits
}

public static class StockAdjustmentReasonExtensions
{
    /// <summary>
    /// Required direction of the quantity change: -1 stock out, +1 stock in, 0 either (correction).
    /// Damaged, expired, lost and sample stock leaves the shelf; found stock comes back.
    /// </summary>
    public static int RequiredSign(this StockAdjustmentReason reason) => reason switch
    {
        StockAdjustmentReason.Found => 1,
        StockAdjustmentReason.Correction => 0,
        _ => -1
    };

    /// <summary>Ledger movement type written to StockMovements for this reason.</summary>
    public static MovementType ToMovementType(this StockAdjustmentReason reason) => reason switch
    {
        StockAdjustmentReason.Damaged => MovementType.Damaged,
        StockAdjustmentReason.Expired => MovementType.Expired,
        StockAdjustmentReason.Lost or StockAdjustmentReason.Sample => MovementType.WriteOff,
        _ => MovementType.StockAdjustment
    };

    public static string ToLabel(this StockAdjustmentReason reason) => reason switch
    {
        StockAdjustmentReason.Damaged => "Damaged / Breakage",
        StockAdjustmentReason.Expired => "Expired",
        StockAdjustmentReason.Lost => "Lost / Theft",
        StockAdjustmentReason.Found => "Found (excess)",
        StockAdjustmentReason.Correction => "Count correction",
        StockAdjustmentReason.Sample => "Free sample / Internal use",
        _ => reason.ToString()
    };
}

public static class StockDetailTabExtensions
{
    public static string ToTitle(this StockDetailTab tab) => tab switch
    {
        StockDetailTab.BranchStock => "BRANCH STK",
        _ => tab.ToString().ToUpperInvariant()
    };
}
