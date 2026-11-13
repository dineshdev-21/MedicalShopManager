using FluentAssertions;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Modules.Reports;
using Moq;
using Xunit;

namespace MedicalShopManager.UnitTests.Reports;

/// <summary>Runner dispatch / validation / error wrapping, favourites service, and Excel export helpers.</summary>
public class ReportsServicesTests
{
    private static ReportResult EmptyResult(ReportDefinition d, ReportParameters p) => new() { Definition = d, Parameters = p };

    private static Mock<IReportProvider> Provider(params string[] ids)
    {
        var mock = new Mock<IReportProvider>();
        mock.SetupGet(x => x.ReportIds).Returns(ids);
        mock.Setup(x => x.RunAsync(It.IsAny<ReportDefinition>(), It.IsAny<ReportParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReportDefinition d, ReportParameters p, CancellationToken _) => EmptyResult(d, p));
        return mock;
    }

    [Fact]
    public async Task Runner_DispatchesToTheOwningProvider()
    {
        var sales = Provider(ReportIds.SalesRegister);
        var gst = Provider(ReportIds.Gstr1);
        var runner = new ReportRunner(new[] { sales.Object, gst.Object });

        var result = await runner.RunAsync("GSTR-1", new ReportParameters { From = new DateTime(2026, 10, 1), To = new DateTime(2026, 10, 31) });

        result.Definition.Id.Should().Be(ReportIds.Gstr1);
        gst.Verify(x => x.RunAsync(It.Is<ReportDefinition>(d => d.Id == ReportIds.Gstr1), It.IsAny<ReportParameters>(), It.IsAny<CancellationToken>()), Times.Once);
        sales.Verify(x => x.RunAsync(It.IsAny<ReportDefinition>(), It.IsAny<ReportParameters>(), It.IsAny<CancellationToken>()), Times.Never);
        runner.CanRun(ReportIds.SalesRegister).Should().BeTrue();
        runner.CanRun(ReportIds.CashBook).Should().BeFalse();
    }

    [Fact]
    public async Task Runner_RejectsInvalidFilters_BeforeQuerying()
    {
        var provider = Provider(ReportIds.SalesRegister);
        var runner = new ReportRunner(new[] { provider.Object });
        var act = () => runner.RunAsync(ReportIds.SalesRegister, new ReportParameters { From = new DateTime(2026, 10, 31), To = new DateTime(2026, 10, 1) });
        await act.Should().ThrowAsync<ReportException>().WithMessage("*after*");
        provider.Verify(x => x.RunAsync(It.IsAny<ReportDefinition>(), It.IsAny<ReportParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Runner_UnknownOrUnprovidedReports_GiveFriendlyErrors()
    {
        var runner = new ReportRunner(Array.Empty<IReportProvider>());
        await runner.Invoking(r => r.RunAsync("nope", new ReportParameters())).Should().ThrowAsync<ReportException>().WithMessage("Unknown report*");
        await runner.Invoking(r => r.RunAsync(ReportIds.CashBook, new ReportParameters())).Should().ThrowAsync<ReportException>().WithMessage("*not available*");
    }

    [Fact]
    public async Task Runner_WrapsProviderFailures_ButLetsCancellationThrough()
    {
        var failing = new Mock<IReportProvider>();
        failing.SetupGet(x => x.ReportIds).Returns(new[] { ReportIds.DayBook });
        failing.Setup(x => x.RunAsync(It.IsAny<ReportDefinition>(), It.IsAny<ReportParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no such table: Sales"));
        var runner = new ReportRunner(new[] { failing.Object });
        await runner.Invoking(r => r.RunAsync(ReportIds.DayBook, new ReportParameters()))
            .Should().ThrowAsync<ReportException>().WithMessage("Could not prepare 'Day Book'. no such table: Sales");

        failing.Setup(x => x.RunAsync(It.IsAny<ReportDefinition>(), It.IsAny<ReportParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        await runner.Invoking(r => r.RunAsync(ReportIds.DayBook, new ReportParameters())).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Runner_RejectsSheetsWithMistypedValues()
    {
        var bad = new Mock<IReportProvider>();
        bad.SetupGet(x => x.ReportIds).Returns(new[] { ReportIds.DayBook });
        bad.Setup(x => x.RunAsync(It.IsAny<ReportDefinition>(), It.IsAny<ReportParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReportDefinition d, ReportParameters p, CancellationToken _) => EmptyResult(d, p) with
            {
                Sheets = new[] { new ReportTable("X", new[] { ReportColumn.Money("Amount") }, new[] { ReportRow.Of(1.5m) }) }
            });
        await new ReportRunner(new[] { bad.Object }).Invoking(r => r.RunAsync(ReportIds.DayBook, new ReportParameters()))
            .Should().ThrowAsync<ReportException>();
    }

    [Fact]
    public void Runner_RejectsTwoProvidersForOneReport()
    {
        var act = () => new ReportRunner(new[] { Provider(ReportIds.Gstr1).Object, Provider(ReportIds.Gstr1).Object });
        act.Should().Throw<InvalidOperationException>().WithMessage("*gstr-1*");
    }

    [Fact]
    public async Task CatalogService_StoresFavouritesPerUser_AndAnonymousAsZero()
    {
        var repository = new Mock<IReportFavouritesRepository>();
        repository.Setup(x => x.GetAsync(0)).ReturnsAsync(new HashSet<string> { ReportIds.Gstr1 });
        var service = new ReportCatalogService(repository.Object);

        (await service.GetFavouritesAsync(null)).Should().Contain(ReportIds.Gstr1);
        (await service.SetFavouriteAsync(7, "GSTR-1", true)).Should().BeTrue();
        repository.Verify(x => x.SetAsync(7, ReportIds.Gstr1, true), Times.Once);
        await service.Invoking(s => s.SetFavouriteAsync(7, "unknown", true)).Should().ThrowAsync<ReportException>();
        service.Reports.Should().BeSameAs(ReportCatalog.All);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+91 98450", "'+91 98450")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@x", "'@x")]
    [InlineData("Dolo 650", "Dolo 650")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Sanitize_NeutralisesFormulaInjection(string? input, string expected)
    {
        ReportExcelExporter.Sanitize(input).Should().Be(expected);
    }

    [Fact]
    public void UniqueSheetName_CleansTruncatesAndDeduplicates()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReportExcelExporter.UniqueSheetName("hsn(b2b)", used).Should().Be("hsn(b2b)");
        ReportExcelExporter.UniqueSheetName("a/b:c?d*e[f]", used).Should().Be("a-b-c-d-e-f-");
        var longName = new string('x', 40);
        ReportExcelExporter.UniqueSheetName(longName, used).Should().HaveLength(31);
        ReportExcelExporter.UniqueSheetName(longName, used).Should().EndWith(" (2)").And.HaveLength(31);
        ReportExcelExporter.UniqueSheetName("HSN(B2B)", used).Should().Be("HSN(B2B) (2)");
    }
}
