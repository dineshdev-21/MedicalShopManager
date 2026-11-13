using Microsoft.Data.Sqlite;
using MedicalShopManager.Infrastructure.Database.Migrations;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// v16 — Finance: cash &amp; bank, receipts / payments, cheques, party ledgers, expenses v2.
/// <list type="bullet">
/// <item>FinanceAccounts: cash in hand (seeded, Id 1), bank accounts, UPI wallets, card POS, each with an opening balance.
/// Balances are never stored: they are derived from source rows on every read (lesson 7).</item>
/// <item>FinanceMethodAccounts: which account a legacy payment method (Payments / Expenses rows written without an
/// account, e.g. by Billing and Purchases) books into. Every method except Credit starts on cash in hand.</item>
/// <item>FinanceMovements: transfers between accounts and manual add / reduce money entries.</item>
/// <item>FinanceVouchers + FinanceVoucherAllocations: receipts (Payment In, DocumentType.Receipt) and payment vouchers
/// (Payment Out, DocumentType.Payment) with the bills / opening balance they settle.</item>
/// <item>FinanceCheques: received / issued cheques (pending → deposited → cleared, or bounced / cancelled).</item>
/// <item>FinanceExpenseCategories: expense heads, seeded with Id = legacy ExpenseCategory value + 1.</item>
/// <item>Payments (owned by Finance): AccountId, VoucherId, ChequeId, PartyType, PartyId.</item>
/// <item>Expenses (owned by Finance): number, category, payee + GSTIN, bill no, GST split, account, mode, cancel.</item>
/// </list>
/// Depends on v1–v3 only (Sales / Purchases columns of other modules are read at run time, never here).
/// </summary>
public sealed class Migration0016Finance : IMigration
{
    public int Version => 16;
    public string Description => "Finance: cash & bank accounts, receipts / payments, cheques, transfers, expense categories and GST";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS FinanceAccounts (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                Name                TEXT NOT NULL UNIQUE COLLATE NOCASE,
                AccountType         INTEGER NOT NULL DEFAULT 2,
                BankName            TEXT,
                AccountNumber       TEXT,
                Ifsc                TEXT,
                HolderName          TEXT,
                UpiId               TEXT,
                OpeningBalancePaisa INTEGER NOT NULL DEFAULT 0,
                OpeningDate         TEXT NOT NULL,
                IsActive            INTEGER NOT NULL DEFAULT 1,
                Notes               TEXT,
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS FinanceMethodAccounts (
                PaymentMethod       INTEGER PRIMARY KEY,
                AccountId           INTEGER NOT NULL REFERENCES FinanceAccounts(Id)
            );

            CREATE TABLE IF NOT EXISTS FinanceMovements (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                Kind                INTEGER NOT NULL,
                MovementDate        TEXT NOT NULL,
                FromAccountId       INTEGER REFERENCES FinanceAccounts(Id),
                ToAccountId         INTEGER REFERENCES FinanceAccounts(Id),
                AmountPaisa         INTEGER NOT NULL CHECK (AmountPaisa > 0),
                Reference           TEXT,
                Notes               TEXT,
                IsCancelled         INTEGER NOT NULL DEFAULT 0,
                CancelReason        TEXT,
                CancelledAt         TEXT,
                CreatedByUserId     INTEGER REFERENCES Users(Id),
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS FinanceCheques (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                Direction           INTEGER NOT NULL,
                ChequeNumber        TEXT NOT NULL,
                ChequeDate          TEXT NOT NULL,
                BankName            TEXT,
                AmountPaisa         INTEGER NOT NULL CHECK (AmountPaisa > 0),
                PartyType           INTEGER NOT NULL,
                PartyId             INTEGER NOT NULL,
                PartyName           TEXT NOT NULL,
                VoucherId           INTEGER,
                Status              INTEGER NOT NULL DEFAULT 1,
                AccountId           INTEGER REFERENCES FinanceAccounts(Id),
                DepositDate         TEXT,
                ClearedDate         TEXT,
                BouncedDate         TEXT,
                StatusNote          TEXT,
                CreatedByUserId     INTEGER REFERENCES Users(Id),
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS FinanceVouchers (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                VoucherNumber       TEXT NOT NULL UNIQUE,
                Kind                INTEGER NOT NULL,
                VoucherDate         TEXT NOT NULL,
                PartyType           INTEGER NOT NULL,
                PartyId             INTEGER NOT NULL,
                PartyName           TEXT NOT NULL,
                Mode                INTEGER NOT NULL,
                AccountId           INTEGER REFERENCES FinanceAccounts(Id),
                AmountPaisa         INTEGER NOT NULL CHECK (AmountPaisa > 0),
                Reference           TEXT,
                Notes               TEXT,
                ChequeId            INTEGER REFERENCES FinanceCheques(Id),
                Status              INTEGER NOT NULL DEFAULT 1,
                ReversedAt          TEXT,
                ReversalReason      TEXT,
                CreatedByUserId     INTEGER REFERENCES Users(Id),
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS FinanceVoucherAllocations (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                VoucherId           INTEGER NOT NULL REFERENCES FinanceVouchers(Id),
                Target              INTEGER NOT NULL,
                DocumentId          INTEGER NOT NULL,
                DocumentNumber      TEXT NOT NULL,
                AmountPaisa         INTEGER NOT NULL CHECK (AmountPaisa > 0),
                PaymentId           INTEGER,
                IsReversed          INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS FinanceExpenseCategories (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                Name                TEXT NOT NULL UNIQUE COLLATE NOCASE,
                IsActive            INTEGER NOT NULL DEFAULT 1,
                SortOrder           INTEGER NOT NULL DEFAULT 0,
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );");

        // ─── Seeds: cash in hand, legacy method map (Credit = 4 moves no money), expense heads = enum value + 1 ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            INSERT OR IGNORE INTO FinanceAccounts (Id, Name, AccountType, OpeningBalancePaisa, OpeningDate, Notes)
            VALUES (1, 'Cash In Hand', 1, 0, '2000-01-01', 'Shop till. Sales, refunds and expenses paid in cash book here.');

            INSERT OR IGNORE INTO FinanceMethodAccounts (PaymentMethod, AccountId) VALUES
                (0, 1), (1, 1), (2, 1), (3, 1), (5, 1), (6, 1);

            INSERT OR IGNORE INTO FinanceExpenseCategories (Id, Name, SortOrder) VALUES
                (1, 'Rent', 1), (2, 'Salary', 2), (3, 'Electricity', 3), (4, 'Delivery', 4), (5, 'Packaging', 5),
                (6, 'Transport', 6), (7, 'Bank charges', 7), (8, 'Maintenance', 8), (9, 'Insurance', 9),
                (10, 'License & registration', 10), (11, 'Advertising', 11), (12, 'Stationery', 12), (13, 'Internet', 13),
                (14, 'Phone', 14), (15, 'Miscellaneous', 15), (16, 'Other', 16);");

        // ─── Payments (Finance-owned): where the money went and which voucher / cheque / party it belongs to ───
        async Task Add(string table, string column, string definition) =>
            await MigrationSql.AddColumnIfMissingAsync(connection, transaction, table, column, definition);

        await Add("Payments", "AccountId", "INTEGER REFERENCES FinanceAccounts(Id)");
        await Add("Payments", "VoucherId", "INTEGER REFERENCES FinanceVouchers(Id)");
        await Add("Payments", "ChequeId", "INTEGER REFERENCES FinanceCheques(Id)");
        await Add("Payments", "PartyType", "INTEGER");
        await Add("Payments", "PartyId", "INTEGER");

        // ─── Expenses (Finance-owned) v2 ───
        await Add("Expenses", "ExpenseNumber", "TEXT");
        await Add("Expenses", "CategoryId", "INTEGER REFERENCES FinanceExpenseCategories(Id)");
        await Add("Expenses", "PayeeName", "TEXT");
        await Add("Expenses", "PayeeGstin", "TEXT");
        await Add("Expenses", "InvoiceNumber", "TEXT");
        await Add("Expenses", "GstRatePercent", "REAL NOT NULL DEFAULT 0");
        await Add("Expenses", "IsInterState", "INTEGER NOT NULL DEFAULT 0");
        await Add("Expenses", "TaxablePaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("Expenses", "CgstPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("Expenses", "SgstPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("Expenses", "IgstPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("Expenses", "AccountId", "INTEGER REFERENCES FinanceAccounts(Id)");
        await Add("Expenses", "Mode", "INTEGER");
        await Add("Expenses", "IsCancelled", "INTEGER NOT NULL DEFAULT 0");
        await Add("Expenses", "CancelReason", "TEXT");
        await Add("Expenses", "CancelledAt", "TEXT");
        await Add("Expenses", "UpdatedAt", "TEXT");

        // Existing expenses: category from the enum, no GST (taxable = amount), mode from the legacy method.
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            UPDATE Expenses SET CategoryId = CASE WHEN Category BETWEEN 0 AND 15 THEN Category + 1 ELSE 16 END
            WHERE CategoryId IS NULL;
            UPDATE Expenses SET TaxablePaisa = AmountPaisa WHERE TaxablePaisa = 0 AND GstRatePercent = 0;
            UPDATE Expenses SET Mode = CASE PaymentMethod WHEN 1 THEN 2 WHEN 2 THEN 3 WHEN 3 THEN 4 ELSE 1 END
            WHERE Mode IS NULL;");

        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE INDEX IF NOT EXISTS IX_FinanceAccounts_IsActive ON FinanceAccounts(IsActive);
            CREATE INDEX IF NOT EXISTS IX_FinanceMethodAccounts_AccountId ON FinanceMethodAccounts(AccountId);
            CREATE INDEX IF NOT EXISTS IX_FinanceMovements_MovementDate ON FinanceMovements(MovementDate);
            CREATE INDEX IF NOT EXISTS IX_FinanceMovements_FromAccountId ON FinanceMovements(FromAccountId);
            CREATE INDEX IF NOT EXISTS IX_FinanceMovements_ToAccountId ON FinanceMovements(ToAccountId);
            CREATE INDEX IF NOT EXISTS IX_FinanceMovements_CreatedByUserId ON FinanceMovements(CreatedByUserId);
            CREATE INDEX IF NOT EXISTS IX_FinanceCheques_Party ON FinanceCheques(PartyType, PartyId);
            CREATE INDEX IF NOT EXISTS IX_FinanceCheques_Status ON FinanceCheques(Status);
            CREATE INDEX IF NOT EXISTS IX_FinanceCheques_ChequeDate ON FinanceCheques(ChequeDate);
            CREATE INDEX IF NOT EXISTS IX_FinanceCheques_ClearedDate ON FinanceCheques(ClearedDate);
            CREATE INDEX IF NOT EXISTS IX_FinanceCheques_VoucherId ON FinanceCheques(VoucherId);
            CREATE INDEX IF NOT EXISTS IX_FinanceCheques_AccountId ON FinanceCheques(AccountId);
            CREATE INDEX IF NOT EXISTS IX_FinanceCheques_CreatedByUserId ON FinanceCheques(CreatedByUserId);
            CREATE INDEX IF NOT EXISTS IX_FinanceVouchers_Party ON FinanceVouchers(PartyType, PartyId);
            CREATE INDEX IF NOT EXISTS IX_FinanceVouchers_VoucherDate ON FinanceVouchers(VoucherDate);
            CREATE INDEX IF NOT EXISTS IX_FinanceVouchers_AccountId ON FinanceVouchers(AccountId);
            CREATE INDEX IF NOT EXISTS IX_FinanceVouchers_ChequeId ON FinanceVouchers(ChequeId);
            CREATE INDEX IF NOT EXISTS IX_FinanceVouchers_CreatedByUserId ON FinanceVouchers(CreatedByUserId);
            CREATE INDEX IF NOT EXISTS IX_FinanceVoucherAllocations_VoucherId ON FinanceVoucherAllocations(VoucherId);
            CREATE INDEX IF NOT EXISTS IX_FinanceVoucherAllocations_Document ON FinanceVoucherAllocations(Target, DocumentId);
            CREATE INDEX IF NOT EXISTS IX_FinanceVoucherAllocations_PaymentId ON FinanceVoucherAllocations(PaymentId);
            CREATE INDEX IF NOT EXISTS IX_Payments_AccountId ON Payments(AccountId);
            CREATE INDEX IF NOT EXISTS IX_Payments_VoucherId ON Payments(VoucherId);
            CREATE INDEX IF NOT EXISTS IX_Payments_ChequeId ON Payments(ChequeId);
            CREATE INDEX IF NOT EXISTS IX_Payments_Party ON Payments(PartyType, PartyId);
            CREATE INDEX IF NOT EXISTS IX_Payments_Type ON Payments(PaymentType, TransactionId);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Expenses_ExpenseNumber ON Expenses(ExpenseNumber);
            CREATE INDEX IF NOT EXISTS IX_Expenses_CategoryId ON Expenses(CategoryId);
            CREATE INDEX IF NOT EXISTS IX_Expenses_AccountId ON Expenses(AccountId);
            CREATE INDEX IF NOT EXISTS IX_Expenses_CreatedByUserId ON Expenses(CreatedByUserId);");
    }
}
