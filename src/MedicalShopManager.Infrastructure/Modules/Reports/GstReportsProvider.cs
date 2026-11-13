using System.Globalization;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>
/// GSTR-1, GSTR-3B summary and HSN-wise summary. Reads active sales / credit notes (Billing, SalesReturns) and
/// confirmed purchases / debit notes (Purchases); the pure builders in Core do the classification.
/// </summary>
public sealed class GstReportsProvider : ReportProviderBase
{
    private const string StateCodeKey = "ControlCenter.Business.StateCode";
    private const string ShopGstinKey = "ShopGstin";

    private readonly IAppSettingsRepository _settings;

    public GstReportsProvider(DatabaseManager db, IAppSettingsRepository settings) : base(db) => _settings = settings;

    public override IReadOnlyCollection<string> ReportIds { get; } =
        new[] { Core.Modules.Reports.ReportIds.Gstr1, Core.Modules.Reports.ReportIds.Gstr3b, Core.Modules.Reports.ReportIds.HsnSummary };

    public override async Task<ReportResult> RunAsync(ReportDefinition definition, ReportParameters parameters, CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var context = await LoadContextAsync(notes);
        return definition.Id switch
        {
            Core.Modules.Reports.ReportIds.Gstr1 => await Gstr1Async(definition, parameters, context, notes, cancellationToken),
            Core.Modules.Reports.ReportIds.Gstr3b => await Gstr3bAsync(definition, parameters, context, notes, cancellationToken),
            _ => await HsnAsync(definition, parameters, context, notes, cancellationToken)
        };
    }

    /// <summary>Shop state (Control Center business profile, else the shop GSTIN), B2CL limit and UQC (migration 18 settings).</summary>
    public async Task<GstReturnContext> LoadContextAsync(ICollection<string> notes)
    {
        var state = (await _settings.GetValueAsync(StateCodeKey))?.Trim();
        if (string.IsNullOrEmpty(state)) state = ReportFormat.StateOfGstin(await _settings.GetValueAsync(ShopGstinKey));
        if (string.IsNullOrEmpty(state))
        {
            notes.Add("The shop's state is not set (Control Center ▸ Business Profile), so place of supply is blank for walk-in sales.");
            state = string.Empty;
        }
        var threshold = long.TryParse(await _settings.GetValueAsync(Migration0018Reports.B2clThresholdKey), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var t) && t > 0 ? t : GstReturnContext.DefaultB2clThresholdPaisa;
        var uqc = (await _settings.GetValueAsync(Migration0018Reports.HsnUqcKey))?.Trim();
        return new GstReturnContext(state, threshold, string.IsNullOrEmpty(uqc) ? "NOS" : uqc.ToUpperInvariant());
    }

    private async Task<ReportResult> Gstr1Async(ReportDefinition d, ReportParameters p, GstReturnContext context, List<string> notes, CancellationToken ct)
    {
        var invoices = await InvoiceLinesAsync(p, ct);
        var creditNotes = await NoteLinesAsync(p, ct);
        var hsn = await SaleHsnLinesAsync(p, ct);
        var documents = await DocumentsAsync(p, ct);
        var sheets = Gstr1Builder.Build(invoices, creditNotes, hsn, documents, context, notes);
        AddHsnWarning(hsn, notes);

        var taxable = invoices.Where(i => i.WithGst && i.Rate > 0).Sum(i => i.TaxablePaisa);
        var tax = invoices.Sum(i => i.TaxPaisa);
        return Result(d, p, sheets, new[]
        {
            new ReportStat("Invoices", (long)invoices.Select(i => i.SaleId).Distinct().Count(), ReportColumnKind.Integer),
            new ReportStat("Taxable value (sales)", taxable),
            new ReportStat("Tax on sales", tax),
            new ReportStat("Credit notes (taxable)", creditNotes.Sum(n => n.TaxablePaisa))
        }, notes.Append($"B2C-large limit: invoice value above ₹{ReportFormat.Money(context.B2clThresholdPaisa)} (setting {Migration0018Reports.B2clThresholdKey})."));
    }

