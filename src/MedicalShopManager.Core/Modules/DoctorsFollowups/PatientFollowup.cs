using System.Globalization;

namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>
/// One refill follow-up: a patient (customer + patient name) who bought a chronic medicine and should come back for
/// it around <see cref="NextRefillDate"/> (Localwell PatientFollowup). Rows are stored in PatientFollowups; the
/// time-dependent members (<see cref="Bucket"/>, <see cref="DaysToDue"/>, <see cref="RemainingUnits"/>) are filled
/// by <see cref="RefillCalculator.Evaluate"/> for a given day and are never stored.
/// </summary>
public sealed record PatientFollowup
{
    public long Id { get; init; }
    public long CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string? CustomerPhone { get; init; }

    /// <summary>Patient as written on the bill; null / "Self" = the customer.</summary>
    public string? PatientName { get; init; }

    /// <summary>Normalised patient (see <see cref="FollowupKeys.PatientKey"/>); "" = the customer.</summary>
    public string PatientKey { get; init; } = string.Empty;

    /// <summary>Patient gender from the customer master (0 = not given, 1 male, 2 female, 3 other).</summary>
    public int Gender { get; init; }

    /// <summary>"C&lt;compositionId&gt;" or "M&lt;medicineId&gt;" (see <see cref="FollowupKeys.ItemKey"/>).</summary>
    public string ItemKey { get; init; } = string.Empty;

    public long? MedicineId { get; init; }
    public string MedicineName { get; init; } = string.Empty;
    public long? CompositionId { get; init; }
    public string? CompositionName { get; init; }

    /// <summary>Bill that started the follow-up (null for manual ones).</summary>
    public long? SaleId { get; init; }
    public string? InvoiceNumber { get; init; }

    /// <summary>Date the patient last bought the item (bill date, or the start date of a manual follow-up).</summary>
    public DateTime SaleDate { get; init; }

    /// <summary>Units bought (paid + free) on that date.</summary>
    public long QuantityUnits { get; init; }

    /// <summary>Units consumed per day (Localwell "consumption", default 0.5).</summary>
    public decimal UnitsPerDay { get; init; }

    /// <summary>True when the user set <see cref="UnitsPerDay"/> on this follow-up (regeneration keeps it).</summary>
    public bool UnitsPerDayIsManual { get; init; }

    public DateTime NextRefillDate { get; init; }
    public DateTime? SnoozedUntil { get; init; }
    public FollowupStatus Status { get; init; } = FollowupStatus.Pending;
    public FollowupSource Source { get; init; } = FollowupSource.Sale;

    /// <summary>Mark As Helpful / Not Helpful; null = not answered.</summary>
    public bool? IsHelpful { get; init; }

    public DateTime? ActionedAt { get; init; }
    public string? ActionedByName { get; init; }
    public string? StatusReason { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedByName { get; init; }
    public int NoteCount { get; init; }

    // ─── Evaluated for a day (RefillCalculator.Evaluate); not stored ───
    public FollowupBucket Bucket { get; init; } = FollowupBucket.Later;
    public int DaysToDue { get; init; }
    public decimal RemainingUnits { get; init; }

    /// <summary>The date the reminder is due: the snooze date when snoozed, else the next refill date.</summary>
    public DateTime DueDate => SnoozedUntil is { } snoozed && snoozed.Date > NextRefillDate.Date ? snoozed.Date : NextRefillDate.Date;

    public bool IsPending => Status == FollowupStatus.Pending;
    public bool IsSnoozed => SnoozedUntil is { } s && s.Date > NextRefillDate.Date;
    public string PatientDisplay => string.IsNullOrWhiteSpace(PatientName) || PatientKey.Length == 0 ? CustomerName : PatientName!.Trim();
    public string Initials => FollowupKeys.Initials(PatientDisplay);
    public string PhoneDisplay => string.IsNullOrWhiteSpace(CustomerPhone) ? "-" : CustomerPhone!;

    public string GenderLabel => Gender switch { 1 => "Male", 2 => "Female", 3 => "Other", _ => string.Empty };

    public string ItemDisplay => string.IsNullOrWhiteSpace(CompositionName) ? MedicineName : $"{MedicineName} ({CompositionName})";
    public string QuantityDisplay => QuantityUnits == 1 ? "1 unit" : $"{QuantityUnits} units";
    public string ConsumptionDisplay => $"{UnitsPerDay.ToString("0.##", CultureInfo.InvariantCulture)} unit(s)/day";
    public string RemainingDisplay => $"{RemainingUnits.ToString("0.#", CultureInfo.InvariantCulture)} units left";
    public string SaleReference => InvoiceNumber ?? (Source == FollowupSource.Sale ? "-" : SourceLabel);

    public string SourceLabel => Source switch
    {
        FollowupSource.Manual => "Manual",
        FollowupSource.Recommended => "Recommended",
        _ => "Sale"
    };

    public string StatusLabel => Status switch
    {
        FollowupStatus.Done => "Followed up",
        FollowupStatus.Refilled => "Refilled",
        FollowupStatus.Dismissed => "Dismissed",
        FollowupStatus.Cancelled => "Bill cancelled",
        _ => Bucket switch
        {
            FollowupBucket.Lapsed => "Lapsed",
            FollowupBucket.Overdue => "Overdue",
            FollowupBucket.DueToday => "Due today",
            FollowupBucket.Upcoming => "Upcoming",
            _ => "Pending"
        }
    };

    /// <summary>"Due today", "Overdue by 3 days", "Due in 5 days", "Lapsed 20 days ago", or the status.</summary>
    public string DueLabel => !IsPending
        ? StatusLabel
        : DaysToDue switch
        {
            0 => "Due today",
            < 0 when Bucket == FollowupBucket.Lapsed => $"Lapsed {Days(-DaysToDue)} ago",
            < 0 => $"Overdue by {Days(-DaysToDue)}",
            _ => $"Due in {Days(DaysToDue)}"
        };

    public string HelpfulLabel => IsHelpful switch { true => "Helpful", false => "Not helpful", _ => string.Empty };

    private static string Days(int days) => days == 1 ? "1 day" : $"{days} days";
}

/// <summary>A remark on a follow-up (Localwell FollowupNote). Soft-deleted with IsActive = 0.</summary>
public sealed record FollowupNote
{
    public long Id { get; init; }
    public long FollowupId { get; init; }
    public string Note { get; init; } = string.Empty;
    public long? UserId { get; init; }
    public string? UserName { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }

