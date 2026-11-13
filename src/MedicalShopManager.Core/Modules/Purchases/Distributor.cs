namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>
/// Supplier / distributor master v2 (spec 04 "Distributors", MASTER-SPEC §2 Distributor): the pre-6.2 Suppliers
/// row plus state, pincode and credit period (migration 12). Money is paisa.
/// </summary>
public sealed class Distributor
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }

    /// <summary>Two-digit GST state code (e.g. "29" Karnataka). Derived from the GSTIN when blank.</summary>
    public string? StateCode { get; set; }

    public string? Pincode { get; set; }
    public string? Gstin { get; set; }

    /// <summary>Wholesale drug licence number (Form 20B/21B).</summary>
    public string? DrugLicenseNumber { get; set; }

    /// <summary>Credit period in days; the purchase due date defaults to invoice date + this.</summary>
    public int CreditDays { get; set; }

    /// <summary>Amount owed to the distributor when the shop started using the app (positive = shop pays).</summary>
    public long OpeningBalancePaisa { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Derived in SQL, never written: opening + unpaid balances of confirmed purchases − open debit notes
    /// (net of refunds received).
    /// </summary>
    public long OutstandingBalancePaisa { get; set; }

    /// <summary>Derived: date of the latest confirmed purchase ("Prev:" on the purchase screen).</summary>
    public DateTime? LastPurchaseDate { get; set; }

    /// <summary>Derived: unpaid amount of confirmed purchases past their due date.</summary>
    public long OverduePaisa { get; set; }

    /// <summary>State code used for the IGST decision: <see cref="StateCode"/>, else the GSTIN's first two digits.</summary>
    public string? EffectiveStateCode =>
        !string.IsNullOrWhiteSpace(StateCode) ? StateCode.Trim()
        : Gstin is { Length: >= 2 } g ? g[..2] : null;

    public decimal OpeningBalance => OpeningBalancePaisa / 100m;
    public decimal OutstandingBalance => OutstandingBalancePaisa / 100m;
    public decimal Overdue => OverduePaisa / 100m;

    /// <summary>One-line summary for pickers: "Alpha Pharma · 29ABCDE1234F1Z5 · 30 days".</summary>
    public string DisplayLine => string.Join(" · ", new[]
    {
        Name,
        string.IsNullOrWhiteSpace(Gstin) ? null : Gstin,
        CreditDays > 0 ? $"{CreditDays} days credit" : null
    }.Where(s => !string.IsNullOrEmpty(s)));

    public override string ToString() => Name;
}
