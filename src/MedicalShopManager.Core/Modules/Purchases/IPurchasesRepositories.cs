using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>Distributor master (Suppliers table) with derived balances and the supplier ledger.</summary>
public interface IDistributorRepository
{
    Task<Distributor?> GetByIdAsync(long id);
    Task<IReadOnlyList<Distributor>> SearchAsync(string? query, bool includeInactive = false);
    Task<long> InsertAsync(Distributor distributor);
    Task<bool> UpdateAsync(Distributor distributor);
    Task<bool> SetActiveAsync(long id, bool isActive);

    /// <summary>Another distributor (not <paramref name="excludeId"/>) already uses this GSTIN.</summary>
    Task<bool> GstinExistsAsync(string gstin, long excludeId = 0);

    /// <summary>Ledger movements of one distributor (purchases, payments, debit notes, refunds), oldest first.</summary>
    Task<IReadOnlyList<SupplierLedgerEntry>> GetLedgerEntriesAsync(long supplierId);
}

/// <summary>Purchase bills: drafts, transactional posting to stock, edits/cancellation with stock reversal, payments.</summary>
public interface IPurchaseBillRepository
{
    Task<PurchaseBill?> GetAsync(long id);
    Task<IReadOnlyList<PurchaseListRow>> ListAsync(PurchaseListFilter filter);

    /// <summary>Inserts or updates a draft and its lines in one transaction. Returns the purchase id.</summary>
    Task<long> SaveDraftAsync(PurchaseBill bill);

    /// <summary>Hard-deletes a draft (drafts never touched stock).</summary>
    Task<bool> DeleteDraftAsync(long id);

    /// <summary>
    /// Posts a (new or draft) purchase in one transaction: allocates the purchase number, creates or merges batches by
    /// MedicineId + BatchNumber + Expiry, writes stock movements and the optional initial payment. Rolls everything
    /// back on failure.
    /// </summary>
    Task<PurchasePostingResult> ConfirmAsync(PurchaseBill bill, PurchasePaymentInput? initialPayment, long? userId);

    /// <summary>Replaces a confirmed purchase: reverses its stock, re-applies the new lines, keeps number and payments.</summary>
    Task<PurchasePostingResult> UpdateConfirmedAsync(PurchaseBill bill, long? userId);

    /// <summary>Cancels a confirmed purchase and takes its stock back out (only while that stock is still on hand).</summary>
    Task CancelConfirmedAsync(long purchaseId, string reason, long? userId);

    /// <summary>Records a payment against one purchase (Payments row + cached paid/balance) in one transaction.</summary>
    Task<long> RecordPaymentAsync(long purchaseId, PurchasePaymentInput payment, long? userId);

    /// <summary>Spreads one payment over the distributor's unpaid bills, oldest due first.</summary>
    Task<IReadOnlyList<PaymentAllocation>> RecordSupplierPaymentAsync(long supplierId, PurchasePaymentInput payment, long? userId);

    Task<IReadOnlyList<PurchasePaymentRecord>> GetPaymentsAsync(long purchaseId);

    /// <summary>Another non-cancelled purchase of the distributor with the same bill number ("This Bill is Duplicate").</summary>
    Task<PurchaseListRow?> FindDuplicateAsync(long supplierId, string supplierInvoiceNumber, long excludeId = 0);

    /// <summary>Most recent confirmed line for an item, to pre-fill pack, rates and GST.</summary>
    Task<PurchaseBillLine?> GetLastLineForMedicineAsync(long medicineId);

    /// <summary>Saved import column mapping for a distributor (null if none).</summary>
    Task<IReadOnlyList<PurchaseImportField>?> GetImportMappingAsync(long supplierId, IReadOnlyList<string> headers);
    Task SaveImportMappingAsync(long supplierId, IReadOnlyList<string> headers, IReadOnlyList<PurchaseImportField> mapping);
}

/// <summary>Debit notes (purchase and expiry returns).</summary>
public interface IPurchaseReturnRepository
{
    /// <summary>
    /// Batches in stock that can go back to <paramref name="supplierId"/>: lines of its confirmed purchases with
    /// stock on hand (limited to one purchase when <paramref name="purchaseId"/> is set).
    /// </summary>
    Task<IReadOnlyList<ReturnableBatch>> GetReturnableBatchesAsync(long supplierId, long? purchaseId = null, string? search = null);

    /// <summary>Expired or near-expiry batches in stock (expiry on or before <paramref name="expiringBy"/>), optionally for one distributor.</summary>
    Task<IReadOnlyList<ReturnableBatch>> GetExpiryCandidatesAsync(DateTime expiringBy, long? supplierId = null);

    /// <summary>Posts a debit note: allocates its number, takes the stock out, all in one transaction.</summary>
    Task<PurchaseReturnDocument> PostAsync(PurchaseReturnDocument doc, long? userId);

    Task<PurchaseReturnDocument?> GetAsync(long id);
    Task<IReadOnlyList<PurchaseReturnListRow>> ListAsync(DateTime from, DateTime to, long? supplierId = null);

    /// <summary>Cancels a debit note and puts its stock back.</summary>
    Task CancelAsync(long id, long? userId);
}
