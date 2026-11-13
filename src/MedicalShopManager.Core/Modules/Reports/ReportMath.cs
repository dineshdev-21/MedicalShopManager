using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>
/// Report arithmetic, all through <see cref="PricingCalculator"/> (ARCHITECTURE §7): stock values, the cost share of
/// returned units, and margin on sales excluding GST.
/// </summary>
public static class ReportMath
{
    /// <summary>Value of <paramref name="units"/> at a per-pack rate (0 for a pack size below 1, which bad data could hold).</summary>
    public static long StockValue(long ratePerPackPaisa, long units, int unitsPerPack) =>
        units == 0 || ratePerPackPaisa == 0 ? 0 : PricingCalculator.AmountForUnits(ratePerPackPaisa, units, Math.Max(unitsPerPack, 1));

    /// <summary>
    /// Cost of goods returned against a bill line: the line's recorded cost pro rata to the units returned,
    /// round(lineCost × returned / sold). Without a bill line (or a line with no units) the batch's purchase rate is used.
    /// </summary>
    public static long ReturnedCost(long saleLineCostPaisa, long saleLineUnits, long returnedUnits, long batchRatePerPackPaisa, int batchUnitsPerPack)
    {
        if (returnedUnits <= 0) return 0;
        if (saleLineUnits > 0)
            return PricingCalculator.AmountForUnits(saleLineCostPaisa, Math.Min(returnedUnits, saleLineUnits), (int)Math.Min(saleLineUnits, int.MaxValue));
        return StockValue(batchRatePerPackPaisa, returnedUnits, batchUnitsPerPack);
    }

    /// <summary>Profit as % of net sales excluding GST (2 dp); 0 when there are no sales.</summary>
    public static decimal MarginPercent(long profitPaisa, long netSalesExGstPaisa) =>
        netSalesExGstPaisa <= 0 ? 0m : Math.Round(PricingCalculator.PercentFromAmount(profitPaisa, netSalesExGstPaisa), 2, MidpointRounding.AwayFromZero);

    /// <summary>Share of a part in a whole, in % (2 dp); 0 when the whole is 0.</summary>
    public static decimal SharePercent(long partPaisa, long wholePaisa) =>
        wholePaisa == 0 ? 0m : Math.Round(PricingCalculator.PercentFromAmount(partPaisa, wholePaisa), 2, MidpointRounding.AwayFromZero);

    /// <summary>Whole days from <paramref name="from"/> to <paramref name="to"/> (dates only).</summary>
    public static long DaysBetween(DateTime from, DateTime to) => (long)(to.Date - from.Date).TotalDays;
}

/// <summary>Outstanding split by age (days since the document date as on a date): 0–30, 31–60, 61–90, over 90.</summary>
public sealed class ReportAgeing
{
    private readonly long[] _buckets = new long[4];

    public long Days0To30 => _buckets[0];
    public long Days31To60 => _buckets[1];
    public long Days61To90 => _buckets[2];
    public long Over90 => _buckets[3];
    public long Total => _buckets.Sum();
    public int Documents { get; private set; }
    public DateTime? Oldest { get; private set; }

    public static int BucketOf(long ageDays) => ageDays switch
    {
        <= 30 => 0,
        <= 60 => 1,
        <= 90 => 2,
        _ => 3
    };

    /// <summary>Adds an open amount dated <paramref name="documentDate"/>; zero / negative amounts are ignored.</summary>
    public void Add(DateTime documentDate, long amountPaisa, DateTime asOf)
    {
        if (amountPaisa <= 0) return;
        _buckets[BucketOf(Math.Max(ReportMath.DaysBetween(documentDate, asOf), 0))] += amountPaisa;
        Documents++;
        if (Oldest == null || documentDate.Date < Oldest) Oldest = documentDate.Date;
    }
}

/// <summary>Expiry state of a batch as on a date: expired from its expiry date onwards (ARCHITECTURE §11).</summary>
public static class ReportExpiry
{
    public static long DaysLeft(DateTime expiryDate, DateTime asOf) => ReportMath.DaysBetween(asOf, expiryDate);

    public static bool IsExpired(DateTime expiryDate, DateTime asOf) => expiryDate.Date <= asOf.Date;

    public static bool IsNearExpiry(DateTime expiryDate, DateTime asOf, int withinDays) =>
        !IsExpired(expiryDate, asOf) && DaysLeft(expiryDate, asOf) <= withinDays;

    public static string StatusLabel(DateTime expiryDate, DateTime asOf, int withinDays) =>
        IsExpired(expiryDate, asOf) ? "Expired" : IsNearExpiry(expiryDate, asOf, withinDays) ? "Near expiry" : "OK";

    /// <summary>Whether a batch passes the expiry filter of the near-expiry report.</summary>
    public static bool Matches(string filterKey, DateTime expiryDate, DateTime asOf, int withinDays) => filterKey switch
    {
        ReportChoiceKeys.Expired => IsExpired(expiryDate, asOf),
        ReportChoiceKeys.NearExpiry => IsNearExpiry(expiryDate, asOf, withinDays),
        _ => IsExpired(expiryDate, asOf) || IsNearExpiry(expiryDate, asOf, withinDays)
    };
}
