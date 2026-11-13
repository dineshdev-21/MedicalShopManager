using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.ControlCenter;

/// <summary>
/// Batch access to the AppSettings rows and ControlCenterAssets images Control Center owns. Unlike
/// IAppSettingsRepository (one connection per key) a save writes every key of an area in <b>one transaction</b>, so a
/// half-saved business profile can never be printed. Parameterised SQL only.
/// </summary>
public sealed class ControlCenterSettingsStore
{
    private readonly DatabaseManager _db;

    public ControlCenterSettingsStore(DatabaseManager db) => _db = db;

    /// <summary>Values of the given keys (keys without a row are absent from the result).</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(IEnumerable<string> keys)
    {
        var list = keys.Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list.Count == 0) return result;

        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        var names = new List<string>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var name = "@k" + i;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, list[i]);
        }
        cmd.CommandText = $"SELECT Key, Value FROM AppSettings WHERE Key IN ({string.Join(", ", names)})";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        return result;
    }

    public async Task<string?> GetValueAsync(string key) =>
        (await GetAsync(new[] { key })).TryGetValue(key, out var value) ? value : null;

    /// <summary>Upserts all <paramref name="values"/> in one transaction; rolls back entirely on any failure.</summary>
    public async Task SetManyAsync(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return;

        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await SetManyAsync(conn, tx, values);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Upserts on the caller's transaction.</summary>
    public static async Task SetManyAsync(SqliteConnection conn, SqliteTransaction tx, IReadOnlyDictionary<string, string> values)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO AppSettings (Key, Value, UpdatedAt) VALUES (@Key, @Value, @Now)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt";
        var key = cmd.Parameters.Add("@Key", SqliteType.Text);
        var value = cmd.Parameters.Add("@Value", SqliteType.Text);
        cmd.Parameters.AddWithValue("@Now", now);
        foreach (var (k, v) in values)
        {
            if (string.IsNullOrWhiteSpace(k)) throw new ArgumentException("Setting keys cannot be blank.", nameof(values));
            key.Value = k;
            value.Value = v ?? string.Empty;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ─── Branding images ───

    public async Task<BrandingImage?> GetImageAsync(BrandingImageKind kind)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ContentType, Data, UpdatedAt FROM ControlCenterAssets WHERE AssetKey = @Key";
        cmd.Parameters.AddWithValue("@Key", BrandingImageRules.AssetKey(kind));
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var data = (byte[])reader.GetValue(1);
        var updated = DateTime.TryParse(reader.GetString(2), out var u) ? u : DateTime.MinValue;
        return new BrandingImage(kind, reader.GetString(0), data, updated);
    }

    public async Task SaveImageAsync(BrandingImageKind kind, string contentType, byte[] data)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ControlCenterAssets (AssetKey, ContentType, Data, UpdatedAt) VALUES (@Key, @Type, @Data, @Now)
            ON CONFLICT(AssetKey) DO UPDATE SET ContentType = excluded.ContentType, Data = excluded.Data, UpdatedAt = excluded.UpdatedAt";
        cmd.Parameters.AddWithValue("@Key", BrandingImageRules.AssetKey(kind));
        cmd.Parameters.AddWithValue("@Type", contentType);
        cmd.Parameters.Add("@Data", SqliteType.Blob).Value = data;
        cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> DeleteImageAsync(BrandingImageKind kind)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ControlCenterAssets WHERE AssetKey = @Key";
        cmd.Parameters.AddWithValue("@Key", BrandingImageRules.AssetKey(kind));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }
}
