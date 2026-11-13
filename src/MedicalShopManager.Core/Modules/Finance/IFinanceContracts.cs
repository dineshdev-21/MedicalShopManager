using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.Finance;

/// <summary>Page keys of the Finance module (other modules open them with INavigationService.NavigateTo).</summary>
public static class FinancePages
{
    public const string Expenses = "Expenses";
    public const string CashBank = "FinanceCashBank";
    public const string AccountStatement = "FinanceAccountStatement";
    public const string ReceivePayment = "FinanceReceivePayment";
    public const string PaySupplier = "FinancePaySupplier";
    public const string Vouchers = "FinanceVouchers";
    public const string Cheques = "FinanceCheques";
    public const string PartyLedgers = "FinancePartyLedgers";
}

/// <summary>Navigation parameter: open Receive Payment / Pay Supplier for a party (e.g. from the dashboard's due list).</summary>
public sealed record OpenPartyPaymentRequest(PartyType PartyType, long PartyId, FinancePaymentMode? Mode = null);

/// <summary>Navigation parameter: open the account statement of an account.</summary>
public sealed record OpenAccountStatementRequest(long AccountId, DateTime? From = null, DateTime? To = null);

/// <summary>Navigation parameter: open the party ledgers on a party.</summary>
public sealed record OpenPartyLedgerRequest(PartyType PartyType, long PartyId);

/// <summary>Cash &amp; bank accounts, the legacy method → account map, transfers / adjustments and account statements.</summary>
public interface IFinanceAccountRepository
{
    /// <summary>Accounts with their balance as at the end of <paramref name="asOf"/> (derived from source rows).</summary>
    Task<IReadOnlyList<FinanceAccount>> ListAsync(DateTime asOf, bool includeInactive = false);
    Task<FinanceAccount?> GetAsync(long id);
    Task<bool> NameExistsAsync(string name, long excludeId = 0);
    Task<long> InsertAsync(FinanceAccount account);
    Task UpdateAsync(FinanceAccount account);
    /// <summary>Deactivates / reactivates; an account mapped to a payment method cannot be deactivated.</summary>
    Task SetActiveAsync(long id, bool isActive);

    Task<IReadOnlyList<PaymentMethodAccount>> GetMethodAccountsAsync();
    Task SetMethodAccountAsync(PaymentMethod method, long accountId);

    Task<AccountStatement> GetStatementAsync(long accountId, DateTime from, DateTime to);

    /// <summary>Inserts a transfer / adjustment in one transaction (accounts checked to exist and be active).</summary>
    Task<long> InsertMovementAsync(FinanceMovement movement);
    Task<FinanceMovement?> GetMovementAsync(long id);
    Task CancelMovementAsync(long id, string reason);
    Task<IReadOnlyList<FinanceMovement>> ListMovementsAsync(DateTime from, DateTime to);
}

/// <summary>Customers / suppliers as payment parties: dues, balances with ageing and ledgers (read-only over other modules' tables).</summary>
public interface IFinancePartyRepository
{
    Task<IReadOnlyList<FinanceParty>> SearchAsync(PartyType partyType, string? text, int limit = 30);
    Task<FinanceParty?> GetAsync(PartyType partyType, long partyId);
    /// <summary>Open documents (opening balance, credit sales / purchase bills) with a balance due, oldest first.</summary>
    Task<IReadOnlyList<DueDocument>> GetDuesAsync(PartyType partyType, long partyId);
    /// <summary>Receivables (customers) or payables (suppliers) with ageing as on <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<PartyBalanceRow>> GetBalancesAsync(PartyType partyType, DateTime asOf, bool includeSettled = false);
    Task<PartyLedger> GetLedgerAsync(PartyType partyType, long partyId, DateTime from, DateTime to);
}

/// <summary>Receipts (payment in) and payment vouchers (payment out) with their allocations.</summary>
public interface IFinanceVoucherRepository
{
    /// <summary>
    /// Posts a voucher in one transaction: number (DocumentSequences), cheque (cheque mode), voucher row, allocations
    /// (Payments rows + cached paid / balance on the bill), all re-validated against the dues read inside the transaction.
    /// </summary>
    Task<FinanceVoucher> PostAsync(VoucherRequest request);
    Task<FinanceVoucher?> GetAsync(long id);
    Task<IReadOnlyList<FinanceVoucher>> ListAsync(VoucherFilter filter);
    /// <summary>Cancels an active voucher (not one paid by a cleared cheque) and reverses its allocations.</summary>
    Task CancelAsync(long id, string reason);
}

