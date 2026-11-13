using Microsoft.Data.Sqlite;
using MedicalShopManager.Infrastructure.Database.Migrations;

namespace MedicalShopManager.Infrastructure.Modules.SalesReturns;

/// <summary>
/// v11 — Sales returns and credit notes (spec 03, MASTER-SPEC §3.3).
/// <list type="bullet">
/// <item>SaleReturns: one row per return / credit note (both numbers UNIQUE, allocated from DocumentSequences),
/// optional original bill, customer snapshot, refund mode, GST reversal totals, status (cancel keeps the row).</item>
/// <item>SaleReturnItems: returned batch lines with the GST split and what happened to the units (restock / expired
/// stock / damaged).</item>
/// <item>SaleReturnRefunds: money paid back against a credit note (open credit = net − refunds, derived in SQL).</item>
/// <item>Returns (shared with Purchases): SaleReturnId / SaleReturnItemId so the legacy ledger rows written for every
/// returned line (which Billing checks before editing a bill) can be traced and removed on cancel.</item>
/// <item>Setting SalesReturns.RoundOffToRupee.</item>
/// </list>
/// Depends on v1–v3 only.
/// </summary>
public sealed class Migration0011SalesReturns : IMigration
{
    public int Version => 11;
    public string Description => "Sales returns: credit notes, returned lines, refunds";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS SaleReturns (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ReturnNumber        TEXT NOT NULL UNIQUE,
                CreditNoteNumber    TEXT NOT NULL UNIQUE,
                ReturnDate          TEXT NOT NULL,
                SaleId              INTEGER REFERENCES Sales(Id),
                SaleInvoiceNumber   TEXT,
                SaleInvoiceDate     TEXT,
                CustomerId          INTEGER REFERENCES Customers(Id),
                CustomerName        TEXT,
                CustomerPhone       TEXT,
                CustomerGstin       TEXT,
                IsInterState        INTEGER NOT NULL DEFAULT 0,
                RefundMode          INTEGER NOT NULL DEFAULT 1,
                RefundMethod        INTEGER NOT NULL DEFAULT 0,
                RefundReference     TEXT,
                Reason              TEXT,
                Notes               TEXT,
                GrossPaisa          INTEGER NOT NULL DEFAULT 0,
                DiscountPaisa       INTEGER NOT NULL DEFAULT 0,
                TaxablePaisa        INTEGER NOT NULL DEFAULT 0,
                CgstPaisa           INTEGER NOT NULL DEFAULT 0,
                SgstPaisa           INTEGER NOT NULL DEFAULT 0,
                IgstPaisa           INTEGER NOT NULL DEFAULT 0,
                TaxPaisa            INTEGER NOT NULL DEFAULT 0,
                RoundOffPaisa       INTEGER NOT NULL DEFAULT 0,
                NetAmountPaisa      INTEGER NOT NULL DEFAULT 0,
                Status              INTEGER NOT NULL DEFAULT 1,
                CancelledAt         TEXT,
                CancelReason        TEXT,
                CreatedByUserId     INTEGER REFERENCES Users(Id),
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS SaleReturnItems (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                SaleReturnId        INTEGER NOT NULL REFERENCES SaleReturns(Id),
                SaleItemId          INTEGER REFERENCES SaleItems(Id),
                MedicineId          INTEGER NOT NULL REFERENCES Medicines(Id),
                MedicineName        TEXT NOT NULL,
                HsnCode             TEXT,
                BatchId             INTEGER NOT NULL REFERENCES MedicineBatches(Id),
                BatchNumber         TEXT NOT NULL,
                ExpiryDate          TEXT,
                UnitsPerPack        INTEGER NOT NULL DEFAULT 1,
                Units               INTEGER NOT NULL,
                RatePerPackPaisa    INTEGER NOT NULL DEFAULT 0,
                MrpPerPackPaisa     INTEGER NOT NULL DEFAULT 0,
                DiscountPercent     REAL NOT NULL DEFAULT 0,
                GstRatePercent      REAL NOT NULL DEFAULT 0,
                GrossPaisa          INTEGER NOT NULL DEFAULT 0,
                DiscountPaisa       INTEGER NOT NULL DEFAULT 0,
                TaxablePaisa        INTEGER NOT NULL DEFAULT 0,
                CgstPaisa           INTEGER NOT NULL DEFAULT 0,
                SgstPaisa           INTEGER NOT NULL DEFAULT 0,
                IgstPaisa           INTEGER NOT NULL DEFAULT 0,
                TaxPaisa            INTEGER NOT NULL DEFAULT 0,
                LineTotalPaisa      INTEGER NOT NULL DEFAULT 0,
                Disposition         INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS SaleReturnRefunds (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                SaleReturnId        INTEGER NOT NULL REFERENCES SaleReturns(Id),
                RefundDate          TEXT NOT NULL,
                Method              INTEGER NOT NULL,
                AmountPaisa         INTEGER NOT NULL,
                Reference           TEXT,
                PaymentId           INTEGER,
                CreatedByUserId     INTEGER REFERENCES Users(Id),
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );");

        await MigrationSql.AddColumnIfMissingAsync(connection, transaction, "Returns", "SaleReturnId", "INTEGER");
        await MigrationSql.AddColumnIfMissingAsync(connection, transaction, "Returns", "SaleReturnItemId", "INTEGER");

        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE INDEX IF NOT EXISTS IX_SaleReturns_ReturnDate ON SaleReturns(ReturnDate);
            CREATE INDEX IF NOT EXISTS IX_SaleReturns_SaleId ON SaleReturns(SaleId);
            CREATE INDEX IF NOT EXISTS IX_SaleReturns_CustomerId ON SaleReturns(CustomerId);
            CREATE INDEX IF NOT EXISTS IX_SaleReturns_Status ON SaleReturns(Status);
            CREATE INDEX IF NOT EXISTS IX_SaleReturns_CreatedByUserId ON SaleReturns(CreatedByUserId);
            CREATE INDEX IF NOT EXISTS IX_SaleReturnItems_SaleReturnId ON SaleReturnItems(SaleReturnId);
            CREATE INDEX IF NOT EXISTS IX_SaleReturnItems_SaleItemId ON SaleReturnItems(SaleItemId);
            CREATE INDEX IF NOT EXISTS IX_SaleReturnItems_MedicineId ON SaleReturnItems(MedicineId);
            CREATE INDEX IF NOT EXISTS IX_SaleReturnItems_BatchId ON SaleReturnItems(BatchId);
            CREATE INDEX IF NOT EXISTS IX_SaleReturnRefunds_SaleReturnId ON SaleReturnRefunds(SaleReturnId);
            CREATE INDEX IF NOT EXISTS IX_SaleReturnRefunds_RefundDate ON SaleReturnRefunds(RefundDate);
            CREATE INDEX IF NOT EXISTS IX_Returns_SaleReturnId ON Returns(SaleReturnId);
            CREATE INDEX IF NOT EXISTS IX_Returns_SalesReturns_Original ON Returns(ReturnType, OriginalTransactionId);

            INSERT OR IGNORE INTO AppSettings (Key, Value, Description) VALUES
            ('SalesReturns.RoundOffToRupee', '1', 'Round sale return / credit note totals to the nearest rupee');");
    }
}
