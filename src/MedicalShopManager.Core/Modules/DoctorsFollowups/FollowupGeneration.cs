namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>A bill line of a chronic item sold to an identified customer (input to the generation planner).</summary>
public sealed record FollowupSaleLine(
    long SaleId,
    string InvoiceNumber,
    DateTime SaleDate,
    long CustomerId,
    string CustomerName,
    string? PatientName,
    long MedicineId,
    string MedicineName,
    long? CompositionId,
    string? CompositionName,
    long Units,
    decimal? RuleUnitsPerDay);

/// <summary>The stored fields of a follow-up the planner needs.</summary>
public sealed record ExistingFollowup(
    long Id,
    long CustomerId,
    string PatientKey,
    string ItemKey,
    long? SaleId,
    DateTime SaleDate,
    long QuantityUnits,
    decimal UnitsPerDay,
    bool UnitsPerDayIsManual,
    FollowupStatus Status,
    FollowupSource Source);

/// <summary>A follow-up row to insert.</summary>
public sealed record NewFollowup
{
    public long CustomerId { get; init; }
    public string? PatientName { get; init; }
    public string PatientKey { get; init; } = string.Empty;
    public string ItemKey { get; init; } = string.Empty;
    public long? MedicineId { get; init; }
    public string MedicineName { get; init; } = string.Empty;
    public long? CompositionId { get; init; }
    public string? CompositionName { get; init; }
    public long? SaleId { get; init; }
    public string? InvoiceNumber { get; init; }
    public DateTime SaleDate { get; init; }
    public long QuantityUnits { get; init; }
    public decimal UnitsPerDay { get; init; }
    public bool UnitsPerDayIsManual { get; init; }
    public DateTime NextRefillDate { get; init; }
    public FollowupStatus Status { get; init; } = FollowupStatus.Pending;
    public FollowupSource Source { get; init; } = FollowupSource.Sale;
    public long? ClosedBySaleId { get; init; }
    public string? StatusReason { get; init; }
    public string? Note { get; init; }
}

/// <summary>New values for a pending sale follow-up whose bill was edited (quantity, date, customer) or whose rule changed.</summary>
public sealed record FollowupUpdate(
    long Id,
    long CustomerId,
    string? PatientName,
    string PatientKey,
    long? MedicineId,
    string MedicineName,
    long? CompositionId,
    string? CompositionName,
    string? InvoiceNumber,
    DateTime SaleDate,
    long QuantityUnits,
    decimal UnitsPerDay,
    DateTime NextRefillDate);

/// <summary>A pending follow-up that stops: refilled by a newer bill, or cancelled with its bill.</summary>
public sealed record FollowupClosure(long Id, FollowupStatus Status, long? BySaleId, string Reason);

public sealed record FollowupPlan(IReadOnlyList<NewFollowup> Inserts, IReadOnlyList<FollowupUpdate> Updates, IReadOnlyList<FollowupClosure> Closures)
{
    public static readonly FollowupPlan Empty = new(Array.Empty<NewFollowup>(), Array.Empty<FollowupUpdate>(), Array.Empty<FollowupClosure>());
    public bool IsEmpty => Inserts.Count == 0 && Updates.Count == 0 && Closures.Count == 0;
}

/// <summary>What a generation run changed.</summary>
public sealed record FollowupGenerationResult(int Created, int Updated, int Refilled, int Cancelled)
{
    public static readonly FollowupGenerationResult None = new(0, 0, 0, 0);
    public bool HasChanges => Created + Updated + Refilled + Cancelled > 0;

    public string Summary => HasChanges
        ? $"{Created} new, {Updated} updated, {Refilled} refilled, {Cancelled} cancelled follow-up(s) from bills."
        : "Follow-ups are up to date with the bills.";

    public static FollowupGenerationResult From(FollowupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new FollowupGenerationResult(
            plan.Inserts.Count(i => i.Status == FollowupStatus.Pending),
            plan.Updates.Count,
            plan.Closures.Count(c => c.Status == FollowupStatus.Refilled),
            plan.Closures.Count(c => c.Status == FollowupStatus.Cancelled));
    }
}