public interface IFinanceChequeRepository
{
    Task<IReadOnlyList<FinanceCheque>> ListAsync(ChequeFilter filter);
    Task<FinanceCheque?> GetAsync(long id);
    /// <summary>Moves a cheque to a new status in one transaction; bounce / cancel reverses the voucher's allocations.</summary>
    Task ChangeStatusAsync(ChequeStatusChange change);
}

public interface IFinanceExpenseRepository
{
    Task<IReadOnlyList<FinanceExpenseCategory>> ListCategoriesAsync(bool includeInactive = false);
    Task<long> InsertCategoryAsync(string name);
    Task RenameCategoryAsync(long id, string name);
    Task SetCategoryActiveAsync(long id, bool isActive);

    Task<IReadOnlyList<FinanceExpense>> ListAsync(ExpenseFilter filter);
    Task<FinanceExpense?> GetAsync(long id);
    /// <summary>Inserts with an expense number allocated in the same transaction.</summary>
    Task<FinanceExpense> InsertAsync(FinanceExpense expense);
    Task UpdateAsync(FinanceExpense expense);
    Task CancelAsync(long id, string reason);
    Task<IReadOnlyList<ExpenseCategoryTotal>> GetCategoryTotalsAsync(DateTime from, DateTime to);
}

/// <summary>Accounts, transfers and statements with validation and audit logging.</summary>
public interface ICashBankService
{
    Task<IReadOnlyList<FinanceAccount>> GetAccountsAsync(bool includeInactive = false);
    Task<FinanceAccount> SaveAccountAsync(FinanceAccount account, long? userId);
    Task SetAccountActiveAsync(long accountId, bool isActive, long? userId);
    Task<IReadOnlyList<PaymentMethodAccount>> GetMethodAccountsAsync();
    Task SetMethodAccountAsync(PaymentMethod method, long accountId, long? userId);
    Task<AccountStatement> GetStatementAsync(long accountId, DateTime from, DateTime to);
    Task<long> RecordMovementAsync(FinanceMovement movement, long? userId);
    Task CancelMovementAsync(long movementId, string reason, long? userId);
    Task<IReadOnlyList<FinanceMovement>> GetMovementsAsync(DateTime from, DateTime to);
}

/// <summary>Receive payment from customers, pay suppliers, party ledgers and receivable / payable ageing.</summary>
public interface IPartyPaymentService
{
    Task<IReadOnlyList<FinanceParty>> SearchPartiesAsync(PartyType partyType, string? text);
    Task<FinanceParty?> GetPartyAsync(PartyType partyType, long partyId);
    Task<IReadOnlyList<DueDocument>> GetDuesAsync(PartyType partyType, long partyId);
    Task<FinanceVoucher> PostAsync(VoucherRequest request);
    Task<FinanceVoucher?> GetVoucherAsync(long voucherId);
    Task<IReadOnlyList<FinanceVoucher>> GetVouchersAsync(VoucherFilter filter);
    Task CancelVoucherAsync(long voucherId, string reason, long? userId);
    Task<IReadOnlyList<PartyBalanceRow>> GetBalancesAsync(PartyType partyType, DateTime asOf, bool includeSettled = false);
    Task<PartyLedger> GetLedgerAsync(PartyType partyType, long partyId, DateTime from, DateTime to);
    /// <summary>The voucher number the next receipt / payment would get (display only).</summary>
    Task<string> PeekNumberAsync(FinanceVoucherKind kind, DateTime date);
}

public interface IChequeService
{
    Task<IReadOnlyList<FinanceCheque>> GetChequesAsync(ChequeFilter filter);
    Task ChangeStatusAsync(ChequeStatusChange change);
}

public interface IExpenseService
{
    Task<IReadOnlyList<FinanceExpenseCategory>> GetCategoriesAsync(bool includeInactive = false);
    Task<FinanceExpenseCategory> AddCategoryAsync(string name, long? userId);
    Task RenameCategoryAsync(long categoryId, string name, long? userId);
    Task SetCategoryActiveAsync(long categoryId, bool isActive, long? userId);
    Task<(IReadOnlyList<FinanceExpense> Rows, ExpenseTotals Totals)> GetExpensesAsync(ExpenseFilter filter);
    Task<FinanceExpense?> GetExpenseAsync(long expenseId);
    /// <summary>GST split the form shows before saving (same maths the save uses).</summary>
    Task<ExpenseAmounts> PreviewAmountsAsync(long amountPaisa, decimal gstPercent, bool amountIncludesGst, string? payeeGstin);
    Task<FinanceExpense> SaveAsync(ExpenseRequest request);
    Task CancelAsync(long expenseId, string reason, long? userId);
    Task<IReadOnlyList<ExpenseCategoryTotal>> GetCategoryTotalsAsync(DateTime from, DateTime to);
    Task<string> PeekNumberAsync(DateTime date);
}
