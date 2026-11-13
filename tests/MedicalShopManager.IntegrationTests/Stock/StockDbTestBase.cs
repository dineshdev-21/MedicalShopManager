using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Modules.Stock;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Repositories;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Stock;

/// <summary>Temp SQLite database with every migration applied, seed helpers and the real Stock services.</summary>
public abstract class StockDbTestBase : IAsyncLifetime
{
    protected readonly string DbPath = Path.Combine(Path.GetTempPath(), $"MSM_Stock_{Guid.NewGuid():N}.db");
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

    /// <summary>An item (15's strip by default, GST 12 %). Composition text is linked to a Compositions row like MedicineRepository does.</summary>
    protected async Task<long> SeedMedicineAsync(string name = "Dolo 650", string composition = "Paracetamol 650mg", int pack = 15,
        string manufacturer = "Micro Labs", int status = 0, int minStock = 0)
    {
        var compositionId = await LongAsync(@"
            INSERT INTO Compositions (Name) SELECT @C WHERE NOT EXISTS (SELECT 1 FROM Compositions WHERE Name = @C COLLATE NOCASE);
            SELECT Id FROM Compositions WHERE Name = @C COLLATE NOCASE;", ("@C", composition));
        return await LongAsync(@"
            INSERT INTO Medicines (Name, Composition, CompositionId, Manufacturer, PackLabel, UnitsPerPack, AllowLooseSale, Schedule, HsnCode,
                GstRatePercent, Status, DefaultMrpPaisa, RackLocation, MinStock)
            VALUES (@Name, @Comp, @CompId, @Mfr, @Label, @Pack, 1, 0, '30049099', 12, @Status, 18840, 'A1', @Min);
            SELECT last_insert_rowid();",
            ("@Name", name), ("@Comp", composition), ("@CompId", compositionId), ("@Mfr", manufacturer), ("@Label", $"{pack}'s"),
            ("@Pack", pack), ("@Status", status), ("@Min", minStock));
    }

    /// <summary>A batch row directly in MedicineBatches (no movement), rates per strip.</summary>
    protected Task<long> SeedBatchAsync(long medicineId, string number = "B1", int units = 30, int pack = 15, DateTime? expiry = null,
        long rate = 12000, long mrp = 18840) =>
        LongAsync(@"
            INSERT INTO MedicineBatches (MedicineId, BatchNumber, ExpiryDate, PurchaseDate, QuantityReceived, QuantityRemaining, FreeUnits,
                UnitsPerPack, PurchaseRatePaisa, PtrPaisa, SaleRatePaisa, MrpPaisa, DiscountPercent, GstRatePercent, IsActive)
            VALUES (@M, @B, @E, @P, @Q, @Q, 0, @Pack, @Rate, 0, @Mrp, @Mrp, 0, 12, 1);
            SELECT last_insert_rowid();",
            ("@M", medicineId), ("@B", number), ("@E", (expiry ?? Today.AddYears(1)).ToString("yyyy-MM-dd")),
            ("@P", Today.ToString("yyyy-MM-dd")), ("@Q", units), ("@Pack", pack), ("@Rate", rate), ("@Mrp", mrp));

    protected Task<long> SeedSupplierAsync(string name = "Alpha Pharma", string phone = "9800000001") =>
        LongAsync("INSERT INTO Suppliers (Name, Phone, IsActive) VALUES (@N, @P, 1); SELECT last_insert_rowid();", ("@N", name), ("@P", phone));

    /// <summary>A purchase with one line (Quantity in units, rate per pack).</summary>
    protected async Task<long> SeedPurchaseAsync(long supplierId, long medicineId, string batch, DateTime date, int units, long rate,
        string invoice = "INV-1", PurchaseStatus status = PurchaseStatus.Confirmed, int free = 0)
    {
        var purchaseId = await LongAsync(@"
            INSERT INTO Purchases (SupplierInvoiceNumber, SupplierId, SupplierName, PurchaseDate, Status)
            VALUES (@Inv, @S, (SELECT Name FROM Suppliers WHERE Id = @S), @D, @Status);
            SELECT last_insert_rowid();", ("@Inv", invoice), ("@S", supplierId), ("@D", date.ToString("yyyy-MM-dd")), ("@Status", (int)status));
        await ExecAsync(@"
            INSERT INTO PurchaseItems (PurchaseId, MedicineId, MedicineName, BatchNumber, ExpiryDate, Quantity, FreeQuantity, PurchasePricePaisa,
                MrpPaisa, GstRatePercent, LineTotalPaisa)
            VALUES (@P, @M, 'x', @B, @E, @Q, @F, @R, 18840, 12, @Total)",
            ("@P", purchaseId), ("@M", medicineId), ("@B", batch), ("@E", Today.AddYears(1).ToString("yyyy-MM-dd")), ("@Q", units), ("@F", free),
            ("@R", rate), ("@Total", rate * units / 15));
        return purchaseId;
    }

    protected async Task<long> SeedSaleAsync(long medicineId, long batchId, string invoice, int units, string? customer = null)
    {
        var saleId = await LongAsync(@"
            INSERT INTO Sales (InvoiceNumber, InvoiceDate, CustomerName) VALUES (@Inv, @D, @C); SELECT last_insert_rowid();",
            ("@Inv", invoice), ("@D", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")), ("@C", customer));
        await ExecAsync(@"
            INSERT INTO SaleItems (SaleId, MedicineId, MedicineName, BatchId, BatchNumber, ExpiryDate, Quantity, UnitPricePaisa, DiscountPercent, LineTotalPaisa)
            VALUES (@S, @M, 'x', @B, (SELECT BatchNumber FROM MedicineBatches WHERE Id = @B), @E, @Q, 1256, 10, @T)",
            ("@S", saleId), ("@M", medicineId), ("@B", batchId), ("@E", Today.AddYears(1).ToString("yyyy-MM-dd")), ("@Q", units), ("@T", 1256L * units));
        return saleId;
    }

    protected Task<long> StockOfAsync(long batchId) => LongAsync("SELECT QuantityRemaining FROM MedicineBatches WHERE Id = @Id", ("@Id", batchId));

    protected Task<long> CountAsync(string table) => LongAsync($"SELECT COUNT(*) FROM {table}");

    protected Task<string> PeekAdjustmentNumberAsync() =>
        new DocumentNumberService(Db, NullLogger<DocumentNumberService>.Instance).PeekNextAsync(DocumentType.StockAdjustment, Today);

    protected StockRepository Repo() => new(Db);
    protected StockAdjustmentRepository Postings() => new(Db);

    protected AuditService Audit()
    {
        var auth = new AuthService(new UserRepository(Db), NullLogger<AuthService>.Instance);
        return new AuditService(new AuditLogRepository(Db), auth, NullLogger<AuditService>.Instance);
    }

    protected StockService Service() => new(Repo(), Postings(), new AppSettingsRepository(Db), Audit(),
        new AuthService(new UserRepository(Db), NullLogger<AuthService>.Instance),
        new DocumentNumberService(Db, NullLogger<DocumentNumberService>.Instance), new ExcelSyncQueueRepository(Db),
        NullLogger<StockService>.Instance);

    protected MedicineService Medicines() => new(new MedicineRepository(Db, NullLogger<MedicineRepository>.Instance),
        new BatchRepository(Db, NullLogger<BatchRepository>.Instance), Audit(), new ExcelSyncQueueRepository(Db),
        NullLogger<MedicineService>.Instance);

    protected StockImportService Import() => new(Repo(), Service(), Medicines(), new AppSettingsRepository(Db), Audit(),
        NullLogger<StockImportService>.Instance);
}
