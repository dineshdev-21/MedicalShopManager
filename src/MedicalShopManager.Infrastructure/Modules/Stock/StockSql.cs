using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Modules.Stock;

/// <summary>Statements shared by the Stock repositories, bound to the caller's connection and (optional) transaction.</summary>
internal static class StockSql
{
    /// <summary>StockMovements.MovementDate format used across the app.</summary>
    public const string MovementDateFormat = "yyyy-MM-dd HH:mm:ss";

    public static async Task<bool> BatchExistsAsync(SqliteConnection conn, SqliteTransaction? tx, long medicineId, string batchNumber)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM MedicineBatches WHERE MedicineId = @MedicineId AND TRIM(BatchNumber) = @BatchNumber COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@MedicineId", medicineId);
        cmd.Parameters.AddWithValue("@BatchNumber", batchNumber.Trim());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    public static async Task<string?> MedicineNameAsync(SqliteConnection conn, SqliteTransaction tx, long medicineId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Name FROM Medicines WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", medicineId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public static async Task<long> InsertMovementAsync(SqliteConnection conn, SqliteTransaction tx, StockMovement m)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO StockMovements (MovementDate, MedicineId, MedicineName, BatchId, BatchNumber,
                MovementType, QuantityIn, QuantityOut, BalanceAfter, Reference, Reason, CreatedByUserId, CreatedAt)
            VALUES (@MovementDate, @MedicineId, @MedicineName, @BatchId, @BatchNumber,
                @MovementType, @QuantityIn, @QuantityOut, @BalanceAfter, @Reference, @Reason, @CreatedByUserId, @CreatedAt);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@MovementDate", m.MovementDate.ToString(MovementDateFormat));
        cmd.Parameters.AddWithValue("@MedicineId", m.MedicineId);
        cmd.Parameters.AddWithValue("@MedicineName", m.MedicineName);
        cmd.Parameters.AddWithValue("@BatchId", m.BatchId);
        cmd.Parameters.AddWithValue("@BatchNumber", m.BatchNumber);
        cmd.Parameters.AddWithValue("@MovementType", (int)m.MovementType);
        cmd.Parameters.AddWithValue("@QuantityIn", m.QuantityIn);
        cmd.Parameters.AddWithValue("@QuantityOut", m.QuantityOut);
        cmd.Parameters.AddWithValue("@BalanceAfter", m.BalanceAfter);
        cmd.Parameters.AddWithValue("@Reference", (object?)m.Reference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Reason", (object?)m.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedByUserId", m.CreatedByUserId.HasValue ? m.CreatedByUserId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("o"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
