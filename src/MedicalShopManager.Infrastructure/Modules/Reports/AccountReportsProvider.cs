using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>
/// Day book, cash book and expense summary. Money movements follow the Finance module's ledger rules: Payments rows
/// written by a Finance voucher (VoucherId set) are allocation records, so the voucher is counted instead; credit
/// (method 4) moves no money; cancelled expenses / vouchers / movements are ignored.
/// </summary>
public sealed class AccountReportsProvider : ReportProviderBase
{
    private const int Cash = (int)PaymentMethod.Cash;
    private const int Credit = (int)PaymentMethod.Credit;
    private const int SalePayment = (int)PaymentType.SalePayment;
    private const int ExpensePayment = (int)PaymentType.ExpensePayment;
    private const int Refund = (int)PaymentType.CustomerRefund;
    private const int VoucherActive = (int)FinanceVoucherStatus.Active;
    private const int Receipt = (int)FinanceVoucherKind.Receipt;
    private const int CashMode = (int)FinancePaymentMode.Cash;
    private const int CashAccount = (int)FinanceAccountType.Cash;

    public AccountReportsProvider(DatabaseManager db) : base(db) { }

    public override IReadOnlyCollection<string> ReportIds { get; } = new[]
    {
        Core.Modules.Reports.ReportIds.DayBook, Core.Modules.Reports.ReportIds.CashBook, Core.Modules.Reports.ReportIds.ExpenseSummary
    };

    public override Task<ReportResult> RunAsync(ReportDefinition definition, ReportParameters parameters, CancellationToken cancellationToken = default) =>
        definition.Id switch
        {
            Core.Modules.Reports.ReportIds.DayBook => DayBookAsync(definition, parameters, cancellationToken),
            Core.Modules.Reports.ReportIds.CashBook => CashBookAsync(definition, parameters, cancellationToken),
            _ => ExpenseSummaryAsync(definition, parameters, cancellationToken)
        };

    // ───────────────────────── Day book ─────────────────────────

    private static readonly string DayBookSql = $@"
        SELECT s.InvoiceDate AS At, 1 AS Ord, 'Sale' AS Kind, s.InvoiceNumber AS Number, COALESCE(NULLIF(TRIM(s.CustomerName), ''), 'Walk-in') AS Party,
               s.PaymentMethod AS Method, NULL AS VoucherMode, s.GrandTotalPaisa AS Amount, 0 AS InP, 0 AS OutP, s.Id AS Sid
        FROM Sales s WHERE s.Status = {SaleActive} AND s.InvoiceDate >= @From AND s.InvoiceDate < @ToEx
        UNION ALL
        SELECT r.ReturnDate, 2, 'Sale return', r.ReturnNumber, COALESCE(NULLIF(TRIM(r.CustomerName), ''), 'Walk-in'), NULL, NULL,
               r.NetAmountPaisa, 0, 0, r.Id
        FROM SaleReturns r WHERE r.Status = {ReturnActive} AND r.ReturnDate >= @From AND r.ReturnDate < @ToEx
        UNION ALL
        SELECT pu.PurchaseDate, 3, 'Purchase', COALESCE(NULLIF(pu.SupplierInvoiceNumber, ''), pu.PurchaseNumber, '#' || pu.Id), pu.SupplierName,
               NULL, NULL, pu.GrandTotalPaisa, 0, 0, pu.Id
        FROM Purchases pu WHERE pu.Status = {PurchaseConfirmed} AND pu.PurchaseDate >= @From AND pu.PurchaseDate < @ToEx
        UNION ALL
        SELECT pr.ReturnDate, 4, 'Purchase return', pr.ReturnNumber, pr.SupplierName, NULL, NULL, pr.NetAmountPaisa, 0, 0, pr.Id
        FROM PurchaseReturns pr WHERE pr.IsCancelled = 0 AND pr.ReturnDate >= @From AND pr.ReturnDate < @ToEx
        UNION ALL
        SELECT p.PaymentDate, 5,
               CASE p.PaymentType WHEN {SalePayment} THEN 'Received (bill)' WHEN {Refund} THEN 'Refund paid' ELSE 'Paid to supplier' END,
               p.TransactionReference, COALESCE(s.CustomerName, pu.SupplierName), p.PaymentMethod, NULL, 0,
               CASE WHEN p.PaymentType = {SalePayment} THEN p.AmountPaisa ELSE 0 END,
               CASE WHEN p.PaymentType = {SalePayment} THEN 0 ELSE p.AmountPaisa END, p.Id
        FROM Payments p
        LEFT JOIN Sales s ON p.PaymentType = {SalePayment} AND s.Id = p.TransactionId
        LEFT JOIN Purchases pu ON p.PaymentType NOT IN ({SalePayment}, {Refund}) AND pu.Id = p.TransactionId
        WHERE p.VoucherId IS NULL AND p.PaymentType <> {ExpensePayment} AND p.PaymentMethod <> {Credit}
          AND p.PaymentDate >= @From AND p.PaymentDate < @ToEx
        UNION ALL
        SELECT e.ExpenseDate, 6, 'Expense', COALESCE(e.ExpenseNumber, '#' || e.Id), COALESCE(e.PayeeName, c.Name, 'Expense'),
               e.PaymentMethod, NULL, 0, 0, e.AmountPaisa, e.Id
        FROM Expenses e LEFT JOIN FinanceExpenseCategories c ON c.Id = e.CategoryId
        WHERE e.IsCancelled = 0 AND e.PaymentMethod <> {Credit} AND e.ExpenseDate >= @From AND e.ExpenseDate < @ToEx
        UNION ALL
        SELECT v.VoucherDate, 7, CASE v.Kind WHEN {Receipt} THEN 'Payment in' ELSE 'Payment out' END, v.VoucherNumber, v.PartyName,
               NULL, v.Mode, 0,
               CASE v.Kind WHEN {Receipt} THEN v.AmountPaisa ELSE 0 END, CASE v.Kind WHEN {Receipt} THEN 0 ELSE v.AmountPaisa END, v.Id
        FROM FinanceVouchers v WHERE v.Status = {VoucherActive} AND v.VoucherDate >= @From AND v.VoucherDate < @ToEx
        ORDER BY 1, 2, 11";

