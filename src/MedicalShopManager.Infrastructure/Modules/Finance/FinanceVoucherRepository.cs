using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Numbering;
using static MedicalShopManager.Infrastructure.Modules.Finance.FinanceSql;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// Receipts (Payment In) and payment vouchers (Payment Out). Posting runs in one SQLite transaction: the dues are re-read
/// inside it, allocations validated (or made oldest-first), the number allocated from DocumentSequences, the cheque and
/// voucher inserted, and each bill settled with a Payments row (so Billing / Purchases screens show the payment) plus
/// the bill's cached paid / balance / status. A rollback consumes no number and changes no bill.
/// </summary>
public sealed class FinanceVoucherRepository : IFinanceVoucherRepository
{
    private readonly DatabaseManager _db;

    public FinanceVoucherRepository(DatabaseManager db) => _db = db;

    public async Task<FinanceVoucher> PostAsync(VoucherRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        long voucherId;
        using var conn = _db.CreateNewConnection();
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                voucherId = await InsertAsync(conn, tx, request);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        return await LoadAsync(conn, null, voucherId) ?? throw new InvalidOperationException("The voucher was saved but could not be read back.");
    }

    private static async Task<long> InsertAsync(SqliteConnection conn, SqliteTransaction tx, VoucherRequest request)
    {
        var partyName = await PartyNameAsync(conn, tx, request.PartyType, request.PartyId)
            ?? throw new FinanceValidationException(request.PartyType == PartyType.Customer
                ? "The customer was not found. Choose the customer again."
                : "The supplier was not found. Choose the supplier again.");
        var isCheque = request.Mode == FinancePaymentMode.Cheque;
        if (request.AccountId is { } accountId) await FinanceAccountRepository.RequireActiveAccountAsync(conn, tx, accountId);
        else if (!isCheque) throw new FinanceValidationException("Choose the account for this payment.");

        var dues = await FinancePartyRepository.LoadDuesAsync(conn, tx, request.PartyType, request.PartyId);
        IReadOnlyList<VoucherAllocation> allocations;
        if (request.Allocations is null)
            allocations = FinanceRules.AllocateOldestFirst(request.AmountPaisa, dues);
        else
        {
            var errors = FinanceRules.ValidateAllocations(request.AmountPaisa, request.Allocations, dues);
            if (errors.Count > 0) throw new FinanceValidationException(errors);
            allocations = request.Allocations;
        }

        var docType = request.Kind == FinanceVoucherKind.Receipt ? DocumentType.Receipt : DocumentType.Payment;
        var number = await DocumentSequenceAllocator.AllocateAsync(conn, tx, docType, request.Date);
        var now = Now();

        long? chequeId = null;
        if (isCheque)
        {
            var cheque = request.Cheque ?? throw new FinanceValidationException("Enter the cheque details.");
            chequeId = await conn.LongAsync(tx, @"
                INSERT INTO FinanceCheques (Direction, ChequeNumber, ChequeDate, BankName, AmountPaisa, PartyType, PartyId, PartyName,
                    Status, AccountId, CreatedByUserId, CreatedAt, UpdatedAt)
                VALUES (@Dir, @No, @Date, @Bank, @Amt, @PT, @PID, @PName, @Pending, @Acc, @User, @Now, @Now);
                SELECT last_insert_rowid();",
                ("@Dir", (int)(request.Kind == FinanceVoucherKind.Receipt ? ChequeDirection.Received : ChequeDirection.Issued)),
                ("@No", cheque.ChequeNumber.Trim()), ("@Date", D(cheque.ChequeDate)), ("@Bank", Blank(cheque.BankName)),
                ("@Amt", request.AmountPaisa), ("@PT", (int)request.PartyType), ("@PID", request.PartyId), ("@PName", partyName),
                ("@Pending", (int)ChequeStatus.Pending), ("@Acc", request.AccountId), ("@User", request.UserId), ("@Now", now));
        }

        var voucherId = await conn.LongAsync(tx, @"
            INSERT INTO FinanceVouchers (VoucherNumber, Kind, VoucherDate, PartyType, PartyId, PartyName, Mode, AccountId, AmountPaisa,
                Reference, Notes, ChequeId, Status, CreatedByUserId, CreatedAt)
            VALUES (@No, @Kind, @Date, @PT, @PID, @PName, @Mode, @Acc, @Amt, @Ref, @Notes, @Cheque, @Active, @User, @Now);
            SELECT last_insert_rowid();",
            ("@No", number), ("@Kind", (int)request.Kind), ("@Date", D(request.Date)), ("@PT", (int)request.PartyType),
            ("@PID", request.PartyId), ("@PName", partyName), ("@Mode", (int)request.Mode), ("@Acc", isCheque ? null : request.AccountId),
            ("@Amt", request.AmountPaisa), ("@Ref", Blank(request.Reference)), ("@Notes", Blank(request.Notes)), ("@Cheque", chequeId),
            ("@Active", (int)FinanceVoucherStatus.Active), ("@User", request.UserId), ("@Now", now));
        if (chequeId is not null)
            await conn.ExecAsync(tx, "UPDATE FinanceCheques SET VoucherId = @V WHERE Id = @C", ("@V", voucherId), ("@C", chequeId));

        var paymentDate = request.Date.Date.Add(DateTime.Now.TimeOfDay).ToString(DateTimeFormat);
        foreach (var allocation in allocations.Where(a => a.AmountPaisa > 0))
        {
            var paymentId = await ApplyAllocationAsync(conn, tx, request, allocation, voucherId, number, isCheque ? null : request.AccountId,
                chequeId, paymentDate);
            await conn.ExecAsync(tx, @"
                INSERT INTO FinanceVoucherAllocations (VoucherId, Target, DocumentId, DocumentNumber, AmountPaisa, PaymentId)
                VALUES (@V, @T, @D, @N, @A, @P)",
                ("@V", voucherId), ("@T", (int)allocation.Target), ("@D", allocation.DocumentId), ("@N", allocation.Number),
                ("@A", allocation.AmountPaisa), ("@P", paymentId));
        }
        return voucherId;
    }

