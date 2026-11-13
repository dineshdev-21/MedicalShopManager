using Microsoft.Data.Sqlite;
using MedicalShopManager.Infrastructure.Database.Migrations;

namespace MedicalShopManager.Infrastructure.Modules.DoctorsFollowups;

/// <summary>
/// Doctors &amp; follow-ups (6.2), migration 17: the doctor master (Doctors), refill follow-ups (PatientFollowups),
/// their remarks (FollowupNotes), per-composition / per-medicine refill rules (FollowupRefillRules) and the four
/// DoctorsFollowups.* settings. Only new tables; depends on v1–v3 only. Rows that point at other modules' tables
/// (customers, bills, medicines) carry an index but no SQL foreign key, so this module never blocks their changes
/// (ARCHITECTURE-MODULES §10); the service checks the rows exist.
/// </summary>
public sealed class Migration0017DoctorsFollowups : IMigration
{
    public int Version => 17;
    public string Description => "Doctors & follow-ups: doctor master, refill follow-ups, notes, refill rules";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS Doctors (
                Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                Name               TEXT NOT NULL,
                RegistrationNo     TEXT COLLATE NOCASE,
                Qualification      TEXT,
                Specialization     TEXT,
                ClinicName         TEXT,
                Phone              TEXT,
                Email              TEXT,
                Address            TEXT,
                City               TEXT,
                CommissionPercent  REAL NOT NULL DEFAULT 0 CHECK (CommissionPercent >= 0 AND CommissionPercent <= 100),
                CommissionOnProfit INTEGER NOT NULL DEFAULT 0,
                Notes              TEXT,
                IsActive           INTEGER NOT NULL DEFAULT 1,
                CreatedAt          TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt          TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Doctors_RegistrationNo ON Doctors(RegistrationNo) WHERE RegistrationNo IS NOT NULL;
            CREATE INDEX IF NOT EXISTS IX_Doctors_Name ON Doctors(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_Doctors_Phone ON Doctors(Phone);

            CREATE TABLE IF NOT EXISTS PatientFollowups (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                CustomerId          INTEGER NOT NULL,
                PatientName         TEXT,
                PatientKey          TEXT NOT NULL DEFAULT '',
                ItemKey             TEXT NOT NULL,
                MedicineId          INTEGER,
                MedicineName        TEXT NOT NULL,
                CompositionId       INTEGER,
                CompositionName     TEXT,
                SaleId              INTEGER,
                InvoiceNumber       TEXT,
                SaleDate            TEXT NOT NULL,
                QuantityUnits       INTEGER NOT NULL CHECK (QuantityUnits > 0),
                UnitsPerDay         REAL NOT NULL CHECK (UnitsPerDay > 0),
                UnitsPerDayIsManual INTEGER NOT NULL DEFAULT 0,
                NextRefillDate      TEXT NOT NULL,
                SnoozedUntil        TEXT,
                Status              INTEGER NOT NULL DEFAULT 1,
                Source              INTEGER NOT NULL DEFAULT 1,
                IsHelpful           INTEGER,
                ActionedAt          TEXT,
                ActionedByUserId    INTEGER,
                ActionedByName      TEXT,
                ClosedBySaleId      INTEGER,
                StatusReason        TEXT,
                CreatedAt           TEXT NOT NULL,
                CreatedByUserId     INTEGER,
                CreatedByName       TEXT,
                UpdatedAt           TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_PatientFollowups_SaleItem ON PatientFollowups(SaleId, ItemKey) WHERE SaleId IS NOT NULL;
            CREATE INDEX IF NOT EXISTS IX_PatientFollowups_Customer ON PatientFollowups(CustomerId, PatientKey, ItemKey);
            CREATE INDEX IF NOT EXISTS IX_PatientFollowups_StatusDue ON PatientFollowups(Status, NextRefillDate);
            CREATE INDEX IF NOT EXISTS IX_PatientFollowups_SaleDate ON PatientFollowups(SaleDate);
            CREATE INDEX IF NOT EXISTS IX_PatientFollowups_MedicineId ON PatientFollowups(MedicineId);
            CREATE INDEX IF NOT EXISTS IX_PatientFollowups_CompositionId ON PatientFollowups(CompositionId);

            CREATE TABLE IF NOT EXISTS FollowupNotes (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                FollowupId INTEGER NOT NULL REFERENCES PatientFollowups(Id),
                Note       TEXT NOT NULL,
                UserId     INTEGER,
                UserName   TEXT,
                IsActive   INTEGER NOT NULL DEFAULT 1,
                CreatedAt  TEXT NOT NULL,
                UpdatedAt  TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_FollowupNotes_FollowupId ON FollowupNotes(FollowupId);

            CREATE TABLE IF NOT EXISTS FollowupRefillRules (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                CompositionId INTEGER UNIQUE,
                MedicineId    INTEGER UNIQUE,
                IsChronic     INTEGER NOT NULL DEFAULT 1,
                UnitsPerDay   REAL CHECK (UnitsPerDay IS NULL OR UnitsPerDay > 0),
                UpdatedAt     TEXT NOT NULL,
                CHECK ((CompositionId IS NULL) <> (MedicineId IS NULL))
            );");

        foreach (var (key, value, description) in new[]
        {
            ("DoctorsFollowups.DefaultUnitsPerDay", "0.5", "Refill follow-ups: units consumed per day when the item has no rule"),
            ("DoctorsFollowups.UpcomingDays", "7", "Refill follow-ups: show reminders this many days before they are due"),
            ("DoctorsFollowups.GraceDays", "7", "Refill follow-ups: an overdue reminder lapses (Expired) after this many days"),
            ("DoctorsFollowups.LookbackDays", "120", "Refill follow-ups: bills of the last N days are scanned for chronic medicines")
        })
        {
            await MigrationSql.ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO AppSettings (Key, Value, Description) VALUES (@Key, @Value, @Description);",
                ("@Key", key), ("@Value", value), ("@Description", description));
        }
    }
}
