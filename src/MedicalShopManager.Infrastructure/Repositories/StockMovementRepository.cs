using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class StockMovementRepository : IStockMovementRepository
{
    private readonly Database.DatabaseManager _db;
    public StockMovementRepository(Database.DatabaseManager db) => _db = db;

    public async Task<StockMovement?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM StockMovements WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapMovement(reader) : null;
    }

    public async Task<IReadOnlyList<StockMovement>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM StockMovements ORDER BY MovementDate DESC LIMIT 1000";
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<StockMovement>> GetByMedicineAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM StockMovements WHERE MedicineId = @MedicineId ORDER BY MovementDate DESC";
        cmd.Parameters.AddWithValue("@MedicineId", medicineId);
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<StockMovement>> GetByBatchAsync(long batchId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM StockMovements WHERE BatchId = @BatchId ORDER BY MovementDate DESC";
        cmd.Parameters.AddWithValue("@BatchId", batchId);
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<StockMovement>> GetByDateRangeAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM StockMovements WHERE MovementDate BETWEEN @From AND @To ORDER BY MovementDate DESC";
        cmd.Parameters.AddWithValue("@From", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@To", to.ToString("yyyy-MM-dd 23:59:59"));
        return await ReadList(cmd);
    }

    public async Task<IReadOnlyList<StockMovement>> GetByTypeAsync(MovementType type)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM StockMovements WHERE MovementType = @Type ORDER BY MovementDate DESC LIMIT 500";
        cmd.Parameters.AddWithValue("@Type", (int)type);
        return await ReadList(cmd);
    }

    public async Task<long> InsertAsync(StockMovement entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO StockMovements (MovementDate, MedicineId, MedicineName, BatchId, BatchNumber,
                MovementType, QuantityIn, QuantityOut, BalanceAfter, Reference, Reason, CreatedByUserId, CreatedAt)
            VALUES (@MovementDate, @MedicineId, @MedicineName, @BatchId, @BatchNumber,
                @MovementType, @QuantityIn, @QuantityOut, @BalanceAfter, @Reference, @Reason, @CreatedByUserId, @CreatedAt);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@MovementDate", entity.MovementDate.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@MedicineId", entity.MedicineId);
        cmd.Parameters.AddWithValue("@MedicineName", entity.MedicineName);
        cmd.Parameters.AddWithValue("@BatchId", entity.BatchId);
        cmd.Parameters.AddWithValue("@BatchNumber", entity.BatchNumber);
        cmd.Parameters.AddWithValue("@MovementType", (int)entity.MovementType);
        cmd.Parameters.AddWithValue("@QuantityIn", entity.QuantityIn);
        cmd.Parameters.AddWithValue("@QuantityOut", entity.QuantityOut);
        cmd.Parameters.AddWithValue("@BalanceAfter", entity.BalanceAfter);
        cmd.Parameters.AddWithValue("@Reference", (object?)entity.Reference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Reason", (object?)entity.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedByUserId", entity.CreatedByUserId.HasValue ? entity.CreatedByUserId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", entity.CreatedAt.ToString("o"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public Task<bool> UpdateAsync(StockMovement entity) => throw new NotSupportedException("Stock movements are immutable.");
    public Task<bool> DeleteAsync(long id) => throw new NotSupportedException("Stock movements cannot be deleted.");

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM StockMovements";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<StockMovement>> ReadList(SqliteCommand cmd)
    {
        var list = new List<StockMovement>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapMovement(reader));
        return list;
    }

    private static StockMovement MapMovement(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        MovementDate = DateTime.Parse(r.GetString(r.GetOrdinal("MovementDate"))),
        MedicineId = r.GetInt64(r.GetOrdinal("MedicineId")),
        MedicineName = r.GetString(r.GetOrdinal("MedicineName")),
        BatchId = r.GetInt64(r.GetOrdinal("BatchId")),
        BatchNumber = r.GetString(r.GetOrdinal("BatchNumber")),
        MovementType = (MovementType)r.GetInt32(r.GetOrdinal("MovementType")),
        QuantityIn = r.GetInt32(r.GetOrdinal("QuantityIn")),
        QuantityOut = r.GetInt32(r.GetOrdinal("QuantityOut")),
        BalanceAfter = r.GetInt32(r.GetOrdinal("BalanceAfter")),
        Reference = r.IsDBNull(r.GetOrdinal("Reference")) ? null : r.GetString(r.GetOrdinal("Reference")),
        Reason = r.IsDBNull(r.GetOrdinal("Reason")) ? null : r.GetString(r.GetOrdinal("Reason")),
        CreatedByUserId = r.IsDBNull(r.GetOrdinal("CreatedByUserId")) ? null : r.GetInt64(r.GetOrdinal("CreatedByUserId")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt")))
    };
}
