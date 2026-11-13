using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Models;

/// <summary>
/// Represents a purchase from a supplier.
/// </summary>
public class Purchase
{
    public long Id { get; set; }
    
    /// <summary>Supplier's invoice number.</summary>
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    
    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public DateTime PurchaseDate { get; set; } = DateTime.Today;
    
    public long SubtotalPaisa { get; set; }
    public long DiscountAmountPaisa { get; set; }
    public long TaxAmountPaisa { get; set; }
    public long GrandTotalPaisa { get; set; }
    public long AmountPaidPaisa { get; set; }
    public long BalancePaisa { get; set; }
    
    public PurchaseStatus Status { get; set; } = PurchaseStatus.Draft;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Unpaid;
    public string? Notes { get; set; }
    public long? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Supplier? Supplier { get; set; }
    public List<PurchaseItem> Items { get; set; } = new();

    // Display
    public decimal Subtotal => SubtotalPaisa / 100m;
    public decimal DiscountAmount => DiscountAmountPaisa / 100m;
    public decimal TaxAmount => TaxAmountPaisa / 100m;
    public decimal GrandTotal => GrandTotalPaisa / 100m;
    public decimal AmountPaid => AmountPaidPaisa / 100m;
    public decimal Balance => BalancePaisa / 100m;
}

/// <summary>
/// A single line item in a purchase.
/// </summary>
public class PurchaseItem
{
    public long Id { get; set; }
    public long PurchaseId { get; set; }
    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public DateTime? ManufacturingDate { get; set; }
    
    public int Quantity { get; set; }
    public int FreeQuantity { get; set; }
    
    /// <summary>Purchase price per unit in paisa.</summary>
    public long PurchasePricePaisa { get; set; }
    
    /// <summary>MRP per unit in paisa.</summary>
    public long MrpPaisa { get; set; }
    
    /// <summary>Selling price per unit in paisa.</summary>
    public long SellingPricePaisa { get; set; }
    
    public decimal GstRatePercent { get; set; }
    public long GstAmountPaisa { get; set; }
    public decimal DiscountPercent { get; set; }
    public long DiscountAmountPaisa { get; set; }
    public long LineTotalPaisa { get; set; }

    // Display
    public decimal PurchasePrice => PurchasePricePaisa / 100m;
    public decimal Mrp => MrpPaisa / 100m;
    public decimal SellingPrice => SellingPricePaisa / 100m;
    public decimal LineTotal => LineTotalPaisa / 100m;
}
