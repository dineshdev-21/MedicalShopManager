using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Stock;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Repositories;

namespace MedicalShopManager.Infrastructure.Modules.Stock;

/// <summary>
/// Stock workspace queries, item settings and bulk updates over Medicines / MedicineBatches / purchases / sales.
/// Only data is written to the frozen Medicines table (no schema); every value is a bound parameter.
/// </summary>
public sealed class StockRepository : IStockRepository
{
    private readonly DatabaseManager _db;

    public StockRepository(DatabaseManager db) => _db = db;

    // ───────────────────────── Snapshots ─────────────────────────

    public async Task<IReadOnlyList<StockItemSnapshot>> GetSnapshotsAsync(bool includeInactive)
    {
        using var conn = _db.CreateNewConnection();
        var medicines = await ReadMedicinesAsync(conn,
            includeInactive ? "SELECT * FROM Medicines ORDER BY Name" : "SELECT * FROM Medicines WHERE Status = 0 ORDER BY Name");
        var batches = await ReadBatchesAsync(conn, "SELECT * FROM MedicineBatches ORDER BY MedicineId, ExpiryDate");
        return Join(medicines, batches);
    }

    public async Task<StockItemSnapshot?> GetSnapshotAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        var medicines = await ReadMedicinesAsync(conn, "SELECT * FROM Medicines WHERE Id = @Id", ("@Id", medicineId));
        if (medicines.Count == 0) return null;
        var batches = await ReadBatchesAsync(conn, "SELECT * FROM MedicineBatches WHERE MedicineId = @Id ORDER BY ExpiryDate", ("@Id", medicineId));
        return new StockItemSnapshot(medicines[0], batches);
    }

    public async Task<IReadOnlyList<StockItemSnapshot>> GetSubstituteSnapshotsAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        const string sameComposition = @"
            FROM Medicines s
            JOIN Medicines m ON m.Id = @Id AND m.CompositionId IS NOT NULL AND s.CompositionId = m.CompositionId
            WHERE s.Id <> @Id AND s.Status = 0";
        var medicines = await ReadMedicinesAsync(conn, "SELECT s.* " + sameComposition + " ORDER BY s.Name", ("@Id", medicineId));
        if (medicines.Count == 0) return Array.Empty<StockItemSnapshot>();
        var batches = await ReadBatchesAsync(conn,
            "SELECT b.* FROM MedicineBatches b WHERE b.MedicineId IN (SELECT s.Id " + sameComposition + ") ORDER BY b.ExpiryDate",
            ("@Id", medicineId));
        return Join(medicines, batches);
    }

    private static IReadOnlyList<StockItemSnapshot> Join(IReadOnlyList<Medicine> medicines, IReadOnlyList<MedicineBatch> batches)
    {
        var byMedicine = batches.ToLookup(b => b.MedicineId);
        return medicines.Select(m => new StockItemSnapshot(m, byMedicine[m.Id].ToList())).ToList();
    }

    public async Task<IReadOnlyList<string>> GetManufacturersAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT TRIM(Manufacturer) FROM Medicines
            WHERE Manufacturer IS NOT NULL AND TRIM(Manufacturer) <> ''
            ORDER BY TRIM(Manufacturer) COLLATE NOCASE";
        var list = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(reader.GetString(0));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ───────────────────────── Detail tabs ─────────────────────────

    public async Task<IReadOnlyList<ItemPurchaseRow>> GetPurchasesAsync(long medicineId, int limit)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT p.Id, p.PurchaseDate, p.SupplierInvoiceNumber, p.SupplierName, p.Status,
                   pi.BatchNumber, pi.ExpiryDate, pi.Quantity, pi.FreeQuantity, pi.PurchasePricePaisa,
                   pi.MrpPaisa, pi.GstRatePercent, pi.LineTotalPaisa,
                   COALESCE((SELECT b.UnitsPerPack FROM MedicineBatches b
                             WHERE b.MedicineId = pi.MedicineId AND b.BatchNumber = pi.BatchNumber ORDER BY b.Id LIMIT 1), 1)
            FROM PurchaseItems pi
            JOIN Purchases p ON p.Id = pi.PurchaseId
            WHERE pi.MedicineId = @Id
            ORDER BY p.PurchaseDate DESC, p.Id DESC
            LIMIT @Limit";
        cmd.Parameters.AddWithValue("@Id", medicineId);
        cmd.Parameters.AddWithValue("@Limit", limit);
        var list = new List<ItemPurchaseRow>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new ItemPurchaseRow
            {
                PurchaseId = r.GetInt64(0),
                PurchaseDate = DateTime.Parse(r.GetString(1)),
                InvoiceNumber = r.GetString(2),
                SupplierName = r.GetString(3),
                Status = r.GetInt32(4),
                BatchNumber = r.GetString(5),
                ExpiryDate = DateTime.Parse(r.GetString(6)),
                Quantity = r.GetInt64(7),
                FreeQuantity = r.GetInt64(8),
                RatePaisa = r.GetInt64(9),
                MrpPaisa = r.GetInt64(10),
                GstRatePercent = r.GetDecimal(11),
                LineTotalPaisa = r.GetInt64(12),
                UnitsPerPack = Math.Max(r.GetInt32(13), 1)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<ItemSaleRow>> GetSalesAsync(long medicineId, int limit)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.Id, s.InvoiceDate, s.InvoiceNumber, COALESCE(NULLIF(TRIM(s.CustomerName), ''), 'Cash / Counter Sale'),
                   si.BatchNumber, si.Quantity, si.UnitPricePaisa, si.DiscountPercent, si.LineTotalPaisa,
                   COALESCE(b.UnitsPerPack, 1)
            FROM SaleItems si
            JOIN Sales s ON s.Id = si.SaleId
            LEFT JOIN MedicineBatches b ON b.Id = si.BatchId
            WHERE si.MedicineId = @Id
            ORDER BY s.InvoiceDate DESC, s.Id DESC
            LIMIT @Limit";
        cmd.Parameters.AddWithValue("@Id", medicineId);
        cmd.Parameters.AddWithValue("@Limit", limit);
        var list = new List<ItemSaleRow>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new ItemSaleRow
            {
                SaleId = r.GetInt64(0),
                InvoiceDate = DateTime.Parse(r.GetString(1)),
                InvoiceNumber = r.GetString(2),
                CustomerName = r.GetString(3),
                BatchNumber = r.GetString(4),
                Quantity = r.GetInt64(5),
                UnitPricePaisa = r.GetInt64(6),
                DiscountPercent = r.GetDecimal(7),
                LineTotalPaisa = r.GetInt64(8),
                UnitsPerPack = Math.Max(r.GetInt32(9), 1)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<StockMovement>> GetMovementsAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM StockMovements WHERE MedicineId = @Id ORDER BY MovementDate, Id";
        cmd.Parameters.AddWithValue("@Id", medicineId);
        var list = new List<StockMovement>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new StockMovement
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
                CreatedByUserId = r.IsDBNull(r.GetOrdinal("CreatedByUserId")) ? null : r.GetInt64(r.GetOrdinal("CreatedByUserId"))
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<ItemDistributorRow>> GetDistributorsAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        // Confirmed purchases only; the last rate is the newest purchase line (by date, then id) of that supplier.
        cmd.CommandText = @"
            WITH lines AS (
                SELECT p.SupplierId, p.PurchaseDate, p.Id AS PurchaseId, pi.Id AS LineId,
                       pi.Quantity + pi.FreeQuantity AS Units, pi.PurchasePricePaisa
                FROM PurchaseItems pi
                JOIN Purchases p ON p.Id = pi.PurchaseId
                WHERE pi.MedicineId = @Id AND p.Status = @Confirmed
            )
            SELECT s.Id, s.Name, s.Phone,
                   COUNT(DISTINCT l.PurchaseId), SUM(l.Units), MAX(l.PurchaseDate),
                   (SELECT l2.PurchasePricePaisa FROM lines l2 WHERE l2.SupplierId = s.Id
                    ORDER BY l2.PurchaseDate DESC, l2.LineId DESC LIMIT 1)
            FROM lines l
            JOIN Suppliers s ON s.Id = l.SupplierId
            GROUP BY s.Id, s.Name, s.Phone
            ORDER BY MAX(l.PurchaseDate) DESC";
        cmd.Parameters.AddWithValue("@Id", medicineId);
        cmd.Parameters.AddWithValue("@Confirmed", (int)PurchaseStatus.Confirmed);
        var list = new List<ItemDistributorRow>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new ItemDistributorRow
            {
                SupplierId = r.GetInt64(0),
                Name = r.GetString(1),
                Phone = r.IsDBNull(2) ? null : r.GetString(2),
                PurchaseCount = r.GetInt32(3),
                TotalUnits = r.GetInt64(4),
                LastPurchaseDate = r.IsDBNull(5) ? null : DateTime.Parse(r.GetString(5)),
                LastRatePaisa = r.IsDBNull(6) ? 0 : r.GetInt64(6)
            });
        }
        return list;
    }

    // ───────────────────────── Settings ─────────────────────────

    public async Task<StockItemExtras?> GetExtrasAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MinDiscountPercent, IsDiscountLocked, IgnoreForShortage FROM StockItemSettings WHERE MedicineId = @Id";
        cmd.Parameters.AddWithValue("@Id", medicineId);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new StockItemExtras(medicineId, r.GetDecimal(0), r.GetInt32(1) == 1, r.GetInt32(2) == 1);
    }

    public async Task<bool> SaveSettingsAsync(StockItemSettings s)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        int updated;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE Medicines SET RackLocation = @Rack, MinStock = @Min, MaxStock = @Max,
                    MaxDiscountPercent = @MaxDisc, Schedule = @Schedule, AllowLooseSale = @Loose, UpdatedAt = @Now
                WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", s.MedicineId);
            cmd.Parameters.AddWithValue("@Rack", string.IsNullOrWhiteSpace(s.RackLocation) ? DBNull.Value : s.RackLocation.Trim());
            cmd.Parameters.AddWithValue("@Min", s.MinStock);
            cmd.Parameters.AddWithValue("@Max", s.MaxStock);
            cmd.Parameters.AddWithValue("@MaxDisc", s.MaxDiscountPercent.HasValue ? s.MaxDiscountPercent.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@Schedule", (int)s.Schedule);
            cmd.Parameters.AddWithValue("@Loose", s.AllowLooseSale ? 1 : 0);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o"));
            updated = await cmd.ExecuteNonQueryAsync();
        }
        if (updated == 0)
        {
            tx.Rollback();
            return false;
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO StockItemSettings (MedicineId, MinDiscountPercent, IsDiscountLocked, IgnoreForShortage, UpdatedAt)
                VALUES (@Id, @MinDisc, @Locked, @Ignore, @Now)
                ON CONFLICT(MedicineId) DO UPDATE SET MinDiscountPercent = excluded.MinDiscountPercent,
                    IsDiscountLocked = excluded.IsDiscountLocked, IgnoreForShortage = excluded.IgnoreForShortage,
                    UpdatedAt = excluded.UpdatedAt";
            cmd.Parameters.AddWithValue("@Id", s.MedicineId);
            cmd.Parameters.AddWithValue("@MinDisc", s.MinDiscountPercent);
            cmd.Parameters.AddWithValue("@Locked", s.IsDiscountLocked ? 1 : 0);
            cmd.Parameters.AddWithValue("@Ignore", s.IgnoreForShortage ? 1 : 0);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }
        tx.Commit();
        return true;
    }

    public async Task<int> BulkUpdateAsync(StockBulkUpdate u)
    {
        // Column names come from this fixed list only; values are parameters.
        var sets = new List<(string Column, object Value)>();
        if (u.GstRatePercent is { } gst) sets.Add(("GstRatePercent", gst));
        if (u.HsnCode != null) sets.Add(("HsnCode", string.IsNullOrWhiteSpace(u.HsnCode) ? DBNull.Value : u.HsnCode.Trim()));
        if (u.RackLocation != null) sets.Add(("RackLocation", string.IsNullOrWhiteSpace(u.RackLocation) ? DBNull.Value : u.RackLocation.Trim()));
        if (u.MinStock is { } min) sets.Add(("MinStock", min));
        if (u.MaxStock is { } max) sets.Add(("MaxStock", max));
        if (u.MaxDiscountPercent is { } disc) sets.Add(("MaxDiscountPercent", disc));
        if (u.Schedule is { } schedule) sets.Add(("Schedule", (int)schedule));
        if (u.Category != null) sets.Add(("Category", string.IsNullOrWhiteSpace(u.Category) ? DBNull.Value : u.Category.Trim()));
        if (sets.Count == 0 || u.MedicineIds.Count == 0) return 0;

        var ids = u.MedicineIds.Distinct().ToList();
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        var updated = 0;
        foreach (var chunk in ids.Chunk(500))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var idParams = chunk.Select((_, i) => $"@Id{i}").ToList();
            cmd.CommandText = $@"
                UPDATE Medicines SET {string.Join(", ", sets.Select((s, i) => $"{s.Column} = @V{i}"))}, UpdatedAt = @Now
                WHERE Id IN ({string.Join(", ", idParams)})";
            for (var i = 0; i < sets.Count; i++) cmd.Parameters.AddWithValue($"@V{i}", sets[i].Value);
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(idParams[i], chunk[i]);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o"));
            updated += await cmd.ExecuteNonQueryAsync();

            // A partial min/max change must not leave any item with max below min.
            using var check = conn.CreateCommand();
            check.Transaction = tx;
            check.CommandText = $@"
                SELECT Name FROM Medicines WHERE Id IN ({string.Join(", ", idParams)}) AND MaxStock > 0 AND MaxStock < MinStock LIMIT 1";
            for (var i = 0; i < chunk.Length; i++) check.Parameters.AddWithValue(idParams[i], chunk[i]);
            if (await check.ExecuteScalarAsync() is string bad)
            {
                tx.Rollback();
                throw new StockValidationException($"'{bad}' would have max stock below min stock. Nothing was updated.");
            }
        }
        tx.Commit();
        return updated;
    }

    public async Task<long?> FindMedicineAsync(string name, string? manufacturer)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id FROM Medicines
            WHERE TRIM(Name) = @Name COLLATE NOCASE AND COALESCE(TRIM(Manufacturer), '') = @Mfr COLLATE NOCASE
            ORDER BY Status, Id LIMIT 1";
        cmd.Parameters.AddWithValue("@Name", name.Trim());
        cmd.Parameters.AddWithValue("@Mfr", (manufacturer ?? string.Empty).Trim());
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public async Task<bool> BatchExistsAsync(long medicineId, string batchNumber)
    {
        using var conn = _db.CreateNewConnection();
        return await StockSql.BatchExistsAsync(conn, null, medicineId, batchNumber);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static async Task<IReadOnlyList<Medicine>> ReadMedicinesAsync(SqliteConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var list = new List<Medicine>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MedicineRepository.MapMedicine(reader));
        return list;
    }

    private static async Task<IReadOnlyList<MedicineBatch>> ReadBatchesAsync(SqliteConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var list = new List<MedicineBatch>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(BatchRepository.MapBatch(reader));
        return list;
    }
}
