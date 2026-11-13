using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Billing;

/// <summary>Auto-saved unsaved bills (BillingSaleDrafts). Timestamps are local time, 'yyyy-MM-dd HH:mm:ss'.</summary>
public sealed class SaleDraftRepository : ISaleDraftRepository
{
    private readonly DatabaseManager _db;

    public SaleDraftRepository(DatabaseManager db) => _db = db;

    public async Task<long> SaveAsync(long? draftId, SaleDraftSummary summary, string payload)
    {
        ArgumentNullException.ThrowIfNull(summary);
        using var conn = _db.CreateNewConnection();
        var now = DateTime.Now;
        if (draftId is > 0)
        {
            using var update = conn.Command(null, @"
                UPDATE BillingSaleDrafts SET CustomerName = @CustomerName, ItemCount = @ItemCount, TotalPaisa = @TotalPaisa,
                    Payload = @Payload, UpdatedAt = @Now WHERE Id = @Id");
            AddSummary(update, summary, payload);
            update.AddDateTime("@Now", now);
            update.Add("@Id", draftId.Value);
            if (await update.ExecuteNonQueryAsync() > 0) return draftId.Value;
        }

        using var insert = conn.Command(null, @"
            INSERT INTO BillingSaleDrafts (CustomerName, ItemCount, TotalPaisa, Payload, CreatedByUserId, CreatedAt, UpdatedAt)
            VALUES (@CustomerName, @ItemCount, @TotalPaisa, @Payload, @UserId, @Now, @Now);
            SELECT last_insert_rowid();");
        AddSummary(insert, summary, payload);
        insert.Add("@UserId", summary.CreatedByUserId);
        insert.AddDateTime("@Now", now);
        return Convert.ToInt64(await insert.ExecuteScalarAsync());
    }

    public async Task<IReadOnlyList<SaleDraftSummary>> ListAsync(DateTime updatedSince)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null,
            "SELECT * FROM BillingSaleDrafts WHERE UpdatedAt >= @Since ORDER BY UpdatedAt DESC, Id DESC LIMIT 200");
        cmd.AddDateTime("@Since", updatedSince);
        var list = new List<SaleDraftSummary>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(MapSummary(r));
        return list;
    }

    public async Task<SaleDraft?> GetAsync(long draftId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "SELECT * FROM BillingSaleDrafts WHERE Id = @Id");
        cmd.Add("@Id", draftId);
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new SaleDraft(MapSummary(r), r.Str("Payload") ?? "{}") : null;
    }

    public async Task<bool> DeleteAsync(long draftId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "DELETE FROM BillingSaleDrafts WHERE Id = @Id");
        cmd.Add("@Id", draftId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> PurgeAsync(DateTime cutoff)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "DELETE FROM BillingSaleDrafts WHERE UpdatedAt < @Cutoff");
        cmd.AddDateTime("@Cutoff", cutoff);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static void AddSummary(SqliteCommand cmd, SaleDraftSummary summary, string payload)
    {
        cmd.Add("@CustomerName", summary.CustomerName);
        cmd.Add("@ItemCount", summary.ItemCount);
        cmd.Add("@TotalPaisa", summary.TotalPaisa);
        cmd.Add("@Payload", payload);
    }

    private static SaleDraftSummary MapSummary(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        CustomerName = r.Str("CustomerName"),
        ItemCount = r.Int("ItemCount"),
        TotalPaisa = r.Long("TotalPaisa"),
        CreatedByUserId = r.LongOrNull("CreatedByUserId"),
        CreatedAt = r.Date("CreatedAt"),
        UpdatedAt = r.Date("UpdatedAt")
    };
}

/// <summary>Saved custom-charge names (BillingCustomCharges); removing one only deactivates it.</summary>
public sealed class CustomChargeRepository : ICustomChargeRepository
{
    private readonly DatabaseManager _db;

    public CustomChargeRepository(DatabaseManager db) => _db = db;

    public async Task<IReadOnlyList<CustomChargeOption>> GetActiveAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "SELECT Id, Name FROM BillingCustomCharges WHERE IsActive = 1 ORDER BY Name COLLATE NOCASE");
        var list = new List<CustomChargeOption>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new CustomChargeOption(r.Long("Id"), r.Str("Name") ?? string.Empty));
        return list;
    }

    public async Task<long> EnsureAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new BillingValidationException("Please add custom charge type");
        var trimmed = name.Trim();
        if (trimmed.Length > 60) throw new BillingValidationException("Custom charge name must be 60 characters or fewer");
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            INSERT INTO BillingCustomCharges (Name, IsActive) VALUES (@Name, 1)
            ON CONFLICT(Name) DO UPDATE SET IsActive = 1;
            SELECT Id FROM BillingCustomCharges WHERE Name = @Name;");
        cmd.Add("@Name", trimmed);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<bool> DeactivateAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "UPDATE BillingCustomCharges SET IsActive = 0 WHERE Id = @Id");
        cmd.Add("@Id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }
}
