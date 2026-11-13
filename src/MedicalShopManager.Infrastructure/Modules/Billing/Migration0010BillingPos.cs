using Microsoft.Data.Sqlite;
using MedicalShopManager.Infrastructure.Database.Migrations;

namespace MedicalShopManager.Infrastructure.Modules.Billing;

/// <summary>
/// Billing (6.2) schema, migration 10: per-pack sale lines (pack snapshot, packs + loose + free, MRP and rate per
/// pack), the POS header fields (patient, doctor, Rx, custom charge, round-off, due date, WITH GST, status),
/// individual / business customer fields, auto-saved drafts, custom-charge names, the Schedule H1/X register view
/// and the two Billing.* settings (draft retention, batch order; every other sale setting is Control Center's). Only tables Billing owns (Sales, SaleItems, Customers) are altered; depends on v1–v3.
/// </summary>
public sealed class Migration0010BillingPos : IMigration
{
    public int Version => 10;
    public string Description => "Billing: per-pack sale lines, POS header fields, customer fields, drafts, custom charges, H1 register";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await AddColumnsAsync(connection, transaction, "Sales", new (string, string)[]
        {
            ("Status", "INTEGER NOT NULL DEFAULT 1"),
            ("IsCounterSale", "INTEGER NOT NULL DEFAULT 1"),
            ("CustomerGstin", "TEXT"),
            ("PatientName", "TEXT"),
            ("PatientAge", "TEXT"),
            ("PatientGender", "INTEGER NOT NULL DEFAULT 0"),
            ("PatientAddress", "TEXT"),
            ("DoctorId", "INTEGER"),
            ("DoctorName", "TEXT"),
            ("DoctorRegNo", "TEXT"),
            ("PrescriptionNumber", "TEXT"),
            ("PrescriptionFilePath", "TEXT"),
            ("WithGst", "INTEGER NOT NULL DEFAULT 1"),
            ("IsInterState", "INTEGER NOT NULL DEFAULT 0"),
            ("CustomChargeName", "TEXT"),
            ("CustomChargePaisa", "INTEGER NOT NULL DEFAULT 0"),
            ("RoundOffPaisa", "INTEGER NOT NULL DEFAULT 0"),
            ("MrpTotalPaisa", "INTEGER NOT NULL DEFAULT 0"),
            ("SavingsPaisa", "INTEGER NOT NULL DEFAULT 0"),
            ("DueDate", "TEXT"),
            ("ModifiedAt", "TEXT"),
            ("CancelledAt", "TEXT"),
            ("CancelReason", "TEXT")
        });

        await AddColumnsAsync(connection, transaction, "SaleItems", new (string, string)[]
        {
            ("UnitsPerPack", "INTEGER NOT NULL DEFAULT 1"),
            ("Packs", "INTEGER NOT NULL DEFAULT 0"),
            ("LooseUnits", "INTEGER NOT NULL DEFAULT 0"),
            ("FreeUnits", "INTEGER NOT NULL DEFAULT 0"),
            ("MrpPerPackPaisa", "INTEGER NOT NULL DEFAULT 0"),
            ("RatePerPackPaisa", "INTEGER NOT NULL DEFAULT 0"),
            ("BillDiscountPaisa", "INTEGER NOT NULL DEFAULT 0"),
            ("Schedule", "INTEGER NOT NULL DEFAULT 0"),
            ("Manufacturer", "TEXT"),
            ("RackLocation", "TEXT")
        });

        await AddColumnsAsync(connection, transaction, "Customers", new (string, string)[]
        {
            ("CustomerType", "INTEGER NOT NULL DEFAULT 1"),
            ("Gender", "INTEGER NOT NULL DEFAULT 0"),
            ("DateOfBirth", "TEXT"),
            ("Gstin", "TEXT"),
            ("Pan", "TEXT"),
            ("IsCompositionDealer", "INTEGER NOT NULL DEFAULT 0"),
            ("DefaultDiscountPercent", "REAL NOT NULL DEFAULT 0"),
            ("ReferenceNo", "TEXT"),
            ("OpeningBalancePaisa", "INTEGER NOT NULL DEFAULT 0"),
            ("OpeningBalanceDate", "TEXT"),
            ("CreditDays", "INTEGER NOT NULL DEFAULT 0")
        });

