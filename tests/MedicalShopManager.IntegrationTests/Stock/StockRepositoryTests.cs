using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Stock;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Stock;

/// <summary>Stock read side, item settings and bulk update against SQLite.</summary>
public class StockRepositoryTests : StockDbTestBase
{
    [Fact]
    public async Task StockList_AggregatesBatchesPerItem_AndHidesInactiveItems()
    {
        var dolo = await SeedMedicineAsync("Dolo 650", minStock: 40);
        await SeedBatchAsync(dolo, "B1", units: 30, expiry: Today.AddDays(30));
        await SeedBatchAsync(dolo, "EXP", units: 10, expiry: Today.AddDays(-2));
        var crocin = await SeedMedicineAsync("Crocin", "Paracetamol 500mg", pack: 10, manufacturer: "GSK");
        await SeedMedicineAsync("Old Syrup", "Dextromethorphan", status: (int)MedicineStatus.Discontinued);

        var result = await Service().GetStockListAsync(new StockListQuery());
        result.Items.Select(i => i.Name).Should().Equal("Crocin", "Dolo 650");
        var item = result.Items.Single(i => i.MedicineId == dolo);
        item.StockUnits.Should().Be(30);
        item.ExpiredUnits.Should().Be(10);
        item.IsLowStock.Should().BeTrue();
        item.HasNearExpiry.Should().BeTrue();
        item.StockValuePaisa.Should().Be(24000);
        result.Summary.OutOfStockCount.Should().Be(1);
        result.Items.Single(i => i.MedicineId == crocin).IsInStock.Should().BeFalse();

        (await Service().GetStockListAsync(new StockListQuery { IncludeInactive = true })).Items.Should().HaveCount(3);
        (await Service().GetStockListAsync(new StockListQuery { Filter = StockFilter.Expired })).Items.Should().ContainSingle(i => i.MedicineId == dolo);
        (await Service().GetStockListAsync(new StockListQuery { Manufacturer = "gsk" })).Items.Should().ContainSingle(i => i.MedicineId == crocin);
        (await Service().GetManufacturersAsync()).Should().Equal("GSK", "Micro Labs");
    }

    [Fact]
    public async Task ItemDetail_HeaderAndBatchRows()
    {
        var dolo = await SeedMedicineAsync();
        await SeedBatchAsync(dolo, "LATE", units: 15, expiry: Today.AddYears(2), rate: 11000);
        await SeedBatchAsync(dolo, "EARLY", units: 20, expiry: Today.AddDays(200), rate: 12000);
        await SeedBatchAsync(dolo, "ZERO", units: 0);

        var detail = await Service().GetItemDetailAsync(dolo, showZeroBatches: false);
        detail!.Header.Name.Should().Be("Dolo 650");
        detail.Header.PurchaseRatePaisa.Should().Be(12000, "the FEFO batch prices the header");
        detail.Header.StockUnits.Should().Be(35);
        detail.Header.StockDisplay.Should().Be("2 + 5");
        detail.Header.LocationLabel.Should().Be("A1");
        detail.Batches.Select(b => b.BatchNumber).Should().Equal("EARLY", "LATE");
        detail.Batches[0].MarginPercent.Should().Be(28.66m);
        (await Service().GetItemDetailAsync(dolo, showZeroBatches: true))!.Batches.Should().HaveCount(3);
    }

    [Fact]
    public async Task Substitutes_AreActiveItemsWithTheSameComposition()
    {
        var dolo = await SeedMedicineAsync("Dolo 650", "Paracetamol 650mg");
        var calpol = await SeedMedicineAsync("Calpol 650", "PARACETAMOL 650MG", manufacturer: "GSK");
        await SeedBatchAsync(calpol, "C1", units: 45);
        await SeedMedicineAsync("Pacimol 650", "Paracetamol 650mg", manufacturer: "Ipca", status: (int)MedicineStatus.Discontinued);
        await SeedMedicineAsync("Crocin", "Paracetamol 500mg");

        var subs = await Service().GetSubstitutesAsync(dolo);
        subs.Should().ContainSingle();
        subs[0].Name.Should().Be("Calpol 650");
        subs[0].StockUnits.Should().Be(45);
        subs[0].StockDisplay.Should().Be("3");
        (await Service().GetSubstitutesAsync(calpol)).Select(s => s.Name).Should().Equal("Dolo 650");
    }

