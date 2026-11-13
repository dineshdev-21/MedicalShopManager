namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>
/// Whether sales of a composition (or of a medicine without composition) create refill follow-ups, and at what
/// consumption. The default comes from Compositions.IsChronic (kernel); this module's FollowupRefillRules table
/// overrides it per composition or per medicine (a medicine rule beats its composition's rule).
/// </summary>
public sealed record RefillRule
{
    /// <summary>Set for a composition rule.</summary>
    public long? CompositionId { get; init; }

    /// <summary>Set for a medicine rule (items without a composition).</summary>
    public long? MedicineId { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>Medicines using the composition (composition rules) or the composition text (medicine rules).</summary>
    public string? Detail { get; init; }

    /// <summary>Compositions.IsChronic (false for medicine rules).</summary>
    public bool DefaultIsChronic { get; init; }

    /// <summary>The module's override; null = use the default.</summary>
    public bool? RuleIsChronic { get; init; }

    /// <summary>Consumption for follow-ups of this item; null = the module default.</summary>
    public decimal? UnitsPerDay { get; init; }

    public bool IsChronic => RuleIsChronic ?? DefaultIsChronic;
    public string Kind => CompositionId is not null ? "Composition" : "Medicine";
    public string ConsumptionLabel => UnitsPerDay is { } perDay ? $"{perDay:0.##}/day" : "default";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if ((CompositionId is null) == (MedicineId is null)) errors.Add("A refill rule is for one composition or one medicine.");
        if (UnitsPerDay is { } perDay && RefillCalculator.ValidateUnitsPerDay(perDay) is { } error) errors.Add(error);
        return errors;
    }
}
