using MedicalShopManager.Core.Numbering;

namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>
/// Filter values for one run. Dates are inclusive calendar dates (time ignored). Choices hold the selected option key
/// per <see cref="ReportChoice.Key"/>; a missing key means the choice's default.
/// </summary>
public sealed record ReportParameters
{
    public DateTime From { get; init; } = DateTime.Today;
    public DateTime To { get; init; } = DateTime.Today;
    public DateTime AsOf { get; init; } = DateTime.Today;
    public int? Number { get; init; }
    public IReadOnlyDictionary<string, string> Choices { get; init; } = new Dictionary<string, string>();

    /// <summary>Exclusive upper bound for SQL (<c>date &lt; ToExclusive</c>), so timestamps on the last day are included.</summary>
    public DateTime ToExclusive => To.Date.AddDays(1);

    public string Choice(ReportDefinition definition, string key)
    {
        var choice = definition.Parameters.Choices.FirstOrDefault(c => c.Key == key)
                     ?? throw new ArgumentException($"Report '{definition.Id}' has no choice '{key}'.", nameof(key));
        return Choices.TryGetValue(key, out var value) && choice.HasOption(value) ? value : choice.DefaultKey;
    }

    public int NumberOr(ReportDefinition definition) =>
        Number ?? definition.Parameters.Number?.Default ?? 0;

    /// <summary>Default filters for a report as of <paramref name="today"/>.</summary>
    public static ReportParameters DefaultsFor(ReportDefinition definition, DateTime today)
    {
        var (from, to) = ReportDateRanges.For(definition.Parameters.DefaultRange, today);
        return new ReportParameters
        {
            From = from,
            To = to,
            AsOf = today.Date,
            Number = definition.Parameters.Number?.Default,
            Choices = definition.Parameters.Choices.ToDictionary(c => c.Key, c => c.DefaultKey)
        };
    }

    /// <summary>User-facing problems with these filters for <paramref name="definition"/> (empty = valid).</summary>
    public IReadOnlyList<string> Validate(ReportDefinition definition)
    {
        var errors = new List<string>();
        var spec = definition.Parameters;
        if (spec.UsesDateRange)
        {
            if (From.Date > To.Date)
                errors.Add("The From date is after the To date.");
            else if ((To.Date - From.Date).TotalDays + 1 > spec.MaxRangeDays)
                errors.Add($"Choose a period of at most {spec.MaxRangeDays} days for this report.");
            if (From.Year < 2000 || To.Year > 2100)
                errors.Add("Enter dates between 2000 and 2100.");
        }
        if (spec.UsesAsOfDate && (AsOf.Year < 2000 || AsOf.Year > 2100))
            errors.Add("Enter an 'as on' date between 2000 and 2100.");
        if (spec.Number is { } number)
        {
            var value = Number ?? number.Default;
            if (value < number.Min || value > number.Max)
                errors.Add($"{number.Label} must be between {number.Min} and {number.Max}.");
        }
        foreach (var choice in spec.Choices)
        {
            if (Choices.TryGetValue(choice.Key, out var key) && !choice.HasOption(key))
                errors.Add($"Pick a valid option for {choice.Label}.");
        }
        return errors;
    }

    /// <summary>"01-Apr-2026 to 30-Apr-2026" / "As on 05-Nov-2026" for headers and exports.</summary>
    public string PeriodLabel(ReportDefinition definition)
    {
        if (definition.Parameters.UsesDateRange)
            return From.Date == To.Date ? ReportFormat.Date(From) : $"{ReportFormat.Date(From)} to {ReportFormat.Date(To)}";
        return $"As on {ReportFormat.Date(AsOf)}";
    }
}

/// <summary>Date presets (financial year April–March).</summary>
public static class ReportDateRanges
{
    public static (DateTime From, DateTime To) For(ReportDatePreset preset, DateTime today)
    {
        var d = today.Date;
        var monthStart = new DateTime(d.Year, d.Month, 1);
        var fy = FinancialYear.For(d);
        return preset switch
        {
            ReportDatePreset.Today => (d, d),
            ReportDatePreset.Yesterday => (d.AddDays(-1), d.AddDays(-1)),
            ReportDatePreset.ThisMonth => (monthStart, d),
            ReportDatePreset.LastMonth => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            ReportDatePreset.Last30Days => (d.AddDays(-29), d),
            ReportDatePreset.Last90Days => (d.AddDays(-89), d),
            ReportDatePreset.ThisFinancialYear => (fy.Start, d),
            ReportDatePreset.LastFinancialYear => (new FinancialYear(fy.StartYear - 1).Start, new FinancialYear(fy.StartYear - 1).End),
            _ => (d, d)
        };
    }
}
