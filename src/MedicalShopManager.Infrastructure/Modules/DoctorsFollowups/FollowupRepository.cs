using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Modules.DoctorsFollowups;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.DoctorsFollowups;

/// <summary>
/// PatientFollowups (generation, list, status changes). Reads Billing's Sales / SaleItems / Customers and the
/// kernel's Medicines / Compositions, never writes them. Notes, look-ups and refill rules are in the other part.
/// </summary>
public sealed partial class FollowupRepository : IFollowupRepository
{
    private const string SelectFollowup = @"
        SELECT f.*, COALESCE(c.Name, '') AS CustomerName, c.Phone AS CustomerPhone, COALESCE(c.Gender, 0) AS Gender,
               (SELECT COUNT(*) FROM FollowupNotes n WHERE n.FollowupId = f.Id AND n.IsActive = 1) AS NoteCount
        FROM PatientFollowups f
        LEFT JOIN Customers c ON c.Id = f.CustomerId";

    /// <summary>Joins that resolve a bill line's refill rule: medicine rule, else composition rule, else Compositions.IsChronic.</summary>
    private const string RuleJoins = @"
        LEFT JOIN Medicines m ON m.Id = si.MedicineId
        LEFT JOIN Compositions comp ON comp.Id = m.CompositionId
        LEFT JOIN FollowupRefillRules rm ON rm.MedicineId = si.MedicineId
        LEFT JOIN FollowupRefillRules rc ON rc.CompositionId = m.CompositionId";

    private const string IsChronicExpression = "COALESCE(rm.IsChronic, rc.IsChronic, comp.IsChronic, 0)";

    private readonly DatabaseManager _db;

    public FollowupRepository(DatabaseManager db) => _db = db;

    // ───────────────────────── Generation ─────────────────────────

