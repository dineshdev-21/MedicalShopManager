namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>Printable purchase register: the rows of the purchase list with its filters and totals.</summary>
public sealed record PurchaseRegisterReport(
    DateTime From,
    DateTime To,
    string? SupplierName,
    string StatusLabel,
    IReadOnlyList<PurchaseListRow> Rows,
    PurchaseListSummary Summary)
{
    public static PurchaseRegisterReport Create(PurchaseListFilter filter, string? supplierName, IReadOnlyList<PurchaseListRow> rows)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(rows);
        return new PurchaseRegisterReport(filter.From.Date, filter.To.Date, supplierName,
            filter.Status == PurchaseListStatusFilter.All ? "All" : filter.Status.ToString(), rows, PurchaseListSummary.From(rows));
    }

    public string PeriodLabel => $"{From:dd-MM-yyyy} to {To:dd-MM-yyyy}";
}
