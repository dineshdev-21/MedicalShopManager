using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Purchases;

/// <summary>Posting, merging, editing, cancelling and paying purchases on a real SQLite database.</summary>
public class PurchasesPostingTests : PurchasesDbTestBase
{
    [Fact]
    public async Task Migration12_AddsColumnsTablesAndSettings()
    {
        (await LongAsync("SELECT COUNT(*) FROM SchemaMigrations WHERE Version = 12")).Should().Be(1);
        (await LongAsync("SELECT COUNT(*) FROM pragma_table_info('PurchaseItems') WHERE name IN ('UnitsPerPack','NetPurchaseRatePaisa','BatchId')")).Should().Be(3);
        (await LongAsync("SELECT COUNT(*) FROM pragma_table_info('Suppliers') WHERE name IN ('CreditDays','StateCode','Pincode')")).Should().Be(3);
        (await LongAsync("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('PurchaseReturns','PurchaseReturnItems','PurchaseImportMappings')")).Should().Be(3);
        (await ScalarAsync("SELECT Value FROM AppSettings WHERE Key = 'Purchases.NearExpiryMonths'")).Should().Be("3");
    }

    [Fact]
    public async Task Confirm_CreatesPerPackBatch_Number_Movement_AndPayment()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        var expectedNumber = await PeekAsync(DocumentType.Purchase);
        var bill = Bill(supplierId, medicineId, packs: 10, freePacks: 1);

        var result = await EntryService().ConfirmAsync(bill, new PurchasePaymentInput(50000, PaymentMethod.UPI, "UTR1", Today), userId: null);

        result.PurchaseNumber.Should().Be(expectedNumber);
        var batchId = result.BatchIds.Single();
        (await StockAsync(batchId)).Should().Be(165, "10 paid + 1 free strip of 15");
        (await LongAsync("SELECT UnitsPerPack FROM MedicineBatches WHERE Id = @B", ("@B", batchId))).Should().Be(15, "no more pack-of-1 interim");
        (await LongAsync("SELECT PurchaseRatePaisa FROM MedicineBatches WHERE Id = @B", ("@B", batchId))).Should().Be(12000);
        (await LongAsync("SELECT MrpPaisa FROM MedicineBatches WHERE Id = @B", ("@B", batchId))).Should().Be(18840);
        (await LongAsync("SELECT FreeUnits FROM MedicineBatches WHERE Id = @B", ("@B", batchId))).Should().Be(15);
        (await LongAsync("SELECT COUNT(*) FROM StockMovements WHERE BatchId = @B AND QuantityIn = 165 AND BalanceAfter = 165", ("@B", batchId))).Should().Be(1);

