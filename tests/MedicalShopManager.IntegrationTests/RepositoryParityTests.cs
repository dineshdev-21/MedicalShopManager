using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Repositories;
using Xunit;

namespace MedicalShopManager.IntegrationTests;

/// <summary>
/// Round-trips model fields through the real SQLite schema so model, schema and repository SQL stay in step.
/// </summary>
public class RepositoryParityTests : IAsyncLifetime
{
    private readonly string _tempDbPath = Path.Combine(Path.GetTempPath(), $"MSM_Parity_{Guid.NewGuid():N}.db");
    private DatabaseManager _db = null!;

    public async Task InitializeAsync()
    {
        _db = new DatabaseManager(_tempDbPath, NullLogger<DatabaseManager>.Instance);
        await _db.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Medicine_ItemMasterFields_RoundTrip()
    {
        var repo = new MedicineRepository(_db, NullLogger<MedicineRepository>.Instance);

        var id = await repo.InsertAsync(new Medicine
        {
            Name = "Paracetamol 500",
            GstRatePercent = 12,
            Schedule = DrugSchedule.H,
            MinStock = 20,
            MaxStock = 200,
            Unit = "Strip",
            RackLocation = "A-3",
            DefaultSellingPricePaisa = 3550
        });

        var loaded = await repo.GetByIdAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(12m, loaded!.GstRatePercent);
        Assert.Equal(DrugSchedule.H, loaded.Schedule);
        Assert.Equal(20, loaded.MinStock);
        Assert.Equal(200, loaded.MaxStock);
        Assert.Equal("Strip", loaded.Unit);
        Assert.Equal("A-3", loaded.RackLocation);
        Assert.Equal(3550, loaded.DefaultSellingPricePaisa);
        Assert.Equal(35.50m, loaded.DefaultSellingPrice);
    }

    [Fact]
    public async Task Supplier_OutstandingBalance_IsOpeningPlusConfirmedPurchaseBalances()
    {
        var suppliers = new SupplierRepository(_db, NullLogger<SupplierRepository>.Instance);
        var purchases = new PurchaseRepository(_db, NullLogger<PurchaseRepository>.Instance);

        var supplierId = await suppliers.InsertAsync(new Supplier
        {
            Name = "Alpha Pharma",
            DrugLicenseNumber = "KA-B20-12345",
            OpeningBalancePaisa = 10_000
        });

        var purchaseId = await purchases.InsertAsync(new Purchase
        {
            SupplierInvoiceNumber = "SUP-1",
            SupplierId = supplierId,
            SupplierName = "Alpha Pharma",
            GrandTotalPaisa = 50_000,
            BalancePaisa = 50_000,
            Status = PurchaseStatus.Draft
        });

        // Draft purchases are not owed yet
        var beforeConfirm = await suppliers.GetByIdAsync(supplierId);
        Assert.Equal("KA-B20-12345", beforeConfirm!.DrugLicenseNumber);
        Assert.Equal(10_000, beforeConfirm.OutstandingBalancePaisa);

        Assert.True(await purchases.UpdateStatusAsync(purchaseId, PurchaseStatus.Confirmed));

        var afterConfirm = await suppliers.GetByIdAsync(supplierId);
        Assert.Equal(PurchaseStatus.Confirmed, (await purchases.GetByIdAsync(purchaseId))!.Status);
        Assert.Equal(60_000, afterConfirm!.OutstandingBalancePaisa);
        Assert.Equal(600m, afterConfirm.OutstandingBalance);
        Assert.Equal(60_000, await suppliers.GetTotalOutstandingBalanceAsync());
    }

    [Fact]
    public async Task Customer_NewFields_RoundTrip_AndOutstandingFromSaleDues()
    {
        var repo = new CustomerRepository(_db);

        var id = await repo.InsertAsync(new Customer
        {
            Name = "Ravi Kumar",
            Phone = "9876543210",
            Email = "ravi@example.com",
            DoctorName = "Dr. Rao"
        });

        using (var conn = _db.CreateNewConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO Sales (InvoiceNumber, InvoiceDate, CustomerId, GrandTotalPaisa, BalanceDuePaisa)
                                VALUES ('INV-1', datetime('now'), @Id, 20000, 7500)";
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        var loaded = await repo.GetByPhoneAsync("9876543210");
        Assert.NotNull(loaded);
        Assert.Equal("ravi@example.com", loaded!.Email);
        Assert.Equal("Dr. Rao", loaded.DoctorName);
        Assert.True(loaded.IsActive);
        Assert.Equal(7500, loaded.OutstandingBalancePaisa);

        loaded.IsActive = false;
        loaded.DoctorName = "Dr. Iyer";
        Assert.True(await repo.UpdateAsync(loaded));

        var updated = await repo.GetByIdAsync(id);
        Assert.False(updated!.IsActive);
        Assert.Equal("Dr. Iyer", updated.DoctorName);
    }

    [Fact]
    public async Task AuditLog_GetRecentAsync_ReturnsNewestFirst_RespectingLimit()
    {
        var repo = new AuditLogRepository(_db);
        var start = DateTime.UtcNow.AddMinutes(-10);

        for (var i = 0; i < 3; i++)
            await repo.InsertAsync(new AuditLog { Action = $"Action{i}", Timestamp = start.AddMinutes(i) });

        var recent = await repo.GetRecentAsync(2);

        Assert.Equal(new[] { "Action2", "Action1" }, recent.Select(a => a.Action));
    }
}