    private async Task<ReportResult> Gstr3bAsync(ReportDefinition d, ReportParameters p, GstReturnContext context, List<string> notes, CancellationToken ct)
    {
        var summary = Gstr3bBuilder.Calculate(await InvoiceLinesAsync(p, ct), await NoteLinesAsync(p, ct), await InwardLinesAsync(p, ct), context);
        notes.Add("Tax payable is output tax minus net ITC per head; the portal applies the set-off order (IGST credit first) when you file.");
        return Result(d, p, Gstr3bBuilder.Build(summary), new[]
        {
            new ReportStat("Output tax", summary.Outward.TaxPaisa),
            new ReportStat("Net ITC", summary.NetItc.TaxPaisa),
            new ReportStat("Net payable", summary.NetPayable.TaxPaisa)
        }, notes);
    }

    private async Task<ReportResult> HsnAsync(ReportDefinition d, ReportParameters p, GstReturnContext context, List<string> notes, CancellationToken ct)
    {
        var sales = await SaleHsnLinesAsync(p, ct);
        var purchases = await PurchaseHsnLinesAsync(p, ct);
        AddHsnWarning(sales, notes);
        var salesSheet = HsnSummaryBuilder.Sheet("Sales (net of returns)", sales, context.Uqc);
        var purchaseSheet = HsnSummaryBuilder.Sheet("Purchases (net of returns)", purchases, context.Uqc);
        return Result(d, p, new[] { salesSheet, purchaseSheet }, new[]
        {
            new ReportStat("HSN codes sold", (long)salesSheet.Rows.Count, ReportColumnKind.Integer),
            new ReportStat("Sales taxable", salesSheet.Total("Taxable Value")),
            new ReportStat("Purchases taxable", purchaseSheet.Total("Taxable Value"))
        }, notes);
    }

    private static void AddHsnWarning(IReadOnlyList<GstHsnLine> lines, ICollection<string> notes)
    {
        var missing = HsnSummaryBuilder.MissingHsnCount(lines);
        if (missing > 0)
            notes.Add($"{missing} group(s) of items have no HSN code and are listed with a blank HSN — set the HSN on the item master.");
    }

    private Task<List<GstInvoiceLine>> InvoiceLinesAsync(ReportParameters p, CancellationToken ct) => ReportSql.QueryAsync(Db, $@"
        SELECT s.Id, s.InvoiceNumber, s.InvoiceDate, s.CustomerName, COALESCE(NULLIF(TRIM(s.CustomerGstin), ''), c.Gstin) AS Gstin,
               s.IsInterState, s.WithGst, s.GrandTotalPaisa, si.GstRatePercent AS Rate,
               SUM(si.TaxableValuePaisa) AS Taxable, SUM(si.IgstAmountPaisa) AS Igst, SUM(si.CgstAmountPaisa) AS Cgst, SUM(si.SgstAmountPaisa) AS Sgst
        FROM Sales s
        JOIN SaleItems si ON si.SaleId = s.Id
        LEFT JOIN Customers c ON c.Id = s.CustomerId
        WHERE s.Status = {SaleActive} AND s.InvoiceDate >= @From AND s.InvoiceDate < @ToEx
        GROUP BY s.Id, si.GstRatePercent
        ORDER BY s.InvoiceDate, s.Id",
        r => new GstInvoiceLine
        {
            SaleId = r.Long("Id"), InvoiceNumber = r.Text("InvoiceNumber"), InvoiceDate = r.Day("InvoiceDate"),
            CustomerName = r.Str("CustomerName"), CustomerGstin = r.Str("Gstin"), IsInterState = r.Bool("IsInterState"),
            WithGst = r.Bool("WithGst"), InvoiceValuePaisa = r.Long("GrandTotalPaisa"), Rate = r.Percent("Rate"),
            TaxablePaisa = r.Long("Taxable"), IgstPaisa = r.Long("Igst"), CgstPaisa = r.Long("Cgst"), SgstPaisa = r.Long("Sgst")
        }, ct, ReportSql.Range(p));

