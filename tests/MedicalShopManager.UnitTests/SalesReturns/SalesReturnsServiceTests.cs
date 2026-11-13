using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.SalesReturns;
using MedicalShopManager.Infrastructure.Modules.SalesReturns;
using Moq;
using Xunit;

namespace MedicalShopManager.UnitTests.SalesReturns;

/// <summary>SaleReturnService and SalesRegisterService orchestration with mocked repositories.</summary>
public class SalesReturnsServiceTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    private readonly Mock<ISaleReturnRepository> _repo = new();
    private readonly Mock<IDocumentNumberService> _numbers = new();
    private readonly Mock<IAppSettingsRepository> _settings = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<IExcelSyncQueueRepository> _sync = new();

    private SaleReturnService Service() =>
        new(_repo.Object, _numbers.Object, _settings.Object, _audit.Object, _sync.Object, NullLogger<SaleReturnService>.Instance, () => Today);

    private static ReturnableSale Sale() => new()
    {
        SaleId = 7, InvoiceNumber = "INV-7", InvoiceDate = Today.AddDays(-2), GrandTotalPaisa = 25100, AmountPaidPaisa = 25100,
        Lines =
        {
            new ReturnableSaleLine
            {
                SaleItemId = 11, MedicineId = 1, MedicineName = "Dolo 650", BatchId = 5, BatchNumber = "B1", ExpiryDate = Today.AddYears(1),
                UnitsPerPack = 15, SoldUnits = 20, RatePerPackPaisa = 18840, GstPercent = 12m, LineTotalPaisa = 25120, TaxablePaisa = 22429
            }
        }
    };

    private static SaleReturnRequest Request(long units = 20) => new()
    {
        SaleId = 7, ReturnDate = Today, Lines = new[] { new SaleReturnLineRequest { SaleItemId = 11, MedicineId = 1, BatchId = 5, Units = units } }
    };

    [Fact]
    public async Task Post_InvalidReturn_ThrowsWithEveryMessage_AndWritesNothing()
    {
        _repo.Setup(r => r.GetReturnableSaleAsync(7)).ReturnsAsync(Sale());

        var act = () => Service().PostAsync(Request(25) with { ReturnDate = Today.AddDays(1) });

        var ex = (await act.Should().ThrowAsync<SaleReturnValidationException>()).Which;
        ex.Errors.Should().HaveCount(2);
        _repo.Verify(r => r.InsertAsync(It.IsAny<SaleReturnDocument>()), Times.Never);
        _audit.Verify(a => a.LogAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Post_Valid_InsertsTheComputedDocument_AuditsAndQueuesSync()
    {
        _repo.Setup(r => r.GetReturnableSaleAsync(7)).ReturnsAsync(Sale());
        SaleReturnDocument? inserted = null;
        _repo.Setup(r => r.InsertAsync(It.IsAny<SaleReturnDocument>()))
            .Callback<SaleReturnDocument>(d => inserted = d)
            .ReturnsAsync((42L, "SR/2627/00001", "CN/2627/00001"));
        _repo.Setup(r => r.GetAsync(42)).ReturnsAsync(() => inserted);

        var posted = await Service().PostAsync(Request());

        inserted!.NetAmountPaisa.Should().Be(25100);
        inserted.RoundOffPaisa.Should().Be(-20);
        posted.ReturnNumber.Should().Be("SR/2627/00001");
        posted.CreditNoteNumber.Should().Be("CN/2627/00001");
        _audit.Verify(a => a.LogAsync("CreateSaleReturn", "SaleReturn", 42, null, It.IsAny<string?>(),
            It.Is<string?>(s => s!.Contains("CN/2627/00001") && s.Contains("INV-7"))), Times.Once);
        _sync.Verify(s => s.EnqueueAsync(SyncEntityType.Return, 42), Times.Once);
        _sync.Verify(s => s.EnqueueAsync(SyncEntityType.Batch, 5), Times.Once);
    }

    [Fact]
    public async Task Post_AuditFailure_DoesNotUndoAPostedReturn()
    {
        _repo.Setup(r => r.GetReturnableSaleAsync(7)).ReturnsAsync(Sale());
        _repo.Setup(r => r.InsertAsync(It.IsAny<SaleReturnDocument>())).ReturnsAsync((42L, "SR/1", "CN/1"));
        _audit.Setup(a => a.LogAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>())).ThrowsAsync(new InvalidOperationException("disk full"));
        _sync.Setup(s => s.EnqueueAsync(It.IsAny<SyncEntityType>(), It.IsAny<long?>())).ThrowsAsync(new InvalidOperationException("locked"));

        var posted = await Service().PostAsync(Request());

        posted.CreditNoteNumber.Should().Be("CN/1");
    }

    [Fact]
    public async Task Post_WithoutBill_LoadsTheBatchesItPrices()
    {
        _repo.Setup(r => r.GetBatchesAsync(It.IsAny<IEnumerable<long>>())).ReturnsAsync(new Dictionary<long, SaleReturnBatchInfo>
        {
            [5] = new() { BatchId = 5, MedicineId = 1, MedicineName = "Dolo 650", BatchNumber = "B1", ExpiryDate = Today.AddYears(1), UnitsPerPack = 15,
                MrpPaisa = 18840, GstPercent = 12m }
        });
        _repo.Setup(r => r.InsertAsync(It.IsAny<SaleReturnDocument>())).ReturnsAsync((1L, "SR/1", "CN/1"));
        var request = new SaleReturnRequest
        {
            ReturnDate = Today, Lines = new[] { new SaleReturnLineRequest { MedicineId = 1, BatchId = 5, Units = 15, RatePerPackPaisa = 18840 } }
        };

        var posted = await Service().PostAsync(request);

        posted.NetAmountPaisa.Should().Be(18800);
        _repo.Verify(r => r.GetReturnableSaleAsync(It.IsAny<long>()), Times.Never);
    }

    [Theory]
    [InlineData(null, RoundOffMode.NearestRupee)]
    [InlineData("1", RoundOffMode.NearestRupee)]
    [InlineData("0", RoundOffMode.None)]
    [InlineData("false", RoundOffMode.None)]
    public async Task RoundOff_ComesFromTheModuleSetting(string? value, RoundOffMode expected)
    {
        _settings.Setup(s => s.GetValueAsync(SaleReturnService.RoundOffSettingKey)).ReturnsAsync(value);
        (await Service().GetRoundOffAsync()).Should().Be(expected);
    }

    [Fact]
    public async Task PeekNumbers_UsesSaleReturnAndCreditNoteSequences()
    {
        _numbers.Setup(n => n.PeekNextAsync(DocumentType.SaleReturn, Today)).ReturnsAsync("SR/2627/00009");
        _numbers.Setup(n => n.PeekNextAsync(DocumentType.CreditNote, Today)).ReturnsAsync("CN/2627/00004");
        (await Service().PeekNumbersAsync(Today)).Should().Be(("SR/2627/00009", "CN/2627/00004"));
    }

    [Fact]
    public async Task RefundCredit_ValidatesAgainstOpenCredit_BeforeWriting()
    {
        _repo.Setup(r => r.GetAsync(3)).ReturnsAsync(new SaleReturnDocument
        {
            Id = 3, CreditNoteNumber = "CN/3", ReturnDate = Today, NetAmountPaisa = 5000, RefundedPaisa = 4000
        });
        var refund = new CreditRefundRequest { SaleReturnId = 3, AmountPaisa = 2000, RefundDate = Today };

        await FluentActions.Invoking(() => Service().RefundCreditAsync(refund)).Should().ThrowAsync<SaleReturnValidationException>();
        _repo.Verify(r => r.AddRefundAsync(It.IsAny<CreditRefundRequest>(), It.IsAny<string>()), Times.Never);

        await Service().RefundCreditAsync(refund with { AmountPaisa = 1000 });
        _repo.Verify(r => r.AddRefundAsync(It.Is<CreditRefundRequest>(x => x.AmountPaisa == 1000), "CN/3"), Times.Once);
        _audit.Verify(a => a.LogAsync("RefundCreditNote", "SaleReturn", 3, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_NeedsAReason_ThenCancelsAndAudits()
    {
        _repo.Setup(r => r.GetAsync(3)).ReturnsAsync(new SaleReturnDocument { Id = 3, ReturnNumber = "SR/3", CreditNoteNumber = "CN/3" });

        await FluentActions.Invoking(() => Service().CancelAsync(3, "", 1)).Should().ThrowAsync<SaleReturnValidationException>();
        _repo.Verify(r => r.CancelAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long?>()), Times.Never);

        await Service().CancelAsync(3, "  Entered twice  ", 1);
        _repo.Verify(r => r.CancelAsync(3, "Entered twice", 1), Times.Once);
        _audit.Verify(a => a.LogAsync("CancelSaleReturn", "SaleReturn", 3, It.IsAny<string?>(), null, It.Is<string?>(s => s!.Contains("Entered twice"))),
            Times.Once);
    }

    [Fact]
    public async Task Cancel_UnknownReturn_IsAValidationError()
    {
        await FluentActions.Invoking(() => Service().CancelAsync(99, "reason", null)).Should().ThrowAsync<SaleReturnValidationException>();
    }

    [Fact]
    public async Task List_RejectsReversedDates_ButNotForOpenCredit()
    {
        _repo.Setup(r => r.ListAsync(It.IsAny<SaleReturnFilter>())).ReturnsAsync(Array.Empty<SaleReturnRow>());
        var reversed = new SaleReturnFilter { From = Today, To = Today.AddDays(-3) };

        await FluentActions.Invoking(() => Service().ListAsync(reversed)).Should().ThrowAsync<SaleReturnValidationException>();
        var (rows, totals) = await Service().ListAsync(reversed with { OpenCreditOnly = true });
        rows.Should().BeEmpty();
        totals.Count.Should().Be(0);
    }

    [Fact]
    public async Task Register_ValidatesTheFilter_AndAddsTotals()
    {
        var register = new Mock<ISalesRegisterRepository>();
        register.Setup(r => r.QueryAsync(It.IsAny<SalesRegisterFilter>())).ReturnsAsync(new[]
        {
            new SalesRegisterRow { SaleId = 1, GrandTotalPaisa = 1000, AmountPaidPaisa = 1000 },
            new SalesRegisterRow { SaleId = 2, GrandTotalPaisa = 500, IsCancelled = true }
        });
        var service = new SalesRegisterService(register.Object);

        var (rows, totals) = await service.QueryAsync(new SalesRegisterFilter { From = Today, To = Today });
        rows.Should().HaveCount(2);
        totals.GrandTotalPaisa.Should().Be(1000);
        await FluentActions.Invoking(() => service.QueryAsync(new SalesRegisterFilter { From = Today, To = Today.AddDays(-1) }))
            .Should().ThrowAsync<SaleReturnValidationException>();
    }
}
