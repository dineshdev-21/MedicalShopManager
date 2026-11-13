using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Numbering;
using static MedicalShopManager.Infrastructure.Modules.Finance.FinanceSql;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// Expenses v2 (Finance owns the Expenses table) and expense categories. An expense posts in one transaction with its
/// EXP number from DocumentSequences; the legacy Category / PaymentMethod columns stay filled so pre-6.2 readers
/// (reports, Excel sync) keep working. Cancelling keeps the row and its number (an audit trail), it never deletes.
/// </summary>
public sealed class FinanceExpenseRepository : IFinanceExpenseRepository
{
    private readonly DatabaseManager _db;

    public FinanceExpenseRepository(DatabaseManager db) => _db = db;

    // ───────────────────────── Categories ─────────────────────────

    public async Task<IReadOnlyList<FinanceExpenseCategory>> ListCategoriesAsync(bool includeInactive = false)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT Id, Name, IsActive, SortOrder FROM FinanceExpenseCategories WHERE (@All = 1 OR IsActive = 1)
            ORDER BY SortOrder, Name COLLATE NOCASE", ("@All", includeInactive ? 1 : 0));
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<FinanceExpenseCategory>();
        while (await r.ReadAsync())
            list.Add(new FinanceExpenseCategory
            {
                Id = r.Long("Id"), Name = r.Str("Name") ?? string.Empty, IsActive = r.Bool("IsActive", true), SortOrder = r.Int("SortOrder")
            });
        return list;
    }

    public async Task<long> InsertCategoryAsync(string name)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await RequireUniqueCategoryAsync(conn, tx, name, 0);
            var id = await conn.LongAsync(tx, @"
                INSERT INTO FinanceExpenseCategories (Name, IsActive, SortOrder, CreatedAt)
                VALUES (@N, 1, COALESCE((SELECT MAX(SortOrder) FROM FinanceExpenseCategories), 0) + 1, @Now);
                SELECT last_insert_rowid();", ("@N", name.Trim()), ("@Now", Now()));
            tx.Commit();
            return id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task RenameCategoryAsync(long id, string name)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await RequireUniqueCategoryAsync(conn, tx, name, id);
            var changed = await conn.ExecAsync(tx, "UPDATE FinanceExpenseCategories SET Name = @N WHERE Id = @Id", ("@N", name.Trim()), ("@Id", id));
            if (changed == 0) throw new FinanceValidationException("The category was not found.");
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task SetCategoryActiveAsync(long id, bool isActive)
    {
        using var conn = _db.CreateNewConnection();
        var changed = await conn.ExecAsync(null, "UPDATE FinanceExpenseCategories SET IsActive = @A WHERE Id = @Id",
            ("@A", isActive ? 1 : 0), ("@Id", id));
        if (changed == 0) throw new FinanceValidationException("The category was not found.");
    }

    private static async Task RequireUniqueCategoryAsync(SqliteConnection conn, SqliteTransaction tx, string name, long excludeId)
    {
        var count = await conn.LongAsync(tx, "SELECT COUNT(*) FROM FinanceExpenseCategories WHERE Name = @N COLLATE NOCASE AND Id <> @Id",
            ("@N", name.Trim()), ("@Id", excludeId));
        if (count > 0) throw new FinanceValidationException($"A category named \"{name.Trim()}\" already exists.");
    }

    // ───────────────────────── Expenses ─────────────────────────

    private const string SelectSql = @"
        SELECT e.*, c.Name AS CategoryName, a.Name AS AccountName FROM Expenses e
        LEFT JOIN FinanceExpenseCategories c ON c.Id = e.CategoryId
        LEFT JOIN FinanceAccounts a ON a.Id = e.AccountId";

    public async Task<IReadOnlyList<FinanceExpense>> ListAsync(ExpenseFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, $@"{SelectSql}
            WHERE substr(e.ExpenseDate, 1, 10) >= @From AND substr(e.ExpenseDate, 1, 10) <= @To
              AND (@Cat IS NULL OR e.CategoryId = @Cat)
              AND (@Acc IS NULL OR e.AccountId = @Acc)
              AND (@Gst IS NULL OR (@Gst = 1 AND e.CgstPaisa + e.SgstPaisa + e.IgstPaisa > 0)
                                OR (@Gst = 0 AND e.CgstPaisa + e.SgstPaisa + e.IgstPaisa = 0))
              AND (@All = 1 OR e.IsCancelled = 0)
              AND (@Search IS NULL OR e.ExpenseNumber LIKE @Search ESCAPE '\' OR e.PayeeName LIKE @Search ESCAPE '\'
                   OR e.Description LIKE @Search ESCAPE '\' OR e.InvoiceNumber LIKE @Search ESCAPE '\' OR c.Name LIKE @Search ESCAPE '\')
            ORDER BY e.ExpenseDate DESC, e.Id DESC
            LIMIT 5000",
            ("@From", D(filter.From)), ("@To", D(filter.To)), ("@Cat", filter.CategoryId), ("@Acc", filter.AccountId),
            ("@Gst", filter.WithGst is { } g ? (g ? 1 : 0) : null), ("@All", filter.IncludeCancelled ? 1 : 0),
            ("@Search", LikeOrNull(filter.SearchText)));
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<FinanceExpense>();
        while (await r.ReadAsync()) list.Add(Map(r));
        return list;
    }

    public async Task<FinanceExpense?> GetAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        return await GetAsync(conn, null, id);
    }

    private static async Task<FinanceExpense?> GetAsync(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        using var cmd = conn.Command(tx, SelectSql + " WHERE e.Id = @Id", ("@Id", id));
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public async Task<FinanceExpense> InsertAsync(FinanceExpense expense)
    {
        ArgumentNullException.ThrowIfNull(expense);
        long id;
        using var conn = _db.CreateNewConnection();
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                await RequireCategoryAsync(conn, tx, expense.CategoryId);
                if (expense.AccountId is { } accountId) await FinanceAccountRepository.RequireActiveAccountAsync(conn, tx, accountId);
                var number = await DocumentSequenceAllocator.AllocateAsync(conn, tx, DocumentType.Expense, expense.ExpenseDate);
                var args = Values(expense).Concat(new (string, object?)[]
                {
                    ("@No", number), ("@User", expense.CreatedByUserId), ("@Created", Now())
                }).ToArray();
                id = await conn.LongAsync(tx, @"
                    INSERT INTO Expenses (ExpenseDate, Category, Description, AmountPaisa, PaymentMethod, ReferenceNumber, Notes, CreatedByUserId,
                        CreatedAt, ExpenseNumber, CategoryId, PayeeName, PayeeGstin, InvoiceNumber, GstRatePercent, IsInterState, TaxablePaisa,
                        CgstPaisa, SgstPaisa, IgstPaisa, AccountId, Mode, IsCancelled, UpdatedAt)
                    VALUES (@Date, @LegacyCat, @Descr, @Amt, @Method, @Ref, @Notes, @User,
                        @Created, @No, @Cat, @Payee, @Gstin, @Inv, @Rate, @Inter, @Taxable,
                        @Cgst, @Sgst, @Igst, @Acc, @Mode, 0, @Created);
                    SELECT last_insert_rowid();", args);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        return await GetAsync(conn, null, id) ?? throw new InvalidOperationException("The expense was saved but could not be read back.");
    }

    public async Task UpdateAsync(FinanceExpense expense)
    {
        ArgumentNullException.ThrowIfNull(expense);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await RequireCategoryAsync(conn, tx, expense.CategoryId);
            if (expense.AccountId is { } accountId) await FinanceAccountRepository.RequireActiveAccountAsync(conn, tx, accountId);
            var args = Values(expense).Concat(new (string, object?)[] { ("@Id", expense.Id), ("@Now", Now()) }).ToArray();
            var changed = await conn.ExecAsync(tx, @"
                UPDATE Expenses SET ExpenseDate = @Date, Category = @LegacyCat, Description = @Descr, AmountPaisa = @Amt, PaymentMethod = @Method,
                    ReferenceNumber = @Ref, Notes = @Notes, CategoryId = @Cat, PayeeName = @Payee, PayeeGstin = @Gstin, InvoiceNumber = @Inv,
                    GstRatePercent = @Rate, IsInterState = @Inter, TaxablePaisa = @Taxable, CgstPaisa = @Cgst, SgstPaisa = @Sgst, IgstPaisa = @Igst,
                    AccountId = @Acc, Mode = @Mode, UpdatedAt = @Now
                WHERE Id = @Id AND IsCancelled = 0", args);
            if (changed == 0) throw new FinanceValidationException("The expense was not found or has been cancelled.");
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task CancelAsync(long id, string reason)
    {
        using var conn = _db.CreateNewConnection();
        var changed = await conn.ExecAsync(null, @"
            UPDATE Expenses SET IsCancelled = 1, CancelReason = @R, CancelledAt = @Now, UpdatedAt = @Now WHERE Id = @Id AND IsCancelled = 0",
            ("@R", reason.Trim()), ("@Now", Now()), ("@Id", id));
        if (changed == 0) throw new FinanceValidationException("The expense was not found or is already cancelled.");
    }

    public async Task<IReadOnlyList<ExpenseCategoryTotal>> GetCategoryTotalsAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT COALESCE(c.Name, 'Other') AS CategoryName, COUNT(*) AS Cnt, SUM(e.AmountPaisa) AS Total
            FROM Expenses e LEFT JOIN FinanceExpenseCategories c ON c.Id = e.CategoryId
            WHERE e.IsCancelled = 0 AND substr(e.ExpenseDate, 1, 10) >= @From AND substr(e.ExpenseDate, 1, 10) <= @To
            GROUP BY COALESCE(c.Name, 'Other') ORDER BY Total DESC", ("@From", D(from)), ("@To", D(to)));
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<ExpenseCategoryTotal>();
        while (await r.ReadAsync()) list.Add(new ExpenseCategoryTotal(r.Str("CategoryName") ?? "Other", r.Int("Cnt"), r.Long("Total")));
        return list;
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static async Task RequireCategoryAsync(SqliteConnection conn, SqliteTransaction tx, long categoryId)
    {
        var active = await conn.ScalarAsync(tx, "SELECT IsActive FROM FinanceExpenseCategories WHERE Id = @Id", ("@Id", categoryId));
        if (active is null) throw new FinanceValidationException("The expense category was not found. Choose it again.");
        if (Convert.ToInt64(active) == 0) throw new FinanceValidationException("The expense category is inactive. Choose another.");
    }

    /// <summary>Legacy ExpenseCategory value for a category id (seeded ids 1–16 are enum value + 1; custom heads map to Other).</summary>
    internal static int LegacyCategory(long categoryId) =>
        categoryId is >= 1 and <= 16 ? (int)categoryId - 1 : (int)ExpenseCategory.Other;

    private static (string, object?)[] Values(FinanceExpense e) => new (string, object?)[]
    {
        ("@Date", D(e.ExpenseDate)), ("@LegacyCat", LegacyCategory(e.CategoryId)), ("@Descr", Blank(e.Description)),
        ("@Amt", e.AmountPaisa), ("@Method", (int)e.Mode.ToPaymentMethod()), ("@Ref", Blank(e.Reference)), ("@Notes", Blank(e.Notes)),
        ("@Cat", e.CategoryId), ("@Payee", Blank(e.PayeeName)), ("@Gstin", Blank(e.PayeeGstin)?.ToUpperInvariant()),
        ("@Inv", Blank(e.InvoiceNumber)), ("@Rate", (double)e.GstRatePercent), ("@Inter", e.IsInterState ? 1 : 0),
        ("@Taxable", e.TaxablePaisa), ("@Cgst", e.CgstPaisa), ("@Sgst", e.SgstPaisa), ("@Igst", e.IgstPaisa),
        ("@Acc", e.AccountId), ("@Mode", (int)e.Mode)
    };

    private static FinanceExpense Map(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        ExpenseNumber = r.Str("ExpenseNumber"),
        ExpenseDate = r.Date("ExpenseDate").Date,
        CategoryId = r.Long("CategoryId"),
        CategoryName = r.Str("CategoryName") ?? "Other",
        Description = r.Str("Description"),
        PayeeName = r.Str("PayeeName"),
        PayeeGstin = r.Str("PayeeGstin"),
        InvoiceNumber = r.Str("InvoiceNumber"),
        GstRatePercent = r.Dec("GstRatePercent"),
        IsInterState = r.Bool("IsInterState"),
        TaxablePaisa = r.Long("TaxablePaisa"),
        CgstPaisa = r.Long("CgstPaisa"),
        SgstPaisa = r.Long("SgstPaisa"),
        IgstPaisa = r.Long("IgstPaisa"),
        AmountPaisa = r.Long("AmountPaisa"),
        AccountId = r.LongOrNull("AccountId"),
        AccountName = r.Str("AccountName"),
        Mode = r.IntOrNull("Mode") is { } m ? (FinancePaymentMode)m : FinancePaymentMode.Cash,
        Reference = r.Str("ReferenceNumber"),
        Notes = r.Str("Notes"),
        IsCancelled = r.Bool("IsCancelled"),
        CancelReason = r.Str("CancelReason"),
        CreatedByUserId = r.LongOrNull("CreatedByUserId"),
        CreatedAt = r.Date("CreatedAt")
    };
}
