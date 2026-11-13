using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>
/// Goods returned to a distributor, issued as a debit note (spec 04 "Return Purchase" / "Return Expired Products").
/// Stock leaves the shop when it is posted. Rates are per pack ex-GST; quantities are units; money is paisa.
/// </summary>
public sealed class PurchaseReturnDocument
{
    public long Id { get; set; }

    /// <summary>Debit note number from DocumentSequences (DocumentType.DebitNote), allocated when posted.</summary>
    public string? ReturnNumber { get; set; }

    public DateTime ReturnDate { get; set; } = DateTime.Today;
    public PurchaseReturnKind Kind { get; set; } = PurchaseReturnKind.Regular;

    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string? SupplierGstin { get; set; }

    /// <summary>Purchase the goods came from, when the return was made against one bill.</summary>
    public long? PurchaseId { get; set; }
    public string? PurchaseReference { get; set; }

    public PurchaseReturnReason Reason { get; set; } = PurchaseReturnReason.Other;
    public string? Notes { get; set; }
    public bool IsInterState { get; set; }

    // ── Computed (PurchaseCalculator.CalculateReturn) ──
    public long TaxablePaisa { get; set; }
    public long CgstPaisa { get; set; }
    public long SgstPaisa { get; set; }
    public long IgstPaisa { get; set; }
    public long TaxAmountPaisa { get; set; }
    public long RoundOffPaisa { get; set; }
    /// <summary>Debit note value: what the distributor owes the shop.</summary>
    public long NetAmountPaisa { get; set; }

    /// <summary>Cash / bank refund already received for this debit note (the rest reduces the payable).</summary>
    public long RefundReceivedPaisa { get; set; }

    public bool IsCancelled { get; set; }
    public long? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<PurchaseReturnLine> Lines { get; set; } = new();

    public long OpenCreditPaisa => IsCancelled ? 0 : NetAmountPaisa - RefundReceivedPaisa;
    public decimal NetAmount => NetAmountPaisa / 100m;
    public string ReasonLabel => Reason.ToLabel();
    public string KindLabel => Kind.ToLabel();
}

public sealed class PurchaseReturnLine
{
    public long Id { get; set; }
    public long PurchaseReturnId { get; set; }

    /// <summary>Purchase line the units came from (limits the quantity to what that bill delivered).</summary>
    public long? PurchaseItemId { get; set; }

    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public string? HsnCode { get; set; }
    public long BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public int UnitsPerPack { get; set; } = 1;

    /// <summary>Units returned.</summary>
    public int Quantity { get; set; }

    /// <summary>Return rate per pack, ex-GST (defaults to the purchase line's net rate).</summary>
    public long RatePaisa { get; set; }

    public decimal GstRatePercent { get; set; }

    // ── Computed ──
    public long TaxablePaisa { get; set; }
    public long TaxPaisa { get; set; }
    public long CgstPaisa { get; set; }
    public long SgstPaisa { get; set; }
    public long IgstPaisa { get; set; }
    public long LineTotalPaisa { get; set; }

    public string QuantityDisplay => PackQuantity.Format(Quantity, Math.Max(UnitsPerPack, 1));
    public string ExpiryLabel => ExpiryDate.ToString("MM/yy");
}

/// <summary>
/// A batch the shop can return to a distributor: stock on hand, the purchase it came from (when known) and the
/// most that can still go back.
/// </summary>
public sealed class ReturnableBatch
{
    public long BatchId { get; set; }
    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public string? HsnCode { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public int UnitsPerPack { get; set; } = 1;
    public int QuantityRemaining { get; set; }

    public long? SupplierId { get; set; }
    public string? SupplierName { get; set; }
    public long? PurchaseId { get; set; }
    public long? PurchaseItemId { get; set; }
    public string? PurchaseReference { get; set; }
    public DateTime? PurchaseDate { get; set; }

    /// <summary>Units delivered by <see cref="PurchaseItemId"/> (0 when not tied to a purchase line).</summary>
    public int PurchasedUnits { get; set; }

    /// <summary>Units of that purchase line already returned on earlier debit notes.</summary>
    public int ReturnedUnits { get; set; }

    /// <summary>Default return rate per pack ex-GST.</summary>
    public long RatePaisa { get; set; }

    public long MrpPaisa { get; set; }
    public decimal GstRatePercent { get; set; }

    /// <summary>Stock on hand, capped by what the purchase line delivered minus earlier returns.</summary>
    public int MaxReturnableUnits => PurchaseItemId.HasValue
        ? Math.Max(0, Math.Min(QuantityRemaining, PurchasedUnits - ReturnedUnits))
        : Math.Max(0, QuantityRemaining);

    public bool IsExpired => ExpiryDate.Date <= DateTime.Today;
    public int DaysToExpiry => (ExpiryDate.Date - DateTime.Today).Days;
    public string ExpiryLabel => ExpiryDate.ToString("MM/yy");
    public string StockDisplay => PackQuantity.Format(QuantityRemaining, Math.Max(UnitsPerPack, 1));
    public string MaxReturnableDisplay => PackQuantity.Format(MaxReturnableUnits, Math.Max(UnitsPerPack, 1));
    public decimal Rate => RatePaisa / 100m;
    public decimal Mrp => MrpPaisa / 100m;
}
