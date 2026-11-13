using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Stock;
using MedicalShopManager.Infrastructure.Modules.Stock;
using Xunit;

namespace MedicalShopManager.UnitTests.Stock;

/// <summary>Rules of <see cref="StockService"/> with the repositories mocked (no database) and a fixed clock.</summary>
public class StockServiceTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Today.AddHours(10), TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private readonly Mock<IStockRepository> _stock = new();
    private readonly Mock<IStockAdjustmentRepository> _postings = new();
    private readonly Mock<IAppSettingsRepository> _settings = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IDocumentNumberService> _numbers = new();
    private readonly Mock<IExcelSyncQueueRepository> _excel = new();

    private readonly Medicine _dolo = new()
    {
        Id = 1, Name = "Dolo 650", Manufacturer = "Micro Labs", Composition = "Paracetamol 650mg", UnitsPerPack = 15, PackLabel = "15's",
        AllowLooseSale = true, GstRatePercent = 12m, MinStock = 20, MaxStock = 300, RackLocation = "A1"
    };

    public StockServiceTests()
    {
        _auth.SetupGet(a => a.CurrentUser).Returns(new User { Id = 5, Username = "ravi", DisplayName = "Ravi" });
        _stock.Setup(s => s.GetSnapshotAsync(1)).ReturnsAsync(() => new StockItemSnapshot(_dolo, new[]
        {
            new MedicineBatch { Id = 10, MedicineId = 1, BatchNumber = "B1", QuantityRemaining = 30, QuantityReceived = 30, UnitsPerPack = 15,
                ExpiryDate = Today.AddDays(40), PurchaseRatePaisa = 12000, MrpPaisa = 18840, SaleRatePaisa = 18840, GstRatePercent = 12m },
            new MedicineBatch { Id = 11, MedicineId = 1, BatchNumber = "B0", QuantityRemaining = 0, QuantityReceived = 15, UnitsPerPack = 15,
                ExpiryDate = Today.AddDays(400), PurchaseRatePaisa = 12000, MrpPaisa = 18840, SaleRatePaisa = 18840, GstRatePercent = 12m }
        }));
    }

    private StockService Service() => new(_stock.Object, _postings.Object, _settings.Object, _audit.Object, _auth.Object,
        _numbers.Object, _excel.Object, NullLogger<StockService>.Instance, new FixedClock());

    // ───────── settings ─────────

    [Theory]
    [InlineData(null, 90)]
    [InlineData("60", 60)]
    [InlineData("abc", 90)]
    [InlineData("0", 90)]
    [InlineData("1000", 90)]
    public async Task NearExpiryDays_ComesFromTheSettingWithASaneDefault(string? raw, int expected)
    {
        _settings.Setup(s => s.GetValueAsync("NearExpiryDays")).ReturnsAsync(raw);
        (await Service().GetNearExpiryDaysAsync()).Should().Be(expected);
    }

    [Fact]
    public async Task ShowZeroBatches_IsRememberedInItsOwnSetting()
    {
        _settings.Setup(s => s.GetValueAsync(StockService.ShowZeroBatchesKey)).ReturnsAsync("1");
        (await Service().GetShowZeroBatchesAsync()).Should().BeTrue();
        await Service().SetShowZeroBatchesAsync(false);
        _settings.Verify(s => s.SetValueAsync("Stock.ShowZeroBatches", "0", It.IsAny<string?>()), Times.Once);
    }

    // ───────── reads ─────────

    [Fact]
    public async Task ItemDetail_UsesTheClock_AndHidesZeroBatchesUnlessAsked()
    {
        var detail = await Service().GetItemDetailAsync(1, showZeroBatches: false);
        detail!.Batches.Should().ContainSingle(b => b.BatchNumber == "B1");
        detail.Batches[0].Status.Should().Be(BatchStockStatus.NearExpiry);
        detail.Header.StockUnits.Should().Be(30);
        (await Service().GetItemDetailAsync(1, showZeroBatches: true))!.Batches.Should().HaveCount(2);
        (await Service().GetItemDetailAsync(99, false)).Should().BeNull();
    }

    [Fact]
    public async Task StockList_SummarisesAndFiltersSnapshots()
    {
        var crocin = new Medicine { Id = 2, Name = "Crocin", UnitsPerPack = 10 };
        _stock.Setup(s => s.GetSnapshotsAsync(false)).ReturnsAsync(new[]
        {
            (await _stock.Object.GetSnapshotAsync(1))!, new StockItemSnapshot(crocin, Array.Empty<MedicineBatch>())
        });
        var result = await Service().GetStockListAsync(new StockListQuery { Filter = StockFilter.InStock });
        result.Items.Should().ContainSingle(i => i.Name == "Dolo 650");
        result.Summary.StockValuePaisa.Should().Be(24000);
        (await Service().GetStockListAsync(new StockListQuery { Filter = StockFilter.OutOfStock })).Items.Should().ContainSingle(i => i.Name == "Crocin");
    }

    [Fact]
    public async Task Substitutes_ListInStockItemsFirst()
    {
        _stock.Setup(s => s.GetSubstituteSnapshotsAsync(1)).ReturnsAsync(new[]
        {
            new StockItemSnapshot(new Medicine { Id = 3, Name = "Alpha", UnitsPerPack = 10 }, Array.Empty<MedicineBatch>()),
            new StockItemSnapshot(new Medicine { Id = 4, Name = "Zeta", UnitsPerPack = 10, PackLabel = "10's" }, new[]
            {
                new MedicineBatch { Id = 40, MedicineId = 4, QuantityRemaining = 25, UnitsPerPack = 10, ExpiryDate = Today.AddYears(1), MrpPaisa = 5000, IsActive = true }
            })
        });
        var subs = await Service().GetSubstitutesAsync(1);
        subs.Select(s => s.Name).Should().Equal("Zeta", "Alpha");
        subs[0].StockDisplay.Should().Be("2 + 5");
        subs[0].MrpPaisa.Should().Be(5000);
    }

    [Fact]
    public async Task Timeline_IsBuiltFromTheMovements()
    {
        _stock.Setup(s => s.GetMovementsAsync(1)).ReturnsAsync(new[]
        {
            new StockMovement { Id = 1, MovementDate = Today, QuantityIn = 30, MovementType = MovementType.OpeningStock },
            new StockMovement { Id = 2, MovementDate = Today, QuantityOut = 4, MovementType = MovementType.SaleDispatch }
        });
        var timeline = await Service().GetTimelineAsync(1);
        timeline.Select(t => t.RunningBalance).Should().Equal(26, 30);
    }

    // ───────── item settings ─────────

    [Fact]
    public async Task Settings_MergeItemColumnsWithModuleExtras()
    {
        _stock.Setup(s => s.GetExtrasAsync(1)).ReturnsAsync(new StockItemExtras(1, 2m, true, false));
        var settings = await Service().GetSettingsAsync(1);
        settings!.RackLocation.Should().Be("A1");
        settings.MinStock.Should().Be(20);
        settings.MinDiscountPercent.Should().Be(2m);
        settings.IsDiscountLocked.Should().BeTrue();
        (await Service().GetSettingsAsync(99)).Should().BeNull();
    }

    [Fact]
    public async Task SaveSettings_ValidatesSavesAndAudits()
    {
        _stock.Setup(s => s.SaveSettingsAsync(It.IsAny<StockItemSettings>())).ReturnsAsync(true);
        await Service().SaveSettingsAsync(new StockItemSettings { MedicineId = 1, MinStock = 10, MaxStock = 50, RackLocation = "B2" });
        _audit.Verify(a => a.LogAsync("UpdateStockSettings", "Medicine", 1, It.Is<string>(s => s.Contains("A1")),
            It.Is<string>(s => s.Contains("B2")), It.IsAny<string>()), Times.Once);
        _excel.Verify(e => e.EnqueueAsync(SyncEntityType.Medicine, 1), Times.Once);
    }

    [Fact]
    public async Task SaveSettings_RejectsInvalidValuesBeforeWriting()
    {
        var act = () => Service().SaveSettingsAsync(new StockItemSettings { MedicineId = 1, MinStock = 50, MaxStock = 10 });
        await act.Should().ThrowAsync<StockValidationException>().WithMessage("*Max stock*");
        _stock.Verify(s => s.SaveSettingsAsync(It.IsAny<StockItemSettings>()), Times.Never);
    }

    [Fact]
    public async Task SaveSettings_ForAMissingItemFails()
    {
        var act = () => Service().SaveSettingsAsync(new StockItemSettings { MedicineId = 99 });
        await act.Should().ThrowAsync<StockValidationException>().WithMessage("*no longer exists*");
    }

    // ───────── opening batch ─────────

    private static NewBatchRequest NewBatch(long loose = 0) => new()
    {
        MedicineId = 1, BatchNumber = "nb1", ExpiryMonth = 12, ExpiryYear = 2027, UnitsPerPack = 15, Packs = 2, LooseUnits = loose,
        MrpPaisa = 18840, PurchaseRatePaisa = 12000, SaleRatePaisa = 18840, GstPercent = 12m, EntryDate = Today
    };

    [Fact]
    public async Task AddBatch_PostsTheBatchAndAuditsTheOpeningStock()
    {
        _postings.Setup(p => p.InsertOpeningBatchAsync(It.IsAny<MedicineBatch>(), 5)).ReturnsAsync(77);
        var id = await Service().AddBatchAsync(NewBatch(loose: 5));
        id.Should().Be(77);
        _postings.Verify(p => p.InsertOpeningBatchAsync(It.Is<MedicineBatch>(b =>
            b.BatchNumber == "NB1" && b.QuantityRemaining == 35 && b.ExpiryDate == new DateTime(2027, 12, 31)), 5), Times.Once);
        _audit.Verify(a => a.LogAsync("AddOpeningBatch", "MedicineBatch", 77, null, It.Is<string>(s => s.Contains("NB1")), "Opening stock"), Times.Once);
        _excel.Verify(e => e.EnqueueAsync(SyncEntityType.Batch, 77), Times.Once);
    }

    [Fact]
    public async Task AddBatch_RefusesLooseUnitsForWholePackItems()
    {
        _dolo.AllowLooseSale = false;
        var act = () => Service().AddBatchAsync(NewBatch(loose: 5));
        await act.Should().ThrowAsync<StockValidationException>().WithMessage("*whole packs*");
        _postings.Verify(p => p.InsertOpeningBatchAsync(It.IsAny<MedicineBatch>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact]
    public async Task AddBatch_ForAMissingItemFails()
    {
        var act = () => Service().AddBatchAsync(NewBatch() with { MedicineId = 99 });
        await act.Should().ThrowAsync<StockValidationException>();
    }

    // ───────── adjustments ─────────

    [Fact]
    public async Task PostAdjustment_RejectsInvalidRequestsWithoutTouchingStock()
    {
        var act = () => Service().PostAdjustmentAsync(new StockAdjustmentRequest
        {
            Reason = StockAdjustmentReason.Damaged, AdjustmentDate = Today,
            Lines = new[] { new StockAdjustmentLineRequest(10, 5) }
        });
        await act.Should().ThrowAsync<StockValidationException>().WithMessage("*negative*");
        _postings.Verify(p => p.PostAdjustmentAsync(It.IsAny<StockAdjustmentRequest>(), It.IsAny<long?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task PostAdjustment_RejectsFutureDatesAgainstTheClock()
    {
        var act = () => Service().PostAdjustmentAsync(new StockAdjustmentRequest
        {
            Reason = StockAdjustmentReason.Found, AdjustmentDate = Today.AddDays(1),
            Lines = new[] { new StockAdjustmentLineRequest(10, 5) }
        });
        await act.Should().ThrowAsync<StockValidationException>().WithMessage("*future*");
    }

    [Fact]
    public async Task PostAdjustment_PostsAsTheCurrentUser_AuditsAndQueuesSync()
    {
        var posted = new StockAdjustment
        {
            Id = 3, AdjustmentNumber = "ADJ/2627/00001", Reason = StockAdjustmentReason.Damaged, TotalValuePaisa = -8000,
            Lines = { new StockAdjustmentLine { MedicineId = 1, BatchId = 10, BatchNumber = "B1", QuantityChange = -10, ValuePaisa = -8000 } }
        };
        _postings.Setup(p => p.PostAdjustmentAsync(It.IsAny<StockAdjustmentRequest>(), 5, "Ravi")).ReturnsAsync(posted);

        var result = await Service().PostAdjustmentAsync(new StockAdjustmentRequest
        {
            Reason = StockAdjustmentReason.Damaged, AdjustmentDate = Today, Lines = new[] { new StockAdjustmentLineRequest(10, -10) }
        });

        result.Should().BeSameAs(posted);
        _audit.Verify(a => a.LogAsync("StockAdjustment", "StockAdjustment", 3, null,
            It.Is<string>(s => s.Contains("ADJ/2627/00001") && s.Contains("-8000")), It.IsAny<string>()), Times.Once);
        _excel.Verify(e => e.EnqueueAsync(SyncEntityType.Batch, 10), Times.Once);
        _excel.Verify(e => e.EnqueueAsync(SyncEntityType.Medicine, 1), Times.Once);
    }

    [Fact]
    public async Task PeekAdjustmentNumber_UsesTheStockAdjustmentSequence()
    {
        _numbers.Setup(n => n.PeekNextAsync(DocumentType.StockAdjustment, Today)).ReturnsAsync("ADJ/2627/00009");
        (await Service().PeekAdjustmentNumberAsync(Today)).Should().Be("ADJ/2627/00009");
    }

    // ───────── bulk update ─────────

    [Fact]
    public async Task BulkUpdate_DeduplicatesIds_ReportsMissingItems_AndAudits()
    {
        _stock.Setup(s => s.BulkUpdateAsync(It.IsAny<StockBulkUpdate>())).ReturnsAsync(2);
        var result = await Service().BulkUpdateAsync(new StockBulkUpdate { MedicineIds = new long[] { 1, 2, 2, 3 }, RackLocation = "C3" });
        result.Succeeded.Should().Be(2);
        result.Failed.Should().Be(1);
        _stock.Verify(s => s.BulkUpdateAsync(It.Is<StockBulkUpdate>(u => u.MedicineIds.Count == 3)), Times.Once);
        _audit.Verify(a => a.LogAsync("BulkUpdateStockSettings", "Medicine", null, null, It.Is<string>(s => s.Contains("C3")), It.IsAny<string>()), Times.Once);
        _excel.Verify(e => e.EnqueueAsync(SyncEntityType.Medicine, It.IsAny<long?>()), Times.Exactly(3));
    }

    [Fact]
    public async Task BulkUpdate_RejectsAnEmptyChange()
    {
        var act = () => Service().BulkUpdateAsync(new StockBulkUpdate { MedicineIds = new long[] { 1 } });
        await act.Should().ThrowAsync<StockValidationException>();
        _stock.Verify(s => s.BulkUpdateAsync(It.IsAny<StockBulkUpdate>()), Times.Never);
    }
}
