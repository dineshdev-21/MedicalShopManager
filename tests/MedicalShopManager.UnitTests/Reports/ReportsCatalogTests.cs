using FluentAssertions;
using MedicalShopManager.Core.Modules.Reports;
using Xunit;

namespace MedicalShopManager.UnitTests.Reports;

/// <summary>The report catalogue: stable unique ids, every report reachable, search, and valid default filters.</summary>
public class ReportsCatalogTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    [Fact]
    public void Ids_AreUniqueAndCoverEveryRequiredReport()
    {
        var ids = ReportCatalog.All.Select(d => d.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().Contain(new[]
        {
            ReportIds.SalesRegister, ReportIds.PurchaseRegister, ReportIds.SaleReturns, ReportIds.PurchaseReturns, ReportIds.Gstr1,
            ReportIds.Gstr3b, ReportIds.HsnSummary, ReportIds.DayBook, ReportIds.CashBook, ReportIds.ItemProfit, ReportIds.BillProfit,
            ReportIds.StockValuation, ReportIds.ExpiryBySupplier, ReportIds.StockMovement, ReportIds.H1Register, ReportIds.NarcoticRegister,
            ReportIds.CustomerOutstanding, ReportIds.SupplierOutstanding, ReportIds.DoctorSales, ReportIds.ExpenseSummary, ReportIds.ProfitLoss
        });
    }

    [Fact]
    public void EveryCategory_HasAtLeastOneReport()
    {
        foreach (var category in Enum.GetValues<ReportCategory>())
            ReportCatalog.Search(null, category).Should().NotBeEmpty($"category {category} is shown in the catalogue");
    }

    [Theory]
    [InlineData("gstr", ReportIds.Gstr1)]
    [InlineData("B2CS", ReportIds.Gstr1)]
    [InlineData("h1", ReportIds.H1Register)]
    [InlineData("ndps", ReportIds.NarcoticRegister)]
    [InlineData("dead stock", ReportIds.StockMovement)]
    [InlineData("udhar", ReportIds.CustomerOutstanding)]
    [InlineData("expiry supplier", ReportIds.ExpiryBySupplier)]
    public void Search_MatchesNamesAndKeywords(string text, string expectedId)
    {
        ReportCatalog.Search(text).Select(d => d.Id).Should().Contain(expectedId);
    }

    [Fact]
    public void Search_AllTermsMustMatch_AndBlankReturnsEverything()
    {
        ReportCatalog.Search("gstr zzz-no-such-word").Should().BeEmpty();
        ReportCatalog.Search("  ").Should().HaveCount(ReportCatalog.All.Count);
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndGetThrowsForUnknownIds()
    {
        ReportCatalog.Find("GSTR-1")!.Id.Should().Be(ReportIds.Gstr1);
        ReportCatalog.Find(null).Should().BeNull();
        var act = () => ReportCatalog.Get("nope");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void DefaultParameters_AreValidForEveryReport()
    {
        foreach (var definition in ReportCatalog.All)
        {
            var parameters = ReportParameters.DefaultsFor(definition, Today);
            parameters.Validate(definition).Should().BeEmpty($"defaults of {definition.Id} must run without edits");
            foreach (var choice in definition.Parameters.Choices)
                choice.HasOption(choice.DefaultKey).Should().BeTrue($"{definition.Id}.{choice.Key} default must be an option");
        }
    }
}
