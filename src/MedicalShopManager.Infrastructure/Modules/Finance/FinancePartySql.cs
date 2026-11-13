using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// SQL over customers / suppliers and their documents (read-only over Billing, SalesReturns and Purchases tables).
/// Party balances follow ARCHITECTURE §10 and are derived from source rows:
/// customer due = opening − opening settled + Σ bill balances − open credit notes − unallocated receipts;
/// supplier due = opening − opening settled + Σ bill balances − open debit notes − unallocated payments.
/// </summary>
internal static class FinancePartySql
{
    public const int CustomerType = (int)PartyType.Customer;
    public const int SupplierType = (int)PartyType.Supplier;
    public const int SaleActive = 1;          // SaleStatus.Active (Billing, migration 10)
    public const int SaleReturnActive = 1;    // SaleReturnStatus.Active (SalesReturns, migration 11)
    public const int PurchaseConfirmed = (int)PurchaseStatus.Confirmed;
    private const int Opening = (int)AllocationTarget.OpeningBalance;
    private const int VoucherActive = (int)FinanceVoucherStatus.Active;

    /// <summary>Opening balance already settled by Finance vouchers of party <c>{alias}.Id</c>.</summary>
    public static string OpeningSettled(int partyType, string alias) => $@"
        COALESCE((SELECT SUM(oa.AmountPaisa) FROM FinanceVoucherAllocations oa JOIN FinanceVouchers ov ON ov.Id = oa.VoucherId
                  WHERE oa.Target = {Opening} AND oa.IsReversed = 0 AND ov.PartyType = {partyType} AND ov.PartyId = {alias}.Id), 0)";

    /// <summary>
    /// Unallocated part of active vouchers (advance / on account) of party <c>{alias}.Id</c>. An allocation counts only
    /// while its Payments row exists: when the bill's owner removes it (a sale edited or cancelled after the receipt),
    /// the money stays with the party as an advance instead of disappearing.
    /// </summary>
    public static string Advance(int partyType, string alias) => $@"
        COALESCE((SELECT SUM(av.AmountPaisa - COALESCE((SELECT SUM(aa.AmountPaisa) FROM FinanceVoucherAllocations aa
                                                         WHERE aa.VoucherId = av.Id AND aa.IsReversed = 0
                                                           AND (aa.PaymentId IS NULL OR EXISTS (SELECT 1 FROM Payments ap WHERE ap.Id = aa.PaymentId))), 0))
                  FROM FinanceVouchers av WHERE av.PartyType = {partyType} AND av.PartyId = {alias}.Id AND av.Status = {VoucherActive}), 0)";

    /// <summary>Open credit notes (sale returns not yet refunded) of customer <c>c</c>.</summary>
    public const string CustomerOpenCredit = @"
        COALESCE((SELECT SUM(r.NetAmountPaisa - COALESCE((SELECT SUM(f.AmountPaisa) FROM SaleReturnRefunds f WHERE f.SaleReturnId = r.Id), 0))
                  FROM SaleReturns r WHERE r.CustomerId = c.Id AND r.Status = 1), 0)";

    /// <summary>Open debit notes (purchase returns not yet refunded) of supplier <c>s</c>.</summary>
    public const string SupplierOpenDebit = @"
        COALESCE((SELECT SUM(r.NetAmountPaisa - r.RefundReceivedPaisa) FROM PurchaseReturns r WHERE r.SupplierId = s.Id AND r.IsCancelled = 0), 0)";

    public static readonly string CustomerParties = $@"
        SELECT c.Id, c.Name, c.Phone, c.Gstin, c.IsActive,
               c.OpeningBalancePaisa - {OpeningSettled(CustomerType, "c")} AS OpeningOutstanding,
               COALESCE(c.OpeningBalanceDate, substr(c.CreatedAt, 1, 10)) AS OpeningDate,
               {CustomerOpenCredit} AS OpenNotes,
               {Advance(CustomerType, "c")} AS AdvancePaisa,
               COALESCE((SELECT SUM(-sb.BalanceDuePaisa) FROM Sales sb WHERE sb.CustomerId = c.Id AND sb.Status = {SaleActive} AND sb.BalanceDuePaisa < 0), 0) AS OverpaidPaisa
        FROM Customers c";

    public static readonly string SupplierParties = $@"
        SELECT s.Id, s.Name, s.Phone, s.Gstin, s.IsActive,
               s.OpeningBalancePaisa - {OpeningSettled(SupplierType, "s")} AS OpeningOutstanding,
               substr(s.CreatedAt, 1, 10) AS OpeningDate,
               {SupplierOpenDebit} AS OpenNotes,
               {Advance(SupplierType, "s")} AS AdvancePaisa,
               COALESCE((SELECT SUM(-pb.BalancePaisa) FROM Purchases pb WHERE pb.SupplierId = s.Id AND pb.Status = {PurchaseConfirmed} AND pb.BalancePaisa < 0), 0) AS OverpaidPaisa
        FROM Suppliers s";

    /// <summary>Open credit sales: PartyId, Id, Number, Date, DueDate, Total, Paid, Balance.</summary>
    public const string OpenSales = @"
        SELECT CustomerId AS PartyId, Id, InvoiceNumber AS Number, InvoiceDate AS DocDate, DueDate, GrandTotalPaisa AS Total,
               AmountPaidPaisa AS Paid, BalanceDuePaisa AS Balance
        FROM Sales WHERE Status = 1 AND BalanceDuePaisa > 0 AND CustomerId IS NOT NULL";

    /// <summary>Open purchase bills, same columns as <see cref="OpenSales"/>.</summary>
    public static readonly string OpenPurchases = $@"
        SELECT SupplierId AS PartyId, Id,
               COALESCE(NULLIF(SupplierInvoiceNumber, ''), PurchaseNumber, '#' || Id) AS Number, PurchaseDate AS DocDate, DueDate,
               GrandTotalPaisa AS Total, AmountPaidPaisa AS Paid, BalancePaisa AS Balance
        FROM Purchases WHERE Status = {PurchaseConfirmed} AND BalancePaisa > 0";
}
