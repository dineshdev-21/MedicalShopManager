using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Infrastructure.Database;
using static MedicalShopManager.Infrastructure.Modules.Finance.FinanceSql;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// Cheque register. A cheque moves money in the account ledger only when it clears (on the cleared date, through the
/// account chosen at deposit / clearing). Bounce or cancel reverses its voucher's bill allocations in the same
/// transaction, so the dues are open again.
/// </summary>
public sealed class FinanceChequeRepository : IFinanceChequeRepository
{
    private readonly DatabaseManager _db;

    public FinanceChequeRepository(DatabaseManager db) => _db = db;

    private const string SelectSql = @"
        SELECT ch.*, a.Name AS AccountName, v.VoucherNumber FROM FinanceCheques ch
        LEFT JOIN FinanceAccounts a ON a.Id = ch.AccountId
        LEFT JOIN FinanceVouchers v ON v.Id = ch.VoucherId";

    public async Task<IReadOnlyList<FinanceCheque>> ListAsync(ChequeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, $@"{SelectSql}
            WHERE (@Open = 1 AND ch.Status IN ({(int)ChequeStatus.Pending}, {(int)ChequeStatus.Deposited})
                   OR @Open = 0 AND (@Search IS NOT NULL OR (ch.ChequeDate >= @From AND ch.ChequeDate <= @To)))
              AND (@Dir IS NULL OR ch.Direction = @Dir)
              AND (@Status IS NULL OR ch.Status = @Status)
              AND (@Search IS NULL OR ch.ChequeNumber LIKE @Search ESCAPE '\' OR ch.PartyName LIKE @Search ESCAPE '\'
                   OR ch.BankName LIKE @Search ESCAPE '\' OR v.VoucherNumber LIKE @Search ESCAPE '\')
            ORDER BY ch.ChequeDate, ch.Id
            LIMIT 5000",
            ("@Open", filter.OpenOnly ? 1 : 0), ("@From", D(filter.From)), ("@To", D(filter.To)),
            ("@Dir", filter.Direction is { } d ? (int)d : null), ("@Status", filter.Status is { } s ? (int)s : null),
            ("@Search", LikeOrNull(filter.SearchText)));
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<FinanceCheque>();
        while (await r.ReadAsync()) list.Add(Map(r));
        return list;
    }

    public async Task<FinanceCheque?> GetAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        return await GetAsync(conn, null, id);
    }

    private static async Task<FinanceCheque?> GetAsync(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        using var cmd = conn.Command(tx, SelectSql + " WHERE ch.Id = @Id", ("@Id", id));
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public async Task ChangeStatusAsync(ChequeStatusChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var cheque = await GetAsync(conn, tx, change.ChequeId) ?? throw new FinanceValidationException("The cheque was not found.");
            var errors = FinanceRules.ValidateChequeChange(cheque, change);
            if (errors.Count > 0) throw new FinanceValidationException(errors);
            var accountId = change.AccountId ?? cheque.AccountId;
            if (change.NewStatus is ChequeStatus.Deposited or ChequeStatus.Cleared && accountId is { } acc)
                await FinanceAccountRepository.RequireActiveAccountAsync(conn, tx, acc);

            var date = D(change.Date);
            var note = Blank(change.Note);
            switch (change.NewStatus)
            {
                case ChequeStatus.Deposited:
                    await UpdateAsync(conn, tx, cheque, "Status = @S, DepositDate = @D, AccountId = @A, StatusNote = COALESCE(@N, StatusNote)",
                        ("@S", (int)ChequeStatus.Deposited), ("@D", date), ("@A", accountId), ("@N", note));
                    break;
                case ChequeStatus.Cleared:
                    await UpdateAsync(conn, tx, cheque,
                        "Status = @S, ClearedDate = @D, DepositDate = COALESCE(DepositDate, @D), AccountId = @A, StatusNote = COALESCE(@N, StatusNote)",
                        ("@S", (int)ChequeStatus.Cleared), ("@D", date), ("@A", accountId), ("@N", note));
                    break;
                case ChequeStatus.Bounced:
                    await UpdateAsync(conn, tx, cheque, "Status = @S, BouncedDate = @D, StatusNote = @N",
                        ("@S", (int)ChequeStatus.Bounced), ("@D", date), ("@N", note));
                    if (cheque.VoucherId is { } bouncedVoucher)
                        await FinanceVoucherRepository.ReverseAsync(conn, tx, bouncedVoucher, FinanceVoucherStatus.Bounced, change.Date, note ?? "Cheque bounced");
                    break;
                case ChequeStatus.Cancelled:
                    await UpdateAsync(conn, tx, cheque, "Status = @S, StatusNote = @N", ("@S", (int)ChequeStatus.Cancelled), ("@N", note));
                    if (cheque.VoucherId is { } cancelledVoucher)
                        await FinanceVoucherRepository.ReverseAsync(conn, tx, cancelledVoucher, FinanceVoucherStatus.Cancelled, change.Date,
                            "Cheque cancelled: " + note);
                    break;
                default:
                    throw new FinanceValidationException("Choose deposited, cleared, bounced or cancelled.");
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Updates the cheque only if its status is still the one validated (two users cannot both move it).</summary>
    private static async Task UpdateAsync(SqliteConnection conn, SqliteTransaction tx, FinanceCheque cheque, string set, params (string, object?)[] args)
    {
        var all = args.Concat(new (string, object?)[] { ("@Id", cheque.Id), ("@Old", (int)cheque.Status), ("@Now", Now()) }).ToArray();
        var changed = await conn.ExecAsync(tx, $"UPDATE FinanceCheques SET {set}, UpdatedAt = @Now WHERE Id = @Id AND Status = @Old", all);
        if (changed != 1) throw new FinanceValidationException("The cheque was changed by someone else. Reload and try again.");
    }

    private static FinanceCheque Map(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        Direction = (ChequeDirection)r.Int("Direction"),
        ChequeNumber = r.Str("ChequeNumber") ?? string.Empty,
        ChequeDate = r.Date("ChequeDate"),
        BankName = r.Str("BankName"),
        AmountPaisa = r.Long("AmountPaisa"),
        PartyType = (PartyType)r.Int("PartyType"),
        PartyId = r.Long("PartyId"),
        PartyName = r.Str("PartyName") ?? string.Empty,
        VoucherId = r.LongOrNull("VoucherId"),
        VoucherNumber = r.Str("VoucherNumber"),
        Status = (ChequeStatus)r.Int("Status", 1),
        AccountId = r.LongOrNull("AccountId"),
        AccountName = r.Str("AccountName"),
        DepositDate = r.DateOrNull("DepositDate"),
        ClearedDate = r.DateOrNull("ClearedDate"),
        BouncedDate = r.DateOrNull("BouncedDate"),
        StatusNote = r.Str("StatusNote"),
        CreatedAt = r.Date("CreatedAt")
    };
}
