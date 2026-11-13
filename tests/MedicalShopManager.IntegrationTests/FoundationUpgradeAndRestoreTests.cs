using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Database.Migrations;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Repositories;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.IntegrationTests;

/// <summary>
/// Upgrade of a database file written by the ORIGINAL v1 code (Fixtures/v1-original-database.sql is a .dump of a
/// file created by the pre-registry DatabaseManager), migration ordering, numbering atomicity under rollbacks,
/// and backup restore into a live WAL database.
/// </summary>
public class FoundationUpgradeAndRestoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"MSM_Upgrade_{Guid.NewGuid():N}");

    public FoundationUpgradeAndRestoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    private static DatabaseManager NewDb(string path, IEnumerable<IMigration>? migrations = null) =>
        new(path, NullLogger<DatabaseManager>.Instance, migrations);

    /// <summary>Recreates the original v1 file (rollback journal, exactly as the old code left it).</summary>
    private string CreateOriginalV1File(string name)
    {
        var path = PathFor(name);
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-original-database.sql"));
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = script;
        cmd.ExecuteNonQuery();
        return path;
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every migration of this build (foundation 1–3 plus whatever modules have added).</summary>
    private static HashSet<int> AllVersions() => MigrationCatalog.Discover().Select(m => m.Version).ToHashSet();

    private static readonly string[] DataTables =
    {
        "Suppliers", "Customers", "Medicines", "MedicineBatches", "Purchases", "PurchaseItems", "Sales", "SaleItems",
        "StockMovements", "Payments", "Expenses", "Returns", "Users", "AppSettings"
    };

    private static Dictionary<string, long> RowCounts(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        return DataTables.ToDictionary(t => t, t => (long)Scalar(conn, $"SELECT COUNT(*) FROM {t}")!);
    }

    private static HashSet<string> SettingKeys(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key FROM AppSettings";
        using var reader = cmd.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    // ─── Original v1 file → current schema ───

    [Fact]
    public async Task OriginalV1File_UpgradesInPlace_KeepingEveryRow()
    {
        var path = CreateOriginalV1File("v1.db");
        var before = RowCounts(path);
        var keysBefore = SettingKeys(path);
        Assert.All(DataTables, t => Assert.True(before[t] > 0, $"fixture has no rows in {t}"));

        using var db = NewDb(path);
        await db.InitializeAsync();
        await db.InitializeAsync(); // idempotent on the upgraded file

        Assert.Equal(AllVersions(), (await db.GetAppliedVersionsAsync()).ToHashSet());

        var after = RowCounts(path);
        foreach (var table in DataTables.Where(t => t != "AppSettings"))
            Assert.Equal(before[table], after[table]);
        // AppSettings: every v1 key survives except InvoicePrefix / InvoiceNextNumber (moved to DocumentSequences).
        // Modules may seed their own '<Module>.<Key>' settings (contract section 6), so only namespaced keys may be added.
        var movedKeys = new[] { "InvoicePrefix", "InvoiceNextNumber" };
        var keysAfter = SettingKeys(path);
        Assert.Equal(new HashSet<string>(keysBefore.Except(movedKeys)), new HashSet<string>(keysAfter.Intersect(keysBefore)));
        Assert.All(keysAfter.Except(keysBefore), key => Assert.Contains('.', key));

        using var conn = db.CreateNewConnection();
        Assert.Equal("ok", Scalar(conn, "PRAGMA integrity_check"));
        using (var fk = conn.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_key_check";
            using var reader = fk.ExecuteReader();
            Assert.False(reader.Read(), "foreign key violations after upgrade");
        }

        // New columns and tables exist; renamed columns are gone.
        foreach (var column in new[] { "Composition", "CompositionId", "Manufacturer", "PackLabel", "UnitsPerPack", "AllowLooseSale",
                                       "Schedule", "MinStock", "MaxDiscountPercent", "DefaultMrpPaisa" })
            Assert.Equal(1L, Scalar(conn, $"SELECT COUNT(*) FROM pragma_table_info('Medicines') WHERE name = '{column}'"));
        foreach (var column in new[] { "GenericName", "PackSize", "PrescriptionClassification", "ReorderLevel" })
            Assert.Equal(0L, Scalar(conn, $"SELECT COUNT(*) FROM pragma_table_info('Medicines') WHERE name = '{column}'"));
        foreach (var column in new[] { "PurchaseRatePaisa", "SaleRatePaisa", "MrpPaisa", "PtrPaisa", "UnitsPerPack" })
            Assert.Equal(1L, Scalar(conn, $"SELECT COUNT(*) FROM pragma_table_info('MedicineBatches') WHERE name = '{column}'"));
        Assert.Equal(3L, Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Compositions','DocumentSequences','DocumentSequenceCounters')"));
        Assert.Equal(db.LatestSchemaVersion.ToString(), Scalar(conn, "SELECT Value FROM AppSettings WHERE Key = 'SchemaVersion'"));

        // Values converted: per-unit prices become per-pack rates; quantities stay in units.
        var medicines = new MedicineRepository(db, NullLogger<MedicineRepository>.Instance);
        var dolo = (await medicines.GetByIdAsync(1))!;
        Assert.Equal("Paracetamol 650mg", dolo.Composition);
        Assert.NotNull(dolo.CompositionId);
        Assert.Equal(15, dolo.UnitsPerPack);
        Assert.Equal(DrugSchedule.H, dolo.Schedule);
        Assert.Equal(20, dolo.MinStock);
        Assert.Equal(213 * 15, dolo.DefaultSellingPricePaisa);
        Assert.Equal(1, dolo.DefaultSupplierId);
        var syrup = (await medicines.GetByIdAsync(2))!;
        Assert.Equal(1, syrup.UnitsPerPack);           // pack size 0 in v1 is repaired to 1
        Assert.Equal(DrugSchedule.NRx, syrup.Schedule);
        Assert.Null(syrup.CompositionId);

        var batches = new BatchRepository(db, NullLogger<BatchRepository>.Instance);
        var b1 = (await batches.GetByIdAsync(1))!;
        Assert.Equal((15, 2250L, 3195L, 3390L, 140L), (b1.UnitsPerPack, b1.PurchaseRatePaisa, b1.SaleRatePaisa, b1.MrpPaisa, (long)b1.QuantityRemaining));
        var b2 = (await batches.GetByIdAsync(2))!;
        Assert.Equal((1, 6000L, 9000L, 9500L), (b2.UnitsPerPack, b2.PurchaseRatePaisa, b2.SaleRatePaisa, b2.MrpPaisa));

        // Old sale intact; the invoice series continues from the v1 setting (InvoiceNextNumber = 8).
        var sale = await new SaleRepository(db, NullLogger<SaleRepository>.Instance).GetByInvoiceNumberAsync("INV-2026-00007");
        Assert.NotNull(sale);
        Assert.Equal(2130, sale!.GrandTotalPaisa);
        var numbers = new DocumentNumberService(db, NullLogger<DocumentNumberService>.Instance);
        Assert.Equal("INV-2026-00008", await numbers.AllocateNextAsync(DocumentType.Sale, new DateTime(2026, 11, 4)));
        Assert.Equal("SR/2627/00001", await numbers.AllocateNextAsync(DocumentType.SaleReturn, new DateTime(2026, 11, 4)));
    }

    // ─── Migration ordering ───

    private sealed class CreatesTable : IMigration
    {
        public int Version => 900; // test-only versions sit outside every reserved range
        public string Description => "creates Ordered";
        public Task ApplyAsync(SqliteConnection c, SqliteTransaction t) => MigrationSql.ExecuteAsync(c, t, "CREATE TABLE Ordered (Id INTEGER, Note TEXT);");
    }

    private sealed class UsesTable : IMigration
    {
        public int Version => 950;
        public string Description => "needs Ordered";
        public Task ApplyAsync(SqliteConnection c, SqliteTransaction t) => MigrationSql.ExecuteAsync(c, t, "INSERT INTO Ordered VALUES (1, 'after 900');");
    }

    [Fact]
    public async Task Migrations_RunInAscendingVersionOrder_WhateverOrderTheyArePassedIn()
    {
        var path = PathFor("order.db");
        var migrations = new IMigration[] { new UsesTable(), new CreatesTable() }.Concat(MigrationCatalog.Discover().Reverse());
        using var db = NewDb(path, migrations);
        await db.InitializeAsync();

        using var conn = db.CreateNewConnection();
        Assert.Equal("after 900", Scalar(conn, "SELECT Note FROM Ordered"));
        Assert.Equal(950, db.LatestSchemaVersion);
    }

    [Fact]
    public async Task MigrationSql_RejectsNonIdentifierNames()
    {
        var path = PathFor("ident.db");
        using var db = NewDb(path);
        await db.InitializeAsync();
        using var conn = db.CreateNewConnection();
        using var tx = conn.BeginTransaction();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            MigrationSql.AddColumnIfMissingAsync(conn, tx, "Medicines; DROP TABLE Users", "X", "TEXT"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            MigrationSql.RenameColumnIfExistsAsync(conn, tx, "Medicines", "Name", "1Bad"));
        await MigrationSql.AddColumnIfMissingAsync(conn, tx, "Customers", "Loyalty_Points2", "INTEGER NOT NULL DEFAULT 0");
        Assert.True(await MigrationSql.ColumnExistsAsync(conn, tx, "Customers", "Loyalty_Points2"));
    }

    // ─── Numbering atomicity ───

    [Fact]
    public async Task Allocation_RolledBackInParallel_NeverConsumesOrDuplicatesANumber()
    {
        var path = PathFor("numbers.db");
        using var db = NewDb(path);
        await db.InitializeAsync();
        var date = new DateTime(2026, 7, 1);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            using var conn = db.CreateNewConnection();
            using var tx = conn.BeginTransaction();
            var number = await DocumentSequenceAllocator.AllocateAsync(conn, tx, DocumentType.SaleReturn, date);
            var commit = i % 2 == 1;
            if (commit) tx.Commit(); else tx.Rollback();
            return (number, commit);
        })));

        var committed = results.Where(r => r.commit).Select(r => r.number).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range(1, 10).Select(n => $"SR/2627/{n:D5}"), committed);

        var numbers = new DocumentNumberService(db, NullLogger<DocumentNumberService>.Instance);
        Assert.Equal(11, (await numbers.GetSequenceAsync(DocumentType.SaleReturn, date))!.NextNumber);
        // FY reset: the next financial year starts its own series while 2026-27 continues.
        Assert.Equal("SR/2728/00001", await numbers.AllocateNextAsync(DocumentType.SaleReturn, new DateTime(2027, 4, 1)));
        Assert.Equal("SR/2627/00011", await numbers.AllocateNextAsync(DocumentType.SaleReturn, new DateTime(2027, 3, 31)));
    }

    // ─── Backup restore ───

    private sealed class NoAudit : IAuditService
    {
        public Task LogAsync(string action, string? entityType = null, long? entityId = null,
            string? oldValues = null, string? newValues = null, string? reason = null) => Task.CompletedTask;
        public Task<IReadOnlyList<Core.Models.AuditLog>> GetLogsAsync(DateTime from, DateTime to) =>
            Task.FromResult<IReadOnlyList<Core.Models.AuditLog>>(Array.Empty<Core.Models.AuditLog>());
        public Task<IReadOnlyList<Core.Models.AuditLog>> GetLogsByUserAsync(long userId) => GetLogsAsync(default, default);
    }

    private BackupService NewBackups(DatabaseManager db) =>
        new(db, new NoAudit(), NullLogger<BackupService>.Instance, PathFor("backups"));

    private static long SupplierCount(DatabaseManager db, string name)
    {
        using var conn = db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Suppliers WHERE Name = @Name";
        cmd.Parameters.AddWithValue("@Name", name);
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task Restore_ReplacesLiveWalDatabase_AndKeepsASafetyCopyWithUncheckpointedData()
    {
        var path = PathFor("live.db");
        using var db = NewDb(path);
        await db.InitializeAsync();
        using (var conn = db.CreateNewConnection()) Exec(conn, "INSERT INTO Suppliers (Name) VALUES ('Before Backup')");

        var backups = NewBackups(db);
        var archive = await backups.CreateBackupAsync();

        // Written after the backup; with pooled connections open it sits in the -wal file.
        using var holder = db.CreateNewConnection();
        Exec(holder, "INSERT INTO Suppliers (Name) VALUES ('After Backup')");
        Assert.True(File.Exists(path + "-wal"));

        await backups.RestoreBackupAsync(archive);

        Assert.Equal(1, SupplierCount(db, "Before Backup"));
        Assert.Equal(0, SupplierCount(db, "After Backup"));
        Assert.Equal(0L, Scalar(holder, "SELECT COUNT(*) FROM Suppliers WHERE Name = 'After Backup'")); // open connections see it too
        Assert.Equal("ok", Scalar(holder, "PRAGMA integrity_check"));

        var safetyCopy = Directory.GetFiles(_dir, "live.db.prerestore.*").Single();
        using var safety = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = safetyCopy, Pooling = false }.ToString());
        safety.Open();
        Assert.Equal(1L, Scalar(safety, "SELECT COUNT(*) FROM Suppliers WHERE Name = 'After Backup'"));
    }

    [Fact]
    public async Task Restore_OfAnOriginalV1Backup_IsMigratedToTheCurrentSchema()
    {
        var v1 = CreateOriginalV1File("old-backup.db");
        var path = PathFor("current.db");
        using var db = NewDb(path);
        await db.InitializeAsync();

        await NewBackups(db).RestoreBackupAsync(v1);

        Assert.Equal(AllVersions(), (await db.GetAppliedVersionsAsync()).ToHashSet());
        var batch = (await new BatchRepository(db, NullLogger<BatchRepository>.Instance).GetByIdAsync(1))!;
        Assert.Equal(3390, batch.MrpPaisa);
        Assert.Equal(1, SupplierCount(db, "Pharma Dist"));
    }

    private sealed class FutureMigration : IMigration
    {
        public int Version => 9999;
        public string Description => "from a newer build";
        public Task ApplyAsync(SqliteConnection c, SqliteTransaction t) => MigrationSql.ExecuteAsync(c, t, "CREATE TABLE FutureOnly (Id INTEGER);");
    }

    [Fact]
    public async Task Restore_RefusesABackupFromANewerBuild_AndLeavesLiveDataUntouched()
    {
        using var future = NewDb(PathFor("future.db"), MigrationCatalog.Discover().Append(new FutureMigration()));
        await future.InitializeAsync();
        var archive = await NewBackups(future).CreateBackupAsync(PathFor("future-backups"));

        using var db = NewDb(PathFor("live2.db"));
        await db.InitializeAsync();
        using (var conn = db.CreateNewConnection()) Exec(conn, "INSERT INTO Suppliers (Name) VALUES ('Keep Me')");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBackups(db).RestoreBackupAsync(archive));
        Assert.Contains("newer version", ex.Message);
        Assert.Equal(1, SupplierCount(db, "Keep Me"));
    }
}
