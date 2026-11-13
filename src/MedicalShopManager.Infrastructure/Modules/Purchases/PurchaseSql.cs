using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MedicalShopManager.Infrastructure.Modules.Purchases;

/// <summary>Small ADO.NET helpers shared by the Purchases repositories (parameterised commands only).</summary>
internal static class PurchaseSql
{
    public const string DateFormat = "yyyy-MM-dd";
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static string Date(DateTime value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);
    public static string Stamp(DateTime value) => value.ToString("o", CultureInfo.InvariantCulture);
    public static object DateOrNull(DateTime? value) => value.HasValue ? Date(value.Value) : DBNull.Value;
    public static object OrNull(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    public static object OrNull(long? value) => value.HasValue ? value.Value : DBNull.Value;

    public static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static async Task<int> ExecAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(conn, tx, sql, parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(conn, tx, sql, parameters);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    // ─── Reader helpers ───
    public static long Long(this SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? 0 : r.GetInt64(r.GetOrdinal(column));
    public static long? LongOrNull(this SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetInt64(r.GetOrdinal(column));
    public static int Int(this SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? 0 : r.GetInt32(r.GetOrdinal(column));
    public static bool Bool(this SqliteDataReader r, string column) => !r.IsDBNull(r.GetOrdinal(column)) && r.GetInt64(r.GetOrdinal(column)) != 0;
    public static decimal Dec(this SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? 0m : r.GetDecimal(r.GetOrdinal(column));
    public static string Str(this SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? string.Empty : r.GetString(r.GetOrdinal(column));
    public static string? StrOrNull(this SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetString(r.GetOrdinal(column));

    public static DateTime DateValue(this SqliteDataReader r, string column) =>
        r.IsDBNull(r.GetOrdinal(column)) ? default : DateTime.Parse(r.GetString(r.GetOrdinal(column)), CultureInfo.InvariantCulture);

    public static DateTime? DateOrNullValue(this SqliteDataReader r, string column) =>
        r.IsDBNull(r.GetOrdinal(column)) ? null : DateTime.Parse(r.GetString(r.GetOrdinal(column)), CultureInfo.InvariantCulture);

    /// <summary>Escapes LIKE wildcards so user text matches literally (used with ESCAPE '\').</summary>
    public static string Like(string text) =>
        "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
