using System.Globalization;

namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>
/// Module settings (AppSettings keys <c>DoctorsFollowups.*</c>, seeded by migration 17). Whether follow-ups are
/// created at all is Control Center's sale setting "Patient reminders" (<c>SaleSettings.PatientReminders</c>).
/// </summary>
public sealed record FollowupSettings
{
    public const string DefaultUnitsPerDayKey = "DoctorsFollowups.DefaultUnitsPerDay";
    public const string UpcomingDaysKey = "DoctorsFollowups.UpcomingDays";
    public const string GraceDaysKey = "DoctorsFollowups.GraceDays";
    public const string LookbackDaysKey = "DoctorsFollowups.LookbackDays";

    public static readonly FollowupSettings Default = new();

    /// <summary>Consumption used when neither the follow-up nor the item's refill rule sets one.</summary>
    public decimal DefaultUnitsPerDay { get; init; } = RefillCalculator.DefaultUnitsPerDay;

    /// <summary>A reminder is "upcoming" (Arriving) this many days before it is due.</summary>
    public int UpcomingDays { get; init; } = 7;

    /// <summary>An overdue reminder lapses (Expired) this many days after it was due.</summary>
    public int GraceDays { get; init; } = 7;

    /// <summary>Bills of the last N days are scanned for chronic medicines (older bills are not re-read).</summary>
    public int LookbackDays { get; init; } = 120;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (RefillCalculator.ValidateUnitsPerDay(DefaultUnitsPerDay) is { } consumption) errors.Add("Default " + consumption[..1].ToLowerInvariant() + consumption[1..]);
        if (UpcomingDays is < 0 or > 60) errors.Add("Upcoming window must be between 0 and 60 days.");
        if (GraceDays is < 0 or > 90) errors.Add("Grace period must be between 0 and 90 days.");
        if (LookbackDays is < 7 or > 730) errors.Add("Bills to scan must be between 7 and 730 days.");
        return errors;
    }

    /// <summary>Reads the settings from stored text values; missing or invalid values fall back to the defaults.</summary>
    public static FollowupSettings FromValues(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var d = Default;
        var parsed = new FollowupSettings
        {
            DefaultUnitsPerDay = decimal.TryParse(read(DefaultUnitsPerDayKey), NumberStyles.Number, CultureInfo.InvariantCulture, out var perDay)
                ? perDay : d.DefaultUnitsPerDay,
            UpcomingDays = int.TryParse(read(UpcomingDaysKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var upcoming)
                ? upcoming : d.UpcomingDays,
            GraceDays = int.TryParse(read(GraceDaysKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var grace)
                ? grace : d.GraceDays,
            LookbackDays = int.TryParse(read(LookbackDaysKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lookback)
                ? lookback : d.LookbackDays
        };
        // One bad value must not break the page: fall back field by field.
        return parsed with
        {
            DefaultUnitsPerDay = RefillCalculator.ValidateUnitsPerDay(parsed.DefaultUnitsPerDay) is null ? parsed.DefaultUnitsPerDay : d.DefaultUnitsPerDay,
            UpcomingDays = parsed.UpcomingDays is >= 0 and <= 60 ? parsed.UpcomingDays : d.UpcomingDays,
            GraceDays = parsed.GraceDays is >= 0 and <= 90 ? parsed.GraceDays : d.GraceDays,
            LookbackDays = parsed.LookbackDays is >= 7 and <= 730 ? parsed.LookbackDays : d.LookbackDays
        };
    }

    /// <summary>The settings as stored text values, keyed by AppSettings key.</summary>
    public IReadOnlyDictionary<string, string> ToValues() => new Dictionary<string, string>
    {
        [DefaultUnitsPerDayKey] = DefaultUnitsPerDay.ToString("0.###", CultureInfo.InvariantCulture),
        [UpcomingDaysKey] = UpcomingDays.ToString(CultureInfo.InvariantCulture),
        [GraceDaysKey] = GraceDays.ToString(CultureInfo.InvariantCulture),
        [LookbackDaysKey] = LookbackDays.ToString(CultureInfo.InvariantCulture)
    };
}
