using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using static MedicalShopManager.Infrastructure.Modules.Purchases.PurchaseSql;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>
/// Stock writes for purchases and debit notes, always on the caller's transaction: batch create/merge, guarded
/// stock-out, and the StockMovements ledger row for each change.
/// </summary>
internal static class PurchaseStockLedger
{
    /// <summary>
    /// Adds a purchase line to stock. Merges into the existing batch with the same item, batch number (case-insensitive)
    /// and expiry month when its pack size matches (the rates' basis); otherwise creates a new batch.
    /// Returns the batch id and its new balance.
    /// </summary>
    public static async Task<(long BatchId, int Balance)> ReceiveAsync(SqliteConnection conn, SqliteTransaction tx,
        PurchaseBill bill, PurchaseBillLine line)
    {
        var units = line.Quantity + line.FreeQuantity;
        var now = Stamp(DateTime.UtcNow);
        var batchNumber = line.BatchNumber.Trim();

        long? existingId = null;
        using (var find = Command(conn, tx, @"
            SELECT Id FROM MedicineBatches
            WHERE MedicineId = @M AND BatchNumber = @B COLLATE NOCASE AND substr(ExpiryDate, 1, 7) = @ExpMonth AND UnitsPerPack = @Pack
            ORDER BY Id LIMIT 1",
            ("@M", line.MedicineId), ("@B", batchNumber), ("@ExpMonth", line.ExpiryDate.ToString("yyyy-MM")), ("@Pack", line.UnitsPerPack)))
        {
            var found = await find.ExecuteScalarAsync();
            if (found is not null and not DBNull) existingId = Convert.ToInt64(found);
        }

        if (existingId is long id)
        {
            var balance = await ScalarAsync(conn, tx, @"
                UPDATE MedicineBatches SET
                    QuantityReceived = QuantityReceived + @U, QuantityRemaining = QuantityRemaining + @U, FreeUnits = FreeUnits + @F,
                    PurchaseRatePaisa = @PR, PtrPaisa = @PTR, SaleRatePaisa = @SR, MrpPaisa = @MRP, GstRatePercent = @G,
                    SupplierId = @S, IsActive = 1, UpdatedAt = @Now
                WHERE Id = @Id
                RETURNING QuantityRemaining;",
                ("@U", units), ("@F", line.FreeQuantity), ("@PR", line.PurchaseRatePaisa), ("@PTR", line.PtrPaisa),
                ("@SR", line.SaleRatePaisa), ("@MRP", line.MrpPaisa), ("@G", line.GstRatePercent), ("@S", bill.SupplierId),
                ("@Now", now), ("@Id", id));
            return (id, Convert.ToInt32(balance));
        }

        var newId = await ScalarAsync(conn, tx, @"
            INSERT INTO MedicineBatches (MedicineId, BatchNumber, ManufacturingDate, ExpiryDate, PurchaseDate, SupplierId,
                QuantityReceived, QuantityRemaining, FreeUnits, UnitsPerPack, PurchaseRatePaisa, PtrPaisa, SaleRatePaisa, MrpPaisa,
                DiscountPercent, GstRatePercent, IsActive, CreatedAt, UpdatedAt)
            VALUES (@M, @B, @Mfg, @Exp, @PDate, @S, @U, @U, @F, @Pack, @PR, @PTR, @SR, @MRP, 0, @G, 1, @Now, @Now);
            SELECT last_insert_rowid();",
            ("@M", line.MedicineId), ("@B", batchNumber), ("@Mfg", DateOrNull(line.ManufacturingDate)), ("@Exp", Date(line.ExpiryDate)),
            ("@PDate", Date(bill.InvoiceDate)), ("@S", bill.SupplierId), ("@U", units), ("@F", line.FreeQuantity),
            ("@Pack", line.UnitsPerPack), ("@PR", line.PurchaseRatePaisa), ("@PTR", line.PtrPaisa), ("@SR", line.SaleRatePaisa),
            ("@MRP", line.MrpPaisa), ("@G", line.GstRatePercent), ("@Now", now));
        return (Convert.ToInt64(newId), units);
    }

