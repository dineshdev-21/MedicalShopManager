namespace MedicalShopManager.Core.Modules.Billing;

/// <summary>Customers with the Billing fields (individual / business). Soft delete only (ledgers may point at a customer).</summary>
public interface IBillingCustomerRepository
{
    /// <summary>Active customers whose name, mobile or GSTIN contains <paramref name="text"/>, with derived balances.</summary>
    Task<IReadOnlyList<BillingCustomer>> SearchAsync(string? text, int limit = 25);
    Task<BillingCustomer?> GetByIdAsync(long id);
    Task<BillingCustomer?> FindByPhoneAsync(string phone);
    Task<long> InsertAsync(BillingCustomer customer);
    Task<bool> UpdateAsync(BillingCustomer customer);
    Task<bool> DeactivateAsync(long id);
}

/// <summary>Read-only product and batch lookups for the POS (Medicines / MedicineBatches).</summary>
public interface ISaleCatalogRepository
{
    /// <summary>Active items matching name, composition, manufacturer, barcode or SKU; in-stock first.</summary>
    Task<IReadOnlyList<SaleCatalogItem>> SearchAsync(string text, int limit = 30);
    /// <summary>Exact barcode or SKU match (scanner input).</summary>
    Task<SaleCatalogItem?> FindByCodeAsync(string code);
    Task<SaleCatalogItem?> GetItemAsync(long medicineId);
    /// <summary>All active batches of an item (including expired and empty ones; the caller filters).</summary>
    Task<IReadOnlyList<SaleBatchOption>> GetBatchesAsync(long medicineId);
    Task<SaleBatchOption?> GetBatchAsync(long batchId);
}

/// <summary>Posting, editing, cancelling and reading sales (Sales / SaleItems / StockMovements / Payments).</summary>
public interface IBillingSaleRepository
{
    /// <summary>
    /// Posts a new sale in one transaction: invoice number (DocumentSequences), header, lines, guarded stock
    /// deduction per batch, stock movements, payment rows, and deletes the source draft. Rollback leaves no trace.
    /// </summary>
    Task<(long SaleId, string InvoiceNumber)> InsertAsync(SalePosting posting);

    /// <summary>
    /// Replaces an existing active sale (edit mode) in one transaction: restores the old lines' stock, rewrites
    /// header/lines/payments and deducts the new quantities. The invoice number is kept.
    /// </summary>
    Task UpdateAsync(long saleId, SalePosting posting);

    /// <summary>Cancels (deletes) a sale: restores stock with movements, removes its payments, marks it Cancelled.</summary>
    Task CancelAsync(long saleId, long? userId, string reason);

    Task<BillingInvoice?> GetInvoiceAsync(long saleId);

    /// <summary>True when sale returns were recorded against the sale (edit / delete are then blocked).</summary>
    Task<bool> HasReturnsAsync(long saleId);

    /// <summary>Statutory register lines (H1 / X / NRx) of active sales in a date range.</summary>
    Task<IReadOnlyList<H1RegisterEntry>> GetScheduleRegisterAsync(DateTime from, DateTime to);
}

/// <summary>Auto-saved unsaved bills (spec 02 §O), kept for the retention window.</summary>
public interface ISaleDraftRepository
{
    /// <summary>Inserts (id null/0) or updates a draft; returns its id.</summary>
    Task<long> SaveAsync(long? draftId, SaleDraftSummary summary, string payload);
    Task<IReadOnlyList<SaleDraftSummary>> ListAsync(DateTime updatedSince);
    Task<SaleDraft?> GetAsync(long draftId);
    Task<bool> DeleteAsync(long draftId);
    /// <summary>Deletes drafts last updated before <paramref name="cutoff"/>; returns the count.</summary>
    Task<int> PurgeAsync(DateTime cutoff);
}

/// <summary>Saved custom-charge names.</summary>
public interface ICustomChargeRepository
{
    Task<IReadOnlyList<CustomChargeOption>> GetActiveAsync();
    /// <summary>Returns the id of the (re)activated charge with this name (case-insensitive).</summary>
    Task<long> EnsureAsync(string name);
    Task<bool> DeactivateAsync(long id);
}

