using FluentAssertions;
using MedicalShopManager.Core.Modules.Reports;
using Xunit;

namespace MedicalShopManager.UnitTests.Reports;

/// <summary>Filter defaults, presets (financial year April–March), validation and labels.</summary>
public class ReportsParametersTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    [Theory]
    [InlineData(ReportDatePreset.Today, "2026-11-05", "2026-11-05")]
    [InlineData(ReportDatePreset.Yesterday, "2026-11-04", "2026-11-04")]
    [InlineData(ReportDatePreset.ThisMonth, "2026-11-01", "2026-11-05")]
    [InlineData(ReportDatePreset.LastMonth, "2026-10-01", "2026-10-31")]
    [InlineData(ReportDatePreset.Last30Days, "2026-10-07", "2026-11-05")]
    [InlineData(ReportDatePreset.Last90Days, "2026-08-08", "2026-11-05")]
    [InlineData(ReportDatePreset.ThisFinancialYear, "2026-04-01", "2026-11-05")]
    [InlineData(ReportDatePreset.LastFinancialYear, "2025-04-01", "2026-03-31")]
    public void Presets_GiveTheExpectedRange(ReportDatePreset preset, string from, string to)
    {
        var (f, t) = ReportDateRanges.For(preset, Today);
        f.Should().Be(DateTime.Parse(from));
        t.Should().Be(DateTime.Parse(to));
    }

    [Fact]
    public void LastMonth_InJanuary_IsDecemberOfThePreviousYear()
    {
        ReportDateRanges.For(ReportDatePreset.LastMonth, new DateTime(2027, 1, 15))
            .Should().Be((new DateTime(2026, 12, 1), new DateTime(2026, 12, 31)));
        ReportDateRanges.For(ReportDatePreset.ThisFinancialYear, new DateTime(2027, 2, 1)).From.Should().Be(new DateTime(2026, 4, 1));
    }

    [Fact]
    public void Validate_RejectsReversedAndTooLongRanges()
    {
        var definition = ReportCatalog.Get(ReportIds.SalesRegister);
        new ReportParameters { From = Today, To = Today.AddDays(-1) }.Validate(definition)
            .Should().ContainSingle().Which.Should().Contain("after");
        new ReportParameters { From = Today.AddDays(-500), To = Today }.Validate(definition)
            .Should().ContainSingle().Which.Should().Contain("400");
        new ReportParameters { From = Today.AddDays(-399), To = Today }.Validate(definition).Should().BeEmpty();
    }

    [Fact]
    public void DayBook_IsLimitedToAQuarter()
    {
        var definition = ReportCatalog.Get(ReportIds.DayBook);
        new ReportParameters { From = Today.AddDays(-91), To = Today }.Validate(definition).Should().BeEmpty();
        new ReportParameters { From = Today.AddDays(-92), To = Today }.Validate(definition).Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_ChecksNumberBoundsAndChoiceKeys()
    {
        var definition = ReportCatalog.Get(ReportIds.ExpiryBySupplier);
        new ReportParameters { Number = 0 }.Validate(definition).Should().ContainSingle().Which.Should().Contain("between 1 and 730");
        new ReportParameters { Number = 30 }.Validate(definition).Should().BeEmpty();
        new ReportParameters { Number = 30, Choices = new Dictionary<string, string> { [ReportChoiceKeys.ExpiryState] = "bogus" } }
            .Validate(definition).Should().ContainSingle().Which.Should().Contain("valid option");
    }

    [Fact]
    public void Choice_FallsBackToTheDefault_ForMissingOrUnknownKeys()
    {
        var definition = ReportCatalog.Get(ReportIds.StockValuation);
        var parameters = new ReportParameters { Choices = new Dictionary<string, string> { [ReportChoiceKeys.GroupBy] = "nonsense" } };
        parameters.Choice(definition, ReportChoiceKeys.GroupBy).Should().Be(ReportChoiceKeys.Batch);
        parameters.Choice(definition, ReportChoiceKeys.ExpiryState).Should().Be(ReportChoiceKeys.IncludeExpired);
        var act = () => parameters.Choice(definition, "unknown");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NumberOr_UsesTheDefinitionDefault()
    {
        var definition = ReportCatalog.Get(ReportIds.ExpiryBySupplier);
        new ReportParameters().NumberOr(definition).Should().Be(90);
        new ReportParameters { Number = 30 }.NumberOr(definition).Should().Be(30);
    }

    [Fact]
    public void PeriodLabel_ShowsRangeSingleDayOrAsOn()
    {
        var sales = ReportCatalog.Get(ReportIds.SalesRegister);
        new ReportParameters { From = new DateTime(2026, 4, 1), To = new DateTime(2026, 4, 30) }.PeriodLabel(sales)
            .Should().Be("01-Apr-2026 to 30-Apr-2026");
        new ReportParameters { From = Today, To = Today }.PeriodLabel(sales).Should().Be("05-Nov-2026");
        new ReportParameters { AsOf = Today }.PeriodLabel(ReportCatalog.Get(ReportIds.StockValuation)).Should().Be("As on 05-Nov-2026");
        new ReportParameters { AsOf = Today }.PeriodLabel(ReportCatalog.Get(ReportIds.CustomerOutstanding)).Should().Be("As on 05-Nov-2026");
    }

    [Fact]
    public void ToExclusive_IsTheDayAfterTo()
    {
        new ReportParameters { To = new DateTime(2026, 10, 31, 18, 30, 0) }.ToExclusive.Should().Be(new DateTime(2026, 11, 1));
    }
}
