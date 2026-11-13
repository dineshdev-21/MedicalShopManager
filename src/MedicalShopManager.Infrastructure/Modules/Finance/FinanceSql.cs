using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>Null-safe readers and parameter helpers for the Finance repositories (parameterised SQL only).</summary>
internal static class FinanceSql
{
    public const string DateFormat = "yyyy-MM-dd";
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static string? Str(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
    }

    public static long Long(this SqliteDataReader r, string column, long fallback = 0)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? fallback : r.GetInt64(i);
    }

    public static long? LongOrNull(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    public static int Int(this SqliteDataReader r, string column, int fallback = 0)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? fallback : r.GetInt32(i);
    }

    public static int? IntOrNull(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    public static decimal Dec(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? 0m : Math.Round(Convert.ToDecimal(r.GetDouble(i)), 4);
    }

    public static bool Bool(this SqliteDataReader r, string column, bool fallback = false)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? fallback : r.GetInt64(i) != 0;
    }

    public static DateTime Date(this SqliteDataReader r, string column) => DateOrNull(r, column) ?? DateTime.MinValue;

    public static DateTime? DateOrNull(this SqliteDataReader r, string column)
    {
        var text = Str(r, column);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value) ? value : null;
    }

    public static void Add(this SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public static void AddDate(this SqliteCommand cmd, string name, DateTime? value) =>
        cmd.Parameters.AddWithValue(name, value is { } d ? d.ToString(DateFormat, CultureInfo.InvariantCulture) : DBNull.Value);

    public static void AddDateTime(this SqliteCommand cmd, string name, DateTime value) =>
        cmd.Parameters.AddWithValue(name, value.ToString(DateTimeFormat, CultureInfo.InvariantCulture));

    public static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>"%text%" for LIKE with the wildcard characters in the user's text escaped (use ESCAPE '\').</summary>
    public static string? LikeOrNull(string? text) => string.IsNullOrWhiteSpace(text)
        ? null
        : "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static SqliteCommand Command(this SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static async Task<int> ExecAsync(this SqliteConnection conn, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = conn.Command(tx, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(this SqliteConnection conn, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = conn.Command(tx, sql, args);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    public static async Task<long> LongAsync(this SqliteConnection conn, SqliteTransaction? tx, string sql, params (string, object?)[] args) =>
        Convert.ToInt64(await conn.ScalarAsync(tx, sql, args) ?? 0L, CultureInfo.InvariantCulture);

    public static string D(DateTime date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);
    public static string Now() => DateTime.Now.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
}
