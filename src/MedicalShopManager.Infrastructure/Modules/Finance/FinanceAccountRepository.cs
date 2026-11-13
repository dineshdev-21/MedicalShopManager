using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Infrastructure.Database;
using static MedicalShopManager.Infrastructure.Modules.Finance.FinanceSql;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// Cash &amp; bank accounts, the payment-method → account map, transfers / adjustments and account statements.
/// Balances come from <see cref="FinanceLedgerSql.Entries"/> on every read.
/// </summary>
public sealed class FinanceAccountRepository : IFinanceAccountRepository
{
    private readonly DatabaseManager _db;

    public FinanceAccountRepository(DatabaseManager db) => _db = db;

    public async Task<IReadOnlyList<FinanceAccount>> ListAsync(DateTime asOf, bool includeInactive = false)
    {
        using var conn = _db.CreateNewConnection();
        var accounts = new List<FinanceAccount>();
        using (var cmd = conn.Command(null, @"
            SELECT * FROM FinanceAccounts WHERE (@All = 1 OR IsActive = 1) ORDER BY AccountType, Name COLLATE NOCASE",
            ("@All", includeInactive ? 1 : 0)))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) accounts.Add(Map(r));
        }

        var movements = new Dictionary<long, (long Net, DateTime? Last)>();
        using (var cmd = conn.Command(null, $@"
            SELECT AccountId, COALESCE(SUM(InPaisa - OutPaisa), 0) AS Net, MAX(EntryDate) AS LastDate
            FROM ({FinanceLedgerSql.Entries}) WHERE AccountId IS NOT NULL AND EntryDate <= @AsOf GROUP BY AccountId",
            ("@AsOf", D(asOf))))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) movements[r.Long("AccountId")] = (r.Long("Net"), r.DateOrNull("LastDate"));
        }

        foreach (var account in accounts)
        {
            var opening = account.OpeningDate.Date <= asOf.Date ? account.OpeningBalancePaisa : 0;
            movements.TryGetValue(account.Id, out var m);
            account.BalancePaisa = opening + m.Net;
            account.LastTransactionDate = m.Last;
        }
        return accounts;
    }

    public async Task<FinanceAccount?> GetAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        return await GetAsync(conn, null, id);
    }

    private static async Task<FinanceAccount?> GetAsync(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        using var cmd = conn.Command(tx, "SELECT * FROM FinanceAccounts WHERE Id = @Id", ("@Id", id));
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public async Task<bool> NameExistsAsync(string name, long excludeId = 0)
    {
        using var conn = _db.CreateNewConnection();
        return await conn.LongAsync(null, "SELECT COUNT(*) FROM FinanceAccounts WHERE Name = @N COLLATE NOCASE AND Id <> @Id",
            ("@N", name.Trim()), ("@Id", excludeId)) > 0;
    }

    public async Task<long> InsertAsync(FinanceAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var conn = _db.CreateNewConnection();
        return await conn.LongAsync(null, @"
            INSERT INTO FinanceAccounts (Name, AccountType, BankName, AccountNumber, Ifsc, HolderName, UpiId, OpeningBalancePaisa,
                OpeningDate, IsActive, Notes, CreatedAt, UpdatedAt)
            VALUES (@Name, @Type, @Bank, @AccNo, @Ifsc, @Holder, @Upi, @Opening, @OpeningDate, 1, @Notes, @Now, @Now);
            SELECT last_insert_rowid();", Values(account));
    }

    public async Task UpdateAsync(FinanceAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var conn = _db.CreateNewConnection();
        var args = Values(account).Append(("@Id", account.Id)).ToArray();
        var changed = await conn.ExecAsync(null, @"
            UPDATE FinanceAccounts SET Name = @Name, AccountType = @Type, BankName = @Bank, AccountNumber = @AccNo, Ifsc = @Ifsc,
                HolderName = @Holder, UpiId = @Upi, OpeningBalancePaisa = @Opening, OpeningDate = @OpeningDate, Notes = @Notes, UpdatedAt = @Now
            WHERE Id = @Id", args);
        if (changed == 0) throw new FinanceValidationException("The account was not found. It may have been removed.");
    }

    public async Task SetActiveAsync(long id, bool isActive)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            if (!isActive)
            {
                var mapped = await conn.LongAsync(tx, "SELECT COUNT(*) FROM FinanceMethodAccounts WHERE AccountId = @Id", ("@Id", id));
                if (mapped > 0)
                    throw new FinanceValidationException(
                        "This account receives cash / UPI / card / bank payments from bills. Choose another account for those methods first.");
                var openCheques = await conn.LongAsync(tx, $@"
                    SELECT COUNT(*) FROM FinanceCheques WHERE AccountId = @Id AND Status IN ({(int)ChequeStatus.Pending}, {(int)ChequeStatus.Deposited})",
                    ("@Id", id));
                if (openCheques > 0) throw new FinanceValidationException("This account has cheques waiting to clear. Clear or bounce them first.");
            }
            var changed = await conn.ExecAsync(tx, "UPDATE FinanceAccounts SET IsActive = @A, UpdatedAt = @Now WHERE Id = @Id",
                ("@A", isActive ? 1 : 0), ("@Now", Now()), ("@Id", id));
            if (changed == 0) throw new FinanceValidationException("The account was not found.");
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ───────────────────────── Method → account map ─────────────────────────

    public async Task<IReadOnlyList<PaymentMethodAccount>> GetMethodAccountsAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "SELECT PaymentMethod, AccountId FROM FinanceMethodAccounts ORDER BY PaymentMethod");
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<PaymentMethodAccount>();
        while (await r.ReadAsync()) list.Add(new PaymentMethodAccount((PaymentMethod)r.Int("PaymentMethod"), r.Long("AccountId")));
        return list;
    }

    public async Task SetMethodAccountAsync(PaymentMethod method, long accountId)
    {
        if (method == PaymentMethod.Credit) throw new FinanceValidationException("Credit sales move no money, so they have no account.");
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await RequireActiveAccountAsync(conn, tx, accountId);
            await conn.ExecAsync(tx, @"
                INSERT INTO FinanceMethodAccounts (PaymentMethod, AccountId) VALUES (@M, @A)
                ON CONFLICT(PaymentMethod) DO UPDATE SET AccountId = excluded.AccountId",
                ("@M", (int)method), ("@A", accountId));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ───────────────────────── Statement ─────────────────────────

    public async Task<AccountStatement> GetStatementAsync(long accountId, DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        var account = await GetAsync(conn, null, accountId) ?? throw new FinanceValidationException("The account was not found.");

        var before = await conn.LongAsync(null, $@"
            SELECT COALESCE(SUM(InPaisa - OutPaisa), 0) FROM ({FinanceLedgerSql.Entries}) WHERE AccountId = @A AND EntryDate < @From",
            ("@A", accountId), ("@From", D(from)));
        var opening = before + (account.OpeningDate.Date < from.Date ? account.OpeningBalancePaisa : 0);

        var lines = new List<AccountStatementLine>();
        if (account.OpeningDate.Date >= from.Date && account.OpeningDate.Date <= to.Date && account.OpeningBalancePaisa != 0)
            lines.Add(new AccountStatementLine
            {
                Date = account.OpeningDate.Date, Source = AccountEntrySource.OpeningBalance, Description = "Opening balance",
                InPaisa = Math.Max(0, account.OpeningBalancePaisa), OutPaisa = Math.Max(0, -account.OpeningBalancePaisa), SortKey = 0
            });

        using (var cmd = conn.Command(null, $@"
            SELECT * FROM ({FinanceLedgerSql.Entries}) WHERE AccountId = @A AND EntryDate >= @From AND EntryDate <= @To
            ORDER BY EntryDate, SortKey", ("@A", accountId), ("@From", D(from)), ("@To", D(to))))
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
                lines.Add(new AccountStatementLine
                {
                    Date = r.Date("EntryDate"), Source = (AccountEntrySource)r.Int("Source"), Reference = r.Str("Reference"),
                    PartyName = r.Str("PartyName"), Description = r.Str("Description"), InPaisa = r.Long("InPaisa"),
                    OutPaisa = r.Long("OutPaisa"), SortKey = r.Long("SortKey")
                });
        }

        return new AccountStatement
        {
            Account = account, From = from.Date, To = to.Date, OpeningBalancePaisa = opening,
            Lines = FinanceRules.WithRunningBalance(opening, lines)
        };
    }

    // ───────────────────────── Transfers / adjustments ─────────────────────────

    public async Task<long> InsertMovementAsync(FinanceMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            if (movement.FromAccountId is { } fromId) await RequireActiveAccountAsync(conn, tx, fromId);
            if (movement.ToAccountId is { } toId) await RequireActiveAccountAsync(conn, tx, toId);
            var id = await conn.LongAsync(tx, @"
                INSERT INTO FinanceMovements (Kind, MovementDate, FromAccountId, ToAccountId, AmountPaisa, Reference, Notes, CreatedByUserId, CreatedAt)
                VALUES (@Kind, @Date, @From, @To, @Amt, @Ref, @Notes, @User, @Now);
                SELECT last_insert_rowid();",
                ("@Kind", (int)movement.Kind), ("@Date", D(movement.MovementDate)), ("@From", movement.FromAccountId),
                ("@To", movement.ToAccountId), ("@Amt", movement.AmountPaisa), ("@Ref", Blank(movement.Reference)),
                ("@Notes", Blank(movement.Notes)), ("@User", movement.CreatedByUserId), ("@Now", Now()));
            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<FinanceMovement?> GetMovementAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, MovementSelect + " WHERE mv.Id = @Id", ("@Id", id));
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapMovement(r) : null;
    }

    public async Task CancelMovementAsync(long id, string reason)
    {
        using var conn = _db.CreateNewConnection();
        var changed = await conn.ExecAsync(null, @"
            UPDATE FinanceMovements SET IsCancelled = 1, CancelReason = @R, CancelledAt = @Now WHERE Id = @Id AND IsCancelled = 0",
            ("@R", reason.Trim()), ("@Now", Now()), ("@Id", id));
        if (changed == 0) throw new FinanceValidationException("The entry was not found or is already cancelled.");
    }

    public async Task<IReadOnlyList<FinanceMovement>> ListMovementsAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, MovementSelect + " WHERE mv.MovementDate >= @From AND mv.MovementDate <= @To ORDER BY mv.MovementDate DESC, mv.Id DESC",
            ("@From", D(from)), ("@To", D(to)));
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<FinanceMovement>();
        while (await r.ReadAsync()) list.Add(MapMovement(r));
        return list;
    }

    private const string MovementSelect = @"
        SELECT mv.*, fa.Name AS FromName, ta.Name AS ToName FROM FinanceMovements mv
        LEFT JOIN FinanceAccounts fa ON fa.Id = mv.FromAccountId
        LEFT JOIN FinanceAccounts ta ON ta.Id = mv.ToAccountId";

    // ───────────────────────── Helpers ─────────────────────────

    internal static async Task RequireActiveAccountAsync(SqliteConnection conn, SqliteTransaction? tx, long accountId)
    {
        var active = await conn.ScalarAsync(tx, "SELECT IsActive FROM FinanceAccounts WHERE Id = @Id", ("@Id", accountId));
        if (active is null) throw new FinanceValidationException("The chosen account was not found. Reload and choose again.");
        if (Convert.ToInt64(active) == 0) throw new FinanceValidationException("The chosen account is inactive. Reactivate it or choose another.");
    }

    private static (string, object?)[] Values(FinanceAccount a) => new (string, object?)[]
    {
        ("@Name", a.Name.Trim()), ("@Type", (int)a.AccountType), ("@Bank", Blank(a.BankName)), ("@AccNo", Blank(a.AccountNumber)),
        ("@Ifsc", Blank(a.Ifsc)?.ToUpperInvariant()), ("@Holder", Blank(a.HolderName)), ("@Upi", Blank(a.UpiId)),
        ("@Opening", a.OpeningBalancePaisa), ("@OpeningDate", D(a.OpeningDate)), ("@Notes", Blank(a.Notes)), ("@Now", Now())
    };

    private static FinanceAccount Map(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        Name = r.Str("Name") ?? string.Empty,
        AccountType = (FinanceAccountType)r.Int("AccountType", (int)FinanceAccountType.Bank),
        BankName = r.Str("BankName"),
        AccountNumber = r.Str("AccountNumber"),
        Ifsc = r.Str("Ifsc"),
        HolderName = r.Str("HolderName"),
        UpiId = r.Str("UpiId"),
        OpeningBalancePaisa = r.Long("OpeningBalancePaisa"),
        OpeningDate = r.DateOrNull("OpeningDate") ?? new DateTime(2000, 1, 1),
        IsActive = r.Bool("IsActive", true),
        Notes = r.Str("Notes")
    };

    private static FinanceMovement MapMovement(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        Kind = (FinanceMovementKind)r.Int("Kind"),
        MovementDate = r.Date("MovementDate"),
        FromAccountId = r.LongOrNull("FromAccountId"),
        ToAccountId = r.LongOrNull("ToAccountId"),
        FromAccountName = r.Str("FromName"),
        ToAccountName = r.Str("ToName"),
        AmountPaisa = r.Long("AmountPaisa"),
        Reference = r.Str("Reference"),
        Notes = r.Str("Notes"),
        IsCancelled = r.Bool("IsCancelled"),
        CreatedByUserId = r.LongOrNull("CreatedByUserId")
    };
}
