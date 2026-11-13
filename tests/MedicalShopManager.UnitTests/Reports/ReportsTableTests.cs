using FluentAssertions;
using MedicalShopManager.Core.Modules.Reports;
using Xunit;

namespace MedicalShopManager.UnitTests.Reports;

/// <summary>Report sheets: type checks, totals (subtotal / opening rows excluded), quick filter and display formats.</summary>
public class ReportsTableTests
{
    private static ReportTable Sample() => new("Sample", new[]
    {
        ReportColumn.Date("Date"), ReportColumn.Text("Party"), ReportColumn.Money("Amount"), ReportColumn.Qty("Units"),
        ReportColumn.Percent("Margin %"), ReportColumn.Money("Rate", total: false)
    }, new[]
    {
        ReportRow.Of(new DateTime(2026, 10, 1), "Apollo Clinic", 12345L, 10L, 12.5m, 100L),
        ReportRow.Of(new DateTime(2026, 10, 2), "Ravi Kumar", 55L, 5L, 7m, 200L),
        ReportRow.Subtotal(null, "Subtotal", 12400L, 15L, null, null),
        ReportRow.Opening(null, "Opening", 999L, 1L, null, null)
    });

    [Fact]
    public void Totals_SumTotalColumns_AndSkipSubtotalAndOpeningRows()
    {
        var totals = Sample().Totals();
        totals[2].Should().Be(12400L);
        totals[3].Should().Be(15L);
        totals[4].Should().BeNull("percent columns are never added up");
        totals[5].Should().BeNull("rate columns are not totals");
        totals[1].Should().BeNull();
        Sample().Total("amount").Should().Be(12400L);
        Sample().HasTotals.Should().BeTrue();
    }

    [Fact]
    public void Filter_KeepsRowsContainingEveryWord_AsDisplayed()
    {
        Sample().Filter("ravi").Rows.Should().ContainSingle();
        Sample().Filter("01-Oct-2026").Rows.Should().ContainSingle();
        Sample().Filter("123.45").Rows.Should().ContainSingle("money is matched as shown in rupees");
        Sample().Filter("apollo ravi").Rows.Should().BeEmpty();
        Sample().Filter(" ").Rows.Should().HaveCount(4);
    }

    [Fact]
    public void EnsureValid_RejectsWrongValueTypesAndCounts()
    {
        var wrongType = new ReportTable("X", new[] { ReportColumn.Money("Amount") }, new[] { ReportRow.Of(12.5m) });
        wrongType.Invoking(t => t.EnsureValid()).Should().Throw<InvalidOperationException>().WithMessage("*Amount*");
        var wrongCount = new ReportTable("X", new[] { ReportColumn.Money("Amount") }, new[] { ReportRow.Of(1L, 2L) });
        wrongCount.Invoking(t => t.EnsureValid()).Should().Throw<InvalidOperationException>();
        Sample().Invoking(t => t.EnsureValid()).Should().NotThrow();
    }

    [Fact]
    public void ShowTotalsOff_HidesTheTotalsRow()
    {
        (Sample() with { ShowTotals = false }).HasTotals.Should().BeFalse();
    }

    [Fact]
    public void IndexOf_ThrowsForUnknownColumns()
    {
        Sample().Invoking(t => t.IndexOf("nope")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(12345678L, ReportColumnKind.Money, "1,23,456.78")]
    [InlineData(-5L, ReportColumnKind.Money, "-0.05")]
    [InlineData(1500L, ReportColumnKind.Quantity, "1,500")]
    [InlineData(null, ReportColumnKind.Money, "")]
    public void Cell_FormatsIndianStyle(long? value, ReportColumnKind kind, string expected)
    {
        ReportFormat.Cell(value, kind).Should().Be(expected);
    }

    [Fact]
    public void Cell_FormatsDatesAndPercents()
    {
        ReportFormat.Cell(new DateTime(2026, 4, 1), ReportColumnKind.Date).Should().Be("01-Apr-2026");
        ReportFormat.Cell(12.5m, ReportColumnKind.Percent).Should().Be("12.5%");
        ReportFormat.Cell(12m, ReportColumnKind.Percent).Should().Be("12%");
    }

    [Theory]
    [InlineData(20L, 15, "1 + 5")]
    [InlineData(30L, 15, "2")]
    [InlineData(7L, 1, "7")]
    public void Quantity_ShowsPacksAndLoose(long units, int pack, string expected)
    {
        ReportFormat.Quantity(units, pack).Should().Be(expected);
    }

    [Theory]
    [InlineData("29ABCDE1234F1Z5", "29ABCDE1234F1Z5")]
    [InlineData(" 29abcde1234f1z5 ", "29ABCDE1234F1Z5")]
    [InlineData("ABCDE1234F", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeGstin_AcceptsOnly15CharacterGstins(string? input, string? expected)
    {
        ReportFormat.NormalizeGstin(input).Should().Be(expected);
    }

    [Fact]
    public void PlaceOfSupply_UsesTheGstStateList()
    {
        ReportFormat.PlaceOfSupply("29").Should().Be("29-Karnataka");
        ReportFormat.PlaceOfSupply("7").Should().Be("07-Delhi");
        ReportFormat.PlaceOfSupply("").Should().BeEmpty();
        ReportFormat.StateOfGstin("27AAAPL1234C1ZV").Should().Be("27");
    }
}