    public async Task<IReadOnlyList<FollowupSaleLine>> GetChronicSaleLinesAsync(DateTime from)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, $@"
            SELECT s.Id AS SaleId, s.InvoiceNumber, s.InvoiceDate, s.CustomerId, COALESCE(c.Name, s.CustomerName, '') AS CustomerName,
                   s.PatientName, si.MedicineId, si.MedicineName, m.CompositionId, comp.Name AS CompositionName, si.Quantity AS Units,
                   COALESCE(rm.UnitsPerDay, rc.UnitsPerDay) AS RuleUnitsPerDay
            FROM SaleItems si
            JOIN Sales s ON s.Id = si.SaleId
            LEFT JOIN Customers c ON c.Id = s.CustomerId
            {RuleJoins}
            WHERE s.Status = 1 AND s.CustomerId IS NOT NULL AND s.InvoiceDate >= @From AND si.Quantity > 0
              AND {IsChronicExpression} = 1
            ORDER BY s.InvoiceDate, s.Id, si.Id");
        cmd.AddDate("@From", from.Date);
        var lines = new List<FollowupSaleLine>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            lines.Add(new FollowupSaleLine(r.Long("SaleId"), r.Text("InvoiceNumber"), r.Date("InvoiceDate"), r.Long("CustomerId"),
                r.Text("CustomerName"), r.Str("PatientName"), r.Long("MedicineId"), r.Text("MedicineName"), r.LongOrNull("CompositionId"),
                r.Str("CompositionName"), r.Long("Units"), r.DecOrNull("RuleUnitsPerDay")));
        }
        return lines;
    }

    public async Task<IReadOnlyList<ExistingFollowup>> GetForGenerationAsync(DateTime from)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT Id, CustomerId, PatientKey, ItemKey, SaleId, SaleDate, QuantityUnits, UnitsPerDay, UnitsPerDayIsManual, Status, Source
            FROM PatientFollowups
            WHERE Status = @Pending OR SaleDate >= @From");
        cmd.Add("@Pending", (int)FollowupStatus.Pending);
        cmd.AddDate("@From", from.Date);
        var list = new List<ExistingFollowup>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new ExistingFollowup(r.Long("Id"), r.Long("CustomerId"), r.Text("PatientKey"), r.Text("ItemKey"), r.LongOrNull("SaleId"),
                r.Date("SaleDate"), r.Long("QuantityUnits"), r.Dec("UnitsPerDay"), r.Bool("UnitsPerDayIsManual"),
                (FollowupStatus)r.Int("Status"), (FollowupSource)r.Int("Source")));
        }
        return list;
    }

    public async Task ApplyPlanAsync(FollowupPlan plan, long? userId, string? userName, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsEmpty) return;
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        foreach (var insert in plan.Inserts) await InsertCoreAsync(conn, tx, insert, userId, userName, now, skipIfBillLineExists: true);

        foreach (var u in plan.Updates)
        {
            using var cmd = conn.Command(tx, @"
                UPDATE PatientFollowups SET CustomerId = @CustomerId, PatientName = @PatientName, PatientKey = @PatientKey,
                    MedicineId = @MedicineId, MedicineName = @MedicineName, CompositionId = @CompositionId, CompositionName = @CompositionName,
                    InvoiceNumber = @Invoice, SaleDate = @SaleDate, QuantityUnits = @Units, UnitsPerDay = @PerDay, NextRefillDate = @Next,
                    UpdatedAt = @Now
                WHERE Id = @Id AND Status = @Pending");
            cmd.Add("@CustomerId", u.CustomerId);
            cmd.Add("@PatientName", DoctorsFollowupsSql.Blank(u.PatientName));
            cmd.Add("@PatientKey", u.PatientKey);
            cmd.Add("@MedicineId", u.MedicineId);
            cmd.Add("@MedicineName", u.MedicineName);
            cmd.Add("@CompositionId", u.CompositionId);
            cmd.Add("@CompositionName", DoctorsFollowupsSql.Blank(u.CompositionName));
            cmd.Add("@Invoice", u.InvoiceNumber);
            cmd.AddDate("@SaleDate", u.SaleDate);
            cmd.Add("@Units", u.QuantityUnits);
            cmd.AddDecimal("@PerDay", u.UnitsPerDay);
            cmd.AddDate("@Next", u.NextRefillDate);
            cmd.AddDateTime("@Now", now);
            cmd.Add("@Id", u.Id);
            cmd.Add("@Pending", (int)FollowupStatus.Pending);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var c in plan.Closures)
        {
            using var cmd = conn.Command(tx, @"
                UPDATE PatientFollowups SET Status = @Status, ClosedBySaleId = @BySale, StatusReason = @Reason, UpdatedAt = @Now
                WHERE Id = @Id AND Status = @Pending");
            cmd.Add("@Status", (int)c.Status);
            cmd.Add("@BySale", c.BySaleId);
            cmd.Add("@Reason", c.Reason);
            cmd.AddDateTime("@Now", now);
            cmd.Add("@Id", c.Id);
            cmd.Add("@Pending", (int)FollowupStatus.Pending);
            await cmd.ExecuteNonQueryAsync();
        }
        tx.Commit();
    }

    // ───────────────────────── List and read ─────────────────────────

    public async Task<IReadOnlyList<PatientFollowup>> QueryAsync(FollowupQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var actioned = query.Tab == FollowupTab.Actioned;
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectFollowup + $@"
            WHERE f.Status = @Status
              AND (@CustomerId IS NULL OR f.CustomerId = @CustomerId)
              AND (@Gender IS NULL OR COALESCE(c.Gender, 0) = @Gender)
              AND (@Search IS NULL OR c.Name LIKE @Search ESCAPE '\' OR c.Phone LIKE @Search ESCAPE '\'
                   OR f.PatientName LIKE @Search ESCAPE '\' OR f.MedicineName LIKE @Search ESCAPE '\'
                   OR f.CompositionName LIKE @Search ESCAPE '\' OR f.InvoiceNumber LIKE @Search ESCAPE '\')
            ORDER BY {(actioned ? "f.ActionedAt DESC" : "f.NextRefillDate")}, f.Id
            LIMIT @Limit");
        cmd.Add("@Status", (int)(actioned ? FollowupStatus.Done : FollowupStatus.Pending));
        cmd.Add("@CustomerId", query.CustomerId);
        cmd.Add("@Gender", query.Gender);
        cmd.Add("@Search", DoctorsFollowupsSql.LikeOrNull(query.SearchText));
        cmd.Add("@Limit", Math.Clamp(query.Limit, 1, 10000));
        return await ReadFollowupsAsync(cmd);
    }

    public async Task<PatientFollowup?> GetAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectFollowup + " WHERE f.Id = @Id");
        cmd.Add("@Id", id);
        return (await ReadFollowupsAsync(cmd)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<PatientFollowup>> GetManyAsync(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return Array.Empty<PatientFollowup>();
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectFollowup + $" WHERE f.Id IN ({cmd.InList("id", ids)}) ORDER BY f.NextRefillDate, f.Id";
        return await ReadFollowupsAsync(cmd);
    }

    public async Task<long> InsertAsync(NewFollowup followup, long? userId, string? userName, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(followup);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        var id = await InsertCoreAsync(conn, tx, followup, userId, userName, now, skipIfBillLineExists: false);
        tx.Commit();
        return id;
    }

    // ───────────────────────── Status changes ─────────────────────────

    public async Task<int> CloseAsync(IReadOnlyCollection<long> ids, FollowupStatus status, string? reason, string? note, long? userId,
        string? userName, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (status is not (FollowupStatus.Done or FollowupStatus.Dismissed))
            throw new ArgumentOutOfRangeException(nameof(status), "Only Done or Dismissed can be set by the user.");
        if (ids.Count == 0) return 0;

        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        var changed = 0;
        foreach (var id in ids.Distinct())
        {
            using (var cmd = conn.Command(tx, @"
                UPDATE PatientFollowups SET Status = @Status, StatusReason = @Reason, ActionedAt = @Now, ActionedByUserId = @UserId,
                    ActionedByName = @UserName, UpdatedAt = @Now
                WHERE Id = @Id AND Status = @Pending"))
            {
                cmd.Add("@Status", (int)status);
                cmd.Add("@Reason", DoctorsFollowupsSql.Blank(reason));
                cmd.AddDateTime("@Now", now);
                cmd.Add("@UserId", userId);
                cmd.Add("@UserName", userName);
                cmd.Add("@Id", id);
                cmd.Add("@Pending", (int)FollowupStatus.Pending);
                if (await cmd.ExecuteNonQueryAsync() == 0) continue;
            }
            changed++;
            if (!string.IsNullOrWhiteSpace(note)) await InsertNoteAsync(conn, tx, id, note.Trim(), userId, userName, now);
        }
        tx.Commit();
        return changed;
    }

    public async Task<bool> ReopenAsync(long id, DateTime now)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            UPDATE PatientFollowups SET Status = @Pending, StatusReason = NULL, ActionedAt = NULL, ActionedByUserId = NULL,
                ActionedByName = NULL, UpdatedAt = @Now
            WHERE Id = @Id AND Status IN (@Done, @Dismissed)");
        cmd.Add("@Pending", (int)FollowupStatus.Pending);
        cmd.Add("@Done", (int)FollowupStatus.Done);
        cmd.Add("@Dismissed", (int)FollowupStatus.Dismissed);
        cmd.AddDateTime("@Now", now);
        cmd.Add("@Id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public Task<bool> SnoozeAsync(long id, DateTime? until, DateTime now) =>
        UpdatePendingAsync(id, now, "SnoozedUntil = @Value", cmd => cmd.AddDate("@Value", until));

    public async Task<bool> SetHelpfulAsync(long id, bool? isHelpful, DateTime now)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "UPDATE PatientFollowups SET IsHelpful = @Value, UpdatedAt = @Now WHERE Id = @Id");
        cmd.AddBool("@Value", isHelpful);
        cmd.AddDateTime("@Now", now);
        cmd.Add("@Id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public Task<bool> UpdateConsumptionAsync(long id, decimal unitsPerDay, DateTime nextRefillDate, DateTime now) =>
        UpdatePendingAsync(id, now, "UnitsPerDay = @PerDay, UnitsPerDayIsManual = 1, NextRefillDate = @Next, SnoozedUntil = NULL", cmd =>
        {
            cmd.AddDecimal("@PerDay", unitsPerDay);
            cmd.AddDate("@Next", nextRefillDate);
        });

    // ───────────────────────── Helpers ─────────────────────────

    /// <param name="setClause">Constant SQL fragment (no user text); values are bound by <paramref name="bind"/>.</param>
    private async Task<bool> UpdatePendingAsync(long id, DateTime now, string setClause, Action<SqliteCommand> bind)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, $"UPDATE PatientFollowups SET {setClause}, UpdatedAt = @Now WHERE Id = @Id AND Status = @Pending");
        bind(cmd);
        cmd.AddDateTime("@Now", now);
        cmd.Add("@Id", id);
        cmd.Add("@Pending", (int)FollowupStatus.Pending);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static async Task<long> InsertCoreAsync(SqliteConnection conn, SqliteTransaction tx, NewFollowup f, long? userId, string? userName,
        DateTime now, bool skipIfBillLineExists)
    {
        // A bill line gets one follow-up; a second generation run (another screen) simply skips it.
        var guard = skipIfBillLineExists
            ? "WHERE NOT EXISTS (SELECT 1 FROM PatientFollowups x WHERE x.SaleId = @SaleId AND x.ItemKey = @ItemKey)"
            : string.Empty;
        long id;
        using (var cmd = conn.Command(tx, $@"
            INSERT INTO PatientFollowups (CustomerId, PatientName, PatientKey, ItemKey, MedicineId, MedicineName, CompositionId, CompositionName,
                SaleId, InvoiceNumber, SaleDate, QuantityUnits, UnitsPerDay, UnitsPerDayIsManual, NextRefillDate, Status, Source,
                ClosedBySaleId, StatusReason, CreatedAt, CreatedByUserId, CreatedByName, UpdatedAt)
            SELECT @CustomerId, @PatientName, @PatientKey, @ItemKey, @MedicineId, @MedicineName, @CompositionId, @CompositionName,
                @SaleId, @Invoice, @SaleDate, @Units, @PerDay, @Manual, @Next, @Status, @Source, @ClosedBy, @Reason, @Now, @UserId, @UserName, @Now
            {guard};
            SELECT CASE WHEN changes() > 0 THEN last_insert_rowid() ELSE 0 END;"))
        {
            cmd.Add("@CustomerId", f.CustomerId);
            cmd.Add("@PatientName", DoctorsFollowupsSql.Blank(f.PatientName));
            cmd.Add("@PatientKey", f.PatientKey);
            cmd.Add("@ItemKey", f.ItemKey);
            cmd.Add("@MedicineId", f.MedicineId);
            cmd.Add("@MedicineName", f.MedicineName);
            cmd.Add("@CompositionId", f.CompositionId);
            cmd.Add("@CompositionName", DoctorsFollowupsSql.Blank(f.CompositionName));
            cmd.Add("@SaleId", f.SaleId);
            cmd.Add("@Invoice", DoctorsFollowupsSql.Blank(f.InvoiceNumber));
            cmd.AddDate("@SaleDate", f.SaleDate);
            cmd.Add("@Units", f.QuantityUnits);
            cmd.AddDecimal("@PerDay", f.UnitsPerDay);
            cmd.Add("@Manual", f.UnitsPerDayIsManual ? 1 : 0);
            cmd.AddDate("@Next", f.NextRefillDate);
            cmd.Add("@Status", (int)f.Status);
            cmd.Add("@Source", (int)f.Source);
            cmd.Add("@ClosedBy", f.ClosedBySaleId);
            cmd.Add("@Reason", DoctorsFollowupsSql.Blank(f.StatusReason));
            cmd.AddDateTime("@Now", now);
            cmd.Add("@UserId", userId);
            cmd.Add("@UserName", userName);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        if (id > 0 && !string.IsNullOrWhiteSpace(f.Note)) await InsertNoteAsync(conn, tx, id, f.Note.Trim(), userId, userName, now);
        return id;
    }

    private static async Task<IReadOnlyList<PatientFollowup>> ReadFollowupsAsync(SqliteCommand cmd)
    {
        var list = new List<PatientFollowup>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new PatientFollowup
            {
                Id = r.Long("Id"),
                CustomerId = r.Long("CustomerId"),
                CustomerName = r.Text("CustomerName"),
                CustomerPhone = r.Str("CustomerPhone"),
                PatientName = r.Str("PatientName"),
                PatientKey = r.Text("PatientKey"),
                Gender = r.Int("Gender"),
                ItemKey = r.Text("ItemKey"),
                MedicineId = r.LongOrNull("MedicineId"),
                MedicineName = r.Text("MedicineName"),
                CompositionId = r.LongOrNull("CompositionId"),
                CompositionName = r.Str("CompositionName"),
                SaleId = r.LongOrNull("SaleId"),
                InvoiceNumber = r.Str("InvoiceNumber"),
                SaleDate = r.Date("SaleDate"),
                QuantityUnits = r.Long("QuantityUnits"),
                UnitsPerDay = r.Dec("UnitsPerDay"),
                UnitsPerDayIsManual = r.Bool("UnitsPerDayIsManual"),
                NextRefillDate = r.Date("NextRefillDate"),
                SnoozedUntil = r.DateOrNull("SnoozedUntil"),
                Status = (FollowupStatus)r.Int("Status", 1),
                Source = (FollowupSource)r.Int("Source", 1),
                IsHelpful = r.BoolOrNull("IsHelpful"),
                ActionedAt = r.DateOrNull("ActionedAt"),
                ActionedByName = r.Str("ActionedByName"),
                StatusReason = r.Str("StatusReason"),
                CreatedAt = r.Date("CreatedAt"),
                CreatedByName = r.Str("CreatedByName"),
                NoteCount = r.Int("NoteCount")
            });
        }
        return list;
    }
}