    private Task<List<GstNoteLine>> NoteLinesAsync(ReportParameters p, CancellationToken ct) => ReportSql.QueryAsync(Db, $@"
        SELECT r.Id, r.CreditNoteNumber, r.ReturnDate, r.CustomerName, COALESCE(NULLIF(TRIM(r.CustomerGstin), ''), c.Gstin) AS Gstin,
               r.IsInterState, r.NetAmountPaisa, s.GrandTotalPaisa AS OriginalValue, i.GstRatePercent AS Rate,
               SUM(i.TaxablePaisa) AS Taxable, SUM(i.IgstPaisa) AS Igst, SUM(i.CgstPaisa) AS Cgst, SUM(i.SgstPaisa) AS Sgst
        FROM SaleReturns r
        JOIN SaleReturnItems i ON i.SaleReturnId = r.Id
        LEFT JOIN Customers c ON c.Id = r.CustomerId
        LEFT JOIN Sales s ON s.Id = r.SaleId
        WHERE r.Status = {ReturnActive} AND r.ReturnDate >= @From AND r.ReturnDate < @ToEx
        GROUP BY r.Id, i.GstRatePercent
        ORDER BY r.ReturnDate, r.Id",
        r => new GstNoteLine
        {
            ReturnId = r.Long("Id"), NoteNumber = r.Text("CreditNoteNumber"), NoteDate = r.Day("ReturnDate"),
            CustomerName = r.Str("CustomerName"), CustomerGstin = r.Str("Gstin"), IsInterState = r.Bool("IsInterState"),
            NoteValuePaisa = r.Long("NetAmountPaisa"), OriginalInvoiceValuePaisa = r.LongOrNull("OriginalValue"), Rate = r.Percent("Rate"),
            TaxablePaisa = r.Long("Taxable"), IgstPaisa = r.Long("Igst"), CgstPaisa = r.Long("Cgst"), SgstPaisa = r.Long("Sgst")
        }, ct, ReportSql.Range(p));

    private async Task<List<GstHsnLine>> SaleHsnLinesAsync(ReportParameters p, CancellationToken ct)
    {
        static GstHsnLine Map(Microsoft.Data.Sqlite.SqliteDataReader r, int sign) => new()
        {
            Hsn = r.Str("Hsn"), Rate = r.Percent("Rate"), IsB2b = ReportFormat.NormalizeGstin(r.Str("Gstin")) != null,
            Units = sign * r.Long("Units"), TaxablePaisa = sign * r.Long("Taxable"), IgstPaisa = sign * r.Long("Igst"),
            CgstPaisa = sign * r.Long("Cgst"), SgstPaisa = sign * r.Long("Sgst")
        };
        var sales = await ReportSql.QueryAsync(Db, $@"
            SELECT COALESCE(NULLIF(TRIM(si.HsnCode), ''), m.HsnCode) AS Hsn, si.GstRatePercent AS Rate,
                   COALESCE(NULLIF(TRIM(s.CustomerGstin), ''), c.Gstin) AS Gstin, SUM(si.Quantity) AS Units,
                   SUM(si.TaxableValuePaisa) AS Taxable, SUM(si.IgstAmountPaisa) AS Igst, SUM(si.CgstAmountPaisa) AS Cgst, SUM(si.SgstAmountPaisa) AS Sgst
            FROM Sales s
            JOIN SaleItems si ON si.SaleId = s.Id
            LEFT JOIN Medicines m ON m.Id = si.MedicineId
            LEFT JOIN Customers c ON c.Id = s.CustomerId
            WHERE s.Status = {SaleActive} AND s.InvoiceDate >= @From AND s.InvoiceDate < @ToEx
            GROUP BY Hsn, si.GstRatePercent, Gstin",
            r => Map(r, 1), ct, ReportSql.Range(p));
        var returns = await ReportSql.QueryAsync(Db, $@"
            SELECT COALESCE(NULLIF(TRIM(i.HsnCode), ''), m.HsnCode) AS Hsn, i.GstRatePercent AS Rate,
                   COALESCE(NULLIF(TRIM(r.CustomerGstin), ''), c.Gstin) AS Gstin, SUM(i.Units) AS Units,
                   SUM(i.TaxablePaisa) AS Taxable, SUM(i.IgstPaisa) AS Igst, SUM(i.CgstPaisa) AS Cgst, SUM(i.SgstPaisa) AS Sgst
            FROM SaleReturns r
            JOIN SaleReturnItems i ON i.SaleReturnId = r.Id
            LEFT JOIN Medicines m ON m.Id = i.MedicineId
            LEFT JOIN Customers c ON c.Id = r.CustomerId
            WHERE r.Status = {ReturnActive} AND r.ReturnDate >= @From AND r.ReturnDate < @ToEx
            GROUP BY Hsn, i.GstRatePercent, Gstin",
            r => Map(r, -1), ct, ReportSql.Range(p));
        return sales.Concat(returns).ToList();
    }

