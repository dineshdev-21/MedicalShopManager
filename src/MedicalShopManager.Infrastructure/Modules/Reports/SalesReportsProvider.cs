using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>Sales register, sale returns register and doctor-wise sales (read-only over Billing / SalesReturns / Doctors tables).</summary>
public sealed class SalesReportsProvider : ReportProviderBase
{
    public SalesReportsProvider(DatabaseManager db) : base(db) { }

    public override IReadOnlyCollection<string> ReportIds { get; } =
        new[] { Core.Modules.Reports.ReportIds.SalesRegister, Core.Modules.Reports.ReportIds.SaleReturns, Core.Modules.Reports.ReportIds.DoctorSales };

    public override Task<ReportResult> RunAsync(ReportDefinition definition, ReportParameters parameters, CancellationToken cancellationToken = default) =>
        definition.Id switch
        {
            Core.Modules.Reports.ReportIds.SalesRegister => SalesRegisterAsync(definition, parameters, cancellationToken),
            Core.Modules.Reports.ReportIds.SaleReturns => SaleReturnsAsync(definition, parameters, cancellationToken),
            _ => DoctorSalesAsync(definition, parameters, cancellationToken)
        };

    private const string ItemSums = @"
        LEFT JOIN (SELECT SaleId, COUNT(*) AS Items, SUM(TaxableValuePaisa) AS Taxable, SUM(CgstAmountPaisa) AS Cgst,
                          SUM(SgstAmountPaisa) AS Sgst, SUM(IgstAmountPaisa) AS Igst, SUM(CogsPaisa) AS Cost
                   FROM SaleItems GROUP BY SaleId) t ON t.SaleId = s.Id";

    private async Task<ReportResult> SalesRegisterAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var status = StatusFilter(p.Choice(d, ReportChoiceKeys.Status), SaleActive, SaleCancelled);
        var rows = await ReportSql.QueryAsync(Db, $@"
            SELECT s.InvoiceNumber, s.InvoiceDate, s.CustomerName, s.CustomerPhone,
                   COALESCE(NULLIF(TRIM(s.CustomerGstin), ''), c.Gstin) AS Gstin, COALESCE(t.Items, 0) AS Items,
                   COALESCE(t.Taxable, 0) AS Taxable, COALESCE(t.Cgst, 0) AS Cgst, COALESCE(t.Sgst, 0) AS Sgst, COALESCE(t.Igst, 0) AS Igst,
                   s.RoundOffPaisa, s.GrandTotalPaisa, s.AmountPaidPaisa, s.BalanceDuePaisa, s.PaymentMethod, s.Status
            FROM Sales s
            LEFT JOIN Customers c ON c.Id = s.CustomerId
            {ItemSums}
            WHERE s.InvoiceDate >= @From AND s.InvoiceDate < @ToEx AND (@Status = 0 OR s.Status = @Status)
            ORDER BY s.InvoiceDate, s.Id",
            r => ReportRow.Of(r.Day("InvoiceDate"), r.Text("InvoiceNumber"), ReportSql.PartyOrWalkIn(r.Str("CustomerName")),
                Blank(r.Str("CustomerPhone")), Blank(r.Str("Gstin")), r.Long("Items"), r.Long("Taxable"), r.Long("Cgst"), r.Long("Sgst"),
                r.Long("Igst"), r.Long("RoundOffPaisa"), r.Long("GrandTotalPaisa"), r.Long("AmountPaidPaisa"), r.Long("BalanceDuePaisa"),
                ModeLabel(r.Long("PaymentMethod")), r.Long("Status") == SaleCancelled ? "Cancelled" : "Active"),
            ct, ReportSql.Range(p, ("@Status", status)));

