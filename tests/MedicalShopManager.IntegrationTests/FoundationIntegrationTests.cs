using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Database.Migrations;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Repositories;
using Xunit;

namespace MedicalShopManager.IntegrationTests;

/// <summary>
/// Migration registry, the v1 → v3 upgrade path, document numbering and item master v2 against real SQLite files.
/// </summary>
public class FoundationIntegrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"MSM_Foundation_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
        }
    }

    private DatabaseManager NewDb(IEnumerable<IMigration>? migrations = null) =>
        new(_dbPath, NullLogger<DatabaseManager>.Instance, migrations);

    private DocumentNumberService NewNumbers(DatabaseManager db) => new(db, NullLogger<DocumentNumberService>.Instance);

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    // ─── Migration registry ───

    [Fact]
    public async Task Initialize_AppliesEveryDiscoveredMigration_AndIsIdempotent()
    {
        using var db = NewDb();
        await db.InitializeAsync();
        await db.InitializeAsync(); // second run must be a no-op

        var applied = await db.GetAppliedVersionsAsync();
        Assert.Equal(MigrationCatalog.Discover().Select(m => m.Version).ToHashSet(), applied.ToHashSet());

        using var conn = db.CreateNewConnection();
        Assert.Equal(db.LatestSchemaVersion.ToString(), await ScalarAsync(conn, "SELECT Value FROM AppSettings WHERE Key = 'SchemaVersion'"));
        Assert.Equal(0L, await ScalarAsync(conn, "SELECT COUNT(*) FROM AppSettings WHERE Key IN ('InvoicePrefix','InvoiceNextNumber')"));
        Assert.Equal((long)Enum.GetValues<DocumentType>().Length, await ScalarAsync(conn, "SELECT COUNT(*) FROM DocumentSequences"));
    }

    private sealed class FailingMigration : IMigration
    {
        public int Version => 990;
        public string Description => "fails half way";
        public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            await MigrationSql.ExecuteAsync(connection, transaction, "CREATE TABLE HalfDone (Id INTEGER);");
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class LateModuleMigration : IMigration
    {
        public int Version => 900; // test-only, outside every reserved range
        public string Description => "lower number merged after a higher one";
        public Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction) =>
            MigrationSql.ExecuteAsync(connection, transaction, "CREATE TABLE LateModule (Id INTEGER);");
    }

    private sealed class HighMigration : IMigration
    {
        public int Version => 950; // test-only, outside every reserved range
        public string Description => "higher number applied first";
        public Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction) =>
            MigrationSql.ExecuteAsync(connection, transaction, "CREATE TABLE HighFirst (Id INTEGER);");
    }

    [Fact]
    public async Task FailedMigration_RollsBackAndIsNotRecorded()
    {
        var migrations = MigrationCatalog.Discover().Append(new FailingMigration()).ToList();
        using var db = NewDb(migrations);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(db.InitializeAsync);
        Assert.Contains("v990", ex.Message);

        var applied = await db.GetAppliedVersionsAsync();
        Assert.DoesNotContain(990, applied);
        Assert.Contains(3, applied); // earlier migrations committed in their own transactions

        using var conn = db.CreateNewConnection();
        Assert.Equal(0L, await ScalarAsync(conn, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'HalfDone'"));
    }

    [Fact]
    public async Task PendingMeansNotRecorded_SoALowerNumberMergedLaterStillRuns()
    {
        using (var first = NewDb(MigrationCatalog.Discover().Append(new HighMigration())))
            await first.InitializeAsync();

        using var second = NewDb(MigrationCatalog.Discover().Append(new HighMigration()).Append(new LateModuleMigration()));
        await second.InitializeAsync();

        var applied = await second.GetAppliedVersionsAsync();
        Assert.Contains(900, applied);
        Assert.Contains(950, applied);
    }

    // ─── v1 → v3 upgrade with data ───

    [Fact]
    public async Task UpgradeFromV1_ConvertsItemMasterAndContinuesInvoiceSeries()
    {
        // Build a v1-only database with data in the old shape.
        using (var v1 = NewDb(new IMigration[] { new Migration0001InitialSchema() }))
        {
            await v1.InitializeAsync();
            using var conn = v1.CreateNewConnection();
            await ExecAsync(conn, @"
                INSERT INTO Medicines (Id, Name, GenericName, PackSize, PrescriptionClassification, ReorderLevel, DefaultSellingPricePaisa)
                VALUES (1, 'Dolo 650', ' Paracetamol 650mg ', 15, 2, 25, 200),
                       (2, 'Calpol 650', 'paracetamol 650mg', 10, 0, 5, 0);
                INSERT INTO MedicineBatches (Id, MedicineId, BatchNumber, ExpiryDate, PurchaseDate, QuantityReceived, QuantityRemaining,
                    PurchaseCostPerUnitPaisa, SellingPricePerUnitPaisa, MrpPerUnitPaisa)
                VALUES (1, 1, 'B1', '2027-06-30', '2026-01-01', 150, 50, 150, 200, 226);
                UPDATE AppSettings SET Value = 'BILL' WHERE Key = 'InvoicePrefix';
                UPDATE AppSettings SET Value = '42' WHERE Key = 'InvoiceNextNumber';");
        }

        using var db = NewDb();
        await db.InitializeAsync();

        var medicines = new MedicineRepository(db, NullLogger<MedicineRepository>.Instance);
        var dolo = await medicines.GetByIdAsync(1);
        var calpol = await medicines.GetByIdAsync(2);
        Assert.NotNull(dolo);
        Assert.Equal("Paracetamol 650mg", dolo!.Composition);
        Assert.NotNull(dolo.CompositionId);
        Assert.Equal(dolo.CompositionId, calpol!.CompositionId); // same salt, different case
        Assert.Equal(15, dolo.UnitsPerPack);
        Assert.Equal(DrugSchedule.H1, dolo.Schedule);
        Assert.Equal(25, dolo.MinStock);
        Assert.Equal(3000, dolo.DefaultSellingPricePaisa); // 200 per unit × 15
        Assert.True(dolo.AllowLooseSale);

        var batch = await new BatchRepository(db, NullLogger<BatchRepository>.Instance).GetByIdAsync(1);
        Assert.Equal(15, batch!.UnitsPerPack);
        Assert.Equal(2250, batch.PurchaseRatePaisa);
        Assert.Equal(3000, batch.SaleRatePaisa);
        Assert.Equal(3390, batch.MrpPaisa);
        Assert.Equal(50, batch.QuantityRemaining); // units unchanged
        Assert.Equal("3 + 5", batch.QuantityRemainingDisplay);

        var numbers = NewNumbers(db);
        var sale = await numbers.GetSequenceAsync(DocumentType.Sale);
        Assert.Equal("BILL", sale!.Prefix);
        Assert.Equal(42, sale.NextNumber);
        Assert.Equal($"BILL-{DateTime.Now:yyyy}-00042", await numbers.AllocateNextAsync(DocumentType.Sale, DateTime.Now));
    }

    // ─── Document numbering ───

    [Fact]
    public async Task Numbers_AreSequential_PeekDoesNotConsume_AndResetInNewFinancialYear()
    {
        using var db = NewDb();
        await db.InitializeAsync();
        var numbers = NewNumbers(db);

        var may = new DateTime(2026, 5, 10);
        Assert.Equal("SR/2627/00001", await numbers.PeekNextAsync(DocumentType.SaleReturn, may));
        Assert.Equal("SR/2627/00001", await numbers.PeekNextAsync(DocumentType.SaleReturn, may));
        Assert.Equal("SR/2627/00001", await numbers.AllocateNextAsync(DocumentType.SaleReturn, may));
        Assert.Equal("SR/2627/00002", await numbers.AllocateNextAsync(DocumentType.SaleReturn, may));

        // FY 2027-28 has its own counter; a document back-dated into 2026-27 afterwards continues that year's
        // series, so it can never repeat a number already issued.
        var nextFy = new DateTime(2027, 4, 2);
        Assert.Equal("SR/2728/00001", await numbers.PeekNextAsync(DocumentType.SaleReturn, nextFy));
        Assert.Equal("SR/2728/00001", await numbers.AllocateNextAsync(DocumentType.SaleReturn, nextFy));
        Assert.Equal("SR/2627/00003", await numbers.AllocateNextAsync(DocumentType.SaleReturn, new DateTime(2027, 3, 31)));
        Assert.Equal(4, (await numbers.GetSequenceAsync(DocumentType.SaleReturn, may))!.NextNumber);
        Assert.Equal(2, (await numbers.GetSequenceAsync(DocumentType.SaleReturn, nextFy))!.NextNumber);
    }

    [Fact]
    public async Task Numbers_ConcurrentAllocationsAreUnique()
    {
        using var db = NewDb();
        await db.InitializeAsync();
        var numbers = NewNumbers(db);

        var results = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => Task.Run(() => numbers.AllocateNextAsync(DocumentType.Receipt, new DateTime(2026, 6, 1)))));

        Assert.Equal(25, results.Distinct().Count());
        Assert.Equal(26, (await numbers.GetSequenceAsync(DocumentType.Receipt, new DateTime(2026, 6, 1)))!.NextNumber);
    }

    [Fact]
    public async Task UpdateSequence_ValidatesAndPersists()
    {
        using var db = NewDb();
        await db.InitializeAsync();
        var numbers = NewNumbers(db);

        var sequence = (await numbers.GetSequenceAsync(DocumentType.Expense))!;
        sequence.Template = "{PREFIX}/{SEQ}"; // resets per FY without an FY token → would repeat numbers
        await Assert.ThrowsAsync<ArgumentException>(() => numbers.UpdateSequenceAsync(sequence));

        sequence.ResetEachFinancialYear = false;
        sequence.Prefix = "EX";
        sequence.NextNumber = 100;
        sequence.Padding = 4;
        await numbers.UpdateSequenceAsync(sequence);

        Assert.Equal("EX/0100", await numbers.AllocateNextAsync(DocumentType.Expense));
    }

    [Fact]
    public async Task SaleInsert_AllocatesNumberInsideTransaction_AndRollbackDoesNotConsumeIt()
    {
        using var db = NewDb();
        await db.InitializeAsync();
        var numbers = NewNumbers(db);
        var sales = new SaleRepository(db, NullLogger<SaleRepository>.Instance);

        var medicineId = await new MedicineRepository(db, NullLogger<MedicineRepository>.Instance)
            .InsertAsync(new Medicine { Name = "Dolo 650", UnitsPerPack = 15 });
        var batchId = await new BatchRepository(db, NullLogger<BatchRepository>.Instance).InsertAsync(new MedicineBatch
        {
            MedicineId = medicineId, BatchNumber = "B1", UnitsPerPack = 15, ExpiryDate = DateTime.Today.AddYears(1),
            PurchaseDate = DateTime.Today, QuantityReceived = 30, QuantityRemaining = 30
        });

        Sale NewSale(long itemMedicineId) => new()
        {
            InvoiceDate = new DateTime(2026, 11, 4),
            Items = { new SaleItem { MedicineId = itemMedicineId, MedicineName = "Dolo 650", BatchId = batchId, BatchNumber = "B1",
                                     ExpiryDate = DateTime.Today.AddYears(1), Quantity = 1, UnitPricePaisa = 200, LineTotalPaisa = 200 } }
        };

        var ok = NewSale(medicineId);
        await sales.InsertWithItemsAsync(ok);
        Assert.Equal("INV-2026-00001", ok.InvoiceNumber);

        // FK violation on the item → whole insert rolls back, including the number.
        await Assert.ThrowsAsync<SqliteException>(() => sales.InsertWithItemsAsync(NewSale(itemMedicineId: 9999)));
        Assert.Equal(2, (await numbers.GetSequenceAsync(DocumentType.Sale))!.NextNumber);

        var next = NewSale(medicineId);
        await sales.InsertWithItemsAsync(next);
        Assert.Equal("INV-2026-00002", next.InvoiceNumber);
    }

    // ─── Item master v2 round trip ───

    [Fact]
    public async Task Medicine_And_Batch_V2Fields_RoundTrip()
    {
        using var db = NewDb();
        await db.InitializeAsync();
        var medicines = new MedicineRepository(db, NullLogger<MedicineRepository>.Instance);

        var id = await medicines.InsertAsync(new Medicine
        {
            Name = "Pan 40", Composition = "Pantoprazole 40mg", Manufacturer = "Alkem", PackLabel = "15's", UnitsPerPack = 15,
            AllowLooseSale = false, HsnCode = "30049099", GstRatePercent = 12, Schedule = DrugSchedule.H, Category = "Tablets",
            RackLocation = "B-2", MinStock = 30, MaxStock = 300, MaxDiscountPercent = 15, Barcode = "8901234567890",
            DefaultMrpPaisa = 15500, DefaultSellingPricePaisa = 14000
        });

        var loaded = (await medicines.GetByIdAsync(id))!;
        Assert.Equal("Pantoprazole 40mg", loaded.Composition);
        Assert.NotNull(loaded.CompositionId);
        Assert.Equal("Alkem", loaded.Manufacturer);
        Assert.Equal("15's", loaded.PackLabel);
        Assert.Equal(15, loaded.UnitsPerPack);
        Assert.False(loaded.AllowLooseSale);
        Assert.Equal(DrugSchedule.H, loaded.Schedule);
        Assert.Equal(15m, loaded.MaxDiscountPercent);
        Assert.Equal(15500, loaded.DefaultMrpPaisa);
        Assert.True(loaded.IsActive);

        // Search finds by composition and manufacturer.
        Assert.Single(await medicines.SearchAsync("pantoprazole"));
        Assert.Single(await medicines.SearchAsync("Alkem"));

        // Clearing the composition clears the id.
        loaded.Composition = null;
        loaded.MaxDiscountPercent = null;
        await medicines.UpdateAsync(loaded);
        var cleared = (await medicines.GetByIdAsync(id))!;
        Assert.Null(cleared.CompositionId);
        Assert.Null(cleared.MaxDiscountPercent);

        var batches = new BatchRepository(db, NullLogger<BatchRepository>.Instance);
        var batchId = await batches.InsertAsync(new MedicineBatch
        {
            MedicineId = id, BatchNumber = "P1", UnitsPerPack = 15, ExpiryDate = MedicineBatch.ExpiryFromMonthYear(6, 27),
            PurchaseDate = DateTime.Today, QuantityReceived = 165, QuantityRemaining = 165, FreeUnits = 15,
            PurchaseRatePaisa = 9800, PtrPaisa = 10000, SaleRatePaisa = 14000, MrpPaisa = 15500, GstRatePercent = 12
        });
        var batch = (await batches.GetByIdAsync(batchId))!;
        Assert.Equal(new DateTime(2027, 6, 30), batch.ExpiryDate);
        Assert.Equal(9800, batch.PurchaseRatePaisa);
        Assert.Equal(10000, batch.PtrPaisa);
        Assert.Equal(14000, batch.SaleRatePaisa);
        Assert.Equal(15500, batch.MrpPaisa);
        Assert.Equal(15, batch.FreeUnits);
        Assert.Equal("11", batch.QuantityRemainingDisplay);
    }
}
