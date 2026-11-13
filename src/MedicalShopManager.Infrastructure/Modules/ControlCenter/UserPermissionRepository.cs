using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.ControlCenter;

/// <summary>Per-user permission overrides (table ControlCenterUserPermissions, migration 19).</summary>
public sealed class UserPermissionRepository
{
    private readonly DatabaseManager _db;

    public UserPermissionRepository(DatabaseManager db) => _db = db;

    public async Task<IReadOnlyDictionary<string, bool>> GetOverridesAsync(long userId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PermissionKey, IsPermitted FROM ControlCenterUserPermissions WHERE UserId = @UserId";
        cmd.Parameters.AddWithValue("@UserId", userId);
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.GetInt64(1) == 1;
        return result;
    }

    /// <summary>Replaces all of a user's overrides in one transaction (delete + insert); all or nothing.</summary>
    public async Task ReplaceAsync(long userId, IReadOnlyDictionary<string, bool> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            using (var delete = conn.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM ControlCenterUserPermissions WHERE UserId = @UserId";
                delete.Parameters.AddWithValue("@UserId", userId);
                await delete.ExecuteNonQueryAsync();
            }

            if (overrides.Count > 0)
            {
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = @"
                    INSERT INTO ControlCenterUserPermissions (UserId, PermissionKey, IsPermitted, UpdatedAt)
                    VALUES (@UserId, @Key, @Permitted, @Now)";
                insert.Parameters.AddWithValue("@UserId", userId);
                insert.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o"));
                var key = insert.Parameters.Add("@Key", Microsoft.Data.Sqlite.SqliteType.Text);
                var permitted = insert.Parameters.Add("@Permitted", Microsoft.Data.Sqlite.SqliteType.Integer);
                foreach (var (k, v) in overrides)
                {
                    key.Value = k;
                    permitted.Value = v ? 1 : 0;
                    await insert.ExecuteNonQueryAsync();
                }
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
