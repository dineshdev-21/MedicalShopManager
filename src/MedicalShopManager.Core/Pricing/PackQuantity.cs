namespace MedicalShopManager.Core.Pricing;

/// <summary>
/// A quantity expressed as whole packs plus loose units (Localwell's "PACKS" + "LOOSE" columns).
/// Stock is always stored in units; this type only converts for entry and display.
/// </summary>
public readonly record struct PackQuantity(long Packs, long Loose)
{
    /// <summary>Total units = packs × unitsPerPack + loose.</summary>
    public long ToUnits(int unitsPerPack)
    {
        PricingCalculator.EnsureUnitsPerPack(unitsPerPack);
        if (Packs < 0 || Loose < 0)
            throw new ArgumentOutOfRangeException(nameof(Packs), "Packs and loose units cannot be negative.");
        return checked(Packs * unitsPerPack + Loose);
    }

    /// <summary>Splits a non-negative unit count into whole packs and the loose remainder.</summary>
    public static PackQuantity FromUnits(long units, int unitsPerPack)
    {
        PricingCalculator.EnsureUnitsPerPack(unitsPerPack);
        if (units < 0)
            throw new ArgumentOutOfRangeException(nameof(units), "Use Format() for negative quantities.");
        return new PackQuantity(units / unitsPerPack, units % unitsPerPack);
    }

    /// <summary>
    /// Display text for a unit count: "3" (whole packs), "3 + 5" (packs + loose), "0 + 5", or with a leading "-"
    /// for negative adjustments. A pack size of 1 always shows plain units.
    /// </summary>
    public static string Format(long units, int unitsPerPack)
    {
        var sign = units < 0 ? "-" : string.Empty;
        var q = FromUnits(Math.Abs(units), unitsPerPack);
        return q.Loose == 0 ? $"{sign}{q.Packs}" : $"{sign}{q.Packs} + {q.Loose}";
    }
}
