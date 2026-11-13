using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Models;

/// <summary>
/// Item master: one row per product the shop stocks (Localwell "Medicine" + "Stock" card).
/// Quantities are in units; prices are per pack of <see cref="UnitsPerPack"/> units.
/// FROZEN for module agents (docs/ARCHITECTURE-MODULES.md): extend with module-owned side tables instead.
/// </summary>
public class Medicine
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Salt / composition text, e.g. "Paracetamol 500mg" (formerly GenericName).</summary>
    public string? Composition { get; set; }

    /// <summary>Normalised composition id; set by the repository from <see cref="Composition"/>, never by callers.</summary>
    public long? CompositionId { get; set; }

    /// <summary>Manufacturer / marketing company (Localwell "COMPANY").</summary>
    public string? Manufacturer { get; set; }

    public string? Brand { get; set; }
    public string? Category { get; set; }
    public DosageForm DosageForm { get; set; } = DosageForm.Tablet;
    public string? Strength { get; set; }

    /// <summary>Pack label as printed, e.g. "15's", "100ml", "Pack of 10".</summary>
    public string? PackLabel { get; set; }

    /// <summary>Units in one pack (15 for a 15's strip). Basis for loose sale and per-pack rates. Always ≥ 1.</summary>
    public int UnitsPerPack { get; set; } = 1;

    /// <summary>False blocks loose (part-pack) sales: billing only accepts whole packs.</summary>
    public bool AllowLooseSale { get; set; } = true;

    public string? Barcode { get; set; }
    public string? Sku { get; set; }
    public string? HsnCode { get; set; }

    /// <summary>GST rate as a percentage (e.g. 5, 12, 18, 28).</summary>
    public decimal GstRatePercent { get; set; }

    public DrugSchedule Schedule { get; set; } = DrugSchedule.None;

    /// <summary>Re-order level in units: stock at or below this is "low stock".</summary>
    public int MinStock { get; set; } = 10;

    /// <summary>Upper stock limit in units used for re-order quantity suggestions (0 = no limit).</summary>
    public int MaxStock { get; set; }

    /// <summary>Highest line discount % billing may give on this item (null = no item-level cap).</summary>
    public decimal? MaxDiscountPercent { get; set; }

    /// <summary>Selling unit shown on bills (e.g. Strip, Bottle, Tube).</summary>
    public string? Unit { get; set; }

    /// <summary>Shelf/rack position in the shop (e.g. A-3).</summary>
    public string? RackLocation { get; set; }

    /// <summary>Default MRP per pack in paisa (GST-inclusive), used to pre-fill new batches.</summary>
    public long DefaultMrpPaisa { get; set; }

    /// <summary>Default selling rate per pack in paisa (GST-inclusive), used when a batch has no sale rate of its own.</summary>
    public long DefaultSellingPricePaisa { get; set; }

    public long? DefaultSupplierId { get; set; }
    public MedicineStatus Status { get; set; } = MedicineStatus.Active;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation (not persisted in DB directly)
    public List<MedicineBatch> Batches { get; set; } = new();
    public Supplier? DefaultSupplier { get; set; }

    /// <summary>Derived from <see cref="Status"/>; there is no separate IsActive column.</summary>
    public bool IsActive => Status == MedicineStatus.Active;

    /// <summary>
    /// Total units remaining across all non-expired batches.
    /// </summary>
    public int TotalStock => Batches
        .Where(b => b.ExpiryDate > DateTime.Today && b.QuantityRemaining > 0)
        .Sum(b => b.QuantityRemaining);

    /// <summary>Total stock as "packs + loose", e.g. "3 + 5".</summary>
    public string TotalStockDisplay => PackQuantity.Format(TotalStock, Math.Max(UnitsPerPack, 1));

    public decimal DefaultMrp => DefaultMrpPaisa / 100m;
    public decimal DefaultSellingPrice => DefaultSellingPricePaisa / 100m;
    public string ScheduleLabel => Schedule.ToLabel();

    public bool IsLowStock => TotalStock <= MinStock && TotalStock > 0;
    public bool IsOutOfStock => TotalStock == 0;
}
