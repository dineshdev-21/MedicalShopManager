-- Database created by the ORIGINAL v1 DatabaseManager (MedicalShopManager-after-repair, schema v1, before the
-- migration registry), then filled with sample rows; produced with: sqlite3 v1.db .dump. Do not edit by hand.
PRAGMA foreign_keys=OFF;
BEGIN TRANSACTION;
CREATE TABLE SchemaMigrations (
                Version     INTEGER PRIMARY KEY,
                AppliedAt   TEXT NOT NULL DEFAULT (datetime('now')),
                Description TEXT
            );
INSERT INTO SchemaMigrations VALUES(1,'2026-11-04 12:15:16','Initial schema: all tables, indexes, default settings, admin user');
CREATE TABLE Medicines (
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
INSERT INTO Medicines VALUES(1,'Dolo 650','Paracetamol 650mg',NULL,NULL,0,NULL,15,NULL,NULL,'30049099',12.0,1,20,0,NULL,NULL,213,1,0,NULL,'2026-11-04 12:23:03','2026-11-04 12:23:03');
INSERT INTO Medicines VALUES(2,'Syrup X',NULL,NULL,NULL,0,NULL,0,NULL,NULL,NULL,5.0,4,5,0,NULL,NULL,9000,NULL,0,NULL,'2026-11-04 12:23:03','2026-11-04 12:23:03');
CREATE TABLE MedicineBatches (
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
INSERT INTO MedicineBatches VALUES(1,1,'B1',NULL,'2027-06-30','2026-04-10',1,150,140,0,150,213,226,0.0,12.0,1,'2026-11-04 12:23:03','2026-11-04 12:23:03');
INSERT INTO MedicineBatches VALUES(2,2,'S1',NULL,'2027-01-31','2026-04-10',1,10,10,0,6000,9000,9500,0.0,5.0,1,'2026-11-04 12:23:03','2026-11-04 12:23:03');
CREATE TABLE Suppliers (
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
INSERT INTO Suppliers VALUES(1,'Pharma Dist',NULL,NULL,NULL,NULL,'27AAAAA0000A1Z5',NULL,1,0,'2026-11-04 12:23:03','2026-11-04 12:23:03');
CREATE TABLE Customers (
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
INSERT INTO Customers VALUES(1,'Ravi','9999999999',NULL,NULL,NULL,1,'2026-11-04 12:23:03','2026-11-04 12:23:03');
CREATE TABLE Sales (
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
INSERT INTO Sales VALUES(1,'INV-2026-00007','2026-05-01',1,NULL,NULL,2130,0,0,0,0,0,2130,2130,0,0,0,0,0,NULL,NULL,NULL,'2026-11-04 12:23:03');
CREATE TABLE SaleItems (
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
INSERT INTO SaleItems VALUES(1,1,1,'Dolo 650',1,'B1','2027-06-30',NULL,10,213,0.0,0,0,0.0,0,0,0,0,0,2130);
CREATE TABLE Purchases (
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
INSERT INTO Purchases VALUES(1,'PD-77',1,'Pharma Dist','2026-04-10',22500,0,0,25200,0,0,0,2,NULL,NULL,'2026-11-04 12:23:03','2026-11-04 12:23:03');
CREATE TABLE PurchaseItems (
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
INSERT INTO PurchaseItems VALUES(1,1,1,'Dolo 650','B1','2027-06-30',NULL,150,0,150,0,0,0.0,0,0.0,0,22500);
CREATE TABLE Payments (
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
INSERT INTO Payments VALUES(1,1,NULL,'2026-05-01',0,0,2130,NULL,NULL,NULL,'2026-11-04 12:23:03');
CREATE TABLE Expenses (
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
INSERT INTO Expenses VALUES(1,'2026-05-02',1,NULL,50000,0,NULL,NULL,NULL,'2026-11-04 12:23:03');
CREATE TABLE Returns (
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
INSERT INTO Returns VALUES(1,NULL,NULL,0,'2026-05-03',1,'Dolo 650',1,NULL,1,213,NULL,0,NULL,'2026-11-04 12:23:03');
CREATE TABLE StockMovements (
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
INSERT INTO StockMovements VALUES(1,'2026-05-01',1,'Dolo 650',1,'B1',1,0,10,140,'INV-2026-00007',NULL,NULL,'2026-11-04 12:23:03');
CREATE TABLE Users (
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
INSERT INTO Users VALUES(1,'admin','Administrator','$2a$11$K3wG5GzQFk3u8sYQKV9X4OqzEq1VgpKJFXhMPxfQH3kFnGqCf.NSy',0,1,NULL,'2026-11-04 12:15:16','2026-11-04 12:15:16');
CREATE TABLE AuditLogs (
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
CREATE TABLE ExcelSyncQueue (
                    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    EntityType   INTEGER NOT NULL,
                    EntityId     INTEGER,
                    Status       INTEGER NOT NULL DEFAULT 0,
                    RetryCount   INTEGER NOT NULL DEFAULT 0,
                    ErrorMessage TEXT,
                    CreatedAt    TEXT NOT NULL DEFAULT (datetime('now')),
                    ProcessedAt  TEXT
                );
CREATE TABLE AppSettings (
                    Key         TEXT PRIMARY KEY,
                    Value       TEXT NOT NULL,
                    Description TEXT,
                    UpdatedAt   TEXT NOT NULL DEFAULT (datetime('now'))
                );
INSERT INTO AppSettings VALUES('ShopName','My Medical Shop','Name of the pharmacy','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('ShopAddress','','Shop address','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('ShopPhone','','Shop phone number','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('ShopGstin','','Shop GSTIN','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('InvoicePrefix','INV','Prefix for invoice numbers','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('InvoiceNextNumber','8','Next invoice sequence number','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('DefaultGstRate','12','Default GST rate percentage','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('IsInterState','0','Whether transactions are inter-state (IGST) by default','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('ExcelWorkbookPath','','Custom Excel workbook location (blank for default)','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('NearExpiryDays','90','Days before expiry to flag as near-expiry','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('BackupRetentionDays','30','Number of days to retain backups','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('AutoBackupEnabled','1','Enable automatic daily backups','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('SchemaVersion','1','Current database schema version','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('DarkMode','0','Enable dark mode theme','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('LicenseNumber','','Drug license number','2026-11-04 12:15:16');
INSERT INTO AppSettings VALUES('PharmacistName','','Registered pharmacist name','2026-11-04 12:15:16');
PRAGMA writable_schema=ON;
CREATE TABLE IF NOT EXISTS sqlite_sequence(name,seq);
DELETE FROM sqlite_sequence;
INSERT INTO sqlite_sequence VALUES('Users',1);
INSERT INTO sqlite_sequence VALUES('Suppliers',1);
INSERT INTO sqlite_sequence VALUES('Customers',1);
INSERT INTO sqlite_sequence VALUES('Medicines',2);
INSERT INTO sqlite_sequence VALUES('MedicineBatches',2);
INSERT INTO sqlite_sequence VALUES('Purchases',1);
INSERT INTO sqlite_sequence VALUES('PurchaseItems',1);
INSERT INTO sqlite_sequence VALUES('Sales',1);
INSERT INTO sqlite_sequence VALUES('SaleItems',1);
INSERT INTO sqlite_sequence VALUES('StockMovements',1);
INSERT INTO sqlite_sequence VALUES('Payments',1);
INSERT INTO sqlite_sequence VALUES('Expenses',1);
INSERT INTO sqlite_sequence VALUES('Returns',1);
CREATE INDEX IX_Medicines_Name ON Medicines(Name);
CREATE INDEX IX_Medicines_GenericName ON Medicines(GenericName);
CREATE INDEX IX_Medicines_Barcode ON Medicines(Barcode);
CREATE INDEX IX_Medicines_Sku ON Medicines(Sku);
CREATE INDEX IX_Medicines_Category ON Medicines(Category);
CREATE INDEX IX_Medicines_Status ON Medicines(Status);
CREATE INDEX IX_MedicineBatches_MedicineId ON MedicineBatches(MedicineId);
CREATE INDEX IX_MedicineBatches_BatchNumber ON MedicineBatches(BatchNumber);
CREATE INDEX IX_MedicineBatches_ExpiryDate ON MedicineBatches(ExpiryDate);
CREATE INDEX IX_MedicineBatches_SupplierId ON MedicineBatches(SupplierId);
CREATE INDEX IX_Sales_InvoiceNumber ON Sales(InvoiceNumber);
CREATE INDEX IX_Sales_InvoiceDate ON Sales(InvoiceDate);
CREATE INDEX IX_Sales_CustomerId ON Sales(CustomerId);
CREATE INDEX IX_SaleItems_SaleId ON SaleItems(SaleId);
CREATE INDEX IX_SaleItems_MedicineId ON SaleItems(MedicineId);
CREATE INDEX IX_SaleItems_BatchId ON SaleItems(BatchId);
CREATE INDEX IX_Purchases_SupplierId ON Purchases(SupplierId);
CREATE INDEX IX_Purchases_PurchaseDate ON Purchases(PurchaseDate);
CREATE INDEX IX_Purchases_Status ON Purchases(Status);
CREATE INDEX IX_PurchaseItems_PurchaseId ON PurchaseItems(PurchaseId);
CREATE INDEX IX_Payments_TransactionId ON Payments(TransactionId);
CREATE INDEX IX_Payments_PaymentDate ON Payments(PaymentDate);
CREATE INDEX IX_Expenses_ExpenseDate ON Expenses(ExpenseDate);
CREATE INDEX IX_Expenses_Category ON Expenses(Category);
CREATE INDEX IX_Returns_ReturnDate ON Returns(ReturnDate);
CREATE INDEX IX_Returns_OriginalTransactionId ON Returns(OriginalTransactionId);
CREATE INDEX IX_StockMovements_MedicineId ON StockMovements(MedicineId);
CREATE INDEX IX_StockMovements_BatchId ON StockMovements(BatchId);
CREATE INDEX IX_StockMovements_MovementDate ON StockMovements(MovementDate);
CREATE INDEX IX_AuditLogs_Timestamp ON AuditLogs(Timestamp);
CREATE INDEX IX_AuditLogs_UserId ON AuditLogs(UserId);
CREATE INDEX IX_AuditLogs_EntityType ON AuditLogs(EntityType, EntityId);
CREATE INDEX IX_ExcelSyncQueue_Status ON ExcelSyncQueue(Status);
CREATE INDEX IX_Suppliers_Name ON Suppliers(Name);
CREATE INDEX IX_Customers_Phone ON Customers(Phone);
PRAGMA writable_schema=OFF;
COMMIT;
