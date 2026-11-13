using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Modules.Billing;
using MedicalShopManager.Infrastructure.Modules.ControlCenter;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Repositories;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Billing;

/// <summary>Temp SQLite database with every migration applied, plus seed helpers for Billing tests.</summary>
public abstract class BillingDbTestBase : IAsyncLifetime
{
    protected readonly string DbPath = Path.Combine(Path.GetTempPath(), $"MSM_Billing_{Guid.NewGuid():N}.db");
    protected DatabaseManager Db = null!;

    public virtual async Task InitializeAsync()
    {
        Db = new DatabaseManager(DbPath, NullLogger<DatabaseManager>.Instance);
        await Db.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    protected async Task<object?> ScalarAsync(string sql, params (string, object?)[] args)
    {
        using var conn = Db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    protected async Task<long> LongAsync(string sql, params (string, object?)[] args) => Convert.ToInt64(await ScalarAsync(sql, args) ?? 0L);

    /// <summary>Medicine "name" (15's, GST 12 %) with one batch; returns (medicineId, batchId).</summary>
    protected async Task<(long MedicineId, long BatchId)> SeedItemAsync(string name = "Dolo 650", long units = 100, long mrpPaisa = 18840,
        DrugSchedule schedule = DrugSchedule.None, DateTime? expiry = null, string? barcode = null, string composition = "Paracetamol 650mg")
    {
        var medicineId = await LongAsync(@"
            INSERT INTO Medicines (Name, Composition, Manufacturer, UnitsPerPack, AllowLooseSale, Schedule, HsnCode, GstRatePercent, Barcode,
                Status, DefaultMrpPaisa, RackLocation)
            VALUES (@Name, @Composition, 'Micro Labs', 15, 1, @Schedule, '30049099', 12, @Barcode, 0, @Mrp, 'A1');
            SELECT last_insert_rowid();",
            ("@Name", name), ("@Composition", composition), ("@Schedule", (int)schedule), ("@Barcode", barcode), ("@Mrp", mrpPaisa));
        var batchId = await AddBatchAsync(medicineId, "B-" + name[..3], units, mrpPaisa, expiry ?? DateTime.Today.AddYears(1));
        return (medicineId, batchId);
    }

    protected async Task<long> AddBatchAsync(long medicineId, string batchNumber, long units, long mrpPaisa, DateTime expiry) =>
        await LongAsync(@"
            INSERT INTO MedicineBatches (MedicineId, BatchNumber, ExpiryDate, PurchaseDate, QuantityReceived, QuantityRemaining,
                PurchaseRatePaisa, SaleRatePaisa, MrpPaisa, GstRatePercent, UnitsPerPack, IsActive)
            VALUES (@MedicineId, @Batch, @Expiry, @Purchased, @Units, @Units, 12000, @Mrp, @Mrp, 12, 15, 1);
            SELECT last_insert_rowid();",
            ("@MedicineId", medicineId), ("@Batch", batchNumber), ("@Expiry", expiry.ToString("yyyy-MM-dd")),
            ("@Purchased", DateTime.Today.AddMonths(-1).ToString("yyyy-MM-dd")), ("@Units", units), ("@Mrp", mrpPaisa));

    protected Task<long> StockAsync(long batchId) => LongAsync("SELECT QuantityRemaining FROM MedicineBatches WHERE Id = @Id", ("@Id", batchId));

    protected SaleBillingService NewService()
    {
        var auth = new AuthService(new UserRepository(Db), NullLogger<AuthService>.Instance);
        var audit = new AuditService(new AuditLogRepository(Db), auth, NullLogger<AuditService>.Instance);
        var controlCenter = new ControlCenterSettingsService(new ControlCenterSettingsStore(Db), audit, NullLogger<ControlCenterSettingsService>.Instance);
        return new SaleBillingService(new BillingSaleRepository(Db), new SaleCatalogRepository(Db), new BillingCustomerRepository(Db),
            new AppSettingsRepository(Db), controlCenter, audit, new ExcelSyncQueueRepository(Db), auth, NullLogger<SaleBillingService>.Instance);
    }

    protected async Task<BillLine> LineAsync(long medicineId, long packs, long loose = 0, decimal discount = 0m, long free = 0)
    {
        var catalog = new SaleCatalogRepository(Db);
        var item = (await catalog.GetItemAsync(medicineId))!;
        var batch = BillingCalculator.PickDefaultBatch(BillingCalculator.Annotate(await catalog.GetBatchesAsync(medicineId), DateTime.Now, 6))!;
        return BillingCalculator.CreateLine(item, batch, 0m, packs, loose) with { DiscountPercent = discount, FreeUnits = free };
    }

    protected static SalePostingRequest CashSale(long netPaisa, params BillLine[] lines) => new()
    {
        InvoiceDate = DateTime.Now,
        IsCounterSale = true,
        Lines = lines,
        Tenders = new[] { new SaleTender { Mode = TenderMode.Cash, AmountPaisa = netPaisa } }
    };
}

/// <summary>Posting, editing and cancelling sales through SaleBillingService / BillingSaleRepository on a real database.</summary>
public class BillingPostingTests : BillingDbTestBase
{
    [Fact]
    public async Task CashSale_DeductsPackAndLooseUnits_WritesLedgerPaymentAndNumber()
    {
        var (medicineId, batchId) = await SeedItemAsync();
        var service = NewService();
        var line = await LineAsync(medicineId, packs: 1, loose: 5);
        var expectedNumber = await new DocumentNumberService(Db, NullLogger<DocumentNumberService>.Instance).PeekNextAsync(DocumentType.Sale);

        var posted = await service.SaveAsync(CashSale(25100, line) with { CashTenderedPaisa = 30000 });

        posted.InvoiceNumber.Should().Be(expectedNumber);
        posted.NetPayablePaisa.Should().Be(25100); // golden vector ₹251.20 rounded to the rupee
        posted.ChangePaisa.Should().Be(4900);
        (await StockAsync(batchId)).Should().Be(80);
        (await LongAsync("SELECT COUNT(*) FROM StockMovements WHERE BatchId = @B AND QuantityOut = 20 AND BalanceAfter = 80", ("@B", batchId))).Should().Be(1);
        (await LongAsync("SELECT SUM(AmountPaisa) FROM Payments WHERE TransactionId = @S", ("@S", posted.SaleId))).Should().Be(25100);

        var invoice = (await service.GetInvoiceAsync(posted.SaleId))!;
        invoice.Lines.Should().ContainSingle();
        var saved = invoice.Lines[0];
        (saved.Packs, saved.Loose, saved.Quantity, saved.UnitsPerPack).Should().Be((1L, 5L, 20, 15));
        saved.LineTotalPaisa.Should().Be(25120);
        invoice.RoundOffPaisa.Should().Be(-20);
        invoice.GrandTotalPaisa.Should().Be(25100);
        invoice.MrpTotalPaisa.Should().Be(25120);
        (invoice.TaxablePaisa + invoice.TaxPaisa).Should().Be(25120);
        (await LongAsync("SELECT COUNT(*) FROM AuditLogs WHERE Action = 'CreateSale' AND EntityId = @S", ("@S", posted.SaleId))).Should().Be(1);
    }

    [Fact]
    public async Task InsufficientStock_RollsBackEverything_AndDoesNotConsumeTheNumber()
    {
        var (medicineId, batchId) = await SeedItemAsync(units: 10);
        var numbers = new DocumentNumberService(Db, NullLogger<DocumentNumberService>.Instance);
        var before = await numbers.PeekNextAsync(DocumentType.Sale);
        var line = (await LineAsync(medicineId, packs: 1)) with { AvailableUnits = 1000 };
        var bill = BillingCalculator.ComputeBill(new BillRequest { Lines = new[] { line } });
        var payment = BillingCalculator.ResolvePayment(bill.Totals.NetPayablePaisa, CashSale(bill.Totals.NetPayablePaisa, line)).Resolution!;

        var act = () => new BillingSaleRepository(Db).InsertAsync(new SalePosting(CashSale(bill.Totals.NetPayablePaisa, line), bill, payment, false));

        (await act.Should().ThrowAsync<BillingValidationException>()).Which.Message.Should().Contain("Insufficient stock");
        (await StockAsync(batchId)).Should().Be(10);
        (await LongAsync("SELECT COUNT(*) FROM Sales")).Should().Be(0);
        (await LongAsync("SELECT COUNT(*) FROM SaleItems")).Should().Be(0);
        (await LongAsync("SELECT COUNT(*) FROM StockMovements")).Should().Be(0);
        (await LongAsync("SELECT COUNT(*) FROM Payments")).Should().Be(0);
        (await numbers.PeekNextAsync(DocumentType.Sale)).Should().Be(before);
    }

    [Fact]
    public async Task SecondLineFailure_RollsBackTheFirstLinesStock()
    {
        var (dolo, doloBatch) = await SeedItemAsync("Dolo 650", units: 100);
        var (pan, _) = await SeedItemAsync("Pan-D", units: 5);
        var first = await LineAsync(dolo, packs: 1);
        var second = (await LineAsync(pan, packs: 1)) with { AvailableUnits = 999 };
        var bill = BillingCalculator.ComputeBill(new BillRequest { Lines = new[] { first, second } });
        var request = CashSale(bill.Totals.NetPayablePaisa, first, second);
        var payment = BillingCalculator.ResolvePayment(bill.Totals.NetPayablePaisa, request).Resolution!;

        await FluentActions.Invoking(() => new BillingSaleRepository(Db).InsertAsync(new SalePosting(request, bill, payment, false)))
            .Should().ThrowAsync<BillingValidationException>();
        (await StockAsync(doloBatch)).Should().Be(100);
    }

    [Fact]
    public async Task Edit_RestoresOldLines_DeductsNewOnes_KeepsTheNumber()
    {
        var (medicineId, batchId) = await SeedItemAsync();
        var service = NewService();
        var posted = await service.SaveAsync(CashSale(18800, await LineAsync(medicineId, packs: 1)));
        (await StockAsync(batchId)).Should().Be(85);

        var edit = (await service.LoadForEditAsync(posted.SaleId))!;
        edit.Lines[0].AvailableUnits.Should().Be(100);
        var changed = edit with
        {
            Lines = new[] { edit.Lines[0] with { Packs = 2 } },
            Tenders = new[] { new SaleTender { Mode = TenderMode.Upi, AmountPaisa = 37700, Reference = "UTR123" } }
        };
        var saved = await service.SaveAsync(changed);

        saved.SaleId.Should().Be(posted.SaleId);
        saved.InvoiceNumber.Should().Be(posted.InvoiceNumber);
        (await StockAsync(batchId)).Should().Be(70);
        (await LongAsync("SELECT COUNT(*) FROM Payments WHERE TransactionId = @S", ("@S", posted.SaleId))).Should().Be(1);
        var invoice = (await service.GetInvoiceAsync(posted.SaleId))!;
        invoice.PaymentMethod.Should().Be(PaymentMethod.UPI);
        invoice.Payments[0].Reference.Should().Be("UTR123");
    }

    [Fact]
    public async Task Delete_RestoresStock_RemovesPayments_AndBlocksSecondDelete()
    {
        var (medicineId, batchId) = await SeedItemAsync();
        var service = NewService();
        var posted = await service.SaveAsync(CashSale(37700, await LineAsync(medicineId, packs: 2)));

        await service.DeleteAsync(posted.SaleId, null, "Entered twice");

        (await StockAsync(batchId)).Should().Be(100);
        (await LongAsync("SELECT COUNT(*) FROM Payments WHERE TransactionId = @S", ("@S", posted.SaleId))).Should().Be(0);
        var invoice = (await service.GetInvoiceAsync(posted.SaleId))!;
        invoice.Status.Should().Be(SaleStatus.Cancelled);
        (await LongAsync("SELECT COUNT(*) FROM StockMovements WHERE BatchId = @B AND QuantityIn = 30", ("@B", batchId))).Should().Be(1);
        await FluentActions.Invoking(() => service.DeleteAsync(posted.SaleId, null, "again")).Should().ThrowAsync<BillingValidationException>();
        (await service.LoadForEditAsync(posted.SaleId)).Should().BeNull();
    }

    [Fact]
    public async Task CreditSale_ToCustomer_ShowsAsReceivable_UntilDeleted()
    {
        var (medicineId, _) = await SeedItemAsync();
        var customers = new BillingCustomerRepository(Db);
        var customerId = await customers.InsertAsync(new BillingCustomer { Name = "Asha", Phone = "9876543210", CreditDays = 15 });
        var service = NewService();

        var posted = await service.SaveAsync(new SalePostingRequest
        {
            InvoiceDate = DateTime.Now,
            IsCounterSale = false,
            CustomerId = customerId,
            IsCredit = true,
            DueDate = DateTime.Today.AddDays(15),
            Lines = new[] { await LineAsync(medicineId, packs: 1) }
        });

        posted.BalanceDuePaisa.Should().Be(18800);
        (await customers.GetByIdAsync(customerId))!.OutstandingBalancePaisa.Should().Be(18800);
        var invoice = (await service.GetInvoiceAsync(posted.SaleId))!;
        invoice.PaymentStatus.Should().Be(PaymentStatus.Unpaid);
        invoice.DueDate.Should().Be(DateTime.Today.AddDays(15));
        invoice.CustomerName.Should().Be("Asha");

        await service.DeleteAsync(posted.SaleId, null, "returned");
        (await customers.GetByIdAsync(customerId))!.OutstandingBalancePaisa.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleH1Sale_IsListedInTheRegister()
    {
        var (medicineId, _) = await SeedItemAsync("Alprax 0.5", schedule: DrugSchedule.H1);
        var service = NewService();
        var line = await LineAsync(medicineId, packs: 1);
        await FluentActions.Invoking(() => service.SaveAsync(CashSale(18800, line)))
            .Should().ThrowAsync<BillingValidationException>(); // no patient / doctor / Rx yet

        var posted = await service.SaveAsync(CashSale(18800, line) with
        {
            PatientName = "Ravi Kumar", PatientAddress = "MG Road", DoctorName = "Dr Mehta", DoctorRegNo = "KMC-1234", PrescriptionNumber = "RX-77"
        });

        var register = await service.GetScheduleRegisterAsync(DateTime.Today.AddDays(-1), DateTime.Today);
        register.Should().ContainSingle();
        var entry = register[0];
        (entry.InvoiceNumber, entry.Schedule, entry.PatientName, entry.DoctorName, entry.DoctorRegNo, entry.PrescriptionNumber, entry.Quantity)
            .Should().Be((posted.InvoiceNumber, DrugSchedule.H1, "Ravi Kumar", "Dr Mehta", "KMC-1234", "RX-77", 15));
    }

    [Fact]
    public async Task Posting_DeletesTheSourceDraft_AndStoresFreeUnits()
    {
        var (medicineId, batchId) = await SeedItemAsync();
        var drafts = new SaleDraftRepository(Db);
        var draftId = await drafts.SaveAsync(null, new SaleDraftSummary { ItemCount = 1, TotalPaisa = 18800 }, "{}");

        await NewService().SaveAsync(CashSale(18800, await LineAsync(medicineId, packs: 1, free: 2)) with { DraftId = draftId });

        (await drafts.GetAsync(draftId)).Should().BeNull();
        (await StockAsync(batchId)).Should().Be(83);
        (await LongAsync("SELECT FreeUnits FROM SaleItems")).Should().Be(2);
    }

    [Fact]
    public async Task Replicate_UsesCurrentStock_ForANewBill()
    {
        var (medicineId, _) = await SeedItemAsync();
        var service = NewService();
        var posted = await service.SaveAsync(CashSale(18800, await LineAsync(medicineId, packs: 1)));
        var (request, warnings) = await service.ReplicateAsync(posted.SaleId);
        request.EditingSaleId.Should().BeNull();
        request.Lines.Should().ContainSingle(l => l.Packs == 1 && l.MedicineId == medicineId);
        warnings.Should().BeEmpty();
        var second = await service.SaveAsync(request with { Tenders = new[] { new SaleTender { AmountPaisa = 18800 } } });
        second.InvoiceNumber.Should().NotBe(posted.InvoiceNumber);
    }
}
