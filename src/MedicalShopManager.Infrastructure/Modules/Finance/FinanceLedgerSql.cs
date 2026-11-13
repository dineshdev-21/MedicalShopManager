using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// The account ledger, derived on every read from the rows that move money (lesson 7: no running totals):
/// <list type="bullet">
/// <item>Payments rows written by Billing (sale tenders), Purchases (supplier payments) and SalesReturns (refunds) — booked
/// to their own AccountId, else to the account mapped to their PaymentMethod. Credit (method 4) moves no money. Rows
/// written by Finance vouchers (VoucherId set) are allocation records only and are skipped: the voucher is the movement.</item>
/// <item>Expenses (not cancelled).</item>
/// <item>Receipts / payment vouchers paid by cash, UPI, card or bank (active only).</item>
/// <item>Cheques on the date they cleared, in the account they cleared through.</item>
/// <item>Transfers and add / reduce money entries (not cancelled).</item>
/// </list>
/// Columns: AccountId, EntryDate ('yyyy-MM-dd'), Source (<see cref="AccountEntrySource"/>), Reference, PartyName,
/// Description, InPaisa, OutPaisa, SortKey. Enum values are compile-time constants; user values are always parameters.
/// </summary>
internal static class FinanceLedgerSql
{
    private const int SaleType = (int)PaymentType.SalePayment;
    private const int PurchaseType = (int)PaymentType.PurchasePayment;
    private const int SupplierType = (int)PaymentType.SupplierPayment;
    private const int RefundType = (int)PaymentType.CustomerRefund;
    private const int ExpenseType = (int)PaymentType.ExpensePayment;
    private const int Credit = (int)PaymentMethod.Credit;

    public static readonly string Entries = $@"
        SELECT COALESCE(p.AccountId, m.AccountId) AS AccountId, substr(p.PaymentDate, 1, 10) AS EntryDate,
               CASE p.PaymentType WHEN {SaleType} THEN {(int)AccountEntrySource.SalePayment}
                                  WHEN {PurchaseType} THEN {(int)AccountEntrySource.SupplierPayment}
                                  WHEN {SupplierType} THEN {(int)AccountEntrySource.SupplierPayment}
                                  WHEN {RefundType} THEN {(int)AccountEntrySource.CustomerRefund}
                                  ELSE {(int)AccountEntrySource.OtherPayment} END AS Source,
               p.TransactionReference AS Reference,
               COALESCE(s.CustomerName, pu.SupplierName) AS PartyName,
               p.Notes AS Description,
               CASE WHEN p.PaymentType = {SaleType} THEN p.AmountPaisa ELSE 0 END AS InPaisa,
               CASE WHEN p.PaymentType = {SaleType} THEN 0 ELSE p.AmountPaisa END AS OutPaisa,
               100000000000 + p.Id AS SortKey
        FROM Payments p
        LEFT JOIN FinanceMethodAccounts m ON m.PaymentMethod = p.PaymentMethod
        LEFT JOIN Sales s ON p.PaymentType = {SaleType} AND s.Id = p.TransactionId
        LEFT JOIN Purchases pu ON p.PaymentType IN ({PurchaseType}, {SupplierType}) AND pu.Id = p.TransactionId
        WHERE p.VoucherId IS NULL AND p.PaymentType <> {ExpenseType}
          AND (p.AccountId IS NOT NULL OR p.PaymentMethod <> {Credit})

        UNION ALL
        SELECT COALESCE(e.AccountId, m.AccountId), substr(e.ExpenseDate, 1, 10), {(int)AccountEntrySource.Expense},
               COALESCE(e.ExpenseNumber, '#' || e.Id), e.PayeeName,
               COALESCE(c.Name, 'Expense') || CASE WHEN e.Description IS NULL OR e.Description = '' THEN '' ELSE ': ' || e.Description END,
               0, e.AmountPaisa, 200000000000 + e.Id
        FROM Expenses e
        LEFT JOIN FinanceMethodAccounts m ON m.PaymentMethod = e.PaymentMethod
        LEFT JOIN FinanceExpenseCategories c ON c.Id = e.CategoryId
        WHERE e.IsCancelled = 0 AND (e.AccountId IS NOT NULL OR e.PaymentMethod <> {Credit})