    private async Task<List<GstHsnLine>> PurchaseHsnLinesAsync(ReportParameters p, CancellationToken ct)
    {
        static GstHsnLine Map(Microsoft.Data.Sqlite.SqliteDataReader r, int sign) => new()
        {
            Hsn = r.Str("Hsn"), Rate = r.Percent("Rate"), Units = sign * r.Long("Units"), TaxablePaisa = sign * r.Long("Taxable"),
            IgstPaisa = sign * r.Long("Igst"), CgstPaisa = sign * r.Long("Cgst"), SgstPaisa = sign * r.Long("Sgst")
        };
        var purchases = await ReportSql.QueryAsync(Db, $@"
            SELECT COALESCE(NULLIF(TRIM(pi.HsnCode), ''), m.HsnCode) AS Hsn, pi.GstRatePercent AS Rate,
                   SUM(pi.Quantity + pi.FreeQuantity) AS Units, SUM(pi.TaxablePaisa) AS Taxable,
                   SUM(pi.IgstPaisa) AS Igst, SUM(pi.CgstPaisa) AS Cgst, SUM(pi.SgstPaisa) AS Sgst
            FROM Purchases pu
            JOIN PurchaseItems pi ON pi.PurchaseId = pu.Id
            LEFT JOIN Medicines m ON m.Id = pi.MedicineId
            WHERE pu.Status = {PurchaseConfirmed} AND pu.PurchaseDate >= @From AND pu.PurchaseDate < @ToEx
            GROUP BY Hsn, pi.GstRatePercent",
            r => Map(r, 1), ct, ReportSql.Range(p));
        var returns = await ReportSql.QueryAsync(Db, @"
            SELECT COALESCE(NULLIF(TRIM(i.HsnCode), ''), m.HsnCode) AS Hsn, i.GstRatePercent AS Rate, SUM(i.Quantity) AS Units,
                   SUM(i.TaxablePaisa) AS Taxable, SUM(i.IgstPaisa) AS Igst, SUM(i.CgstPaisa) AS Cgst, SUM(i.SgstPaisa) AS Sgst
            FROM PurchaseReturns r
            JOIN PurchaseReturnItems i ON i.PurchaseReturnId = r.Id
            LEFT JOIN Medicines m ON m.Id = i.MedicineId
            WHERE r.IsCancelled = 0 AND r.ReturnDate >= @From AND r.ReturnDate < @ToEx
            GROUP BY Hsn, i.GstRatePercent",
            r => Map(r, -1), ct, ReportSql.Range(p));
        return purchases.Concat(returns).ToList();
    }