    /// <summary>Settles one bill: Payments row + the bill's cached paid / balance / status, guarded so a bill never goes below zero.</summary>
    private static async Task<long?> ApplyAllocationAsync(SqliteConnection conn, SqliteTransaction tx, VoucherRequest request,
        VoucherAllocation allocation, long voucherId, string voucherNumber, long? accountId, long? chequeId, string paymentDate)
    {
        switch (allocation.Target)
        {
            case AllocationTarget.OpeningBalance:
                return null;

            case AllocationTarget.Sale:
            {
                if (request.Kind != FinanceVoucherKind.Receipt) throw new FinanceValidationException("Sale bills are settled by receipts.");
                var changed = await conn.ExecAsync(tx, $@"
                    UPDATE Sales SET AmountPaidPaisa = AmountPaidPaisa + @A, BalanceDuePaisa = BalanceDuePaisa - @A,
                        PaymentStatus = CASE WHEN BalanceDuePaisa - @A <= 0 THEN {(int)PaymentStatus.Paid} ELSE {(int)PaymentStatus.Partial} END,
                        ModifiedAt = @Now
                    WHERE Id = @Id AND CustomerId = @Party AND Status = {FinancePartySql.SaleActive} AND BalanceDuePaisa >= @A",
                    ("@A", allocation.AmountPaisa), ("@Now", Now()), ("@Id", allocation.DocumentId), ("@Party", request.PartyId));
                if (changed != 1) throw StaleBill(allocation);
                var invoice = await conn.ScalarAsync(tx, "SELECT InvoiceNumber FROM Sales WHERE Id = @Id", ("@Id", allocation.DocumentId)) as string;
                return await InsertPaymentAsync(conn, tx, allocation.DocumentId, invoice ?? allocation.Number, PaymentType.SalePayment, request,
                    allocation.AmountPaisa, voucherId, voucherNumber, accountId, chequeId, paymentDate,
                    $"Receipt {voucherNumber} against invoice {invoice ?? allocation.Number}");
            }

            case AllocationTarget.Purchase:
            {
                if (request.Kind != FinanceVoucherKind.Payment) throw new FinanceValidationException("Purchase bills are settled by payments.");
                var changed = await conn.ExecAsync(tx, $@"
                    UPDATE Purchases SET AmountPaidPaisa = AmountPaidPaisa + @A, BalancePaisa = BalancePaisa - @A,
                        PaymentStatus = CASE WHEN BalancePaisa - @A <= 0 THEN {(int)PaymentStatus.Paid} ELSE {(int)PaymentStatus.Partial} END,
                        UpdatedAt = @Now
                    WHERE Id = @Id AND SupplierId = @Party AND Status = {FinancePartySql.PurchaseConfirmed} AND BalancePaisa >= @A",
                    ("@A", allocation.AmountPaisa), ("@Now", Now()), ("@Id", allocation.DocumentId), ("@Party", request.PartyId));
                if (changed != 1) throw StaleBill(allocation);
                string? purchaseNumber = null, supplierInvoice = null;
                using (var cmd = conn.Command(tx, "SELECT PurchaseNumber, SupplierInvoiceNumber FROM Purchases WHERE Id = @Id", ("@Id", allocation.DocumentId)))
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                    {
                        purchaseNumber = r.Str("PurchaseNumber");
                        supplierInvoice = r.Str("SupplierInvoiceNumber");
                    }
                }
                return await InsertPaymentAsync(conn, tx, allocation.DocumentId, purchaseNumber ?? $"#{allocation.DocumentId}",
                    PaymentType.SupplierPayment, request, allocation.AmountPaisa, voucherId, voucherNumber, accountId, chequeId, paymentDate,
                    $"Payment {voucherNumber}: bill {supplierInvoice ?? allocation.Number}");
            }

            default:
                throw new FinanceValidationException("Unknown document type in the allocation.");
        }
    }

    private static FinanceValidationException StaleBill(VoucherAllocation allocation) =>
        new($"{allocation.Number} changed while you were entering the payment (it may have been paid, edited or cancelled). Reload the dues and try again.");

    private static Task<long> InsertPaymentAsync(SqliteConnection conn, SqliteTransaction tx, long documentId, string documentNumber,
        PaymentType type, VoucherRequest request, long amountPaisa, long voucherId, string voucherNumber, long? accountId, long? chequeId,
        string paymentDate, string notes) =>
        conn.LongAsync(tx, @"
            INSERT INTO Payments (TransactionId, TransactionReference, PaymentDate, PaymentType, PaymentMethod, AmountPaisa, ReferenceNumber,
                Notes, CreatedByUserId, CreatedAt, AccountId, VoucherId, ChequeId, PartyType, PartyId)
            VALUES (@T, @TRef, @Date, @Type, @Method, @Amt, @Ref, @Notes, @User, @Now, @Acc, @V, @Cheque, @PT, @PID);
            SELECT last_insert_rowid();",
            ("@T", documentId), ("@TRef", documentNumber), ("@Date", paymentDate), ("@Type", (int)type),
            ("@Method", (int)request.Mode.ToPaymentMethod()), ("@Amt", amountPaisa),
            ("@Ref", Blank(request.Reference) ?? (request.Cheque is { } c ? $"Chq {c.ChequeNumber.Trim()}" : voucherNumber)),
            ("@Notes", notes), ("@User", request.UserId), ("@Now", Now()), ("@Acc", accountId), ("@V", voucherId), ("@Cheque", chequeId),
            ("@PT", (int)request.PartyType), ("@PID", request.PartyId));

    /// <summary>
    /// Reverses every live allocation of a voucher (bounce / cancel): deletes the Payments row it wrote and gives the amount
    /// back to the bill's balance. A row already removed by the bill's owner (e.g. the sale was cancelled) is skipped.
    /// </summary>
    internal static async Task ReverseAsync(SqliteConnection conn, SqliteTransaction tx, long voucherId, FinanceVoucherStatus newStatus,
        DateTime date, string reason)
    {
        var allocations = new List<(long Id, AllocationTarget Target, long DocumentId, long Amount, long? PaymentId)>();
        using (var cmd = conn.Command(tx, "SELECT * FROM FinanceVoucherAllocations WHERE VoucherId = @V AND IsReversed = 0", ("@V", voucherId)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
                allocations.Add((r.Long("Id"), (AllocationTarget)r.Int("Target"), r.Long("DocumentId"), r.Long("AmountPaisa"), r.LongOrNull("PaymentId")));
        }

        foreach (var a in allocations)
        {
            if (a.PaymentId is { } paymentId
                && await conn.ExecAsync(tx, "DELETE FROM Payments WHERE Id = @P AND VoucherId = @V", ("@P", paymentId), ("@V", voucherId)) == 1)
            {
                if (a.Target == AllocationTarget.Sale)
                    await conn.ExecAsync(tx, $@"
                        UPDATE Sales SET AmountPaidPaisa = AmountPaidPaisa - @A, BalanceDuePaisa = BalanceDuePaisa + @A,
                            PaymentStatus = CASE WHEN AmountPaidPaisa - @A <= 0 THEN {(int)PaymentStatus.Unpaid}
                                                 WHEN BalanceDuePaisa + @A <= 0 THEN {(int)PaymentStatus.Paid}
                                                 ELSE {(int)PaymentStatus.Partial} END,
                            ModifiedAt = @Now
                        WHERE Id = @Id AND Status = {FinancePartySql.SaleActive}",
                        ("@A", a.Amount), ("@Now", Now()), ("@Id", a.DocumentId));
                else if (a.Target == AllocationTarget.Purchase)
                    await conn.ExecAsync(tx, $@"
                        UPDATE Purchases SET AmountPaidPaisa = AmountPaidPaisa - @A, BalancePaisa = BalancePaisa + @A,
                            PaymentStatus = CASE WHEN AmountPaidPaisa - @A <= 0 THEN {(int)PaymentStatus.Unpaid}
                                                 WHEN BalancePaisa + @A <= 0 THEN {(int)PaymentStatus.Paid}
                                                 ELSE {(int)PaymentStatus.Partial} END,
                            UpdatedAt = @Now
                        WHERE Id = @Id AND Status = {FinancePartySql.PurchaseConfirmed}",
                        ("@A", a.Amount), ("@Now", Now()), ("@Id", a.DocumentId));
            }
            await conn.ExecAsync(tx, "UPDATE FinanceVoucherAllocations SET IsReversed = 1 WHERE Id = @Id", ("@Id", a.Id));
        }

        var changed = await conn.ExecAsync(tx, $@"
            UPDATE FinanceVouchers SET Status = @S, ReversedAt = @D, ReversalReason = @R
            WHERE Id = @Id AND Status = {(int)FinanceVoucherStatus.Active}",
            ("@S", (int)newStatus), ("@D", date.Date.Add(DateTime.Now.TimeOfDay).ToString(DateTimeFormat)), ("@R", reason.Trim()), ("@Id", voucherId));
        if (changed != 1) throw new FinanceValidationException("The receipt / payment is no longer active.");
    }

    public async Task CancelAsync(long id, string reason)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var voucher = await LoadAsync(conn, tx, id) ?? throw new FinanceValidationException("The receipt / payment was not found.");
            if (voucher.Status != FinanceVoucherStatus.Active)
                throw new FinanceValidationException($"{voucher.VoucherNumber} is already {voucher.StatusLabel.ToLowerInvariant()}.");
            if (voucher.ChequeId is { } chequeId)
            {
                if (voucher.ChequeStatus == ChequeStatus.Cleared)
                    throw new FinanceValidationException(
                        $"The cheque of {voucher.VoucherNumber} has already cleared, so the money has moved. Record a refund / reverse entry instead.");
                await conn.ExecAsync(tx, $@"
                    UPDATE FinanceCheques SET Status = {(int)ChequeStatus.Cancelled}, StatusNote = @R, UpdatedAt = @Now
                    WHERE Id = @Id AND Status IN ({(int)ChequeStatus.Pending}, {(int)ChequeStatus.Deposited})",
                    ("@R", "Payment cancelled: " + reason.Trim()), ("@Now", Now()), ("@Id", chequeId));
            }
            await ReverseAsync(conn, tx, id, FinanceVoucherStatus.Cancelled, DateTime.Today, reason);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ───────────────────────── Reads ─────────────────────────

    private const string SelectSql = @"
        SELECT v.*, a.Name AS AccountName, ch.ChequeNumber, ch.ChequeDate, ch.BankName AS ChequeBank, ch.Status AS ChequeStatus,
               u.DisplayName AS CreatedByName,
               CASE v.PartyType WHEN 1 THEN (SELECT Phone FROM Customers WHERE Id = v.PartyId)
                                WHEN 2 THEN (SELECT Phone FROM Suppliers WHERE Id = v.PartyId) END AS PartyPhone
        FROM FinanceVouchers v
        LEFT JOIN FinanceAccounts a ON a.Id = v.AccountId
        LEFT JOIN FinanceCheques ch ON ch.Id = v.ChequeId
        LEFT JOIN Users u ON u.Id = v.CreatedByUserId";

    public async Task<FinanceVoucher?> GetAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        return await LoadAsync(conn, null, id);
    }

    private static async Task<FinanceVoucher?> LoadAsync(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        FinanceVoucher? voucher;
        using (var cmd = conn.Command(tx, SelectSql + " WHERE v.Id = @Id", ("@Id", id)))
        using (var r = await cmd.ExecuteReaderAsync())
            voucher = await r.ReadAsync() ? Map(r) : null;
        if (voucher is null) return null;
        using (var cmd = conn.Command(tx, "SELECT * FROM FinanceVoucherAllocations WHERE VoucherId = @Id ORDER BY Id", ("@Id", id)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) voucher.Allocations.Add(MapAllocation(r));
        }
        return voucher;
    }

    public async Task<IReadOnlyList<FinanceVoucher>> ListAsync(VoucherFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        const string where = @"
            WHERE (@Search IS NOT NULL OR @PartyId IS NOT NULL OR (v.VoucherDate >= @From AND v.VoucherDate <= @To))
              AND (@Kind IS NULL OR v.Kind = @Kind)
              AND (@PartyType IS NULL OR v.PartyType = @PartyType)
              AND (@PartyId IS NULL OR v.PartyId = @PartyId)
              AND (@All = 1 OR v.Status = 1)
              AND (@Search IS NULL OR v.VoucherNumber LIKE @Search ESCAPE '\' OR v.PartyName LIKE @Search ESCAPE '\'
                   OR v.Reference LIKE @Search ESCAPE '\' OR v.ChequeId IN (SELECT Id FROM FinanceCheques WHERE ChequeNumber LIKE @Search ESCAPE '\'))";
        (string, object?)[] args =
        {
            ("@From", D(filter.From)), ("@To", D(filter.To)), ("@Kind", filter.Kind is { } k ? (int)k : null),
            ("@PartyType", filter.PartyType is { } pt ? (int)pt : null), ("@PartyId", filter.PartyId),
            ("@All", filter.IncludeReversed ? 1 : 0), ("@Search", LikeOrNull(filter.SearchText)), ("@Limit", Math.Clamp(filter.Limit, 1, 10000))
        };
        using var conn = _db.CreateNewConnection();
        var vouchers = new List<FinanceVoucher>();
        using (var cmd = conn.Command(null, $"{SelectSql} {where} ORDER BY v.VoucherDate DESC, v.Id DESC LIMIT @Limit", args))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) vouchers.Add(Map(r));
        }
        var byId = vouchers.ToDictionary(v => v.Id);
        using (var cmd = conn.Command(null, $"SELECT a.* FROM FinanceVoucherAllocations a JOIN FinanceVouchers v ON v.Id = a.VoucherId {where} ORDER BY a.Id", args))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
                if (byId.TryGetValue(r.Long("VoucherId"), out var voucher)) voucher.Allocations.Add(MapAllocation(r));
        }
        return vouchers;
    }

    internal static async Task<string?> PartyNameAsync(SqliteConnection conn, SqliteTransaction? tx, PartyType partyType, long partyId) =>
        partyType switch
        {
            PartyType.Customer => await conn.ScalarAsync(tx, "SELECT Name FROM Customers WHERE Id = @Id", ("@Id", partyId)) as string,
            PartyType.Supplier => await conn.ScalarAsync(tx, "SELECT Name FROM Suppliers WHERE Id = @Id", ("@Id", partyId)) as string,
            _ => null
        };

    private static FinanceVoucher Map(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        VoucherNumber = r.Str("VoucherNumber") ?? string.Empty,
        Kind = (FinanceVoucherKind)r.Int("Kind"),
        VoucherDate = r.Date("VoucherDate"),
        PartyType = (PartyType)r.Int("PartyType"),
        PartyId = r.Long("PartyId"),
        PartyName = r.Str("PartyName") ?? string.Empty,
        PartyPhone = r.Str("PartyPhone"),
        Mode = (FinancePaymentMode)r.Int("Mode"),
        AccountId = r.LongOrNull("AccountId"),
        AccountName = r.Str("AccountName"),
        AmountPaisa = r.Long("AmountPaisa"),
        Reference = r.Str("Reference"),
        Notes = r.Str("Notes"),
        ChequeId = r.LongOrNull("ChequeId"),
        ChequeNumber = r.Str("ChequeNumber"),
        ChequeDate = r.DateOrNull("ChequeDate"),
        ChequeBank = r.Str("ChequeBank"),
        ChequeStatus = r.IntOrNull("ChequeStatus") is { } cs ? (ChequeStatus)cs : null,
        Status = (FinanceVoucherStatus)r.Int("Status", 1),
        ReversedAt = r.DateOrNull("ReversedAt"),
        ReversalReason = r.Str("ReversalReason"),
        CreatedByName = r.Str("CreatedByName"),
        CreatedAt = r.Date("CreatedAt")
    };

    private static VoucherAllocation MapAllocation(SqliteDataReader r) =>
        new((AllocationTarget)r.Int("Target"), r.Long("DocumentId"), r.Str("DocumentNumber") ?? string.Empty, r.Long("AmountPaisa"));
}
