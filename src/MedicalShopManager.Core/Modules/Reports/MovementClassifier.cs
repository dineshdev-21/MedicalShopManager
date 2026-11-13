namespace MedicalShopManager.Core.Modules.Reports;

public enum MovementClass
{
    Fast,
    Slow,
    NonMoving
}

/// <summary>Sales of one item in the analysis period plus its stock now.</summary>
public sealed record ItemMovementInput
{
    public long MedicineId { get; init; }
    public string Name { get; init; } = string.Empty;
    public long SalesValuePaisa { get; init; }
    public long UnitsSold { get; init; }
    public long StockUnits { get; init; }
}

/// <summary>
/// FSN analysis by sales value (Pareto): items with sales in the period are ranked by sales value; those that together
/// make up the first <see cref="FastShare"/> of total sales value are fast-moving, the rest slow-moving. Items with no
/// sale in the period but stock in hand are non-moving. Items with neither are not classified (null).
/// </summary>
public static class MovementClassifier
{
    public const decimal FastShare = 0.80m;

    public static IReadOnlyDictionary<long, MovementClass> Classify(IReadOnlyList<ItemMovementInput> items)
    {
        var result = new Dictionary<long, MovementClass>();
        var sold = items.Where(i => i.SalesValuePaisa > 0 || i.UnitsSold > 0)
            .OrderByDescending(i => i.SalesValuePaisa).ThenByDescending(i => i.UnitsSold).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var total = sold.Sum(i => Math.Max(i.SalesValuePaisa, 0));
        long cumulativeBefore = 0;
        foreach (var item in sold)
        {
            // An item is fast while the share reached *before* it is still under the cut-off, so the item that crosses
            // 80 % is fast too (the top seller is always fast).
            var fast = total == 0 || cumulativeBefore < FastShare * total;
            result[item.MedicineId] = fast && item.SalesValuePaisa > 0 ? MovementClass.Fast : MovementClass.Slow;
            cumulativeBefore += Math.Max(item.SalesValuePaisa, 0);
        }
        foreach (var item in items.Where(i => i.SalesValuePaisa <= 0 && i.UnitsSold <= 0 && i.StockUnits > 0))
            result[item.MedicineId] = MovementClass.NonMoving;
        return result;
    }

    public static string ToLabel(this MovementClass value) => value switch
    {
        MovementClass.Fast => "Fast",
        MovementClass.Slow => "Slow",
        MovementClass.NonMoving => "Non-moving",
        _ => value.ToString()
    };

    public static bool Matches(string filterKey, MovementClass value) => filterKey switch
    {
        ReportChoiceKeys.Fast => value == MovementClass.Fast,
        ReportChoiceKeys.Slow => value == MovementClass.Slow,
        ReportChoiceKeys.NonMoving => value == MovementClass.NonMoving,
        _ => true
    };

    /// <summary>Days the stock lasts at the period's average daily sale; null when nothing sold.</summary>
    public static long? DaysOfCover(long stockUnits, long unitsSold, int periodDays)
    {
        if (unitsSold <= 0 || periodDays <= 0) return null;
        return (long)Math.Floor(stockUnits * (decimal)periodDays / unitsSold);
    }
}
