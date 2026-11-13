using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.SalesReturns;

/// <summary>Read-only queries over Billing's Sales / SaleItems for the sales register (this module never writes them).</summary>
public interface ISalesRegisterRepository
{
    Task<IReadOnlyList<SalesRegisterRow>> QueryAsync(SalesRegisterFilter filter);

    /// <summary>Active bills whose number, customer name or mobile contains <paramref name="text"/>, newest first.</summary>
    Task<IReadOnlyList<SalesRegisterRow>> SearchBillsAsync(string text, int limit = 25);
}

/// <summary>Sale returns (SaleReturns / SaleReturnItems / SaleReturnRefunds plus the shared Returns ledger rows).</summary>
public interface ISaleReturnRepository
{
    /// <summary>The bill with every line's returned quantity and amount so far; null when it does not exist.</summary>
    Task<ReturnableSale?> GetReturnableSaleAsync(long saleId);

    /// <summary>Batch details for returns without a bill.</summary>
    Task<IReadOnlyDictionary<long, SaleReturnBatchInfo>> GetBatchesAsync(IEnumerable<long> batchIds);

    /// <summary>All batches of an item (expired ones included: an expired batch can still come back, as expired stock).</summary>
    Task<IReadOnlyList<SaleReturnBatchInfo>> GetItemBatchesAsync(long medicineId);

    Task<IReadOnlyList<SaleReturnItemOption>> SearchItemsAsync(string text, int limit = 30);
    Task<IReadOnlyList<SaleReturnCustomerOption>> SearchCustomersAsync(string text, int limit = 25);

    /// <summary>
    /// Posts a computed return in one transaction: return and credit note numbers (DocumentSequences), header, lines,
    /// returnable-quantity re-check (two tills), stock back into each batch (except damaged), stock ledger rows,
    /// shared Returns rows, and the refund (refund row + Payments row) when refunded now. Rollback leaves no trace.
    /// </summary>
    /// <exception cref="SaleReturnValidationException">Quantities no longer returnable, bill cancelled, batch missing.</exception>
    Task<(long Id, string ReturnNumber, string CreditNoteNumber)> InsertAsync(SaleReturnDocument document);

    Task<SaleReturnDocument?> GetAsync(long id);
    Task<IReadOnlyList<SaleReturnRow>> ListAsync(SaleReturnFilter filter);

    /// <summary>Records a refund of open credit (refund row + Payments row) in one transaction; re-checks the open balance.</summary>
    Task<long> AddRefundAsync(CreditRefundRequest refund, string creditNoteNumber);

    /// <summary>
    /// Cancels a return in one transaction: restocked units leave their batches again (refused if they were sold
    /// since), Returns rows and refund Payments rows are removed, the return is marked Cancelled.
    /// </summary>
    Task CancelAsync(long id, string reason, long? userId);

    /// <summary>Open credit (credit notes not yet refunded or adjusted) of a customer.</summary>
    Task<long> GetCustomerCreditAsync(long customerId);
}

/// <summary>Sales register with totals.</summary>
public interface ISalesRegisterService
{
    /// <exception cref="SaleReturnValidationException">Invalid date range.</exception>
    Task<(IReadOnlyList<SalesRegisterRow> Rows, SalesRegisterTotals Totals)> QueryAsync(SalesRegisterFilter filter);
    Task<IReadOnlyList<SalesRegisterRow>> SearchBillsAsync(string text);
}

/// <summary>The sale return workflow: look up, compute, validate, post, refund, cancel, read.</summary>
public interface ISaleReturnService
{
    Task<RoundOffMode> GetRoundOffAsync();

    /// <summary>Numbers the next return would get (display only; never stored).</summary>
    Task<(string ReturnNumber, string CreditNoteNumber)> PeekNumbersAsync(DateTime returnDate);

    Task<ReturnableSale?> GetReturnableSaleAsync(long saleId);
    Task<IReadOnlyList<SaleReturnItemOption>> SearchItemsAsync(string text);
    Task<IReadOnlyList<SaleReturnBatchInfo>> GetItemBatchesAsync(long medicineId);
    Task<IReadOnlyList<SaleReturnCustomerOption>> SearchCustomersAsync(string text);

    /// <summary>Running totals and messages for the screen (pure, no I/O).</summary>
    (SaleReturnDocument Document, IReadOnlyList<string> Errors) Preview(SaleReturnRequest request, ReturnableSale? sale,
        IReadOnlyDictionary<long, SaleReturnBatchInfo> batches, RoundOffMode roundOff);

    /// <summary>Validates against fresh data and posts; returns the saved credit note.</summary>
    /// <exception cref="SaleReturnValidationException">Invalid return.</exception>
    Task<SaleReturnDocument> PostAsync(SaleReturnRequest request);

    Task<SaleReturnDocument?> GetAsync(long id);
    Task<(IReadOnlyList<SaleReturnRow> Rows, SaleReturnTotals Totals)> ListAsync(SaleReturnFilter filter);

    /// <exception cref="SaleReturnValidationException">More than the open credit, invalid method or date.</exception>
    Task<SaleReturnDocument> RefundCreditAsync(CreditRefundRequest refund);

    /// <exception cref="SaleReturnValidationException">Already cancelled, no reason, or restocked units already sold.</exception>
    Task CancelAsync(long id, string reason, long? userId);

    Task<long> GetCustomerCreditAsync(long customerId);
}