/// <summary>
/// Turns chronic bill lines into follow-up changes (pure; the repository applies the plan in one transaction):
/// <list type="number">
/// <item>One follow-up per bill and item key (lines of the same salt on a bill add up). A new bill line creates a
/// pending follow-up with consumption = the item's refill rule, else the default.</item>
/// <item>A pending sale follow-up whose bill changed (units, date, customer, patient) is updated; a consumption the
/// user typed is kept.</item>
/// <item>For each patient and item only the latest purchase stays pending: older pending ones become
/// <see cref="FollowupStatus.Refilled"/> (a bill on the same day beats a manual follow-up).</item>
/// <item>A pending sale follow-up of a bill inside the scanned window that no longer has the line (bill cancelled,
/// line removed, customer removed, item no longer chronic) is <see cref="FollowupStatus.Cancelled"/>.</item>
/// </list>
/// </summary>
public static class FollowupGenerationPlanner
{
    public const string CancelReason = "Bill cancelled or edited, or the item is no longer marked for refill reminders.";

    public static FollowupPlan Plan(IReadOnlyList<FollowupSaleLine> lines, IReadOnlyList<ExistingFollowup> existing,
        decimal defaultUnitsPerDay, DateTime scanFrom)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(existing);
        if (RefillCalculator.ValidateUnitsPerDay(defaultUnitsPerDay) is { } error) throw new ArgumentOutOfRangeException(nameof(defaultUnitsPerDay), error);

        var groups = GroupLines(lines);
        var bySale = existing.Where(e => e.SaleId is not null)
            .GroupBy(e => (e.SaleId!.Value, e.ItemKey))
            .ToDictionary(g => g.Key, g => g.First());

        var inserts = new List<NewFollowup>();
        var updates = new List<FollowupUpdate>();
        var closures = new List<FollowupClosure>();
        var timeline = new List<Entry>();

        foreach (var group in groups)
        {
            if (bySale.TryGetValue((group.SaleId, group.ItemKey), out var current))
            {
                var effective = current;
                if (current is { Status: FollowupStatus.Pending, Source: FollowupSource.Sale })
                {
                    var perDay = current.UnitsPerDayIsManual ? current.UnitsPerDay : group.RuleUnitsPerDay ?? defaultUnitsPerDay;
                    if (current.QuantityUnits != group.Units || current.UnitsPerDay != perDay || current.CustomerId != group.CustomerId
                        || current.PatientKey != group.PatientKey || current.SaleDate.Date != group.SaleDate.Date)
                    {
                        updates.Add(new FollowupUpdate(current.Id, group.CustomerId, group.PatientName, group.PatientKey, group.MedicineId,
                            group.MedicineName, group.CompositionId, group.CompositionName, group.InvoiceNumber, group.SaleDate.Date,
                            group.Units, perDay, RefillCalculator.NextRefillDate(group.SaleDate, group.Units, perDay)));
                        effective = current with { CustomerId = group.CustomerId, PatientKey = group.PatientKey, SaleDate = group.SaleDate.Date };
                    }
                }
                timeline.Add(Entry.ForExisting(effective));
                continue;
            }

            var unitsPerDay = group.RuleUnitsPerDay ?? defaultUnitsPerDay;
            inserts.Add(new NewFollowup
            {
                CustomerId = group.CustomerId,
                PatientName = group.PatientName,
                PatientKey = group.PatientKey,
                ItemKey = group.ItemKey,
                MedicineId = group.MedicineId,
                MedicineName = group.MedicineName,
                CompositionId = group.CompositionId,
                CompositionName = group.CompositionName,
                SaleId = group.SaleId,
                InvoiceNumber = group.InvoiceNumber,
                SaleDate = group.SaleDate.Date,
                QuantityUnits = group.Units,
                UnitsPerDay = unitsPerDay,
                NextRefillDate = RefillCalculator.NextRefillDate(group.SaleDate, group.Units, unitsPerDay),
                Source = FollowupSource.Sale
            });
            timeline.Add(Entry.ForInsert(inserts.Count - 1, group.CustomerId, group.PatientKey, group.ItemKey, group.SaleDate.Date, group.SaleId));
        }

        // Pending sale follow-ups of scanned bills that no longer carry the line.
        var lineKeys = groups.Select(g => (g.SaleId, g.ItemKey)).ToHashSet();
        var cancelled = new HashSet<long>();
        foreach (var e in existing)
        {
            if (e is not { Status: FollowupStatus.Pending, Source: FollowupSource.Sale, SaleId: { } saleId }) continue;
            if (e.SaleDate.Date < scanFrom.Date || lineKeys.Contains((saleId, e.ItemKey))) continue;
            closures.Add(new FollowupClosure(e.Id, FollowupStatus.Cancelled, null, CancelReason));
            cancelled.Add(e.Id);
        }

