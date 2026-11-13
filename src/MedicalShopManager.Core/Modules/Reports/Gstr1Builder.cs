namespace MedicalShopManager.Core.Modules.Reports;

public enum GstSupplyKind
{
    B2b,
    B2cl,
    B2cs,
    /// <summary>Nil-rated (0 %) or exempt (bill of supply): GSTR-1 table 8.</summary>
    NilOrExempt
}

/// <summary>
/// Builds the GSTR-1 sheets in the GST offline-tool column layout (sheet names and headers as in the portal's Excel
/// template, so rows can be pasted or imported). Rules:
/// <list type="bullet">
/// <item>Registered buyer (valid GSTIN) → b2b, one row per invoice and rate; place of supply from the GSTIN.</item>
/// <item>Unregistered, inter-state, invoice value above the threshold → b2cl; otherwise → b2cs aggregated by place
/// of supply and rate (type OE), net of credit notes to unregistered buyers.</item>
/// <item>0 % lines and bills of supply → exemp (nil rated / exempted), split inter/intra and registered/unregistered.</item>
/// <item>Credit notes: registered → cdnr; unregistered against a B2C-large bill → cdnur (B2CL); else reduce b2cs.</item>
/// <item>hsn(b2b) / hsn(b2c): HSN + rate, sales minus returns (Phase-III split, from May 2025).</item>
/// </list>
/// Walk-in sales have no address state, so an unregistered inter-state sale takes the shop state as place of supply
/// and is listed in the notes.
/// </summary>
public static class Gstr1Builder
{
    public const string B2b = "b2b", B2cl = "b2cl", B2cs = "b2cs", Cdnr = "cdnr", Cdnur = "cdnur", Exemp = "exemp",
        HsnB2b = "hsn(b2b)", HsnB2c = "hsn(b2c)", Docs = "docs";

    private const string RegularB2b = "Regular B2B";

    public static GstSupplyKind Classify(GstInvoiceLine line, GstReturnContext context)
    {
        if (!line.WithGst || line.Rate == 0m) return GstSupplyKind.NilOrExempt;
        if (ReportFormat.NormalizeGstin(line.CustomerGstin) != null) return GstSupplyKind.B2b;
        return line.IsInterState && line.InvoiceValuePaisa > context.B2clThresholdPaisa ? GstSupplyKind.B2cl : GstSupplyKind.B2cs;
    }

    /// <summary>Place-of-supply state code: the buyer GSTIN's state, else the shop state.</summary>
    public static string PlaceOfSupplyCode(string? customerGstin, GstReturnContext context) =>
        ReportFormat.StateOfGstin(customerGstin) ?? context.ShopStateCode;

    public static IReadOnlyList<ReportTable> Build(IReadOnlyList<GstInvoiceLine> invoices, IReadOnlyList<GstNoteLine> notes,
        IReadOnlyList<GstHsnLine> hsn, IReadOnlyList<GstDocumentSummary> documents, GstReturnContext context, ICollection<string> warnings)
    {
        var unknownPos = invoices.Where(i => i.IsInterState && ReportFormat.NormalizeGstin(i.CustomerGstin) == null)
            .Select(i => i.InvoiceNumber).Distinct().ToList();
        if (unknownPos.Count > 0)
            warnings.Add($"{unknownPos.Count} inter-state bill(s) to unregistered buyers have no buyer state; the shop state was used as place of supply ({string.Join(", ", unknownPos.Take(5))}{(unknownPos.Count > 5 ? ", …" : "")}).");

        return new[]
        {
            BuildB2b(invoices, context),
            BuildB2cl(invoices, context),
            BuildB2cs(invoices, notes, context, warnings),
            BuildCdnr(notes, context),
            BuildCdnur(notes, context),
            BuildExemp(invoices, notes, context),
            HsnSummaryBuilder.PortalSheet(HsnB2b, hsn.Where(h => h.IsB2b).ToList(), context.Uqc),
            HsnSummaryBuilder.PortalSheet(HsnB2c, hsn.Where(h => !h.IsB2b).ToList(), context.Uqc),
            BuildDocs(documents)
        };
    }

    private static ReportTable Portal(string name, ReportColumn[] columns, IEnumerable<ReportRow> rows) =>
        new ReportTable(name, columns, rows.ToList()) { PortalFormat = true }.EnsureValid();

