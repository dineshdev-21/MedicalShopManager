using FluentAssertions;
using MedicalShopManager.Core.Modules.Reports;
using Xunit;

namespace MedicalShopManager.UnitTests.Reports;

/// <summary>
/// GSTR-1 classification and sheet layout (GST offline tool templates). Shop in Karnataka (29). B2CL limit ₹1,00,000
/// (Notification 12/2024-CT, from 1 Aug 2024): inter-state, unregistered, invoice value strictly above the limit.
/// </summary>
public class ReportsGstr1BuilderTests
{
    private static readonly GstReturnContext Context = new("29");
    private const string Registered = "29AAACA1111A1Z1";
    private const string RegisteredMh = "27AAACB2222B1Z2";

    private static GstInvoiceLine Invoice(long id, decimal rate = 12, long taxable = 10000, string? gstin = null, bool inter = false,
        long value = 11200, bool withGst = true) => new()
    {
        SaleId = id, InvoiceNumber = $"INV-{id}", InvoiceDate = new DateTime(2026, 10, (int)Math.Min(id, 28)), CustomerName = $"Cust {id}",
        CustomerGstin = gstin, IsInterState = inter, WithGst = withGst, InvoiceValuePaisa = value, Rate = rate, TaxablePaisa = taxable,
        IgstPaisa = inter ? taxable * (long)rate / 100 : 0, CgstPaisa = inter ? 0 : taxable * (long)rate / 200,
        SgstPaisa = inter ? 0 : taxable * (long)rate / 200
    };

    private static GstNoteLine Note(long id, decimal rate = 12, long taxable = 1000, string? gstin = null, bool inter = false, long? original = 11200) => new()
    {
        ReturnId = id, NoteNumber = $"CN-{id}", NoteDate = new DateTime(2026, 10, 20), CustomerGstin = gstin, IsInterState = inter,
        NoteValuePaisa = taxable + taxable * (long)rate / 100, OriginalInvoiceValuePaisa = original, Rate = rate, TaxablePaisa = taxable,
        IgstPaisa = inter ? taxable * (long)rate / 100 : 0, CgstPaisa = inter ? 0 : taxable * (long)rate / 200,
        SgstPaisa = inter ? 0 : taxable * (long)rate / 200
    };

    private static IReadOnlyList<ReportTable> Build(IReadOnlyList<GstInvoiceLine> invoices, IReadOnlyList<GstNoteLine>? notes = null,
        List<string>? warnings = null) =>
        Gstr1Builder.Build(invoices, notes ?? Array.Empty<GstNoteLine>(), Array.Empty<GstHsnLine>(), Array.Empty<GstDocumentSummary>(),
            Context, warnings ?? new List<string>());

    [Theory]
    [InlineData(Registered, false, 11200L, GstSupplyKind.B2b)]
    [InlineData(null, false, 50_000_000L, GstSupplyKind.B2cs)]
    [InlineData(null, true, 10_000_000L, GstSupplyKind.B2cs)]
    [InlineData(null, true, 10_000_001L, GstSupplyKind.B2cl)]
    [InlineData("not-a-gstin", true, 20_000_000L, GstSupplyKind.B2cl)]
    public void Classify_FollowsTheGstr1Rules(string? gstin, bool inter, long value, GstSupplyKind expected)
    {
        Gstr1Builder.Classify(Invoice(1, gstin: gstin, inter: inter, value: value), Context).Should().Be(expected);
    }

    [Fact]
    public void Classify_ZeroRateAndBillOfSupply_AreNilOrExempt()
    {
        Gstr1Builder.Classify(Invoice(1, rate: 0, gstin: Registered), Context).Should().Be(GstSupplyKind.NilOrExempt);
        Gstr1Builder.Classify(Invoice(1, withGst: false), Context).Should().Be(GstSupplyKind.NilOrExempt);
    }

    [Fact]
    public void Sheets_UseThePortalNamesAndAreInPortalFormat()
    {
        var sheets = Build(new[] { Invoice(1) });
        sheets.Select(s => s.Name).Should().Equal("b2b", "b2cl", "b2cs", "cdnr", "cdnur", "exemp", "hsn(b2b)", "hsn(b2c)", "docs");
        sheets.Should().OnlyContain(s => s.PortalFormat);
    }

    [Fact]
    public void B2b_HasOneRowPerInvoiceAndRate_WithPortalColumns()
    {
        var b2b = Build(new[] { Invoice(1, gstin: RegisteredMh, inter: true), Invoice(1, rate: 5, taxable: 2000, gstin: RegisteredMh, inter: true) })
            .First(s => s.Name == "b2b");
        b2b.Columns.Select(c => c.Header).Should().Equal("GSTIN/UIN of Recipient", "Receiver Name", "Invoice Number", "Invoice date",
            "Invoice Value", "Place Of Supply", "Reverse Charge", "Applicable % of Tax Rate", "Invoice Type", "E-Commerce GSTIN", "Rate",
            "Taxable Value", "Cess Amount");
        b2b.Rows.Should().HaveCount(2);
        b2b.Value(0, "Place Of Supply").Should().Be("27-Maharashtra");
        b2b.Value(0, "Reverse Charge").Should().Be("N");
        b2b.Value(0, "Invoice Type").Should().Be("Regular B2B");
        b2b.Value(0, "Rate").Should().Be(5m);
        b2b.Total("Taxable Value").Should().Be(12000);
        b2b.Totals()[b2b.IndexOf("Invoice Value")].Should().BeNull("the invoice value repeats on every rate row");
    }

