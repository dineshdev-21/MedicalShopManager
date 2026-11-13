using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Modules.DoctorsFollowups;

namespace MedicalShopManager.Infrastructure.Modules.DoctorsFollowups;

/// <summary>Follow-up notes, purchase history for recommendations, customer / item look-ups and refill rules.</summary>
public sealed partial class FollowupRepository
{
    // ───────────────────────── Notes ─────────────────────────

    public async Task<IReadOnlyList<FollowupNote>> GetNotesAsync(long followupId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT * FROM FollowupNotes WHERE FollowupId = @Id AND IsActive = 1 ORDER BY CreatedAt DESC, Id DESC");
        cmd.Add("@Id", followupId);
        return await ReadNotesAsync(cmd);
    }

    public async Task<FollowupNote?> GetNoteAsync(long noteId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "SELECT * FROM FollowupNotes WHERE Id = @Id AND IsActive = 1");
        cmd.Add("@Id", noteId);
        return (await ReadNotesAsync(cmd)).FirstOrDefault();
    }

    public async Task<long> AddNoteAsync(long followupId, string note, long? userId, string? userName, DateTime now)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        var id = await InsertNoteAsync(conn, tx, followupId, note, userId, userName, now);
        using (var touch = conn.Command(tx, "UPDATE PatientFollowups SET UpdatedAt = @Now WHERE Id = @Id"))
        {
            touch.AddDateTime("@Now", now);
            touch.Add("@Id", followupId);
            await touch.ExecuteNonQueryAsync();
        }
        tx.Commit();
        return id;
    }

    public async Task<bool> UpdateNoteAsync(long noteId, string note, DateTime now)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "UPDATE FollowupNotes SET Note = @Note, UpdatedAt = @Now WHERE Id = @Id AND IsActive = 1");
        cmd.Add("@Note", note);
        cmd.AddDateTime("@Now", now);
        cmd.Add("@Id", noteId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteNoteAsync(long noteId, DateTime now)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "UPDATE FollowupNotes SET IsActive = 0, UpdatedAt = @Now WHERE Id = @Id AND IsActive = 1");
        cmd.AddDateTime("@Now", now);
        cmd.Add("@Id", noteId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static async Task<long> InsertNoteAsync(SqliteConnection conn, SqliteTransaction tx, long followupId, string note, long? userId,
        string? userName, DateTime now)
    {
        using var cmd = conn.Command(tx, @"
            INSERT INTO FollowupNotes (FollowupId, Note, UserId, UserName, IsActive, CreatedAt)
            VALUES (@FollowupId, @Note, @UserId, @UserName, 1, @Now);
            SELECT last_insert_rowid();");
        cmd.Add("@FollowupId", followupId);
        cmd.Add("@Note", note);
        cmd.Add("@UserId", userId);
        cmd.Add("@UserName", userName);
        cmd.AddDateTime("@Now", now);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<FollowupNote>> ReadNotesAsync(SqliteCommand cmd)
    {
        var list = new List<FollowupNote>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new FollowupNote
            {
                Id = r.Long("Id"),
                FollowupId = r.Long("FollowupId"),
                Note = r.Text("Note"),
                UserId = r.LongOrNull("UserId"),
                UserName = r.Str("UserName"),
                CreatedAt = r.Date("CreatedAt"),
                UpdatedAt = r.DateOrNull("UpdatedAt")
            });
        }
        return list;
    }

    // ───────────────────────── Recommendations ─────────────────────────

    public async Task<IReadOnlyList<FollowupPurchaseRow>> GetPurchaseHistoryAsync(DateTime from)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, $@"
            SELECT s.CustomerId, COALESCE(c.Name, s.CustomerName, '') AS CustomerName, c.Phone, s.PatientName, s.Id AS SaleId,
                   s.InvoiceDate, si.MedicineId, si.MedicineName, m.CompositionId, comp.Name AS CompositionName, si.Quantity AS Units
            FROM SaleItems si
            JOIN Sales s ON s.Id = si.SaleId
            LEFT JOIN Customers c ON c.Id = s.CustomerId
            {RuleJoins}
            WHERE s.Status = 1 AND s.CustomerId IS NOT NULL AND s.InvoiceDate >= @From AND si.Quantity > 0
              AND {IsChronicExpression} = 0
            ORDER BY s.CustomerId, s.InvoiceDate");
        cmd.AddDate("@From", from.Date);
        var rows = new List<FollowupPurchaseRow>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows.Add(new FollowupPurchaseRow(r.Long("CustomerId"), r.Text("CustomerName"), r.Str("Phone"), r.Str("PatientName"),
                r.Long("SaleId"), r.Date("InvoiceDate"), r.Long("MedicineId"), r.Text("MedicineName"), r.LongOrNull("CompositionId"),
                r.Str("CompositionName"), r.Long("Units")));
        }
        return rows;
    }

    public async Task<IReadOnlySet<(long CustomerId, string PatientKey, string ItemKey)>> GetPendingKeysAsync()
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "SELECT DISTINCT CustomerId, PatientKey, ItemKey FROM PatientFollowups WHERE Status = @Pending");
        cmd.Add("@Pending", (int)FollowupStatus.Pending);
        var keys = new HashSet<(long, string, string)>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) keys.Add((r.Long("CustomerId"), r.Text("PatientKey"), r.Text("ItemKey")));
        return keys;
    }

    // ───────────────────────── Look-ups ─────────────────────────

    public async Task<CustomerFollowupSummary?> GetCustomerSummaryAsync(long customerId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT c.Id, c.Name, c.Phone, c.Address,
                   (SELECT COUNT(*) FROM Sales s WHERE s.CustomerId = c.Id AND s.Status = 1) AS SalesCount,
                   (SELECT COALESCE(SUM(s.GrandTotalPaisa), 0) FROM Sales s WHERE s.CustomerId = c.Id AND s.Status = 1) AS TotalPaisa,
                   (SELECT MIN(s.InvoiceDate) FROM Sales s WHERE s.CustomerId = c.Id AND s.Status = 1) AS FirstSale,
                   (SELECT MAX(s.InvoiceDate) FROM Sales s WHERE s.CustomerId = c.Id AND s.Status = 1) AS LastSale
            FROM Customers c WHERE c.Id = @Id");
        cmd.Add("@Id", customerId);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return CustomerMetrics.Build(r.Long("Id"), r.Text("Name"), r.Str("Phone"), r.Str("Address"), r.Int("SalesCount"),
            r.Long("TotalPaisa"), r.DateOrNull("FirstSale"), r.DateOrNull("LastSale"));
    }

    public async Task<IReadOnlyList<FollowupCustomerOption>> SearchCustomersAsync(string? text, int limit = 25)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT Id, Name, Phone FROM Customers
            WHERE IsActive = 1 AND (@Search IS NULL OR Name LIKE @Search ESCAPE '\' OR Phone LIKE @Search ESCAPE '\')
            ORDER BY Name COLLATE NOCASE, Id
            LIMIT @Limit");
        cmd.Add("@Search", DoctorsFollowupsSql.LikeOrNull(text));
        cmd.Add("@Limit", Math.Clamp(limit, 1, 200));
        var list = new List<FollowupCustomerOption>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new FollowupCustomerOption(r.Long("Id"), r.Text("Name"), r.Str("Phone")));
        return list;
    }

    private const string SelectItem = @"
        SELECT m.Id AS MedicineId, m.Name, m.CompositionId, comp.Name AS CompositionName, COALESCE(m.UnitsPerPack, 1) AS UnitsPerPack,
               COALESCE(rm.IsChronic, rc.IsChronic, comp.IsChronic, 0) AS IsChronic, COALESCE(rm.UnitsPerDay, rc.UnitsPerDay) AS UnitsPerDay
        FROM Medicines m
        LEFT JOIN Compositions comp ON comp.Id = m.CompositionId
        LEFT JOIN FollowupRefillRules rm ON rm.MedicineId = m.Id
        LEFT JOIN FollowupRefillRules rc ON rc.CompositionId = m.CompositionId";

    public async Task<IReadOnlyList<FollowupItemOption>> SearchItemsAsync(string? text, int limit = 30)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectItem + @"
            WHERE (@Search IS NULL OR m.Name LIKE @Search ESCAPE '\' OR m.Composition LIKE @Search ESCAPE '\')
            ORDER BY m.Name COLLATE NOCASE, m.Id
            LIMIT @Limit");
        cmd.Add("@Search", DoctorsFollowupsSql.LikeOrNull(text));
        cmd.Add("@Limit", Math.Clamp(limit, 1, 200));
        return await ReadItemsAsync(cmd);
    }

    public async Task<FollowupItemOption?> GetItemAsync(long medicineId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectItem + " WHERE m.Id = @Id");
        cmd.Add("@Id", medicineId);
        return (await ReadItemsAsync(cmd)).FirstOrDefault();
    }

    private static async Task<IReadOnlyList<FollowupItemOption>> ReadItemsAsync(SqliteCommand cmd)
    {
        var list = new List<FollowupItemOption>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new FollowupItemOption(r.Long("MedicineId"), r.Text("Name"), r.LongOrNull("CompositionId"), r.Str("CompositionName"),
                r.Bool("IsChronic"), r.DecOrNull("UnitsPerDay"), Math.Max(1, r.Int("UnitsPerPack", 1))));
        }
        return list;
    }

    // ───────────────────────── Refill rules ─────────────────────────

    public async Task<IReadOnlyList<RefillRule>> SearchRulesAsync(string? text, bool chronicOnly, int limit = 200)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT * FROM (
                SELECT comp.Id AS CompositionId, NULL AS MedicineId, comp.Name AS Name,
                       (SELECT COUNT(*) FROM Medicines m WHERE m.CompositionId = comp.Id) || ' item(s)' AS Detail,
                       comp.IsChronic AS DefaultIsChronic, r.IsChronic AS RuleIsChronic, r.UnitsPerDay AS UnitsPerDay
                FROM Compositions comp
                LEFT JOIN FollowupRefillRules r ON r.CompositionId = comp.Id
                WHERE (@Search IS NULL OR comp.Name LIKE @Search ESCAPE '\'
                       OR EXISTS (SELECT 1 FROM Medicines m WHERE m.CompositionId = comp.Id AND m.Name LIKE @Search ESCAPE '\'))
                  AND (@ChronicOnly = 0 OR COALESCE(r.IsChronic, comp.IsChronic, 0) = 1)
                UNION ALL
                SELECT NULL AS CompositionId, m.Id AS MedicineId, m.Name AS Name, m.Composition AS Detail,
                       0 AS DefaultIsChronic, r.IsChronic AS RuleIsChronic, r.UnitsPerDay AS UnitsPerDay
                FROM Medicines m
                LEFT JOIN FollowupRefillRules r ON r.MedicineId = m.Id
                WHERE (m.CompositionId IS NULL OR r.Id IS NOT NULL)
                  AND (@Search IS NULL OR m.Name LIKE @Search ESCAPE '\')
                  AND (@ChronicOnly = 0 OR COALESCE(r.IsChronic, 0) = 1)
            ) x
            ORDER BY x.Name COLLATE NOCASE, x.CompositionId, x.MedicineId
            LIMIT @Limit");
        cmd.Add("@Search", DoctorsFollowupsSql.LikeOrNull(text));
        cmd.Add("@ChronicOnly", chronicOnly ? 1 : 0);
        cmd.Add("@Limit", Math.Clamp(limit, 1, 2000));
        var list = new List<RefillRule>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new RefillRule
            {
                CompositionId = r.LongOrNull("CompositionId"),
                MedicineId = r.LongOrNull("MedicineId"),
                Name = r.Text("Name"),
                Detail = r.Str("Detail"),
                DefaultIsChronic = r.Bool("DefaultIsChronic"),
                RuleIsChronic = r.BoolOrNull("RuleIsChronic"),
                UnitsPerDay = r.DecOrNull("UnitsPerDay")
            });
        }
        return list;
    }

    public async Task SaveRuleAsync(RefillRule rule, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        using (var delete = conn.Command(tx, "DELETE FROM FollowupRefillRules WHERE CompositionId = @C OR MedicineId = @M"))
        {
            delete.Add("@C", rule.CompositionId);
            delete.Add("@M", rule.MedicineId);
            await delete.ExecuteNonQueryAsync();
        }

        // No override and no consumption = back to the default (no row).
        if (rule.RuleIsChronic is not null || rule.UnitsPerDay is not null)
        {
            using var insert = conn.Command(tx, @"
                INSERT INTO FollowupRefillRules (CompositionId, MedicineId, IsChronic, UnitsPerDay, UpdatedAt)
                VALUES (@C, @M, @Chronic, @PerDay, @Now)");
            insert.Add("@C", rule.CompositionId);
            insert.Add("@M", rule.MedicineId);
            insert.Add("@Chronic", rule.IsChronic ? 1 : 0);
            insert.AddDecimal("@PerDay", rule.UnitsPerDay);
            insert.AddDateTime("@Now", now);
            await insert.ExecuteNonQueryAsync();
        }
        tx.Commit();
    }
}