    private static ReportTable BuildB2b(IReadOnlyList<GstInvoiceLine> invoices, GstReturnContext context)
    {
        var rows = invoices.Where(i => Classify(i, context) == GstSupplyKind.B2b)
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.SaleId).ThenBy(i => i.Rate)
            .Select(i => ReportRow.Of(ReportFormat.NormalizeGstin(i.CustomerGstin), i.CustomerName ?? string.Empty, i.InvoiceNumber,
                i.InvoiceDate.Date, i.InvoiceValuePaisa, ReportFormat.PlaceOfSupply(PlaceOfSupplyCode(i.CustomerGstin, context)), "N",
                string.Empty, RegularB2b, string.Empty, ReportFormat.Rate(i.Rate), i.TaxablePaisa, 0L));
        return Portal(B2b, new[]
        {
            ReportColumn.Text("GSTIN/UIN of Recipient"), ReportColumn.Text("Receiver Name"), ReportColumn.Text("Invoice Number"),
            ReportColumn.Date("Invoice date"), ReportColumn.Money("Invoice Value", total: false), ReportColumn.Text("Place Of Supply"),
            ReportColumn.Text("Reverse Charge", 0.6), ReportColumn.Text("Applicable % of Tax Rate", 0.6), ReportColumn.Text("Invoice Type"),
            ReportColumn.Text("E-Commerce GSTIN"), ReportColumn.Percent("Rate"), ReportColumn.Money("Taxable Value"), ReportColumn.Money("Cess Amount")
        }, rows);
    }

    private static ReportTable BuildB2cl(IReadOnlyList<GstInvoiceLine> invoices, GstReturnContext context)
    {
        var rows = invoices.Where(i => Classify(i, context) == GstSupplyKind.B2cl)
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.SaleId).ThenBy(i => i.Rate)
            .Select(i => ReportRow.Of(i.InvoiceNumber, i.InvoiceDate.Date, i.InvoiceValuePaisa,
                ReportFormat.PlaceOfSupply(PlaceOfSupplyCode(i.CustomerGstin, context)), string.Empty, ReportFormat.Rate(i.Rate),
                i.TaxablePaisa, 0L, string.Empty));
        return Portal(B2cl, new[]
        {
            ReportColumn.Text("Invoice Number"), ReportColumn.Date("Invoice date"), ReportColumn.Money("Invoice Value", total: false),
            ReportColumn.Text("Place Of Supply"), ReportColumn.Text("Applicable % of Tax Rate", 0.6), ReportColumn.Percent("Rate"),
            ReportColumn.Money("Taxable Value"), ReportColumn.Money("Cess Amount"), ReportColumn.Text("E-Commerce GSTIN")
        }, rows);
    }

    /// <summary>True when a credit note to an unregistered buyer belongs in CDNUR (against an inter-state B2C-large bill).</summary>
    public static bool IsCdnurB2cl(GstNoteLine note, GstReturnContext context) =>
        ReportFormat.NormalizeGstin(note.CustomerGstin) == null && note.IsInterState && note.Rate > 0m
        && note.OriginalInvoiceValuePaisa is { } value && value > context.B2clThresholdPaisa;

    private static ReportTable BuildB2cs(IReadOnlyList<GstInvoiceLine> invoices, IReadOnlyList<GstNoteLine> notes,
        GstReturnContext context, ICollection<string> warnings)
    {
        var sales = invoices.Where(i => Classify(i, context) == GstSupplyKind.B2cs)
            .Select(i => (Pos: PlaceOfSupplyCode(i.CustomerGstin, context), Rate: ReportFormat.Rate(i.Rate), i.TaxablePaisa));
        var returns = notes.Where(n => n.Rate > 0m && ReportFormat.NormalizeGstin(n.CustomerGstin) == null && !IsCdnurB2cl(n, context))
            .Select(n => (Pos: PlaceOfSupplyCode(n.CustomerGstin, context), Rate: ReportFormat.Rate(n.Rate), TaxablePaisa: -n.TaxablePaisa));
        var groups = sales.Concat(returns)
            .GroupBy(x => (x.Pos, x.Rate))
            .Select(g => (g.Key.Pos, g.Key.Rate, Taxable: g.Sum(x => x.TaxablePaisa)))
            .Where(g => g.Taxable != 0)
            .OrderBy(g => g.Pos, StringComparer.Ordinal).ThenBy(g => g.Rate)
            .ToList();
        if (groups.Any(g => g.Taxable < 0))
            warnings.Add("Some B2C (small) rows are negative because returns exceeded sales for that rate; the portal does not accept negative B2CS values — adjust them against an earlier period.");
        return Portal(B2cs, new[]
        {
            ReportColumn.Text("Type", 0.5), ReportColumn.Text("Place Of Supply"), ReportColumn.Text("Applicable % of Tax Rate", 0.6),
            ReportColumn.Percent("Rate"), ReportColumn.Money("Taxable Value"), ReportColumn.Money("Cess Amount"), ReportColumn.Text("E-Commerce GSTIN")
        }, groups.Select(g => ReportRow.Of("OE", ReportFormat.PlaceOfSupply(g.Pos), string.Empty, g.Rate, g.Taxable, 0L, string.Empty)));
    }

    private static ReportTable BuildCdnr(IReadOnlyList<GstNoteLine> notes, GstReturnContext context)
    {
        var rows = notes.Where(n => n.Rate > 0m && ReportFormat.NormalizeGstin(n.CustomerGstin) != null)
            .OrderBy(n => n.NoteDate).ThenBy(n => n.ReturnId).ThenBy(n => n.Rate)
            .Select(n => ReportRow.Of(ReportFormat.NormalizeGstin(n.CustomerGstin), n.CustomerName ?? string.Empty, n.NoteNumber,
                n.NoteDate.Date, "C", ReportFormat.PlaceOfSupply(PlaceOfSupplyCode(n.CustomerGstin, context)), "N", RegularB2b,
                n.NoteValuePaisa, string.Empty, ReportFormat.Rate(n.Rate), n.TaxablePaisa, 0L));
        return Portal(Cdnr, new[]
        {
            ReportColumn.Text("GSTIN/UIN of Recipient"), ReportColumn.Text("Receiver Name"), ReportColumn.Text("Note Number"),
            ReportColumn.Date("Note Date"), ReportColumn.Text("Note Type", 0.5), ReportColumn.Text("Place Of Supply"),
            ReportColumn.Text("Reverse Charge", 0.6), ReportColumn.Text("Note Supply Type"), ReportColumn.Money("Note Value", total: false),
            ReportColumn.Text("Applicable % of Tax Rate", 0.6), ReportColumn.Percent("Rate"), ReportColumn.Money("Taxable Value"),
            ReportColumn.Money("Cess Amount")
        }, rows);
    }

    private static ReportTable BuildCdnur(IReadOnlyList<GstNoteLine> notes, GstReturnContext context)
    {
        var rows = notes.Where(n => IsCdnurB2cl(n, context))
            .OrderBy(n => n.NoteDate).ThenBy(n => n.ReturnId).ThenBy(n => n.Rate)
            .Select(n => ReportRow.Of("B2CL", n.NoteNumber, n.NoteDate.Date, "C",
                ReportFormat.PlaceOfSupply(PlaceOfSupplyCode(n.CustomerGstin, context)), n.NoteValuePaisa, string.Empty,
                ReportFormat.Rate(n.Rate), n.TaxablePaisa, 0L));
        return Portal(Cdnur, new[]
        {
            ReportColumn.Text("UR Type", 0.5), ReportColumn.Text("Note Number"), ReportColumn.Date("Note Date"), ReportColumn.Text("Note Type", 0.5),
            ReportColumn.Text("Place Of Supply"), ReportColumn.Money("Note Value", total: false), ReportColumn.Text("Applicable % of Tax Rate", 0.6),
            ReportColumn.Percent("Rate"), ReportColumn.Money("Taxable Value"), ReportColumn.Money("Cess Amount")
        }, rows);
    }

    public static readonly string[] ExempDescriptions =
    {
        "Inter-State supplies to registered persons",
        "Intra-State supplies to registered persons",
        "Inter-State supplies to unregistered persons",
        "Intra-State supplies to unregistered persons"
    };

    private static int ExempIndex(string? gstin, bool interState) =>
        (ReportFormat.NormalizeGstin(gstin) != null ? 0 : 2) + (interState ? 0 : 1);

    private static ReportTable BuildExemp(IReadOnlyList<GstInvoiceLine> invoices, IReadOnlyList<GstNoteLine> notes, GstReturnContext context)
    {
        var nil = new long[4];
        var exempt = new long[4];
        foreach (var line in invoices.Where(i => Classify(i, context) == GstSupplyKind.NilOrExempt))
        {
            var index = ExempIndex(line.CustomerGstin, line.IsInterState);
            if (line.WithGst) nil[index] += line.TaxablePaisa;
            else exempt[index] += line.TaxablePaisa;
        }
        foreach (var note in notes.Where(n => n.Rate == 0m))
            nil[ExempIndex(note.CustomerGstin, note.IsInterState)] -= note.TaxablePaisa;

        var rows = ExempDescriptions.Select((d, i) => ReportRow.Of(d, nil[i], exempt[i], 0L));
        return Portal(Exemp, new[]
        {
            ReportColumn.Text("Description", 3), ReportColumn.Money("Nil Rated Supplies"),
            ReportColumn.Money("Exempted(other than nil rated/non GST supply)"), ReportColumn.Money("Non-GST Supplies")
        }, rows);
    }

    private static ReportTable BuildDocs(IReadOnlyList<GstDocumentSummary> documents)
    {
        var rows = documents.Where(d => d.Total > 0)
            .Select(d => ReportRow.Of(d.Nature, d.FirstNumber ?? string.Empty, d.LastNumber ?? string.Empty, d.Total, d.Cancelled));
        return Portal(Docs, new[]
        {
            ReportColumn.Text("Nature of Document", 2.5), ReportColumn.Text("Sr. No. From"), ReportColumn.Text("Sr. No. To"),
            ReportColumn.Count("Total Number"), ReportColumn.Count("Cancelled")
        }, rows);
    }
}
