using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>Customer card metrics on the follow-ups page (Localwell customer_metrics: sales count, total, average order, revisit).</summary>
public static class CustomerMetrics
{
    /// <summary>
    /// Average order value = round(total ÷ bills) to the paisa; average revisit = days between the first and last bill
    /// ÷ (bills − 1), rounded (null with fewer than two bills).
    /// </summary>
    public static CustomerFollowupSummary Build(long customerId, string name, string? phone, string? address, int salesCount,
        long totalSalesPaisa, DateTime? firstSale, DateTime? lastSale)
    {
        var average = salesCount > 0 ? PricingCalculator.RoundPaisa((decimal)totalSalesPaisa / salesCount) : 0;
        int? revisit = salesCount > 1 && firstSale is { } first && lastSale is { } last
            ? (int)Math.Round((decimal)(last.Date - first.Date).Days / (salesCount - 1), MidpointRounding.AwayFromZero)
            : null;
        return new CustomerFollowupSummary(customerId, name, phone, address, salesCount, totalSalesPaisa, average, firstSale, lastSale, revisit);
    }
}
