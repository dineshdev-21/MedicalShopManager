using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Infrastructure.Database.Migrations;

/// <summary>
/// v3 — foundation tables shared by all modules: DocumentSequences (numbering format, one row per
/// <see cref="DocumentType"/>) and DocumentSequenceCounters (next number per type and period; the period is the
/// financial-year start year for sequences that reset every FY, else 0 — MASTER-SPEC PrefixSequence unique (doc_type, FY)).
/// The sale sequence takes over the old AppSettings InvoicePrefix / InvoiceNextNumber and keeps the old
/// "INV-yyyy-00001" format so existing invoice numbers continue without a gap; those settings are then removed.
/// </summary>
public sealed class Migration0003FoundationTables : IMigration
{
    public int Version => 3;
    public string Description => "Foundation: document sequences and per-period counters (prefix, template, padding, FY reset)";

    /// <summary>Default prefix per document type for new installs (sale is migrated from settings).</summary>
    private static readonly (DocumentType Type, string Prefix)[] Defaults =
    {
        (DocumentType.SaleReturn, "SR"),
        (DocumentType.CreditNote, "CN"),
        (DocumentType.Purchase, "PUR"),
        (DocumentType.PurchaseReturn, "PR"),
        (DocumentType.DebitNote, "DN"),
        (DocumentType.PurchaseOrder, "PO"),
        (DocumentType.Receipt, "RCT"),
        (DocumentType.Payment, "PAY"),
        (DocumentType.Expense, "EXP"),
        (DocumentType.StockAdjustment, "ADJ"),
        (DocumentType.StockAudit, "AUD")
    };

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS DocumentSequences (
                DocumentType               INTEGER PRIMARY KEY,
                Prefix                     TEXT NOT NULL DEFAULT '',
                Template                   TEXT NOT NULL DEFAULT '{PREFIX}/{FY2}/{SEQ}',
                Padding                    INTEGER NOT NULL DEFAULT 5,
                ResetEachFinancialYear     INTEGER NOT NULL DEFAULT 1,
                UpdatedAt                  TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS DocumentSequenceCounters (
                DocumentType   INTEGER NOT NULL REFERENCES DocumentSequences(DocumentType),
                Period         INTEGER NOT NULL,
                NextNumber     INTEGER NOT NULL DEFAULT 1 CHECK (NextNumber >= 1),
                PRIMARY KEY (DocumentType, Period)
            );");

        // Sale: continue the existing invoice series exactly where it stopped.
        var prefix = await MigrationSql.ScalarAsync(connection, transaction,
            "SELECT Value FROM AppSettings WHERE Key = 'InvoicePrefix'") as string;
        var nextText = await MigrationSql.ScalarAsync(connection, transaction,
            "SELECT Value FROM AppSettings WHERE Key = 'InvoiceNextNumber'") as string;
        var next = long.TryParse(nextText, out var n) && n >= 1 ? n : 1;

        await MigrationSql.ExecuteAsync(connection, transaction, @"
            INSERT OR IGNORE INTO DocumentSequences (DocumentType, Prefix, Template, Padding, ResetEachFinancialYear)
            VALUES (@Type, @Prefix, '{PREFIX}-{YYYY}-{SEQ}', 5, 0);
            INSERT OR IGNORE INTO DocumentSequenceCounters (DocumentType, Period, NextNumber) VALUES (@Type, 0, @Next);",
            ("@Type", (int)DocumentType.Sale),
            ("@Prefix", string.IsNullOrWhiteSpace(prefix) ? "INV" : prefix.Trim()),
            ("@Next", next));

        foreach (var (type, defaultPrefix) in Defaults)
        {
            await MigrationSql.ExecuteAsync(connection, transaction, @"
                INSERT OR IGNORE INTO DocumentSequences (DocumentType, Prefix, Template, Padding, ResetEachFinancialYear)
                VALUES (@Type, @Prefix, '{PREFIX}/{FY2}/{SEQ}', 5, 1);",
                ("@Type", (int)type), ("@Prefix", defaultPrefix));
        }

        await MigrationSql.ExecuteAsync(connection, transaction,
            "DELETE FROM AppSettings WHERE Key IN ('InvoicePrefix', 'InvoiceNextNumber');");
    }
}
