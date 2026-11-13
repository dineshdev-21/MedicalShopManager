using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>
/// Customer and supplier outstanding, derived from source rows (ARCHITECTURE §10, lesson 7) with the same rule as the
/// Finance party ledgers: due = opening balance not yet settled + open bill balances − open credit / debit notes −
/// unallocated advances − overpayments. Open bills and the unsettled opening balance are aged by their date.
/// </summary>
public sealed class PartyReportsProvider : ReportProviderBase
{
    private const int Opening = (int)AllocationTarget.OpeningBalance;
    private const int VoucherActive = (int)FinanceVoucherStatus.Active;

    public PartyReportsProvider(DatabaseManager db) : base(db) { }

    public override IReadOnlyCollection<string> ReportIds { get; } =
        new[] { Core.Modules.Reports.ReportIds.CustomerOutstanding, Core.Modules.Reports.ReportIds.SupplierOutstanding };

    public override Task<ReportResult> RunAsync(ReportDefinition definition, ReportParameters parameters, CancellationToken cancellationToken = default) =>
        definition.Id == Core.Modules.Reports.ReportIds.CustomerOutstanding
            ? OutstandingAsync(definition, parameters, PartyType.Customer, cancellationToken)
            : OutstandingAsync(definition, parameters, PartyType.Supplier, cancellationToken);

    private static string OpeningSettled(PartyType type, string alias) => $@"
        COALESCE((SELECT SUM(oa.AmountPaisa) FROM FinanceVoucherAllocations oa JOIN FinanceVouchers ov ON ov.Id = oa.VoucherId
                  WHERE oa.Target = {Opening} AND oa.IsReversed = 0 AND ov.PartyType = {(int)type} AND ov.PartyId = {alias}.Id), 0)";

    private static string Advance(PartyType type, string alias) => $@"
        COALESCE((SELECT SUM(av.AmountPaisa - COALESCE((SELECT SUM(aa.AmountPaisa) FROM FinanceVoucherAllocations aa
                                                         WHERE aa.VoucherId = av.Id AND aa.IsReversed = 0), 0))
                  FROM FinanceVouchers av WHERE av.PartyType = {(int)type} AND av.PartyId = {alias}.Id AND av.Status = {VoucherActive}), 0)";

    private static readonly string CustomerSql = $@"
        SELECT c.Id, c.Name, c.Phone, c.Gstin,
               c.OpeningBalancePaisa - {OpeningSettled(PartyType.Customer, "c")} AS OpeningDue,
               COALESCE(c.OpeningBalanceDate, substr(c.CreatedAt, 1, 10)) AS OpeningDate,
               COALESCE((SELECT SUM(r.NetAmountPaisa - COALESCE((SELECT SUM(f.AmountPaisa) FROM SaleReturnRefunds f WHERE f.SaleReturnId = r.Id), 0))
                         FROM SaleReturns r WHERE r.CustomerId = c.Id AND r.Status = {ReturnActive}), 0)
               + {Advance(PartyType.Customer, "c")}
               + COALESCE((SELECT SUM(-sb.BalanceDuePaisa) FROM Sales sb WHERE sb.CustomerId = c.Id AND sb.Status = {SaleActive} AND sb.BalanceDuePaisa < 0), 0) AS Credits
        FROM Customers c";

    private static readonly string SupplierSql = $@"
        SELECT s.Id, s.Name, s.Phone, s.Gstin,
               s.OpeningBalancePaisa - {OpeningSettled(PartyType.Supplier, "s")} AS OpeningDue,
               substr(s.CreatedAt, 1, 10) AS OpeningDate,
               COALESCE((SELECT SUM(r.NetAmountPaisa - r.RefundReceivedPaisa) FROM PurchaseReturns r WHERE r.SupplierId = s.Id AND r.IsCancelled = 0), 0)
               + {Advance(PartyType.Supplier, "s")}
               + COALESCE((SELECT SUM(-pb.BalancePaisa) FROM Purchases pb WHERE pb.SupplierId = s.Id AND pb.Status = {PurchaseConfirmed} AND pb.BalancePaisa < 0), 0) AS Credits
        FROM Suppliers s";

