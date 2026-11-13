using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class AppSettingsRepository : IAppSettingsRepository
{
    private readonly Database.DatabaseManager _db;

    public AppSettingsRepository(Database.DatabaseManager db) => _db = db;

    public async Task<string?> GetValueAsync(string key)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppSettings WHERE Key = @Key";
        cmd.Parameters.AddWithValue("@Key", key);
        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    public async Task SetValueAsync(string key, string value, string? description = null)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO AppSettings (Key, Value, Description, UpdatedAt) VALUES (@Key, @Value, @Desc, @UpdatedAt)
            ON CONFLICT(Key) DO UPDATE SET Value = @Value, Description = COALESCE(@Desc, Description), UpdatedAt = @UpdatedAt";
        cmd.Parameters.AddWithValue("@Key", key);
        cmd.Parameters.AddWithValue("@Value", value);
        cmd.Parameters.AddWithValue("@Desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<AppSetting>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM AppSettings ORDER BY Key";
        var list = new List<AppSetting>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new AppSetting
            {
                Key = reader.GetString(reader.GetOrdinal("Key")),
                Value = reader.GetString(reader.GetOrdinal("Value")),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")))
            });
        }
        return list;
    }
}
