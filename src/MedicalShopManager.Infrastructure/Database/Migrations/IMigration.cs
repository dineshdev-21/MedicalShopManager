using Microsoft.Data.Sqlite;

namespace MedicalShopManager.Infrastructure.Database.Migrations;

/// <summary>
/// One schema change. DatabaseManager discovers every non-abstract implementation with a public parameterless
/// constructor in the Infrastructure assembly, sorts them by <see cref="Version"/> and applies the ones not yet in
/// SchemaMigrations — each in its own transaction, recorded in that same transaction.
///
/// Reserved version numbers (docs/ARCHITECTURE-MODULES.md §Migrations):
///   1 initial schema · 2 item master v2 · 3 foundation tables (document sequences)
///   10 billing · 11 sales-returns · 12 purchases · 13 stock · 14 audit-labels · 15 orderbook · 16 finance
///   17 doctors-followups · 18 reports · 19 control-center · 20 dashboard-search
///   Overflow: module with reserved number V may also use 100 + (V − 10) × 10 … +9 (billing 100–109, …, dashboard-search 200–209).
/// A migration must never be edited once released; write a new one instead.
/// </summary>
public interface IMigration
{
    int Version { get; }
    string Description { get; }

    /// <summary>
    /// Applies the change. Every command must run on <paramref name="transaction"/> (use <see cref="MigrationSql"/>).
    /// Do not commit, roll back or change PRAGMAs here; the runner owns the transaction.
    /// </summary>
    Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction);
}