        // Pre-6.2 sale lines were billed per unit: their pack basis is 1 and the unit price is the per-pack rate.
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            UPDATE SaleItems SET Packs = Quantity, RatePerPackPaisa = UnitPricePaisa, MrpPerPackPaisa = UnitPricePaisa
            WHERE RatePerPackPaisa = 0 AND UnitsPerPack = 1;
            UPDATE Sales SET IsCounterSale = CASE WHEN CustomerId IS NULL THEN 1 ELSE 0 END;");

        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS BillingSaleDrafts (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                CustomerName    TEXT,
                ItemCount       INTEGER NOT NULL DEFAULT 0,
                TotalPaisa      INTEGER NOT NULL DEFAULT 0,
                Payload         TEXT NOT NULL,
                CreatedByUserId INTEGER,
                CreatedAt       TEXT NOT NULL,
                UpdatedAt       TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_BillingSaleDrafts_UpdatedAt ON BillingSaleDrafts(UpdatedAt);

            CREATE TABLE IF NOT EXISTS BillingCustomCharges (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT NOT NULL COLLATE NOCASE UNIQUE,
                IsActive  INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT OR IGNORE INTO BillingCustomCharges (Name) VALUES ('Delivery Fee');
            INSERT OR IGNORE INTO BillingCustomCharges (Name) VALUES ('Doctor Fee');

            CREATE INDEX IF NOT EXISTS IX_Sales_Status ON Sales(Status);
            CREATE INDEX IF NOT EXISTS IX_Sales_DoctorId ON Sales(DoctorId);
            CREATE INDEX IF NOT EXISTS IX_Sales_DueDate ON Sales(DueDate);
            CREATE INDEX IF NOT EXISTS IX_SaleItems_Schedule ON SaleItems(Schedule);
            CREATE INDEX IF NOT EXISTS IX_Customers_Name ON Customers(Name);
            CREATE INDEX IF NOT EXISTS IX_Customers_Gstin ON Customers(Gstin);

            DROP VIEW IF EXISTS BillingScheduleRegister;
            CREATE VIEW BillingScheduleRegister AS
                SELECT s.Id AS SaleId, si.Id AS SaleItemId, s.InvoiceNumber, s.InvoiceDate, si.Schedule,
                       si.MedicineName, si.Manufacturer, si.BatchNumber, si.ExpiryDate, si.Quantity,
                       s.PatientName, s.PatientAddress, s.DoctorName, s.DoctorRegNo, s.PrescriptionNumber, s.CustomerName
                FROM SaleItems si JOIN Sales s ON s.Id = si.SaleId
                WHERE s.Status = 1 AND si.Schedule IN (2, 3, 4);");

        foreach (var (key, value, description) in new[]
        {
            ("Billing.DraftRetentionDays", "7", "POS: unsaved bills (F8) are kept this many days"),
            ("Billing.BatchOrder", "0", "POS: batch auto-pick order, 0 = first expiry first out, 1 = last in first out")
        })
        {
            await MigrationSql.ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO AppSettings (Key, Value, Description) VALUES (@Key, @Value, @Description);",
                ("@Key", key), ("@Value", value), ("@Description", description));
        }
    }

    private static async Task AddColumnsAsync(SqliteConnection connection, SqliteTransaction transaction, string table,
        IEnumerable<(string Column, string Definition)> columns)
    {
        foreach (var (column, definition) in columns)
            await MigrationSql.AddColumnIfMissingAsync(connection, transaction, table, column, definition);
    }
}
