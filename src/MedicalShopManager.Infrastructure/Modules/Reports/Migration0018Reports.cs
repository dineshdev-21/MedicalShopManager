using Microsoft.Data.Sqlite;
using MedicalShopManager.Infrastructure.Database.Migrations;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>
/// v18 — Reports module:
/// <list type="bullet">
/// <item>ReportFavourites: reports starred per user (UserId 0 = no user signed in).</item>
/// <item>Settings Reports.B2clThresholdPaisa (GSTR-1 B2C-large limit, ₹1,00,000 since 1 Aug 2024) and
/// Reports.HsnUqc (unit quantity code printed in the HSN summary).</item>
/// </list>
/// Reports only read other modules' tables; this is the only schema the module owns.
/// </summary>
public sealed class Migration0018Reports : IMigration
{
    public const string B2clThresholdKey = "Reports.B2clThresholdPaisa";
    public const string HsnUqcKey = "Reports.HsnUqc";

    public int Version => 18;
    public string Description => "Reports: favourites, GST report settings";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await MigrationSql.ExecuteAsync(connection, transaction, @"
            CREATE TABLE IF NOT EXISTS ReportFavourites (
                UserId      INTEGER NOT NULL DEFAULT 0,
                ReportId    TEXT NOT NULL,
                CreatedAt   TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (UserId, ReportId)
            );

            INSERT OR IGNORE INTO AppSettings (Key, Value, Description)
            VALUES ('Reports.B2clThresholdPaisa', '10000000', 'Reports: GSTR-1 B2C-large invoice value limit in paisa (inter-state, unregistered buyer)');

            INSERT OR IGNORE INTO AppSettings (Key, Value, Description)
            VALUES ('Reports.HsnUqc', 'NOS', 'Reports: unit quantity code (UQC) in the GSTR-1 HSN summary');");
    }
}
