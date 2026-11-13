using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.SalesReturns;

namespace MedicalShopManager.App.Modules.SalesReturns.ViewModels;

/// <summary>An option of a drop-down: value plus the text the user sees.</summary>
public sealed record SalesReturnsOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Shared UI helpers of the SalesReturns screens: error wording, option lists, rupee entry.</summary>
internal static class SalesReturnsUi
{
    /// <summary>User-facing messages for an exception (validation messages as written; details of anything else go to the log).</summary>
    public static IReadOnlyList<string> Messages(Exception ex, string action) => ex switch
    {
        SaleReturnValidationException v => v.Errors.Count > 0 ? v.Errors : new[] { v.Message },
        ArgumentException or InvalidOperationException => new[] { ex.Message },
        _ => new[] { $"Could not {action}: {ex.Message}" }
    };

    public static string Message(Exception ex, string action) => string.Join(Environment.NewLine, Messages(ex, action));

    /// <summary>Rupees typed on screen to paisa (rounded once, half away from zero).</summary>
    public static long Paisa(decimal rupees) => (long)Math.Round(rupees * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal Rupees(long paisa) => paisa / 100m;

    public static IReadOnlyList<SalesReturnsOption<SalesRegisterStatus>> StatusFilters { get; } =
        Enum.GetValues<SalesRegisterStatus>().Select(s => new SalesReturnsOption<SalesRegisterStatus>(s, s.ToLabel())).ToList();

    public static IReadOnlyList<SalesReturnsOption<PaymentMethod?>> ModeFilters { get; } = new[]
    {
        new SalesReturnsOption<PaymentMethod?>(null, "All modes"),
        new SalesReturnsOption<PaymentMethod?>(PaymentMethod.Cash, "Cash"),
        new SalesReturnsOption<PaymentMethod?>(PaymentMethod.UPI, "UPI"),
        new SalesReturnsOption<PaymentMethod?>(PaymentMethod.Card, "Card"),
        new SalesReturnsOption<PaymentMethod?>(PaymentMethod.BankTransfer, "Bank transfer"),
        new SalesReturnsOption<PaymentMethod?>(PaymentMethod.Credit, "Credit"),
        new SalesReturnsOption<PaymentMethod?>(PaymentMethod.Split, "Split")
    };

    public static IReadOnlyList<SalesReturnsOption<SaleRefundMode>> RefundModes { get; } = new[]
    {
        new SalesReturnsOption<SaleRefundMode>(SaleRefundMode.Cash, "Refund now"),
        new SalesReturnsOption<SaleRefundMode>(SaleRefundMode.CreditToCustomer, "Credit to customer (credit note)")
    };

    public static IReadOnlyList<SalesReturnsOption<SaleRefundMode?>> RefundModeFilters { get; } = new[]
    {
        new SalesReturnsOption<SaleRefundMode?>(null, "All"),
        new SalesReturnsOption<SaleRefundMode?>(SaleRefundMode.Cash, "Refunded now"),
        new SalesReturnsOption<SaleRefundMode?>(SaleRefundMode.CreditToCustomer, "Credit to customer")
    };

    public static IReadOnlyList<SalesReturnsOption<PaymentMethod>> RefundMethods { get; } = new[]
    {
        new SalesReturnsOption<PaymentMethod>(PaymentMethod.Cash, "Cash"),
        new SalesReturnsOption<PaymentMethod>(PaymentMethod.UPI, "UPI"),
        new SalesReturnsOption<PaymentMethod>(PaymentMethod.Card, "Card"),
        new SalesReturnsOption<PaymentMethod>(PaymentMethod.BankTransfer, "Bank transfer")
    };

    /// <summary>Common reasons offered in the editable REASON box.</summary>
    public static IReadOnlyList<string> Reasons { get; } = new[]
    {
        "Prescription changed by doctor", "Excess quantity", "Wrong item billed", "Adverse reaction / side effect",
        "Damaged / defective pack", "Near expiry", "Patient discharged / deceased", "Other"
    };
}
