using Microsoft.Data.Sqlite;

namespace MedicalShopManager.Infrastructure.Database.Migrations;

/// <summary>SQL helpers for <see cref="IMigration"/> implementations. Every call is bound to the migration's transaction.</summary>
public static class MigrationSql
{
    /// <summary>Executes one or more statements (separated by ';') with optional named parameters.</summary>
    public static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync();
        return result == DBNull.Value ? null : result;
    }

    /// <summary>True when <paramref name="table"/> has a column named <paramref name="column"/> (case-insensitive).</summary>
    public static async Task<bool> ColumnExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@Table) WHERE name = @Column COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@Table", table);
        cmd.Parameters.AddWithValue("@Column", column);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    /// <summary>Adds a column only if it is missing, so a migration can be re-run safely on a partially upgraded copy.</summary>
    public static async Task AddColumnIfMissingAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string column, string definition)
    {
        EnsureIdentifier(table, nameof(table));
        EnsureIdentifier(column, nameof(column));
        if (!await ColumnExistsAsync(connection, transaction, table, column))
            await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    /// <summary>Renames a column when the old name still exists and the new one does not.</summary>
    public static async Task RenameColumnIfExistsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string oldName, string newName)
    {
        EnsureIdentifier(table, nameof(table));
        EnsureIdentifier(oldName, nameof(oldName));
        EnsureIdentifier(newName, nameof(newName));
        if (await ColumnExistsAsync(connection, transaction, table, oldName)
            && !await ColumnExistsAsync(connection, transaction, table, newName))
        {
            await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} RENAME COLUMN {oldName} TO {newName};");
        }
    }

    /// <summary>
    /// Table and column names are spliced into DDL (SQLite cannot bind identifiers), so only plain identifiers are
    /// accepted: letters, digits and '_', not starting with a digit.
    /// </summary>
    internal static void EnsureIdentifier(string name, string parameter)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException($"'{name}' is not a valid SQL identifier.", parameter);
    }
}
