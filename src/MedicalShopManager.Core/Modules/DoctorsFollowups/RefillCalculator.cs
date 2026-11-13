using System.Globalization;

namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>
/// Refill maths (MASTER-SPEC §4.9, server-side in Localwell; formulas from 01-shell-dashboard §5):
/// <code>
/// days supply      = floor(units bought / units per day)               (1 … 365)
/// next refill date = sale date + days supply
/// units left       = max(0, units bought − units per day × days since the sale)
/// </code>
/// Buckets relative to today (due = snooze date when snoozed, else next refill date):
/// overdue up to <see cref="FollowupSettings.GraceDays"/> days, then lapsed (Localwell "Expired"); due today;
/// upcoming within <see cref="FollowupSettings.UpcomingDays"/>; later after that.
/// </summary>
public static class RefillCalculator
{
    /// <summary>Localwell's consumption when nothing better is known (units per day).</summary>
    public const decimal DefaultUnitsPerDay = 0.5m;

    public const decimal MinUnitsPerDay = 0.05m;
    public const decimal MaxUnitsPerDay = 50m;

    /// <summary>A refill is never expected more than a year out.</summary>
    public const int MaxDaysSupply = 365;

    /// <summary>A reminder can be snoozed at most this many days ahead.</summary>
    public const int MaxSnoozeDays = 90;

    /// <summary>Days the bought quantity lasts at the given consumption (1 … <see cref="MaxDaysSupply"/>).</summary>
    public static int DaysSupply(long units, decimal unitsPerDay)
    {
        if (unitsPerDay <= 0) throw new ArgumentOutOfRangeException(nameof(unitsPerDay), "Consumption must be positive.");
        if (units <= 0) return 0;
        var days = Math.Floor(units / unitsPerDay);
        return (int)Math.Clamp(days, 1m, MaxDaysSupply);
    }

    public static DateTime NextRefillDate(DateTime saleDate, long units, decimal unitsPerDay) =>
        saleDate.Date.AddDays(DaysSupply(units, unitsPerDay));

    /// <summary>Units still on hand at <paramref name="today"/> (one decimal, never negative).</summary>
    public static decimal RemainingUnits(long units, decimal unitsPerDay, DateTime saleDate, DateTime today)
    {
        if (units <= 0) return 0m;
        var days = Math.Max(0, (today.Date - saleDate.Date).Days);
        var left = units - unitsPerDay * days;
        return left <= 0 ? 0m : Math.Round(left, 1, MidpointRounding.ToZero);
    }

    /// <summary>Bucket of a follow-up with the given status and due date on <paramref name="today"/>.</summary>
    public static FollowupBucket Classify(FollowupStatus status, DateTime dueDate, DateTime today, FollowupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (status != FollowupStatus.Pending) return FollowupBucket.Closed;
        var days = (dueDate.Date - today.Date).Days;
        if (days < -settings.GraceDays) return FollowupBucket.Lapsed;
        if (days < 0) return FollowupBucket.Overdue;
        if (days == 0) return FollowupBucket.DueToday;
        return days <= settings.UpcomingDays ? FollowupBucket.Upcoming : FollowupBucket.Later;
    }

    /// <summary>Whether a bucket is listed on a tab.</summary>
    public static bool IsInTab(FollowupBucket bucket, FollowupStatus status, FollowupTab tab) => tab switch
    {
        FollowupTab.Arriving => bucket is FollowupBucket.Overdue or FollowupBucket.DueToday or FollowupBucket.Upcoming,
        FollowupTab.Expired => bucket == FollowupBucket.Lapsed,
        FollowupTab.Actioned => status == FollowupStatus.Done,
        FollowupTab.AllPending => status == FollowupStatus.Pending,
        _ => false
    };

    /// <summary>The follow-up with its bucket, days to due and units left filled in for <paramref name="today"/>.</summary>
    public static PatientFollowup Evaluate(PatientFollowup followup, DateTime today, FollowupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(followup);
        return followup with
        {
            Bucket = Classify(followup.Status, followup.DueDate, today, settings),
            DaysToDue = (followup.DueDate - today.Date).Days,
            RemainingUnits = RemainingUnits(followup.QuantityUnits, followup.UnitsPerDay, followup.SaleDate, today)
        };
    }

