using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Modules.Purchases;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Repositories;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Purchases;

/// <summary>Temp SQLite database with every migration applied, plus seed helpers and real services for Purchases tests.</summary>
public abstract class PurchasesDbTestBase : IAsyncLifetime
{
    protected readonly string DbPath = Path.Combine(Path.GetTempPath(), $"MSM_Purchases_{Guid.NewGuid():N}.db");
    protected DatabaseManager Db = null!;
    protected static readonly DateTime Today = DateTime.Today;

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

    protected Task ExecAsync(string sql, params (string, object?)[] args) => ScalarAsync(sql, args);

    /// <summary>Item "name": 15's strip, GST 12 %, MRP ₹188.40 per strip.</summary>
    protected Task<long> SeedMedicineAsync(string name = "Dolo 650", int pack = 15, string? barcode = null) =>
        LongAsync(@"
            INSERT INTO Medicines (Name, Composition, Manufacturer, UnitsPerPack, AllowLooseSale, Schedule, HsnCode, GstRatePercent, Barcode,
                Status, DefaultMrpPaisa, RackLocation)
            VALUES (@Name, 'Paracetamol 650mg', 'Micro Labs', @Pack, 1, 0, '30049099', 12, @Barcode, 0, 18840, 'A1');
            SELECT last_insert_rowid();", ("@Name", name), ("@Pack", pack), ("@Barcode", barcode));

    protected Task<long> SeedSupplierAsync(string name = "Alpha Pharma", string? gstin = "29ABCDE1234F1Z5", int creditDays = 30) =>
        LongAsync(@"
            INSERT INTO Suppliers (Name, Gstin, StateCode, CreditDays, IsActive, OpeningBalancePaisa) VALUES (@N, @G, '29', @C, 1, 0);
            SELECT last_insert_rowid();", ("@N", name), ("@G", gstin), ("@C", creditDays));

    protected Task<long> StockAsync(long batchId) => LongAsync("SELECT QuantityRemaining FROM MedicineBatches WHERE Id = @Id", ("@Id", batchId));

    protected PurchaseBillRepository Bills() => new(Db);
    protected PurchaseReturnRepository ReturnsRepo() => new(Db);
    protected DistributorRepository DistributorsRepo() => new(Db);

    protected PurchaseEntryService EntryService() => new(Bills(), DistributorsRepo(), new AppSettingsRepository(Db), Audit(),
        new ExcelSyncQueueRepository(Db), NullLogger<PurchaseEntryService>.Instance);

    protected PurchaseReturnService ReturnService() => new(ReturnsRepo(), DistributorsRepo(),
        new DocumentNumberService(Db, NullLogger<DocumentNumberService>.Instance), new AppSettingsRepository(Db), Audit(),
        new ExcelSyncQueueRepository(Db), NullLogger<PurchaseReturnService>.Instance);

    protected DistributorService DistributorService() => new(DistributorsRepo(), Audit(), new ExcelSyncQueueRepository(Db));

    private AuditService Audit()
    {
        var auth = new AuthService(new UserRepository(Db), NullLogger<AuthService>.Instance);
        return new AuditService(new AuditLogRepository(Db), auth, NullLogger<AuditService>.Instance);
    }

    protected Task<string> PeekAsync(DocumentType type, DateTime? date = null) =>
        new DocumentNumberService(Db, NullLogger<DocumentNumberService>.Instance).PeekNextAsync(type, date ?? Today);

    /// <summary>A valid bill: <paramref name="packs"/> strips of 15 at ₹120 (ex-GST), MRP ₹188.40, GST 12 %.</summary>
    protected static PurchaseBill Bill(long supplierId, long medicineId, string invoice = "INV-1", int packs = 10, string batch = "B1",
        DateTime? expiry = null, int freePacks = 0) => new()
    {
        SupplierId = supplierId,
        SupplierInvoiceNumber = invoice,
        InvoiceDate = Today,
        Lines =
        {
            new PurchaseBillLine
            {
                MedicineId = medicineId, MedicineName = "Dolo 650", HsnCode = "30049099", BatchNumber = batch,
                ExpiryDate = expiry ?? new DateTime(Today.Year + 2, 11, 30), UnitsPerPack = 15, Quantity = packs * 15,
                FreeQuantity = freePacks * 15, EnteredRatePaisa = 12000, MrpPaisa = 18840, SaleRatePaisa = 18840, GstRatePercent = 12
            }
        }
    };
}
