using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Infrastructure.Database;
using Xunit;

namespace MedicalShopManager.IntegrationTests;

public class DatabaseLifecycleTests : IDisposable
{
    private readonly string _tempDbPath;

    public DatabaseLifecycleTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"MSM_Test_{Guid.NewGuid():N}.db");
    }

    [Fact]
    public async Task InitializeAsync_CreatesAll17TablesAndSeedsDefaults()
    {
        // Arrange
        using var db = new DatabaseManager(_tempDbPath, NullLogger<DatabaseManager>.Instance);

        // Act
        await db.InitializeAsync();

        // Assert - Connect and inspect SQLite master
        using var conn = new SqliteConnection($"Data Source={_tempDbPath}");
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        // Check essential tables
        string[] expectedTables =
        {
            "Medicines", "MedicineBatches", "Suppliers", "Customers",
            "Sales", "SaleItems", "Purchases", "PurchaseItems",
            "Payments", "Expenses", "Returns", "StockMovements",
            "Users", "AuditLogs", "AppSettings",
            "ExcelSyncQueue", "SchemaMigrations",
            "Compositions", "DocumentSequences"
        };

        foreach (var expected in expectedTables)
        {
            Assert.Contains(expected, tables);
        }

        // Verify default admin user exists
        using var userCmd = conn.CreateCommand();
        userCmd.CommandText = "SELECT Username, Role FROM Users WHERE Username = 'admin';";
        using var userReader = await userCmd.ExecuteReaderAsync();
        Assert.True(await userReader.ReadAsync());
        Assert.Equal("admin", userReader.GetString(0));

        // Verify schema version is recorded
        using var migCmd = conn.CreateCommand();
        migCmd.CommandText = "SELECT MAX(Version) FROM SchemaMigrations;";
        var version = Convert.ToInt32(await migCmd.ExecuteScalarAsync());
        Assert.Equal(db.LatestSchemaVersion, version);
        Assert.True(version >= 3);

        // Verify SQLite PRAGMA integrity_check
        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA integrity_check;";
        var integrityResult = await pragmaCmd.ExecuteScalarAsync();
        Assert.Equal("ok", integrityResult?.ToString());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { /* ignore */ }
        }
    }
}
