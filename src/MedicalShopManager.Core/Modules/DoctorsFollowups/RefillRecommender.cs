namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>A bill line of an identified customer (input to the recommender).</summary>
public sealed record FollowupPurchaseRow(
    long CustomerId,
    string CustomerName,
    string? Phone,
    string? PatientName,
    long SaleId,
    DateTime SaleDate,
    long MedicineId,
    string MedicineName,
    long? CompositionId,
    string? CompositionName,
    long Units);

/// <summary>
/// A suggested follow-up: the customer keeps buying an item that is not marked for refill reminders
/// (Localwell "followups_to_suggest").
/// </summary>
public sealed record RefillRecommendation(
    long CustomerId,
    string CustomerName,
    string? Phone,
    string? PatientName,
    string PatientKey,
    string ItemKey,
    long MedicineId,
    string MedicineName,
    long? CompositionId,
    string? CompositionName,
    int PurchaseCount,
    DateTime FirstPurchase,
    DateTime LastPurchase,
    long LastUnits,
    int AverageIntervalDays,
    decimal SuggestedUnitsPerDay,
    DateTime SuggestedDueDate)
{
    public string PatientDisplay => string.IsNullOrWhiteSpace(PatientName) || PatientKey.Length == 0 ? CustomerName : PatientName!;
    public string ItemDisplay => string.IsNullOrWhiteSpace(CompositionName) ? MedicineName : $"{MedicineName} ({CompositionName})";
    public string PatternLabel => $"{PurchaseCount} purchases, about every {AverageIntervalDays} days";
}

/// <summary>
/// Recommended reminders from purchase history (pure). A customer (and patient) who bought the same item (same
/// composition, else same medicine) on at least <see cref="MinPurchases"/> different days, at a steady interval of
/// <see cref="MinIntervalDays"/>–<see cref="MaxIntervalDays"/> days, without a pending follow-up for it, is suggested.
/// Consumption = units bought before the last purchase ÷ days between the first and last purchase; due = last
/// purchase + average interval. Suggestions that lapsed more than <see cref="StaleAfterDays"/> days ago are dropped.
/// </summary>
public static class RefillRecommender
{
    public const int MinPurchases = 2;
    public const int MinIntervalDays = 7;
    public const int MaxIntervalDays = 120;
    public const int StaleAfterDays = 60;

    public static IReadOnlyList<RefillRecommendation> Recommend(IEnumerable<FollowupPurchaseRow> rows,
        IReadOnlySet<(long CustomerId, string PatientKey, string ItemKey)> pendingKeys, DateTime today, int limit = 200)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(pendingKeys);
        var result = new List<RefillRecommendation>();

        var groups = rows.Where(r => r.Units > 0 && r.CustomerId > 0)
            .GroupBy(r => (r.CustomerId, PatientKey: FollowupKeys.PatientKey(r.PatientName, r.CustomerName),
                ItemKey: FollowupKeys.ItemKey(r.CompositionId, r.MedicineId)));
        foreach (var group in groups)
        {
            if (pendingKeys.Contains(group.Key)) continue;
            var days = group.GroupBy(r => r.SaleDate.Date)
                .Select(d => (Date: d.Key, Units: d.Sum(r => r.Units)))
                .OrderBy(d => d.Date)
                .ToList();
            if (days.Count < MinPurchases) continue;

            var first = days[0];
            var last = days[^1];
            var span = (last.Date - first.Date).Days;
            var interval = (int)Math.Round((decimal)span / (days.Count - 1), MidpointRounding.AwayFromZero);
            if (interval < MinIntervalDays || interval > MaxIntervalDays) continue;

            var due = last.Date.AddDays(interval);
            if (due < today.Date.AddDays(-StaleAfterDays)) continue;

            var consumed = days.Take(days.Count - 1).Sum(d => d.Units);
            var perDay = Math.Round((decimal)consumed / span, 2, MidpointRounding.AwayFromZero);
            perDay = Math.Clamp(perDay, RefillCalculator.MinUnitsPerDay, RefillCalculator.MaxUnitsPerDay);

            var latest = group.OrderByDescending(r => r.SaleDate).ThenByDescending(r => r.SaleId).First();
            result.Add(new RefillRecommendation(latest.CustomerId, latest.CustomerName, latest.Phone,
                string.IsNullOrWhiteSpace(latest.PatientName) ? null : latest.PatientName.Trim(), group.Key.PatientKey, group.Key.ItemKey,
                latest.MedicineId, latest.MedicineName, latest.CompositionId, latest.CompositionName, days.Count, first.Date, last.Date,
                last.Units, interval, perDay, due));
        }

        return result
            .OrderBy(r => r.SuggestedDueDate)
            .ThenBy(r => r.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.MedicineName, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    /// <summary>The manual follow-up an accepted recommendation becomes.</summary>
    public static ManualFollowupRequest ToRequest(RefillRecommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        return new ManualFollowupRequest
        {
            CustomerId = recommendation.CustomerId,
            PatientName = recommendation.PatientName,
            MedicineId = recommendation.MedicineId,
            StartDate = recommendation.LastPurchase,
            QuantityUnits = recommendation.LastUnits,
            UnitsPerDay = recommendation.SuggestedUnitsPerDay,
            Source = FollowupSource.Recommended,
            Note = $"Recommended: {recommendation.PatternLabel}."
        };
    }
}
