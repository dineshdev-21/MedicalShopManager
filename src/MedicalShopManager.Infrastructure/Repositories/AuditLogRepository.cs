using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class AuditLogRepository : IAuditLogRepository
{
    private readonly Database.DatabaseManager _db;

    public AuditLogRepository(Database.DatabaseManager db) => _db = db;

    public async Task InsertAsync(AuditLog log)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO AuditLogs (UserId, Username, Timestamp, Action, EntityType, EntityId, OldValues, NewValues, Reason, IpAddress)
            VALUES (@UserId, @Username, @Timestamp, @Action, @EntityType, @EntityId, @OldValues, @NewValues, @Reason, @IpAddress)";
        cmd.Parameters.AddWithValue("@UserId", log.UserId.HasValue ? log.UserId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@Username", (object?)log.Username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Timestamp", log.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("@Action", log.Action);
        cmd.Parameters.AddWithValue("@EntityType", (object?)log.EntityType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@EntityId", log.EntityId.HasValue ? log.EntityId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@OldValues", (object?)log.OldValues ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NewValues", (object?)log.NewValues ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Reason", (object?)log.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IpAddress", (object?)log.IpAddress ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<AuditLog>> GetByDateRangeAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM AuditLogs WHERE Timestamp BETWEEN @From AND @To ORDER BY Timestamp DESC LIMIT 500";
        cmd.Parameters.AddWithValue("@From", from.ToString("o"));
        cmd.Parameters.AddWithValue("@To", to.ToString("o"));
        return await ReadAuditLogs(cmd);
    }

    public async Task<IReadOnlyList<AuditLog>> GetByUserAsync(long userId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM AuditLogs WHERE UserId = @UserId ORDER BY Timestamp DESC LIMIT 500";
        cmd.Parameters.AddWithValue("@UserId", userId);
        return await ReadAuditLogs(cmd);
    }

    public async Task<IReadOnlyList<AuditLog>> GetRecentAsync(int limit = 100)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM AuditLogs ORDER BY Timestamp DESC, Id DESC LIMIT @Limit";
        cmd.Parameters.AddWithValue("@Limit", limit);
        return await ReadAuditLogs(cmd);
    }

    public async Task<IReadOnlyList<AuditLog>> GetByEntityAsync(string entityType, long entityId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM AuditLogs WHERE EntityType = @EntityType AND EntityId = @EntityId ORDER BY Timestamp DESC";
        cmd.Parameters.AddWithValue("@EntityType", entityType);
        cmd.Parameters.AddWithValue("@EntityId", entityId);
        return await ReadAuditLogs(cmd);
    }

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AuditLogs";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<AuditLog>> ReadAuditLogs(SqliteCommand cmd)
    {
        var list = new List<AuditLog>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new AuditLog
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                UserId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? null : reader.GetInt64(reader.GetOrdinal("UserId")),
                Username = reader.IsDBNull(reader.GetOrdinal("Username")) ? null : reader.GetString(reader.GetOrdinal("Username")),
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("Timestamp"))),
                Action = reader.GetString(reader.GetOrdinal("Action")),
                EntityType = reader.IsDBNull(reader.GetOrdinal("EntityType")) ? null : reader.GetString(reader.GetOrdinal("EntityType")),
                EntityId = reader.IsDBNull(reader.GetOrdinal("EntityId")) ? null : reader.GetInt64(reader.GetOrdinal("EntityId")),
                OldValues = reader.IsDBNull(reader.GetOrdinal("OldValues")) ? null : reader.GetString(reader.GetOrdinal("OldValues")),
                NewValues = reader.IsDBNull(reader.GetOrdinal("NewValues")) ? null : reader.GetString(reader.GetOrdinal("NewValues")),
                Reason = reader.IsDBNull(reader.GetOrdinal("Reason")) ? null : reader.GetString(reader.GetOrdinal("Reason")),
                IpAddress = reader.IsDBNull(reader.GetOrdinal("IpAddress")) ? null : reader.GetString(reader.GetOrdinal("IpAddress"))
            });
        }
        return list;
    }
}
