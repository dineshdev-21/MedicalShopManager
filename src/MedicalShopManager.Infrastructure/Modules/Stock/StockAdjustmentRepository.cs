using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Stock;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Repositories;

namespace MedicalShopManager.Infrastructure.Modules.Stock;

/// <summary>
/// Stock postings (opening batches, numbered adjustments). Each posting runs in one transaction: the document
/// number, batch quantities, stock movements and the voucher commit together or not at all.
/// </summary>
public sealed class StockAdjustmentRepository : IStockAdjustmentRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly DatabaseManager _db;

    public StockAdjustmentRepository(DatabaseManager db) => _db = db;

    // ───────────────────────── Opening batch ─────────────────────────

    public async Task<long> InsertOpeningBatchAsync(MedicineBatch batch, long? userId)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();

        var medicineName = await StockSql.MedicineNameAsync(conn, tx, batch.MedicineId)
            ?? throw new StockValidationException("The item no longer exists.");
        if (await StockSql.BatchExistsAsync(conn, tx, batch.MedicineId, batch.BatchNumber))
            throw new StockValidationException(
                $"Batch {batch.BatchNumber} already exists for {medicineName}. Use a stock adjustment to change its quantity.");

        var now = DateTime.UtcNow.ToString("o");
        long batchId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO MedicineBatches (MedicineId, BatchNumber, ManufacturingDate, ExpiryDate, PurchaseDate, SupplierId,
                    QuantityReceived, QuantityRemaining, FreeUnits, UnitsPerPack, PurchaseRatePaisa, PtrPaisa, SaleRatePaisa,
                    MrpPaisa, DiscountPercent, GstRatePercent, IsActive, CreatedAt, UpdatedAt)
                VALUES (@MedicineId, @BatchNumber, NULL, @ExpiryDate, @PurchaseDate, @SupplierId,
                    @Qty, @Qty, @FreeUnits, @UnitsPerPack, @PurchaseRate, @Ptr, @SaleRate,
                    @Mrp, @Discount, @Gst, 1, @Now, @Now);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@MedicineId", batch.MedicineId);
            cmd.Parameters.AddWithValue("@BatchNumber", batch.BatchNumber);
            cmd.Parameters.AddWithValue("@ExpiryDate", batch.ExpiryDate.ToString(DateFormat));
            cmd.Parameters.AddWithValue("@PurchaseDate", batch.PurchaseDate.ToString(DateFormat));
            cmd.Parameters.AddWithValue("@SupplierId", batch.SupplierId.HasValue ? batch.SupplierId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@Qty", batch.QuantityRemaining);
            cmd.Parameters.AddWithValue("@FreeUnits", batch.FreeUnits);
            cmd.Parameters.AddWithValue("@UnitsPerPack", Math.Max(batch.UnitsPerPack, 1));
            cmd.Parameters.AddWithValue("@PurchaseRate", batch.PurchaseRatePaisa);
            cmd.Parameters.AddWithValue("@Ptr", batch.PtrPaisa);
            cmd.Parameters.AddWithValue("@SaleRate", batch.SaleRatePaisa);
            cmd.Parameters.AddWithValue("@Mrp", batch.MrpPaisa);
            cmd.Parameters.AddWithValue("@Discount", batch.DiscountPercent);
            cmd.Parameters.AddWithValue("@Gst", batch.GstRatePercent);
            cmd.Parameters.AddWithValue("@Now", now);
            batchId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        await StockSql.InsertMovementAsync(conn, tx, new StockMovement
        {
            MovementDate = batch.PurchaseDate.Date == DateTime.Today ? DateTime.Now : batch.PurchaseDate.Date,
            MedicineId = batch.MedicineId,
            MedicineName = medicineName,
            BatchId = batchId,
            BatchNumber = batch.BatchNumber,
            MovementType = MovementType.OpeningStock,
            QuantityIn = batch.QuantityRemaining,
            BalanceAfter = batch.QuantityRemaining,
            Reference = "Opening Stock",
            Reason = "New batch added from Stock",
            CreatedByUserId = userId
        });

        tx.Commit();
        return batchId;
    }

    // ───────────────────────── Adjustments ─────────────────────────

    public async Task<StockAdjustment> PostAdjustmentAsync(StockAdjustmentRequest request, long? userId, string? userName)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();

        var number = await DocumentSequenceAllocator.AllocateAsync(conn, tx, DocumentType.StockAdjustment, request.AdjustmentDate);
        var adjustment = new StockAdjustment
        {
            AdjustmentNumber = number,
            AdjustmentDate = request.AdjustmentDate.Date,
            Reason = request.Reason,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedByUserId = userId,
            CreatedByName = userName,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var requested in request.Lines)
        {
            var (batch, medicineName) = await ReadBatchAsync(conn, tx, requested.BatchId)
                ?? throw new StockValidationException("A batch in this adjustment no longer exists. Reload and try again.");

            var before = (long)batch.QuantityRemaining;
            if (StockRules.ValidateAdjustmentBalance($"{batch.BatchNumber} of {medicineName}", before, requested.QuantityChange) is { } error)
                throw new StockValidationException(error);
            var after = before + requested.QuantityChange;
            if (after > int.MaxValue) throw new StockValidationException($"Batch {batch.BatchNumber}: quantity is too large.");

            using (var update = conn.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE MedicineBatches SET QuantityRemaining = @Qty, UpdatedAt = @Now WHERE Id = @Id AND QuantityRemaining = @Before";
                update.Parameters.AddWithValue("@Qty", after);
                update.Parameters.AddWithValue("@Before", before);
                update.Parameters.AddWithValue("@Id", batch.Id);
                update.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o"));
                if (await update.ExecuteNonQueryAsync() != 1)
                    throw new StockValidationException($"Stock of batch {batch.BatchNumber} changed while saving. Reload and try again.");
            }

            var cost = StockRules.CostPaisa(batch);
            adjustment.Lines.Add(new StockAdjustmentLine
            {
                MedicineId = batch.MedicineId,
                MedicineName = medicineName,
                BatchId = batch.Id,
                BatchNumber = batch.BatchNumber,
                ExpiryDate = batch.ExpiryDate,
                QuantityChange = requested.QuantityChange,
                BalanceBefore = before,
                BalanceAfter = after,
                UnitsPerPack = Math.Max(batch.UnitsPerPack, 1),
                RatePaisa = cost,
                ValuePaisa = StockRules.AdjustmentValuePaisa(requested.QuantityChange, cost, batch.UnitsPerPack)
            });
        }
        adjustment.TotalValuePaisa = adjustment.Lines.Sum(l => l.ValuePaisa);

        using (var header = conn.CreateCommand())
        {
            header.Transaction = tx;
            header.CommandText = @"
                INSERT INTO StockAdjustments (AdjustmentNumber, AdjustmentDate, Reason, Notes, TotalValuePaisa, CreatedByUserId, CreatedByName, CreatedAt)
                VALUES (@Number, @Date, @Reason, @Notes, @Total, @UserId, @UserName, @CreatedAt);
                SELECT last_insert_rowid();";
            header.Parameters.AddWithValue("@Number", adjustment.AdjustmentNumber);
            header.Parameters.AddWithValue("@Date", adjustment.AdjustmentDate.ToString(DateFormat));
            header.Parameters.AddWithValue("@Reason", (int)adjustment.Reason);
            header.Parameters.AddWithValue("@Notes", (object?)adjustment.Notes ?? DBNull.Value);
            header.Parameters.AddWithValue("@Total", adjustment.TotalValuePaisa);
            header.Parameters.AddWithValue("@UserId", userId.HasValue ? userId.Value : DBNull.Value);
            header.Parameters.AddWithValue("@UserName", (object?)userName ?? DBNull.Value);
            header.Parameters.AddWithValue("@CreatedAt", adjustment.CreatedAt.ToString("o"));
            adjustment.Id = Convert.ToInt64(await header.ExecuteScalarAsync());
        }

        var movementDate = adjustment.AdjustmentDate == DateTime.Today ? DateTime.Now : adjustment.AdjustmentDate;
        var reasonText = adjustment.Reason.ToLabel() + (adjustment.Notes is { } notes ? $": {notes}" : string.Empty);
        foreach (var line in adjustment.Lines)
        {
            line.AdjustmentId = adjustment.Id;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO StockAdjustmentLines (AdjustmentId, MedicineId, MedicineName, BatchId, BatchNumber, ExpiryDate,
                        QuantityChange, BalanceBefore, BalanceAfter, UnitsPerPack, RatePaisa, ValuePaisa)
                    VALUES (@AdjustmentId, @MedicineId, @MedicineName, @BatchId, @BatchNumber, @Expiry,
                        @Change, @Before, @After, @Upp, @Rate, @Value);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@AdjustmentId", line.AdjustmentId);
                cmd.Parameters.AddWithValue("@MedicineId", line.MedicineId);
                cmd.Parameters.AddWithValue("@MedicineName", line.MedicineName);
                cmd.Parameters.AddWithValue("@BatchId", line.BatchId);
                cmd.Parameters.AddWithValue("@BatchNumber", line.BatchNumber);
                cmd.Parameters.AddWithValue("@Expiry", line.ExpiryDate.ToString(DateFormat));
                cmd.Parameters.AddWithValue("@Change", line.QuantityChange);
                cmd.Parameters.AddWithValue("@Before", line.BalanceBefore);
                cmd.Parameters.AddWithValue("@After", line.BalanceAfter);
                cmd.Parameters.AddWithValue("@Upp", line.UnitsPerPack);
                cmd.Parameters.AddWithValue("@Rate", line.RatePaisa);
                cmd.Parameters.AddWithValue("@Value", line.ValuePaisa);
                line.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }

            await StockSql.InsertMovementAsync(conn, tx, new StockMovement
            {
                MovementDate = movementDate,
                MedicineId = line.MedicineId,
                MedicineName = line.MedicineName,
                BatchId = line.BatchId,
                BatchNumber = line.BatchNumber,
                MovementType = adjustment.Reason.ToMovementType(),
                QuantityIn = (int)Math.Max(line.QuantityChange, 0),
                QuantityOut = (int)Math.Max(-line.QuantityChange, 0),
                BalanceAfter = (int)line.BalanceAfter,
                Reference = adjustment.AdjustmentNumber,
                Reason = reasonText,
                CreatedByUserId = userId
            });
        }

        tx.Commit();
        return adjustment;
    }

    private static async Task<(MedicineBatch Batch, string MedicineName)?> ReadBatchAsync(SqliteConnection conn, SqliteTransaction tx, long batchId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT b.*, m.Name AS MedicineName FROM MedicineBatches b JOIN Medicines m ON m.Id = b.MedicineId WHERE b.Id = @Id";
        cmd.Parameters.AddWithValue("@Id", batchId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (BatchRepository.MapBatch(reader), reader.GetString(reader.GetOrdinal("MedicineName")));
    }

    public async Task<IReadOnlyList<StockAdjustment>> GetAdjustmentsAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT a.*, (SELECT COUNT(*) FROM StockAdjustmentLines l WHERE l.AdjustmentId = a.Id) AS LineCount
            FROM StockAdjustments a
            WHERE a.AdjustmentDate BETWEEN @From AND @To
            ORDER BY a.AdjustmentDate DESC, a.Id DESC";
        cmd.Parameters.AddWithValue("@From", from.ToString(DateFormat));
        cmd.Parameters.AddWithValue("@To", to.ToString(DateFormat));
        var list = new List<StockAdjustment>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapHeader(reader));
        return list;
    }

    public async Task<StockAdjustment?> GetAdjustmentAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        StockAdjustment adjustment;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT a.*, 0 AS LineCount FROM StockAdjustments a WHERE a.Id = @Id";
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            adjustment = MapHeader(reader);
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM StockAdjustmentLines WHERE AdjustmentId = @Id ORDER BY Id";
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                adjustment.Lines.Add(new StockAdjustmentLine
                {
                    Id = r.GetInt64(r.GetOrdinal("Id")),
                    AdjustmentId = id,
                    MedicineId = r.GetInt64(r.GetOrdinal("MedicineId")),
                    MedicineName = r.GetString(r.GetOrdinal("MedicineName")),
                    BatchId = r.GetInt64(r.GetOrdinal("BatchId")),
                    BatchNumber = r.GetString(r.GetOrdinal("BatchNumber")),
                    ExpiryDate = DateTime.Parse(r.GetString(r.GetOrdinal("ExpiryDate"))),
                    QuantityChange = r.GetInt64(r.GetOrdinal("QuantityChange")),
                    BalanceBefore = r.GetInt64(r.GetOrdinal("BalanceBefore")),
                    BalanceAfter = r.GetInt64(r.GetOrdinal("BalanceAfter")),
                    UnitsPerPack = Math.Max(r.GetInt32(r.GetOrdinal("UnitsPerPack")), 1),
                    RatePaisa = r.GetInt64(r.GetOrdinal("RatePaisa")),
                    ValuePaisa = r.GetInt64(r.GetOrdinal("ValuePaisa"))
                });
            }
        }
        return adjustment;
    }

    private static StockAdjustment MapHeader(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        AdjustmentNumber = r.GetString(r.GetOrdinal("AdjustmentNumber")),
        AdjustmentDate = DateTime.Parse(r.GetString(r.GetOrdinal("AdjustmentDate"))),
        Reason = (StockAdjustmentReason)r.GetInt32(r.GetOrdinal("Reason")),
        Notes = r.IsDBNull(r.GetOrdinal("Notes")) ? null : r.GetString(r.GetOrdinal("Notes")),
        TotalValuePaisa = r.GetInt64(r.GetOrdinal("TotalValuePaisa")),
        CreatedByUserId = r.IsDBNull(r.GetOrdinal("CreatedByUserId")) ? null : r.GetInt64(r.GetOrdinal("CreatedByUserId")),
        CreatedByName = r.IsDBNull(r.GetOrdinal("CreatedByName")) ? null : r.GetString(r.GetOrdinal("CreatedByName")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt"))),
        LineCountHint = r.GetInt32(r.GetOrdinal("LineCount"))
    };
}
