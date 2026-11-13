using Microsoft.Data.Sqlite;

namespace MedicalShopManager.Infrastructure.Database.Migrations;

/// <summary>
/// v2 — item master v2 (Localwell item fields).
/// <list type="bullet">
/// <item>Medicines: GenericName→Composition, PackSize→UnitsPerPack, PrescriptionClassification→Schedule (same integers),
/// ReorderLevel→MinStock; new Manufacturer, CompositionId, PackLabel, AllowLooseSale, MaxDiscountPercent, DefaultMrpPaisa.</item>
/// <item>Compositions: normalised salts, back-filled from the existing composition text.</item>
/// <item>MedicineBatches: per-unit prices become per-pack rates (PurchaseRatePaisa, SaleRatePaisa, MrpPaisa = old × pack size),
/// new PtrPaisa and UnitsPerPack (snapshot of the medicine's pack size).</item>
/// </list>
/// </summary>
public sealed class Migration0002ItemMasterV2 : IMigration
{
    public int Version => 2;
    public string Description => "Item master v2: compositions, pack/loose units, schedule, per-pack batch rates";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Compositions (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Name        TEXT NOT NULL UNIQUE COLLATE NOCASE,
                IsChronic   INTEGER NOT NULL DEFAULT 0,
                CreatedAt   TEXT NOT NULL DEFAULT (datetime('now'))
            );");

        // ─── Medicines ───
        await MigrationSql.RenameColumnIfExistsAsync(connection, transaction, "Medicines", "GenericName", "Composition");
        await MigrationSql.RenameColumnIfExistsAsync(connection, transaction, "Medicines", "PackSize", "UnitsPerPack");
        await MigrationSql.RenameColumnIfExistsAsync(connection, transaction, "Medicines", "PrescriptionClassification", "Schedule");
        await MigrationSql.RenameColumnIfExistsAsync(connection, transaction, "Medicines", "ReorderLevel", "MinStock");

        await MigrationSql.AddColumnIfMissingAsync(connection, transaction, "Medicines", "CompositionId", "INTEGER REFERENCES Compositions(Id)");
        await MigrationSql.AddColumnIfMissingAsync(connection, transaction, "Medicines", "Manufacturer", "TEXT");
        await MigrationSql.AddColumnIfMissingAsync(connection, transaction, "Medicines", "PackLabel", "TEXT");
        await MigrationSql.AddColumnIfMissingAsync(connection, transaction, "Medicines", "AllowLooseSale", "INTEGER NOT NULL DEFAULT 1");
        await MigrationSql.AddColumnIfMissingAsync(connection, transaction, "Medicines", "MaxDiscountPercent", "REAL");
        await MigrationSql.AddColumnIfMissingAsync(connection, transaction, "Medicines", "DefaultMrpPaisa", "INTEGER NOT NULL DEFAULT 0");

        // Default selling price was per unit; it is now per pack like every other rate.
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            UPDATE Medicines SET UnitsPerPack = 1 WHERE UnitsPerPack IS NULL OR UnitsPerPack < 1;
            UPDATE Medicines SET DefaultSellingPricePaisa = DefaultSellingPricePaisa * UnitsPerPack;");

        // Back-fill normalised compositions from the text column.
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            UPDATE Medicines SET Composition = NULLIF(TRIM(Composition), '');
            INSERT OR IGNORE INTO Compositions (Name)
                SELECT DISTINCT Composition FROM Medicines WHERE Composition IS NOT NULL;
            UPDATE Medicines SET CompositionId =
                (SELECT c.Id FROM Compositions c WHERE c.Name = Medicines.Composition COLLATE NOCASE)
            WHERE Composition IS NOT NULL;");

        // ─── MedicineBatches ───
        await MigrationSql.RenameColumnIfExistsAsync(connection, transaction, "MedicineBatches", "PurchaseCostPerUnitPaisa", "PurchaseRatePaisa");
        await MigrationSql.RenameColumnIfExistsAsync(connection, transaction, "MedicineBatches", "SellingPricePerUnitPaisa", "SaleRatePaisa");
        await MigrationSql.RenameColumnIfExistsAsync(connection, transaction, "MedicineBatches", "MrpPerUnitPaisa", "MrpPaisa");
        await MigrationSql.AddColumnIfMissingAsync(connection, transaction, "MedicineBatches", "PtrPaisa", "INTEGER NOT NULL DEFAULT 0");
        await MigrationSql.AddColumnIfMissingAsync(connection, transaction, "MedicineBatches", "UnitsPerPack", "INTEGER NOT NULL DEFAULT 1");

        await MigrationSql.ExecuteAsync(connection, transaction, @"
            UPDATE MedicineBatches SET UnitsPerPack =
                COALESCE((SELECT MAX(m.UnitsPerPack, 1) FROM Medicines m WHERE m.Id = MedicineBatches.MedicineId), 1);
            UPDATE MedicineBatches SET
                PurchaseRatePaisa = PurchaseRatePaisa * UnitsPerPack,
                SaleRatePaisa     = SaleRatePaisa * UnitsPerPack,
                MrpPaisa          = MrpPaisa * UnitsPerPack;");

        // ─── Indexes ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            DROP INDEX IF EXISTS IX_Medicines_GenericName;
            CREATE INDEX IF NOT EXISTS IX_Medicines_Composition ON Medicines(Composition);
            CREATE INDEX IF NOT EXISTS IX_Medicines_CompositionId ON Medicines(CompositionId);
            CREATE INDEX IF NOT EXISTS IX_Medicines_Manufacturer ON Medicines(Manufacturer);
            CREATE INDEX IF NOT EXISTS IX_Medicines_HsnCode ON Medicines(HsnCode);");
    }
}
