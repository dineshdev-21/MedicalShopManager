using System.Globalization;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>Display formats shared by the viewer, print template and Excel export.</summary>
public static class ReportFormat
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>GST portal / Indian date style: 05-Nov-2026.</summary>
    public static string Date(DateTime date) => date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);

    /// <summary>Rupees with Indian digit grouping: 123456.78 → "1,23,456.78".</summary>
    public static string Money(long paisa) => (paisa / 100m).ToString("N2", India);

    public static decimal Rupees(long paisa) => paisa / 100m;

    public static string Percent(decimal percent) => percent.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>Display text for any cell value of the given column kind.</summary>
    public static string Cell(object? value, ReportColumnKind kind) => value switch
    {
        null => string.Empty,
        DateTime d => Date(d),
        long l when kind == ReportColumnKind.Money => Money(l),
        long l => l.ToString("N0", India),
        int i => i.ToString("N0", India),
        decimal m when kind == ReportColumnKind.Percent => Percent(m),
        decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    /// <summary>Units as packs + loose for a pack size ("3 + 5"); plain units when the pack is 1.</summary>
    public static string Quantity(long units, int unitsPerPack) =>
        unitsPerPack <= 1 ? units.ToString(CultureInfo.InvariantCulture) : PackQuantity.Format(units, unitsPerPack);

    /// <summary>GST "Place Of Supply" text: "29-Karnataka". Blank / unknown codes return the code alone.</summary>
    public static string PlaceOfSupply(string? stateCode)
    {
        var state = IndianStates.Find(stateCode);
        return state == null ? (stateCode ?? string.Empty).Trim() : $"{state.Code}-{state.Name}";
    }

    /// <summary>State code of a GSTIN (first two digits) or null when it is not a 15-character GSTIN.</summary>
    public static string? StateOfGstin(string? gstin)
    {
        var g = NormalizeGstin(gstin);
        return g == null ? null : g[..2];
    }

    /// <summary>Upper-cased 15-character GSTIN starting with two digits, else null (treated as unregistered).</summary>
    public static string? NormalizeGstin(string? gstin)
    {
        if (string.IsNullOrWhiteSpace(gstin)) return null;
        var g = gstin.Trim().ToUpperInvariant();
        return g.Length == 15 && char.IsDigit(g[0]) && char.IsDigit(g[1]) ? g : null;
    }

    /// <summary>GST rate as the portal expects it (12, 5, 0.25 …).</summary>
    public static decimal Rate(decimal rate) => Math.Round(rate, 2, MidpointRounding.AwayFromZero);
}
