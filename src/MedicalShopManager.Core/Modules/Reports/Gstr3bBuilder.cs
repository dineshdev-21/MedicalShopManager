namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>Tax heads of one GSTR-3B row.</summary>
public readonly record struct GstAmounts(long TaxablePaisa, long IgstPaisa, long CgstPaisa, long SgstPaisa)
{
    public long TaxPaisa => IgstPaisa + CgstPaisa + SgstPaisa;
    public static GstAmounts operator +(GstAmounts a, GstAmounts b) =>
        new(a.TaxablePaisa + b.TaxablePaisa, a.IgstPaisa + b.IgstPaisa, a.CgstPaisa + b.CgstPaisa, a.SgstPaisa + b.SgstPaisa);
    public static GstAmounts operator -(GstAmounts a, GstAmounts b) =>
        new(a.TaxablePaisa - b.TaxablePaisa, a.IgstPaisa - b.IgstPaisa, a.CgstPaisa - b.CgstPaisa, a.SgstPaisa - b.SgstPaisa);
}

/// <summary>The figures of a GSTR-3B summary (all net of credit / debit notes of the period).</summary>
public sealed record Gstr3bSummary
{
    public GstAmounts Outward { get; init; }
    public long NilExemptPaisa { get; init; }
    public GstAmounts ItcGross { get; init; }
    public GstAmounts ItcReversed { get; init; }
    public GstAmounts NetItc => ItcGross - ItcReversed;
    public GstAmounts NetPayable => Outward - NetItc;
    /// <summary>Inter-state supplies to unregistered persons by place of supply (table 3.2).</summary>
    public IReadOnlyList<(string PlaceOfSupply, long TaxablePaisa, long IgstPaisa)> InterStateUnregistered { get; init; } =
        Array.Empty<(string, long, long)>();
    public long ExemptInwardInterPaisa { get; init; }
    public long ExemptInwardIntraPaisa { get; init; }
}

/// <summary>
/// GSTR-3B summary. Outward supplies (3.1) come from sales minus credit notes; ITC (4) from confirmed purchases minus
/// purchase returns / debit notes. The "tax payable" row is output tax minus ITC per head, before the Rule 88A
/// cross-utilisation order (IGST credit first), which the portal applies when the return is filed.
/// </summary>
public static class Gstr3bBuilder
{
    public const string Summary = "3B summary", InterState = "3.2 inter-state B2C", ExemptInward = "5 exempt inward";

    public static Gstr3bSummary Calculate(IReadOnlyList<GstInvoiceLine> invoices, IReadOnlyList<GstNoteLine> notes,
        IReadOnlyList<GstInwardLine> inward, GstReturnContext context)
    {
        var taxableSales = invoices.Where(i => i.WithGst && i.Rate > 0m)
            .Aggregate(new GstAmounts(), (sum, i) => sum + new GstAmounts(i.TaxablePaisa, i.IgstPaisa, i.CgstPaisa, i.SgstPaisa));
        var taxableNotes = notes.Where(n => n.Rate > 0m)
            .Aggregate(new GstAmounts(), (sum, n) => sum + new GstAmounts(n.TaxablePaisa, n.IgstPaisa, n.CgstPaisa, n.SgstPaisa));
        var nilExempt = invoices.Where(i => !i.WithGst || i.Rate == 0m).Sum(i => i.TaxablePaisa)
                        - notes.Where(n => n.Rate == 0m).Sum(n => n.TaxablePaisa);

        var interState = invoices
            .Where(i => i.WithGst && i.Rate > 0m && i.IsInterState && ReportFormat.NormalizeGstin(i.CustomerGstin) == null)
            .Select(i => (Pos: Gstr1Builder.PlaceOfSupplyCode(i.CustomerGstin, context), i.TaxablePaisa, i.IgstPaisa))
            .Concat(notes.Where(n => n.Rate > 0m && n.IsInterState && ReportFormat.NormalizeGstin(n.CustomerGstin) == null)
                .Select(n => (Pos: Gstr1Builder.PlaceOfSupplyCode(n.CustomerGstin, context), TaxablePaisa: -n.TaxablePaisa, IgstPaisa: -n.IgstPaisa)))
            .GroupBy(x => x.Pos)
            .Select(g => (ReportFormat.PlaceOfSupply(g.Key), g.Sum(x => x.TaxablePaisa), g.Sum(x => x.IgstPaisa)))
            .Where(x => x.Item2 != 0 || x.Item3 != 0)
            .OrderBy(x => x.Item1, StringComparer.Ordinal)
            .ToList();

        GstAmounts Sum(IEnumerable<GstInwardLine> lines) =>
            lines.Aggregate(new GstAmounts(), (sum, l) => sum + new GstAmounts(l.TaxablePaisa, l.IgstPaisa, l.CgstPaisa, l.SgstPaisa));

        var exemptInward = inward.Where(l => l.Rate == 0m).ToList();
        long Net(IEnumerable<GstInwardLine> lines) => lines.Sum(l => l.IsReturn ? -l.TaxablePaisa : l.TaxablePaisa);

        return new Gstr3bSummary
        {
            Outward = taxableSales - taxableNotes,
            NilExemptPaisa = nilExempt,
            ItcGross = Sum(inward.Where(l => !l.IsReturn && l.Rate > 0m)),
            ItcReversed = Sum(inward.Where(l => l.IsReturn && l.Rate > 0m)),
            InterStateUnregistered = interState,
            ExemptInwardInterPaisa = Net(exemptInward.Where(l => l.IsInterState)),
            ExemptInwardIntraPaisa = Net(exemptInward.Where(l => !l.IsInterState))
        };
    }