        // Everything else that is not cancelled takes part in "latest purchase wins".
        var seenExisting = timeline.Where(t => t.ExistingId is not null).Select(t => t.ExistingId!.Value).ToHashSet();
        foreach (var e in existing)
        {
            if (seenExisting.Contains(e.Id) || cancelled.Contains(e.Id) || e.Status == FollowupStatus.Cancelled) continue;
            timeline.Add(Entry.ForExisting(e));
        }

        foreach (var patientItem in timeline.GroupBy(t => (t.CustomerId, t.PatientKey, t.ItemKey)))
        {
            var ordered = patientItem
                .OrderByDescending(t => t.SaleDate)
                .ThenByDescending(t => t.SaleId is null ? 0 : 1)
                .ThenByDescending(t => t.SaleId ?? 0)
                .ThenByDescending(t => t.ExistingId ?? long.MaxValue)
                .ToList();
            var latest = ordered[0];
            foreach (var older in ordered.Skip(1))
            {
                var reason = latest.SaleId is null
                    ? $"Bought again on {latest.SaleDate:dd-MM-yyyy}."
                    : $"Bought again on {latest.SaleDate:dd-MM-yyyy} (bill {Invoice(groups, latest.SaleId.Value)}).";
                if (older.InsertIndex is { } index)
                {
                    inserts[index] = inserts[index] with { Status = FollowupStatus.Refilled, ClosedBySaleId = latest.SaleId, StatusReason = reason };
                }
                else if (older.IsPending && older.ExistingId is { } id)
                {
                    closures.Add(new FollowupClosure(id, FollowupStatus.Refilled, latest.SaleId, reason));
                }
            }
        }

        // An update of a follow-up that is closed in the same run is pointless.
        var closedIds = closures.Select(c => c.Id).ToHashSet();
        updates.RemoveAll(u => closedIds.Contains(u.Id));
        return new FollowupPlan(inserts, updates, closures);
    }

    private static string Invoice(IEnumerable<LineGroup> groups, long saleId) =>
        groups.FirstOrDefault(g => g.SaleId == saleId)?.InvoiceNumber ?? "#" + saleId;

    private static List<LineGroup> GroupLines(IEnumerable<FollowupSaleLine> lines) =>
        lines.Where(l => l.Units > 0 && l.CustomerId > 0)
            .GroupBy(l => (l.SaleId, ItemKey: FollowupKeys.ItemKey(l.CompositionId, l.MedicineId)))
            .Select(g =>
            {
                var first = g.OrderBy(l => l.MedicineId).First();
                var names = g.Select(l => l.MedicineName.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var rule = g.Select(l => l.RuleUnitsPerDay).FirstOrDefault(r => r is > 0);
                return new LineGroup(g.Key.SaleId, g.Key.ItemKey, first.InvoiceNumber, first.SaleDate, first.CustomerId,
                    string.IsNullOrWhiteSpace(first.PatientName) ? null : first.PatientName.Trim(),
                    FollowupKeys.PatientKey(first.PatientName, first.CustomerName), first.MedicineId,
                    names.Count == 0 ? first.MedicineName : string.Join(" / ", names), first.CompositionId, first.CompositionName,
                    g.Sum(l => l.Units), rule is { } r && RefillCalculator.ValidateUnitsPerDay(r) is null ? r : null);
            })
            .OrderBy(g => g.SaleDate).ThenBy(g => g.SaleId).ThenBy(g => g.ItemKey, StringComparer.Ordinal)
            .ToList();

    private sealed record LineGroup(long SaleId, string ItemKey, string InvoiceNumber, DateTime SaleDate, long CustomerId, string? PatientName,
        string PatientKey, long MedicineId, string MedicineName, long? CompositionId, string? CompositionName, long Units, decimal? RuleUnitsPerDay);

    private sealed record Entry(long CustomerId, string PatientKey, string ItemKey, DateTime SaleDate, long? SaleId, long? ExistingId,
        int? InsertIndex, bool IsPending)
    {
        public static Entry ForExisting(ExistingFollowup e) =>
            new(e.CustomerId, e.PatientKey, e.ItemKey, e.SaleDate.Date, e.SaleId, e.Id, null, e.Status == FollowupStatus.Pending);

        public static Entry ForInsert(int index, long customerId, string patientKey, string itemKey, DateTime saleDate, long saleId) =>
            new(customerId, patientKey, itemKey, saleDate, saleId, null, index, true);
    }
}
