using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MedicalShopManager.Infrastructure.Modules.DoctorsFollowups;

/// <summary>Null-safe readers and parameter helpers for the DoctorsFollowups repositories (parameterised SQL only).</summary>
internal static class DoctorsFollowupsSql
{
    public const string DateFormat = "yyyy-MM-dd";
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static string? Str(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
    }

    public static string Text(this SqliteDataReader r, string column) => Str(r, column) ?? string.Empty;

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

    public static decimal Dec(this SqliteDataReader r, string column) => DecOrNull(r, column) ?? 0m;

    public static decimal? DecOrNull(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : Math.Round(Convert.ToDecimal(r.GetDouble(i)), 4);
    }

    public static bool Bool(this SqliteDataReader r, string column, bool fallback = false)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? fallback : r.GetInt64(i) != 0;
    }

    public static bool? BoolOrNull(this SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i) != 0;
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

    public static void AddDecimal(this SqliteCommand cmd, string name, decimal? value) =>
        cmd.Parameters.AddWithValue(name, value is { } d ? (double)d : DBNull.Value);

    public static void AddBool(this SqliteCommand cmd, string name, bool? value) =>
        cmd.Parameters.AddWithValue(name, value is { } b ? (b ? 1 : 0) : DBNull.Value);

    /// <summary>"%text%" for LIKE with the wildcard characters in the user's text escaped (use ESCAPE '\').</summary>
    public static string Like(string text) =>
        "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>Null for blank search text, else the LIKE pattern.</summary>
    public static string? LikeOrNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : Like(text);

    public static SqliteCommand Command(this SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    public static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>"@p0, @p1, …" with the values bound (for IN lists of ids; no user text is concatenated).</summary>
    public static string InList(this SqliteCommand cmd, string prefix, IEnumerable<long> ids)
    {
        var names = new List<string>();
        foreach (var id in ids.Distinct())
        {
            var name = $"@{prefix}{names.Count}";
            cmd.Parameters.AddWithValue(name, id);
            names.Add(name);
        }
        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }
}
