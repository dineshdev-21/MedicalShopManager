using Microsoft.Data.Sqlite;
using MedicalShopManager.Infrastructure.Database.Migrations;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>
/// v12 — Purchases module (spec 04).
/// <list type="bullet">
/// <item>Suppliers: state code, pincode, credit days.</item>
/// <item>Purchases: internal purchase number (UNIQUE), due date, GST options, bill discount, other charges, GST split,
/// round-off, import source, confirmed-at. Existing rows get Taxable = Subtotal − Discount and an intra-state split.</item>
/// <item>PurchaseItems: per-pack entry. UnitsPerPack (default 1, which is exactly what the old per-unit rows meant),
/// entered rate, PTR, scheme, HSN, GST split, net rate, net purchase rate, margin and the batch the line posted to.</item>
/// <item>New tables: PurchaseReturns / PurchaseReturnItems (debit notes) and PurchaseImportMappings (saved CSV mappings).</item>
/// <item>Settings: Purchases.DefaultSaleDiscountPercent, Purchases.NearExpiryMonths, Purchases.RoundOffToRupee.</item>
/// </list>
/// </summary>
public sealed class Migration0012PurchasesV2 : IMigration
{
    public int Version => 12;
    public string Description => "Purchases: per-pack purchase entry, distributor credit/state, debit notes, CSV import mappings";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        async Task Add(string table, string column, string definition) =>
            await MigrationSql.AddColumnIfMissingAsync(connection, transaction, table, column, definition);

        // ─── Suppliers ───
        await Add("Suppliers", "StateCode", "TEXT");
        await Add("Suppliers", "Pincode", "TEXT");
        await Add("Suppliers", "CreditDays", "INTEGER NOT NULL DEFAULT 0");

