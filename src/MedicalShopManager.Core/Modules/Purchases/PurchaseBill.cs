using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>
/// A distributor purchase bill (spec 04 "Add Purchase"), stored in the Purchases / PurchaseItems tables.
/// Rates are per pack; quantities are units; money is paisa. Computed fields are filled by
/// <see cref="PurchaseCalculator"/> and are never trusted from the caller.
/// </summary>
public sealed class PurchaseBill
{
    public long Id { get; set; }

    /// <summary>Internal purchase number from DocumentSequences (DocumentType.Purchase), allocated on confirm.</summary>
    public string? PurchaseNumber { get; set; }

    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string? SupplierGstin { get; set; }

    /// <summary>Distributor's bill number (INVOICE NO).</summary>
    public string SupplierInvoiceNumber { get; set; } = string.Empty;

    /// <summary>Distributor's bill date (INVOICE DATE). Stored in Purchases.PurchaseDate.</summary>
    public DateTime InvoiceDate { get; set; } = DateTime.Today;

    /// <summary>PAYMENT DUE DATE; defaults to invoice date + the distributor's credit days.</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>BILL HAS GST COLUMN? When false GST is not added to the bill total (spec 04).</summary>
    public bool WithGst { get; set; } = true;

    /// <summary>Entered rates already include GST; the calculator backs GST out to get the ex-GST rate.</summary>
    public bool RatesIncludeGst { get; set; }

    /// <summary>IGST instead of CGST + SGST (distributor in another state).</summary>
    public bool IsInterState { get; set; }

    /// <summary>OVERALL BILL DISCOUNT %, applied proportionally to every line's taxable value.</summary>
    public decimal BillDiscountPercent { get; set; }

    /// <summary>Delivery / custom charge added after tax (not taxable).</summary>
    public long OtherChargesPaisa { get; set; }

    public string? OtherChargesLabel { get; set; }

    // ── Computed totals (PurchaseCalculator) ──
    /// <summary>Σ rate × qty before any discount.</summary>
    public long SubtotalPaisa { get; set; }
    /// <summary>Item + scheme + bill discounts (Subtotal − Taxable).</summary>
    public long DiscountAmountPaisa { get; set; }
    public long TaxablePaisa { get; set; }
    public long CgstPaisa { get; set; }
    public long SgstPaisa { get; set; }
    public long IgstPaisa { get; set; }
    public long TaxAmountPaisa { get; set; }
    public long RoundOffPaisa { get; set; }
    /// <summary>Invoice amount, rounded to the rupee (what the shop owes for this bill).</summary>
    public long GrandTotalPaisa { get; set; }

    // ── Payment ──
    public long AmountPaidPaisa { get; set; }
    public long BalancePaisa { get; set; }
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Unpaid;

    public PurchaseStatus Status { get; set; } = PurchaseStatus.Draft;

    /// <summary>"CSV" / "Excel" when the lines came from a supplier file; null for manual entry.</summary>
    public string? ImportSource { get; set; }

    public string? Notes { get; set; }
    public long? CreatedByUserId { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<PurchaseBillLine> Lines { get; set; } = new();

    public bool IsDraft => Status == PurchaseStatus.Draft;
    public bool IsConfirmed => Status == PurchaseStatus.Confirmed;
    public bool IsOverdue => IsConfirmed && BalancePaisa > 0 && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;
    public long TotalUnits => Lines.Sum(l => l.Quantity + l.FreeQuantity);
}

/// <summary>One purchase line. Quantities are units (packs × UnitsPerPack); rates are per pack.</summary>
public sealed class PurchaseBillLine
{
    public long Id { get; set; }
    public long PurchaseId { get; set; }
    public int LineNo { get; set; }

    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public string? HsnCode { get; set; }

    public string BatchNumber { get; set; } = string.Empty;
    /// <summary>Last day of the expiry month (MedicineBatch.ExpiryFromMonthYear).</summary>
    public DateTime ExpiryDate { get; set; }
    public DateTime? ManufacturingDate { get; set; }

    /// <summary>PACK: units per pack, the basis of every rate on this line and on the batch it creates.</summary>
    public int UnitsPerPack { get; set; } = 1;

    /// <summary>Paid quantity in units.</summary>
    public int Quantity { get; set; }

    /// <summary>Free quantity in units.</summary>
    public int FreeQuantity { get; set; }

    /// <summary>RATE as entered per pack (GST-inclusive when the bill's RatesIncludeGst is set).</summary>
    public long EnteredRatePaisa { get; set; }

    /// <summary>Purchase rate per pack excluding GST (computed from <see cref="EnteredRatePaisa"/>).</summary>
    public long PurchaseRatePaisa { get; set; }

    /// <summary>Price to retailer per pack (ex-GST); defaults to the purchase rate.</summary>
    public long PtrPaisa { get; set; }

    public long MrpPaisa { get; set; }

    /// <summary>Selling rate per pack, GST-inclusive, ≤ MRP. Defaults from MRP and the sale-rate setting.</summary>
    public long SaleRatePaisa { get; set; }

    public decimal DiscountPercent { get; set; }
    public decimal SchemePercent { get; set; }

    /// <summary>"X + Y" scheme as printed on the bill (informational; free units are in <see cref="FreeQuantity"/>).</summary>
    public int SchemeQuantity { get; set; }
    public int SchemeFreeQuantity { get; set; }

    public decimal GstRatePercent { get; set; }

    // ── Computed (PurchaseCalculator) ──
    /// <summary>rate × qty before discounts.</summary>
    public long ValueBeforeDiscountPaisa { get; set; }
    public long DiscountAmountPaisa { get; set; }
    public long TaxablePaisa { get; set; }
    public long GstAmountPaisa { get; set; }
    public long CgstPaisa { get; set; }
    public long SgstPaisa { get; set; }
    public long IgstPaisa { get; set; }
    public long LineTotalPaisa { get; set; }
    /// <summary>Rate per pack after item, scheme and bill discounts, ex-GST (NET RATE).</summary>
    public long NetRatePaisa { get; set; }
    /// <summary>Landed cost per pack ex-GST spread over paid + free units (batch_net_purchase_rate).</summary>
    public long NetPurchaseRatePaisa { get; set; }
    /// <summary>Margin % on the sale rate ex-GST against the net purchase rate.</summary>
    public decimal MarginPercent { get; set; }

    /// <summary>Batch created or merged into on confirm.</summary>
    public long? BatchId { get; set; }

    public string QuantityDisplay => PackQuantity.Format(Quantity, Math.Max(UnitsPerPack, 1));
    public string FreeQuantityDisplay => PackQuantity.Format(FreeQuantity, Math.Max(UnitsPerPack, 1));
    public string ExpiryLabel => ExpiryDate.ToString("MM/yy");
}

/// <summary>A payment made to the distributor against a purchase.</summary>
public sealed record PurchasePaymentInput(long AmountPaisa, PaymentMethod Method, string? Reference, DateTime Date);

/// <summary>A payment row linked to a purchase (Payments table, PaymentType SupplierPayment).</summary>
public sealed record PurchasePaymentRecord(long Id, long PurchaseId, DateTime Date, long AmountPaisa, PaymentMethod Method, string? Reference, string? Notes)
{
    public decimal Amount => AmountPaisa / 100m;
}

/// <summary>Allocation of a lump-sum supplier payment to one bill (oldest due first).</summary>
public sealed record PaymentAllocation(long PurchaseId, string? PurchaseNumber, string SupplierInvoiceNumber, long AmountPaisa);
