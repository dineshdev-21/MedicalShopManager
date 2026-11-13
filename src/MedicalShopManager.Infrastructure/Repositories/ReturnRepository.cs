using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class ReturnRepository : IReturnRepository
{
    private readonly Database.DatabaseManager _db;
    public ReturnRepository(Database.DatabaseManager db) => _db = db;

    public async Task<Return?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Returns WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapReturn(reader) : null;
    }

    public async Task<IReadOnlyList<Return>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Returns ORDER BY ReturnDate DESC LIMIT 500";
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<Return>> GetByTypeAsync(ReturnType type)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Returns WHERE ReturnType = @Type ORDER BY ReturnDate DESC";
        cmd.Parameters.AddWithValue("@Type", (int)type);
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<Return>> GetByDateRangeAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Returns WHERE ReturnDate BETWEEN @From AND @To ORDER BY ReturnDate DESC";
        cmd.Parameters.AddWithValue("@From", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@To", to.ToString("yyyy-MM-dd 23:59:59"));
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<Return>> GetByOriginalTransactionAsync(long transactionId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Returns WHERE OriginalTransactionId = @TxnId ORDER BY ReturnDate DESC";
        cmd.Parameters.AddWithValue("@TxnId", transactionId);
        return await ReadList(cmd);
    }

    public async Task<long> InsertAsync(Return entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Returns (OriginalTransactionId, OriginalReference, ReturnType, ReturnDate,
                MedicineId, MedicineName, BatchId, BatchNumber, Quantity, RefundAmountPaisa,
                Reason, ReturnedToStock, ProcessedByUserId, CreatedAt)
            VALUES (@OriginalTransactionId, @OriginalReference, @ReturnType, @ReturnDate,
                @MedicineId, @MedicineName, @BatchId, @BatchNumber, @Quantity, @RefundAmountPaisa,
                @Reason, @ReturnedToStock, @ProcessedByUserId, @CreatedAt);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@OriginalTransactionId", entity.OriginalTransactionId.HasValue ? entity.OriginalTransactionId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@OriginalReference", (object?)entity.OriginalReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ReturnType", (int)entity.ReturnType);
        cmd.Parameters.AddWithValue("@ReturnDate", entity.ReturnDate.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@MedicineId", entity.MedicineId);
        cmd.Parameters.AddWithValue("@MedicineName", entity.MedicineName);
        cmd.Parameters.AddWithValue("@BatchId", entity.BatchId.HasValue ? entity.BatchId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@BatchNumber", (object?)entity.BatchNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Quantity", entity.Quantity);
        cmd.Parameters.AddWithValue("@RefundAmountPaisa", entity.RefundAmountPaisa);
        cmd.Parameters.AddWithValue("@Reason", (object?)entity.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ReturnedToStock", entity.ReturnedToStock ? 1 : 0);
        cmd.Parameters.AddWithValue("@ProcessedByUserId", entity.ProcessedByUserId.HasValue ? entity.ProcessedByUserId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", entity.CreatedAt.ToString("o"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public Task<bool> UpdateAsync(Return entity) => throw new NotSupportedException("Returns are immutable.");
    public Task<bool> DeleteAsync(long id) => throw new NotSupportedException("Returns cannot be deleted.");

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Returns";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<Return>> ReadList(SqliteCommand cmd)
    {
        var list = new List<Return>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapReturn(reader));
        return list;
    }

    private static Return MapReturn(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        OriginalTransactionId = r.IsDBNull(r.GetOrdinal("OriginalTransactionId")) ? null : r.GetInt64(r.GetOrdinal("OriginalTransactionId")),
        OriginalReference = r.IsDBNull(r.GetOrdinal("OriginalReference")) ? null : r.GetString(r.GetOrdinal("OriginalReference")),
        ReturnType = (ReturnType)r.GetInt32(r.GetOrdinal("ReturnType")),
        ReturnDate = DateTime.Parse(r.GetString(r.GetOrdinal("ReturnDate"))),
        MedicineId = r.GetInt64(r.GetOrdinal("MedicineId")),
        MedicineName = r.GetString(r.GetOrdinal("MedicineName")),
        BatchId = r.IsDBNull(r.GetOrdinal("BatchId")) ? null : r.GetInt64(r.GetOrdinal("BatchId")),
        BatchNumber = r.IsDBNull(r.GetOrdinal("BatchNumber")) ? null : r.GetString(r.GetOrdinal("BatchNumber")),
        Quantity = r.GetInt32(r.GetOrdinal("Quantity")),
        RefundAmountPaisa = r.GetInt64(r.GetOrdinal("RefundAmountPaisa")),
        Reason = r.IsDBNull(r.GetOrdinal("Reason")) ? null : r.GetString(r.GetOrdinal("Reason")),
        ReturnedToStock = r.GetInt32(r.GetOrdinal("ReturnedToStock")) == 1,
        ProcessedByUserId = r.IsDBNull(r.GetOrdinal("ProcessedByUserId")) ? null : r.GetInt64(r.GetOrdinal("ProcessedByUserId")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt")))
    };
}
