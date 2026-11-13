using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Numbering;
using static MedicalShopManager.Infrastructure.Modules.Purchases.PurchaseSql;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>
/// Debit notes (PurchaseReturns / PurchaseReturnItems). Posting allocates the DebitNote number, writes the document
/// and takes the stock out in one transaction; cancelling puts the stock back.
/// </summary>
public sealed class PurchaseReturnRepository : IPurchaseReturnRepository
{
    private readonly DatabaseManager _db;

    public PurchaseReturnRepository(DatabaseManager db) => _db = db;

    private const string ReturnedUnitsSql = @"
        COALESCE((SELECT SUM(ri.Quantity) FROM PurchaseReturnItems ri JOIN PurchaseReturns pr ON pr.Id = ri.PurchaseReturnId
                  WHERE ri.PurchaseItemId = i.Id AND pr.IsCancelled = 0), 0)";

    public async Task<IReadOnlyList<ReturnableBatch>> GetReturnableBatchesAsync(long supplierId, long? purchaseId = null, string? search = null)
    {
        var filters = new List<string>();
        var parameters = new List<(string, object?)> { ("@S", supplierId) };
        if (purchaseId is long pid)
        {
            filters.Add("p.Id = @P");
            parameters.Add(("@P", pid));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            filters.Add(@"(m.Name LIKE @Q ESCAPE '\' OR b.BatchNumber LIKE @Q ESCAPE '\')");
            parameters.Add(("@Q", Like(search)));
        }
        var extra = filters.Count > 0 ? " AND " + string.Join(" AND ", filters) : string.Empty;

        var list = new List<ReturnableBatch>();
        using var conn = _db.CreateNewConnection();

        // Lines of the distributor's confirmed purchases that still have stock.
        using (var cmd = Command(conn, null, $@"
            SELECT i.Id AS PurchaseItemId, p.Id AS PurchaseId, COALESCE(p.PurchaseNumber, p.SupplierInvoiceNumber) AS PurchaseReference,
                p.PurchaseDate, p.SupplierId, p.SupplierName, b.Id AS BatchId, b.MedicineId, m.Name AS MedicineName,
                COALESCE(i.HsnCode, m.HsnCode) AS HsnCode, b.BatchNumber, b.ExpiryDate, b.UnitsPerPack, b.QuantityRemaining, b.MrpPaisa,
                i.GstRatePercent, COALESCE(NULLIF(i.NetRatePaisa, 0), i.PurchasePricePaisa) AS RatePaisa,
                i.Quantity + i.FreeQuantity AS PurchasedUnits, {ReturnedUnitsSql} AS ReturnedUnits
            FROM PurchaseItems i
            JOIN Purchases p ON p.Id = i.PurchaseId
            JOIN MedicineBatches b ON b.Id = i.BatchId
            JOIN Medicines m ON m.Id = b.MedicineId
            WHERE p.SupplierId = @S AND p.Status = {(int)PurchaseStatus.Confirmed} AND b.QuantityRemaining > 0 {extra}
            ORDER BY p.PurchaseDate DESC, p.Id DESC, i.LineNo", parameters.ToArray()))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) list.Add(MapReturnable(r, linked: true));
        }

        // Stock received from the distributor outside a purchase bill (opening stock, pre-6.2 batches).
        if (purchaseId is null)
        {
            using var cmd = Command(conn, null, $@"
                SELECT NULL AS PurchaseItemId, NULL AS PurchaseId, NULL AS PurchaseReference, b.PurchaseDate, b.SupplierId,
                    s.Name AS SupplierName, b.Id AS BatchId, b.MedicineId, m.Name AS MedicineName, m.HsnCode, b.BatchNumber,
                    b.ExpiryDate, b.UnitsPerPack, b.QuantityRemaining, b.MrpPaisa, b.GstRatePercent, b.PurchaseRatePaisa AS RatePaisa,
                    0 AS PurchasedUnits, 0 AS ReturnedUnits
                FROM MedicineBatches b
                JOIN Medicines m ON m.Id = b.MedicineId
                LEFT JOIN Suppliers s ON s.Id = b.SupplierId
                WHERE b.SupplierId = @S AND b.QuantityRemaining > 0
                  AND NOT EXISTS (SELECT 1 FROM PurchaseItems i WHERE i.BatchId = b.Id) {extra}
                ORDER BY m.Name, b.ExpiryDate", parameters.ToArray());
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(MapReturnable(r, linked: false));
        }

        return list.Where(b => b.MaxReturnableUnits > 0).ToList();
    }

    public async Task<IReadOnlyList<ReturnableBatch>> GetExpiryCandidatesAsync(DateTime expiringBy, long? supplierId = null)
    {
        var parameters = new List<(string, object?)> { ("@By", Date(expiringBy)) };
        var supplierFilter = string.Empty;
        if (supplierId is long s)
        {
            supplierFilter = " AND b.SupplierId = @S";
            parameters.Add(("@S", s));
        }

        using var conn = _db.CreateNewConnection();
        using var cmd = Command(conn, null, $@"
            SELECT NULL AS PurchaseItemId,
                (SELECT i.PurchaseId FROM PurchaseItems i WHERE i.BatchId = b.Id ORDER BY i.Id DESC LIMIT 1) AS PurchaseId,
                (SELECT COALESCE(p.PurchaseNumber, p.SupplierInvoiceNumber) FROM PurchaseItems i JOIN Purchases p ON p.Id = i.PurchaseId
                 WHERE i.BatchId = b.Id ORDER BY i.Id DESC LIMIT 1) AS PurchaseReference,
                b.PurchaseDate, b.SupplierId, s.Name AS SupplierName, b.Id AS BatchId, b.MedicineId, m.Name AS MedicineName, m.HsnCode,
                b.BatchNumber, b.ExpiryDate, b.UnitsPerPack, b.QuantityRemaining, b.MrpPaisa, b.GstRatePercent,
                COALESCE((SELECT NULLIF(i.NetRatePaisa, 0) FROM PurchaseItems i WHERE i.BatchId = b.Id ORDER BY i.Id DESC LIMIT 1),
                         b.PurchaseRatePaisa) AS RatePaisa,
                0 AS PurchasedUnits, 0 AS ReturnedUnits
            FROM MedicineBatches b
            JOIN Medicines m ON m.Id = b.MedicineId
            LEFT JOIN Suppliers s ON s.Id = b.SupplierId
            WHERE b.QuantityRemaining > 0 AND b.ExpiryDate <= @By {supplierFilter}
            ORDER BY b.ExpiryDate, m.Name", parameters.ToArray());
        var list = new List<ReturnableBatch>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(MapReturnable(r, linked: false));
        return list;
    }

    public async Task<PurchaseReturnDocument> PostAsync(PurchaseReturnDocument doc, long? userId)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Id > 0) throw new PurchaseRuleException("This debit note is already posted.");
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var number = await DocumentSequenceAllocator.AllocateAsync(conn, tx, DocumentType.DebitNote, doc.ReturnDate);
            var id = Convert.ToInt64(await ScalarAsync(conn, tx, @"
                INSERT INTO PurchaseReturns (ReturnNumber, ReturnDate, Kind, SupplierId, SupplierName, PurchaseId, PurchaseReference,
                    Reason, Notes, IsInterState, TaxablePaisa, CgstPaisa, SgstPaisa, IgstPaisa, TaxAmountPaisa, RoundOffPaisa,
                    NetAmountPaisa, RefundReceivedPaisa, IsCancelled, CreatedByUserId, CreatedAt)
                VALUES (@No, @Date, @Kind, @S, @SName, @P, @PRef, @Reason, @Notes, @Inter, @Taxable, @Cgst, @Sgst, @Igst, @Tax, @Round,
                    @Net, @Refund, 0, @User, @Now);
                SELECT last_insert_rowid();",
                ("@No", number), ("@Date", Date(doc.ReturnDate)), ("@Kind", (int)doc.Kind), ("@S", doc.SupplierId), ("@SName", doc.SupplierName),
                ("@P", OrNull(doc.PurchaseId)), ("@PRef", OrNull(doc.PurchaseReference)), ("@Reason", (int)doc.Reason),
                ("@Notes", OrNull(doc.Notes)), ("@Inter", doc.IsInterState ? 1 : 0), ("@Taxable", doc.TaxablePaisa), ("@Cgst", doc.CgstPaisa),
                ("@Sgst", doc.SgstPaisa), ("@Igst", doc.IgstPaisa), ("@Tax", doc.TaxAmountPaisa), ("@Round", doc.RoundOffPaisa),
                ("@Net", doc.NetAmountPaisa), ("@Refund", doc.RefundReceivedPaisa), ("@User", OrNull(userId)), ("@Now", Stamp(DateTime.UtcNow))));

            foreach (var line in doc.Lines)
            {
                if (line.PurchaseItemId is long itemId)
                {
                    using var cmd = Command(conn, tx, $@"
                        SELECT i.Quantity + i.FreeQuantity AS PurchasedUnits, {ReturnedUnitsSql} AS ReturnedUnits
                        FROM PurchaseItems i WHERE i.Id = @I", ("@I", itemId));
                    using var r = await cmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync()) throw new PurchaseRuleException($"The purchase line of {line.MedicineName} no longer exists.");
                    var left = r.Int("PurchasedUnits") - r.Int("ReturnedUnits");
                    if (line.Quantity > left)
                        throw new PurchaseRuleException($"Only {left} units of {line.MedicineName} (Batch {line.BatchNumber}) from that purchase can still be returned.");
                }

                var balance = await PurchaseStockLedger.TakeOutAsync(conn, tx, line.BatchId, line.Quantity, 0, receivedToo: false,
                    line.MedicineName, line.BatchNumber, "Reduce the return quantity.");

                line.PurchaseReturnId = id;
                line.Id = Convert.ToInt64(await ScalarAsync(conn, tx, @"
                    INSERT INTO PurchaseReturnItems (PurchaseReturnId, PurchaseItemId, MedicineId, MedicineName, HsnCode, BatchId, BatchNumber,
                        ExpiryDate, UnitsPerPack, Quantity, RatePaisa, GstRatePercent, TaxablePaisa, TaxPaisa, CgstPaisa, SgstPaisa, IgstPaisa, LineTotalPaisa)
                    VALUES (@R, @I, @M, @Name, @Hsn, @B, @BNo, @Exp, @Pack, @Q, @Rate, @G, @Taxable, @Tax, @Cgst, @Sgst, @Igst, @Total);
                    SELECT last_insert_rowid();",
                    ("@R", id), ("@I", OrNull(line.PurchaseItemId)), ("@M", line.MedicineId), ("@Name", line.MedicineName),
                    ("@Hsn", OrNull(line.HsnCode)), ("@B", line.BatchId), ("@BNo", line.BatchNumber), ("@Exp", Date(line.ExpiryDate)),
                    ("@Pack", line.UnitsPerPack), ("@Q", line.Quantity), ("@Rate", line.RatePaisa), ("@G", line.GstRatePercent),
                    ("@Taxable", line.TaxablePaisa), ("@Tax", line.TaxPaisa), ("@Cgst", line.CgstPaisa), ("@Sgst", line.SgstPaisa),
                    ("@Igst", line.IgstPaisa), ("@Total", line.LineTotalPaisa)));

                await PurchaseStockLedger.InsertMovementAsync(conn, tx, DateTime.Now, line.MedicineId, line.MedicineName, line.BatchId,
                    line.BatchNumber, MovementType.PurchaseReturn, -line.Quantity, balance, $"DebitNote:{id}",
                    $"Debit note {number} to {doc.SupplierName} ({doc.Reason.ToLabel()})", userId);
            }

            tx.Commit();
            doc.Id = id;
            doc.ReturnNumber = number;
            doc.CreatedByUserId = userId;
            return doc;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<PurchaseReturnDocument?> GetAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        return await LoadAsync(conn, null, id);
    }

    private static async Task<PurchaseReturnDocument?> LoadAsync(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        PurchaseReturnDocument doc;
        using (var cmd = Command(conn, tx, "SELECT r.*, s.Gstin AS SupplierGstin FROM PurchaseReturns r LEFT JOIN Suppliers s ON s.Id = r.SupplierId WHERE r.Id = @Id", ("@Id", id)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            if (!await r.ReadAsync()) return null;
            doc = new PurchaseReturnDocument
            {
                Id = r.Long("Id"),
                ReturnNumber = r.Str("ReturnNumber"),
                ReturnDate = r.DateValue("ReturnDate"),
                Kind = (PurchaseReturnKind)r.Int("Kind"),
                SupplierId = r.Long("SupplierId"),
                SupplierName = r.Str("SupplierName"),
                SupplierGstin = r.StrOrNull("SupplierGstin"),
                PurchaseId = r.LongOrNull("PurchaseId"),
                PurchaseReference = r.StrOrNull("PurchaseReference"),
                Reason = (PurchaseReturnReason)r.Int("Reason"),
                Notes = r.StrOrNull("Notes"),
                IsInterState = r.Bool("IsInterState"),
                TaxablePaisa = r.Long("TaxablePaisa"),
                CgstPaisa = r.Long("CgstPaisa"),
                SgstPaisa = r.Long("SgstPaisa"),
                IgstPaisa = r.Long("IgstPaisa"),
                TaxAmountPaisa = r.Long("TaxAmountPaisa"),
                RoundOffPaisa = r.Long("RoundOffPaisa"),
                NetAmountPaisa = r.Long("NetAmountPaisa"),
                RefundReceivedPaisa = r.Long("RefundReceivedPaisa"),
                IsCancelled = r.Bool("IsCancelled"),
                CreatedByUserId = r.LongOrNull("CreatedByUserId"),
                CreatedAt = r.DateValue("CreatedAt")
            };
        }

        using (var cmd = Command(conn, tx, "SELECT * FROM PurchaseReturnItems WHERE PurchaseReturnId = @Id ORDER BY Id", ("@Id", id)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                doc.Lines.Add(new PurchaseReturnLine
                {
                    Id = r.Long("Id"),
                    PurchaseReturnId = id,
                    PurchaseItemId = r.LongOrNull("PurchaseItemId"),
                    MedicineId = r.Long("MedicineId"),
                    MedicineName = r.Str("MedicineName"),
                    HsnCode = r.StrOrNull("HsnCode"),
                    BatchId = r.Long("BatchId"),
                    BatchNumber = r.Str("BatchNumber"),
                    ExpiryDate = r.DateValue("ExpiryDate"),
                    UnitsPerPack = Math.Max(1, r.Int("UnitsPerPack")),
                    Quantity = r.Int("Quantity"),
                    RatePaisa = r.Long("RatePaisa"),
                    GstRatePercent = r.Dec("GstRatePercent"),
                    TaxablePaisa = r.Long("TaxablePaisa"),
                    TaxPaisa = r.Long("TaxPaisa"),
                    CgstPaisa = r.Long("CgstPaisa"),
                    SgstPaisa = r.Long("SgstPaisa"),
                    IgstPaisa = r.Long("IgstPaisa"),
                    LineTotalPaisa = r.Long("LineTotalPaisa")
                });
            }
        }
        return doc;
    }

    public async Task<IReadOnlyList<PurchaseReturnListRow>> ListAsync(DateTime from, DateTime to, long? supplierId = null)
    {
        var parameters = new List<(string, object?)> { ("@From", Date(from)), ("@To", Date(to)) };
        var supplierFilter = string.Empty;
        if (supplierId is long s)
        {
            supplierFilter = " AND r.SupplierId = @S";
            parameters.Add(("@S", s));
        }
        using var conn = _db.CreateNewConnection();
        using var cmd = Command(conn, null, $@"
            SELECT r.*, (SELECT COUNT(*) FROM PurchaseReturnItems i WHERE i.PurchaseReturnId = r.Id) AS ItemCount
            FROM PurchaseReturns r WHERE r.ReturnDate BETWEEN @From AND @To {supplierFilter}
            ORDER BY r.ReturnDate DESC, r.Id DESC LIMIT 5000", parameters.ToArray());
        var list = new List<PurchaseReturnListRow>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new PurchaseReturnListRow
            {
                Id = r.Long("Id"),
                ReturnNumber = r.Str("ReturnNumber"),
                ReturnDate = r.DateValue("ReturnDate"),
                Kind = (PurchaseReturnKind)r.Int("Kind"),
                SupplierId = r.Long("SupplierId"),
                SupplierName = r.Str("SupplierName"),
                PurchaseReference = r.StrOrNull("PurchaseReference"),
                Reason = (PurchaseReturnReason)r.Int("Reason"),
                ItemCount = r.Int("ItemCount"),
                TaxablePaisa = r.Long("TaxablePaisa"),
                TaxAmountPaisa = r.Long("TaxAmountPaisa"),
                NetAmountPaisa = r.Long("NetAmountPaisa"),
                RefundReceivedPaisa = r.Long("RefundReceivedPaisa"),
                IsCancelled = r.Bool("IsCancelled")
            });
        }
        return list;
    }

    public async Task CancelAsync(long id, long? userId)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var doc = await LoadAsync(conn, tx, id) ?? throw new PurchaseRuleException($"Debit note #{id} was not found.");
            if (doc.IsCancelled) throw new PurchaseRuleException($"Debit note {doc.ReturnNumber} is already cancelled.");
            foreach (var line in doc.Lines)
            {
                var balance = await PurchaseStockLedger.PutBackAsync(conn, tx, line.BatchId, line.Quantity);
                await PurchaseStockLedger.InsertMovementAsync(conn, tx, DateTime.Now, line.MedicineId, line.MedicineName, line.BatchId,
                    line.BatchNumber, MovementType.StockAdjustment, line.Quantity, balance, $"DebitNote:{id}",
                    $"Debit note {doc.ReturnNumber} cancelled", userId);
            }
            await ExecAsync(conn, tx, "UPDATE PurchaseReturns SET IsCancelled = 1, CancelledAt = @Now WHERE Id = @Id",
                ("@Now", Stamp(DateTime.UtcNow)), ("@Id", id));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static ReturnableBatch MapReturnable(SqliteDataReader r, bool linked) => new()
    {
        BatchId = r.Long("BatchId"),
        MedicineId = r.Long("MedicineId"),
        MedicineName = r.Str("MedicineName"),
        HsnCode = r.StrOrNull("HsnCode"),
        BatchNumber = r.Str("BatchNumber"),
        ExpiryDate = r.DateValue("ExpiryDate"),
        UnitsPerPack = Math.Max(1, r.Int("UnitsPerPack")),
        QuantityRemaining = r.Int("QuantityRemaining"),
        SupplierId = r.LongOrNull("SupplierId"),
        SupplierName = r.StrOrNull("SupplierName"),
        PurchaseId = r.LongOrNull("PurchaseId"),
        PurchaseItemId = linked ? r.LongOrNull("PurchaseItemId") : null,
        PurchaseReference = r.StrOrNull("PurchaseReference"),
        PurchaseDate = r.DateOrNullValue("PurchaseDate"),
        PurchasedUnits = r.Int("PurchasedUnits"),
        ReturnedUnits = r.Int("ReturnedUnits"),
        RatePaisa = r.Long("RatePaisa"),
        MrpPaisa = r.Long("MrpPaisa"),
        GstRatePercent = r.Dec("GstRatePercent")
    };
}