    private async Task<List<GstInwardLine>> InwardLinesAsync(ReportParameters p, CancellationToken ct)
    {
        var purchases = await ReportSql.QueryAsync(Db, $@"
            SELECT pu.IsInterState, pi.GstRatePercent AS Rate, SUM(pi.TaxablePaisa) AS Taxable,
                   SUM(pi.IgstPaisa) AS Igst, SUM(pi.CgstPaisa) AS Cgst, SUM(pi.SgstPaisa) AS Sgst
            FROM Purchases pu
            JOIN PurchaseItems pi ON pi.PurchaseId = pu.Id
            WHERE pu.Status = {PurchaseConfirmed} AND pu.PurchaseDate >= @From AND pu.PurchaseDate < @ToEx
            GROUP BY pu.IsInterState, pi.GstRatePercent",
            r => Inward(r, false), ct, ReportSql.Range(p));
        var returns = await ReportSql.QueryAsync(Db, @"
            SELECT r.IsInterState, i.GstRatePercent AS Rate, SUM(i.TaxablePaisa) AS Taxable,
                   SUM(i.IgstPaisa) AS Igst, SUM(i.CgstPaisa) AS Cgst, SUM(i.SgstPaisa) AS Sgst
            FROM PurchaseReturns r
            JOIN PurchaseReturnItems i ON i.PurchaseReturnId = r.Id
            WHERE r.IsCancelled = 0 AND r.ReturnDate >= @From AND r.ReturnDate < @ToEx
            GROUP BY r.IsInterState, i.GstRatePercent",
            r => Inward(r, true), ct, ReportSql.Range(p));
        return purchases.Concat(returns).ToList();
    }

    private static GstInwardLine Inward(Microsoft.Data.Sqlite.SqliteDataReader r, bool isReturn) => new()
    {
        IsReturn = isReturn, IsInterState = r.Bool("IsInterState"), Rate = r.Percent("Rate"), TaxablePaisa = r.Long("Taxable"),
        IgstPaisa = r.Long("Igst"), CgstPaisa = r.Long("Cgst"), SgstPaisa = r.Long("Sgst")
    };

    private async Task<List<GstDocumentSummary>> DocumentsAsync(ReportParameters p, CancellationToken ct)
    {
        var invoices = await ReportSql.QueryAsync(Db, $@"
            SELECT (SELECT InvoiceNumber FROM Sales WHERE InvoiceDate >= @From AND InvoiceDate < @ToEx ORDER BY Id LIMIT 1) AS FirstNo,
                   (SELECT InvoiceNumber FROM Sales WHERE InvoiceDate >= @From AND InvoiceDate < @ToEx ORDER BY Id DESC LIMIT 1) AS LastNo,
                   COUNT(*) AS Total, COALESCE(SUM(CASE WHEN Status = {SaleCancelled} THEN 1 ELSE 0 END), 0) AS Cancelled
            FROM Sales WHERE InvoiceDate >= @From AND InvoiceDate < @ToEx",
            r => new GstDocumentSummary("Invoices for outward supply", r.Str("FirstNo"), r.Str("LastNo"), r.Long("Total"), r.Long("Cancelled")),
            ct, ReportSql.Range(p));
        var notes = await ReportSql.QueryAsync(Db, $@"
            SELECT (SELECT CreditNoteNumber FROM SaleReturns WHERE ReturnDate >= @From AND ReturnDate < @ToEx ORDER BY Id LIMIT 1) AS FirstNo,
                   (SELECT CreditNoteNumber FROM SaleReturns WHERE ReturnDate >= @From AND ReturnDate < @ToEx ORDER BY Id DESC LIMIT 1) AS LastNo,
                   COUNT(*) AS Total, COALESCE(SUM(CASE WHEN Status = {ReturnCancelled} THEN 1 ELSE 0 END), 0) AS Cancelled
            FROM SaleReturns WHERE ReturnDate >= @From AND ReturnDate < @ToEx",
            r => new GstDocumentSummary("Credit Note", r.Str("FirstNo"), r.Str("LastNo"), r.Long("Total"), r.Long("Cancelled")),
            ct, ReportSql.Range(p));
        return invoices.Concat(notes).ToList();
    }
}
