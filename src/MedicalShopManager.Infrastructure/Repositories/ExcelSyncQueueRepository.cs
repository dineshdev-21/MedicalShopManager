using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class ExcelSyncQueueRepository : IExcelSyncQueueRepository
{
    private readonly Database.DatabaseManager _db;

    public ExcelSyncQueueRepository(Database.DatabaseManager db) => _db = db;

    public async Task EnqueueAsync(ExcelSyncQueueItem item)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ExcelSyncQueue (EntityType, EntityId, Status, RetryCount, CreatedAt)
            VALUES (@EntityType, @EntityId, @Status, 0, @CreatedAt)";
        cmd.Parameters.AddWithValue("@EntityType", (int)item.EntityType);
        cmd.Parameters.AddWithValue("@EntityId", item.EntityId.HasValue ? item.EntityId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", (int)SyncStatus.Pending);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task EnqueueAsync(SyncEntityType entityType, long? entityId = null)
    {
        await EnqueueAsync(new ExcelSyncQueueItem
        {
            EntityType = entityType,
            EntityId = entityId,
            Status = SyncStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });
    }

    public async Task<IReadOnlyList<ExcelSyncQueueItem>> GetPendingAsync(int limit = 100)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM ExcelSyncQueue 
            WHERE Status IN (0, 3) AND RetryCount < 5
            ORDER BY CreatedAt ASC LIMIT @Limit";
        cmd.Parameters.AddWithValue("@Limit", limit);
        var list = new List<ExcelSyncQueueItem>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ExcelSyncQueueItem
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                EntityType = (SyncEntityType)reader.GetInt32(reader.GetOrdinal("EntityType")),
                EntityId = reader.IsDBNull(reader.GetOrdinal("EntityId")) ? null : reader.GetInt64(reader.GetOrdinal("EntityId")),
                Status = (SyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
                CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
                ProcessedAt = reader.IsDBNull(reader.GetOrdinal("ProcessedAt")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("ProcessedAt")))
            });
        }
        return list;
    }

    public async Task MarkCompletedAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ExcelSyncQueue SET Status = @S, ProcessedAt = @P WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@S", (int)SyncStatus.Completed);
        cmd.Parameters.AddWithValue("@P", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MarkFailedAsync(long id, string errorMessage)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE ExcelSyncQueue 
            SET Status = @S, ErrorMessage = @Err, RetryCount = RetryCount + 1, ProcessedAt = @P 
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@S", (int)SyncStatus.Failed);
        cmd.Parameters.AddWithValue("@Err", errorMessage);
        cmd.Parameters.AddWithValue("@P", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> GetPendingCountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ExcelSyncQueue WHERE Status IN (0, 3) AND RetryCount < 5";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<DateTime?> GetLastSuccessfulSyncTimeAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(ProcessedAt) FROM ExcelSyncQueue WHERE Status = @S";
        cmd.Parameters.AddWithValue("@S", (int)SyncStatus.Completed);
        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value) return null;
        return DateTime.Parse(result.ToString()!);
    }
}
