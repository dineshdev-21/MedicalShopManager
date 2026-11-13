using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Numbering;
using static MedicalShopManager.Infrastructure.Modules.Purchases.PurchaseSql;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>
/// Purchases / PurchaseItems persistence. Every write that touches stock or money runs in one SQLite transaction
/// together with its stock movements, payment rows and document number, so a failure leaves nothing behind
/// (ARCHITECTURE-MODULES §8, lesson 8).
/// </summary>
public sealed class PurchaseBillRepository : IPurchaseBillRepository
{
    private static readonly string SupplierPaymentTypes = $"{(int)PaymentType.SupplierPayment}, {(int)PaymentType.PurchasePayment}";
    private readonly DatabaseManager _db;

    public PurchaseBillRepository(DatabaseManager db) => _db = db;

    // ───────────────────────── Reads ─────────────────────────

    public async Task<PurchaseBill?> GetAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        return await LoadAsync(conn, null, id);
    }

    private static async Task<PurchaseBill?> LoadAsync(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        PurchaseBill bill;
        using (var cmd = Command(conn, tx, "SELECT p.*, s.Gstin AS SupplierGstin FROM Purchases p LEFT JOIN Suppliers s ON s.Id = p.SupplierId WHERE p.Id = @Id", ("@Id", id)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            if (!await r.ReadAsync()) return null;
            bill = MapHeader(r);
        }

        using (var cmd = Command(conn, tx, "SELECT * FROM PurchaseItems WHERE PurchaseId = @Id ORDER BY LineNo, Id", ("@Id", id)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) bill.Lines.Add(MapLine(r));
        }
        return bill;
    }

    public async Task<IReadOnlyList<PurchaseListRow>> ListAsync(PurchaseListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var where = new List<string> { "p.PurchaseDate BETWEEN @From AND @To" };
        var parameters = new List<(string, object?)> { ("@From", Date(filter.From)), ("@To", Date(filter.To)), ("@Today", Date(DateTime.Today)) };
        if (filter.SupplierId is long supplierId)
        {
            where.Add("p.SupplierId = @S");
            parameters.Add(("@S", supplierId));
        }
        var confirmed = (int)PurchaseStatus.Confirmed;
        where.Add(filter.Status switch
        {
            PurchaseListStatusFilter.Draft => $"p.Status = {(int)PurchaseStatus.Draft}",
            PurchaseListStatusFilter.Cancelled => $"p.Status = {(int)PurchaseStatus.Cancelled}",
            PurchaseListStatusFilter.Paid => $"p.Status = {confirmed} AND p.PaymentStatus = {(int)PaymentStatus.Paid}",
            PurchaseListStatusFilter.Partial => $"p.Status = {confirmed} AND p.PaymentStatus = {(int)PaymentStatus.Partial}",
            PurchaseListStatusFilter.Unpaid => $"p.Status = {confirmed} AND p.PaymentStatus = {(int)PaymentStatus.Unpaid}",
            PurchaseListStatusFilter.Overdue => $"p.Status = {confirmed} AND p.BalancePaisa > 0 AND p.DueDate IS NOT NULL AND p.DueDate < @Today",
            _ => "1 = 1"
        });
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            where.Add(@"(p.SupplierInvoiceNumber LIKE @Q ESCAPE '\' OR p.PurchaseNumber LIKE @Q ESCAPE '\' OR p.SupplierName LIKE @Q ESCAPE '\'
                        OR EXISTS (SELECT 1 FROM PurchaseItems i WHERE i.PurchaseId = p.Id AND (i.MedicineName LIKE @Q ESCAPE '\' OR i.BatchNumber LIKE @Q ESCAPE '\')))");
            parameters.Add(("@Q", Like(filter.Search)));
        }

        var sql = $@"
            SELECT p.*, (SELECT COUNT(*) FROM PurchaseItems i WHERE i.PurchaseId = p.Id) AS ItemCount
            FROM Purchases p WHERE {string.Join(" AND ", where)}
            ORDER BY p.PurchaseDate DESC, p.Id DESC LIMIT 5000";
        using var conn = _db.CreateNewConnection();
        using var cmd = Command(conn, null, sql, parameters.ToArray());
        var list = new List<PurchaseListRow>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(MapRow(r));
        return list;
    }

    public async Task<PurchaseListRow?> FindDuplicateAsync(long supplierId, string supplierInvoiceNumber, long excludeId = 0)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = Command(conn, null, $@"
            SELECT p.*, (SELECT COUNT(*) FROM PurchaseItems i WHERE i.PurchaseId = p.Id) AS ItemCount FROM Purchases p
            WHERE p.SupplierId = @S AND UPPER(TRIM(p.SupplierInvoiceNumber)) = UPPER(TRIM(@N))
              AND p.Status <> {(int)PurchaseStatus.Cancelled} AND p.Id <> @Ex
            ORDER BY p.Id LIMIT 1",
            ("@S", supplierId), ("@N", supplierInvoiceNumber), ("@Ex", excludeId));
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapRow(r) : null;
    }

    public async Task<PurchaseBillLine?> GetLastLineForMedicineAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = Command(conn, null, $@"
            SELECT i.* FROM PurchaseItems i JOIN Purchases p ON p.Id = i.PurchaseId
            WHERE i.MedicineId = @M AND p.Status = {(int)PurchaseStatus.Confirmed}
            ORDER BY p.PurchaseDate DESC, i.Id DESC LIMIT 1", ("@M", medicineId));
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapLine(r) : null;
    }

    public async Task<IReadOnlyList<PurchasePaymentRecord>> GetPaymentsAsync(long purchaseId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = Command(conn, null, $@"
            SELECT * FROM Payments WHERE TransactionId = @Id AND PaymentType IN ({SupplierPaymentTypes}) ORDER BY PaymentDate, Id",
            ("@Id", purchaseId));
        var list = new List<PurchasePaymentRecord>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new PurchasePaymentRecord(r.Long("Id"), purchaseId, r.DateValue("PaymentDate"), r.Long("AmountPaisa"),
                (PaymentMethod)r.Int("PaymentMethod"), r.StrOrNull("ReferenceNumber"), r.StrOrNull("Notes")));
        }
        return list;
    }

    // ───────────────────────── Drafts ─────────────────────────

    public async Task<long> SaveDraftAsync(PurchaseBill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            if (bill.Id > 0) await RequireStatusAsync(conn, tx, bill.Id, PurchaseStatus.Draft, "Only a draft can be saved as a draft.");
            bill.Status = PurchaseStatus.Draft;
            var id = await UpsertHeaderAsync(conn, tx, bill);
            await ExecAsync(conn, tx, "DELETE FROM PurchaseItems WHERE PurchaseId = @Id", ("@Id", id));
            foreach (var line in bill.Lines) await InsertLineAsync(conn, tx, id, line, batchId: null);
            tx.Commit();
            bill.Id = id;
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> DeleteDraftAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await RequireStatusAsync(conn, tx, id, PurchaseStatus.Draft, "Only drafts can be deleted. Cancel a confirmed purchase instead.");
            await ExecAsync(conn, tx, "DELETE FROM PurchaseItems WHERE PurchaseId = @Id", ("@Id", id));
            var deleted = await ExecAsync(conn, tx, "DELETE FROM Purchases WHERE Id = @Id", ("@Id", id)) > 0;
            tx.Commit();
            return deleted;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ───────────────────────── Posting ─────────────────────────

    public async Task<PurchasePostingResult> ConfirmAsync(PurchaseBill bill, PurchasePaymentInput? initialPayment, long? userId)
    {
        ArgumentNullException.ThrowIfNull(bill);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        var originalNumber = bill.PurchaseNumber;
        var originalStatus = bill.Status;
        try
        {
            if (bill.Id > 0)
                await RequireStatusAsync(conn, tx, bill.Id, PurchaseStatus.Draft, "This purchase is already confirmed or cancelled.");

            if (string.IsNullOrWhiteSpace(bill.PurchaseNumber))
                bill.PurchaseNumber = await DocumentSequenceAllocator.AllocateAsync(conn, tx, DocumentType.Purchase, bill.InvoiceDate);
            bill.Status = PurchaseStatus.Confirmed;
            bill.ConfirmedAt = DateTime.UtcNow;

            var id = await UpsertHeaderAsync(conn, tx, bill);
            await ExecAsync(conn, tx, "DELETE FROM PurchaseItems WHERE PurchaseId = @Id", ("@Id", id));
            var batchIds = await PostLinesAsync(conn, tx, bill, id, userId);

            if (initialPayment is { AmountPaisa: > 0 })
                await InsertPaymentAsync(conn, tx, id, bill.PurchaseNumber!, bill.SupplierInvoiceNumber, initialPayment, userId, "Paid with purchase bill");

            tx.Commit();
            bill.Id = id;
            return new PurchasePostingResult(id, bill.PurchaseNumber!, batchIds);
        }
        catch
        {
            tx.Rollback();
            bill.PurchaseNumber = originalNumber;
            bill.Status = originalStatus;
            bill.ConfirmedAt = null;
            throw;
        }
    }

    public async Task<PurchasePostingResult> UpdateConfirmedAsync(PurchaseBill bill, long? userId)
    {
        ArgumentNullException.ThrowIfNull(bill);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var current = await LoadAsync(conn, tx, bill.Id)
                ?? throw new PurchaseRuleException($"Purchase #{bill.Id} was not found.");
            if (current.Status != PurchaseStatus.Confirmed)
                throw new PurchaseRuleException("Only a confirmed purchase can be edited this way.");
            await RequireNoDebitNotesAsync(conn, tx, current.Id);

            if (bill.GrandTotalPaisa < current.AmountPaidPaisa)
                throw new PurchaseRuleException(
                    $"The new bill total (₹{bill.GrandTotalPaisa / 100m:N2}) is less than the ₹{current.AmountPaidPaisa / 100m:N2} already paid.");

            var reference = current.PurchaseNumber ?? $"#{current.Id}";
            await ReverseLinesAsync(conn, tx, current, userId, $"Purchase {reference} edited",
                "The purchase cannot be edited after this stock was sold or returned.");
            await ExecAsync(conn, tx, "DELETE FROM PurchaseItems WHERE PurchaseId = @Id", ("@Id", current.Id));

            bill.PurchaseNumber = current.PurchaseNumber;
            bill.Status = PurchaseStatus.Confirmed;
            bill.ConfirmedAt = current.ConfirmedAt;
            bill.CreatedAt = current.CreatedAt;
            bill.CreatedByUserId = current.CreatedByUserId;
            bill.AmountPaidPaisa = current.AmountPaidPaisa;
            bill.BalancePaisa = bill.GrandTotalPaisa - current.AmountPaidPaisa;
            bill.PaymentStatus = PurchaseCalculator.PaymentStatusFor(bill.GrandTotalPaisa, current.AmountPaidPaisa);

            await UpsertHeaderAsync(conn, tx, bill);
            var batchIds = await PostLinesAsync(conn, tx, bill, bill.Id, userId);
            tx.Commit();
            return new PurchasePostingResult(bill.Id, bill.PurchaseNumber ?? reference, batchIds);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task CancelConfirmedAsync(long purchaseId, string reason, long? userId)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var current = await LoadAsync(conn, tx, purchaseId)
                ?? throw new PurchaseRuleException($"Purchase #{purchaseId} was not found.");
            if (current.Status != PurchaseStatus.Confirmed)
                throw new PurchaseRuleException("Only a confirmed purchase can be cancelled.");
            if (current.AmountPaidPaisa > 0)
                throw new PurchaseRuleException(
                    $"₹{current.AmountPaidPaisa / 100m:N2} has been paid against this purchase, so it cannot be deleted. Record a purchase return instead.");
            await RequireNoDebitNotesAsync(conn, tx, purchaseId);

            var reference = current.PurchaseNumber ?? $"#{current.Id}";
            await ReverseLinesAsync(conn, tx, current, userId, $"Purchase {reference} cancelled: {reason}",
                "The purchase cannot be deleted after this stock was sold or returned. Record a purchase return instead.");

            var notes = string.IsNullOrWhiteSpace(current.Notes) ? $"Cancelled: {reason}" : $"{current.Notes}\nCancelled: {reason}";
            await ExecAsync(conn, tx, "UPDATE Purchases SET Status = @St, Notes = @N, UpdatedAt = @U WHERE Id = @Id",
                ("@St", (int)PurchaseStatus.Cancelled), ("@N", notes), ("@U", Stamp(DateTime.UtcNow)), ("@Id", purchaseId));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ───────────────────────── Payments ─────────────────────────

    public async Task<long> RecordPaymentAsync(long purchaseId, PurchasePaymentInput payment, long? userId)
    {
        ArgumentNullException.ThrowIfNull(payment);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var current = await LoadAsync(conn, tx, purchaseId) ?? throw new PurchaseRuleException($"Purchase #{purchaseId} was not found.");
            if (current.Status != PurchaseStatus.Confirmed) throw new PurchaseRuleException("Payments can only be recorded against a confirmed purchase.");
            if (payment.AmountPaisa <= 0) throw new PurchaseRuleException("Payment amount must be more than zero.");
            if (payment.AmountPaisa > current.BalancePaisa)
                throw new PurchaseRuleException($"Payment is more than the balance due (₹{current.BalancePaisa / 100m:N2}).");

            var id = await InsertPaymentAsync(conn, tx, purchaseId, current.PurchaseNumber ?? $"#{purchaseId}", current.SupplierInvoiceNumber,
                payment, userId, "Supplier payment");
            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<PaymentAllocation>> RecordSupplierPaymentAsync(long supplierId, PurchasePaymentInput payment, long? userId)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (payment.AmountPaisa <= 0) throw new PurchaseRuleException("Payment amount must be more than zero.");
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var open = new List<(long Id, string Number, string Invoice, long Balance)>();
            using (var cmd = Command(conn, tx, $@"
                SELECT Id, PurchaseNumber, SupplierInvoiceNumber, BalancePaisa FROM Purchases
                WHERE SupplierId = @S AND Status = {(int)PurchaseStatus.Confirmed} AND BalancePaisa > 0
                ORDER BY COALESCE(DueDate, PurchaseDate), PurchaseDate, Id", ("@S", supplierId)))
            using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                    open.Add((r.Long("Id"), r.StrOrNull("PurchaseNumber") ?? $"#{r.Long("Id")}", r.Str("SupplierInvoiceNumber"), r.Long("BalancePaisa")));
            }

            var due = open.Sum(o => o.Balance);
            if (payment.AmountPaisa > due)
                throw new PurchaseRuleException($"Payment is more than the unpaid bills of this distributor (₹{due / 100m:N2}).");

            var allocations = new List<PaymentAllocation>();
            var left = payment.AmountPaisa;
            foreach (var bill in open)
            {
                if (left == 0) break;
                var share = Math.Min(left, bill.Balance);
                await InsertPaymentAsync(conn, tx, bill.Id, bill.Number, bill.Invoice, payment with { AmountPaisa = share }, userId, "Supplier payment (on account)");
                allocations.Add(new PaymentAllocation(bill.Id, bill.Number, bill.Invoice, share));
                left -= share;
            }
            tx.Commit();
            return allocations;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Payments row + cached paid/balance/status on the purchase (same transaction, so they never drift).</summary>
    private static async Task<long> InsertPaymentAsync(SqliteConnection conn, SqliteTransaction tx, long purchaseId, string purchaseNumber,
        string supplierInvoice, PurchasePaymentInput payment, long? userId, string note)
    {
        var paymentId = Convert.ToInt64(await ScalarAsync(conn, tx, @"
            INSERT INTO Payments (TransactionId, TransactionReference, PaymentDate, PaymentType, PaymentMethod, AmountPaisa,
                ReferenceNumber, Notes, CreatedByUserId, CreatedAt)
            VALUES (@T, @TRef, @Date, @Type, @Method, @Amt, @Ref, @Notes, @User, @Now);
            SELECT last_insert_rowid();",
            ("@T", purchaseId), ("@TRef", purchaseNumber), ("@Date", payment.Date.ToString(DateTimeFormat)),
            ("@Type", (int)PaymentType.SupplierPayment), ("@Method", (int)payment.Method), ("@Amt", payment.AmountPaisa),
            ("@Ref", OrNull(payment.Reference)), ("@Notes", $"{note}: bill {supplierInvoice}"), ("@User", OrNull(userId)),
            ("@Now", Stamp(DateTime.UtcNow))));

        using (var cmd = Command(conn, tx, "SELECT GrandTotalPaisa, AmountPaidPaisa FROM Purchases WHERE Id = @Id", ("@Id", purchaseId)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            await r.ReadAsync();
            var grand = r.Long("GrandTotalPaisa");
            var paid = r.Long("AmountPaidPaisa") + payment.AmountPaisa;
            r.Close();
            await ExecAsync(conn, tx, "UPDATE Purchases SET AmountPaidPaisa = @P, BalancePaisa = @B, PaymentStatus = @PS, UpdatedAt = @U WHERE Id = @Id",
                ("@P", paid), ("@B", grand - paid), ("@PS", (int)PurchaseCalculator.PaymentStatusFor(grand, paid)),
                ("@U", Stamp(DateTime.UtcNow)), ("@Id", purchaseId));
        }
        return paymentId;
    }

    // ───────────────────────── Import mappings ─────────────────────────

    private static string HeaderKey(IReadOnlyList<string> headers) =>
        string.Join("|", headers.Select(h => h.Trim().ToLowerInvariant()));

    public async Task<IReadOnlyList<PurchaseImportField>?> GetImportMappingAsync(long supplierId, IReadOnlyList<string> headers)
    {
        using var conn = _db.CreateNewConnection();
        var text = await ScalarAsync(conn, null, "SELECT Mapping FROM PurchaseImportMappings WHERE SupplierId = @S AND HeaderKey = @K",
            ("@S", supplierId), ("@K", HeaderKey(headers))) as string;
        if (string.IsNullOrEmpty(text)) return null;
        var parts = text.Split(',');
        if (parts.Length != headers.Count) return null;
        var mapping = new List<PurchaseImportField>();
        foreach (var part in parts)
        {
            if (!Enum.TryParse<PurchaseImportField>(part, out var field)) return null;
            mapping.Add(field);
        }
        return mapping;
    }

    public async Task SaveImportMappingAsync(long supplierId, IReadOnlyList<string> headers, IReadOnlyList<PurchaseImportField> mapping)
    {
        using var conn = _db.CreateNewConnection();
        await ExecAsync(conn, null, @"
            INSERT INTO PurchaseImportMappings (SupplierId, HeaderKey, Mapping, UpdatedAt) VALUES (@S, @K, @M, @U)
            ON CONFLICT(SupplierId, HeaderKey) DO UPDATE SET Mapping = excluded.Mapping, UpdatedAt = excluded.UpdatedAt;",
            ("@S", supplierId), ("@K", HeaderKey(headers)), ("@M", string.Join(",", mapping)), ("@U", Stamp(DateTime.UtcNow)));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static async Task RequireStatusAsync(SqliteConnection conn, SqliteTransaction tx, long id, PurchaseStatus status, string message)
    {
        var current = await ScalarAsync(conn, tx, "SELECT Status FROM Purchases WHERE Id = @Id", ("@Id", id))
            ?? throw new PurchaseRuleException($"Purchase #{id} was not found.");
        if (Convert.ToInt32(current) != (int)status) throw new PurchaseRuleException(message);
    }

    private static async Task RequireNoDebitNotesAsync(SqliteConnection conn, SqliteTransaction tx, long purchaseId)
    {
        var numbers = new List<string>();
        using (var cmd = Command(conn, tx, @"
            SELECT DISTINCT r.ReturnNumber FROM PurchaseReturns r
            WHERE r.IsCancelled = 0 AND (r.PurchaseId = @Id OR EXISTS (SELECT 1 FROM PurchaseReturnItems ri
                JOIN PurchaseItems i ON i.Id = ri.PurchaseItemId WHERE ri.PurchaseReturnId = r.Id AND i.PurchaseId = @Id))",
            ("@Id", purchaseId)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) numbers.Add(r.GetString(0));
        }
        if (numbers.Count > 0)
            throw new PurchaseRuleException($"Debit note(s) {string.Join(", ", numbers)} were made against this purchase. Cancel them first.");
    }

    private static async Task<IReadOnlyList<long>> PostLinesAsync(SqliteConnection conn, SqliteTransaction tx, PurchaseBill bill, long purchaseId, long? userId)
    {
        var batchIds = new List<long>();
        var reference = $"Purchase:{purchaseId}";
        foreach (var line in bill.Lines)
        {
            var (batchId, balance) = await PurchaseStockLedger.ReceiveAsync(conn, tx, bill, line);
            line.BatchId = batchId;
            await InsertLineAsync(conn, tx, purchaseId, line, batchId);
            var reason = $"Purchase {bill.PurchaseNumber} (bill {bill.SupplierInvoiceNumber}, {bill.SupplierName})"
                         + (line.FreeQuantity > 0 ? $" incl. {line.FreeQuantity} free" : "");
            await PurchaseStockLedger.InsertMovementAsync(conn, tx, DateTime.Now, line.MedicineId, line.MedicineName, batchId,
                line.BatchNumber.Trim(), MovementType.PurchaseReceipt, line.Quantity + line.FreeQuantity, balance, reference, reason, userId);
            batchIds.Add(batchId);
        }
        return batchIds;
    }

    private static async Task ReverseLinesAsync(SqliteConnection conn, SqliteTransaction tx, PurchaseBill current, long? userId, string reason, string whenShort)
    {
        foreach (var line in current.Lines)
        {
            var units = line.Quantity + line.FreeQuantity;
            if (units <= 0) continue;
            var batchId = line.BatchId ?? Convert.ToInt64(await ScalarAsync(conn, tx,
                "SELECT Id FROM MedicineBatches WHERE MedicineId = @M AND BatchNumber = @B ORDER BY Id LIMIT 1",
                ("@M", line.MedicineId), ("@B", line.BatchNumber)) ?? throw new PurchaseRuleException(
                    $"The stock batch {line.BatchNumber} of {line.MedicineName} no longer exists. {whenShort}"));
            var balance = await PurchaseStockLedger.TakeOutAsync(conn, tx, batchId, units, line.FreeQuantity, receivedToo: true,
                line.MedicineName, line.BatchNumber, whenShort);
            await PurchaseStockLedger.InsertMovementAsync(conn, tx, DateTime.Now, line.MedicineId, line.MedicineName, batchId,
                line.BatchNumber, MovementType.StockAdjustment, -units, balance, $"Purchase:{current.Id}", reason, userId);
        }
    }

    private static async Task<long> UpsertHeaderAsync(SqliteConnection conn, SqliteTransaction tx, PurchaseBill b)
    {
        var now = DateTime.UtcNow;
        b.UpdatedAt = now;
        var p = new List<(string, object?)>
        {
            ("@SupplierInvoiceNumber", b.SupplierInvoiceNumber.Trim()), ("@SupplierId", b.SupplierId), ("@SupplierName", b.SupplierName),
            ("@PurchaseDate", Date(b.InvoiceDate)), ("@SubtotalPaisa", b.SubtotalPaisa), ("@DiscountAmountPaisa", b.DiscountAmountPaisa),
            ("@TaxAmountPaisa", b.TaxAmountPaisa), ("@GrandTotalPaisa", b.GrandTotalPaisa), ("@AmountPaidPaisa", b.AmountPaidPaisa),
            ("@BalancePaisa", b.BalancePaisa), ("@Status", (int)b.Status), ("@PaymentStatus", (int)b.PaymentStatus),
            ("@Notes", OrNull(b.Notes)), ("@CreatedByUserId", OrNull(b.CreatedByUserId)), ("@CreatedAt", Stamp(b.CreatedAt)),
            ("@UpdatedAt", Stamp(now)), ("@PurchaseNumber", OrNull(b.PurchaseNumber)), ("@DueDate", DateOrNull(b.DueDate)),
            ("@WithGst", b.WithGst ? 1 : 0), ("@RatesIncludeGst", b.RatesIncludeGst ? 1 : 0), ("@IsInterState", b.IsInterState ? 1 : 0),
            ("@BillDiscountPercent", b.BillDiscountPercent), ("@OtherChargesPaisa", b.OtherChargesPaisa),
            ("@OtherChargesLabel", OrNull(b.OtherChargesLabel)), ("@TaxablePaisa", b.TaxablePaisa), ("@CgstPaisa", b.CgstPaisa),
            ("@SgstPaisa", b.SgstPaisa), ("@IgstPaisa", b.IgstPaisa), ("@RoundOffPaisa", b.RoundOffPaisa),
            ("@ImportSource", OrNull(b.ImportSource)), ("@ConfirmedAt", b.ConfirmedAt.HasValue ? Stamp(b.ConfirmedAt.Value) : DBNull.Value)
        };

        if (b.Id > 0)
        {
            p.Add(("@Id", b.Id));
            await ExecAsync(conn, tx, @"
                UPDATE Purchases SET SupplierInvoiceNumber = @SupplierInvoiceNumber, SupplierId = @SupplierId, SupplierName = @SupplierName,
                    PurchaseDate = @PurchaseDate, SubtotalPaisa = @SubtotalPaisa, DiscountAmountPaisa = @DiscountAmountPaisa,
                    TaxAmountPaisa = @TaxAmountPaisa, GrandTotalPaisa = @GrandTotalPaisa, AmountPaidPaisa = @AmountPaidPaisa,
                    BalancePaisa = @BalancePaisa, Status = @Status, PaymentStatus = @PaymentStatus, Notes = @Notes, UpdatedAt = @UpdatedAt,
                    PurchaseNumber = @PurchaseNumber, DueDate = @DueDate, WithGst = @WithGst, RatesIncludeGst = @RatesIncludeGst,
                    IsInterState = @IsInterState, BillDiscountPercent = @BillDiscountPercent, OtherChargesPaisa = @OtherChargesPaisa,
                    OtherChargesLabel = @OtherChargesLabel, TaxablePaisa = @TaxablePaisa, CgstPaisa = @CgstPaisa, SgstPaisa = @SgstPaisa,
                    IgstPaisa = @IgstPaisa, RoundOffPaisa = @RoundOffPaisa, ImportSource = @ImportSource, ConfirmedAt = @ConfirmedAt
                WHERE Id = @Id", p.ToArray());
            return b.Id;
        }

        b.CreatedAt = now;
        p[p.FindIndex(x => x.Item1 == "@CreatedAt")] = ("@CreatedAt", Stamp(now));
        return Convert.ToInt64(await ScalarAsync(conn, tx, @"
            INSERT INTO Purchases (SupplierInvoiceNumber, SupplierId, SupplierName, PurchaseDate, SubtotalPaisa, DiscountAmountPaisa,
                TaxAmountPaisa, GrandTotalPaisa, AmountPaidPaisa, BalancePaisa, Status, PaymentStatus, Notes, CreatedByUserId, CreatedAt,
                UpdatedAt, PurchaseNumber, DueDate, WithGst, RatesIncludeGst, IsInterState, BillDiscountPercent, OtherChargesPaisa,
                OtherChargesLabel, TaxablePaisa, CgstPaisa, SgstPaisa, IgstPaisa, RoundOffPaisa, ImportSource, ConfirmedAt)
            VALUES (@SupplierInvoiceNumber, @SupplierId, @SupplierName, @PurchaseDate, @SubtotalPaisa, @DiscountAmountPaisa,
                @TaxAmountPaisa, @GrandTotalPaisa, @AmountPaidPaisa, @BalancePaisa, @Status, @PaymentStatus, @Notes, @CreatedByUserId, @CreatedAt,
                @UpdatedAt, @PurchaseNumber, @DueDate, @WithGst, @RatesIncludeGst, @IsInterState, @BillDiscountPercent, @OtherChargesPaisa,
                @OtherChargesLabel, @TaxablePaisa, @CgstPaisa, @SgstPaisa, @IgstPaisa, @RoundOffPaisa, @ImportSource, @ConfirmedAt);
            SELECT last_insert_rowid();", p.ToArray()));
    }

    private static async Task InsertLineAsync(SqliteConnection conn, SqliteTransaction tx, long purchaseId, PurchaseBillLine l, long? batchId)
    {
        l.PurchaseId = purchaseId;
        l.Id = Convert.ToInt64(await ScalarAsync(conn, tx, @"
            INSERT INTO PurchaseItems (PurchaseId, LineNo, MedicineId, MedicineName, HsnCode, BatchNumber, ExpiryDate, ManufacturingDate,
                UnitsPerPack, Quantity, FreeQuantity, EnteredRatePaisa, PurchasePricePaisa, PtrPaisa, MrpPaisa, SellingPricePaisa,
                DiscountPercent, SchemePercent, SchemeQuantity, SchemeFreeQuantity, GstRatePercent, DiscountAmountPaisa, TaxablePaisa,
                GstAmountPaisa, CgstPaisa, SgstPaisa, IgstPaisa, LineTotalPaisa, NetRatePaisa, NetPurchaseRatePaisa, MarginPercent, BatchId)
            VALUES (@PurchaseId, @LineNo, @MedicineId, @MedicineName, @HsnCode, @BatchNumber, @ExpiryDate, @ManufacturingDate,
                @UnitsPerPack, @Quantity, @FreeQuantity, @EnteredRatePaisa, @PurchasePricePaisa, @PtrPaisa, @MrpPaisa, @SellingPricePaisa,
                @DiscountPercent, @SchemePercent, @SchemeQuantity, @SchemeFreeQuantity, @GstRatePercent, @DiscountAmountPaisa, @TaxablePaisa,
                @GstAmountPaisa, @CgstPaisa, @SgstPaisa, @IgstPaisa, @LineTotalPaisa, @NetRatePaisa, @NetPurchaseRatePaisa, @MarginPercent, @BatchId);
            SELECT last_insert_rowid();",
            ("@PurchaseId", purchaseId), ("@LineNo", l.LineNo), ("@MedicineId", l.MedicineId), ("@MedicineName", l.MedicineName),
            ("@HsnCode", OrNull(l.HsnCode)), ("@BatchNumber", l.BatchNumber.Trim()), ("@ExpiryDate", Date(l.ExpiryDate)),
            ("@ManufacturingDate", DateOrNull(l.ManufacturingDate)), ("@UnitsPerPack", l.UnitsPerPack), ("@Quantity", l.Quantity),
            ("@FreeQuantity", l.FreeQuantity), ("@EnteredRatePaisa", l.EnteredRatePaisa), ("@PurchasePricePaisa", l.PurchaseRatePaisa),
            ("@PtrPaisa", l.PtrPaisa), ("@MrpPaisa", l.MrpPaisa), ("@SellingPricePaisa", l.SaleRatePaisa),
            ("@DiscountPercent", l.DiscountPercent), ("@SchemePercent", l.SchemePercent), ("@SchemeQuantity", l.SchemeQuantity),
            ("@SchemeFreeQuantity", l.SchemeFreeQuantity), ("@GstRatePercent", l.GstRatePercent), ("@DiscountAmountPaisa", l.DiscountAmountPaisa),
            ("@TaxablePaisa", l.TaxablePaisa), ("@GstAmountPaisa", l.GstAmountPaisa), ("@CgstPaisa", l.CgstPaisa), ("@SgstPaisa", l.SgstPaisa),
            ("@IgstPaisa", l.IgstPaisa), ("@LineTotalPaisa", l.LineTotalPaisa), ("@NetRatePaisa", l.NetRatePaisa),
            ("@NetPurchaseRatePaisa", l.NetPurchaseRatePaisa), ("@MarginPercent", l.MarginPercent), ("@BatchId", OrNull(batchId))));
    }

    private static PurchaseBill MapHeader(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        PurchaseNumber = r.StrOrNull("PurchaseNumber"),
        SupplierId = r.Long("SupplierId"),
        SupplierName = r.Str("SupplierName"),
        SupplierGstin = r.StrOrNull("SupplierGstin"),
        SupplierInvoiceNumber = r.Str("SupplierInvoiceNumber"),
        InvoiceDate = r.DateValue("PurchaseDate"),
        DueDate = r.DateOrNullValue("DueDate"),
        WithGst = r.Bool("WithGst"),
        RatesIncludeGst = r.Bool("RatesIncludeGst"),
        IsInterState = r.Bool("IsInterState"),
        BillDiscountPercent = r.Dec("BillDiscountPercent"),
        OtherChargesPaisa = r.Long("OtherChargesPaisa"),
        OtherChargesLabel = r.StrOrNull("OtherChargesLabel"),
        SubtotalPaisa = r.Long("SubtotalPaisa"),
        DiscountAmountPaisa = r.Long("DiscountAmountPaisa"),
        TaxablePaisa = r.Long("TaxablePaisa"),
        CgstPaisa = r.Long("CgstPaisa"),
        SgstPaisa = r.Long("SgstPaisa"),
        IgstPaisa = r.Long("IgstPaisa"),
        TaxAmountPaisa = r.Long("TaxAmountPaisa"),
        RoundOffPaisa = r.Long("RoundOffPaisa"),
        GrandTotalPaisa = r.Long("GrandTotalPaisa"),
        AmountPaidPaisa = r.Long("AmountPaidPaisa"),
        BalancePaisa = r.Long("BalancePaisa"),
        PaymentStatus = (PaymentStatus)r.Int("PaymentStatus"),
        Status = (PurchaseStatus)r.Int("Status"),
        ImportSource = r.StrOrNull("ImportSource"),
        Notes = r.StrOrNull("Notes"),
        CreatedByUserId = r.LongOrNull("CreatedByUserId"),
        ConfirmedAt = r.DateOrNullValue("ConfirmedAt"),
        CreatedAt = r.DateValue("CreatedAt"),
        UpdatedAt = r.DateValue("UpdatedAt")
    };

    private static PurchaseBillLine MapLine(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        PurchaseId = r.Long("PurchaseId"),
        LineNo = r.Int("LineNo"),
        MedicineId = r.Long("MedicineId"),
        MedicineName = r.Str("MedicineName"),
        HsnCode = r.StrOrNull("HsnCode"),
        BatchNumber = r.Str("BatchNumber"),
        ExpiryDate = r.DateValue("ExpiryDate"),
        ManufacturingDate = r.DateOrNullValue("ManufacturingDate"),
        UnitsPerPack = Math.Max(1, r.Int("UnitsPerPack")),
        Quantity = r.Int("Quantity"),
        FreeQuantity = r.Int("FreeQuantity"),
        EnteredRatePaisa = r.Long("EnteredRatePaisa"),
        PurchaseRatePaisa = r.Long("PurchasePricePaisa"),
        PtrPaisa = r.Long("PtrPaisa"),
        MrpPaisa = r.Long("MrpPaisa"),
        SaleRatePaisa = r.Long("SellingPricePaisa"),
        DiscountPercent = r.Dec("DiscountPercent"),
        SchemePercent = r.Dec("SchemePercent"),
        SchemeQuantity = r.Int("SchemeQuantity"),
        SchemeFreeQuantity = r.Int("SchemeFreeQuantity"),
        GstRatePercent = r.Dec("GstRatePercent"),
        DiscountAmountPaisa = r.Long("DiscountAmountPaisa"),
        TaxablePaisa = r.Long("TaxablePaisa"),
        ValueBeforeDiscountPaisa = r.Long("TaxablePaisa") + r.Long("DiscountAmountPaisa"),
        GstAmountPaisa = r.Long("GstAmountPaisa"),
        CgstPaisa = r.Long("CgstPaisa"),
        SgstPaisa = r.Long("SgstPaisa"),
        IgstPaisa = r.Long("IgstPaisa"),
        LineTotalPaisa = r.Long("LineTotalPaisa"),
        NetRatePaisa = r.Long("NetRatePaisa"),
        NetPurchaseRatePaisa = r.Long("NetPurchaseRatePaisa"),
        MarginPercent = r.Dec("MarginPercent"),
        BatchId = r.LongOrNull("BatchId")
    };

    private static PurchaseListRow MapRow(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        PurchaseNumber = r.StrOrNull("PurchaseNumber"),
        SupplierInvoiceNumber = r.Str("SupplierInvoiceNumber"),
        InvoiceDate = r.DateValue("PurchaseDate"),
        DueDate = r.DateOrNullValue("DueDate"),
        SupplierId = r.Long("SupplierId"),
        SupplierName = r.Str("SupplierName"),
        ItemCount = r.Int("ItemCount"),
        TaxablePaisa = r.Long("TaxablePaisa"),
        TaxAmountPaisa = r.Long("TaxAmountPaisa"),
        GrandTotalPaisa = r.Long("GrandTotalPaisa"),
        AmountPaidPaisa = r.Long("AmountPaidPaisa"),
        BalancePaisa = r.Long("BalancePaisa"),
        Status = (PurchaseStatus)r.Int("Status"),
        PaymentStatus = (PaymentStatus)r.Int("PaymentStatus"),
        ImportSource = r.StrOrNull("ImportSource")
    };
}
