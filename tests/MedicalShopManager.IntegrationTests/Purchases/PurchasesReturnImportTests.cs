using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Infrastructure.Modules.Purchases;
using MedicalShopManager.Infrastructure.Repositories;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Purchases;

/// <summary>Debit notes (purchase / expiry returns), supplier statement and supplier-bill import on a real SQLite database.</summary>
public class PurchasesReturnImportTests : PurchasesDbTestBase
{
    private async Task<(long SupplierId, long MedicineId, PurchasePostingResult Posted)> SeedPurchaseAsync(int packs = 4, DateTime? expiry = null)
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        var posted = await EntryService().ConfirmAsync(Bill(supplierId, medicineId, packs: packs, expiry: expiry), null, null);
        return (supplierId, medicineId, posted);
    }

    [Fact]
    public async Task PurchaseReturn_IssuesDebitNote_TakesStockOut_AndReducesPayable()
    {
        var (supplierId, _, posted) = await SeedPurchaseAsync();
        var service = ReturnService();
        var expected = await PeekAsync(DocumentType.DebitNote);
        var batch = (await service.GetReturnableBatchesAsync(supplierId, posted.PurchaseId)).Single();
        batch.MaxReturnableUnits.Should().Be(60);
        batch.RatePaisa.Should().Be(12000, "the purchase line's net rate");

        var doc = new PurchaseReturnDocument { SupplierId = supplierId, ReturnDate = Today, PurchaseId = posted.PurchaseId, Reason = PurchaseReturnReason.Damaged };
        doc.Lines.Add(service.CreateLine(batch, 15));
        var note = await service.PostAsync(doc, null);

        note.ReturnNumber.Should().Be(expected);
        note.NetAmountPaisa.Should().Be(13400);
        (await StockAsync(batch.BatchId)).Should().Be(45);
        (await LongAsync("SELECT COUNT(*) FROM StockMovements WHERE MovementType = @T AND QuantityOut = 15", ("@T", (int)MovementType.PurchaseReturn))).Should().Be(1);
        (await DistributorsRepo().GetByIdAsync(supplierId))!.OutstandingBalancePaisa.Should().Be(53800 - 13400);
        (await service.GetReturnableBatchesAsync(supplierId, posted.PurchaseId)).Single().MaxReturnableUnits.Should().Be(45);

        // The purchase cannot be deleted while a debit note refers to it.
        var ex = await Assert.ThrowsAsync<PurchaseRuleException>(() => EntryService().DeleteAsync(posted.PurchaseId, "x", null));
        ex.Message.Should().Contain(note.ReturnNumber!);
    }

    [Fact]
    public async Task PurchaseReturn_MoreThanTheBillDelivered_RollsBackNumberAndStock()
    {
        var (supplierId, _, posted) = await SeedPurchaseAsync(packs: 2);
        var batchId = posted.BatchIds.Single();
        await ExecAsync("UPDATE MedicineBatches SET QuantityRemaining = 100 WHERE Id = @B", ("@B", batchId)); // stock from elsewhere
        var batch = (await ReturnService().GetReturnableBatchesAsync(supplierId, posted.PurchaseId)).Single();
        var before = await PeekAsync(DocumentType.DebitNote);

        var doc = new PurchaseReturnDocument { SupplierId = supplierId, ReturnDate = Today };
        var line = ReturnService().CreateLine(batch, 30);
        line.Quantity = 45; // the bill delivered only 30
        doc.Lines.Add(line);

        await Assert.ThrowsAsync<PurchaseRuleException>(() => ReturnsRepo().PostAsync(doc, null));

        (await StockAsync(batchId)).Should().Be(100);
        (await LongAsync("SELECT COUNT(*) FROM PurchaseReturns")).Should().Be(0);
        (await PeekAsync(DocumentType.DebitNote)).Should().Be(before);
    }

    [Fact]
    public async Task CancelDebitNote_PutsStockBack()
    {
        var (supplierId, _, posted) = await SeedPurchaseAsync();
        var service = ReturnService();
        var batch = (await service.GetReturnableBatchesAsync(supplierId)).Single();
        var doc = new PurchaseReturnDocument { SupplierId = supplierId, ReturnDate = Today };
        doc.Lines.Add(service.CreateLine(batch, 20));
        var note = await service.PostAsync(doc, null);

        await service.CancelAsync(note.Id, null);

        (await StockAsync(posted.BatchIds.Single())).Should().Be(60);
        (await service.GetAsync(note.Id))!.IsCancelled.Should().BeTrue();
        (await service.ListAsync(Today, Today, supplierId)).Single().StatusLabel.Should().Be("Cancelled");
        await Assert.ThrowsAsync<PurchaseRuleException>(() => service.CancelAsync(note.Id, null));
    }

    [Fact]
    public async Task ExpiryReturn_ListsExpiredAndNearExpiryBatches_ByDistributor()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        await EntryService().ConfirmAsync(Bill(supplierId, medicineId, "N", 2, "NEAR", Today.AddMonths(2)), null, null);
        await EntryService().ConfirmAsync(Bill(supplierId, medicineId, "F", 2, "FAR", Today.AddYears(2)), null, null);
        // An already-expired batch from this distributor (opening stock, not from a bill).
        await ExecAsync(@"INSERT INTO MedicineBatches (MedicineId, BatchNumber, ExpiryDate, PurchaseDate, SupplierId, QuantityReceived, QuantityRemaining,
                PurchaseRatePaisa, SaleRatePaisa, MrpPaisa, GstRatePercent, UnitsPerPack, IsActive)
            VALUES (@M, 'OLD', @E, @E, @S, 15, 15, 11000, 18000, 18000, 12, 15, 1)",
            ("@M", medicineId), ("@E", Today.AddMonths(-1).ToString("yyyy-MM-dd")), ("@S", supplierId));

        var service = ReturnService();
        var within3 = await service.GetExpiryCandidatesAsync(supplierId, 3);
        var expiredOnly = await service.GetExpiryCandidatesAsync(supplierId, 0);

        within3.Select(b => b.BatchNumber).Should().BeEquivalentTo("OLD", "NEAR");
        expiredOnly.Select(b => b.BatchNumber).Should().Equal("OLD");
        expiredOnly.Single().IsExpired.Should().BeTrue();

        var doc = new PurchaseReturnDocument { Kind = PurchaseReturnKind.Expiry, SupplierId = supplierId, ReturnDate = Today, Reason = PurchaseReturnReason.Expired };
        doc.Lines.Add(service.CreateLine(expiredOnly.Single(), 15));
        var note = await service.PostAsync(doc, null);
        note.Kind.Should().Be(PurchaseReturnKind.Expiry);
        (await service.GetExpiryCandidatesAsync(supplierId, 0)).Should().BeEmpty();
    }

    [Fact]
    public async Task Statement_DerivesBalanceFromBillsPaymentsAndDebitNotes()
    {
        var (supplierId, _, posted) = await SeedPurchaseAsync(packs: 4); // ₹538
        await EntryService().RecordPaymentAsync(posted.PurchaseId, new PurchasePaymentInput(20000, PaymentMethod.Cash, null, Today), null);
        var service = ReturnService();
        var batch = (await service.GetReturnableBatchesAsync(supplierId)).Single();
        var doc = new PurchaseReturnDocument { SupplierId = supplierId, ReturnDate = Today, RefundReceivedPaisa = 5000 };
        doc.Lines.Add(service.CreateLine(batch, 15)); // ₹134 debit note, ₹50 refunded in cash
        await service.PostAsync(doc, null);

        var statement = await DistributorService().GetStatementAsync(supplierId, Today.AddDays(-30), Today);

        statement.OpeningBalancePaisa.Should().Be(0);
        statement.ClosingBalancePaisa.Should().Be(53800 - 20000 - 13400 + 5000);
        statement.ClosingBalancePaisa.Should().Be((await DistributorsRepo().GetByIdAsync(supplierId))!.OutstandingBalancePaisa,
            "the statement and the distributor list derive the same balance");
        statement.Lines.Select(l => l.Type).Should().Contain(new[]
        {
            SupplierStatementEntryType.Purchase, SupplierStatementEntryType.Payment, SupplierStatementEntryType.DebitNote, SupplierStatementEntryType.RefundReceived
        });
    }

    [Fact]
    public async Task DistributorRepository_SearchSaveAndDeactivate()
    {
        var service = DistributorService();
        var id = await service.SaveAsync(new Distributor { Name = "Gamma Medicals", Gstin = "33ABCDE1234F1Z5", CreditDays = 21, Phone = "9876543210" });

        var found = (await service.SearchAsync("gamma")).Single();
        found.StateCode.Should().Be("33");
        found.CreditDays.Should().Be(21);
        (await DistributorsRepo().GstinExistsAsync("33abcde1234f1z5")).Should().BeTrue();

        await service.SetActiveAsync(id, false);
        (await service.SearchAsync("gamma")).Should().BeEmpty();
        (await service.SearchAsync("gamma", includeInactive: true)).Should().ContainSingle();
    }

    [Fact]
    public async Task Import_Csv_MatchesItems_BuildsPerPackDraft_AndRemembersTheMapping()
    {
        var supplierId = await SeedSupplierAsync();
        await SeedMedicineAsync("Dolo 650");
        var dir = Path.Combine(Path.GetTempPath(), $"MSM_Import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bill.csv");
        var expiry = Today.AddYears(1);
        await File.WriteAllTextAsync(path,
            "Item Name,Batch No,Exp,Pack,Qty,Free,MRP,Rate,GST %\n" +
            $"Dolo 650,DL123,{expiry:MM/yy},15,10,1,188.40,120.00,12\n" +
            $"Unknown Syrup,US1,{expiry:MM/yy},1,5,0,90,60,12\n");
        try
        {
            var medicines = new MedicineRepository(Db, NullLogger<MedicineRepository>.Instance);
            var entry = EntryService();
            var import = new PurchaseImportService(Bills(), DistributorsRepo(), medicines, entry);

            var table = await import.ReadFileAsync(path);
            var mapping = await import.GetMappingAsync(supplierId, table);
            mapping.Should().Equal(PurchaseImportField.ProductName, PurchaseImportField.BatchNumber, PurchaseImportField.Expiry,
                PurchaseImportField.UnitsPerPack, PurchaseImportField.Quantity, PurchaseImportField.FreeQuantity, PurchaseImportField.Mrp,
                PurchaseImportField.Rate, PurchaseImportField.GstPercent);

            var custom = mapping.ToList();
            custom[8] = PurchaseImportField.Ignore; // the user changes one column; it is saved for this distributor
            var result = await import.BuildDraftAsync(supplierId, "CSV-1", Today, table, custom, "CSV");

            result.ImportedCount.Should().Be(1);
            result.Skipped.Should().ContainSingle(r => r.ProductName == "Unknown Syrup");
            var line = result.Draft.Lines.Single();
            line.UnitsPerPack.Should().Be(15);
            line.Quantity.Should().Be(150);
            line.FreeQuantity.Should().Be(15);
            line.EnteredRatePaisa.Should().Be(12000);
            line.GstRatePercent.Should().Be(12, "taken from the item master when the column is ignored");
            result.Draft.GrandTotalPaisa.Should().Be(134400);
            (await import.GetMappingAsync(supplierId, table)).Should().Equal(custom);

            // The draft posts like any purchase.
            var posted = await entry.ConfirmAsync(result.Draft, null, null);
            (await Bills().GetAsync(posted.PurchaseId))!.ImportSource.Should().Be("CSV");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyPurchaseServiceAdapter_PostsWithTheItemsRealPackSize()
    {
        var supplierId = await SeedSupplierAsync();
        var medicineId = await SeedMedicineAsync();
        var adapter = new PurchaseServiceAdapter(EntryService(), new MedicineRepository(Db, NullLogger<MedicineRepository>.Instance));
        var legacy = new Core.Models.Purchase
        {
            SupplierId = supplierId, SupplierName = "Alpha Pharma", SupplierInvoiceNumber = "OLD-1", PurchaseDate = Today,
            Items =
            {
                new Core.Models.PurchaseItem
                {
                    MedicineId = medicineId, MedicineName = "Dolo 650", BatchNumber = "L1", ExpiryDate = Today.AddYears(1), Quantity = 30,
                    PurchasePricePaisa = 800, MrpPaisa = 1256, SellingPricePaisa = 1256, GstRatePercent = 12
                }
            }
        };

        var id = await adapter.CreateDraftAsync(legacy);
        await adapter.ConfirmPurchaseAsync(id, 1);

        (await LongAsync("SELECT UnitsPerPack FROM MedicineBatches WHERE BatchNumber = 'L1'")).Should().Be(15);
        (await LongAsync("SELECT PurchaseRatePaisa FROM MedicineBatches WHERE BatchNumber = 'L1'")).Should().Be(12000, "₹8/unit = ₹120 per strip of 15");
        (await LongAsync("SELECT QuantityRemaining FROM MedicineBatches WHERE BatchNumber = 'L1'")).Should().Be(30);
    }
}