    [Fact]
    public void B2cs_IsAggregatedByPlaceOfSupplyAndRate_NetOfUnregisteredCreditNotes()
    {
        var b2cs = Build(new[] { Invoice(1), Invoice(2), Invoice(3, rate: 5, taxable: 4000) }, new[] { Note(9, taxable: 1500) })
            .First(s => s.Name == "b2cs");
        b2cs.Rows.Should().HaveCount(2);
        b2cs.Value(0, "Type").Should().Be("OE");
        b2cs.Value(0, "Place Of Supply").Should().Be("29-Karnataka");
        b2cs.Value(0, "Rate").Should().Be(5m);
        b2cs.Value(0, "Taxable Value").Should().Be(4000L);
        b2cs.Value(1, "Taxable Value").Should().Be(18500L);
    }

    [Fact]
    public void B2cs_NegativeRows_RaiseAWarning()
    {
        var warnings = new List<string>();
        Build(new[] { Invoice(1, taxable: 1000) }, new[] { Note(9, taxable: 3000) }, warnings);
        warnings.Should().Contain(w => w.Contains("negative"));
    }

    [Fact]
    public void B2cl_ListsLargeInterStateInvoices_AndWarnsAboutUnknownBuyerState()
    {
        var warnings = new List<string>();
        var sheets = Build(new[] { Invoice(4, taxable: 10_000_000, inter: true, value: 11_200_000) }, null, warnings);
        var b2cl = sheets.First(s => s.Name == "b2cl");
        b2cl.Rows.Should().ContainSingle();
        b2cl.Value(0, "Invoice Number").Should().Be("INV-4");
        b2cl.Value(0, "Place Of Supply").Should().Be("29-Karnataka");
        sheets.First(s => s.Name == "b2cs").Rows.Should().BeEmpty();
        warnings.Should().Contain(w => w.Contains("INV-4"));
    }

    [Fact]
    public void CreditNotes_RegisteredGoToCdnr_UnregisteredAgainstB2clGoToCdnur()
    {
        var sheets = Build(Array.Empty<GstInvoiceLine>(), new[]
        {
            Note(1, gstin: Registered),
            Note(2, inter: true, original: 15_000_000),
            Note(3, inter: true, original: 50_000)
        });
        var cdnr = sheets.First(s => s.Name == "cdnr");
        cdnr.Rows.Should().ContainSingle();
        cdnr.Value(0, "Note Type").Should().Be("C");
        cdnr.Value(0, "Note Supply Type").Should().Be("Regular B2B");
        cdnr.Value(0, "GSTIN/UIN of Recipient").Should().Be(Registered);
        var cdnur = sheets.First(s => s.Name == "cdnur");
        cdnur.Rows.Should().ContainSingle();
        cdnur.Value(0, "UR Type").Should().Be("B2CL");
        cdnur.Value(0, "Note Number").Should().Be("CN-2");
        sheets.First(s => s.Name == "b2cs").Value(0, "Taxable Value").Should().Be(-1000L, "note 3 reduces B2C small");
    }

    [Fact]
    public void Exemp_SplitsNilRatedAndExemptByRegistrationAndState()
    {
        var exemp = Build(new[]
        {
            Invoice(1, rate: 0, taxable: 500, gstin: Registered),
            Invoice(2, rate: 0, taxable: 700),
            Invoice(3, rate: 0, taxable: 900, inter: true),
            Invoice(4, taxable: 1100, withGst: false)
        }, new[] { Note(5, rate: 0, taxable: 200) }).First(s => s.Name == "exemp");
        exemp.Rows.Should().HaveCount(4);
        exemp.Value(1, "Description").Should().Be("Intra-State supplies to registered persons");
        exemp.Value(1, "Nil Rated Supplies").Should().Be(500L);
        exemp.Value(2, "Nil Rated Supplies").Should().Be(900L);
        exemp.Value(3, "Nil Rated Supplies").Should().Be(500L, "700 sold − 200 returned");
        exemp.Value(3, "Exempted(other than nil rated/non GST supply)").Should().Be(1100L);
    }

    [Fact]
    public void Docs_SkipsEmptySeries()
    {
        var docs = Gstr1Builder.Build(Array.Empty<GstInvoiceLine>(), Array.Empty<GstNoteLine>(), Array.Empty<GstHsnLine>(), new[]
        {
            new GstDocumentSummary("Invoices for outward supply", "INV-1", "INV-9", 9, 1),
            new GstDocumentSummary("Credit Note", null, null, 0, 0)
        }, Context, new List<string>()).First(s => s.Name == "docs");
        docs.Rows.Should().ContainSingle();
        docs.Value(0, "Sr. No. To").Should().Be("INV-9");
        docs.Value(0, "Cancelled").Should().Be(1L);
    }

    [Fact]
    public void PlaceOfSupply_PrefersTheBuyerGstinState()
    {
        Gstr1Builder.PlaceOfSupplyCode(RegisteredMh, Context).Should().Be("27");
        Gstr1Builder.PlaceOfSupplyCode(null, Context).Should().Be("29");
    }
}
