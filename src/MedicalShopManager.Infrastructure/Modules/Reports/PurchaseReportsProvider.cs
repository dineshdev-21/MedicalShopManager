using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>Purchase register and purchase / expiry returns (debit notes), read-only over the Purchases module's tables.</summary>
public sealed class PurchaseReportsProvider : ReportProviderBase
{
    public PurchaseReportsProvider(DatabaseManager db) : base(db) { }

    public override IReadOnlyCollection<string> ReportIds { get; } =
        new[] { Core.Modules.Reports.ReportIds.PurchaseRegister, Core.Modules.Reports.ReportIds.PurchaseReturns };

    public override Task<ReportResult> RunAsync(ReportDefinition definition, ReportParameters parameters, CancellationToken cancellationToken = default) =>
        definition.Id == Core.Modules.Reports.ReportIds.PurchaseRegister
            ? PurchaseRegisterAsync(definition, parameters, cancellationToken)
            : PurchaseReturnsAsync(definition, parameters, cancellationToken);

    private async Task<ReportResult> PurchaseRegisterAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var unpaidOnly = p.Choice(d, ReportChoiceKeys.Status) == ReportChoiceKeys.Unpaid ? 1 : 0;
        var rows = await ReportSql.QueryAsync(Db, $@"
            SELECT pu.PurchaseDate, COALESCE(pu.PurchaseNumber, '#' || pu.Id) AS PurchaseNumber, pu.SupplierInvoiceNumber,
                   COALESCE(su.Name, pu.SupplierName) AS SupplierName, su.Gstin, pu.DueDate,
                   pu.TaxablePaisa, pu.CgstPaisa, pu.SgstPaisa, pu.IgstPaisa, pu.OtherChargesPaisa, pu.RoundOffPaisa,
                   pu.GrandTotalPaisa, pu.AmountPaidPaisa, pu.BalancePaisa
            FROM Purchases pu
            LEFT JOIN Suppliers su ON su.Id = pu.SupplierId
            WHERE pu.Status = {PurchaseConfirmed} AND pu.PurchaseDate >= @From AND pu.PurchaseDate < @ToEx
              AND (@Unpaid = 0 OR pu.BalancePaisa > 0)
            ORDER BY pu.PurchaseDate, pu.Id",
            r => ReportRow.Of(r.Day("PurchaseDate"), r.Text("PurchaseNumber"), r.Text("SupplierInvoiceNumber"), r.Text("SupplierName"),
                Blank(r.Str("Gstin")), r.DayOrNull("DueDate"), r.Long("TaxablePaisa"), r.Long("CgstPaisa"), r.Long("SgstPaisa"),
                r.Long("IgstPaisa"), r.Long("OtherChargesPaisa"), r.Long("RoundOffPaisa"), r.Long("GrandTotalPaisa"),
                r.Long("AmountPaidPaisa"), r.Long("BalancePaisa")),
            ct, ReportSql.Range(p, ("@Unpaid", unpaidOnly)));

        var table = new ReportTable("Purchase register", new[]
        {
            ReportColumn.Date("Date"), ReportColumn.Text("Purchase no."), ReportColumn.Text("Supplier bill no."), ReportColumn.Text("Supplier", 1.8),
            ReportColumn.Text("GSTIN", 1.4), ReportColumn.Date("Due date"), ReportColumn.Money("Taxable"), ReportColumn.Money("CGST"),
            ReportColumn.Money("SGST"), ReportColumn.Money("IGST"), ReportColumn.Money("Other charges"), ReportColumn.Money("Round off"),
            ReportColumn.Money("Bill amount"), ReportColumn.Money("Paid"), ReportColumn.Money("Balance")
        }, rows);
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Bills", (long)rows.Count, ReportColumnKind.Integer),
            new ReportStat("Purchases", table.Total("Bill amount")),
            new ReportStat("Input GST", table.Total("CGST") + table.Total("SGST") + table.Total("IGST")),
            new ReportStat("Balance due", table.Total("Balance"))
        }, new[] { "Only confirmed purchase bills are listed (drafts and cancelled bills are excluded)." });
    }

    private async Task<ReportResult> PurchaseReturnsAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var choice = p.Choice(d, ReportChoiceKeys.Status);
        var cancelled = choice switch { ReportChoiceKeys.Active => 0, ReportChoiceKeys.Cancelled => 1, _ => -1 };
        var rows = await ReportSql.QueryAsync(Db, @"
            SELECT r.ReturnDate, r.ReturnNumber, r.Kind, COALESCE(su.Name, r.SupplierName) AS SupplierName, su.Gstin,
                   COALESCE(r.PurchaseReference, pu.SupplierInvoiceNumber) AS AgainstBill, r.Reason,
                   r.TaxablePaisa, r.CgstPaisa, r.SgstPaisa, r.IgstPaisa, r.NetAmountPaisa, r.RefundReceivedPaisa, r.IsCancelled
            FROM PurchaseReturns r
            LEFT JOIN Suppliers su ON su.Id = r.SupplierId
            LEFT JOIN Purchases pu ON pu.Id = r.PurchaseId
            WHERE r.ReturnDate >= @From AND r.ReturnDate < @ToEx AND (@Cancelled < 0 OR r.IsCancelled = @Cancelled)
            ORDER BY r.ReturnDate, r.Id",
            r =>
            {
                var isCancelled = r.Bool("IsCancelled");
                var net = r.Long("NetAmountPaisa");
                var refund = r.Long("RefundReceivedPaisa");
                return ReportRow.Of(r.Day("ReturnDate"), r.Text("ReturnNumber"), ((PurchaseReturnKind)r.Long("Kind")).ToLabel(),
                    r.Text("SupplierName"), Blank(r.Str("Gstin")), Blank(r.Str("AgainstBill")), ((PurchaseReturnReason)r.Long("Reason")).ToLabel(),
                    r.Long("TaxablePaisa"), r.Long("CgstPaisa"), r.Long("SgstPaisa"), r.Long("IgstPaisa"), net, refund,
                    isCancelled ? 0L : Math.Max(net - refund, 0), isCancelled ? "Cancelled" : "Active");
            },
            ct, ReportSql.Range(p, ("@Cancelled", cancelled)));

        var table = new ReportTable("Purchase returns", new[]
        {
            ReportColumn.Date("Date"), ReportColumn.Text("Debit note"), ReportColumn.Text("Type"), ReportColumn.Text("Supplier", 1.8),
            ReportColumn.Text("GSTIN", 1.4), ReportColumn.Text("Against bill"), ReportColumn.Text("Reason", 1.2), ReportColumn.Money("Taxable"),
            ReportColumn.Money("CGST"), ReportColumn.Money("SGST"), ReportColumn.Money("IGST"), ReportColumn.Money("Debit note value"),
            ReportColumn.Money("Refund received"), ReportColumn.Money("Pending"), ReportColumn.Text("Status", 0.8)
        }, rows);
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Debit notes", (long)rows.Count, ReportColumnKind.Integer),
            new ReportStat("Debit note value", table.Total("Debit note value")),
            new ReportStat("Pending from suppliers", table.Total("Pending"))
        });
    }
}
