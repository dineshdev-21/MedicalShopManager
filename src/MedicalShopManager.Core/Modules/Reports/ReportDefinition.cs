namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>One option of a report choice (e.g. "Batch-wise" / "Item-wise").</summary>
public sealed record ReportChoiceOption(string Key, string Label);

/// <summary>A drop-down filter of a report. <see cref="DefaultKey"/> must be one of the option keys.</summary>
public sealed record ReportChoice(string Key, string Label, IReadOnlyList<ReportChoiceOption> Options, string DefaultKey)
{
    public bool HasOption(string? key) => key != null && Options.Any(o => o.Key == key);
}

/// <summary>A whole-number filter (e.g. "Expiring within N days").</summary>
public sealed record ReportNumberInput(string Label, int Default, int Min, int Max);

/// <summary>Which filters a report shows; the viewer builds its filter bar from this.</summary>
public sealed record ReportParameterSpec
{
    /// <summary>From / To dates (inclusive).</summary>
    public bool UsesDateRange { get; init; }
    /// <summary>Preset applied when the report opens.</summary>
    public ReportDatePreset DefaultRange { get; init; } = ReportDatePreset.ThisMonth;
    /// <summary>Longest range allowed, in days (keeps registers printable and queries fast).</summary>
    public int MaxRangeDays { get; init; } = 400;
    /// <summary>"As on" date (stock, outstanding).</summary>
    public bool UsesAsOfDate { get; init; }
    public ReportNumberInput? Number { get; init; }
    public IReadOnlyList<ReportChoice> Choices { get; init; } = Array.Empty<ReportChoice>();

    public static ReportParameterSpec DateRange(ReportDatePreset preset = ReportDatePreset.ThisMonth, params ReportChoice[] choices) =>
        new() { UsesDateRange = true, DefaultRange = preset, Choices = choices };

    public static ReportParameterSpec AsOf(params ReportChoice[] choices) => new() { UsesAsOfDate = true, Choices = choices };
}

/// <summary>A catalogue entry: what the report is, where it is listed and which filters it takes.</summary>
public sealed record ReportDefinition(
    string Id,
    string Name,
    ReportCategory Category,
    string Description,
    ReportParameterSpec Parameters,
    IReadOnlyList<string> Keywords)
{
    public string CategoryLabel => Category.ToLabel();

    /// <summary>Case-insensitive match on name, description, category and keywords; blank text matches everything.</summary>
    public bool Matches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var haystack = string.Join(' ', new[] { Name, Description, CategoryLabel, Id }.Concat(Keywords));
        return terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}
