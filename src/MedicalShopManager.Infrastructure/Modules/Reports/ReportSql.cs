using System.Globalization;
using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>
/// ADO.NET helpers for the read-only report queries. Every user value is a parameter; SQL text contains only
/// constants. Dates are compared as text: <c>col &gt;= @From AND col &lt; @ToEx</c> works for both 'yyyy-MM-dd' and
/// 'yyyy-MM-dd HH:mm:ss' columns.
/// </summary>
internal static class ReportSql
{
    public const string DateFormat = "yyyy-MM-dd";

    public static string Date(DateTime value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static SqliteCommand Command(this SqliteConnection conn, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    /// <summary>@From and @ToEx for a report's date range.</summary>
    public static (string, object?)[] Range(ReportParameters p, params (string, object?)[] more) =>
        new (string, object?)[] { ("@From", Date(p.From)), ("@ToEx", Date(p.ToExclusive)) }.Concat(more).ToArray();

    public static async Task<List<T>> QueryAsync<T>(DatabaseManager db, string sql, Func<SqliteDataReader, T> map,
        CancellationToken ct, params (string, object?)[] parameters)
    {
        using var conn = db.CreateNewConnection();
        using var cmd = conn.Command(sql, parameters);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
            rows.Add(map(reader));
        return rows;
    }

    public static async Task<long> ScalarLongAsync(DatabaseManager db, string sql, CancellationToken ct, params (string, object?)[] parameters)
    {
        using var conn = db.CreateNewConnection();
        using var cmd = conn.Command(sql, parameters);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static string? Str(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
    }

    public static string Text(this SqliteDataReader r, string column) => r.Str(column) ?? string.Empty;

    public static long Long(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
    }

    public static long? LongOrNull(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
    }

    public static bool Bool(this SqliteDataReader r, string column) => r.Long(column) != 0;

    /// <summary>REAL percent column read as decimal rounded to 2 dp (12.0 → 12).</summary>
    public static decimal Percent(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? 0m : Math.Round(Convert.ToDecimal(r.GetDouble(i)), 2, MidpointRounding.AwayFromZero);
    }

    public static DateTime Day(this SqliteDataReader r, string column) => ParseDay(r.Str(column)) ?? DateTime.MinValue;

    public static DateTime? DayOrNull(this SqliteDataReader r, string column) => ParseDay(r.Str(column));

    /// <summary>Date part of a stored 'yyyy-MM-dd…' text (date, timestamp or ISO-8601).</summary>
    public static DateTime? ParseDay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 10) return null;
        return DateTime.TryParseExact(text[..10], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>"Walk-in" for blank customer names.</summary>
    public static string PartyOrWalkIn(string? name) => string.IsNullOrWhiteSpace(name) ? "Walk-in" : name.Trim();
}
