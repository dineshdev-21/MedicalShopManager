using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Modules.SalesReturns;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Repositories;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.IntegrationTests.SalesReturns;

/// <summary>
/// Temp SQLite database with every migration applied, plus seed helpers for SalesReturns tests. Sales are seeded with
/// plain SQL in the shape Billing (migration 10) writes them, so these tests do not depend on Billing's services.
/// </summary>
public abstract class SalesReturnsDbTestBase : IAsyncLifetime
{
    protected static readonly DateTime Today = DateTime.Today;

    protected readonly string DbPath = Path.Combine(Path.GetTempPath(), $"MSM_SalesReturns_{Guid.NewGuid():N}.db");
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

    protected Task<long> StockAsync(long batchId) => LongAsync("SELECT QuantityRemaining FROM MedicineBatches WHERE Id = @Id", ("@Id", batchId));

    protected Task SetStockAsync(long batchId, long units) =>
        ScalarAsync("UPDATE MedicineBatches SET QuantityRemaining = @U WHERE Id = @Id", ("@U", units), ("@Id", batchId));

    /// <summary>Medicine (15's, GST 12 %, MRP ₹188.40) with one batch; returns (medicineId, batchId).</summary>
    protected async Task<(long MedicineId, long BatchId)> SeedItemAsync(string name = "Dolo 650", long units = 100, DateTime? expiry = null)
    {
        var medicineId = await LongAsync(@"
            INSERT INTO Medicines (Name, Composition, Manufacturer, UnitsPerPack, AllowLooseSale, Schedule, HsnCode, GstRatePercent, Status, DefaultMrpPaisa)
            VALUES (@Name, 'Paracetamol 650mg', 'Micro Labs', 15, 1, 0, '30049099', 12, 0, 18840);
            SELECT last_insert_rowid();", ("@Name", name));
        var batchId = await LongAsync(@"
            INSERT INTO MedicineBatches (MedicineId, BatchNumber, ExpiryDate, PurchaseDate, QuantityReceived, QuantityRemaining,
                PurchaseRatePaisa, SaleRatePaisa, MrpPaisa, GstRatePercent, UnitsPerPack, IsActive)
            VALUES (@MedicineId, @Batch, @Expiry, @Purchased, @Units, @Units, 12000, 18840, 18840, 12, 15, 1);
            SELECT last_insert_rowid();",
            ("@MedicineId", medicineId), ("@Batch", "B-" + name[..3]), ("@Expiry", (expiry ?? Today.AddYears(1)).ToString("yyyy-MM-dd")),
            ("@Purchased", Today.AddMonths(-2).ToString("yyyy-MM-dd")), ("@Units", units));
        return (medicineId, batchId);
    }

    protected Task<long> SeedCustomerAsync(string name = "Ramesh Kumar", string phone = "9876543210") =>
        LongAsync("INSERT INTO Customers (Name, Phone, IsActive) VALUES (@Name, @Phone, 1); SELECT last_insert_rowid();",
            ("@Name", name), ("@Phone", phone));

    /// <summary>
    /// A posted sale of the golden line (1 pack + 5 loose of 15's at ₹188.40 = ₹251.20, taxable ₹224.29, bill ₹251.00)
    /// from <paramref name="batchId"/>, with the stock already taken out. Returns (saleId, saleItemId).
    /// </summary>
    protected async Task<(long SaleId, long SaleItemId)> SeedSaleAsync(long medicineId, long batchId, string number = "INV-2026-00001",
        long? customerId = null, long paidPaisa = 25100, PaymentMethod method = PaymentMethod.Cash, DateTime? date = null, int status = 1,
        DateTime? expiry = null)
    {
        var invoiceDate = date ?? Today.AddDays(-1).AddHours(11);
        var counter = customerId is null;
        var saleId = await LongAsync(@"
            INSERT INTO Sales (InvoiceNumber, InvoiceDate, CustomerId, CustomerName, CustomerPhone, SubtotalPaisa, DiscountAmountPaisa,
                TaxableAmountPaisa, CgstAmountPaisa, SgstAmountPaisa, IgstAmountPaisa, GrandTotalPaisa, AmountPaidPaisa, BalanceDuePaisa,
                PaymentMethod, PaymentStatus, Status, IsCounterSale, IsInterState, WithGst, RoundOffPaisa, CustomChargePaisa)
            VALUES (@Number, @Date, @CustomerId, @CustomerName, @Phone, 25120, 0, 22429, 1346, 1345, 0, 25100, @Paid, @Due,
                @Method, @PaymentStatus, @Status, @Counter, 0, 1, -20, 0);
            SELECT last_insert_rowid();",
            ("@Number", number), ("@Date", invoiceDate.ToString("yyyy-MM-dd HH:mm:ss")), ("@CustomerId", customerId),
            ("@CustomerName", counter ? "Cash / Counter Sale" : "Ramesh Kumar"), ("@Phone", counter ? null : "9876543210"),
            ("@Paid", paidPaisa), ("@Due", 25100 - paidPaisa), ("@Method", (int)method),
            ("@PaymentStatus", (int)(paidPaisa >= 25100 ? PaymentStatus.Paid : paidPaisa > 0 ? PaymentStatus.Partial : PaymentStatus.Unpaid)),
            ("@Status", status), ("@Counter", counter ? 1 : 0));
        var itemId = await LongAsync(@"
            INSERT INTO SaleItems (SaleId, MedicineId, MedicineName, BatchId, BatchNumber, ExpiryDate, HsnCode, Quantity, UnitPricePaisa,
                DiscountPercent, DiscountAmountPaisa, TaxableValuePaisa, GstRatePercent, CgstAmountPaisa, SgstAmountPaisa, IgstAmountPaisa,
                TaxAmountPaisa, CogsPaisa, LineTotalPaisa, UnitsPerPack, Packs, LooseUnits, FreeUnits, MrpPerPackPaisa, RatePerPackPaisa)
            VALUES (@SaleId, @MedicineId, 'Dolo 650', @BatchId, (SELECT BatchNumber FROM MedicineBatches WHERE Id = @BatchId), @Expiry, '30049099',
                20, 1256, 0, 0, 22429, 12, 1346, 1345, 0, 2691, 16000, 25120, 15, 1, 5, 0, 18840, 18840);
            SELECT last_insert_rowid();",
            ("@SaleId", saleId), ("@MedicineId", medicineId), ("@BatchId", batchId),
            ("@Expiry", (expiry ?? Today.AddYears(1)).ToString("yyyy-MM-dd")));
        await ScalarAsync("UPDATE MedicineBatches SET QuantityRemaining = QuantityRemaining - 20 WHERE Id = @Id", ("@Id", batchId));
        return (saleId, itemId);
    }

    protected SaleReturnRepository Repository() => new(Db);

    protected SaleReturnService Service()
    {
        var auth = new AuthService(new UserRepository(Db), NullLogger<AuthService>.Instance);
        var audit = new AuditService(new AuditLogRepository(Db), auth, NullLogger<AuditService>.Instance);
        return new SaleReturnService(Repository(), new DocumentNumberService(Db, NullLogger<DocumentNumberService>.Instance),
            new AppSettingsRepository(Db), audit, new ExcelSyncQueueRepository(Db), NullLogger<SaleReturnService>.Instance, () => Today);
    }

    protected DocumentNumberService Numbers() => new(Db, NullLogger<DocumentNumberService>.Instance);
}
