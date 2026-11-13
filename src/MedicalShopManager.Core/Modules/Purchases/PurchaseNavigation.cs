namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>
/// Page keys of the Purchases module, so other modules can open its screens with
/// <c>INavigationService.NavigateTo(PurchasePages.Entry, new OpenPurchaseRequest(id))</c>.
/// </summary>
public static class PurchasePages
{
    /// <summary>Add / view / edit purchase (Ctrl+Alt+P). Parameters: <see cref="OpenPurchaseRequest"/>, <see cref="ImportedPurchaseDraftRequest"/>.</summary>
    public const string Entry = "PurchaseEntry";

    /// <summary>Purchase list / register. Parameter: <see cref="PurchaseListRequest"/>.</summary>
    public const string List = "Purchases";

    public const string Import = "PurchaseImport";

    /// <summary>Purchase return (Ctrl+Alt+R). Parameter: <see cref="PurchaseReturnRequest"/>.</summary>
    public const string Return = "PurchaseReturn";

    public const string ExpiryReturn = "PurchaseExpiryReturn";

    /// <summary>Distributor master (Ctrl+Alt+D).</summary>
    public const string Suppliers = "Suppliers";

    /// <summary>Distributor statement. Parameter: <see cref="SupplierStatementRequest"/>.</summary>
    public const string SupplierStatement = "PurchaseSupplierStatement";

    /// <summary>Debit note register (purchase and expiry returns).</summary>
    public const string DebitNotes = "PurchaseDebitNotes";
}

/// <summary>Open an existing purchase on the entry screen (view; <paramref name="Edit"/> starts editing).</summary>
public sealed record OpenPurchaseRequest(long PurchaseId, bool Edit = false);

/// <summary>Open the entry screen pre-filled with an imported (unsaved) draft and the rows that could not be used.</summary>
public sealed record ImportedPurchaseDraftRequest(PurchaseBill Draft, IReadOnlyList<PurchaseImportRow> Skipped);

/// <summary>Open the purchase list filtered to one distributor.</summary>
public sealed record PurchaseListRequest(long? SupplierId);

/// <summary>Open the purchase return screen for a distributor, optionally limited to one purchase.</summary>
public sealed record PurchaseReturnRequest(long SupplierId, long? PurchaseId = null);

/// <summary>Open the statement of one distributor.</summary>
public sealed record SupplierStatementRequest(long SupplierId);