    [Fact]
    public async Task PurchasesSalesAndDistributorTabs_ReadTheItemsDocuments()
    {
        var dolo = await SeedMedicineAsync();
        var other = await SeedMedicineAsync("Crocin", "Paracetamol 500mg");
        var batch = await SeedBatchAsync(dolo, "B1", units: 150);
        var alpha = await SeedSupplierAsync("Alpha Pharma");
        var beta = await SeedSupplierAsync("Beta Distributors", "9800000002");
        await SeedPurchaseAsync(alpha, dolo, "B1", Today.AddDays(-30), 150, 12000, "A-1", free: 15);
        await SeedPurchaseAsync(alpha, dolo, "B1", Today.AddDays(-5), 75, 12500, "A-2");
        await SeedPurchaseAsync(beta, dolo, "B1", Today.AddDays(-10), 30, 11800, "B-1");
        await SeedPurchaseAsync(beta, dolo, "B1", Today.AddDays(-1), 30, 9999, "B-DRAFT", PurchaseStatus.Draft);
        await SeedPurchaseAsync(beta, other, "X1", Today, 30, 5000, "B-2");
        await SeedSaleAsync(dolo, batch, "INV-1", 20);
        await SeedSaleAsync(dolo, batch, "INV-2", 5, "Ramesh");

        var purchases = await Service().GetPurchasesAsync(dolo);
        purchases.Select(p => p.InvoiceNumber).Should().Equal("B-DRAFT", "A-2", "B-1", "A-1");
        var first = purchases.Single(p => p.InvoiceNumber == "A-1");
        first.SupplierName.Should().Be("Alpha Pharma");
        first.QuantityDisplay.Should().Be("10");
        first.FreeDisplay.Should().Be("1");
        first.RatePaisa.Should().Be(12000);
        first.StatusLabel.Should().Be("Confirmed");
        purchases.Single(p => p.InvoiceNumber == "B-DRAFT").StatusLabel.Should().Be("Draft");

        var sales = await Service().GetSalesAsync(dolo);
        sales.Should().HaveCount(2);
        sales.Single(s => s.InvoiceNumber == "INV-1").CustomerName.Should().Be("Cash / Counter Sale");
        sales.Single(s => s.InvoiceNumber == "INV-1").QuantityDisplay.Should().Be("1 + 5");
        sales.Single(s => s.InvoiceNumber == "INV-2").CustomerName.Should().Be("Ramesh");
        sales.Single(s => s.InvoiceNumber == "INV-2").DiscountPercent.Should().Be(10m);

        var distributors = await Service().GetDistributorsAsync(dolo);
        distributors.Select(d => d.Name).Should().Equal("Alpha Pharma", "Beta Distributors");
        var a = distributors[0];
        a.PurchaseCount.Should().Be(2);
        a.TotalUnits.Should().Be(150 + 15 + 75);
        a.LastRatePaisa.Should().Be(12500);
        a.LastPurchaseDate.Should().Be(Today.AddDays(-5));
        a.Phone.Should().Be("9800000001");
        distributors[1].LastRatePaisa.Should().Be(11800, "draft purchases are not counted");
    }

    [Fact]
    public async Task Settings_SaveToItemAndSideTable_AndAreAudited()
    {
        var dolo = await SeedMedicineAsync();
        await Service().SaveSettingsAsync(new StockItemSettings
        {
            MedicineId = dolo, RackLocation = " R-7 ", MinStock = 30, MaxStock = 300, MaxDiscountPercent = 15m, MinDiscountPercent = 2m,
            Schedule = DrugSchedule.H1, AllowLooseSale = false, IsDiscountLocked = true, IgnoreForShortage = true
        });

        var settings = await Service().GetSettingsAsync(dolo);
        settings!.RackLocation.Should().Be("R-7");
        settings.MinStock.Should().Be(30);
        settings.MaxStock.Should().Be(300);
        settings.MaxDiscountPercent.Should().Be(15m);
        settings.MinDiscountPercent.Should().Be(2m);
        settings.Schedule.Should().Be(DrugSchedule.H1);
        settings.AllowLooseSale.Should().BeFalse();
        settings.IsDiscountLocked.Should().BeTrue();
        settings.IgnoreForShortage.Should().BeTrue();
        (await LongAsync("SELECT COUNT(*) FROM AuditLogs WHERE Action = 'UpdateStockSettings' AND EntityId = @Id", ("@Id", dolo))).Should().Be(1);

        // Saving again updates the side-table row instead of adding one.
        await Service().SaveSettingsAsync(settings with { MinDiscountPercent = 0m, IsDiscountLocked = false });
        (await CountAsync("StockItemSettings")).Should().Be(1);
        (await Service().GetSettingsAsync(dolo))!.IsDiscountLocked.Should().BeFalse();
    }

