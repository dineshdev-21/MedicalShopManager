using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Stock;
using Xunit;

namespace MedicalShopManager.UnitTests.Stock;

/// <summary>Pure rules of the Stock workspace (<see cref="StockRules"/>): status, cost, margin, aggregates, filters, timeline, validation.</summary>
public class StockRulesTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    private static MedicineBatch Batch(long id = 1, string number = "B1", int units = 30, int upp = 15, DateTime? expiry = null,
        long rate = 12000, long mrp = 18840, long sale = 18840, decimal gst = 12m, decimal disc = 0m, int free = 0, bool active = true,
        int? received = null) => new()
    {
        Id = id, MedicineId = 1, BatchNumber = number, QuantityRemaining = units, QuantityReceived = received ?? units, FreeUnits = free,
        UnitsPerPack = upp, ExpiryDate = expiry ?? Today.AddYears(1), PurchaseRatePaisa = rate, MrpPaisa = mrp,
        SaleRatePaisa = sale, GstRatePercent = gst, DiscountPercent = disc, IsActive = active
    };

    private static Medicine Item(long id = 1, string name = "Dolo 650", int minStock = 0, DrugSchedule schedule = DrugSchedule.None,
        string? manufacturer = "Micro Labs", MedicineStatus status = MedicineStatus.Active) => new()
    {
        Id = id, Name = name, Manufacturer = manufacturer, Composition = "Paracetamol 650mg", UnitsPerPack = 15, PackLabel = "15's",
        MinStock = minStock, Schedule = schedule, DefaultMrpPaisa = 17500, DefaultSellingPricePaisa = 17000, GstRatePercent = 12m,
        Status = status, RackLocation = "A1", Barcode = "8901234567890"
    };

    // ───────── expiry and status ─────────

    [Fact]
    public void Batch_IsExpired_FromItsExpiryDateOnwards()
    {
        StockRules.IsExpired(Today, Today).Should().BeTrue();
        StockRules.IsExpired(Today.AddDays(1), Today).Should().BeFalse();
        StockRules.IsExpired(Today.AddDays(-1), Today).Should().BeTrue();
    }

    [Fact]
    public void NearExpiry_IsWithinTheWindowButNotExpired()
    {
        StockRules.IsNearExpiry(Today.AddDays(90), Today, 90).Should().BeTrue();
        StockRules.IsNearExpiry(Today.AddDays(91), Today, 90).Should().BeFalse();
        StockRules.IsNearExpiry(Today, Today, 90).Should().BeFalse("an expired batch is expired, not near expiry");
    }

    [Fact]
    public void StatusOf_FollowsPrecedence_InactiveExpiredOutNearIn()
    {
        StockRules.StatusOf(Batch(active: false, expiry: Today.AddDays(-5)), Today, 90).Should().Be(BatchStockStatus.Inactive);
        StockRules.StatusOf(Batch(units: 0, expiry: Today.AddDays(-5)), Today, 90).Should().Be(BatchStockStatus.Expired);
        StockRules.StatusOf(Batch(units: 0, expiry: Today.AddDays(30)), Today, 90).Should().Be(BatchStockStatus.OutOfStock);
        StockRules.StatusOf(Batch(expiry: Today.AddDays(30)), Today, 90).Should().Be(BatchStockStatus.NearExpiry);
        StockRules.StatusOf(Batch(expiry: Today.AddDays(300)), Today, 90).Should().Be(BatchStockStatus.InStock);
    }

    [Theory]
    [InlineData(-1, "Expired 1 day ago")]
    [InlineData(-3, "Expired 3 days ago")]
    [InlineData(0, "Expires today")]
    [InlineData(1, "Expires in 1 day")]
    [InlineData(45, "Expires in 45 days")]
    public void ExpiryHint_DescribesDaysLeft(int days, string expected) =>
        StockRules.ExpiryHint(Today.AddDays(days), Today).Should().Be(expected);

    [Fact]
    public void IsSellable_RequiresActiveStockAndNotExpired()
    {
        StockRules.IsSellable(Batch(), Today).Should().BeTrue();
        StockRules.IsSellable(Batch(units: 0), Today).Should().BeFalse();
        StockRules.IsSellable(Batch(active: false), Today).Should().BeFalse();
        StockRules.IsSellable(Batch(expiry: Today), Today).Should().BeFalse();
    }

    // ───────── rates, cost, margin, value ─────────

    [Fact]
    public void NetRate_IsPurchaseRateAfterBatchDiscount()
    {
        StockRules.NetRatePaisa(Batch(rate: 12000, disc: 10m)).Should().Be(10800);
        StockRules.NetRatePaisa(Batch(rate: 12000)).Should().Be(12000);
    }

    [Fact]
    public void Cost_SpreadsFreeUnitsOverTheReceipt()
    {
        // 10 strips paid + 1 strip free of 15's @ ₹120: taxable ₹1,200 over 11 strips = ₹109.09 per strip.
        var batch = Batch(rate: 12000, units: 165, received: 165, free: 15);
        StockRules.CostPaisa(batch).Should().Be(10909);
    }

    [Fact]
    public void Cost_IsZeroWhenEverythingCameFree_AndNetRateWithoutFree()
    {
        StockRules.CostPaisa(Batch(units: 15, received: 15, free: 15)).Should().Be(0);
        StockRules.CostPaisa(Batch(rate: 12000, disc: 10m)).Should().Be(10800);
    }

    [Fact]
    public void Margin_IsOnSellingPriceExcludingGst()
    {
        // saleEx = 18840 × 100 / 112 = 16821.43; (16821.43 − 12000) / 16821.43 = 28.66 %
        StockRules.MarginPercent(Batch(sale: 18840, gst: 12m, rate: 12000)).Should().Be(28.66m);
    }

    [Fact]
    public void EffectiveSaleRate_FallsBackToMrp()
    {
        StockRules.EffectiveSaleRatePaisa(Batch(sale: 0, mrp: 18840)).Should().Be(18840);
        StockRules.EffectiveSaleRatePaisa(Batch(sale: 17000, mrp: 18840)).Should().Be(17000);
    }

    [Fact]
    public void StockValue_IsRemainingUnitsAtCost_AndMrpValueAtMrp()
    {
        // Localwell golden vector: 1 strip + 5 loose of 15's @ ₹188.40 = ₹251.20.
        var batch = Batch(units: 20, rate: 12000, mrp: 18840);
        StockRules.MrpValuePaisa(batch).Should().Be(25120);
        StockRules.StockValuePaisa(batch).Should().Be(16000);
        StockRules.StockValuePaisa(Batch(units: 0)).Should().Be(0);
        StockRules.MrpValuePaisa(Batch(units: -3)).Should().Be(0);
    }

    // ───────── aggregate ─────────

    [Fact]
    public void PrimaryBatch_IsFirstSellableByExpiry_ElseNewestWithStock_ElseNewest()
    {
        var early = Batch(id: 1, expiry: Today.AddDays(100));
        var late = Batch(id: 2, expiry: Today.AddDays(400));
        var expired = Batch(id: 3, expiry: Today.AddDays(-1));
        StockRules.PrimaryBatch(new[] { late, expired, early }, Today)!.Id.Should().Be(1);
        StockRules.PrimaryBatch(new[] { expired, Batch(id: 4, units: 0) }, Today)!.Id.Should().Be(3);
        StockRules.PrimaryBatch(new[] { Batch(id: 5, units: 0), Batch(id: 6, units: 0) }, Today)!.Id.Should().Be(6);
        StockRules.PrimaryBatch(Array.Empty<MedicineBatch>(), Today).Should().BeNull();
    }

    [Fact]
    public void BatchRows_ShowSellableFirstInFefoOrder_AndHideZeroUnlessAsked()
    {
        var batches = new[]
        {
            Batch(id: 1, number: "LATE", expiry: Today.AddDays(400)),
            Batch(id: 2, number: "EXP", expiry: Today.AddDays(-10)),
            Batch(id: 3, number: "EARLY", expiry: Today.AddDays(60)),
            Batch(id: 4, number: "ZERO", units: 0, expiry: Today.AddDays(30))
        };
        StockRules.BatchRows(batches, Today, 90, showZero: false).Select(r => r.BatchNumber)
            .Should().Equal("EARLY", "LATE", "EXP");
        var all = StockRules.BatchRows(batches, Today, 90, showZero: true);
        all.Select(r => r.BatchNumber).Should().Equal("EARLY", "LATE", "EXP", "ZERO");
        all[0].Status.Should().Be(BatchStockStatus.NearExpiry);
        all[3].StatusLabel.Should().Be("Stock Out");
    }

    [Fact]
    public void ToBatchRow_CarriesRatesMarginAndPackDisplay()
    {
        var row = StockRules.ToBatchRow(Batch(units: 20, rate: 12000, disc: 10m), Today, 90);
        row.NetRatePaisa.Should().Be(10800);
        row.CostPaisa.Should().Be(10800);
        row.StockDisplay.Should().Be("1 + 5");
        row.PackDisplay.Should().Be("15's");
        row.Mrp.Should().Be(188.40m);
    }

    [Fact]
    public void Summarize_CountsOnlySellableStock_AndTracksExpiredSeparately()
    {
        var batches = new[]
        {
            Batch(id: 1, units: 30, expiry: Today.AddDays(60), mrp: 19000),
            Batch(id: 2, units: 15, expiry: Today.AddDays(400)),
            Batch(id: 3, units: 10, expiry: Today.AddDays(-1)),
            Batch(id: 4, units: 99, active: false)
        };
        var item = StockRules.Summarize(Item(minStock: 50), batches, Today, 90);
        item.StockUnits.Should().Be(45);
        item.ExpiredUnits.Should().Be(10);
        item.BatchCount.Should().Be(3);
        item.MrpPaisa.Should().Be(19000, "MRP of the FEFO batch");
        item.NearestExpiry.Should().Be(Today.AddDays(60));
        item.HasNearExpiry.Should().BeTrue();
        item.IsLowStock.Should().BeTrue();
        item.StockPill.Should().Be("Low Stock");
        item.StockDisplay.Should().Be("3");
        item.StockValuePaisa.Should().Be(24000 + 12000);
    }

    [Fact]
    public void Summarize_WithoutBatches_UsesDefaultMrpAndIsOutOfStock()
    {
        var item = StockRules.Summarize(Item(), Array.Empty<MedicineBatch>(), Today, 90);
        item.MrpPaisa.Should().Be(17500);
        item.IsInStock.Should().BeFalse();
        item.StockPill.Should().Be("Stock Out");
        item.ExpiryLabel.Should().Be("-");
    }

    [Fact]
    public void BuildHeader_PricesFromPrimaryBatch_OrItemDefaults()
    {
        var header = StockRules.BuildHeader(Item(), new[] { Batch(rate: 12000, disc: 10m, sale: 18000) }, Today);
        header.NetRatePaisa.Should().Be(10800);
        header.SaleRatePaisa.Should().Be(18000);
        header.StockUnits.Should().Be(30);
        header.PackDisplay.Should().Be("15's");

        var empty = StockRules.BuildHeader(Item(), Array.Empty<MedicineBatch>(), Today);
        empty.MrpPaisa.Should().Be(17500);
        empty.SaleRatePaisa.Should().Be(17000);
        empty.MarginPercent.Should().Be(0m);
        empty.ExpiryLabel.Should().Be("-");
    }

    // ───────── list filters ─────────

    private static StockListItem ListItem(string name, long units = 10, int min = 0, long expired = 0, bool near = false,
        DrugSchedule schedule = DrugSchedule.None, string? mfr = "Cipla", long value = 0, DateTime? nearest = null, bool active = true) => new()
    {
        MedicineId = name.GetHashCode(), Name = name, StockUnits = units, MinStock = min, ExpiredUnits = expired, HasNearExpiry = near,
        Schedule = schedule, Manufacturer = mfr, StockValuePaisa = value, NearestExpiry = nearest, IsActive = active,
        Composition = "Azithromycin 500mg", Barcode = "890111", RackLocation = "R2"
    };

    [Theory]
    [InlineData("azi 500", true)]
    [InlineData("AZITHRAL", true)]
    [InlineData("cipla r2", true)]
    [InlineData("890111", true)]
    [InlineData("paracetamol", false)]
    [InlineData("", true)]
    public void Search_MatchesEveryTermAcrossNameCompositionCompanyBarcodeRack(string search, bool expected) =>
        StockRules.MatchesSearch(ListItem("Azithral 500"), search).Should().Be(expected);

    [Fact]
    public void Filters_SelectByStockState()
    {
        StockRules.MatchesFilter(ListItem("A", units: 0), StockFilter.OutOfStock).Should().BeTrue();
        StockRules.MatchesFilter(ListItem("A", units: 5), StockFilter.InStock).Should().BeTrue();
        StockRules.MatchesFilter(ListItem("A", units: 5, min: 5), StockFilter.LowStock).Should().BeTrue();
        StockRules.MatchesFilter(ListItem("A", units: 0, min: 5), StockFilter.LowStock).Should().BeFalse("out of stock is not low stock");
        StockRules.MatchesFilter(ListItem("A", near: true), StockFilter.NearExpiry).Should().BeTrue();
        StockRules.MatchesFilter(ListItem("A", expired: 3), StockFilter.Expired).Should().BeTrue();
        StockRules.MatchesFilter(ListItem("A"), StockFilter.Expired).Should().BeFalse();
    }

    [Fact]
    public void Apply_FiltersByScheduleManufacturerAndInactive_AndSummarisesShownItems()
    {
        var items = new[]
        {
            ListItem("Alprax", schedule: DrugSchedule.H1, value: 500),
            ListItem("Dolo", mfr: "Micro Labs", value: 300, units: 0),
            ListItem("Zifi", value: 200),
            ListItem("Old", active: false, value: 900)
        };
        var result = StockRules.Apply(items, new StockListQuery { Manufacturer = " cipla " });
        result.Items.Select(i => i.Name).Should().Equal("Alprax", "Zifi");
        result.Summary.StockValuePaisa.Should().Be(700);
        result.Summary.ItemCount.Should().Be(2);

        StockRules.Apply(items, new StockListQuery { Schedule = DrugSchedule.H1 }).Items.Should().ContainSingle(i => i.Name == "Alprax");
        StockRules.Apply(items, new StockListQuery { IncludeInactive = true }).Items.Should().HaveCount(4);
        var summary = StockRules.Apply(items, new StockListQuery()).Summary;
        summary.OutOfStockCount.Should().Be(1);
        summary.InStockCount.Should().Be(2);
    }

    [Fact]
    public void Sort_OrdersByChosenKey()
    {
        var items = new[]
        {
            ListItem("b", units: 5, value: 10, nearest: Today.AddDays(50)),
            ListItem("a", units: 20, value: 5, nearest: null),
            ListItem("c", units: 1, value: 30, nearest: Today.AddDays(10))
        };
        StockRules.Sort(items, StockSort.NameAsc).Select(i => i.Name).Should().Equal("a", "b", "c");
        StockRules.Sort(items, StockSort.NameDesc).Select(i => i.Name).Should().Equal("c", "b", "a");
        StockRules.Sort(items, StockSort.StockHighToLow).Select(i => i.Name).Should().Equal("a", "b", "c");
        StockRules.Sort(items, StockSort.StockLowToHigh).Select(i => i.Name).Should().Equal("c", "b", "a");
        StockRules.Sort(items, StockSort.ValueHighToLow).Select(i => i.Name).Should().Equal("c", "b", "a");
        StockRules.Sort(items, StockSort.ExpirySoonest).Select(i => i.Name).Should().Equal("c", "b", "a");
    }

    // ───────── timeline ─────────

    [Fact]
    public void Timeline_RunsItemBalanceChronologically_AndListsNewestFirst()
    {
        var movements = new[]
        {
            new StockMovement { Id = 3, MovementDate = Today.AddDays(2), MovementType = MovementType.SaleDispatch, QuantityOut = 5, BalanceAfter = 25, BatchNumber = "B1" },
            new StockMovement { Id = 1, MovementDate = Today, MovementType = MovementType.OpeningStock, QuantityIn = 30, BalanceAfter = 30, BatchNumber = "B1" },
            new StockMovement { Id = 2, MovementDate = Today.AddDays(1), MovementType = MovementType.PurchaseReceipt, QuantityIn = 15, BalanceAfter = 15, BatchNumber = "B2" },
            new StockMovement { Id = 4, MovementDate = Today.AddDays(2), MovementType = MovementType.Damaged, QuantityOut = 2, BalanceAfter = 13, BatchNumber = "B2" }
        };
        var timeline = StockRules.BuildTimeline(movements);
        timeline.Select(t => t.MovementId).Should().Equal(4, 3, 2, 1);
        timeline.Select(t => t.RunningBalance).Should().Equal(38, 40, 45, 30);
        timeline[0].SignedQuantity.Should().Be(-2);
        timeline[0].MovementLabel.Should().Be("Damaged");
        timeline[3].MovementLabel.Should().Be("Opening Stock");
    }

    // ───────── adjustments ─────────

    private static StockAdjustmentRequest Request(StockAdjustmentReason reason, string? notes = null, DateTime? date = null,
        params (long Batch, long Change)[] lines) => new()
    {
        Reason = reason, Notes = notes, AdjustmentDate = date ?? Today,
        Lines = lines.Select(l => new StockAdjustmentLineRequest(l.Batch, l.Change)).ToList()
    };

    [Theory]
    [InlineData(StockAdjustmentReason.Damaged, -1, MovementType.Damaged)]
    [InlineData(StockAdjustmentReason.Expired, -1, MovementType.Expired)]
    [InlineData(StockAdjustmentReason.Lost, -1, MovementType.WriteOff)]
    [InlineData(StockAdjustmentReason.Sample, -1, MovementType.WriteOff)]
    [InlineData(StockAdjustmentReason.Found, 1, MovementType.StockAdjustment)]
    [InlineData(StockAdjustmentReason.Correction, 0, MovementType.StockAdjustment)]
    public void Reason_FixesDirectionAndLedgerType(StockAdjustmentReason reason, int sign, MovementType type)
    {
        reason.RequiredSign().Should().Be(sign);
        reason.ToMovementType().Should().Be(type);
        reason.ToLabel().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ValidAdjustments_Pass()
    {
        StockRules.ValidateAdjustmentRequest(Request(StockAdjustmentReason.Damaged, lines: (1, -5)), Today).Should().BeNull();
        StockRules.ValidateAdjustmentRequest(Request(StockAdjustmentReason.Found, lines: (1, 5)), Today).Should().BeNull();
        StockRules.ValidateAdjustmentRequest(Request(StockAdjustmentReason.Correction, "Recount", null, (1, 5), (2, -3)), Today).Should().BeNull();
    }

    [Fact]
    public void InvalidAdjustments_AreRejectedWithAMessage()
    {
        StockRules.ValidateAdjustmentRequest(Request(StockAdjustmentReason.Damaged), Today).Should().Contain("at least one");
        StockRules.ValidateAdjustmentRequest(Request((StockAdjustmentReason)99, lines: (1, -1)), Today).Should().Contain("reason");
        StockRules.ValidateAdjustmentRequest(Request(StockAdjustmentReason.Damaged, date: Today.AddDays(1), lines: (1, -1)), Today).Should().Contain("future");
        StockRules.ValidateAdjustmentRequest(Request(StockAdjustmentReason.Correction, lines: (1, 1)), Today).Should().Contain("note");
        StockRules.ValidateAdjustmentRequest(Request(StockAdjustmentReason.Damaged, lines: new[] { (1L, -1L), (1L, -2L) }), Today).Should().Contain("once");
        StockRules.ValidateAdjustmentRequest(Request(StockAdjustmentReason.Damaged, lines: (1, 0)), Today).Should().Contain("zero");
        StockRules.ValidateAdjustmentRequest(Request(StockAdjustmentReason.Damaged, lines: (1, 3)), Today).Should().Contain("negative");
        StockRules.ValidateAdjustmentRequest(Request(StockAdjustmentReason.Found, lines: (1, -3)), Today).Should().Contain("positive");
    }

    [Fact]
    public void AdjustmentBalance_CannotGoBelowZero()
    {
        StockRules.ValidateAdjustmentBalance("B1", 10, -10).Should().BeNull();
        StockRules.ValidateAdjustmentBalance("B1", 10, -11).Should().Contain("only 10");
        StockRules.ValidateAdjustmentBalance("B1", 0, 5).Should().BeNull();
    }

    [Fact]
    public void AdjustmentValue_IsSignedCostOfTheUnits()
    {
        StockRules.AdjustmentValuePaisa(-20, 12000, 15).Should().Be(-16000);
        StockRules.AdjustmentValuePaisa(20, 12000, 15).Should().Be(16000);
        StockRules.AdjustmentValuePaisa(-1, 1000, 3).Should().Be(-333);
    }

    // ───────── opening batch ─────────

    private static NewBatchRequest NewBatch(Func<NewBatchRequest, NewBatchRequest>? change = null)
    {
        var r = new NewBatchRequest
        {
            MedicineId = 1, BatchNumber = " dl24051 ", ExpiryMonth = 8, ExpiryYear = 27, UnitsPerPack = 15, Packs = 2, LooseUnits = 5,
            MrpPaisa = 18840, PurchaseRatePaisa = 12000, PtrPaisa = 15072, SaleRatePaisa = 18840, GstPercent = 12m, EntryDate = Today
        };
        return change == null ? r : change(r);
    }

    [Fact]
    public void ValidNewBatch_BecomesABatchWithMonthEndExpiryAndUnits()
    {
        StockRules.ValidateNewBatch(NewBatch(), allowLooseSale: true).Should().BeNull();
        var batch = StockRules.ToBatch(NewBatch());
        batch.BatchNumber.Should().Be("DL24051");
        batch.ExpiryDate.Should().Be(new DateTime(2027, 8, 31));
        batch.QuantityReceived.Should().Be(35);
        batch.QuantityRemaining.Should().Be(35);
        batch.UnitsPerPack.Should().Be(15);
        batch.IsActive.Should().BeTrue();
        StockRules.ToBatch(NewBatch(r => r with { SaleRatePaisa = 0 })).SaleRatePaisa.Should().Be(18840, "sale rate defaults to MRP");
    }

    [Theory]
    [InlineData("medicine", "item")]
    [InlineData("batch", "Batch number is required")]
    [InlineData("longbatch", "at most")]
    [InlineData("month", "month")]
    [InlineData("year", "year")]
    [InlineData("pack", "Units per pack")]
    [InlineData("negative", "negative")]
    [InlineData("loose", "less than the pack size")]
    [InlineData("zero", "opening quantity")]
    [InlineData("mrp", "MRP is required")]
    [InlineData("sale", "Sale rate")]
    [InlineData("ptr", "PTR")]
    [InlineData("purchase", "Purchase rate")]
    [InlineData("gst", "GST")]
    [InlineData("discount", "Discount")]
    [InlineData("single", "packs")]
    public void InvalidNewBatch_IsRejected(string problem, string message)
    {
        var request = NewBatch(r => problem switch
        {
            "medicine" => r with { MedicineId = 0 },
            "batch" => r with { BatchNumber = "  " },
            "longbatch" => r with { BatchNumber = new string('X', 31) },
            "month" => r with { ExpiryMonth = 13 },
            "year" => r with { ExpiryYear = 1999 },
            "pack" => r with { UnitsPerPack = 0 },
            "negative" => r with { Packs = -1 },
            "loose" => r with { LooseUnits = 15 },
            "zero" => r with { Packs = 0, LooseUnits = 0 },
            "mrp" => r with { MrpPaisa = 0, SaleRatePaisa = 0, PtrPaisa = 0, PurchaseRatePaisa = 0 },
            "sale" => r with { SaleRatePaisa = 18841 },
            "ptr" => r with { PtrPaisa = 20000 },
            "purchase" => r with { PurchaseRatePaisa = 20000 },
            "gst" => r with { GstPercent = 30m },
            "discount" => r with { DiscountPercent = 101m },
            "single" => r with { UnitsPerPack = 1, LooseUnits = 1 },
            _ => r
        });
        StockRules.ValidateNewBatch(request, allowLooseSale: true).Should().Contain(message);
    }

    [Fact]
    public void NewBatch_LooseUnitsRefusedWhenItemSellsWholePacksOnly()
    {
        StockRules.ValidateNewBatch(NewBatch(), allowLooseSale: false).Should().Contain("whole packs");
        StockRules.ValidateNewBatch(NewBatch(r => r with { LooseUnits = 0 }), allowLooseSale: false).Should().BeNull();
    }

    // ───────── settings and bulk update ─────────

    [Theory]
    [InlineData(null, true)]
    [InlineData("3004", true)]
    [InlineData("300490", true)]
    [InlineData("30049099", true)]
    [InlineData("30049", false)]
    [InlineData("3004A", false)]
    public void Hsn_Is4_6Or8Digits(string? hsn, bool ok) => (StockRules.ValidateHsn(hsn) == null).Should().Be(ok);

    [Fact]
    public void Settings_Validation()
    {
        var ok = new StockItemSettings { MedicineId = 1, MinStock = 10, MaxStock = 100, MaxDiscountPercent = 20m, MinDiscountPercent = 5m };
        StockRules.ValidateSettings(ok).Should().BeNull();
        StockRules.ValidateSettings(ok with { MaxStock = 0 }).Should().BeNull("0 means no maximum");
        StockRules.ValidateSettings(ok with { MedicineId = 0 }).Should().NotBeNull();
        StockRules.ValidateSettings(ok with { MinStock = -1 }).Should().Contain("negative");
        StockRules.ValidateSettings(ok with { MaxStock = 5 }).Should().Contain("Max stock");
        StockRules.ValidateSettings(ok with { MaxDiscountPercent = 120m }).Should().Contain("Max discount");
        StockRules.ValidateSettings(ok with { MinDiscountPercent = 25m }).Should().Contain("Min discount");
        StockRules.ValidateSettings(ok with { Schedule = (DrugSchedule)42 }).Should().Contain("schedule");
        StockRules.ValidateSettings(ok with { RackLocation = new string('R', 31) }).Should().Contain("Rack");
    }

    [Fact]
    public void BulkUpdate_Validation()
    {
        var ok = new StockBulkUpdate { MedicineIds = new long[] { 1, 2 }, GstRatePercent = 12m };
        StockRules.ValidateBulkUpdate(ok).Should().BeNull();
        StockRules.ValidateBulkUpdate(ok with { MedicineIds = Array.Empty<long>() }).Should().Contain("Select");
        StockRules.ValidateBulkUpdate(new StockBulkUpdate { MedicineIds = new long[] { 1 } }).Should().Contain("at least one value");
        StockRules.ValidateBulkUpdate(ok with { GstRatePercent = 40m }).Should().Contain("GST");
        StockRules.ValidateBulkUpdate(ok with { HsnCode = "12" }).Should().Contain("HSN");
        StockRules.ValidateBulkUpdate(ok with { MinStock = -1 }).Should().Contain("negative");
        StockRules.ValidateBulkUpdate(ok with { MinStock = 10, MaxStock = 5 }).Should().Contain("Max stock");
        StockRules.ValidateBulkUpdate(ok with { MaxDiscountPercent = -1m }).Should().Contain("discount");
        StockRules.ValidateBulkUpdate(ok with { Schedule = (DrugSchedule)42 }).Should().Contain("schedule");
        StockRules.ValidateBulkUpdate(ok with { RackLocation = new string('R', 31) }).Should().Contain("Rack");
        new StockBulkUpdate { Category = "" }.HasChanges.Should().BeTrue("an empty string clears the field");
    }

    [Fact]
    public void DetailTabs_HaveLocalwellTitlesInOrder() =>
        Enum.GetValues<StockDetailTab>().Select(t => t.ToTitle()).Should()
            .Equal("BATCHES", "PURCHASES", "SALES", "TIMELINE", "SUBSTITUTE", "BRANCH STK", "DISTRIBUTOR");
}
