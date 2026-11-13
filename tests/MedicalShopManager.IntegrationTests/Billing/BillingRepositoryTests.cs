using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Modules.Billing;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Billing;

/// <summary>Billing repositories (customers, catalog, drafts, custom charges), the prescription store and migration 10.</summary>
public class BillingRepositoryTests : BillingDbTestBase
{
    [Fact]
    public async Task Customers_InsertSearchUpdateDeactivate()
    {
        var repo = new BillingCustomerRepository(Db);
        var id = await repo.InsertAsync(new BillingCustomer
        {
            Kind = CustomerKind.Business, Name = "Rao Clinic", Phone = "9123456789", Gstin = "27ABCDE1234F1Z5", Pan = "ABCDE1234F",
            DefaultDiscountPercent = 7.5m, CreditDays = 30, OpeningBalancePaisa = 5000, OpeningBalanceDate = DateTime.Today
        });
        await repo.InsertAsync(new BillingCustomer { Name = "Asha", Phone = "9000000001", Gender = PersonGender.Female, DateOfBirth = new DateTime(1990, 1, 2) });

        (await repo.SearchAsync("rao")).Should().ContainSingle(c => c.Id == id);
        (await repo.SearchAsync("27ABCDE")).Should().ContainSingle(c => c.Id == id);
        (await repo.SearchAsync(null)).Should().HaveCount(2);
        (await repo.SearchAsync("100%")).Should().BeEmpty(); // LIKE wildcards are escaped

        var saved = (await repo.FindByPhoneAsync("9123456789"))!;
        (saved.Kind, saved.DefaultDiscountPercent, saved.CreditDays, saved.OutstandingBalancePaisa).Should().Be((CustomerKind.Business, 7.5m, 30, 5000L));

        saved.Name = "Rao Clinic & Labs";
        (await repo.UpdateAsync(saved)).Should().BeTrue();
        (await repo.GetByIdAsync(id))!.Name.Should().Be("Rao Clinic & Labs");

        (await repo.DeactivateAsync(id)).Should().BeTrue();
        (await repo.SearchAsync("rao")).Should().BeEmpty();
        (await repo.GetByIdAsync(id))!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Catalog_SearchesNameCompositionBarcode_AndCountsOnlySellableStock()
    {
        var (medicineId, _) = await SeedItemAsync("Dolo 650", units: 40, barcode: "8901234567890");
        await AddBatchAsync(medicineId, "OLD", 25, 18000, DateTime.Today.AddDays(-3)); // expired: not counted
        var catalog = new SaleCatalogRepository(Db);

        var byComposition = await catalog.SearchAsync("paracet");
        byComposition.Should().ContainSingle();
        var item = byComposition[0];
        (item.StockUnits, item.SellableBatchCount, item.MrpPerPackPaisa, item.UnitsPerPack).Should().Be((40L, 1, 18840L, 15));
        item.StockDisplay.Should().Be(BillingFormat.Quantity(40, 15));

        (await catalog.FindByCodeAsync("8901234567890"))!.MedicineId.Should().Be(medicineId);
        (await catalog.GetBatchesAsync(medicineId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Drafts_SaveUpdateListPurge()
    {
        var repo = new SaleDraftRepository(Db);
        var id = await repo.SaveAsync(null, new SaleDraftSummary { CustomerName = "Asha", ItemCount = 1, TotalPaisa = 100 }, "{\"a\":1}");
        (await repo.SaveAsync(id, new SaleDraftSummary { CustomerName = "Asha", ItemCount = 2, TotalPaisa = 300 }, "{\"a\":2}")).Should().Be(id);

        var draft = (await repo.GetAsync(id))!;
        (draft.Summary.ItemCount, draft.Summary.TotalPaisa, draft.Payload).Should().Be((2, 300L, "{\"a\":2}"));
        (await repo.ListAsync(DateTime.Now.AddDays(-7))).Should().ContainSingle();
        (await repo.PurgeAsync(DateTime.Now.AddMinutes(1))).Should().Be(1);
        (await repo.GetAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task DraftService_RoundTripsTheBill()
    {
        var (medicineId, _) = await SeedItemAsync();
        var service = new SaleDraftService(new SaleDraftRepository(Db), new Infrastructure.Repositories.AppSettingsRepository(Db),
            new Infrastructure.Services.AuthService(new Infrastructure.Repositories.UserRepository(Db), NullLogger<Infrastructure.Services.AuthService>.Instance),
            NullLogger<SaleDraftService>.Instance);
        var bill = CashSale(37700, await LineAsync(medicineId, packs: 2, loose: 3, discount: 5m)) with { PatientName = "Ravi", DoctorName = "Dr Rao" };

        var id = (await service.SaveAsync(bill, null, 37700))!.Value;
        var loaded = (await service.LoadAsync(id))!;

        loaded.DraftId.Should().Be(id);
        loaded.Tenders.Should().BeEmpty();
        (loaded.PatientName, loaded.DoctorName).Should().Be(("Ravi", "Dr Rao"));
        loaded.Lines.Should().ContainSingle(l => l.Packs == 2 && l.Loose == 3 && l.DiscountPercent == 5m && l.UnitsPerPack == 15);
        (await service.ListAsync()).Should().ContainSingle(d => d.Id == id && d.ItemCount == 1);
    }

    [Fact]
    public async Task CustomCharges_AreSeeded_CaseInsensitive_AndReactivated()
    {
        var repo = new CustomChargeRepository(Db);
        (await repo.GetActiveAsync()).Select(c => c.Name).Should().Contain("Delivery Fee");
        var id = await repo.EnsureAsync("Packing Charge");
        (await repo.EnsureAsync("packing charge")).Should().Be(id);
        await repo.DeactivateAsync(id);
        (await repo.GetActiveAsync()).Should().NotContain(c => c.Id == id);
        (await repo.EnsureAsync("Packing Charge")).Should().Be(id);
        (await repo.GetActiveAsync()).Should().Contain(c => c.Id == id);
        await FluentActions.Invoking(() => repo.EnsureAsync(" ")).Should().ThrowAsync<BillingValidationException>();
    }

    [Fact]
    public async Task PrescriptionStore_CopiesImages_AndRejectsOtherFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"MSM_Rx_{Guid.NewGuid():N}");
        var source = Path.Combine(Path.GetTempPath(), $"rx_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        try
        {
            var store = new PrescriptionFileStore(folder, NullLogger<PrescriptionFileStore>.Instance);
            var stored = await store.StoreAsync(source);
            File.Exists(stored).Should().BeTrue();
            stored.Should().StartWith(folder).And.EndWith(".png");

            var exe = Path.ChangeExtension(source, ".exe");
            File.Copy(source, exe);
            await FluentActions.Invoking(() => store.StoreAsync(exe)).Should().ThrowAsync<BillingValidationException>();
            File.Delete(exe);
            await FluentActions.Invoking(() => store.StoreAsync(Path.Combine(folder, "missing.png"))).Should().ThrowAsync<BillingValidationException>();
        }
        finally
        {
            File.Delete(source);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Migration10_AddsBillingSettings_AndRegisterView()
    {
        (await ScalarAsync("SELECT Value FROM AppSettings WHERE Key = 'Billing.DraftRetentionDays'")).Should().Be("7");
        (await ScalarAsync("SELECT Value FROM AppSettings WHERE Key = 'Billing.BatchOrder'")).Should().Be("0");
        (await LongAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'view' AND name = 'BillingScheduleRegister'")).Should().Be(1);
        (await LongAsync("SELECT COUNT(*) FROM pragma_table_info('Sales') WHERE name IN ('DoctorId','PrescriptionFilePath','RoundOffPaisa','WithGst','DueDate')"))
            .Should().Be(5);
    }
}

/// <summary>Migration 10 on a database written by the original v1 code (Fixtures/v1-original-database.sql).</summary>
public class BillingUpgradeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"MSM_BillingUpgrade_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm", _path + "-journal" })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task V1SaleLines_BecomePerPackLinesOfOne_AndOpenInTheBillingScreen()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-original-database.sql"));
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = script;
            cmd.ExecuteNonQuery();
        }

        using var db = new DatabaseManager(_path, NullLogger<DatabaseManager>.Instance);
        await db.InitializeAsync();

        var invoice = (await new BillingSaleRepository(db).GetInvoiceAsync(1))!;
        invoice.InvoiceNumber.Should().Be("INV-2026-00007");
        invoice.Status.Should().Be(SaleStatus.Active);
        invoice.IsCounterSale.Should().BeFalse(); // the v1 sale had customer 1
        var line = invoice.Lines.Single();
        (line.UnitsPerPack, line.Packs, line.Quantity, line.RatePerPackPaisa, line.MrpPerPackPaisa).Should().Be((1, 10L, 10, 213L, 213L));
        line.LineTotalPaisa.Should().Be(2130);
    }
}