    public static IReadOnlyList<ReportTable> Build(Gstr3bSummary s)
    {
        var columns = new[]
        {
            ReportColumn.Text("Table", 0.6), ReportColumn.Text("Nature of supplies", 3.5), ReportColumn.Money("Total taxable value", false),
            ReportColumn.Money("Integrated tax", false), ReportColumn.Money("Central tax", false), ReportColumn.Money("State/UT tax", false),
            ReportColumn.Money("Cess", false)
        };
        ReportRow Row(string table, string nature, GstAmounts a, ReportRowStyle style = ReportRowStyle.Normal) =>
            new(new object?[] { table, nature, a.TaxablePaisa, a.IgstPaisa, a.CgstPaisa, a.SgstPaisa, 0L }, style);

        var zero = new GstAmounts();
        var rows = new List<ReportRow>
        {
            Row("3.1(a)", "Outward taxable supplies (other than zero rated, nil rated and exempted)", s.Outward),
            Row("3.1(b)", "Outward taxable supplies (zero rated)", zero),
            Row("3.1(c)", "Other outward supplies (nil rated, exempted)", new GstAmounts(s.NilExemptPaisa, 0, 0, 0)),
            Row("3.1(d)", "Inward supplies (liable to reverse charge)", zero),
            Row("3.1(e)", "Non-GST outward supplies", zero),
            Row("4(A)(5)", "ITC available — all other ITC (purchases)", s.ItcGross),
            Row("4(B)(2)", "ITC reversed — others (purchase returns / debit notes)", s.ItcReversed),
            Row("4(C)", "Net ITC available (A) − (B)", s.NetItc, ReportRowStyle.Subtotal),
            Row("6.1", "Tax payable: 3.1(a) tax − net ITC (negative = credit carried forward)",
                new GstAmounts(0, s.NetPayable.IgstPaisa, s.NetPayable.CgstPaisa, s.NetPayable.SgstPaisa), ReportRowStyle.Subtotal)
        };

        var inter = new ReportTable(InterState, new[]
        {
            ReportColumn.Text("Place of supply (State/UT)", 2), ReportColumn.Money("Total taxable value"), ReportColumn.Money("Amount of integrated tax")
        }, s.InterStateUnregistered.Select(x => ReportRow.Of(x.PlaceOfSupply, x.TaxablePaisa, x.IgstPaisa)).ToList());

        var exempt = new ReportTable(ExemptInward, new[]
        {
            ReportColumn.Text("Nature of supplies", 3), ReportColumn.Money("Inter-State supplies"), ReportColumn.Money("Intra-State supplies")
        }, new[] { ReportRow.Of("From a supplier under composition scheme, exempt and nil rated supply", s.ExemptInwardInterPaisa, s.ExemptInwardIntraPaisa) });

        return new[]
        {
            new ReportTable(Summary, columns, rows) { ShowTotals = false }.EnsureValid(),
            inter.EnsureValid(),
            exempt.EnsureValid()
        };
    }
}
