using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Database.Migrations;
using Xunit;

namespace MedicalShopManager.IntegrationTests;

/// <summary>
/// Integration gate: a brand-new database file initialises with every migration of this build (foundation plus all
/// merged modules) and ends up with every table and view the shared kernel and the merged modules rely on.
/// Expectations about migrations come from <see cref="MigrationCatalog.Discover"/>, so modules merged later keep it green;
/// the table list is a floor (modules merged later only add tables).
/// </summary>
public class FoundationFreshDatabaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"MSM_Fresh_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch { /* best effort */ }
        }
    }

    private static readonly string[] ExpectedTables =
    {
        // foundation 1–3
        "Medicines", "MedicineBatches", "Suppliers", "Customers", "Sales", "SaleItems", "Purchases", "PurchaseItems",
        "Payments", "Expenses", "Returns", "StockMovements", "Users", "AuditLogs", "AppSettings", "ExcelSyncQueue",
        "SchemaMigrations", "Compositions", "DocumentSequences", "DocumentSequenceCounters",
        // Billing (10)
        "BillingSaleDrafts", "BillingCustomCharges",
        // SalesReturns (11)
        "SaleReturns", "SaleReturnItems", "SaleReturnRefunds",
        // Purchases (12)
        "PurchaseReturns", "PurchaseReturnItems", "PurchaseImportMappings",
        // Stock (13)
        "StockItemSettings", "StockAdjustments", "StockAdjustmentLines",
        // Finance (16)
        "FinanceAccounts", "FinanceMethodAccounts", "FinanceMovements", "FinanceCheques", "FinanceVouchers",
        "FinanceVoucherAllocations", "FinanceExpenseCategories",
        // Reports (18)
        "ReportFavourites",
        // Control Center (19)
        "ControlCenterUserPermissions", "ControlCenterAssets",
    };

    private static readonly string[] ExpectedViews = { "BillingScheduleRegister" };

    private static HashSet<string> Names(SqliteConnection conn, string type)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = $type";
        cmd.Parameters.AddWithValue("$type", type);
        using var reader = cmd.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void MigrationCatalog_HasUniqueVersions()
    {
        var versions = MigrationCatalog.Discover().Select(m => m.Version).ToList();
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.Contains(1, versions);
    }

    [Fact]
    public async Task FreshFile_InitializesWithEveryMigrationTableAndView()
    {
        using var db = new DatabaseManager(_path, NullLogger<DatabaseManager>.Instance);
        await db.InitializeAsync();
        await db.InitializeAsync(); // second start on the same file is a no-op

        var expectedVersions = MigrationCatalog.Discover().Select(m => m.Version).ToHashSet();
        Assert.Equal(expectedVersions, (await db.GetAppliedVersionsAsync()).ToHashSet());
        Assert.Equal(expectedVersions.Max(), db.LatestSchemaVersion);

        using var conn = db.CreateNewConnection();
        var tables = Names(conn, "table");
        var missingTables = ExpectedTables.Where(t => !tables.Contains(t)).ToList();
        Assert.True(missingTables.Count == 0, "missing tables: " + string.Join(", ", missingTables));

        var views = Names(conn, "view");
        var missingViews = ExpectedViews.Where(v => !views.Contains(v)).ToList();
        Assert.True(missingViews.Count == 0, "missing views: " + string.Join(", ", missingViews));

        // Every table and view compiles against the final schema (a view naming a dropped/renamed column fails here).
        foreach (var name in tables.Concat(views).Where(n => !n.StartsWith("sqlite_", StringComparison.Ordinal)))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{name}\" LIMIT 0";
            using var reader = cmd.ExecuteReader();
        }

        Assert.Equal("ok", Scalar(conn, "PRAGMA integrity_check"));
        using (var fk = conn.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_key_check";
            using var reader = fk.ExecuteReader();
            Assert.False(reader.Read(), "foreign key violations on a fresh database");
        }

        // Seeded data every module depends on: the admin user, a sequence row per DocumentType, the schema version.
        Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM Users WHERE Username = 'admin'"));
        foreach (var type in Enum.GetValues<DocumentType>())
            Assert.Equal(1L, Scalar(conn, $"SELECT COUNT(*) FROM DocumentSequences WHERE DocumentType = {(int)type}"));
        Assert.Equal(db.LatestSchemaVersion.ToString(), Scalar(conn, "SELECT Value FROM AppSettings WHERE Key = 'SchemaVersion'"));
    }
}
