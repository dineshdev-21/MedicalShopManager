using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.SalesReturns;

/// <summary>How the value of a sale return is settled. Stored as INTEGER in SaleReturns.RefundMode; never renumber.</summary>
public enum SaleRefundMode
{
    /// <summary>Money paid back now (cash / UPI / card / bank), recorded as a refund of the credit note.</summary>
    Cash = 1,
    /// <summary>Credit note stays open in the customer's favour (adjusted later or refunded from Credit Notes).</summary>
    CreditToCustomer = 2
}

/// <summary>What happens to the returned units. Stored as INTEGER in SaleReturnItems.Disposition; never renumber.</summary>
public enum SaleReturnDisposition
{
    /// <summary>Back into the same batch as sellable stock.</summary>
    Restock = 1,
    /// <summary>The batch has expired: units go back into it and sit in expired stock (for an expiry return to the distributor).</summary>
    ExpiredStock = 2,
    /// <summary>Damaged / opened: not put back into stock.</summary>
    Damaged = 3
}

/// <summary>Life cycle of a sale return. Stored as INTEGER in SaleReturns.Status; never renumber.</summary>
public enum SaleReturnStatus
{
    Active = 1,
    /// <summary>Cancelled: restocked units taken back out, refunds reversed, numbers kept for audit.</summary>
    Cancelled = 2
}

/// <summary>Payment-status filter of the sales register.</summary>
public enum SalesRegisterStatus
{
    All = 0,
    Paid = 1,
    Partial = 2,
    Unpaid = 3,
    Cancelled = 4
}

public static class SalesReturnsLabels
{
    public static string ToLabel(this SaleRefundMode mode) => mode switch
    {
        SaleRefundMode.Cash => "Refund now",
        SaleRefundMode.CreditToCustomer => "Credit to customer",
        _ => mode.ToString()
    };

    public static string ToLabel(this SaleReturnDisposition disposition) => disposition switch
    {
        SaleReturnDisposition.Restock => "Restocked",
        SaleReturnDisposition.ExpiredStock => "Expired stock",
        SaleReturnDisposition.Damaged => "Damaged (not restocked)",
        _ => disposition.ToString()
    };

    public static string ToLabel(this SalesRegisterStatus status) => status switch
    {
        SalesRegisterStatus.All => "All bills",
        SalesRegisterStatus.Paid => "Paid",
        SalesRegisterStatus.Partial => "Partly paid",
        SalesRegisterStatus.Unpaid => "Unpaid / credit",
        SalesRegisterStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    /// <summary>Short payment-mode label for register columns.</summary>
    public static string ModeLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "Cash",
        PaymentMethod.UPI => "UPI",
        PaymentMethod.Card => "Card",
        PaymentMethod.BankTransfer => "Bank",
        PaymentMethod.Credit => "Credit",
        PaymentMethod.Split => "Split",
        _ => "Other"
    };

    /// <summary>Methods a refund can be paid by.</summary>
    public static readonly IReadOnlyList<PaymentMethod> RefundMethods =
        new[] { PaymentMethod.Cash, PaymentMethod.UPI, PaymentMethod.Card, PaymentMethod.BankTransfer };
}
