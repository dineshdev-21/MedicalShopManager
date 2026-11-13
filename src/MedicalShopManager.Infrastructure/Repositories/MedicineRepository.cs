using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Repositories;

public class MedicineRepository : IMedicineRepository
{
    private readonly Database.DatabaseManager _db;
    private readonly ILogger<MedicineRepository> _logger;

    public MedicineRepository(Database.DatabaseManager db, ILogger<MedicineRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Medicine?> GetByIdAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Medicines WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);

        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapMedicine(reader) : null;
    }

    public async Task<IReadOnlyList<Medicine>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Medicines ORDER BY Name";

        var list = new List<Medicine>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(MapMedicine(reader));
        return list;
    }

    public async Task<IReadOnlyList<Medicine>> SearchAsync(string query)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM Medicines 
            WHERE Name LIKE @Q OR Composition LIKE @Q OR Manufacturer LIKE @Q OR Brand LIKE @Q
                  OR Barcode LIKE @Q OR Sku LIKE @Q OR Category LIKE @Q
            ORDER BY Name
            LIMIT 100";
        cmd.Parameters.AddWithValue("@Q", $"%{query}%");

        var list = new List<Medicine>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(MapMedicine(reader));
        return list;
    }

    public async Task<Medicine?> GetByBarcodeAsync(string barcode)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Medicines WHERE Barcode = @Barcode";
        cmd.Parameters.AddWithValue("@Barcode", barcode);

        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapMedicine(reader) : null;
    }

    public async Task<IReadOnlyList<Medicine>> GetLowStockAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT m.*, COALESCE(SUM(
                CASE WHEN b.ExpiryDate > date('now') AND b.QuantityRemaining > 0 
                THEN b.QuantityRemaining ELSE 0 END
            ), 0) AS TotalStock
            FROM Medicines m
            LEFT JOIN MedicineBatches b ON m.Id = b.MedicineId
            WHERE m.Status = 0
            GROUP BY m.Id
            HAVING TotalStock <= m.MinStock AND TotalStock > 0
            ORDER BY TotalStock ASC";

        var list = new List<Medicine>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(MapMedicine(reader));
        return list;
    }

    public async Task<IReadOnlyList<Medicine>> GetByStatusAsync(MedicineStatus status)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Medicines WHERE Status = @Status ORDER BY Name";
        cmd.Parameters.AddWithValue("@Status", (int)status);

        var list = new List<Medicine>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(MapMedicine(reader));
        return list;
    }

    public async Task<IReadOnlyList<Medicine>> GetByCategoryAsync(string category)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Medicines WHERE Category = @Category ORDER BY Name";
        cmd.Parameters.AddWithValue("@Category", category);

        var list = new List<Medicine>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(MapMedicine(reader));
        return list;
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Category FROM Medicines WHERE Category IS NOT NULL ORDER BY Category";

        var list = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(reader.GetString(0));
        return list;
    }

    public async Task<Medicine?> GetWithBatchesAsync(long id)
    {
        var medicine = await GetByIdAsync(id);
        if (medicine == null) return null;

        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM MedicineBatches WHERE MedicineId = @MedicineId ORDER BY ExpiryDate";
        cmd.Parameters.AddWithValue("@MedicineId", id);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            medicine.Batches.Add(BatchRepository.MapBatch(reader));

        return medicine;
    }

    public async Task<long> InsertAsync(Medicine entity)
    {
        using var conn = _db.CreateNewConnection();
        using var transaction = conn.BeginTransaction();
        entity.CompositionId = await ResolveCompositionIdAsync(conn, transaction, entity.Composition);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $@"
            INSERT INTO Medicines ({string.Join(", ", WritableColumns)}, CreatedAt, UpdatedAt)
            VALUES ({string.Join(", ", WritableColumns.Select(c => "@" + c))}, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();";

        AddMedicineParameters(cmd, entity);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        transaction.Commit();
        return id;
    }

    public async Task<bool> UpdateAsync(Medicine entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateNewConnection();
        using var transaction = conn.BeginTransaction();
        entity.CompositionId = await ResolveCompositionIdAsync(conn, transaction, entity.Composition);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $@"
            UPDATE Medicines SET {string.Join(", ", WritableColumns.Select(c => $"{c} = @{c}"))}, UpdatedAt = @UpdatedAt
            WHERE Id = @Id";

        cmd.Parameters.AddWithValue("@Id", entity.Id);
        AddMedicineParameters(cmd, entity);
        var updated = await cmd.ExecuteNonQueryAsync() > 0;
        transaction.Commit();
        return updated;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Medicines SET Status = @Status, UpdatedAt = @UpdatedAt WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Status", (int)MedicineStatus.Inactive);
        cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> CountAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Medicines WHERE Status = 0";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>Columns written by insert and update (CreatedAt/UpdatedAt handled separately).</summary>
    private static readonly string[] WritableColumns =
    {
        "Name", "Composition", "CompositionId", "Manufacturer", "Brand", "Category", "DosageForm", "Strength",
        "PackLabel", "UnitsPerPack", "AllowLooseSale", "Barcode", "Sku", "HsnCode", "GstRatePercent", "Schedule",
        "MinStock", "MaxStock", "MaxDiscountPercent", "Unit", "RackLocation", "DefaultMrpPaisa",
        "DefaultSellingPricePaisa", "DefaultSupplierId", "Status", "Notes"
    };

    /// <summary>
    /// Keeps Medicines.CompositionId in step with the composition text: finds the Compositions row by name
    /// (case-insensitive) or creates it. Null/blank text means no composition.
    /// </summary>
    private static async Task<long?> ResolveCompositionIdAsync(SqliteConnection conn, SqliteTransaction transaction, string? composition)
    {
        var name = composition?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT OR IGNORE INTO Compositions (Name) VALUES (@Name);
            SELECT Id FROM Compositions WHERE Name = @Name COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("@Name", name);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static object DbText(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static void AddMedicineParameters(SqliteCommand cmd, Medicine m)
    {
        cmd.Parameters.AddWithValue("@Name", m.Name);
        cmd.Parameters.AddWithValue("@Composition", DbText(m.Composition));
        cmd.Parameters.AddWithValue("@CompositionId", m.CompositionId.HasValue ? m.CompositionId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@Manufacturer", DbText(m.Manufacturer));
        cmd.Parameters.AddWithValue("@Brand", DbText(m.Brand));
        cmd.Parameters.AddWithValue("@Category", DbText(m.Category));
        cmd.Parameters.AddWithValue("@DosageForm", (int)m.DosageForm);
        cmd.Parameters.AddWithValue("@Strength", DbText(m.Strength));
        cmd.Parameters.AddWithValue("@PackLabel", DbText(m.PackLabel));
        cmd.Parameters.AddWithValue("@UnitsPerPack", m.UnitsPerPack);
        cmd.Parameters.AddWithValue("@AllowLooseSale", m.AllowLooseSale ? 1 : 0);
        cmd.Parameters.AddWithValue("@Barcode", DbText(m.Barcode));
        cmd.Parameters.AddWithValue("@Sku", DbText(m.Sku));
        cmd.Parameters.AddWithValue("@HsnCode", DbText(m.HsnCode));
        cmd.Parameters.AddWithValue("@GstRatePercent", m.GstRatePercent);
        cmd.Parameters.AddWithValue("@Schedule", (int)m.Schedule);
        cmd.Parameters.AddWithValue("@MinStock", m.MinStock);
        cmd.Parameters.AddWithValue("@MaxStock", m.MaxStock);
        cmd.Parameters.AddWithValue("@MaxDiscountPercent", m.MaxDiscountPercent.HasValue ? m.MaxDiscountPercent.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@Unit", DbText(m.Unit));
        cmd.Parameters.AddWithValue("@RackLocation", DbText(m.RackLocation));
        cmd.Parameters.AddWithValue("@DefaultMrpPaisa", m.DefaultMrpPaisa);
        cmd.Parameters.AddWithValue("@DefaultSellingPricePaisa", m.DefaultSellingPricePaisa);
        cmd.Parameters.AddWithValue("@DefaultSupplierId", m.DefaultSupplierId.HasValue ? m.DefaultSupplierId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", (int)m.Status);
        cmd.Parameters.AddWithValue("@Notes", DbText(m.Notes));
        cmd.Parameters.AddWithValue("@CreatedAt", m.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@UpdatedAt", m.UpdatedAt.ToString("o"));
    }

    internal static Medicine MapMedicine(SqliteDataReader reader)
    {
        string? Text(string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

        return new Medicine
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Composition = Text("Composition"),
            CompositionId = reader.IsDBNull(reader.GetOrdinal("CompositionId")) ? null : reader.GetInt64(reader.GetOrdinal("CompositionId")),
            Manufacturer = Text("Manufacturer"),
            Brand = Text("Brand"),
            Category = Text("Category"),
            DosageForm = (DosageForm)reader.GetInt32(reader.GetOrdinal("DosageForm")),
            Strength = Text("Strength"),
            PackLabel = Text("PackLabel"),
            UnitsPerPack = Math.Max(reader.GetInt32(reader.GetOrdinal("UnitsPerPack")), 1),
            AllowLooseSale = reader.GetInt32(reader.GetOrdinal("AllowLooseSale")) == 1,
            Barcode = Text("Barcode"),
            Sku = Text("Sku"),
            HsnCode = Text("HsnCode"),
            GstRatePercent = reader.GetDecimal(reader.GetOrdinal("GstRatePercent")),
            Schedule = (DrugSchedule)reader.GetInt32(reader.GetOrdinal("Schedule")),
            MinStock = reader.GetInt32(reader.GetOrdinal("MinStock")),
            MaxStock = reader.GetInt32(reader.GetOrdinal("MaxStock")),
            MaxDiscountPercent = reader.IsDBNull(reader.GetOrdinal("MaxDiscountPercent")) ? null : reader.GetDecimal(reader.GetOrdinal("MaxDiscountPercent")),
            Unit = Text("Unit"),
            RackLocation = Text("RackLocation"),
            DefaultMrpPaisa = reader.GetInt64(reader.GetOrdinal("DefaultMrpPaisa")),
            DefaultSellingPricePaisa = reader.GetInt64(reader.GetOrdinal("DefaultSellingPricePaisa")),
            DefaultSupplierId = reader.IsDBNull(reader.GetOrdinal("DefaultSupplierId")) ? null : reader.GetInt64(reader.GetOrdinal("DefaultSupplierId")),
            Status = (MedicineStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            Notes = Text("Notes"),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")))
        };
    }
}
