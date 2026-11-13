using Microsoft.Data.Sqlite;
using MedicalShopManager.Infrastructure.Database.Migrations;

namespace MedicalShopManager.Infrastructure.Modules.Stock;

/// <summary>
/// v13 — Stock module tables:
/// <list type="bullet">
/// <item>StockItemSettings: module-owned item fields keyed by MedicineId (Medicines is frozen).</item>
/// <item>StockAdjustments + StockAdjustmentLines: numbered stock adjustment vouchers (reason, signed unit changes per batch).</item>
/// <item>Setting Stock.ShowZeroBatches (BATCHES tab toggle).</item>
/// </list>
/// </summary>
public sealed class Migration0013StockModule : IMigration
{
    public int Version => 13;
    public string Description => "Stock: item settings side table, stock adjustments";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS StockItemSettings (
                MedicineId          INTEGER PRIMARY KEY REFERENCES Medicines(Id),
                MinDiscountPercent  REAL NOT NULL DEFAULT 0,
                IsDiscountLocked    INTEGER NOT NULL DEFAULT 0,
                IgnoreForShortage   INTEGER NOT NULL DEFAULT 0,
                UpdatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS StockAdjustments (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                AdjustmentNumber    TEXT NOT NULL UNIQUE,
                AdjustmentDate      TEXT NOT NULL,
                Reason              INTEGER NOT NULL,
                Notes               TEXT,
                TotalValuePaisa     INTEGER NOT NULL DEFAULT 0,
                CreatedByUserId     INTEGER REFERENCES Users(Id),
                CreatedByName       TEXT,
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS StockAdjustmentLines (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                AdjustmentId        INTEGER NOT NULL REFERENCES StockAdjustments(Id),
                MedicineId          INTEGER NOT NULL REFERENCES Medicines(Id),
                MedicineName        TEXT NOT NULL,
                BatchId             INTEGER NOT NULL REFERENCES MedicineBatches(Id),
                BatchNumber         TEXT NOT NULL,
                ExpiryDate          TEXT NOT NULL,
                QuantityChange      INTEGER NOT NULL,
                BalanceBefore       INTEGER NOT NULL,
                BalanceAfter        INTEGER NOT NULL,
                UnitsPerPack        INTEGER NOT NULL DEFAULT 1,
                RatePaisa           INTEGER NOT NULL DEFAULT 0,
                ValuePaisa          INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_StockAdjustments_AdjustmentDate ON StockAdjustments(AdjustmentDate);
            CREATE INDEX IF NOT EXISTS IX_StockAdjustments_CreatedByUserId ON StockAdjustments(CreatedByUserId);
            CREATE INDEX IF NOT EXISTS IX_StockAdjustmentLines_AdjustmentId ON StockAdjustmentLines(AdjustmentId);
            CREATE INDEX IF NOT EXISTS IX_StockAdjustmentLines_MedicineId ON StockAdjustmentLines(MedicineId);
            CREATE INDEX IF NOT EXISTS IX_StockAdjustmentLines_BatchId ON StockAdjustmentLines(BatchId);

            INSERT OR IGNORE INTO AppSettings (Key, Value, Description)
            VALUES ('Stock.ShowZeroBatches', '0', 'Stock: show zero-quantity batches in the BATCHES tab');");
    }
}
