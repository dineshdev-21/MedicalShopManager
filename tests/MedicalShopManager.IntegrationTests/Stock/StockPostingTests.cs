using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Stock;
using MedicalShopManager.Infrastructure.Database.Migrations;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Stock;

/// <summary>Stock postings against SQLite: migration 13, opening batches and numbered adjustments (commit and rollback).</summary>
public class StockPostingTests : StockDbTestBase
{
    [Fact]
    public async Task Migration13_CreatesTheStockTablesAndSetting()
    {
        MigrationCatalog.Discover().Should().Contain(m => m.Version == 13 && m.GetType().Name == "Migration0013StockModule");
        (await LongAsync("SELECT COUNT(*) FROM SchemaMigrations WHERE Version = 13")).Should().Be(1);
        foreach (var table in new[] { "StockItemSettings", "StockAdjustments", "StockAdjustmentLines" })
            (await LongAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @N", ("@N", table))).Should().Be(1, table);
        (await ScalarAsync("SELECT Value FROM AppSettings WHERE Key = 'Stock.ShowZeroBatches'")).Should().Be("0");
    }

    // ───────── opening batch ─────────

    [Fact]
    public async Task AddBatch_WritesTheBatchAndAnOpeningStockMovementTogether()
    {
        var medicineId = await SeedMedicineAsync();
        var batchId = await Service().AddBatchAsync(new NewBatchRequest
        {
            MedicineId = medicineId, BatchNumber = "op-1", ExpiryMonth = 8, ExpiryYear = 27, UnitsPerPack = 15, Packs = 2, LooseUnits = 5,
            MrpPaisa = 18840, PurchaseRatePaisa = 12000, SaleRatePaisa = 18840, GstPercent = 12m
        });

        (await StockOfAsync(batchId)).Should().Be(35);
        (await ScalarAsync("SELECT BatchNumber FROM MedicineBatches WHERE Id = @Id", ("@Id", batchId))).Should().Be("OP-1");
        (await ScalarAsync("SELECT ExpiryDate FROM MedicineBatches WHERE Id = @Id", ("@Id", batchId))).Should().Be("2027-08-31");
        (await LongAsync("SELECT UnitsPerPack FROM MedicineBatches WHERE Id = @Id", ("@Id", batchId))).Should().Be(15);
        (await LongAsync("SELECT QuantityIn FROM StockMovements WHERE BatchId = @Id AND MovementType = @T",
            ("@Id", batchId), ("@T", (int)MovementType.OpeningStock))).Should().Be(35);
        (await LongAsync("SELECT COUNT(*) FROM AuditLogs WHERE Action = 'AddOpeningBatch' AND EntityId = @Id", ("@Id", batchId))).Should().Be(1);
    }

    [Fact]
    public async Task AddBatch_DuplicateBatchNumberIsRefusedAndWritesNothing()
    {
        var medicineId = await SeedMedicineAsync();
        await SeedBatchAsync(medicineId, "B1");
        var movementsBefore = await CountAsync("StockMovements");

        var act = () => Service().AddBatchAsync(new NewBatchRequest
        {
            MedicineId = medicineId, BatchNumber = " b1 ", ExpiryMonth = 8, ExpiryYear = 2027, UnitsPerPack = 15, Packs = 1, MrpPaisa = 18840
        });

        await act.Should().ThrowAsync<StockValidationException>().WithMessage("*already exists*");
        (await CountAsync("MedicineBatches")).Should().Be(1);
        (await CountAsync("StockMovements")).Should().Be(movementsBefore);
    }

    // ───────── adjustments ─────────

    [Fact]
    public async Task Adjustment_PostsNumberQuantitiesMovementsLinesAndAudit()
    {
        var medicineId = await SeedMedicineAsync();
        var b1 = await SeedBatchAsync(medicineId, "B1", units: 30);
        var b2 = await SeedBatchAsync(medicineId, "B2", units: 15, expiry: Today.AddYears(2));
        var expectedNumber = await PeekAdjustmentNumberAsync();

        var posted = await Service().PostAdjustmentAsync(new StockAdjustmentRequest
        {
            Reason = StockAdjustmentReason.Damaged, AdjustmentDate = Today, Notes = "Strips crushed",
            Lines = new[] { new StockAdjustmentLineRequest(b1, -20), new StockAdjustmentLineRequest(b2, -15) }
        });

        posted.AdjustmentNumber.Should().Be(expectedNumber).And.StartWith("ADJ");
        posted.TotalValuePaisa.Should().Be(-16000 - 12000, "20 units and 15 units of 15's at ₹120 cost");
        (await StockOfAsync(b1)).Should().Be(10);
        (await StockOfAsync(b2)).Should().Be(0);
        (await LongAsync("SELECT COUNT(*) FROM StockAdjustmentLines WHERE AdjustmentId = @Id", ("@Id", posted.Id))).Should().Be(2);
        (await LongAsync("SELECT COUNT(*) FROM StockMovements WHERE Reference = @N AND MovementType = @T",
            ("@N", posted.AdjustmentNumber), ("@T", (int)MovementType.Damaged))).Should().Be(2);
        (await LongAsync("SELECT BalanceAfter FROM StockMovements WHERE Reference = @N AND BatchId = @B",
            ("@N", posted.AdjustmentNumber), ("@B", b1))).Should().Be(10);
        (await LongAsync("SELECT COUNT(*) FROM AuditLogs WHERE Action = 'StockAdjustment' AND EntityId = @Id", ("@Id", posted.Id))).Should().Be(1);
        (await PeekAdjustmentNumberAsync()).Should().NotBe(expectedNumber, "the number was consumed");

        var reloaded = await Service().GetAdjustmentAsync(posted.Id);
        reloaded!.Lines.Should().HaveCount(2);
        reloaded.Lines[0].BalanceBefore.Should().Be(30);
        reloaded.Lines[0].BalanceAfter.Should().Be(10);
        reloaded.Lines[0].QuantityDisplay.Should().Be("-1 + 5");
        reloaded.Notes.Should().Be("Strips crushed");
        reloaded.Reason.Should().Be(StockAdjustmentReason.Damaged);

        var list = await Service().GetAdjustmentsAsync(Today.AddDays(-1), Today);
        list.Should().ContainSingle(a => a.Id == posted.Id && a.LineCount == 2);
    }

    [Fact]
    public async Task Adjustment_FailingLineRollsBackEarlierLinesAndTheNumber()
    {
        var medicineId = await SeedMedicineAsync();
        var b1 = await SeedBatchAsync(medicineId, "B1", units: 30);
        var b2 = await SeedBatchAsync(medicineId, "B2", units: 5);
        var expectedNumber = await PeekAdjustmentNumberAsync();
        var movementsBefore = await CountAsync("StockMovements");

        var act = () => Service().PostAdjustmentAsync(new StockAdjustmentRequest
        {
            Reason = StockAdjustmentReason.Lost, AdjustmentDate = Today,
            Lines = new[] { new StockAdjustmentLineRequest(b1, -10), new StockAdjustmentLineRequest(b2, -6) }
        });

        await act.Should().ThrowAsync<StockValidationException>().WithMessage("*only 5*");
        (await StockOfAsync(b1)).Should().Be(30, "the first line is rolled back");
        (await StockOfAsync(b2)).Should().Be(5);
        (await CountAsync("StockAdjustments")).Should().Be(0);
        (await CountAsync("StockAdjustmentLines")).Should().Be(0);
        (await CountAsync("StockMovements")).Should().Be(movementsBefore);
        (await PeekAdjustmentNumberAsync()).Should().Be(expectedNumber, "a rolled-back posting does not consume its number");
    }

    [Fact]
    public async Task Adjustment_UnknownBatchRollsBack()
    {
        var medicineId = await SeedMedicineAsync();
        var b1 = await SeedBatchAsync(medicineId, "B1", units: 30);
        var act = () => Service().PostAdjustmentAsync(new StockAdjustmentRequest
        {
            Reason = StockAdjustmentReason.Found, AdjustmentDate = Today,
            Lines = new[] { new StockAdjustmentLineRequest(b1, 3), new StockAdjustmentLineRequest(9999, 1) }
        });
        await act.Should().ThrowAsync<StockValidationException>().WithMessage("*no longer exists*");
        (await StockOfAsync(b1)).Should().Be(30);
    }

    [Fact]
    public async Task Adjustment_FoundAndCorrectionIncreaseStock()
    {
        var medicineId = await SeedMedicineAsync();
        var b1 = await SeedBatchAsync(medicineId, "B1", units: 0);
        var found = await Service().PostAdjustmentAsync(new StockAdjustmentRequest
        {
            Reason = StockAdjustmentReason.Found, AdjustmentDate = Today, Lines = new[] { new StockAdjustmentLineRequest(b1, 7) }
        });
        found.TotalValuePaisa.Should().Be(5600);
        var correction = await Service().PostAdjustmentAsync(new StockAdjustmentRequest
        {
            Reason = StockAdjustmentReason.Correction, AdjustmentDate = Today.AddDays(-2), Notes = "Physical count",
            Lines = new[] { new StockAdjustmentLineRequest(b1, -2) }
        });
        (await StockOfAsync(b1)).Should().Be(5);
        correction.AdjustmentNumber.Should().NotBe(found.AdjustmentNumber);
        (await ScalarAsync("SELECT Reason FROM StockMovements WHERE Reference = @N", ("@N", correction.AdjustmentNumber)))
            .Should().Be("Count correction: Physical count");
    }

    // ───────── timeline after postings ─────────

    [Fact]
    public async Task Timeline_ReflectsOpeningStockAndAdjustments()
    {
        var medicineId = await SeedMedicineAsync();
        var batchId = await Service().AddBatchAsync(new NewBatchRequest
        {
            MedicineId = medicineId, BatchNumber = "T1", ExpiryMonth = 8, ExpiryYear = 2030, UnitsPerPack = 15, Packs = 2, MrpPaisa = 18840
        });
        await Service().PostAdjustmentAsync(new StockAdjustmentRequest
        {
            Reason = StockAdjustmentReason.Sample, AdjustmentDate = Today, Lines = new[] { new StockAdjustmentLineRequest(batchId, -3) }
        });

        var timeline = await Service().GetTimelineAsync(medicineId);
        timeline.Should().HaveCount(2);
        timeline[0].MovementType.Should().Be(MovementType.WriteOff);
        timeline[0].RunningBalance.Should().Be(27);
        timeline[1].MovementType.Should().Be(MovementType.OpeningStock);
        timeline[1].RunningBalance.Should().Be(30);
    }
}
