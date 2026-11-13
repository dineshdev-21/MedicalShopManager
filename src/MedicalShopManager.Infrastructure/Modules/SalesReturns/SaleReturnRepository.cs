using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.SalesReturns;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Numbering;

namespace MedicalShopManager.Infrastructure.Modules.SalesReturns;

/// <summary>
/// Sale returns / credit notes. Every write runs in one SQLite transaction: numbers (allocated inside it, so a
/// rollback never consumes one), document rows, stock back into batches with ledger rows, the shared Returns rows
/// Billing checks before editing a bill, and refund rows (+ Payments rows of type CustomerRefund for the cash book).
/// Balances (returned units, open credit) are derived in SQL from these rows, never kept as running totals.
/// </summary>
public sealed class SaleReturnRepository : ISaleReturnRepository
{
    private const string RefundedSql = "COALESCE((SELECT SUM(f.AmountPaisa) FROM SaleReturnRefunds f WHERE f.SaleReturnId = r.Id), 0)";

    private readonly DatabaseManager _db;

    public SaleReturnRepository(DatabaseManager db) => _db = db;

    // ───────────────────────── Look-ups ─────────────────────────

    public async Task<ReturnableSale?> GetReturnableSaleAsync(long saleId)
    {
        using var conn = _db.CreateNewConnection();
        return await LoadReturnableSaleAsync(conn, null, saleId);
    }

