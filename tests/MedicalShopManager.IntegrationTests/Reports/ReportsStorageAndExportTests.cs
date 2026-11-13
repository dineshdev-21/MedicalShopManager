using ClosedXML.Excel;
using FluentAssertions;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database.Migrations;
using MedicalShopManager.Infrastructure.Modules.Reports;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Reports;

/// <summary>Migration 18, favourites storage, empty-database behaviour and the Excel export (re-read with ClosedXML).</summary>
public class ReportsStorageAndExportTests : IAsyncLifetime
{
    private readonly ReportsTestDatabase _empty = new() { Seed = false };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"MSM_ReportExport_{Guid.NewGuid():N}");

    public Task InitializeAsync() => _empty.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _empty.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private sealed class RecordingAudit : IAuditService
    {
        public List<(string Action, string? NewValues)> Entries { get; } = new();

        public Task LogAsync(string action, string? entityType = null, long? entityId = null, string? oldValues = null, string? newValues = null,
            string? reason = null)
        {
            Entries.Add((action, newValues));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditLog>> GetLogsAsync(DateTime from, DateTime to) => Task.FromResult<IReadOnlyList<AuditLog>>(Array.Empty<AuditLog>());
        public Task<IReadOnlyList<AuditLog>> GetLogsByUserAsync(long userId) => Task.FromResult<IReadOnlyList<AuditLog>>(Array.Empty<AuditLog>());
    }

    [Fact]
    public async Task Migration18_CreatesFavouritesAndSeedsNamespacedSettings()
    {
        MigrationCatalog.Discover().Should().Contain(m => m.Version == 18 && m is Migration0018Reports);
        (await _empty.Db.GetAppliedVersionsAsync()).Should().Contain(18);
        (await _empty.ExecAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ReportFavourites'")).Should().Be(1);
        (await _empty.ExecAsync("SELECT CAST(Value AS INTEGER) FROM AppSettings WHERE Key = 'Reports.B2clThresholdPaisa'")).Should().Be(10_000_000);
        (await _empty.ExecAsync("SELECT COUNT(*) FROM AppSettings WHERE Key = 'Reports.HsnUqc' AND Value = 'NOS'")).Should().Be(1);
    }

    [Fact]
    public async Task Favourites_ArePerUserAndIdempotent()
    {
        var repository = new ReportFavouritesRepository(_empty.Db);
        await repository.SetAsync(1, ReportIds.Gstr1, true);
        await repository.SetAsync(1, ReportIds.Gstr1, true);
        await repository.SetAsync(1, ReportIds.H1Register, true);
        await repository.SetAsync(2, ReportIds.CashBook, true);
        (await repository.GetAsync(1)).Should().BeEquivalentTo(new[] { ReportIds.Gstr1, ReportIds.H1Register });
        await repository.SetAsync(1, ReportIds.Gstr1, false);
        (await repository.GetAsync(1)).Should().BeEquivalentTo(new[] { ReportIds.H1Register });
        (await repository.GetAsync(2)).Should().BeEquivalentTo(new[] { ReportIds.CashBook });
        (await repository.GetAsync(3)).Should().BeEmpty();
    }

    [Fact]
    public async Task EveryReport_OnAnEmptyDatabase_ReturnsEmptySheetsWithoutErrors()
    {
        foreach (var definition in ReportCatalog.All)
        {
            var result = await _empty.Runner().RunAsync(definition.Id, ReportParameters.DefaultsFor(definition, new DateTime(2026, 11, 5)));
            result.Sheets.Should().NotBeEmpty();
            if (definition.Id is ReportIds.Gstr3b or ReportIds.CashBook or ReportIds.ProfitLoss) continue; // fixed statement rows
            if (definition.Id == ReportIds.Gstr1)
            {
                result.Sheets.Where(s => s.Name != Gstr1Builder.Exemp).Should().OnlyContain(s => !s.HasRows);
                continue;
            }
            result.IsEmpty.Should().BeTrue($"{definition.Id} has no data to show");
        }
    }

    [Fact]
    public async Task Gst_WithoutShopState_WarnsInsteadOfFailing()
    {
        var result = await _empty.RunAsync(ReportIds.Gstr1);
        result.Notes.Should().Contain(n => n.Contains("state is not set"));
    }

    [Fact]
    public async Task ExcelExport_WritesTypedCellsTotalsAndSanitisedText_AndAudits()
    {
        var definition = ReportCatalog.Get(ReportIds.SalesRegister);
        var result = new ReportResult
        {
            Definition = definition,
            Parameters = new ReportParameters { From = new DateTime(2026, 10, 1), To = new DateTime(2026, 10, 31) },
            Sheets = new[]
            {
                new ReportTable("Sales register", new[] { ReportColumn.Date("Date"), ReportColumn.Text("Customer"), ReportColumn.Money("Amount") }, new[]
                {
                    ReportRow.Of(new DateTime(2026, 10, 5), "=HYPERLINK(\"x\")", 12345L),
                    ReportRow.Of(new DateTime(2026, 10, 6), "Ravi", 55L)
                })
            },
            Notes = new[] { "A note" }
        };
        var audit = new RecordingAudit();
        var path = Path.Combine(_dir, "sales.xlsx");

        await new ReportExcelExporter(audit).ExportExcelAsync(result, path, shopName: "Sri Medicals");

        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheets.Select(w => w.Name).Should().Equal("Sales register", "Notes");
            var ws = workbook.Worksheet("Sales register");
            ws.Cell(1, 1).GetString().Should().Be("Sri Medicals");
            ws.Cell(2, 1).GetString().Should().Be("Sales Register");
            ws.Cell(3, 1).GetString().Should().Be("01-Oct-2026 to 31-Oct-2026");
            ws.Cell(6, 1).GetString().Should().Be("Date");
            ws.Cell(7, 1).GetDateTime().Should().Be(new DateTime(2026, 10, 5));
            ws.Cell(7, 2).HasFormula.Should().BeFalse("text starting with = is stored as quoted text, never as a formula");
            ws.Cell(7, 2).DataType.Should().Be(XLDataType.Text);
            ws.Cell(7, 3).GetDouble().Should().Be(123.45);
            ws.Cell(9, 1).GetString().Should().Be("Total");
            ws.Cell(9, 3).GetDouble().Should().Be(124.00);
        }
        audit.Entries.Should().ContainSingle().Which.NewValues.Should().Contain("sales.xlsx").And.Contain("2 rows");
    }

    [Fact]
    public async Task ExcelExport_PortalSheets_HaveHeaderInRow1AndDatesAsText()
    {
        var definition = ReportCatalog.Get(ReportIds.Gstr1);
        var sheets = Gstr1Builder.Build(new[]
        {
            new GstInvoiceLine { SaleId = 1, InvoiceNumber = "INV-1", InvoiceDate = new DateTime(2026, 10, 5), CustomerGstin = "29AAACA1111A1Z1",
                InvoiceValuePaisa = 11200, Rate = 12, TaxablePaisa = 10000, CgstPaisa = 600, SgstPaisa = 600 }
        }, Array.Empty<GstNoteLine>(), Array.Empty<GstHsnLine>(), Array.Empty<GstDocumentSummary>(), new GstReturnContext("29"), new List<string>());
        var result = new ReportResult { Definition = definition, Parameters = new ReportParameters(), Sheets = sheets };
        var path = Path.Combine(_dir, "gstr1.xlsx");

        await new ReportExcelExporter().ExportExcelAsync(result, path);

        using var workbook = new XLWorkbook(path);
        workbook.Worksheets.Select(w => w.Name).Should().StartWith(new[] { "b2b", "b2cl", "b2cs" }).And.Contain("hsn(b2b)");
        var b2b = workbook.Worksheet("b2b");
        b2b.Cell(1, 1).GetString().Should().Be("GSTIN/UIN of Recipient");
        b2b.Cell(2, 1).GetString().Should().Be("29AAACA1111A1Z1");
        b2b.Cell(2, 4).GetString().Should().Be("05-Oct-2026");
        b2b.Cell(2, 5).GetDouble().Should().Be(112.00);
        b2b.Cell(2, 11).GetDouble().Should().Be(12);
        b2b.Cell(3, 1).IsEmpty().Should().BeTrue("portal sheets have no totals row");
    }

    [Fact]
    public async Task ExcelExport_RejectsEmptyRequests()
    {
        var result = new ReportResult { Definition = ReportCatalog.Get(ReportIds.DayBook), Parameters = new ReportParameters() };
        await new ReportExcelExporter().Invoking(e => e.ExportExcelAsync(result, Path.Combine(_dir, "x.xlsx")))
            .Should().ThrowAsync<ReportException>().WithMessage("*nothing to export*");
        await new ReportExcelExporter().Invoking(e => e.ExportExcelAsync(result, " "))
            .Should().ThrowAsync<ReportException>();
    }
}