    /// <summary>
    /// Takes <paramref name="units"/> out of a batch only if they are still in stock; otherwise throws a
    /// <see cref="PurchaseRuleException"/> naming the batch. <paramref name="receivedToo"/> also lowers the received
    /// and free counts (reversing a purchase rather than returning goods). Returns the new balance.
    /// </summary>
    public static async Task<int> TakeOutAsync(SqliteConnection conn, SqliteTransaction tx, long batchId, int units, int freeUnits,
        bool receivedToo, string itemName, string batchNumber, string whenShort)
    {
        var balance = await ScalarAsync(conn, tx, receivedToo
                ? @"UPDATE MedicineBatches SET QuantityRemaining = QuantityRemaining - @U,
                        QuantityReceived = MAX(QuantityReceived - @U, 0), FreeUnits = MAX(FreeUnits - @F, 0),
                        IsActive = CASE WHEN QuantityReceived - @U <= 0 THEN 0 ELSE IsActive END, UpdatedAt = @Now
                    WHERE Id = @Id AND QuantityRemaining >= @U RETURNING QuantityRemaining;"
                : @"UPDATE MedicineBatches SET QuantityRemaining = QuantityRemaining - @U, UpdatedAt = @Now
                    WHERE Id = @Id AND QuantityRemaining >= @U RETURNING QuantityRemaining;",
            ("@U", units), ("@F", freeUnits), ("@Now", Stamp(DateTime.UtcNow)), ("@Id", batchId));
        if (balance is null)
        {
            var onHand = await ScalarAsync(conn, tx, "SELECT QuantityRemaining FROM MedicineBatches WHERE Id = @Id", ("@Id", batchId));
            throw new PurchaseRuleException(
                $"{itemName} (Batch {batchNumber}): only {Convert.ToInt32(onHand ?? 0)} of {units} units are in stock. {whenShort}");
        }
        return Convert.ToInt32(balance);
    }

    /// <summary>Puts units back into a batch (cancelled debit note). Returns the new balance.</summary>
    public static async Task<int> PutBackAsync(SqliteConnection conn, SqliteTransaction tx, long batchId, int units)
    {
        var balance = await ScalarAsync(conn, tx, @"
            UPDATE MedicineBatches SET QuantityRemaining = QuantityRemaining + @U, IsActive = 1, UpdatedAt = @Now
            WHERE Id = @Id RETURNING QuantityRemaining;",
            ("@U", units), ("@Now", Stamp(DateTime.UtcNow)), ("@Id", batchId));
        if (balance is null) throw new PurchaseRuleException($"Batch #{batchId} no longer exists.");
        return Convert.ToInt32(balance);
    }

    /// <summary>One StockMovements row (signed: positive = in, negative = out).</summary>
    public static async Task InsertMovementAsync(SqliteConnection conn, SqliteTransaction tx, DateTime date, long medicineId,
        string medicineName, long batchId, string batchNumber, MovementType type, int signedUnits, int balanceAfter,
        string reference, string reason, long? userId)
    {
        await ExecAsync(conn, tx, @"
            INSERT INTO StockMovements (MovementDate, MedicineId, MedicineName, BatchId, BatchNumber, MovementType,
                QuantityIn, QuantityOut, BalanceAfter, Reference, Reason, CreatedByUserId, CreatedAt)
            VALUES (@Date, @M, @Name, @B, @BNo, @Type, @In, @Out, @Bal, @Ref, @Reason, @User, @Now);",
            ("@Date", date.ToString(DateTimeFormat)), ("@M", medicineId), ("@Name", medicineName), ("@B", batchId),
            ("@BNo", batchNumber), ("@Type", (int)type), ("@In", Math.Max(signedUnits, 0)), ("@Out", Math.Max(-signedUnits, 0)),
            ("@Bal", balanceAfter), ("@Ref", reference), ("@Reason", reason), ("@User", OrNull(userId)),
            ("@Now", Stamp(DateTime.UtcNow)));
    }
}