    private static async Task<ReturnableSale?> LoadReturnableSaleAsync(SqliteConnection conn, SqliteTransaction? tx, long saleId)
    {
        ReturnableSale? sale;
        using (var cmd = conn.Command(tx, @"
            SELECT s.*,
                COALESCE((SELECT SUM(r.NetAmountPaisa) FROM SaleReturns r WHERE r.SaleId = s.Id AND r.Status = 1), 0) AS PrevReturned,
                COALESCE((SELECT SUM(f.AmountPaisa) FROM SaleReturnRefunds f JOIN SaleReturns r ON r.Id = f.SaleReturnId
                          WHERE r.SaleId = s.Id AND r.Status = 1), 0) AS PrevRefunded
            FROM Sales s WHERE s.Id = @Id"))
        {
            cmd.Add("@Id", saleId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            sale = new ReturnableSale
            {
                SaleId = r.Long("Id"),
                InvoiceNumber = r.Str("InvoiceNumber") ?? string.Empty,
                InvoiceDate = r.Date("InvoiceDate"),
                IsCancelled = r.Int("Status", 1) == 2,
                IsCounterSale = r.Bool("IsCounterSale", true),
                CustomerId = r.LongOrNull("CustomerId"),
                CustomerName = r.Str("CustomerName"),
                CustomerPhone = r.Str("CustomerPhone"),
                CustomerGstin = r.Str("CustomerGstin"),
                IsInterState = r.Bool("IsInterState"),
                GrandTotalPaisa = r.Long("GrandTotalPaisa"),
                CustomChargePaisa = r.Long("CustomChargePaisa"),
                AmountPaidPaisa = r.Long("AmountPaidPaisa"),
                BalanceDuePaisa = r.Long("BalanceDuePaisa"),
                PreviouslyReturnedPaisa = r.Long("PrevReturned"),
                PreviouslyRefundedPaisa = r.Long("PrevRefunded")
            };
        }

        using (var cmd = conn.Command(tx, @"
            SELECT si.*, COALESCE(x.Units, 0) AS ReturnedUnits, COALESCE(x.Total, 0) AS ReturnedTotal, COALESCE(x.Taxable, 0) AS ReturnedTaxable
            FROM SaleItems si
            LEFT JOIN (SELECT ri.SaleItemId, SUM(ri.Units) AS Units, SUM(ri.LineTotalPaisa) AS Total, SUM(ri.TaxablePaisa) AS Taxable
                       FROM SaleReturnItems ri JOIN SaleReturns r ON r.Id = ri.SaleReturnId
                       WHERE r.Status = 1 AND r.SaleId = @Id
                       GROUP BY ri.SaleItemId) x ON x.SaleItemId = si.Id
            WHERE si.SaleId = @Id
            ORDER BY si.Id"))
        {
            cmd.Add("@Id", saleId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                sale.Lines.Add(new ReturnableSaleLine
                {
                    SaleItemId = r.Long("Id"),
                    MedicineId = r.Long("MedicineId"),
                    MedicineName = r.Str("MedicineName") ?? string.Empty,
                    Manufacturer = r.Str("Manufacturer"),
                    HsnCode = r.Str("HsnCode"),
                    BatchId = r.Long("BatchId"),
                    BatchNumber = r.Str("BatchNumber") ?? string.Empty,
                    ExpiryDate = r.Date("ExpiryDate"),
                    UnitsPerPack = Math.Max(r.Int("UnitsPerPack", 1), 1),
                    SoldUnits = r.Long("Quantity"),
                    FreeUnits = r.Long("FreeUnits"),
                    ReturnedUnits = r.Long("ReturnedUnits"),
                    RatePerPackPaisa = r.Long("RatePerPackPaisa"),
                    MrpPerPackPaisa = r.Long("MrpPerPackPaisa"),
                    DiscountPercent = r.Dec("DiscountPercent"),
                    GstPercent = r.Dec("GstRatePercent"),
                    LineTotalPaisa = r.Long("LineTotalPaisa"),
                    TaxablePaisa = r.Long("TaxableValuePaisa"),
                    ReturnedTotalPaisa = r.Long("ReturnedTotal"),
                    ReturnedTaxablePaisa = r.Long("ReturnedTaxable")
                });
            }
        }
        return sale;
    }

    public async Task<IReadOnlyDictionary<long, SaleReturnBatchInfo>> GetBatchesAsync(IEnumerable<long> batchIds)
    {
        ArgumentNullException.ThrowIfNull(batchIds);
        var result = new Dictionary<long, SaleReturnBatchInfo>();
        using var conn = _db.CreateNewConnection();
        foreach (var id in batchIds.Distinct())
        {
            var batch = await LoadBatchAsync(conn, null, id);
            if (batch is not null) result[id] = batch;
        }
        return result;
    }

    public async Task<IReadOnlyList<SaleReturnBatchInfo>> GetItemBatchesAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, BatchSelect + " WHERE b.MedicineId = @MedicineId ORDER BY b.ExpiryDate DESC, b.Id DESC");
        cmd.Add("@MedicineId", medicineId);
        var list = new List<SaleReturnBatchInfo>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(MapBatch(r));
        return list;
    }

    public async Task<IReadOnlyList<SaleReturnItemOption>> SearchItemsAsync(string text, int limit = 30)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<SaleReturnItemOption>();
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT m.Id, m.Name, m.Manufacturer, m.PackLabel, m.UnitsPerPack FROM Medicines m
            WHERE m.Name LIKE @Search ESCAPE '\' OR m.Composition LIKE @Search ESCAPE '\' OR m.Barcode = @Exact
            ORDER BY CASE WHEN m.Name LIKE @Prefix ESCAPE '\' THEN 0 ELSE 1 END, m.Name
            LIMIT @Limit");
        cmd.Add("@Search", SalesReturnsSql.Like(text));
        cmd.Add("@Prefix", SalesReturnsSql.Like(text).TrimStart('%'));
        cmd.Add("@Exact", text.Trim());
        cmd.Add("@Limit", Math.Clamp(limit, 1, 200));
        var list = new List<SaleReturnItemOption>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new SaleReturnItemOption(r.Long("Id"), r.Str("Name") ?? string.Empty, r.Str("Manufacturer"), r.Str("PackLabel"),
                Math.Max(r.Int("UnitsPerPack", 1), 1)));
        return list;
    }

    public async Task<IReadOnlyList<SaleReturnCustomerOption>> SearchCustomersAsync(string text, int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<SaleReturnCustomerOption>();
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT Id, Name, Phone FROM Customers
            WHERE IsActive = 1 AND (Name LIKE @Search ESCAPE '\' OR Phone LIKE @Search ESCAPE '\')
            ORDER BY Name LIMIT @Limit");
        cmd.Add("@Search", SalesReturnsSql.Like(text));
        cmd.Add("@Limit", Math.Clamp(limit, 1, 200));
        var list = new List<SaleReturnCustomerOption>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new SaleReturnCustomerOption(r.Long("Id"), r.Str("Name") ?? string.Empty, r.Str("Phone")));
        return list;
    }

    private const string BatchSelect = @"
        SELECT b.Id, b.MedicineId, b.BatchNumber, b.ExpiryDate, b.UnitsPerPack, b.MrpPaisa, b.SaleRatePaisa, b.GstRatePercent,
               b.QuantityRemaining, m.Name AS MedicineName, m.Manufacturer, m.HsnCode
        FROM MedicineBatches b JOIN Medicines m ON m.Id = b.MedicineId";

    private static async Task<SaleReturnBatchInfo?> LoadBatchAsync(SqliteConnection conn, SqliteTransaction? tx, long batchId)
    {
        using var cmd = conn.Command(tx, BatchSelect + " WHERE b.Id = @Id");
        cmd.Add("@Id", batchId);
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapBatch(r) : null;
    }

    private static SaleReturnBatchInfo MapBatch(SqliteDataReader r) => new()
    {
        BatchId = r.Long("Id"),
        MedicineId = r.Long("MedicineId"),
        MedicineName = r.Str("MedicineName") ?? string.Empty,
        Manufacturer = r.Str("Manufacturer"),
        HsnCode = r.Str("HsnCode"),
        BatchNumber = r.Str("BatchNumber") ?? string.Empty,
        ExpiryDate = r.Date("ExpiryDate"),
        UnitsPerPack = Math.Max(r.Int("UnitsPerPack", 1), 1),
        MrpPaisa = r.Long("MrpPaisa"),
        SaleRatePaisa = r.Long("SaleRatePaisa"),
        GstPercent = r.Dec("GstRatePercent"),
        QuantityRemaining = r.Long("QuantityRemaining")
    };

    // ───────────────────────── Posting ─────────────────────────

    public async Task<(long Id, string ReturnNumber, string CreditNoteNumber)> InsertAsync(SaleReturnDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Lines.Count == 0) throw new SaleReturnValidationException("A sale return needs at least one line.");
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await RecheckAsync(conn, tx, document);

            var returnNumber = await DocumentSequenceAllocator.AllocateAsync(conn, tx, DocumentType.SaleReturn, document.ReturnDate);
            var creditNoteNumber = await DocumentSequenceAllocator.AllocateAsync(conn, tx, DocumentType.CreditNote, document.ReturnDate);
            var id = await InsertHeaderAsync(conn, tx, document, returnNumber, creditNoteNumber);
            var against = document.SaleInvoiceNumber is { } inv ? $" against {inv}" : " without bill";
            var movementDate = document.ReturnDate.Date.Add(DateTime.Now.TimeOfDay);

            foreach (var line in document.Lines)
            {
                var itemId = await InsertLineAsync(conn, tx, id, line);
                await InsertLedgerReturnAsync(conn, tx, document, line, id, itemId, creditNoteNumber);
                if (!line.GoesToStock) continue;

                var balance = await MoveStockAsync(conn, tx, line.BatchId, line.Units)
                    ?? throw new SaleReturnValidationException($"Batch {line.BatchNumber} of {line.MedicineName} no longer exists.");
                var reason = $"Sale return {returnNumber} (credit note {creditNoteNumber}){against}" +
                             (line.Disposition == SaleReturnDisposition.ExpiredStock ? " - expired batch, held as expired stock" : "");
                await InsertMovementAsync(conn, tx, movementDate, line, MovementType.SaleReturn, line.Units, 0, balance,
                    $"SaleReturn:{id}", reason, document.CreatedByUserId);
            }

            if (document.RefundMode == SaleRefundMode.Cash && document.NetAmountPaisa > 0)
            {
                await InsertRefundAsync(conn, tx, id, creditNoteNumber, document.ReturnDate, document.RefundMethod,
                    document.NetAmountPaisa, document.RefundReference, document.CreatedByUserId,
                    $"Refund for sale return {returnNumber} (credit note {creditNoteNumber}){against}");
            }

            tx.Commit();
            return (id, returnNumber, creditNoteNumber);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Inside the posting transaction: the bill is still active and each line still has the units to return.</summary>
    private static async Task RecheckAsync(SqliteConnection conn, SqliteTransaction tx, SaleReturnDocument document)
    {
        if (document.SaleId is not { } saleId)
        {
            foreach (var line in document.Lines)
            {
                var batch = await LoadBatchAsync(conn, tx, line.BatchId);
                if (batch is null || batch.MedicineId != line.MedicineId)
                    throw new SaleReturnValidationException($"Batch {line.BatchNumber} of {line.MedicineName} was not found.");
            }
            return;
        }

        var sale = await LoadReturnableSaleAsync(conn, tx, saleId)
                   ?? throw new SaleReturnValidationException("The original invoice no longer exists.");
        if (sale.IsCancelled)
            throw new SaleReturnValidationException($"Invoice {sale.InvoiceNumber} was cancelled, so nothing can be returned against it.");
        var errors = new List<string>();
        foreach (var line in document.Lines)
        {
            var sold = sale.Lines.FirstOrDefault(l => l.SaleItemId == line.SaleItemId);
            if (sold is null || sold.BatchId != line.BatchId)
                errors.Add($"{line.MedicineName} is not on invoice {sale.InvoiceNumber}.");
            else if (line.Units > sold.ReturnableUnits)
                errors.Add($"{line.MedicineName} (Batch {line.BatchNumber}): only {sold.ReturnableDisplay} can still be returned " +
                           "(another return was saved meanwhile). Reload the invoice.");
        }
        if (errors.Count > 0) throw new SaleReturnValidationException(errors);
    }

    private static async Task<long> InsertHeaderAsync(SqliteConnection conn, SqliteTransaction tx, SaleReturnDocument d,
        string returnNumber, string creditNoteNumber)
    {
        using var cmd = conn.Command(tx, @"
            INSERT INTO SaleReturns (ReturnNumber, CreditNoteNumber, ReturnDate, SaleId, SaleInvoiceNumber, SaleInvoiceDate,
                CustomerId, CustomerName, CustomerPhone, CustomerGstin, IsInterState, RefundMode, RefundMethod, RefundReference,
                Reason, Notes, GrossPaisa, DiscountPaisa, TaxablePaisa, CgstPaisa, SgstPaisa, IgstPaisa, TaxPaisa, RoundOffPaisa,
                NetAmountPaisa, Status, CreatedByUserId, CreatedAt)
            VALUES (@ReturnNumber, @CreditNoteNumber, @ReturnDate, @SaleId, @SaleInvoiceNumber, @SaleInvoiceDate,
                @CustomerId, @CustomerName, @CustomerPhone, @CustomerGstin, @IsInterState, @RefundMode, @RefundMethod, @RefundReference,
                @Reason, @Notes, @GrossPaisa, @DiscountPaisa, @TaxablePaisa, @CgstPaisa, @SgstPaisa, @IgstPaisa, @TaxPaisa, @RoundOffPaisa,
                @NetAmountPaisa, @Status, @UserId, @CreatedAt);
            SELECT last_insert_rowid();");
        cmd.Add("@ReturnNumber", returnNumber);
        cmd.Add("@CreditNoteNumber", creditNoteNumber);
        cmd.AddDate("@ReturnDate", d.ReturnDate);
        cmd.Add("@SaleId", d.SaleId);
        cmd.Add("@SaleInvoiceNumber", SalesReturnsSql.Blank(d.SaleInvoiceNumber));
        cmd.AddDate("@SaleInvoiceDate", d.SaleInvoiceDate);
        cmd.Add("@CustomerId", d.CustomerId is > 0 ? d.CustomerId : null);
        cmd.Add("@CustomerName", SalesReturnsSql.Blank(d.CustomerName));
        cmd.Add("@CustomerPhone", SalesReturnsSql.Blank(d.CustomerPhone));
        cmd.Add("@CustomerGstin", SalesReturnsSql.Blank(d.CustomerGstin));
        cmd.Add("@IsInterState", d.IsInterState ? 1 : 0);
        cmd.Add("@RefundMode", (int)d.RefundMode);
        cmd.Add("@RefundMethod", (int)d.RefundMethod);
        cmd.Add("@RefundReference", SalesReturnsSql.Blank(d.RefundReference));
        cmd.Add("@Reason", SalesReturnsSql.Blank(d.Reason));
        cmd.Add("@Notes", SalesReturnsSql.Blank(d.Notes));
        cmd.Add("@GrossPaisa", d.GrossPaisa);
        cmd.Add("@DiscountPaisa", d.DiscountPaisa);
        cmd.Add("@TaxablePaisa", d.TaxablePaisa);
        cmd.Add("@CgstPaisa", d.CgstPaisa);
        cmd.Add("@SgstPaisa", d.SgstPaisa);
        cmd.Add("@IgstPaisa", d.IgstPaisa);
        cmd.Add("@TaxPaisa", d.TaxPaisa);
        cmd.Add("@RoundOffPaisa", d.RoundOffPaisa);
        cmd.Add("@NetAmountPaisa", d.NetAmountPaisa);
        cmd.Add("@Status", (int)SaleReturnStatus.Active);
        cmd.Add("@UserId", d.CreatedByUserId);
        cmd.Add("@CreatedAt", DateTime.UtcNow.ToString("o"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<long> InsertLineAsync(SqliteConnection conn, SqliteTransaction tx, long returnId, SaleReturnDocumentLine l)
    {
        using var cmd = conn.Command(tx, @"
            INSERT INTO SaleReturnItems (SaleReturnId, SaleItemId, MedicineId, MedicineName, HsnCode, BatchId, BatchNumber, ExpiryDate,
                UnitsPerPack, Units, RatePerPackPaisa, MrpPerPackPaisa, DiscountPercent, GstRatePercent, GrossPaisa, DiscountPaisa,
                TaxablePaisa, CgstPaisa, SgstPaisa, IgstPaisa, TaxPaisa, LineTotalPaisa, Disposition)
            VALUES (@ReturnId, @SaleItemId, @MedicineId, @MedicineName, @HsnCode, @BatchId, @BatchNumber, @ExpiryDate,
                @UnitsPerPack, @Units, @Rate, @Mrp, @Discount, @Gst, @Gross, @DiscountPaisa,
                @Taxable, @Cgst, @Sgst, @Igst, @Tax, @Total, @Disposition);
            SELECT last_insert_rowid();");
        cmd.Add("@ReturnId", returnId);
        cmd.Add("@SaleItemId", l.SaleItemId);
        cmd.Add("@MedicineId", l.MedicineId);
        cmd.Add("@MedicineName", l.MedicineName);
        cmd.Add("@HsnCode", SalesReturnsSql.Blank(l.HsnCode));
        cmd.Add("@BatchId", l.BatchId);
        cmd.Add("@BatchNumber", l.BatchNumber);
        cmd.AddDate("@ExpiryDate", l.ExpiryDate == default ? null : l.ExpiryDate);
        cmd.Add("@UnitsPerPack", Math.Max(l.UnitsPerPack, 1));
        cmd.Add("@Units", l.Units);
        cmd.Add("@Rate", l.RatePerPackPaisa);
        cmd.Add("@Mrp", l.MrpPerPackPaisa);
        cmd.Add("@Discount", l.DiscountPercent);
        cmd.Add("@Gst", l.GstPercent);
        cmd.Add("@Gross", l.GrossPaisa);
        cmd.Add("@DiscountPaisa", l.DiscountPaisa);
        cmd.Add("@Taxable", l.TaxablePaisa);
        cmd.Add("@Cgst", l.CgstPaisa);
        cmd.Add("@Sgst", l.SgstPaisa);
        cmd.Add("@Igst", l.IgstPaisa);
        cmd.Add("@Tax", l.TaxPaisa);
        cmd.Add("@Total", l.LineTotalPaisa);
        cmd.Add("@Disposition", (int)l.Disposition);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>The shared Returns ledger row (legacy reports, and Billing's "bill has returns" check).</summary>
    private static async Task InsertLedgerReturnAsync(SqliteConnection conn, SqliteTransaction tx, SaleReturnDocument d,
        SaleReturnDocumentLine l, long returnId, long itemId, string creditNoteNumber)
    {
        using var cmd = conn.Command(tx, @"
            INSERT INTO Returns (OriginalTransactionId, OriginalReference, ReturnType, ReturnDate, MedicineId, MedicineName,
                BatchId, BatchNumber, Quantity, RefundAmountPaisa, Reason, ReturnedToStock, ProcessedByUserId, CreatedAt,
                SaleReturnId, SaleReturnItemId)
            VALUES (@SaleId, @Reference, @Type, @Date, @MedicineId, @MedicineName, @BatchId, @BatchNumber, @Quantity, @Amount,
                @Reason, @ToStock, @UserId, @CreatedAt, @ReturnId, @ItemId);");
        cmd.Add("@SaleId", d.SaleId);
        cmd.Add("@Reference", d.SaleInvoiceNumber ?? creditNoteNumber);
        cmd.Add("@Type", (int)ReturnType.SalesReturn);
        cmd.AddDate("@Date", d.ReturnDate);
        cmd.Add("@MedicineId", l.MedicineId);
        cmd.Add("@MedicineName", l.MedicineName);
        cmd.Add("@BatchId", l.BatchId);
        cmd.Add("@BatchNumber", l.BatchNumber);
        cmd.Add("@Quantity", checked((int)l.Units));
        cmd.Add("@Amount", l.LineTotalPaisa);
        cmd.Add("@Reason", SalesReturnsSql.Blank(d.Reason) ?? l.DispositionLabel);
        cmd.Add("@ToStock", l.GoesToStock ? 1 : 0);
        cmd.Add("@UserId", d.CreatedByUserId);
        cmd.Add("@CreatedAt", DateTime.UtcNow.ToString("o"));
        cmd.Add("@ReturnId", returnId);
        cmd.Add("@ItemId", itemId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertRefundAsync(SqliteConnection conn, SqliteTransaction tx, long returnId, string creditNoteNumber,
        DateTime date, PaymentMethod method, long amountPaisa, string? reference, long? userId, string notes)
    {
        long paymentId;
        using (var cmd = conn.Command(tx, @"
            INSERT INTO Payments (TransactionId, TransactionReference, PaymentDate, PaymentType, PaymentMethod, AmountPaisa,
                ReferenceNumber, Notes, CreatedByUserId, CreatedAt)
            VALUES (@ReturnId, @CreditNote, @Date, @Type, @Method, @Amount, @Reference, @Notes, @UserId, @CreatedAt);
            SELECT last_insert_rowid();"))
        {
            cmd.Add("@ReturnId", returnId);
            cmd.Add("@CreditNote", creditNoteNumber);
            cmd.AddDateTime("@Date", date.Date.Add(DateTime.Now.TimeOfDay));
            cmd.Add("@Type", (int)PaymentType.CustomerRefund);
            cmd.Add("@Method", (int)method);
            cmd.Add("@Amount", amountPaisa);
            cmd.Add("@Reference", SalesReturnsSql.Blank(reference) ?? creditNoteNumber);
            cmd.Add("@Notes", notes);
            cmd.Add("@UserId", userId);
            cmd.Add("@CreatedAt", DateTime.UtcNow.ToString("o"));
            paymentId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        using (var cmd = conn.Command(tx, @"
            INSERT INTO SaleReturnRefunds (SaleReturnId, RefundDate, Method, AmountPaisa, Reference, PaymentId, CreatedByUserId, CreatedAt)
            VALUES (@ReturnId, @Date, @Method, @Amount, @Reference, @PaymentId, @UserId, @CreatedAt);
            SELECT last_insert_rowid();"))
        {
            cmd.Add("@ReturnId", returnId);
            cmd.AddDate("@Date", date);
            cmd.Add("@Method", (int)method);
            cmd.Add("@Amount", amountPaisa);
            cmd.Add("@Reference", SalesReturnsSql.Blank(reference));
            cmd.Add("@PaymentId", paymentId);
            cmd.Add("@UserId", userId);
            cmd.Add("@CreatedAt", DateTime.UtcNow.ToString("o"));
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
    }

    /// <summary>Adds <paramref name="delta"/> units to a batch; a decrement only succeeds if enough remain. Null = refused or missing.</summary>
    private static async Task<long?> MoveStockAsync(SqliteConnection conn, SqliteTransaction tx, long batchId, long delta)
    {
        using var cmd = conn.Command(tx, @"
            UPDATE MedicineBatches SET QuantityRemaining = QuantityRemaining + @Delta, UpdatedAt = @Now
            WHERE Id = @BatchId AND QuantityRemaining + @Delta >= 0
            RETURNING QuantityRemaining;");
        cmd.Add("@Delta", delta);
        cmd.Add("@BatchId", batchId);
        cmd.Add("@Now", DateTime.UtcNow.ToString("o"));
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static async Task InsertMovementAsync(SqliteConnection conn, SqliteTransaction tx, DateTime date, SaleReturnDocumentLine line,
        MovementType type, long quantityIn, long quantityOut, long balanceAfter, string reference, string reason, long? userId)
    {
        using var cmd = conn.Command(tx, @"
            INSERT INTO StockMovements (MovementDate, MedicineId, MedicineName, BatchId, BatchNumber, MovementType,
                QuantityIn, QuantityOut, BalanceAfter, Reference, Reason, CreatedByUserId, CreatedAt)
            VALUES (@Date, @MedicineId, @MedicineName, @BatchId, @BatchNumber, @Type,
                @In, @Out, @Balance, @Reference, @Reason, @UserId, @CreatedAt);");
        cmd.AddDateTime("@Date", date);
        cmd.Add("@MedicineId", line.MedicineId);
        cmd.Add("@MedicineName", line.MedicineName);
        cmd.Add("@BatchId", line.BatchId);
        cmd.Add("@BatchNumber", line.BatchNumber);
        cmd.Add("@Type", (int)type);
        cmd.Add("@In", quantityIn);
        cmd.Add("@Out", quantityOut);
        cmd.Add("@Balance", balanceAfter);
        cmd.Add("@Reference", reference);
        cmd.Add("@Reason", reason);
        cmd.Add("@UserId", userId);
        cmd.Add("@CreatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    // ───────────────────────── Reading ─────────────────────────

    public async Task<SaleReturnDocument?> GetAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        return await LoadDocumentAsync(conn, null, id);
    }

    private static async Task<SaleReturnDocument?> LoadDocumentAsync(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        SaleReturnDocument? doc;
        using (var cmd = conn.Command(tx, $@"
            SELECT r.*, u.DisplayName AS CreatedByName, {RefundedSql} AS RefundedPaisa
            FROM SaleReturns r LEFT JOIN Users u ON u.Id = r.CreatedByUserId
            WHERE r.Id = @Id"))
        {
            cmd.Add("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            doc = new SaleReturnDocument
            {
                Id = r.Long("Id"),
                ReturnNumber = r.Str("ReturnNumber") ?? string.Empty,
                CreditNoteNumber = r.Str("CreditNoteNumber") ?? string.Empty,
                ReturnDate = r.Date("ReturnDate"),
                Status = (SaleReturnStatus)r.Int("Status", 1),
                SaleId = r.LongOrNull("SaleId"),
                SaleInvoiceNumber = r.Str("SaleInvoiceNumber"),
                SaleInvoiceDate = r.DateOrNull("SaleInvoiceDate"),
                CustomerId = r.LongOrNull("CustomerId"),
                CustomerName = r.Str("CustomerName"),
                CustomerPhone = r.Str("CustomerPhone"),
                CustomerGstin = r.Str("CustomerGstin"),
                IsInterState = r.Bool("IsInterState"),
                RefundMode = (SaleRefundMode)r.Int("RefundMode", 1),
                RefundMethod = (PaymentMethod)r.Int("RefundMethod"),
                RefundReference = r.Str("RefundReference"),
                Reason = r.Str("Reason"),
                Notes = r.Str("Notes"),
                GrossPaisa = r.Long("GrossPaisa"),
                DiscountPaisa = r.Long("DiscountPaisa"),
                TaxablePaisa = r.Long("TaxablePaisa"),
                CgstPaisa = r.Long("CgstPaisa"),
                SgstPaisa = r.Long("SgstPaisa"),
                IgstPaisa = r.Long("IgstPaisa"),
                TaxPaisa = r.Long("TaxPaisa"),
                RoundOffPaisa = r.Long("RoundOffPaisa"),
                NetAmountPaisa = r.Long("NetAmountPaisa"),
                RefundedPaisa = r.Long("RefundedPaisa"),
                CreatedByUserId = r.LongOrNull("CreatedByUserId"),
                CreatedByName = r.Str("CreatedByName"),
                CreatedAt = r.Date("CreatedAt"),
                CancelledAt = r.DateOrNull("CancelledAt"),
                CancelReason = r.Str("CancelReason")
            };
        }

        using (var cmd = conn.Command(tx, "SELECT * FROM SaleReturnItems WHERE SaleReturnId = @Id ORDER BY Id"))
        {
            cmd.Add("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                doc.Lines.Add(new SaleReturnDocumentLine
                {
                    Id = r.Long("Id"),
                    SaleItemId = r.LongOrNull("SaleItemId"),
                    MedicineId = r.Long("MedicineId"),
                    MedicineName = r.Str("MedicineName") ?? string.Empty,
                    HsnCode = r.Str("HsnCode"),
                    BatchId = r.Long("BatchId"),
                    BatchNumber = r.Str("BatchNumber") ?? string.Empty,
                    ExpiryDate = r.Date("ExpiryDate") == DateTime.MinValue ? default : r.Date("ExpiryDate"),
                    UnitsPerPack = Math.Max(r.Int("UnitsPerPack", 1), 1),
                    Units = r.Long("Units"),
                    RatePerPackPaisa = r.Long("RatePerPackPaisa"),
                    MrpPerPackPaisa = r.Long("MrpPerPackPaisa"),
                    DiscountPercent = r.Dec("DiscountPercent"),
                    GstPercent = r.Dec("GstRatePercent"),
                    GrossPaisa = r.Long("GrossPaisa"),
                    DiscountPaisa = r.Long("DiscountPaisa"),
                    TaxablePaisa = r.Long("TaxablePaisa"),
                    CgstPaisa = r.Long("CgstPaisa"),
                    SgstPaisa = r.Long("SgstPaisa"),
                    IgstPaisa = r.Long("IgstPaisa"),
                    TaxPaisa = r.Long("TaxPaisa"),
                    LineTotalPaisa = r.Long("LineTotalPaisa"),
                    Disposition = (SaleReturnDisposition)r.Int("Disposition", 1)
                });
            }
        }

        using (var cmd = conn.Command(tx, "SELECT * FROM SaleReturnRefunds WHERE SaleReturnId = @Id ORDER BY RefundDate, Id"))
        {
            cmd.Add("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                doc.Refunds.Add(new SaleReturnRefund(r.Long("Id"), r.Date("RefundDate"), (PaymentMethod)r.Int("Method"),
                    r.Long("AmountPaisa"), r.Str("Reference")));
        }
        return doc;
    }

    public async Task<IReadOnlyList<SaleReturnRow>> ListAsync(SaleReturnFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, $@"
            SELECT r.Id, r.ReturnNumber, r.CreditNoteNumber, r.ReturnDate, r.SaleId, r.SaleInvoiceNumber, r.CustomerId, r.CustomerName,
                   r.CustomerPhone, r.TaxablePaisa, r.TaxPaisa, r.NetAmountPaisa, r.RefundMode, r.Status, r.Reason,
                   (SELECT COUNT(*) FROM SaleReturnItems i WHERE i.SaleReturnId = r.Id) AS ItemCount,
                   {RefundedSql} AS RefundedPaisa
            FROM SaleReturns r
            WHERE (@OpenOnly = 1 OR @Search IS NOT NULL OR (r.ReturnDate >= @From AND r.ReturnDate <= @To))
              AND (@OpenOnly = 0 OR (r.Status = 1 AND r.NetAmountPaisa > {RefundedSql}))
              AND (@IncludeCancelled = 1 OR r.Status = 1)
              AND (@CustomerId IS NULL OR r.CustomerId = @CustomerId)
              AND (@Mode IS NULL OR r.RefundMode = @Mode)
              AND (@Search IS NULL OR r.ReturnNumber LIKE @Search ESCAPE '\' OR r.CreditNoteNumber LIKE @Search ESCAPE '\'
                   OR r.SaleInvoiceNumber LIKE @Search ESCAPE '\' OR r.CustomerName LIKE @Search ESCAPE '\'
                   OR r.CustomerPhone LIKE @Search ESCAPE '\')
            ORDER BY r.ReturnDate DESC, r.Id DESC
            LIMIT @Limit");
        cmd.AddDate("@From", filter.From.Date);
        cmd.AddDate("@To", filter.To.Date);
        cmd.Add("@OpenOnly", filter.OpenCreditOnly ? 1 : 0);
        cmd.Add("@IncludeCancelled", filter.IncludeCancelled && !filter.OpenCreditOnly ? 1 : 0);
        cmd.Add("@CustomerId", filter.CustomerId);
        cmd.Add("@Mode", filter.RefundMode is { } mode ? (int)mode : null);
        cmd.Add("@Search", SalesReturnsSql.LikeOrNull(filter.SearchText));
        cmd.Add("@Limit", Math.Clamp(filter.Limit, 1, 10000));
        var rows = new List<SaleReturnRow>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows.Add(new SaleReturnRow
            {
                Id = r.Long("Id"),
                ReturnNumber = r.Str("ReturnNumber") ?? string.Empty,
                CreditNoteNumber = r.Str("CreditNoteNumber") ?? string.Empty,
                ReturnDate = r.Date("ReturnDate"),
                SaleId = r.LongOrNull("SaleId"),
                SaleInvoiceNumber = r.Str("SaleInvoiceNumber"),
                CustomerId = r.LongOrNull("CustomerId"),
                CustomerName = r.Str("CustomerName"),
                CustomerPhone = r.Str("CustomerPhone"),
                ItemCount = r.Int("ItemCount"),
                TaxablePaisa = r.Long("TaxablePaisa"),
                TaxPaisa = r.Long("TaxPaisa"),
                NetAmountPaisa = r.Long("NetAmountPaisa"),
                RefundedPaisa = r.Long("RefundedPaisa"),
                RefundMode = (SaleRefundMode)r.Int("RefundMode", 1),
                Status = (SaleReturnStatus)r.Int("Status", 1),
                Reason = r.Str("Reason")
            });
        }
        return rows;
    }

    public async Task<long> GetCustomerCreditAsync(long customerId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, $@"
            SELECT COALESCE(SUM(MAX(0, r.NetAmountPaisa - {RefundedSql})), 0)
            FROM SaleReturns r WHERE r.CustomerId = @CustomerId AND r.Status = 1");
        cmd.Add("@CustomerId", customerId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // ───────────────────────── Refund and cancel ─────────────────────────

    public async Task<long> AddRefundAsync(CreditRefundRequest refund, string creditNoteNumber)
    {
        ArgumentNullException.ThrowIfNull(refund);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            long open;
            using (var cmd = conn.Command(tx, $"SELECT r.Status, r.NetAmountPaisa - {RefundedSql} AS OpenPaisa FROM SaleReturns r WHERE r.Id = @Id"))
            {
                cmd.Add("@Id", refund.SaleReturnId);
                using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) throw new SaleReturnValidationException("The credit note no longer exists.");
                if (r.Int("Status", 1) != (int)SaleReturnStatus.Active)
                    throw new SaleReturnValidationException($"Credit note {creditNoteNumber} is cancelled.");
                open = r.Long("OpenPaisa");
            }
            if (refund.AmountPaisa <= 0 || refund.AmountPaisa > open)
                throw new SaleReturnValidationException(
                    $"Only ₹{SaleReturnCalculator.Rupees(Math.Max(0, open))} of credit note {creditNoteNumber} is open.");

            var id = await InsertRefundAsync(conn, tx, refund.SaleReturnId, creditNoteNumber, refund.RefundDate, refund.Method,
                refund.AmountPaisa, refund.Reference, refund.UserId, $"Refund of credit note {creditNoteNumber}");
            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task CancelAsync(long id, string reason, long? userId)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var doc = await LoadDocumentAsync(conn, tx, id) ?? throw new SaleReturnValidationException("The sale return no longer exists.");
            if (doc.IsCancelled) throw new SaleReturnValidationException($"Sale return {doc.ReturnNumber} is already cancelled.");

            var errors = new List<string>();
            foreach (var line in doc.Lines.Where(l => l.GoesToStock))
            {
                var balance = await MoveStockAsync(conn, tx, line.BatchId, -line.Units);
                if (balance is null)
                {
                    errors.Add($"{line.MedicineName} (Batch {line.BatchNumber}): the {line.QuantityDisplay} returned to stock has been sold or " +
                               "adjusted since, so this return cannot be cancelled.");
                    continue;
                }
                await InsertMovementAsync(conn, tx, DateTime.Now, line, MovementType.SaleReturn, 0, line.Units, balance.Value,
                    $"SaleReturn:{id}", $"Sale return {doc.ReturnNumber} cancelled: {reason}", userId);
            }
            if (errors.Count > 0) throw new SaleReturnValidationException(errors);

            using (var cmd = conn.Command(tx, @"
                DELETE FROM Payments WHERE PaymentType = @Refund
                    AND Id IN (SELECT PaymentId FROM SaleReturnRefunds WHERE SaleReturnId = @Id AND PaymentId IS NOT NULL);
                DELETE FROM Returns WHERE SaleReturnId = @Id AND ReturnType = @SalesReturn;
                UPDATE SaleReturns SET Status = @Cancelled, CancelledAt = @Now, CancelReason = @Reason WHERE Id = @Id;"))
            {
                cmd.Add("@Id", id);
                cmd.Add("@Refund", (int)PaymentType.CustomerRefund);
                cmd.Add("@SalesReturn", (int)ReturnType.SalesReturn);
                cmd.Add("@Cancelled", (int)SaleReturnStatus.Cancelled);
                cmd.AddDateTime("@Now", DateTime.Now);
                cmd.Add("@Reason", reason);
                await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
