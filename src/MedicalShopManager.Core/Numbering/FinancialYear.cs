namespace MedicalShopManager.Core.Numbering;

/// <summary>Indian financial year (1 April – 31 March), identified by the calendar year it starts in.</summary>
public readonly record struct FinancialYear(int StartYear)
{
    public DateTime Start => new(StartYear, 4, 1);
    public DateTime End => new(StartYear + 1, 3, 31);

    /// <summary>"2026-27" style label (used on screens and in the {FY} token).</summary>
    public string Label => $"{StartYear}-{(StartYear + 1) % 100:D2}";

    /// <summary>"2627" style compact label (the {FY2} token, keeps invoice numbers within the 16-char GST limit).</summary>
    public string ShortLabel => $"{StartYear % 100:D2}{(StartYear + 1) % 100:D2}";

    /// <summary>The financial year containing <paramref name="date"/>: January–March belong to the year that began the previous April.</summary>
    public static FinancialYear For(DateTime date) => new(date.Month >= 4 ? date.Year : date.Year - 1);

    public override string ToString() => Label;
}