        var saved = (await Bills().GetAsync(result.PurchaseId))!;
        saved.Status.Should().Be(PurchaseStatus.Confirmed);
        saved.GrandTotalPaisa.Should().Be(134400);
        saved.AmountPaidPaisa.Should().Be(50000);
        saved.PaymentStatus.Should().Be(PaymentStatus.Partial);
        saved.DueDate.Should().Be(Today.AddDays(30));
        saved.Lines.Single().NetPurchaseRatePaisa.Should().Be(10909, "₹1,200 taxable over 11 strips");
        saved.Lines.Single().BatchId.Should().Be(batchId);
        (await Bills().GetPaymentsAsync(result.PurchaseId)).Single().AmountPaisa.Should().Be(50000);
        (await LongAsync("SELECT COUNT(*) FROM AuditLogs WHERE Action = 'ConfirmPurchase'")).Should().Be(1);
    }

    [Fact]
    public async Task Confirm_SameItemBatchAndExpiry_MergesIntoOneBatch_OtherExpiryMakesANewOne()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        var first = await EntryService().ConfirmAsync(Bill(supplierId, medicineId, "INV-1", 2, "b1"), null, null);
        var second = await EntryService().ConfirmAsync(Bill(supplierId, medicineId, "INV-2", 3, "B1"), null, null);
        var third = await EntryService().ConfirmAsync(Bill(supplierId, medicineId, "INV-3", 1, "B1", new DateTime(Today.Year + 3, 1, 31)), null, null);

        second.BatchIds.Single().Should().Be(first.BatchIds.Single(), "batch numbers match case-insensitively with the same expiry month");
        (await StockAsync(first.BatchIds.Single())).Should().Be(75);
        third.BatchIds.Single().Should().NotBe(first.BatchIds.Single());
    }

    [Fact]
    public async Task Confirm_FailureMidway_RollsBackStockAndNumber()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        var numberBefore = await PeekAsync(DocumentType.Purchase);
        var bill = Bill(supplierId, medicineId);
        var broken = bill.Lines[0];
        bill.Lines.Add(new PurchaseBillLine
        {
            MedicineId = 9999, MedicineName = "Ghost", HsnCode = "3004", BatchNumber = "G1", ExpiryDate = broken.ExpiryDate, UnitsPerPack = 10,
            Quantity = 10, EnteredRatePaisa = 1000, MrpPaisa = 2000, SaleRatePaisa = 2000, GstRatePercent = 12
        });

        await Assert.ThrowsAnyAsync<Exception>(() => Bills().ConfirmAsync(bill, null, null)); // FK violation on the second line

        (await LongAsync("SELECT COUNT(*) FROM MedicineBatches")).Should().Be(0);
        (await LongAsync("SELECT COUNT(*) FROM Purchases")).Should().Be(0);
        (await LongAsync("SELECT COUNT(*) FROM StockMovements")).Should().Be(0);
        (await PeekAsync(DocumentType.Purchase)).Should().Be(numberBefore, "a rolled-back posting does not consume its number");
        bill.PurchaseNumber.Should().BeNull();
        bill.Status.Should().Be(PurchaseStatus.Draft);
    }

    [Fact]
    public async Task Draft_TouchesNoStock_ThenConfirmsWithItsId_OrIsDeleted()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        var service = EntryService();
        var draft = Bill(supplierId, medicineId);
        var id = await service.SaveDraftAsync(draft, null);

        (await LongAsync("SELECT COUNT(*) FROM MedicineBatches")).Should().Be(0);
        (await service.ListAsync(new PurchaseListFilter(Today, Today, Status: PurchaseListStatusFilter.Draft))).Should().ContainSingle(r => r.Id == id);

        var loaded = (await service.GetAsync(id))!;
        var result = await service.ConfirmAsync(loaded, null, null);
        result.PurchaseId.Should().Be(id);
        (await LongAsync("SELECT COUNT(*) FROM Purchases")).Should().Be(1);

        var other = await service.SaveDraftAsync(Bill(supplierId, medicineId, "INV-9"), null);
        await service.DeleteAsync(other, "", null);
        (await Bills().GetAsync(other)).Should().BeNull();
    }

    [Fact]
    public async Task EditConfirmed_ReversesAndRepostsStock_KeepsNumber()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        var service = EntryService();
        var posted = await service.ConfirmAsync(Bill(supplierId, medicineId, packs: 10), null, null);

        var bill = (await service.GetAsync(posted.PurchaseId))!;
        bill.Lines[0].Quantity = 4 * 15;
        var edited = await service.UpdateConfirmedAsync(bill, null);

        edited.PurchaseNumber.Should().Be(posted.PurchaseNumber);
        (await StockAsync(edited.BatchIds.Single())).Should().Be(60);
        (await Bills().GetAsync(posted.PurchaseId))!.GrandTotalPaisa.Should().Be(53800, "₹537.60 rounded to the rupee");
    }

    [Fact]
    public async Task EditOrDelete_AfterStockWasSold_IsRefused_AndLeavesEverythingAsItWas()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        var service = EntryService();
        var posted = await service.ConfirmAsync(Bill(supplierId, medicineId, packs: 2), null, null);
        var batchId = posted.BatchIds.Single();
        await ExecAsync("UPDATE MedicineBatches SET QuantityRemaining = 10 WHERE Id = @B", ("@B", batchId)); // 20 units sold

        var ex = await Assert.ThrowsAsync<PurchaseRuleException>(() => service.DeleteAsync(posted.PurchaseId, "wrong bill", null));
        ex.Message.Should().Contain("only 10 of 30 units");

        (await StockAsync(batchId)).Should().Be(10);
        (await Bills().GetAsync(posted.PurchaseId))!.Status.Should().Be(PurchaseStatus.Confirmed);
    }

    [Fact]
    public async Task Delete_Confirmed_TakesStockOut_AndCancels_ButNotWhenPaid()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        var service = EntryService();
        var posted = await service.ConfirmAsync(Bill(supplierId, medicineId, "INV-1", 2), null, null);
        var paid = await service.ConfirmAsync(Bill(supplierId, medicineId, "INV-2", 1, "B2"),
            new PurchasePaymentInput(1000, PaymentMethod.Cash, null, Today), null);

        await service.DeleteAsync(posted.PurchaseId, "Entered twice", null);

        (await StockAsync(posted.BatchIds.Single())).Should().Be(0);
        (await Bills().GetAsync(posted.PurchaseId))!.Status.Should().Be(PurchaseStatus.Cancelled);
        await Assert.ThrowsAsync<PurchaseRuleException>(() => service.DeleteAsync(paid.PurchaseId, "x", null));
    }

    [Fact]
    public async Task Duplicate_BillNumberOfTheSameDistributor_IsFound()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        await EntryService().ConfirmAsync(Bill(supplierId, medicineId, "inv-77"), null, null);

        await Assert.ThrowsAsync<DuplicatePurchaseInvoiceException>(() => EntryService().ConfirmAsync(Bill(supplierId, medicineId, " INV-77 ", batch: "B9"), null, null));
        var other = await SeedSupplierAsync("Beta", "29BBBBB1234F1Z5");
        (await Bills().FindDuplicateAsync(other, "INV-77")).Should().BeNull();
    }

    [Fact]
    public async Task Payments_SettleTheBalance_AndOnAccountPaysOldestDueFirst()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        var service = EntryService();
        var older = await service.ConfirmAsync(Bill(supplierId, medicineId, "A", 1), null, null);
        var bill2 = Bill(supplierId, medicineId, "B", 1, "B2");
        bill2.DueDate = Today.AddDays(60);
        var newer = await service.ConfirmAsync(bill2, null, null);

        await Assert.ThrowsAsync<PurchaseRuleException>(() => service.RecordPaymentAsync(older.PurchaseId, new PurchasePaymentInput(999999, PaymentMethod.Cash, null, Today), null));
        var allocations = await service.RecordSupplierPaymentAsync(supplierId, new PurchasePaymentInput(20000, PaymentMethod.BankTransfer, "NEFT", Today), null);

        allocations.Should().HaveCount(2);
        allocations[0].PurchaseId.Should().Be(older.PurchaseId);
        allocations[0].AmountPaisa.Should().Be(13400);
        allocations[1].AmountPaisa.Should().Be(6600);
        (await Bills().GetAsync(older.PurchaseId))!.PaymentStatus.Should().Be(PaymentStatus.Paid);
        (await Bills().GetAsync(newer.PurchaseId))!.BalancePaisa.Should().Be(6800);
        (await DistributorsRepo().GetByIdAsync(supplierId))!.OutstandingBalancePaisa.Should().Be(6800);
    }
}