    private async Task<ReportResult> DayBookAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var rows = await ReportSql.QueryAsync(Db, DayBookSql, r =>
        {
            var method = r.LongOrNull("Method");
            var voucherMode = r.LongOrNull("VoucherMode");
            var mode = method is { } m ? ModeLabel(m) : voucherMode is { } v ? ((FinancePaymentMode)v).ToLabel() : null;
            var amount = r.Long("Amount");
            var inP = r.Long("InP");
            var outP = r.Long("OutP");
            return ReportRow.Of(r.Day("At"), r.Text("Kind"), Blank(r.Str("Number")), Blank(r.Str("Party")), mode,
                amount == 0 ? null : amount, inP == 0 ? null : inP, outP == 0 ? null : outP);
        }, ct, ReportSql.Range(p));

        var table = new ReportTable("Day book", new[]
        {
            ReportColumn.Date("Date"), ReportColumn.Text("Voucher type", 1.2), ReportColumn.Text("Number"), ReportColumn.Text("Party", 2),
            ReportColumn.Text("Mode", 0.8), ReportColumn.Money("Document value", total: false), ReportColumn.Money("Money in"), ReportColumn.Money("Money out")
        }, rows);

        long SumOf(string kind) => rows.Where(r => (string?)r.Values[1] == kind).Sum(r => r.Values[5] as long? ?? 0);
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Sales", SumOf("Sale")),
            new ReportStat("Sale returns", SumOf("Sale return")),
            new ReportStat("Purchases", SumOf("Purchase")),
            new ReportStat("Money in", table.Total("Money in")),
            new ReportStat("Money out", table.Total("Money out"))
        }, new[] { "Document value is the bill / note amount; money in and out are the payments actually received or made (credit is not money)." });
    }

    // ───────────────────────── Cash book ─────────────────────────

    /// <summary>Cash entries: EntryDate, Kind, Number, Party, Narration, InP, OutP, Sid.</summary>
    private static readonly string CashEntriesSql = $@"
        SELECT p.PaymentDate AS EntryDate, 1 AS Ord,
               CASE p.PaymentType WHEN {SalePayment} THEN 'Sale receipt' WHEN {Refund} THEN 'Refund' ELSE 'Supplier payment' END AS Kind,
               p.TransactionReference AS Number, COALESCE(s.CustomerName, pu.SupplierName) AS Party, p.Notes AS Narration,
               CASE WHEN p.PaymentType = {SalePayment} THEN p.AmountPaisa ELSE 0 END AS InP,
               CASE WHEN p.PaymentType = {SalePayment} THEN 0 ELSE p.AmountPaisa END AS OutP, p.Id AS Sid
        FROM Payments p
        LEFT JOIN Sales s ON p.PaymentType = {SalePayment} AND s.Id = p.TransactionId
        LEFT JOIN Purchases pu ON p.PaymentType NOT IN ({SalePayment}, {Refund}) AND pu.Id = p.TransactionId
        WHERE p.VoucherId IS NULL AND p.PaymentType <> {ExpensePayment} AND p.PaymentMethod = {Cash}
        UNION ALL
        SELECT e.ExpenseDate, 2, 'Expense', COALESCE(e.ExpenseNumber, '#' || e.Id), e.PayeeName,
               COALESCE(c.Name, 'Expense') || CASE WHEN e.Description IS NULL OR e.Description = '' THEN '' ELSE ': ' || e.Description END,
               0, e.AmountPaisa, e.Id
        FROM Expenses e LEFT JOIN FinanceExpenseCategories c ON c.Id = e.CategoryId
        WHERE e.IsCancelled = 0 AND e.PaymentMethod = {Cash}
        UNION ALL
        SELECT v.VoucherDate, 3, CASE v.Kind WHEN {Receipt} THEN 'Payment in' ELSE 'Payment out' END, v.VoucherNumber, v.PartyName,
               COALESCE(v.Reference, v.Notes),
               CASE v.Kind WHEN {Receipt} THEN v.AmountPaisa ELSE 0 END, CASE v.Kind WHEN {Receipt} THEN 0 ELSE v.AmountPaisa END, v.Id
        FROM FinanceVouchers v WHERE v.Status = {VoucherActive} AND v.Mode = {CashMode}
        UNION ALL
        SELECT mv.MovementDate, 4, 'Cash added', mv.Reference, NULL, mv.Notes, mv.AmountPaisa, 0, mv.Id
        FROM FinanceMovements mv JOIN FinanceAccounts a ON a.Id = mv.ToAccountId
        WHERE mv.IsCancelled = 0 AND a.AccountType = {CashAccount}
        UNION ALL
        SELECT mv.MovementDate, 4, 'Cash taken out', mv.Reference, NULL, mv.Notes, 0, mv.AmountPaisa, mv.Id
        FROM FinanceMovements mv JOIN FinanceAccounts a ON a.Id = mv.FromAccountId
        WHERE mv.IsCancelled = 0 AND a.AccountType = {CashAccount}
        UNION ALL
        SELECT a.OpeningDate, 0, 'Opening balance', NULL, a.Name, 'Cash account opened', a.OpeningBalancePaisa, 0, a.Id
        FROM FinanceAccounts a WHERE a.AccountType = {CashAccount} AND a.OpeningBalancePaisa <> 0";

    private async Task<ReportResult> CashBookAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var opening = await ReportSql.ScalarLongAsync(Db,
            $"SELECT COALESCE(SUM(InP - OutP), 0) FROM ({CashEntriesSql}) WHERE EntryDate < @From", ct, ReportSql.Range(p));
        var entries = await ReportSql.QueryAsync(Db,
            $"SELECT * FROM ({CashEntriesSql}) WHERE EntryDate >= @From AND EntryDate < @ToEx ORDER BY substr(EntryDate, 1, 10), Ord, Sid",
            r => (Date: r.Day("EntryDate"), Kind: r.Text("Kind"), Number: Blank(r.Str("Number")), Party: Blank(r.Str("Party")),
                Narration: Blank(r.Str("Narration")), In: r.Long("InP"), Out: r.Long("OutP")),
            ct, ReportSql.Range(p));

        var rows = new List<ReportRow> { ReportRow.Opening(p.From.Date, "Opening balance", null, null, null, null, null, opening) };
        var balance = opening;
        foreach (var e in entries)
        {
            balance += e.In - e.Out;
            rows.Add(ReportRow.Of(e.Date, e.Kind, e.Number, e.Party, e.Narration, e.In == 0 ? null : e.In, e.Out == 0 ? null : e.Out, balance));
        }
        var totalIn = entries.Sum(e => e.In);
        var totalOut = entries.Sum(e => e.Out);
        rows.Add(ReportRow.Subtotal(p.To.Date, "Closing balance", null, null, null, totalIn, totalOut, balance));

        var table = new ReportTable("Cash book", new[]
        {
            ReportColumn.Date("Date"), ReportColumn.Text("Particulars", 1.2), ReportColumn.Text("Voucher no."), ReportColumn.Text("Party", 1.6),
            ReportColumn.Text("Narration", 2), ReportColumn.Money("Cash in"), ReportColumn.Money("Cash out"), ReportColumn.Money("Balance", total: false)
        }, rows) { ShowTotals = false };
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Opening cash", opening),
            new ReportStat("Cash in", totalIn),
            new ReportStat("Cash out", totalOut),
            new ReportStat("Closing cash", balance)
        }, new[] { "Cash book lists transactions paid or received in cash, plus transfers into and out of cash accounts (Finance ▸ Cash & Bank)." });
    }

    // ───────────────────────── Expenses ─────────────────────────

    private async Task<ReportResult> ExpenseSummaryAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var expenses = await ReportSql.QueryAsync(Db, @"
            SELECT e.ExpenseDate, COALESCE(e.ExpenseNumber, '#' || e.Id) AS Number, c.Name AS CategoryName, e.Category,
                   e.PayeeName, e.Description, e.PaymentMethod, e.AmountPaisa, e.TaxablePaisa, e.CgstPaisa + e.SgstPaisa + e.IgstPaisa AS Gst
            FROM Expenses e LEFT JOIN FinanceExpenseCategories c ON c.Id = e.CategoryId
            WHERE e.IsCancelled = 0 AND e.ExpenseDate >= @From AND e.ExpenseDate < @ToEx
            ORDER BY e.ExpenseDate, e.Id",
            r =>
            {
                var amount = r.Long("AmountPaisa");
                var gst = r.Long("Gst");
                var taxable = r.Long("TaxablePaisa") > 0 ? r.Long("TaxablePaisa") : amount - gst;
                var category = Blank(r.Str("CategoryName")) ?? ((ExpenseCategory)r.Long("Category")).ToString();
                return (Date: r.Day("ExpenseDate"), Number: r.Text("Number"), Category: category, Payee: Blank(r.Str("PayeeName")),
                    Description: Blank(r.Str("Description")), Mode: ModeLabel(r.Long("PaymentMethod")), Taxable: taxable, Gst: gst, Amount: amount);
            }, ct, ReportSql.Range(p));

        var byCategory = expenses.GroupBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Category: g.First().Category, Count: (long)g.Count(), Taxable: g.Sum(e => e.Taxable), Gst: g.Sum(e => e.Gst), Amount: g.Sum(e => e.Amount)))
            .OrderByDescending(g => g.Amount).ToList();
        var total = expenses.Sum(e => e.Amount);

        var summary = new ReportTable("By category", new[]
        {
            ReportColumn.Text("Category", 2), ReportColumn.Count("Vouchers"), ReportColumn.Money("Taxable"), ReportColumn.Money("GST"),
            ReportColumn.Money("Amount"), ReportColumn.Percent("Share %")
        }, byCategory.Select(g => ReportRow.Of(g.Category, g.Count, g.Taxable, g.Gst, g.Amount, ReportMath.SharePercent(g.Amount, total))).ToList());
        var details = new ReportTable("Expense vouchers", new[]
        {
            ReportColumn.Date("Date"), ReportColumn.Text("Number"), ReportColumn.Text("Category", 1.4), ReportColumn.Text("Payee", 1.5),
            ReportColumn.Text("Description", 2), ReportColumn.Text("Mode", 0.7), ReportColumn.Money("Taxable"), ReportColumn.Money("GST"), ReportColumn.Money("Amount")
        }, expenses.Select(e => ReportRow.Of(e.Date, e.Number, e.Category, e.Payee, e.Description, e.Mode, e.Taxable, e.Gst, e.Amount)).ToList());

        return Result(d, p, new[] { summary, details }, new[]
        {
            new ReportStat("Expenses", total),
            new ReportStat("GST on expenses", expenses.Sum(e => e.Gst)),
            new ReportStat("Vouchers", (long)expenses.Count, ReportColumnKind.Integer)
        }, new[] { "Cancelled expenses are excluded." });
    }
}
