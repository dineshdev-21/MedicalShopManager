namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>
/// HSN-wise summary (GSTR-1 table 12 layout): one row per HSN code and GST rate with total quantity, total value
/// (taxable + tax), taxable value and IGST / CGST / SGST. Lines without an HSN code are grouped under a blank HSN
/// and reported as a warning by the caller.
/// </summary>
public static class HsnSummaryBuilder
{
    /// <summary>Short descriptions for the HSN chapters a pharmacy sells most (used when no description is supplied).</summary>
    private static readonly (string Prefix, string Description)[] KnownChapters =
    {
        ("3004", "Medicaments (packaged doses)"),
        ("3003", "Medicaments (not in measured doses)"),
        ("3002", "Vaccines, blood fractions"),
        ("3005", "Wadding, gauze, bandages"),
        ("3006", "Pharmaceutical goods"),
        ("3304", "Skin care preparations"),
        ("3305", "Hair preparations"),
        ("3306", "Oral / dental hygiene"),
        ("3401", "Soap"),
        ("3808", "Disinfectants"),
        ("4015", "Gloves (rubber)"),
        ("2106", "Food preparations / supplements"),
        ("1901", "Infant food"),
        ("9018", "Medical instruments"),
        ("9019", "Therapy appliances"),
        ("9021", "Orthopaedic appliances"),
        ("9025", "Thermometers"),
        ("9619", "Sanitary pads, diapers"),
    };

    public static string DescriptionFor(string? hsn)
    {
        if (string.IsNullOrWhiteSpace(hsn)) return string.Empty;
        var code = hsn.Trim();
        foreach (var (prefix, description) in KnownChapters)
            if (code.StartsWith(prefix, StringComparison.Ordinal)) return description;
        return string.Empty;
    }

    public static readonly ReportColumn[] Columns =
    {
        ReportColumn.Text("HSN", 0.8), ReportColumn.Text("Description", 2), ReportColumn.Text("UQC", 0.5),
        ReportColumn.Qty("Total Quantity"), ReportColumn.Money("Total Value"), ReportColumn.Percent("Rate"),
        ReportColumn.Money("Taxable Value"), ReportColumn.Money("Integrated Tax Amount"), ReportColumn.Money("Central Tax Amount"),
        ReportColumn.Money("State/UT Tax Amount"), ReportColumn.Money("Cess Amount")
    };

    /// <summary>Groups lines by (HSN, rate); values of returns arrive negative and are netted.</summary>
    public static IReadOnlyList<ReportRow> Rows(IReadOnlyList<GstHsnLine> lines, string uqc) =>
        lines.GroupBy(l => (Hsn: (l.Hsn ?? string.Empty).Trim(), Rate: ReportFormat.Rate(l.Rate)))
            .Select(g =>
            {
                var taxable = g.Sum(l => l.TaxablePaisa);
                var igst = g.Sum(l => l.IgstPaisa);
                var cgst = g.Sum(l => l.CgstPaisa);
                var sgst = g.Sum(l => l.SgstPaisa);
                var units = g.Sum(l => l.Units);
                var description = g.Select(l => l.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))
                                  ?? DescriptionFor(g.Key.Hsn);
                return (g.Key.Hsn, g.Key.Rate, Row: ReportRow.Of(g.Key.Hsn, description, uqc, units, taxable + igst + cgst + sgst,
                    g.Key.Rate, taxable, igst, cgst, sgst, 0L), IsZero: units == 0 && taxable == 0 && igst + cgst + sgst == 0);
            })
            .Where(x => !x.IsZero)
            .OrderBy(x => x.Hsn.Length == 0 ? 1 : 0).ThenBy(x => x.Hsn, StringComparer.Ordinal).ThenBy(x => x.Rate)
            .Select(x => x.Row)
            .ToList();

    public static ReportTable PortalSheet(string name, IReadOnlyList<GstHsnLine> lines, string uqc) =>
        new ReportTable(name, Columns, Rows(lines, uqc)) { PortalFormat = true }.EnsureValid();

    public static ReportTable Sheet(string name, IReadOnlyList<GstHsnLine> lines, string uqc) =>
        new ReportTable(name, Columns, Rows(lines, uqc)).EnsureValid();

    public static int MissingHsnCount(IReadOnlyList<GstHsnLine> lines) =>
        lines.Count(l => string.IsNullOrWhiteSpace(l.Hsn) && (l.Units != 0 || l.TaxablePaisa != 0));
}
