using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Modules.ControlCenter;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Repositories;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.IntegrationTests.ControlCenter;

/// <summary>Temp SQLite database with every migration applied and the real Control Center services wired up.</summary>
public abstract class ControlCenterDbTestBase : IAsyncLifetime
{
    protected readonly string DbPath = Path.Combine(Path.GetTempPath(), $"MSM_ControlCenter_{Guid.NewGuid():N}.db");
    protected DatabaseManager Db = null!;
    protected UserRepository Users = null!;
    protected AuthService Auth = null!;
    protected AuditLogRepository AuditLogs = null!;
    protected AuditService Audit = null!;
    protected ControlCenterSettingsStore Store = null!;
    protected ControlCenterSettingsService Settings = null!;
    protected DocumentNumberService Numbers = null!;
    protected SequenceSettingsService Sequences = null!;
    protected UserPermissionRepository PermissionRepository = null!;
    protected UserPermissionService Permissions = null!;
    protected UserAdminService UserAdmin = null!;

    public virtual async Task InitializeAsync()
    {
        Db = new DatabaseManager(DbPath, NullLogger<DatabaseManager>.Instance);
        await Db.InitializeAsync();

        Users = new UserRepository(Db);
        Auth = new AuthService(Users, NullLogger<AuthService>.Instance);
        AuditLogs = new AuditLogRepository(Db);
        Audit = new AuditService(AuditLogs, Auth, NullLogger<AuditService>.Instance);
        Store = new ControlCenterSettingsStore(Db);
        Settings = new ControlCenterSettingsService(Store, Audit, NullLogger<ControlCenterSettingsService>.Instance);
        Numbers = new DocumentNumberService(Db, NullLogger<DocumentNumberService>.Instance);
        Sequences = new SequenceSettingsService(Db, Numbers, Audit, NullLogger<SequenceSettingsService>.Instance);
        PermissionRepository = new UserPermissionRepository(Db);
        Permissions = new UserPermissionService(Users, PermissionRepository, Auth, Audit);
        UserAdmin = new UserAdminService(Users, Auth, Audit, NullLogger<UserAdminService>.Instance);
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

    protected async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }

    protected async Task<string?> SettingAsync(string key) =>
        await ScalarAsync("SELECT Value FROM AppSettings WHERE Key = @Key", ("@Key", key)) as string;

    protected async Task<IReadOnlyList<AuditLog>> AuditAsync(string action) =>
        (await AuditLogs.GetRecentAsync(500)).Where(l => l.Action == action).ToList();

    /// <summary>The seeded administrator (migration 1).</summary>
    protected async Task<User> AdminAsync() => (await Users.GetByUsernameAsync("admin"))!;
}
