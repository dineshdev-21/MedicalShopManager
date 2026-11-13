using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>
/// Doctor-wise sales for a period: active bills that name the doctor (Sales.DoctorId, or only the typed
/// Sales.DoctorName for bills made before the doctor master existed). Money in paisa.
/// </summary>
public sealed record DoctorSalesRow
{
    /// <summary>Doctor master id; null for bills that only carry a typed doctor name.</summary>
    public long? DoctorId { get; init; }

    public string DoctorName { get; init; } = string.Empty;
    public string? RegistrationNo { get; init; }
    public string? Specialization { get; init; }
    public int BillCount { get; init; }
    public int PatientCount { get; init; }

    /// <summary>Bill totals (what the customers paid, GST included).</summary>
    public long SalesPaisa { get; init; }

    /// <summary>Taxable value of the lines (ex-GST).</summary>
    public long TaxablePaisa { get; init; }

    /// <summary>Cost of goods of the lines (net purchase rate), as recorded on the bill.</summary>
    public long CostPaisa { get; init; }

    public decimal CommissionPercent { get; init; }
    public bool CommissionOnProfit { get; init; }

    // ─── Computed by DoctorSalesCalculator ───
    public long ProfitPaisa { get; init; }
    public long CommissionBasePaisa { get; init; }
    public long CommissionPaisa { get; init; }

    public bool IsLinked => DoctorId is not null;
    public string DisplayName => DoctorRules.DisplayName(DoctorName);
    public string CommissionLabel => CommissionPercent <= 0 ? "-" : $"{CommissionPercent:0.##}% on {(CommissionOnProfit ? "profit" : "sales")}";
    public string LinkLabel => IsLinked ? string.Empty : "not in doctor master";
}

public sealed record DoctorSalesTotals(int Doctors, int Bills, long SalesPaisa, long TaxablePaisa, long ProfitPaisa, long CommissionPaisa)
{
    public static readonly DoctorSalesTotals Empty = new(0, 0, 0, 0, 0, 0);
}

/// <summary>The doctor-wise sales report (screen and print).</summary>
public sealed record DoctorSalesSummary(DateTime From, DateTime To, IReadOnlyList<DoctorSalesRow> Rows, DoctorSalesTotals Totals)
{
    public string Title => $"Doctor-wise sales {From:dd-MM-yyyy} to {To:dd-MM-yyyy}";
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
}

/// <summary>A bill in the drill-down of one doctor.</summary>
public sealed record DoctorSaleBill(long SaleId, string InvoiceNumber, DateTime InvoiceDate, string? CustomerName, string? PatientName,
    int ItemCount, long GrandTotalPaisa, long TaxablePaisa)
{
    public string PartyDisplay => string.IsNullOrWhiteSpace(PatientName) ? CustomerName ?? "-" : $"{PatientName} ({CustomerName})";
}

/// <summary>Commission and totals of the doctor-wise sales report. All money maths goes through <see cref="PricingCalculator"/>.</summary>
public static class DoctorSalesCalculator
{
    public const int MaxRangeDays = 366;

    /// <summary>
    /// Profit = taxable − cost; commission base = profit (never below 0) when the doctor is paid on profit, else the
    /// taxable value; commission = round(base × % / 100) to the paisa.
    /// </summary>
    public static DoctorSalesRow WithCommission(DoctorSalesRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var profit = row.TaxablePaisa - row.CostPaisa;
        var basis = row.CommissionOnProfit ? Math.Max(0, profit) : row.TaxablePaisa;
        var commission = row.CommissionPercent > 0 ? PricingCalculator.PercentOf(basis, row.CommissionPercent) : 0;
        return row with { ProfitPaisa = profit, CommissionBasePaisa = basis, CommissionPaisa = commission };
    }

    public static DoctorSalesSummary Build(DateTime from, DateTime to, IEnumerable<DoctorSalesRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var computed = rows.Select(WithCommission)
            .OrderByDescending(r => r.SalesPaisa)
            .ThenBy(r => r.DoctorName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var totals = new DoctorSalesTotals(
            computed.Count,
            computed.Sum(r => r.BillCount),
            computed.Sum(r => r.SalesPaisa),
            computed.Sum(r => r.TaxablePaisa),
            computed.Sum(r => r.ProfitPaisa),
            computed.Sum(r => r.CommissionPaisa));
        return new DoctorSalesSummary(from.Date, to.Date, computed, totals);
    }

    public static string? ValidateRange(DateTime from, DateTime to)
    {
        if (to.Date < from.Date) return "The 'to' date is before the 'from' date.";
        if ((to.Date - from.Date).Days > MaxRangeDays) return $"Choose a period of at most {MaxRangeDays} days.";
        return null;
    }
}
