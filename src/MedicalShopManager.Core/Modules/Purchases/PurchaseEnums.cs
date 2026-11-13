namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>Kind of purchase return (debit note). Stored as INTEGER in PurchaseReturns.Kind; never renumber.</summary>
public enum PurchaseReturnKind
{
    /// <summary>Goods returned against a purchase or from current stock (spec 04 "Return Purchase").</summary>
    Regular = 1,

    /// <summary>Expired / near-expiry stock returned to the distributor (spec 04 "Return Expired Products").</summary>
    Expiry = 2
}

/// <summary>Why goods go back to the distributor. Stored as INTEGER; never renumber.</summary>
public enum PurchaseReturnReason
{
    Expired = 1,
    NearExpiry = 2,
    Damaged = 3,
    WrongItem = 4,
    ExcessStock = 5,
    Other = 6
}

/// <summary>Status chips of the purchase list (Draft, payment state of confirmed bills, Overdue, Cancelled).</summary>
public enum PurchaseListStatusFilter
{
    All,
    Draft,
    Unpaid,
    Partial,
    Paid,
    Overdue,
    Cancelled
}

/// <summary>Columns of a supplier invoice file (CSV / Excel) that the import screen can map.</summary>
public enum PurchaseImportField
{
    Ignore,
    ProductName,
    Barcode,
    HsnCode,
    BatchNumber,
    Expiry,
    UnitsPerPack,
    Quantity,
    FreeQuantity,
    Mrp,
    Rate,
    Ptr,
    SaleRate,
    DiscountPercent,
    SchemePercent,
    GstPercent
}

/// <summary>Kind of a supplier statement line.</summary>
public enum SupplierStatementEntryType
{
    OpeningBalance,
    Purchase,
    Payment,
    DebitNote,
    RefundReceived
}

/// <summary>
/// How a "X + Y" scheme turns into free quantity (spec 04 <c>getQtyAndFreeQtyConsideringScheme</c>):
/// Full = Y per complete X; Half = also half a scheme from half of X; Quarter = quarter steps from a quarter of X.
/// </summary>
public enum SchemeRounding
{
    Full,
    Half,
    Quarter
}

public static class PurchaseEnumExtensions
{
    public static string ToLabel(this PurchaseReturnReason reason) => reason switch
    {
        PurchaseReturnReason.Expired => "Expired",
        PurchaseReturnReason.NearExpiry => "Near expiry",
        PurchaseReturnReason.Damaged => "Damaged / Breakage",
        PurchaseReturnReason.WrongItem => "Wrong item / Not ordered",
        PurchaseReturnReason.ExcessStock => "Excess stock",
        _ => "Other"
    };

    public static string ToLabel(this PurchaseReturnKind kind) => kind == PurchaseReturnKind.Expiry ? "Expiry Return" : "Purchase Return";

    public static string ToLabel(this PurchaseImportField field) => field switch
    {
        PurchaseImportField.Ignore => "(ignore)",
        PurchaseImportField.ProductName => "Product name",
        PurchaseImportField.HsnCode => "HSN",
        PurchaseImportField.BatchNumber => "Batch no",
        PurchaseImportField.UnitsPerPack => "Pack (units per pack)",
        PurchaseImportField.Quantity => "Qty (packs)",
        PurchaseImportField.FreeQuantity => "Free (packs)",
        PurchaseImportField.Mrp => "MRP / pack",
        PurchaseImportField.Rate => "Purchase rate / pack",
        PurchaseImportField.Ptr => "PTR / pack",
        PurchaseImportField.SaleRate => "Sale rate / pack",
        PurchaseImportField.DiscountPercent => "Disc %",
        PurchaseImportField.SchemePercent => "Scheme %",
        PurchaseImportField.GstPercent => "GST %",
        _ => field.ToString()
    };
}