        UNION ALL
        SELECT v.AccountId, substr(v.VoucherDate, 1, 10),
               CASE v.Kind WHEN {(int)FinanceVoucherKind.Receipt} THEN {(int)AccountEntrySource.Receipt} ELSE {(int)AccountEntrySource.PaymentVoucher} END,
               v.VoucherNumber, v.PartyName, COALESCE(v.Reference, v.Notes),
               CASE v.Kind WHEN {(int)FinanceVoucherKind.Receipt} THEN v.AmountPaisa ELSE 0 END,
               CASE v.Kind WHEN {(int)FinanceVoucherKind.Receipt} THEN 0 ELSE v.AmountPaisa END,
               300000000000 + v.Id
        FROM FinanceVouchers v
        WHERE v.ChequeId IS NULL AND v.Status = {(int)FinanceVoucherStatus.Active}

        UNION ALL
        SELECT ch.AccountId, substr(ch.ClearedDate, 1, 10),
               CASE ch.Direction WHEN {(int)ChequeDirection.Received} THEN {(int)AccountEntrySource.Receipt} ELSE {(int)AccountEntrySource.PaymentVoucher} END,
               COALESCE(v.VoucherNumber, 'Cheque ' || ch.ChequeNumber), ch.PartyName,
               'Cheque ' || ch.ChequeNumber || ' cleared' || CASE WHEN ch.BankName IS NULL THEN '' ELSE ' (' || ch.BankName || ')' END,
               CASE ch.Direction WHEN {(int)ChequeDirection.Received} THEN ch.AmountPaisa ELSE 0 END,
               CASE ch.Direction WHEN {(int)ChequeDirection.Received} THEN 0 ELSE ch.AmountPaisa END,
               400000000000 + ch.Id
        FROM FinanceCheques ch
        LEFT JOIN FinanceVouchers v ON v.Id = ch.VoucherId
        WHERE ch.Status = {(int)ChequeStatus.Cleared} AND ch.AccountId IS NOT NULL AND ch.ClearedDate IS NOT NULL

        UNION ALL
        SELECT mv.ToAccountId, substr(mv.MovementDate, 1, 10),
               CASE mv.Kind WHEN {(int)FinanceMovementKind.Transfer} THEN {(int)AccountEntrySource.Transfer} ELSE {(int)AccountEntrySource.Adjustment} END,
               mv.Reference, NULL,
               CASE WHEN mv.Kind = {(int)FinanceMovementKind.Transfer} THEN 'Transfer from ' || fa.Name ELSE 'Money added' END
                   || CASE WHEN mv.Notes IS NULL OR mv.Notes = '' THEN '' ELSE ': ' || mv.Notes END,
               mv.AmountPaisa, 0, 500000000000 + mv.Id
        FROM FinanceMovements mv
        LEFT JOIN FinanceAccounts fa ON fa.Id = mv.FromAccountId
        WHERE mv.IsCancelled = 0 AND mv.ToAccountId IS NOT NULL

        UNION ALL
        SELECT mv.FromAccountId, substr(mv.MovementDate, 1, 10),
               CASE mv.Kind WHEN {(int)FinanceMovementKind.Transfer} THEN {(int)AccountEntrySource.Transfer} ELSE {(int)AccountEntrySource.Adjustment} END,
               mv.Reference, NULL,
               CASE WHEN mv.Kind = {(int)FinanceMovementKind.Transfer} THEN 'Transfer to ' || ta.Name ELSE 'Money taken out' END
                   || CASE WHEN mv.Notes IS NULL OR mv.Notes = '' THEN '' ELSE ': ' || mv.Notes END,
               0, mv.AmountPaisa, 500000000000 + mv.Id
        FROM FinanceMovements mv
        LEFT JOIN FinanceAccounts ta ON ta.Id = mv.ToAccountId
        WHERE mv.IsCancelled = 0 AND mv.FromAccountId IS NOT NULL";
}
