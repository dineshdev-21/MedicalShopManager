using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class BatchRepository : IBatchRepository
{
    private readonly Database.DatabaseManager _db;
    private readonly ILogger<BatchRepository> _logger;

    public BatchRepository(Database.DatabaseManager db, ILogger<BatchRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MedicineBatch?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM MedicineBatches WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapBatch(reader) : null;
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM MedicineBatches ORDER BY ExpiryDate";
        var list = new List<MedicineBatch>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapBatch(reader));
        return list;
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetByMedicineIdAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM MedicineBatches WHERE MedicineId = @MedicineId ORDER BY ExpiryDate";
        cmd.Parameters.AddWithValue("@MedicineId", medicineId);
        var list = new List<MedicineBatch>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapBatch(reader));
        return list;
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetSellableBatchesAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM MedicineBatches 
            WHERE MedicineId = @MedicineId 
              AND QuantityRemaining > 0 
              AND ExpiryDate > date('now')
              AND IsActive = 1
            ORDER BY ExpiryDate ASC";
        cmd.Parameters.AddWithValue("@MedicineId", medicineId);
        var list = new List<MedicineBatch>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapBatch(reader));
        return list;
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetFefoOrderedBatchesAsync(long medicineId)
    {
        // FEFO = First Expiry, First Out — same as sellable batches ordered by expiry
        return await GetSellableBatchesAsync(medicineId);
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetNearExpiryAsync(int daysThreshold = 90)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM MedicineBatches 
            WHERE QuantityRemaining > 0 
              AND ExpiryDate > date('now') 
              AND ExpiryDate <= date('now', '+' || @Days || ' days')
              AND IsActive = 1
            ORDER BY ExpiryDate ASC";
        cmd.Parameters.AddWithValue("@Days", daysThreshold);
        var list = new List<MedicineBatch>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapBatch(reader));
        return list;
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetExpiredAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM MedicineBatches 
            WHERE QuantityRemaining > 0 
              AND ExpiryDate <= date('now')
            ORDER BY ExpiryDate ASC";
        var list = new List<MedicineBatch>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapBatch(reader));
        return list;
    }

    public async Task<MedicineBatch?> GetByBatchNumberAsync(long medicineId, string batchNumber)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM MedicineBatches WHERE MedicineId = @MedicineId AND BatchNumber = @BatchNumber";
        cmd.Parameters.AddWithValue("@MedicineId", medicineId);
        cmd.Parameters.AddWithValue("@BatchNumber", batchNumber);
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapBatch(reader) : null;
    }

    public async Task<bool> UpdateQuantityAsync(long batchId, int newQuantity)
    {
        if (newQuantity < 0)
            throw new InvalidOperationException($"Cannot set negative quantity ({newQuantity}) for batch {batchId}");

        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE MedicineBatches 
            SET QuantityRemaining = @Qty, UpdatedAt = @UpdatedAt 
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", batchId);
        cmd.Parameters.AddWithValue("@Qty", newQuantity);
        cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<long> InsertAsync(MedicineBatch entity)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO MedicineBatches (MedicineId, BatchNumber, ManufacturingDate, ExpiryDate, PurchaseDate,
                SupplierId, QuantityReceived, QuantityRemaining, FreeUnits, UnitsPerPack, PurchaseRatePaisa,
                PtrPaisa, SaleRatePaisa, MrpPaisa, DiscountPercent, GstRatePercent, IsActive,
                CreatedAt, UpdatedAt)
            VALUES (@MedicineId, @BatchNumber, @ManufacturingDate, @ExpiryDate, @PurchaseDate,
                @SupplierId, @QuantityReceived, @QuantityRemaining, @FreeUnits, @UnitsPerPack, @PurchaseRatePaisa,
                @PtrPaisa, @SaleRatePaisa, @MrpPaisa, @DiscountPercent, @GstRatePercent, @IsActive,
                @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();";
        AddBatchParameters(cmd, entity);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public async Task<bool> UpdateAsync(MedicineBatch entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE MedicineBatches SET 
                MedicineId = @MedicineId, BatchNumber = @BatchNumber, ManufacturingDate = @ManufacturingDate,
                ExpiryDate = @ExpiryDate, PurchaseDate = @PurchaseDate, SupplierId = @SupplierId,
                QuantityReceived = @QuantityReceived, QuantityRemaining = @QuantityRemaining, FreeUnits = @FreeUnits,
                UnitsPerPack = @UnitsPerPack, PurchaseRatePaisa = @PurchaseRatePaisa, PtrPaisa = @PtrPaisa,
                SaleRatePaisa = @SaleRatePaisa, MrpPaisa = @MrpPaisa,
                DiscountPercent = @DiscountPercent, GstRatePercent = @GstRatePercent,
                IsActive = @IsActive, UpdatedAt = @UpdatedAt
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", entity.Id);
        AddBatchParameters(cmd, entity);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE MedicineBatches SET IsActive = 0, UpdatedAt = @UpdatedAt WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM MedicineBatches WHERE IsActive = 1";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static void AddBatchParameters(SqliteCommand cmd, MedicineBatch b)
    {
        cmd.Parameters.AddWithValue("@MedicineId", b.MedicineId);
        cmd.Parameters.AddWithValue("@BatchNumber", b.BatchNumber);
        cmd.Parameters.AddWithValue("@ManufacturingDate", b.ManufacturingDate.HasValue ? b.ManufacturingDate.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        cmd.Parameters.AddWithValue("@ExpiryDate", b.ExpiryDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@PurchaseDate", b.PurchaseDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@SupplierId", b.SupplierId.HasValue ? b.SupplierId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@QuantityReceived", b.QuantityReceived);
        cmd.Parameters.AddWithValue("@QuantityRemaining", b.QuantityRemaining);
        cmd.Parameters.AddWithValue("@FreeUnits", b.FreeUnits);
        if (b.UnitsPerPack < 1)
            throw new ArgumentOutOfRangeException(nameof(b), $"Batch {b.BatchNumber}: units per pack must be at least 1.");
        cmd.Parameters.AddWithValue("@UnitsPerPack", b.UnitsPerPack);
        cmd.Parameters.AddWithValue("@PurchaseRatePaisa", b.PurchaseRatePaisa);
        cmd.Parameters.AddWithValue("@PtrPaisa", b.PtrPaisa);
        cmd.Parameters.AddWithValue("@SaleRatePaisa", b.SaleRatePaisa);
        cmd.Parameters.AddWithValue("@MrpPaisa", b.MrpPaisa);
        cmd.Parameters.AddWithValue("@DiscountPercent", b.DiscountPercent);
        cmd.Parameters.AddWithValue("@GstRatePercent", b.GstRatePercent);
        cmd.Parameters.AddWithValue("@IsActive", b.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@CreatedAt", b.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@UpdatedAt", b.UpdatedAt.ToString("o"));
    }

    internal static MedicineBatch MapBatch(SqliteDataReader reader)
    {
        return new MedicineBatch
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            MedicineId = reader.GetInt64(reader.GetOrdinal("MedicineId")),
            BatchNumber = reader.GetString(reader.GetOrdinal("BatchNumber")),
            ManufacturingDate = reader.IsDBNull(reader.GetOrdinal("ManufacturingDate")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("ManufacturingDate"))),
            ExpiryDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("ExpiryDate"))),
            PurchaseDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("PurchaseDate"))),
            SupplierId = reader.IsDBNull(reader.GetOrdinal("SupplierId")) ? null : reader.GetInt64(reader.GetOrdinal("SupplierId")),
            QuantityReceived = reader.GetInt32(reader.GetOrdinal("QuantityReceived")),
            QuantityRemaining = reader.GetInt32(reader.GetOrdinal("QuantityRemaining")),
            FreeUnits = reader.GetInt32(reader.GetOrdinal("FreeUnits")),
            UnitsPerPack = Math.Max(reader.GetInt32(reader.GetOrdinal("UnitsPerPack")), 1),
            PurchaseRatePaisa = reader.GetInt64(reader.GetOrdinal("PurchaseRatePaisa")),
            PtrPaisa = reader.GetInt64(reader.GetOrdinal("PtrPaisa")),
            SaleRatePaisa = reader.GetInt64(reader.GetOrdinal("SaleRatePaisa")),
            MrpPaisa = reader.GetInt64(reader.GetOrdinal("MrpPaisa")),
            DiscountPercent = reader.GetDecimal(reader.GetOrdinal("DiscountPercent")),
            GstRatePercent = reader.GetDecimal(reader.GetOrdinal("GstRatePercent")),
            IsActive = reader.GetInt32(reader.GetOrdinal("IsActive")) == 1,
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")))
        };
    }
}