    public string AuthorDisplay => string.IsNullOrWhiteSpace(UserName) ? "Unknown user" : UserName!;
    public string Initials => FollowupKeys.Initials(AuthorDisplay);
    public bool IsEdited => UpdatedAt is not null;
}

/// <summary>Counts for the tab badges and the dashboard card.</summary>
public sealed record FollowupCounts(int Arriving, int Expired, int Actioned, int Overdue, int DueToday, int Upcoming, int Later)
{
    public static readonly FollowupCounts Empty = new(0, 0, 0, 0, 0, 0, 0);
    public int AllPending => Arriving + Expired + Later;
}

/// <summary>Filters for the follow-up list.</summary>
public sealed record FollowupQuery
{
    public FollowupTab Tab { get; init; } = FollowupTab.Arriving;

    /// <summary>Patient / customer name or mobile, medicine or composition (LIKE).</summary>
    public string? SearchText { get; init; }

    public long? CustomerId { get; init; }

    /// <summary>Gender filter (1 male, 2 female, 3 other); null = all.</summary>
    public int? Gender { get; init; }

    public FollowupSort Sort { get; init; } = FollowupSort.FollowupDateAscending;
    public int Limit { get; init; } = 500;
}

/// <summary>The list for a tab plus the counts of every tab.</summary>
public sealed record FollowupListResult(IReadOnlyList<PatientFollowup> Items, FollowupCounts Counts);

/// <summary>Customer card on the follow-ups page (Localwell customer_metrics).</summary>
public sealed record CustomerFollowupSummary(
    long CustomerId,
    string CustomerName,
    string? Phone,
    string? Address,
    int SalesCount,
    long TotalSalesPaisa,
    long AverageOrderValuePaisa,
    DateTime? FirstSaleDate,
    DateTime? LastSaleDate,
    int? AverageRevisitDays)
{
    public string RevisitLabel => AverageRevisitDays is { } d ? $"{d} days" : "-";
}

/// <summary>A follow-up added by hand (or accepted from a recommendation).</summary>
public sealed record ManualFollowupRequest
{
    public long CustomerId { get; init; }
    public string? PatientName { get; init; }
    public long MedicineId { get; init; }

    /// <summary>Date the patient last bought / started the medicine.</summary>
    public DateTime StartDate { get; init; } = DateTime.Today;

    public long QuantityUnits { get; init; }
    public decimal UnitsPerDay { get; init; }
    public string? Note { get; init; }
    public FollowupSource Source { get; init; } = FollowupSource.Manual;
}

/// <summary>Customer option for the manual follow-up form.</summary>
public sealed record FollowupCustomerOption(long Id, string Name, string? Phone)
{
    public string Label => string.IsNullOrWhiteSpace(Phone) ? Name : $"{Name} · {Phone}";
}

/// <summary>Item option for the manual follow-up form and the refill rules page.</summary>
public sealed record FollowupItemOption(long MedicineId, string Name, long? CompositionId, string? CompositionName, bool IsChronic,
    decimal? UnitsPerDay, int UnitsPerPack)
{
    public string Label => string.IsNullOrWhiteSpace(CompositionName) ? Name : $"{Name} ({CompositionName})";
}

/// <summary>Printable call list of reminders (the follow-ups page's current tab).</summary>
public sealed record RefillReminderList(string Title, DateTime GeneratedAt, IReadOnlyList<PatientFollowup> Items);