/// <summary>
/// The POS workflow: compute, validate, post, edit, delete, replicate and read sales. All amounts come from
/// <see cref="BillingCalculator"/> (and so <c>PricingCalculator</c>).
/// </summary>
public interface ISaleBillingService
{
    Task<BillingSettings> GetSettingsAsync();

    /// <summary>Remembers the BATCH ORDER toggle (FEFO / LIFO) for the next bills.</summary>
    Task SaveBatchOrderAsync(BatchPickOrder order);

    /// <summary>True when the bill is inter-state for this customer GSTIN (IGST instead of CGST + SGST).</summary>
    bool IsInterState(BillingSettings settings, string? customerGstin);

    /// <summary>Live totals for the screen (same maths as posting).</summary>
    BillComputation Compute(SalePostingRequest request, BillingSettings settings);

    /// <summary>Validation errors before the payment step (empty = ready for payment).</summary>
    IReadOnlyList<string> Validate(SalePostingRequest request, BillingSettings settings);

    Task<IReadOnlyList<SaleCatalogItem>> SearchItemsAsync(string text);
    Task<SaleCatalogItem?> FindByCodeAsync(string code);
    Task<SaleCatalogItem?> GetItemAsync(long medicineId);
    /// <summary>Batches annotated with expiry state and margin, in the configured pick order (sellable first).</summary>
    Task<IReadOnlyList<SaleBatchOption>> GetBatchesAsync(long medicineId, DateTime billDate);

    /// <summary>Posts a new sale or saves an edit (<see cref="SalePostingRequest.EditingSaleId"/>).</summary>
    /// <exception cref="BillingValidationException">Invalid bill or payment.</exception>
    Task<PostedSale> SaveAsync(SalePostingRequest request);

    Task<BillingInvoice?> GetInvoiceAsync(long saleId);

    /// <summary>Loads a sale for editing as a request (lines carry their stock plus the sale's own units).</summary>
    Task<SalePostingRequest?> LoadForEditAsync(long saleId);

    /// <summary>Copies a sale into a new request with currently available batches; warnings list what changed.</summary>
    Task<(SalePostingRequest Request, IReadOnlyList<string> Warnings)> ReplicateAsync(long saleId);

    /// <exception cref="BillingValidationException">The sale is already cancelled or has returns.</exception>
    Task DeleteAsync(long saleId, long? userId, string reason);

    Task<IReadOnlyList<H1RegisterEntry>> GetScheduleRegisterAsync(DateTime from, DateTime to);
}

/// <summary>Draft auto-save and the F8 list.</summary>
public interface ISaleDraftService
{
    /// <summary>Saves the bill as a draft; an empty bill deletes the draft. Returns the draft id (null when deleted).</summary>
    Task<long?> SaveAsync(SalePostingRequest request, long? draftId, long netPayablePaisa);
    /// <summary>Drafts within the retention window (older ones are purged first).</summary>
    Task<IReadOnlyList<SaleDraftSummary>> ListAsync();
    Task<SalePostingRequest?> LoadAsync(long draftId);
    Task DeleteAsync(long draftId);
}

/// <summary>Customer create / edit / search with validation.</summary>
public interface IBillingCustomerService
{
    Task<IReadOnlyList<BillingCustomer>> SearchAsync(string? text);
    Task<BillingCustomer?> GetAsync(long id);
    /// <summary>Validates, normalises and saves; returns the saved customer.</summary>
    /// <exception cref="BillingValidationException">Invalid fields or a duplicate mobile number.</exception>
    Task<BillingCustomer> SaveAsync(BillingCustomer customer);
    Task DeactivateAsync(long id);
}

/// <summary>Keeps attached prescription files (image / PDF) inside the app data folder so backups and other tills see them.</summary>
public interface IPrescriptionFileStore
{
    /// <summary>Copies <paramref name="sourcePath"/> into the store and returns the stored file's full path.</summary>
    /// <exception cref="BillingValidationException">Missing file, unsupported type or too large.</exception>
    Task<string> StoreAsync(string sourcePath);
}
