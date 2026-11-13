using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Models;

/// <summary>
/// Represents a specific batch of a medicine with its own expiry, cost, and quantity tracking.
/// Each batch is tracked independently for FEFO (First Expiry, First Out) dispensing.
/// Quantities are in units. All rates are per pack of <see cref="UnitsPerPack"/> units, in paisa.
/// FROZEN for module agents (docs/ARCHITECTURE-MODULES.md).
/// </summary>
public class MedicineBatch
{
    public long Id { get; set; }
    public long MedicineId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime? ManufacturingDate { get; set; }

    /// <summary>Expiry date. Month/year entry is stored as the last day of that month (<see cref="ExpiryFromMonthYear"/>).</summary>
    public DateTime ExpiryDate { get; set; }

    public DateTime PurchaseDate { get; set; }
    public long? SupplierId { get; set; }

    /// <summary>Units received, including <see cref="FreeUnits"/>.</summary>
    public int QuantityReceived { get; set; }

    /// <summary>Units currently in stock (loose units, not packs).</summary>
    public int QuantityRemaining { get; set; }

    /// <summary>Free units received with the purchase (scheme / bonus), already included in <see cref="QuantityReceived"/>.</summary>
    public int FreeUnits { get; set; }

    /// <summary>
    /// Pack size the rates on this batch refer to, snapshotted from the medicine when the batch is created so
    /// a later change to Medicine.UnitsPerPack never re-prices existing stock. Always ≥ 1.
    /// </summary>
    public int UnitsPerPack { get; set; } = 1;

    /// <summary>Purchase rate per pack in paisa (GST-exclusive, as billed by the distributor).</summary>
    public long PurchaseRatePaisa { get; set; }

    /// <summary>Price to retailer per pack in paisa (GST-exclusive). 0 when not given.</summary>
    public long PtrPaisa { get; set; }

    /// <summary>Selling rate per pack in paisa (GST-inclusive, ≤ MRP).</summary>
    public long SaleRatePaisa { get; set; }

    /// <summary>Maximum Retail Price per pack in paisa (GST-inclusive, printed on the pack).</summary>
    public long MrpPaisa { get; set; }

    /// <summary>Discount percentage on this batch.</summary>
    public decimal DiscountPercent { get; set; }

    /// <summary>GST rate as a percentage.</summary>
    public decimal GstRatePercent { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Medicine? Medicine { get; set; }
    public Supplier? Supplier { get; set; }

    // Computed properties
    public bool IsExpired => ExpiryDate.Date <= DateTime.Today;
    public bool IsNearExpiry => !IsExpired && ExpiryDate.Date <= DateTime.Today.AddDays(90);
    public bool HasStock => QuantityRemaining > 0;
    public bool IsSellable => HasStock && !IsExpired && IsActive;

    /// <summary>Days until expiry. Negative means already expired.</summary>
    public int DaysToExpiry => (ExpiryDate.Date - DateTime.Today).Days;

    /// <summary>Expiry as printed on packs, "MM/yy".</summary>
    public string ExpiryLabel => ExpiryDate.ToString("MM/yy");

    /// <summary>Stock as "packs + loose", e.g. "3 + 5" for 50 units of a 15's.</summary>
    public string QuantityRemainingDisplay => PackQuantity.Format(QuantityRemaining, Math.Max(UnitsPerPack, 1));

    public long RemainingPacks => QuantityRemaining / Math.Max(UnitsPerPack, 1);
    public long RemainingLooseUnits => QuantityRemaining % Math.Max(UnitsPerPack, 1);

    // Convenience decimal properties for display (rupees per pack)
    public decimal PurchaseRate => PurchaseRatePaisa / 100m;
    public decimal Ptr => PtrPaisa / 100m;
    public decimal SaleRate => SaleRatePaisa / 100m;
    public decimal Mrp => MrpPaisa / 100m;

    /// <summary>Last day of the given expiry month: "11/26" means usable through 30-Nov-2026.</summary>
    public static DateTime ExpiryFromMonthYear(int month, int year)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        if (year < 100) year += 2000;
        return new DateTime(year, month, DateTime.DaysInMonth(year, month));
    }
}