    /// <summary>Tab badge and dashboard counts over evaluated follow-ups.</summary>
    public static FollowupCounts Count(IEnumerable<PatientFollowup> evaluated)
    {
        ArgumentNullException.ThrowIfNull(evaluated);
        int overdue = 0, today = 0, upcoming = 0, later = 0, lapsed = 0, actioned = 0;
        foreach (var f in evaluated)
        {
            if (f.Status == FollowupStatus.Done) { actioned++; continue; }
            switch (f.Bucket)
            {
                case FollowupBucket.Overdue: overdue++; break;
                case FollowupBucket.DueToday: today++; break;
                case FollowupBucket.Upcoming: upcoming++; break;
                case FollowupBucket.Later: later++; break;
                case FollowupBucket.Lapsed: lapsed++; break;
            }
        }
        return new FollowupCounts(overdue + today + upcoming, lapsed, actioned, overdue, today, upcoming, later);
    }

    /// <summary>Sorts a list the way the page asks (ties: due date, then patient, then id).</summary>
    public static IReadOnlyList<PatientFollowup> Sort(IEnumerable<PatientFollowup> items, FollowupSort sort)
    {
        ArgumentNullException.ThrowIfNull(items);
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var ordered = sort switch
        {
            FollowupSort.FollowupDateDescending => items.OrderByDescending(f => f.DueDate).ThenBy(f => f.PatientDisplay, comparer),
            FollowupSort.LastSaleDescending => items.OrderByDescending(f => f.SaleDate).ThenBy(f => f.DueDate),
            FollowupSort.PatientNameAscending => items.OrderBy(f => f.PatientDisplay, comparer).ThenBy(f => f.DueDate),
            FollowupSort.PatientNameDescending => items.OrderByDescending(f => f.PatientDisplay, comparer).ThenBy(f => f.DueDate),
            _ => items.OrderBy(f => f.DueDate).ThenBy(f => f.PatientDisplay, comparer)
        };
        return ordered.ThenBy(f => f.Id).ToList();
    }

    /// <summary>Problems with a consumption value (units per day).</summary>
    public static string? ValidateUnitsPerDay(decimal unitsPerDay) =>
        unitsPerDay < MinUnitsPerDay || unitsPerDay > MaxUnitsPerDay
            ? $"Consumption must be between {MinUnitsPerDay.ToString("0.##", CultureInfo.InvariantCulture)} and {MaxUnitsPerDay:0} units a day."
            : null;

    /// <summary>Problems with a snooze date (must be after today and at most <see cref="MaxSnoozeDays"/> ahead).</summary>
    public static string? ValidateSnooze(DateTime until, DateTime today)
    {
        if (until.Date <= today.Date) return "Choose a date after today to snooze the reminder.";
        if (until.Date > today.Date.AddDays(MaxSnoozeDays)) return $"A reminder can be snoozed at most {MaxSnoozeDays} days ahead.";
        return null;
    }

    /// <summary>Problems with a manual follow-up request.</summary>
    public static IReadOnlyList<string> ValidateManual(ManualFollowupRequest request, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        if (request.CustomerId <= 0) errors.Add("Choose the customer to follow up.");
        if (request.MedicineId <= 0) errors.Add("Choose the medicine.");
        if (request.QuantityUnits <= 0) errors.Add("Enter the units bought (more than 0).");
        else if (request.QuantityUnits > 100_000) errors.Add("The units bought look too large.");
        if (ValidateUnitsPerDay(request.UnitsPerDay) is { } consumption) errors.Add(consumption);
        if (request.StartDate.Date > today.Date) errors.Add("The purchase date cannot be in the future.");
        else if (request.StartDate.Date < today.Date.AddYears(-1)) errors.Add("The purchase date is more than a year ago.");
        if (request.Note is { Length: > FollowupKeys.MaxNoteLength }) errors.Add($"A note can have at most {FollowupKeys.MaxNoteLength} characters.");
        return errors;
    }
}