    [Fact]
    public async Task Settings_ForAMissingItemWriteNothing()
    {
        (await Repo().SaveSettingsAsync(new StockItemSettings { MedicineId = 404 })).Should().BeFalse();
        (await CountAsync("StockItemSettings")).Should().Be(0);
    }

    [Fact]
    public async Task BulkUpdate_SetsOnlyGivenFieldsOnSelectedItems()
    {
        var a = await SeedMedicineAsync("A");
        var b = await SeedMedicineAsync("B");
        var c = await SeedMedicineAsync("C");

        var result = await Service().BulkUpdateAsync(new StockBulkUpdate
        {
            MedicineIds = new[] { a, b, 9999 }, GstRatePercent = 5m, HsnCode = "3004", RackLocation = "Z9", MinStock = 12, Schedule = DrugSchedule.H
        });

        result.Succeeded.Should().Be(2);
        result.Failed.Should().Be(1);
        (await ScalarAsync("SELECT RackLocation FROM Medicines WHERE Id = @Id", ("@Id", a))).Should().Be("Z9");
        (await LongAsync("SELECT MinStock FROM Medicines WHERE Id = @Id", ("@Id", b))).Should().Be(12);
        (await LongAsync("SELECT Schedule FROM Medicines WHERE Id = @Id", ("@Id", b))).Should().Be((int)DrugSchedule.H);
        (await ScalarAsync("SELECT HsnCode FROM Medicines WHERE Id = @Id", ("@Id", a))).Should().Be("3004");
        (await ScalarAsync("SELECT RackLocation FROM Medicines WHERE Id = @Id", ("@Id", c))).Should().Be("A1", "not selected");
        (await ScalarAsync("SELECT Manufacturer FROM Medicines WHERE Id = @Id", ("@Id", a))).Should().Be("Micro Labs", "not given");
        (await LongAsync("SELECT COUNT(*) FROM AuditLogs WHERE Action = 'BulkUpdateStockSettings'")).Should().Be(1);
    }

    [Fact]
    public async Task BulkUpdate_LeavingAnyItemWithMaxBelowMin_RollsBackEverything()
    {
        var a = await SeedMedicineAsync("A", minStock: 5);
        var b = await SeedMedicineAsync("B", minStock: 5);
        await ExecAsync("UPDATE Medicines SET MaxStock = 50 WHERE Id = @Id", ("@Id", b));

        var act = () => Service().BulkUpdateAsync(new StockBulkUpdate { MedicineIds = new[] { a, b }, MinStock = 80, RackLocation = "Q1" });

        await act.Should().ThrowAsync<StockValidationException>().WithMessage("*'B'*max stock below min stock*");
        (await LongAsync("SELECT MinStock FROM Medicines WHERE Id = @Id", ("@Id", a))).Should().Be(5);
        (await ScalarAsync("SELECT RackLocation FROM Medicines WHERE Id = @Id", ("@Id", a))).Should().Be("A1");
    }

    [Fact]
    public async Task FindMedicineAndBatchExists_AreCaseInsensitive()
    {
        var dolo = await SeedMedicineAsync("Dolo 650", manufacturer: "Micro Labs");
        await SeedBatchAsync(dolo, "AB12");
        (await Repo().FindMedicineAsync(" dolo 650 ", "MICRO LABS")).Should().Be(dolo);
        (await Repo().FindMedicineAsync("Dolo 650", null)).Should().BeNull();
        (await Repo().BatchExistsAsync(dolo, "ab12")).Should().BeTrue();
        (await Repo().BatchExistsAsync(dolo, "AB13")).Should().BeFalse();
    }

    [Fact]
    public async Task ShowZeroBatches_SettingRoundTrips()
    {
        (await Service().GetShowZeroBatchesAsync()).Should().BeFalse();
        await Service().SetShowZeroBatchesAsync(true);
        (await Service().GetShowZeroBatchesAsync()).Should().BeTrue();
    }
}
