using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Modules.Finance;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Repositories;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Finance;

/// <summary>
/// Temp SQLite database with every discovered migration applied, plus seed helpers for Finance tests. Customers, sales,
/// suppliers and purchases are seeded with plain SQL in the shape their owning modules write them, so these tests do not
/// depend on Billing / Purchases services.
/// </summary>
public abstract class FinanceDbTestBase : IAsyncLifetime
{
    protected static readonly DateTime Today = DateTime.Today;
    protected const long CashAccountId = 1;

    protected readonly string DbPath = Path.Combine(Path.GetTempPath(), $"MSM_Finance_{Guid.NewGuid():N}.db");
    protected DatabaseManager Db = null!;

    public virtual async Task InitializeAsync()
    {
        Db = new DatabaseManager(DbPath, NullLogger<DatabaseManager>.Instance);
        await Db.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    // ───────────────────────── SQL helpers ─────────────────────────

    protected async Task<object?> ScalarAsync(string sql, params (string, object?)[] args)
    {
        using var conn = Db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    protected async Task<long> LongAsync(string sql, params (string, object?)[] args) => Convert.ToInt64(await ScalarAsync(sql, args) ?? 0L);

    protected static string D(DateTime date) => date.ToString("yyyy-MM-dd");

    // ───────────────────────── Seeds ─────────────────────────

    protected Task<long> SeedCustomerAsync(string name = "Ramesh Kumar", long openingPaisa = 0, DateTime? openingDate = null) =>
        LongAsync(@"
            INSERT INTO Customers (Name, Phone, IsActive, OpeningBalancePaisa, OpeningBalanceDate) VALUES (@N, '9876543210', 1, @O, @D);
            SELECT last_insert_rowid();", ("@N", name), ("@O", openingPaisa), ("@D", D(openingDate ?? Today.AddDays(-200))));

    /// <summary>A sale (Billing shape) with <paramref name="paidPaisa"/> paid at the counter; a cash Payments row for the paid part.</summary>
    protected async Task<long> SeedSaleAsync(long? customerId, long totalPaisa, long paidPaisa, DateTime date, string number,
        PaymentMethod method = PaymentMethod.Cash)
    {
        var saleId = await LongAsync(@"
            INSERT INTO Sales (InvoiceNumber, InvoiceDate, CustomerId, CustomerName, GrandTotalPaisa, AmountPaidPaisa, BalanceDuePaisa,
                PaymentMethod, PaymentStatus, Status, IsCounterSale)
            VALUES (@No, @Date, @C, 'Ramesh Kumar', @Total, @Paid, @Due, @M, @PS, 1, @Counter);
            SELECT last_insert_rowid();",
            ("@No", number), ("@Date", date.ToString("yyyy-MM-dd HH:mm:ss")), ("@C", customerId), ("@Total", totalPaisa), ("@Paid", paidPaisa),
            ("@Due", totalPaisa - paidPaisa), ("@M", (int)(paidPaisa == 0 ? PaymentMethod.Credit : method)),
            ("@PS", (int)(paidPaisa >= totalPaisa ? PaymentStatus.Paid : paidPaisa > 0 ? PaymentStatus.Partial : PaymentStatus.Unpaid)),
            ("@Counter", customerId is null ? 1 : 0));
        if (paidPaisa > 0)
            await ScalarAsync(@"
                INSERT INTO Payments (TransactionId, TransactionReference, PaymentDate, PaymentType, PaymentMethod, AmountPaisa)
                VALUES (@S, @No, @Date, @T, @M, @A)",
                ("@S", saleId), ("@No", number), ("@Date", date.ToString("yyyy-MM-dd HH:mm:ss")), ("@T", (int)PaymentType.SalePayment),
                ("@M", (int)method), ("@A", paidPaisa));
        return saleId;
    }

    protected Task<long> SeedSupplierAsync(string name = "Apollo Distributors", long openingPaisa = 0) =>
        LongAsync("INSERT INTO Suppliers (Name, Phone, Gstin, IsActive, OpeningBalancePaisa) VALUES (@N, '9000000001', '27AAACA1234A1Z5', 1, @O); SELECT last_insert_rowid();",
            ("@N", name), ("@O", openingPaisa));

    /// <summary>A confirmed purchase bill (Purchases shape) with nothing paid.</summary>
    protected Task<long> SeedPurchaseAsync(long supplierId, long totalPaisa, DateTime date, string supplierInvoice, long paidPaisa = 0) =>
        LongAsync(@"
            INSERT INTO Purchases (SupplierInvoiceNumber, SupplierId, SupplierName, PurchaseDate, GrandTotalPaisa, AmountPaidPaisa, BalancePaisa,
                Status, PaymentStatus, PurchaseNumber)
            VALUES (@Inv, @S, 'Apollo Distributors', @Date, @Total, @Paid, @Bal, @Confirmed, @PS, @No);
            SELECT last_insert_rowid();",
            ("@Inv", supplierInvoice), ("@S", supplierId), ("@Date", D(date)), ("@Total", totalPaisa), ("@Paid", paidPaisa),
            ("@Bal", totalPaisa - paidPaisa), ("@Confirmed", (int)PurchaseStatus.Confirmed),
            ("@PS", (int)(paidPaisa >= totalPaisa ? PaymentStatus.Paid : paidPaisa > 0 ? PaymentStatus.Partial : PaymentStatus.Unpaid)),
            ("@No", "PUR-" + supplierInvoice));

    protected Task<long> SaleBalanceAsync(long saleId) => LongAsync("SELECT BalanceDuePaisa FROM Sales WHERE Id = @Id", ("@Id", saleId));
    protected Task<long> PurchaseBalanceAsync(long id) => LongAsync("SELECT BalancePaisa FROM Purchases WHERE Id = @Id", ("@Id", id));

    // ───────────────────────── Services ─────────────────────────

    protected FinanceAccountRepository Accounts() => new(Db);
    protected FinancePartyRepository Parties() => new(Db);
    protected FinanceVoucherRepository Vouchers() => new(Db);
    protected FinanceChequeRepository Cheques() => new(Db);
    protected FinanceExpenseRepository Expenses() => new(Db);
    protected DocumentNumberService Numbers() => new(Db, NullLogger<DocumentNumberService>.Instance);

    protected AuditService Audit()
    {
        var auth = new AuthService(new UserRepository(Db), NullLogger<AuthService>.Instance);
        return new AuditService(new AuditLogRepository(Db), auth, NullLogger<AuditService>.Instance);
    }

    protected CashBankService CashBankService() => new(Accounts(), Audit(), () => Today);
    protected PartyPaymentService PaymentService() => new(Parties(), Vouchers(), Accounts(), Numbers(), Audit(), () => Today);
    protected ChequeService ChequeService() => new(Cheques(), Audit(), () => Today);
    protected ExpenseService ExpenseService() => new(Expenses(), Accounts(), Numbers(), new AppSettingsRepository(Db), Audit(), () => Today);

    protected async Task<long> CashBalanceAsync() =>
        (await Accounts().ListAsync(Today)).Single(a => a.Id == CashAccountId).BalancePaisa;

    protected Task<long> AuditCountAsync(string action) => LongAsync("SELECT COUNT(*) FROM AuditLogs WHERE Action = @A", ("@A", action));
}
