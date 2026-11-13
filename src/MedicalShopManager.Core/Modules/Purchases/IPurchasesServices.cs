using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>Settings the purchase screens use (AppSettings keys seeded by migration 12).</summary>
public sealed record PurchaseSettings(decimal DefaultSaleDiscountPercent, int NearExpiryMonths, bool RoundOffToRupee, string? BusinessStateCode)
{
    public const string SaleDiscountKey = "Purchases.DefaultSaleDiscountPercent";
    public const string NearExpiryMonthsKey = "Purchases.NearExpiryMonths";
    public const string RoundOffKey = "Purchases.RoundOffToRupee";

    public static PurchaseSettings Default { get; } = new(0m, 3, true, null);
}

/// <summary>Purchase entry workflow: validation, totals, drafts, confirmation, edits, cancellation and payments.</summary>
public interface IPurchaseEntryService
{
    Task<PurchaseSettings> GetSettingsAsync();

    /// <summary>Fills every computed field (line amounts, totals, round-off, balance, payment status).</summary>
    Task<PurchaseTotals> RecalculateAsync(PurchaseBill bill);

    /// <summary>
    /// A new line pre-filled for an item: pack and HSN/GST from the item master, rates from its last purchase, the
    /// sale rate from MRP and the sale-rate setting.
    /// </summary>
    Task<PurchaseBillLine> CreateLineAsync(Medicine medicine);

    /// <summary>IGST when the distributor's state differs from the shop's.</summary>
    Task<bool> IsInterStateAsync(Distributor distributor);

    Task<PurchaseBill?> GetAsync(long id);
    Task<IReadOnlyList<PurchaseListRow>> ListAsync(PurchaseListFilter filter);

    /// <summary>Other bill of the same distributor with this number (warn; the user may override).</summary>
    Task<PurchaseListRow?> FindDuplicateAsync(PurchaseBill bill);

    Task<long> SaveDraftAsync(PurchaseBill bill, long? userId);

    /// <summary>Validates, then posts stock. <paramref name="allowDuplicateInvoice"/> is the "overwrite warning" override.</summary>
    Task<PurchasePostingResult> ConfirmAsync(PurchaseBill bill, PurchasePaymentInput? initialPayment, long? userId, bool allowDuplicateInvoice = false);

    /// <summary>Saves changes to a confirmed purchase (stock reversed and re-applied in one transaction).</summary>
    Task<PurchasePostingResult> UpdateConfirmedAsync(PurchaseBill bill, long? userId, bool allowDuplicateInvoice = false);

    /// <summary>Deletes a draft, or cancels a confirmed purchase with stock reversal.</summary>
    Task DeleteAsync(long purchaseId, string reason, long? userId);

    Task RecordPaymentAsync(long purchaseId, PurchasePaymentInput payment, long? userId);
    Task<IReadOnlyList<PaymentAllocation>> RecordSupplierPaymentAsync(long supplierId, PurchasePaymentInput payment, long? userId);
    Task<IReadOnlyList<PurchasePaymentRecord>> GetPaymentsAsync(long purchaseId);
}

/// <summary>Purchase returns and expiry returns to distributors (debit notes).</summary>
public interface IPurchaseReturnService
{
    Task<IReadOnlyList<ReturnableBatch>> GetReturnableBatchesAsync(long supplierId, long? purchaseId = null, string? search = null);

    /// <summary>Expired and near-expiry batches (within the near-expiry setting) for the expiry-return screen.</summary>
    Task<IReadOnlyList<ReturnableBatch>> GetExpiryCandidatesAsync(long? supplierId, int? withinMonths = null);

    /// <summary>A debit note line for a batch with the default rate and the maximum quantity.</summary>
    PurchaseReturnLine CreateLine(ReturnableBatch batch, int units);

    Task RecalculateAsync(PurchaseReturnDocument doc);

    /// <summary>Validates and posts; returns the document with its debit note number.</summary>
    Task<PurchaseReturnDocument> PostAsync(PurchaseReturnDocument doc, long? userId);

    Task<PurchaseReturnDocument?> GetAsync(long id);
    Task<IReadOnlyList<PurchaseReturnListRow>> ListAsync(DateTime from, DateTime to, long? supplierId = null);
    Task CancelAsync(long id, long? userId);

    /// <summary>The number the next debit note would get (display only).</summary>
    Task<string> PeekNextNumberAsync(DateTime date);
}

/// <summary>Distributor master and statements.</summary>
public interface IDistributorService
{
    Task<IReadOnlyList<Distributor>> SearchAsync(string? query, bool includeInactive = false);
    Task<Distributor?> GetAsync(long id);

    /// <summary>Validates and inserts or updates; returns the id.</summary>
    Task<long> SaveAsync(Distributor distributor);

    Task SetActiveAsync(long id, bool isActive);

    /// <summary>Statement for a period: opening balance at <paramref name="from"/>, movements, closing balance.</summary>
    Task<SupplierStatement> GetStatementAsync(long supplierId, DateTime from, DateTime to);
}

/// <summary>Result of importing a supplier invoice file into a draft.</summary>
public sealed record PurchaseImportResult(PurchaseBill Draft, IReadOnlyList<PurchaseImportRow> Skipped, int ImportedCount);

/// <summary>Supplier invoice import (CSV or Excel) with column mapping.</summary>
public interface IPurchaseImportService
{
    /// <summary>Reads a .csv / .txt / .xlsx file into a table.</summary>
    Task<PurchaseImportTable> ReadFileAsync(string path);

    /// <summary>The distributor's saved mapping when the headers match, else a suggestion from the header names.</summary>
    Task<IReadOnlyList<PurchaseImportField>> GetMappingAsync(long supplierId, PurchaseImportTable table);

    /// <summary>Parses rows with the mapping (errors flagged per row), without saving anything.</summary>
    IReadOnlyList<PurchaseImportRow> Preview(PurchaseImportTable table, IReadOnlyList<PurchaseImportField> mapping);

    /// <summary>
    /// Matches rows to items (barcode, then exact name), builds a draft purchase for the distributor, saves the
    /// mapping for next time and returns the draft (not yet saved) plus the rows that could not be used.
    /// </summary>
    Task<PurchaseImportResult> BuildDraftAsync(long supplierId, string supplierInvoiceNumber, DateTime invoiceDate,
        PurchaseImportTable table, IReadOnlyList<PurchaseImportField> mapping, string source);
}
