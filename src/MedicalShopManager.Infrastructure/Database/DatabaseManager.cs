using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Infrastructure.Database.Migrations;

namespace MedicalShopManager.Infrastructure.Database;

/// <summary>
/// Manages SQLite database lifecycle: creation, migrations, and connection management.
/// The database is stored in the user's Documents folder, never in the installation directory.
/// FROZEN for module agents: add schema through a new <see cref="IMigration"/> class, never by editing this file.
/// </summary>
public class DatabaseManager : IDisposable
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly ILogger<DatabaseManager> _logger;
    private readonly IReadOnlyList<IMigration> _migrations;
    private SqliteConnection? _connection;

    public string DatabasePath => _databasePath;

    /// <summary>Highest migration version known to this build.</summary>
    public int LatestSchemaVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    /// <param name="databasePath">Database file; null uses Documents\MedicalShopManager\Data\MedicalShop.db.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="migrations">Migrations to apply; null discovers every <see cref="IMigration"/> in Infrastructure.</param>
    public DatabaseManager(string? databasePath, ILogger<DatabaseManager> logger, IEnumerable<IMigration>? migrations = null)
    {
        _logger = logger;
        _migrations = migrations?.OrderBy(m => m.Version).ToList() ?? MigrationCatalog.Discover();

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            var documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var appDataFolder = Path.Combine(documentsFolder, "MedicalShopManager", "Data");
            Directory.CreateDirectory(appDataFolder);
            _databasePath = Path.Combine(appDataFolder, "MedicalShop.db");
        }
        else
        {
            _databasePath = databasePath;
            var dir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true
        }.ToString();

        _logger.LogInformation("Database path: {Path}", _databasePath);
    }

    public SqliteConnection GetConnection()
    {
        if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();

            // Enable WAL mode for better concurrent read/write performance
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }
        return _connection;
    }

    public SqliteConnection CreateNewConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    /// <summary>
    /// Initialize the database: create SchemaMigrations if needed and apply every pending <see cref="IMigration"/>
    /// discovered in the Infrastructure assembly, in ascending version order, each in its own transaction that also
    /// records the version. "Pending" means not recorded, so a lower-numbered module migration merged after a higher
    /// one still runs.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing database...");

        using var connection = CreateNewConnection();

        await ExecuteNonQueryAsync(connection, @"
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                Version     INTEGER PRIMARY KEY,
                AppliedAt   TEXT NOT NULL DEFAULT (datetime('now')),
                Description TEXT
            );
        ");

        // A SchemaMigrations table from an older build may lack Description; the runner records it.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SchemaMigrations') WHERE name = 'Description' COLLATE NOCASE;";
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 0)
                await ExecuteNonQueryAsync(connection, "ALTER TABLE SchemaMigrations ADD COLUMN Description TEXT;");
        }

        var applied = await GetAppliedVersionsAsync(connection);

        foreach (var migration in _migrations.Where(m => !applied.Contains(m.Version)))
        {
            _logger.LogInformation("Applying migration v{Version}: {Description}", migration.Version, migration.Description);
            using var transaction = connection.BeginTransaction();
            try
            {
                await migration.ApplyAsync(connection, transaction);

                await MigrationSql.ExecuteAsync(connection, transaction,
                    "INSERT INTO SchemaMigrations (Version, Description) VALUES (@Version, @Description);",
                    ("@Version", migration.Version), ("@Description", migration.Description));

                transaction.Commit();
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, "Migration v{Version} failed and was rolled back", migration.Version);
                throw new InvalidOperationException($"Database migration v{migration.Version} ({migration.Description}) failed: {ex.Message}", ex);
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'AppSettings';";
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0)
            {
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE AppSettings SET Value = @Version, UpdatedAt = datetime('now') WHERE Key = 'SchemaVersion';";
                update.Parameters.AddWithValue("@Version", LatestSchemaVersion.ToString());
                await update.ExecuteNonQueryAsync();
            }
        }

        _logger.LogInformation("Database initialization complete. Schema version: {Version}", LatestSchemaVersion);
    }

    /// <summary>Versions recorded in SchemaMigrations.</summary>
    public async Task<IReadOnlySet<int>> GetAppliedVersionsAsync()
    {
        using var connection = CreateNewConnection();
        return await GetAppliedVersionsAsync(connection);
    }

    private static async Task<HashSet<int>> GetAppliedVersionsAsync(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Version FROM SchemaMigrations;";
        var versions = new HashSet<int>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            versions.Add(reader.GetInt32(0));
        return versions;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