    private static readonly string OpenCustomerBills = $@"
        SELECT CustomerId AS PartyId, InvoiceDate AS DocDate, BalanceDuePaisa AS Balance
        FROM Sales WHERE Status = {SaleActive} AND BalanceDuePaisa > 0 AND CustomerId IS NOT NULL";

    private static readonly string OpenSupplierBills = $@"
        SELECT SupplierId AS PartyId, PurchaseDate AS DocDate, BalancePaisa AS Balance
        FROM Purchases WHERE Status = {PurchaseConfirmed} AND BalancePaisa > 0";

    private async Task<ReportResult> OutstandingAsync(ReportDefinition d, ReportParameters p, PartyType type, CancellationToken ct)
    {
        var isCustomer = type == PartyType.Customer;
        var asOf = DateTime.Today;
        var parties = await ReportSql.QueryAsync(Db, isCustomer ? CustomerSql : SupplierSql,
            r => (Id: r.Long("Id"), Name: r.Text("Name"), Phone: Blank(r.Str("Phone")), Gstin: Blank(r.Str("Gstin")),
                OpeningDue: r.Long("OpeningDue"), OpeningDate: r.DayOrNull("OpeningDate") ?? asOf, Credits: r.Long("Credits")), ct);
        var bills = (await ReportSql.QueryAsync(Db, isCustomer ? OpenCustomerBills : OpenSupplierBills,
                r => (PartyId: r.Long("PartyId"), Date: r.Day("DocDate"), Balance: r.Long("Balance")), ct))
            .ToLookup(b => b.PartyId);

        var rows = new List<(long Net, ReportRow Row)>();
        foreach (var party in parties)
        {
            var ageing = new ReportAgeing();
            ageing.Add(party.OpeningDate, party.OpeningDue, asOf);
            foreach (var bill in bills[party.Id]) ageing.Add(bill.Date, bill.Balance, asOf);
            var credits = Math.Max(party.Credits, 0) + Math.Max(-party.OpeningDue, 0);
            var net = ageing.Total - credits;
            if (ageing.Total == 0 && credits == 0) continue;
            rows.Add((net, ReportRow.Of(party.Name, party.Phone, party.Gstin, (long)ageing.Documents, ageing.Oldest, ageing.Days0To30,
                ageing.Days31To60, ageing.Days61To90, ageing.Over90, ageing.Total, credits, net)));
        }

        var label = isCustomer ? "Customer" : "Supplier";
        var table = new ReportTable(isCustomer ? "Receivables" : "Payables", new[]
        {
            ReportColumn.Text(label, 2), ReportColumn.Text("Phone"), ReportColumn.Text("GSTIN", 1.4), ReportColumn.Count("Open bills"),
            ReportColumn.Date("Oldest due since"), ReportColumn.Money("0–30 days"), ReportColumn.Money("31–60 days"), ReportColumn.Money("61–90 days"),
            ReportColumn.Money("Over 90 days"), ReportColumn.Money("Total due"), ReportColumn.Money(isCustomer ? "Credit notes / advance" : "Debit notes / advance"),
            ReportColumn.Money("Net due")
        }, rows.OrderByDescending(r => r.Net).Select(r => r.Row).ToList());

        return Result(d, p with { AsOf = asOf }, new[] { table }, new[]
        {
            new ReportStat(isCustomer ? "Customers with dues" : "Suppliers with dues", (long)rows.Count(r => r.Net > 0), ReportColumnKind.Integer),
            new ReportStat(isCustomer ? "Total receivable" : "Total payable", table.Total("Net due")),
            new ReportStat("Over 90 days", table.Total("Over 90 days"))
        }, new[]
        {
            isCustomer
                ? "Walk-in bills without a customer cannot carry a due and are not listed. Ageing counts days since the bill date."
                : "Ageing counts days since the purchase bill date (see the purchase register for due dates)."
        });
    }
}
