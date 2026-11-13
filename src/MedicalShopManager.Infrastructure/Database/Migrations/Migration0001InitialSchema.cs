using Microsoft.Data.Sqlite;

namespace MedicalShopManager.Infrastructure.Database.Migrations;

/// <summary>
/// v1 — initial schema: all core tables, indexes, default settings and the default admin user.
/// Moved verbatim from the original DatabaseManager.ApplyMigrationV1Async; databases created before the
/// migration registry already have version 1 recorded and skip it.
/// </summary>
public sealed class Migration0001InitialSchema : IMigration
{
    public int Version => 1;
    public string Description => "Initial schema: all tables, indexes, default settings, admin user";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        // ─── Medicines ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Medicines (
                Id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                Name                        TEXT NOT NULL,
                GenericName                 TEXT,
                Brand                       TEXT,
                Category                    TEXT,
                DosageForm                  INTEGER NOT NULL DEFAULT 0,
                Strength                    TEXT,
                PackSize                    INTEGER NOT NULL DEFAULT 1,
                Barcode                     TEXT,
                Sku                         TEXT,
                HsnCode                     TEXT,
                GstRatePercent              REAL NOT NULL DEFAULT 0,
                PrescriptionClassification  INTEGER NOT NULL DEFAULT 0,
                ReorderLevel                INTEGER NOT NULL DEFAULT 10,
                MaxStock                    INTEGER NOT NULL DEFAULT 0,
                Unit                        TEXT,
                RackLocation                TEXT,
                DefaultSellingPricePaisa    INTEGER NOT NULL DEFAULT 0,
                DefaultSupplierId           INTEGER,
                Status                      INTEGER NOT NULL DEFAULT 0,
                Notes                       TEXT,
                CreatedAt                   TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt                   TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (DefaultSupplierId) REFERENCES Suppliers(Id)
            );
        ");

        // ─── MedicineBatches ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS MedicineBatches (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                MedicineId              INTEGER NOT NULL,
                BatchNumber             TEXT NOT NULL,
                ManufacturingDate       TEXT,
                ExpiryDate              TEXT NOT NULL,
                PurchaseDate            TEXT NOT NULL,
                SupplierId              INTEGER,
                QuantityReceived        INTEGER NOT NULL DEFAULT 0,
                QuantityRemaining       INTEGER NOT NULL DEFAULT 0,
                FreeUnits               INTEGER NOT NULL DEFAULT 0,
                PurchaseCostPerUnitPaisa INTEGER NOT NULL DEFAULT 0,
                SellingPricePerUnitPaisa INTEGER NOT NULL DEFAULT 0,
                MrpPerUnitPaisa         INTEGER NOT NULL DEFAULT 0,
                DiscountPercent         REAL NOT NULL DEFAULT 0,
                GstRatePercent          REAL NOT NULL DEFAULT 0,
                IsActive                INTEGER NOT NULL DEFAULT 1,
                CreatedAt               TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt               TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (MedicineId) REFERENCES Medicines(Id),
                FOREIGN KEY (SupplierId) REFERENCES Suppliers(Id)
            );
        ");

        // ─── Suppliers ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Suppliers (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                Name                TEXT NOT NULL,
                ContactPerson       TEXT,
                Phone               TEXT,
                Email               TEXT,
                Address             TEXT,
                Gstin               TEXT,
                DrugLicenseNumber   TEXT,
                IsActive            INTEGER NOT NULL DEFAULT 1,
                OpeningBalancePaisa  INTEGER NOT NULL DEFAULT 0,
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt           TEXT NOT NULL DEFAULT (datetime('now'))
            );
        ");

        // ─── Customers ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Customers (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Name        TEXT NOT NULL,
                Phone       TEXT,
                Email       TEXT,
                Address     TEXT,
                DoctorName  TEXT,
                IsActive    INTEGER NOT NULL DEFAULT 1,
                CreatedAt   TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt   TEXT NOT NULL DEFAULT (datetime('now'))
            );
        ");

        // ─── Sales ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Sales (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                InvoiceNumber       TEXT NOT NULL UNIQUE,
                InvoiceDate         TEXT NOT NULL,
                CustomerId          INTEGER,
                CustomerName        TEXT,
                CustomerPhone       TEXT,
                SubtotalPaisa       INTEGER NOT NULL DEFAULT 0,
                DiscountAmountPaisa INTEGER NOT NULL DEFAULT 0,
                TaxableAmountPaisa  INTEGER NOT NULL DEFAULT 0,
                CgstAmountPaisa     INTEGER NOT NULL DEFAULT 0,
                SgstAmountPaisa     INTEGER NOT NULL DEFAULT 0,
                IgstAmountPaisa     INTEGER NOT NULL DEFAULT 0,
                GrandTotalPaisa     INTEGER NOT NULL DEFAULT 0,
                AmountPaidPaisa     INTEGER NOT NULL DEFAULT 0,
                BalanceDuePaisa     INTEGER NOT NULL DEFAULT 0,
                PaymentMethod       INTEGER NOT NULL DEFAULT 0,
                PaymentStatus       INTEGER NOT NULL DEFAULT 0,
                CashReceivedPaisa   INTEGER NOT NULL DEFAULT 0,
                ChangeReturnedPaisa INTEGER NOT NULL DEFAULT 0,
                Notes               TEXT,
                CreatedByUserId     INTEGER,
                PrescriptionRef     TEXT,
                CreatedAt           TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (CustomerId) REFERENCES Customers(Id),
                FOREIGN KEY (CreatedByUserId) REFERENCES Users(Id)
            );
        ");

        // ─── SaleItems ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS SaleItems (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                SaleId              INTEGER NOT NULL,
                MedicineId          INTEGER NOT NULL,
                MedicineName        TEXT NOT NULL,
                BatchId             INTEGER NOT NULL,
                BatchNumber         TEXT NOT NULL,
                ExpiryDate          TEXT NOT NULL,
                HsnCode             TEXT,
                Quantity            INTEGER NOT NULL,
                UnitPricePaisa      INTEGER NOT NULL,
                DiscountPercent     REAL NOT NULL DEFAULT 0,
                DiscountAmountPaisa INTEGER NOT NULL DEFAULT 0,
                TaxableValuePaisa   INTEGER NOT NULL DEFAULT 0,
                GstRatePercent      REAL NOT NULL DEFAULT 0,
                CgstAmountPaisa     INTEGER NOT NULL DEFAULT 0,
                SgstAmountPaisa     INTEGER NOT NULL DEFAULT 0,
                IgstAmountPaisa     INTEGER NOT NULL DEFAULT 0,
                TaxAmountPaisa      INTEGER NOT NULL DEFAULT 0,
                CogsPaisa           INTEGER NOT NULL DEFAULT 0,
                LineTotalPaisa      INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (SaleId) REFERENCES Sales(Id),
                FOREIGN KEY (MedicineId) REFERENCES Medicines(Id),
                FOREIGN KEY (BatchId) REFERENCES MedicineBatches(Id)
            );
        ");

        // ─── Purchases ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Purchases (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                SupplierInvoiceNumber   TEXT NOT NULL,
                SupplierId              INTEGER NOT NULL,
                SupplierName            TEXT NOT NULL,
                PurchaseDate            TEXT NOT NULL,
                SubtotalPaisa           INTEGER NOT NULL DEFAULT 0,
                DiscountAmountPaisa     INTEGER NOT NULL DEFAULT 0,
                TaxAmountPaisa          INTEGER NOT NULL DEFAULT 0,
                GrandTotalPaisa         INTEGER NOT NULL DEFAULT 0,
                AmountPaidPaisa         INTEGER NOT NULL DEFAULT 0,
                BalancePaisa            INTEGER NOT NULL DEFAULT 0,
                Status                  INTEGER NOT NULL DEFAULT 0,
                PaymentStatus           INTEGER NOT NULL DEFAULT 2,
                Notes                   TEXT,
                CreatedByUserId         INTEGER,
                CreatedAt               TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt               TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (SupplierId) REFERENCES Suppliers(Id),
                FOREIGN KEY (CreatedByUserId) REFERENCES Users(Id)
            );
        ");

        // ─── PurchaseItems ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS PurchaseItems (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                PurchaseId          INTEGER NOT NULL,
                MedicineId          INTEGER NOT NULL,
                MedicineName        TEXT NOT NULL,
                BatchNumber         TEXT NOT NULL,
                ExpiryDate          TEXT NOT NULL,
                ManufacturingDate   TEXT,
                Quantity            INTEGER NOT NULL,
                FreeQuantity        INTEGER NOT NULL DEFAULT 0,
                PurchasePricePaisa  INTEGER NOT NULL,
                MrpPaisa            INTEGER NOT NULL DEFAULT 0,
                SellingPricePaisa   INTEGER NOT NULL DEFAULT 0,
                GstRatePercent      REAL NOT NULL DEFAULT 0,
                GstAmountPaisa      INTEGER NOT NULL DEFAULT 0,
                DiscountPercent     REAL NOT NULL DEFAULT 0,
                DiscountAmountPaisa INTEGER NOT NULL DEFAULT 0,
                LineTotalPaisa      INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (PurchaseId) REFERENCES Purchases(Id),
                FOREIGN KEY (MedicineId) REFERENCES Medicines(Id)
            );
        ");

        // ─── Payments ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Payments (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                TransactionId           INTEGER,
                TransactionReference    TEXT,
                PaymentDate             TEXT NOT NULL,
                PaymentType             INTEGER NOT NULL,
                PaymentMethod           INTEGER NOT NULL,
                AmountPaisa             INTEGER NOT NULL,
                ReferenceNumber         TEXT,
                Notes                   TEXT,
                CreatedByUserId         INTEGER,
                CreatedAt               TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (CreatedByUserId) REFERENCES Users(Id)
            );
        ");

        // ─── Expenses ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Expenses (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ExpenseDate     TEXT NOT NULL,
                Category        INTEGER NOT NULL,
                Description     TEXT,
                AmountPaisa     INTEGER NOT NULL,
                PaymentMethod   INTEGER NOT NULL DEFAULT 0,
                ReferenceNumber TEXT,
                Notes           TEXT,
                CreatedByUserId INTEGER,
                CreatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (CreatedByUserId) REFERENCES Users(Id)
            );
        ");

        // ─── Returns ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Returns (
                Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                OriginalTransactionId   INTEGER,
                OriginalReference       TEXT,
                ReturnType              INTEGER NOT NULL,
                ReturnDate              TEXT NOT NULL,
                MedicineId              INTEGER NOT NULL,
                MedicineName            TEXT NOT NULL,
                BatchId                 INTEGER,
                BatchNumber             TEXT,
                Quantity                INTEGER NOT NULL,
                RefundAmountPaisa       INTEGER NOT NULL DEFAULT 0,
                Reason                  TEXT,
                ReturnedToStock         INTEGER NOT NULL DEFAULT 0,
                ProcessedByUserId       INTEGER,
                CreatedAt               TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (MedicineId) REFERENCES Medicines(Id),
                FOREIGN KEY (BatchId) REFERENCES MedicineBatches(Id),
                FOREIGN KEY (ProcessedByUserId) REFERENCES Users(Id)
            );
        ");

        // ─── StockMovements ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS StockMovements (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                MovementDate    TEXT NOT NULL,
                MedicineId      INTEGER NOT NULL,
                MedicineName    TEXT NOT NULL,
                BatchId         INTEGER NOT NULL,
                BatchNumber     TEXT NOT NULL,
                MovementType    INTEGER NOT NULL,
                QuantityIn      INTEGER NOT NULL DEFAULT 0,
                QuantityOut     INTEGER NOT NULL DEFAULT 0,
                BalanceAfter    INTEGER NOT NULL DEFAULT 0,
                Reference       TEXT,
                Reason          TEXT,
                CreatedByUserId INTEGER,
                CreatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (MedicineId) REFERENCES Medicines(Id),
                FOREIGN KEY (BatchId) REFERENCES MedicineBatches(Id),
                FOREIGN KEY (CreatedByUserId) REFERENCES Users(Id)
            );
        ");

        // ─── Users ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Users (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                Username        TEXT NOT NULL UNIQUE COLLATE NOCASE,
                DisplayName     TEXT NOT NULL,
                PasswordHash    TEXT NOT NULL,
                Role            INTEGER NOT NULL DEFAULT 2,
                IsActive        INTEGER NOT NULL DEFAULT 1,
                LastLoginAt     TEXT,
                CreatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
            );
        ");

        // ─── AuditLogs ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS AuditLogs (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId      INTEGER,
                Username    TEXT,
                Timestamp   TEXT NOT NULL DEFAULT (datetime('now')),
                Action      TEXT NOT NULL,
                EntityType  TEXT,
                EntityId    INTEGER,
                OldValues   TEXT,
                NewValues   TEXT,
                Reason      TEXT,
                IpAddress   TEXT,
                FOREIGN KEY (UserId) REFERENCES Users(Id)
            );
        ");

        // ─── ExcelSyncQueue ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS ExcelSyncQueue (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                EntityType   INTEGER NOT NULL,
                EntityId     INTEGER,
                Status       INTEGER NOT NULL DEFAULT 0,
                RetryCount   INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT,
                CreatedAt    TEXT NOT NULL DEFAULT (datetime('now')),
                ProcessedAt  TEXT
            );
        ");

        // ─── AppSettings ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS AppSettings (
                Key         TEXT PRIMARY KEY,
                Value       TEXT NOT NULL,
                Description TEXT,
                UpdatedAt   TEXT NOT NULL DEFAULT (datetime('now'))
            );
        ");

        // ─── Indexes ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE INDEX IF NOT EXISTS IX_Medicines_Name ON Medicines(Name);
            CREATE INDEX IF NOT EXISTS IX_Medicines_GenericName ON Medicines(GenericName);
            CREATE INDEX IF NOT EXISTS IX_Medicines_Barcode ON Medicines(Barcode);
            CREATE INDEX IF NOT EXISTS IX_Medicines_Sku ON Medicines(Sku);
            CREATE INDEX IF NOT EXISTS IX_Medicines_Category ON Medicines(Category);
            CREATE INDEX IF NOT EXISTS IX_Medicines_Status ON Medicines(Status);
            
            CREATE INDEX IF NOT EXISTS IX_MedicineBatches_MedicineId ON MedicineBatches(MedicineId);
            CREATE INDEX IF NOT EXISTS IX_MedicineBatches_BatchNumber ON MedicineBatches(BatchNumber);
            CREATE INDEX IF NOT EXISTS IX_MedicineBatches_ExpiryDate ON MedicineBatches(ExpiryDate);
            CREATE INDEX IF NOT EXISTS IX_MedicineBatches_SupplierId ON MedicineBatches(SupplierId);
            
            CREATE INDEX IF NOT EXISTS IX_Sales_InvoiceNumber ON Sales(InvoiceNumber);
            CREATE INDEX IF NOT EXISTS IX_Sales_InvoiceDate ON Sales(InvoiceDate);
            CREATE INDEX IF NOT EXISTS IX_Sales_CustomerId ON Sales(CustomerId);
            
            CREATE INDEX IF NOT EXISTS IX_SaleItems_SaleId ON SaleItems(SaleId);
            CREATE INDEX IF NOT EXISTS IX_SaleItems_MedicineId ON SaleItems(MedicineId);
            CREATE INDEX IF NOT EXISTS IX_SaleItems_BatchId ON SaleItems(BatchId);
            
            CREATE INDEX IF NOT EXISTS IX_Purchases_SupplierId ON Purchases(SupplierId);
            CREATE INDEX IF NOT EXISTS IX_Purchases_PurchaseDate ON Purchases(PurchaseDate);
            CREATE INDEX IF NOT EXISTS IX_Purchases_Status ON Purchases(Status);
            
            CREATE INDEX IF NOT EXISTS IX_PurchaseItems_PurchaseId ON PurchaseItems(PurchaseId);
            
            CREATE INDEX IF NOT EXISTS IX_Payments_TransactionId ON Payments(TransactionId);
            CREATE INDEX IF NOT EXISTS IX_Payments_PaymentDate ON Payments(PaymentDate);
            
            CREATE INDEX IF NOT EXISTS IX_Expenses_ExpenseDate ON Expenses(ExpenseDate);
            CREATE INDEX IF NOT EXISTS IX_Expenses_Category ON Expenses(Category);
            
            CREATE INDEX IF NOT EXISTS IX_Returns_ReturnDate ON Returns(ReturnDate);
            CREATE INDEX IF NOT EXISTS IX_Returns_OriginalTransactionId ON Returns(OriginalTransactionId);
            
            CREATE INDEX IF NOT EXISTS IX_StockMovements_MedicineId ON StockMovements(MedicineId);
            CREATE INDEX IF NOT EXISTS IX_StockMovements_BatchId ON StockMovements(BatchId);
            CREATE INDEX IF NOT EXISTS IX_StockMovements_MovementDate ON StockMovements(MovementDate);
            
            CREATE INDEX IF NOT EXISTS IX_AuditLogs_Timestamp ON AuditLogs(Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AuditLogs_UserId ON AuditLogs(UserId);
            CREATE INDEX IF NOT EXISTS IX_AuditLogs_EntityType ON AuditLogs(EntityType, EntityId);
            
            CREATE INDEX IF NOT EXISTS IX_ExcelSyncQueue_Status ON ExcelSyncQueue(Status);
            
            CREATE INDEX IF NOT EXISTS IX_Suppliers_Name ON Suppliers(Name);
            CREATE INDEX IF NOT EXISTS IX_Customers_Phone ON Customers(Phone);
        ");

        // ─── Default Settings ───
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            INSERT OR IGNORE INTO AppSettings (Key, Value, Description) VALUES
            ('ShopName', 'My Medical Shop', 'Name of the pharmacy'),
            ('ShopAddress', '', 'Shop address'),
            ('ShopPhone', '', 'Shop phone number'),
            ('ShopGstin', '', 'Shop GSTIN'),
            ('InvoicePrefix', 'INV', 'Prefix for invoice numbers'),
            ('InvoiceNextNumber', '1', 'Next invoice sequence number'),
            ('DefaultGstRate', '12', 'Default GST rate percentage'),
            ('IsInterState', '0', 'Whether transactions are inter-state (IGST) by default'),
            ('ExcelWorkbookPath', '', 'Custom Excel workbook location (blank for default)'),
            ('NearExpiryDays', '90', 'Days before expiry to flag as near-expiry'),
            ('BackupRetentionDays', '30', 'Number of days to retain backups'),
            ('AutoBackupEnabled', '1', 'Enable automatic daily backups'),
            ('SchemaVersion', '1', 'Current database schema version'),
            ('DarkMode', '0', 'Enable dark mode theme'),
            ('LicenseNumber', '', 'Drug license number'),
            ('PharmacistName', '', 'Registered pharmacist name');
        ");

        // ─── Default Admin User (password: admin123 — must be changed on first login) ───
        // BCrypt hash of 'admin123'
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            INSERT OR IGNORE INTO Users (Username, DisplayName, PasswordHash, Role, IsActive)
            VALUES ('admin', 'Administrator', '$2a$11$K3wG5GzQFk3u8sYQKV9X4OqzEq1VgpKJFXhMPxfQH3kFnGqCf.NSy', 0, 1);
        ");
    }
}