        // ─── Purchases ───
        await Add("Purchases", "PurchaseNumber", "TEXT");
        await Add("Purchases", "DueDate", "TEXT");
        await Add("Purchases", "WithGst", "INTEGER NOT NULL DEFAULT 1");
        await Add("Purchases", "RatesIncludeGst", "INTEGER NOT NULL DEFAULT 0");
        await Add("Purchases", "IsInterState", "INTEGER NOT NULL DEFAULT 0");
        await Add("Purchases", "BillDiscountPercent", "REAL NOT NULL DEFAULT 0");
        await Add("Purchases", "OtherChargesPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("Purchases", "OtherChargesLabel", "TEXT");
        await Add("Purchases", "TaxablePaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("Purchases", "CgstPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("Purchases", "SgstPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("Purchases", "IgstPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("Purchases", "RoundOffPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("Purchases", "ImportSource", "TEXT");
        await Add("Purchases", "ConfirmedAt", "TEXT");

        // ─── PurchaseItems ───
        await Add("PurchaseItems", "LineNo", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "HsnCode", "TEXT");
        await Add("PurchaseItems", "UnitsPerPack", "INTEGER NOT NULL DEFAULT 1");
        await Add("PurchaseItems", "EnteredRatePaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "PtrPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "SchemePercent", "REAL NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "SchemeQuantity", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "SchemeFreeQuantity", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "TaxablePaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "CgstPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "SgstPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "IgstPaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "NetRatePaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "NetPurchaseRatePaisa", "INTEGER NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "MarginPercent", "REAL NOT NULL DEFAULT 0");
        await Add("PurchaseItems", "BatchId", "INTEGER REFERENCES MedicineBatches(Id)");

        // Existing rows: per-unit prices are per-pack prices of a 1-unit pack, so only derived columns need filling.
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            UPDATE Purchases SET
                TaxablePaisa = SubtotalPaisa - DiscountAmountPaisa,
                CgstPaisa = (TaxAmountPaisa + 1) / 2,
                SgstPaisa = TaxAmountPaisa - (TaxAmountPaisa + 1) / 2
            WHERE TaxablePaisa = 0 AND SubtotalPaisa <> 0;
            UPDATE PurchaseItems SET
                EnteredRatePaisa = PurchasePricePaisa,
                PtrPaisa = PurchasePricePaisa,
                TaxablePaisa = LineTotalPaisa - GstAmountPaisa,
                CgstPaisa = (GstAmountPaisa + 1) / 2,
                SgstPaisa = GstAmountPaisa - (GstAmountPaisa + 1) / 2,
                NetRatePaisa = PurchasePricePaisa,
                NetPurchaseRatePaisa = CASE WHEN Quantity + FreeQuantity > 0
                    THEN (LineTotalPaisa - GstAmountPaisa) / (Quantity + FreeQuantity) ELSE PurchasePricePaisa END
            WHERE EnteredRatePaisa = 0;
            UPDATE PurchaseItems SET LineNo =
                (SELECT COUNT(*) FROM PurchaseItems p2 WHERE p2.PurchaseId = PurchaseItems.PurchaseId AND p2.Id <= PurchaseItems.Id)
            WHERE LineNo = 0;");

        // ─── Debit notes ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS PurchaseReturns (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ReturnNumber        TEXT NOT NULL UNIQUE,
                ReturnDate          TEXT NOT NULL,
                Kind                INTEGER NOT NULL DEFAULT 1,
                SupplierId          INTEGER NOT NULL REFERENCES Suppliers(Id),
                SupplierName        TEXT NOT NULL,
                PurchaseId          INTEGER REFERENCES Purchases(Id),
                PurchaseReference   TEXT,
                Reason              INTEGER NOT NULL DEFAULT 6,
                Notes               TEXT,
                IsInterState        INTEGER NOT NULL DEFAULT 0,
                TaxablePaisa        INTEGER NOT NULL DEFAULT 0,
                CgstPaisa           INTEGER NOT NULL DEFAULT 0,
                SgstPaisa           INTEGER NOT NULL DEFAULT 0,
                IgstPaisa           INTEGER NOT NULL DEFAULT 0,
                TaxAmountPaisa      INTEGER NOT NULL DEFAULT 0,
                RoundOffPaisa       INTEGER NOT NULL DEFAULT 0,
                NetAmountPaisa      INTEGER NOT NULL DEFAULT 0,
                RefundReceivedPaisa INTEGER NOT NULL DEFAULT 0,
                IsCancelled         INTEGER NOT NULL DEFAULT 0,
                CancelledAt         TEXT,
                CreatedByUserId     INTEGER REFERENCES Users(Id),
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS PurchaseReturnItems (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                PurchaseReturnId    INTEGER NOT NULL REFERENCES PurchaseReturns(Id),
                PurchaseItemId      INTEGER REFERENCES PurchaseItems(Id),
                MedicineId          INTEGER NOT NULL REFERENCES Medicines(Id),
                MedicineName        TEXT NOT NULL,
                HsnCode             TEXT,
                BatchId             INTEGER NOT NULL REFERENCES MedicineBatches(Id),
                BatchNumber         TEXT NOT NULL,
                ExpiryDate          TEXT NOT NULL,
                UnitsPerPack        INTEGER NOT NULL DEFAULT 1,
                Quantity            INTEGER NOT NULL,
                RatePaisa           INTEGER NOT NULL DEFAULT 0,
                GstRatePercent      REAL NOT NULL DEFAULT 0,
                TaxablePaisa        INTEGER NOT NULL DEFAULT 0,
                TaxPaisa            INTEGER NOT NULL DEFAULT 0,
                CgstPaisa           INTEGER NOT NULL DEFAULT 0,
                SgstPaisa           INTEGER NOT NULL DEFAULT 0,
                IgstPaisa           INTEGER NOT NULL DEFAULT 0,
                LineTotalPaisa      INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS PurchaseImportMappings (
                SupplierId   INTEGER NOT NULL REFERENCES Suppliers(Id),
                HeaderKey    TEXT NOT NULL,
                Mapping      TEXT NOT NULL,
                UpdatedAt    TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (SupplierId, HeaderKey)
            );");

        // ─── Indexes ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Purchases_PurchaseNumber ON Purchases(PurchaseNumber) WHERE PurchaseNumber IS NOT NULL;
            CREATE INDEX IF NOT EXISTS IX_Purchases_SupplierInvoice ON Purchases(SupplierId, SupplierInvoiceNumber);
            CREATE INDEX IF NOT EXISTS IX_Purchases_DueDate ON Purchases(DueDate);
            CREATE INDEX IF NOT EXISTS IX_PurchaseItems_MedicineId ON PurchaseItems(MedicineId);
            CREATE INDEX IF NOT EXISTS IX_PurchaseItems_BatchId ON PurchaseItems(BatchId);
            CREATE INDEX IF NOT EXISTS IX_PurchaseReturns_ReturnDate ON PurchaseReturns(ReturnDate);
            CREATE INDEX IF NOT EXISTS IX_PurchaseReturns_SupplierId ON PurchaseReturns(SupplierId);
            CREATE INDEX IF NOT EXISTS IX_PurchaseReturns_PurchaseId ON PurchaseReturns(PurchaseId);
            CREATE INDEX IF NOT EXISTS IX_PurchaseReturnItems_ReturnId ON PurchaseReturnItems(PurchaseReturnId);
            CREATE INDEX IF NOT EXISTS IX_PurchaseReturnItems_BatchId ON PurchaseReturnItems(BatchId);
            CREATE INDEX IF NOT EXISTS IX_PurchaseReturnItems_PurchaseItemId ON PurchaseReturnItems(PurchaseItemId);
            CREATE INDEX IF NOT EXISTS IX_PurchaseReturnItems_MedicineId ON PurchaseReturnItems(MedicineId);");

        // ─── Settings ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            INSERT OR IGNORE INTO AppSettings (Key, Value, Description) VALUES
            ('Purchases.DefaultSaleDiscountPercent', '0', 'Default sale rate on purchase entry = MRP less this % (0 = sell at MRP)'),
            ('Purchases.NearExpiryMonths', '3', 'Expiry return screen lists batches expiring within this many months'),
            ('Purchases.RoundOffToRupee', '1', 'Round purchase invoice and debit note totals to the nearest rupee');");
    }
}
