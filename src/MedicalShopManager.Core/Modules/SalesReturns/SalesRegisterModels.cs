using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.SalesReturns;

/// <summary>Filters of the sales register (Invoices ▸ Sales, Localwell /sale/view-sales and /transactions/sales).</summary>
public sealed record SalesRegisterFilter
{
    public DateTime From { get; init; } = DateTime.Today.AddDays(-30);
    public DateTime To { get; init; } = DateTime.Today;
    public long? CustomerId { get; init; }
    public SalesRegisterStatus Status { get; init; } = SalesRegisterStatus.All;
    /// <summary>Payment mode of the bill; null = any.</summary>
    public PaymentMethod? Mode { get; init; }
    /// <summary>Bill number, customer name or mobile (contains).</summary>
    public string? SearchText { get; init; }
    public int Limit { get; init; } = 2000;
}

/// <summary>One bill in the sales register (read-only view over Billing's Sales table plus this module's returns).</summary>
public sealed record SalesRegisterRow
{
    public long SaleId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateTime InvoiceDate { get; init; }
    public long? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerPhone { get; init; }
    public int ItemCount { get; init; }
    public long GrandTotalPaisa { get; init; }
    public long AmountPaidPaisa { get; init; }
    public long BalanceDuePaisa { get; init; }
    public long TaxPaisa { get; init; }
    public PaymentMethod PaymentMethod { get; init; }
    public bool IsCancelled { get; init; }
    public DateTime? DueDate { get; init; }
    /// <summary>Value of active sale returns against this bill.</summary>
    public long ReturnedPaisa { get; init; }
    public string? CreatedByName { get; init; }

    public string CustomerDisplay => string.IsNullOrWhiteSpace(CustomerName) ? "Cash / Counter Sale" : CustomerName!;
    public string ModeLabel => SalesReturnsLabels.ModeLabel(PaymentMethod);
    public bool HasReturns => ReturnedPaisa > 0;
    public bool CanReturn => !IsCancelled;
    public string StatusLabel => SalesRegisterRules.StatusLabel(this, DateTime.Today);
}

/// <summary>Footer totals of the register (cancelled bills excluded).</summary>
public sealed record SalesRegisterTotals
{
    public int BillCount { get; init; }
    public int CancelledCount { get; init; }
    public long GrandTotalPaisa { get; init; }
    public long PaidPaisa { get; init; }
    public long DuePaisa { get; init; }
    public long TaxPaisa { get; init; }
    public long ReturnedPaisa { get; init; }
    /// <summary>Sales less returns.</summary>
    public long NetSalesPaisa { get; init; }
}

/// <summary>Pure rules of the sales register.</summary>
public static class SalesRegisterRules
{
    /// <summary>Cancelled / Paid / Overdue / Partly paid / Unpaid, as the register STATUS chip shows it.</summary>
    public static string StatusLabel(SalesRegisterRow row, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsCancelled) return "Cancelled";
        if (row.BalanceDuePaisa <= 0) return "Paid";
        if (row.DueDate is { } due && due.Date < today.Date) return "Overdue";
        return row.AmountPaidPaisa > 0 ? "Partly paid" : "Unpaid";
    }

    /// <summary>True when the row passes the status filter (the repository applies the same rule in SQL).</summary>
    public static bool Matches(SalesRegisterRow row, SalesRegisterStatus status) => status switch
    {
        SalesRegisterStatus.All => true,
        SalesRegisterStatus.Cancelled => row.IsCancelled,
        SalesRegisterStatus.Paid => !row.IsCancelled && row.BalanceDuePaisa <= 0,
        SalesRegisterStatus.Partial => !row.IsCancelled && row.BalanceDuePaisa > 0 && row.AmountPaidPaisa > 0,
        SalesRegisterStatus.Unpaid => !row.IsCancelled && row.BalanceDuePaisa > 0 && row.AmountPaidPaisa <= 0,
        _ => true
    };

    public static SalesRegisterTotals Summarize(IEnumerable<SalesRegisterRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        var active = list.Where(r => !r.IsCancelled).ToList();
        var total = active.Sum(r => r.GrandTotalPaisa);
        var returned = active.Sum(r => r.ReturnedPaisa);
        return new SalesRegisterTotals
        {
            BillCount = active.Count,
            CancelledCount = list.Count - active.Count,
            GrandTotalPaisa = total,
            PaidPaisa = active.Sum(r => r.AmountPaidPaisa),
            DuePaisa = active.Sum(r => r.BalanceDuePaisa),
            TaxPaisa = active.Sum(r => r.TaxPaisa),
            ReturnedPaisa = returned,
            NetSalesPaisa = total - returned
        };
    }

    /// <summary>Validates a register filter; null when valid, else a user-facing message.</summary>
    public static string? Validate(SalesRegisterFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.To.Date < filter.From.Date) return "The 'to' date is before the 'from' date.";
        if ((filter.To.Date - filter.From.Date).TotalDays > 366 * 5) return "Choose a date range of at most 5 years.";
        return null;
    }
}
