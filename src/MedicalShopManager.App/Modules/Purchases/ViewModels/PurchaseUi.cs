using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>An option of a drop-down: value plus the text the user sees.</summary>
public sealed record PurchaseOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Shared UI helpers of the Purchases screens: error wording, option lists.</summary>
internal static class PurchaseUi
{
    /// <summary>
    /// User-facing messages for an exception: every validation message, the rule message as written, or a short
    /// "could not …" line for anything unexpected (details go to the log).
    /// </summary>
    public static IReadOnlyList<string> Messages(Exception ex, string action) => ex switch
    {
        PurchaseValidationException v => v.Errors.Count > 0 ? v.Errors : new[] { v.Message },
        PurchaseRuleException or DuplicatePurchaseInvoiceException => new[] { ex.Message },
        ArgumentException or InvalidOperationException => new[] { ex.Message },
        _ => new[] { $"Could not {action}: {ex.Message}" }
    };

    public static string Message(Exception ex, string action) => string.Join(Environment.NewLine, Messages(ex, action));

    /// <summary>Ways to pay a distributor (Credit is not a payment).</summary>
    public static IReadOnlyList<PurchaseOption<PaymentMethod>> PaymentMethods { get; } = new[]
    {
        new PurchaseOption<PaymentMethod>(PaymentMethod.Cash, "Cash"),
        new PurchaseOption<PaymentMethod>(PaymentMethod.UPI, "UPI"),
        new PurchaseOption<PaymentMethod>(PaymentMethod.BankTransfer, "Bank transfer / NEFT"),
        new PurchaseOption<PaymentMethod>(PaymentMethod.Card, "Card"),
        new PurchaseOption<PaymentMethod>(PaymentMethod.Other, "Cheque / Other")
    };

    public static IReadOnlyList<PurchaseOption<PurchaseListStatusFilter>> StatusFilters { get; } = new[]
    {
        new PurchaseOption<PurchaseListStatusFilter>(PurchaseListStatusFilter.All, "All"),
        new PurchaseOption<PurchaseListStatusFilter>(PurchaseListStatusFilter.Draft, "Draft"),
        new PurchaseOption<PurchaseListStatusFilter>(PurchaseListStatusFilter.Unpaid, "Credit (unpaid)"),
        new PurchaseOption<PurchaseListStatusFilter>(PurchaseListStatusFilter.Partial, "Partly paid"),
        new PurchaseOption<PurchaseListStatusFilter>(PurchaseListStatusFilter.Paid, "Paid"),
        new PurchaseOption<PurchaseListStatusFilter>(PurchaseListStatusFilter.Overdue, "Overdue"),
        new PurchaseOption<PurchaseListStatusFilter>(PurchaseListStatusFilter.Cancelled, "Cancelled")
    };

    public static IReadOnlyList<PurchaseOption<PurchaseReturnReason>> ReturnReasons { get; } =
        Enum.GetValues<PurchaseReturnReason>().Select(r => new PurchaseOption<PurchaseReturnReason>(r, r.ToLabel())).ToList();

    /// <summary>"All distributors" entry plus the distributors, for list filters.</summary>
    public static List<PurchaseOption<long?>> SupplierFilter(IEnumerable<Distributor> distributors)
    {
        var list = new List<PurchaseOption<long?>> { new(null, "All distributors") };
        list.AddRange(distributors.Select(d => new PurchaseOption<long?>(d.Id, d.Name)));
        return list;
    }

    public static decimal Rupees(long paisa) => paisa / 100m;

    public static long Paisa(decimal rupees) => PurchaseCalculator.ToPaisa(rupees);
}
