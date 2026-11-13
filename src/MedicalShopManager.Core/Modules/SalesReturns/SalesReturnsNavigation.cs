namespace MedicalShopManager.Core.Modules.SalesReturns;

/// <summary>
/// Page keys and navigation parameters of the SalesReturns module, so other modules (search, customer screens,
/// the billing view) can open its pages: <c>INavigationService.NavigateTo(SalesReturnsPages.Entry, new StartSaleReturnRequest(saleId))</c>.
/// </summary>
public static class SalesReturnsPages
{
    /// <summary>Invoices ▸ Sales (sales register).</summary>
    public const string SalesRegister = "SalesHistory";
    /// <summary>Sales ▸ Sale Return (Ctrl+Alt+T). Accepts <see cref="StartSaleReturnRequest"/> or a plain <c>long</c> sale id.</summary>
    public const string Entry = "SaleReturnEntry";
    /// <summary>Invoices ▸ Sale Returns register.</summary>
    public const string Register = "SaleReturns";
    /// <summary>Invoices ▸ Credit Notes (open customer credit, refunds).</summary>
    public const string CreditNotes = "SaleCreditNotes";
}

/// <summary>Opens the sale return screen on a bill.</summary>
public sealed record StartSaleReturnRequest(long SaleId);

/// <summary>Opens the sales register filtered to one customer.</summary>
public sealed record SalesRegisterCustomerRequest(long CustomerId, string? CustomerName);
