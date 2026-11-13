using FluentAssertions;
using MedicalShopManager.Core.Modules.Reports;
using Xunit;

namespace MedicalShopManager.UnitTests.Reports;

/// <summary>GSTR-3B summary (outward net of credit notes, ITC net of debit notes, 3.2, table 5) and the HSN summary.</summary>
public class ReportsGstr3bAndHsnTests
{
    private static readonly GstReturnContext Context = new("29");

    private static readonly GstInvoiceLine[] Invoices =
    {
        new() { SaleId = 1, Rate = 12, TaxablePaisa = 10000, CgstPaisa = 600, SgstPaisa = 600, CustomerGstin = "29AAACA1111A1Z1" },
        new() { SaleId = 2, Rate = 12, TaxablePaisa = 20000, IgstPaisa = 2400, IsInterState = true },
        new() { SaleId = 3, Rate = 0, TaxablePaisa = 3000 },
        new() { SaleId = 4, Rate = 12, TaxablePaisa = 4000, WithGst = false }
    };

    private static readonly GstNoteLine[] Notes =
    {
        new() { ReturnId = 1, Rate = 12, TaxablePaisa = 1000, CgstPaisa = 60, SgstPaisa = 60 },
        new() { ReturnId = 2, Rate = 12, TaxablePaisa = 5000, IgstPaisa = 600, IsInterState = true },
        new() { ReturnId = 3, Rate = 0, TaxablePaisa = 500 }
    };

    private static readonly GstInwardLine[] Inward =
    {
        new() { Rate = 12, TaxablePaisa = 50000, CgstPaisa = 3000, SgstPaisa = 3000 },
        new() { Rate = 12, TaxablePaisa = 20000, IgstPaisa = 2400, IsInterState = true },
        new() { Rate = 12, TaxablePaisa = 5000, CgstPaisa = 300, SgstPaisa = 300, IsReturn = true },
        new() { Rate = 0, TaxablePaisa = 7000 },
        new() { Rate = 0, TaxablePaisa = 1500, IsInterState = true },
        new() { Rate = 0, TaxablePaisa = 1000, IsReturn = true }
    };

    [Fact]
    public void Calculate_NetsCreditAndDebitNotes()
    {
        var s = Gstr3bBuilder.Calculate(Invoices, Notes, Inward, Context);
        s.Outward.Should().Be(new GstAmounts(30000 - 6000, 2400 - 600, 600 - 60, 600 - 60));
        s.NilExemptPaisa.Should().Be(3000 + 4000 - 500);
        s.ItcGross.Should().Be(new GstAmounts(70000, 2400, 3000, 3000));
        s.ItcReversed.Should().Be(new GstAmounts(5000, 0, 300, 300));
        s.NetItc.Should().Be(new GstAmounts(65000, 2400, 2700, 2700));
        s.NetPayable.IgstPaisa.Should().Be(1800 - 2400);
        s.NetPayable.CgstPaisa.Should().Be(540 - 2700);
        s.ExemptInwardIntraPaisa.Should().Be(7000 - 1000);
        s.ExemptInwardInterPaisa.Should().Be(1500);
    }

    [Fact]
    public void Calculate_Table32_ListsInterStateUnregisteredByPlaceOfSupply()
    {
        var s = Gstr3bBuilder.Calculate(Invoices, Notes, Inward, Context);
        s.InterStateUnregistered.Should().ContainSingle().Which.Should().Be(("29-Karnataka", 15000L, 1800L));
    }

    [Fact]
    public void Build_ProducesTheStatutoryRows()
    {
        var sheets = Gstr3bBuilder.Build(Gstr3bBuilder.Calculate(Invoices, Notes, Inward, Context));
        sheets.Select(x => x.Name).Should().Equal(Gstr3bBuilder.Summary, Gstr3bBuilder.InterState, Gstr3bBuilder.ExemptInward);
        var summary = sheets[0];
        summary.HasTotals.Should().BeFalse();
        summary.Rows.Select(r => r.Values[0]).Should().Equal("3.1(a)", "3.1(b)", "3.1(c)", "3.1(d)", "3.1(e)", "4(A)(5)", "4(B)(2)", "4(C)", "6.1");
        summary.Value(0, "Total taxable value").Should().Be(24000L);
        summary.Value(2, "Total taxable value").Should().Be(6500L);
        summary.Value(7, "Central tax").Should().Be(2700L);
        summary.Value(8, "Integrated tax").Should().Be(-600L);
        summary.Rows[8].Style.Should().Be(ReportRowStyle.Subtotal);
    }

    [Fact]
    public void Calculate_EmptyPeriod_IsAllZero()
    {
        var s = Gstr3bBuilder.Calculate(Array.Empty<GstInvoiceLine>(), Array.Empty<GstNoteLine>(), Array.Empty<GstInwardLine>(), Context);
        s.NetPayable.Should().Be(new GstAmounts());
        s.InterStateUnregistered.Should().BeEmpty();
    }

    [Fact]
    public void Hsn_GroupsByCodeAndRate_NetsReturns_AndComputesTotalValue()
    {
        var lines = new[]
        {
            new GstHsnLine { Hsn = "30049099", Rate = 12, Units = 30, TaxablePaisa = 10000, CgstPaisa = 600, SgstPaisa = 600 },
            new GstHsnLine { Hsn = "30049099 ", Rate = 12, Units = 20, TaxablePaisa = 5000, CgstPaisa = 300, SgstPaisa = 300 },
            new GstHsnLine { Hsn = "30049099", Rate = 12, Units = -3, TaxablePaisa = -1000, CgstPaisa = -60, SgstPaisa = -60 },
            new GstHsnLine { Hsn = "30049099", Rate = 5, Units = 2, TaxablePaisa = 100, IgstPaisa = 5 },
            new GstHsnLine { Hsn = null, Rate = 12, Units = 1, TaxablePaisa = 50, CgstPaisa = 3, SgstPaisa = 3 },
            new GstHsnLine { Hsn = "3006", Rate = 12, Units = 1, TaxablePaisa = 100, CgstPaisa = 6, SgstPaisa = 6 },
            new GstHsnLine { Hsn = "3006", Rate = 12, Units = -1, TaxablePaisa = -100, CgstPaisa = -6, SgstPaisa = -6 }
        };
        var sheet = HsnSummaryBuilder.Sheet("HSN", lines, "NOS");
        sheet.Rows.Should().HaveCount(3, "fully returned HSN 3006 nets to zero and is dropped");
        sheet.Value(0, "HSN").Should().Be("30049099");
        sheet.Value(0, "Rate").Should().Be(5m);
        sheet.Value(1, "Total Quantity").Should().Be(47L);
        sheet.Value(1, "Taxable Value").Should().Be(14000L);
        sheet.Value(1, "Total Value").Should().Be(14000L + 840 + 840);
        sheet.Value(1, "Description").Should().Be("Medicaments (packaged doses)");
        sheet.Value(1, "UQC").Should().Be("NOS");
        sheet.Value(2, "HSN").Should().Be(string.Empty, "lines without HSN are listed last");
        HsnSummaryBuilder.MissingHsnCount(lines).Should().Be(1);
    }

    [Theory]
    [InlineData("30049099", "Medicaments (packaged doses)")]
    [InlineData("9018", "Medical instruments")]
    [InlineData("1234", "")]
    [InlineData(null, "")]
    public void DescriptionFor_KnowsPharmacyChapters(string? hsn, string expected)
    {
        HsnSummaryBuilder.DescriptionFor(hsn).Should().Be(expected);
    }
}