        var table = new ReportTable("Sales register", new[]
        {
            ReportColumn.Date("Date"), ReportColumn.Text("Bill no."), ReportColumn.Text("Customer", 1.8), ReportColumn.Text("Phone"),
            ReportColumn.Text("GSTIN", 1.4), ReportColumn.Count("Items"), ReportColumn.Money("Taxable"), ReportColumn.Money("CGST"),
            ReportColumn.Money("SGST"), ReportColumn.Money("IGST"), ReportColumn.Money("Round off"), ReportColumn.Money("Bill amount"),
            ReportColumn.Money("Paid"), ReportColumn.Money("Due"), ReportColumn.Text("Mode", 0.7), ReportColumn.Text("Status", 0.8)
        }, rows);
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Bills", (long)rows.Count, ReportColumnKind.Integer),
            new ReportStat("Sales (bill amount)", table.Total("Bill amount")),
            new ReportStat("GST", table.Total("CGST") + table.Total("SGST") + table.Total("IGST")),
            new ReportStat("Due", table.Total("Due"))
        });
    }

    private async Task<ReportResult> SaleReturnsAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var status = StatusFilter(p.Choice(d, ReportChoiceKeys.Status), ReturnActive, ReturnCancelled);
        var rows = await ReportSql.QueryAsync(Db, @"
            SELECT r.ReturnDate, r.ReturnNumber, r.CreditNoteNumber, r.SaleInvoiceNumber, r.CustomerName, r.CustomerGstin,
                   r.TaxablePaisa, r.CgstPaisa, r.SgstPaisa, r.IgstPaisa, r.NetAmountPaisa, r.RefundMode, r.Status,
                   COALESCE((SELECT SUM(f.AmountPaisa) FROM SaleReturnRefunds f WHERE f.SaleReturnId = r.Id), 0) AS Refunded
            FROM SaleReturns r
            WHERE r.ReturnDate >= @From AND r.ReturnDate < @ToEx AND (@Status = 0 OR r.Status = @Status)
            ORDER BY r.ReturnDate, r.Id",
            r =>
            {
                var active = r.Long("Status") != ReturnCancelled;
                var net = r.Long("NetAmountPaisa");
                var refunded = r.Long("Refunded");
                return ReportRow.Of(r.Day("ReturnDate"), r.Text("ReturnNumber"), r.Text("CreditNoteNumber"),
                    Blank(r.Str("SaleInvoiceNumber")) ?? "Without bill", ReportSql.PartyOrWalkIn(r.Str("CustomerName")),
                    Blank(r.Str("CustomerGstin")), r.Long("TaxablePaisa"), r.Long("CgstPaisa"), r.Long("SgstPaisa"), r.Long("IgstPaisa"),
                    net, refunded, active ? Math.Max(net - refunded, 0) : 0L,
                    r.Long("RefundMode") == 2 ? "Credit to customer" : "Refund", active ? "Active" : "Cancelled");
            },
            ct, ReportSql.Range(p, ("@Status", status)));

        var table = new ReportTable("Sale returns", new[]
        {
            ReportColumn.Date("Date"), ReportColumn.Text("Return no."), ReportColumn.Text("Credit note"), ReportColumn.Text("Against bill"),
            ReportColumn.Text("Customer", 1.8), ReportColumn.Text("GSTIN", 1.4), ReportColumn.Money("Taxable"), ReportColumn.Money("CGST"),
            ReportColumn.Money("SGST"), ReportColumn.Money("IGST"), ReportColumn.Money("Credit note value"), ReportColumn.Money("Refunded"),
            ReportColumn.Money("Open credit"), ReportColumn.Text("Settlement"), ReportColumn.Text("Status", 0.8)
        }, rows);
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Returns", (long)rows.Count, ReportColumnKind.Integer),
            new ReportStat("Credit note value", table.Total("Credit note value")),
            new ReportStat("Open credit", table.Total("Open credit"))
        });
    }

    private async Task<ReportResult> DoctorSalesAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var rows = await ReportSql.QueryAsync(Db, $@"
            SELECT COALESCE('id:' || s.DoctorId, 'name:' || LOWER(TRIM(s.DoctorName))) AS DoctorKey,
                   MAX(COALESCE(doc.Name, s.DoctorName)) AS DoctorName,
                   MAX(COALESCE(NULLIF(doc.RegistrationNo, ''), s.DoctorRegNo)) AS RegNo,
                   MAX(doc.Specialization) AS Specialization,
                   COUNT(*) AS Bills,
                   COUNT(DISTINCT COALESCE(NULLIF(TRIM(s.PatientName), ''), NULLIF(TRIM(s.CustomerName), ''), 'bill:' || s.Id)) AS Patients,
                   SUM(s.GrandTotalPaisa) AS SalesValue, SUM(COALESCE(t.Taxable, 0)) AS Taxable, SUM(COALESCE(t.Cost, 0)) AS Cost
            FROM Sales s
            LEFT JOIN Doctors doc ON doc.Id = s.DoctorId
            {ItemSums}
            WHERE s.Status = {SaleActive} AND s.InvoiceDate >= @From AND s.InvoiceDate < @ToEx
              AND (s.DoctorId IS NOT NULL OR (s.DoctorName IS NOT NULL AND TRIM(s.DoctorName) <> ''))
            GROUP BY DoctorKey
            ORDER BY SalesValue DESC, DoctorName",
            r =>
            {
                var taxable = r.Long("Taxable");
                var profit = taxable - r.Long("Cost");
                return ReportRow.Of(r.Text("DoctorName"), Blank(r.Str("RegNo")), Blank(r.Str("Specialization")), r.Long("Bills"),
                    r.Long("Patients"), r.Long("SalesValue"), taxable, r.Long("Cost"), profit, ReportMath.MarginPercent(profit, taxable));
            },
            ct, ReportSql.Range(p));

        var table = new ReportTable("Doctor-wise sales", new[]
        {
            ReportColumn.Text("Doctor", 2), ReportColumn.Text("Reg. no."), ReportColumn.Text("Specialization", 1.3), ReportColumn.Count("Bills"),
            ReportColumn.Count("Patients"), ReportColumn.Money("Sales value"), ReportColumn.Money("Taxable"), ReportColumn.Money("Cost"),
            ReportColumn.Money("Profit"), ReportColumn.Percent("Margin %")
        }, rows);
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Doctors", (long)rows.Count, ReportColumnKind.Integer),
            new ReportStat("Prescription sales", table.Total("Sales value")),
            new ReportStat("Profit", table.Total("Profit"))
        }, new[] { "Bills without a doctor are not listed. Returns are not deducted. Profit = taxable value − cost recorded at billing." });
    }
}
