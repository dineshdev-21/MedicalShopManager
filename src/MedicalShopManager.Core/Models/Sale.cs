using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Models;

/// <summary>
/// Represents a completed or pending sale (invoice).
/// </summary>
public class Sale
{
    public long Id { get; set; }
    
    /// <summary>Unique human-readable invoice number (e.g., "INV-2024-0001").</summary>
    public string InvoiceNumber { get; set; } = string.Empty;
    
    public DateTime InvoiceDate { get; set; } = DateTime.Now;
    public long? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    
    /// <summary>Subtotal before discount and tax, in paisa.</summary>
    public long SubtotalPaisa { get; set; }
    
    /// <summary>Total discount amount in paisa.</summary>
    public long DiscountAmountPaisa { get; set; }
    
    /// <summary>Taxable amount after discount, in paisa.</summary>
    public long TaxableAmountPaisa { get; set; }
    
    /// <summary>CGST amount in paisa.</summary>
    public long CgstAmountPaisa { get; set; }
    
    /// <summary>SGST amount in paisa.</summary>
    public long SgstAmountPaisa { get; set; }
    
    /// <summary>IGST amount in paisa (for inter-state, usually 0 for local shop).</summary>
    public long IgstAmountPaisa { get; set; }
    
    /// <summary>Grand total in paisa (taxable + all taxes).</summary>
    public long GrandTotalPaisa { get; set; }
    
    /// <summary>Amount paid by customer in paisa.</summary>
    public long AmountPaidPaisa { get; set; }
    
    /// <summary>Balance due from customer in paisa.</summary>
    public long BalanceDuePaisa { get; set; }
    
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Paid;
    
    /// <summary>Cash received from customer (for change calculation), in paisa.</summary>
    public long CashReceivedPaisa { get; set; }
    
    /// <summary>Change returned to customer, in paisa.</summary>
    public long ChangeReturnedPaisa { get; set; }
    
    public string? Notes { get; set; }
    public long? CreatedByUserId { get; set; }
    
    /// <summary>Prescription reference if applicable.</summary>
    public string? PrescriptionRef { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Customer? Customer { get; set; }
    public List<SaleItem> Items { get; set; } = new();
    public List<Payment> Payments { get; set; } = new();

    // Display properties (rupees)
    public decimal Subtotal => SubtotalPaisa / 100m;
    public decimal DiscountAmount => DiscountAmountPaisa / 100m;
    public decimal TaxableAmount => TaxableAmountPaisa / 100m;
    public decimal CgstAmount => CgstAmountPaisa / 100m;
    public decimal SgstAmount => SgstAmountPaisa / 100m;
    public decimal IgstAmount => IgstAmountPaisa / 100m;
    public decimal GrandTotal => GrandTotalPaisa / 100m;
    public decimal AmountPaid => AmountPaidPaisa / 100m;
    public decimal BalanceDue => BalanceDuePaisa / 100m;
    public decimal CashReceived => CashReceivedPaisa / 100m;
    public decimal ChangeReturned => ChangeReturnedPaisa / 100m;
}

/// <summary>
/// A single line item in a sale invoice.
/// </summary>
public class SaleItem
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public long BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public string? HsnCode { get; set; }
    
    public int Quantity { get; set; }
    
    /// <summary>Unit selling price in paisa.</summary>
    public long UnitPricePaisa { get; set; }
    
    /// <summary>Discount percentage on this line item.</summary>
    public decimal DiscountPercent { get; set; }
    
    /// <summary>Discount amount for this line in paisa.</summary>
    public long DiscountAmountPaisa { get; set; }
    
    /// <summary>Taxable value after discount, in paisa.</summary>
    public long TaxableValuePaisa { get; set; }
    
    /// <summary>GST rate percent (e.g. 12).</summary>
    public decimal GstRatePercent { get; set; }
    
    /// <summary>CGST amount for this line in paisa.</summary>
    public long CgstAmountPaisa { get; set; }
    
    /// <summary>SGST amount for this line in paisa.</summary>
    public long SgstAmountPaisa { get; set; }
    
    /// <summary>IGST amount for this line in paisa.</summary>
    public long IgstAmountPaisa { get; set; }
    
    /// <summary>Total tax amount for this line in paisa.</summary>
    public long TaxAmountPaisa { get; set; }
    
    /// <summary>Cost of goods sold for this line (purchase cost × qty) in paisa.</summary>
    public long CogsPaisa { get; set; }
    
    /// <summary>Line total including tax, in paisa.</summary>
    public long LineTotalPaisa { get; set; }

    // Display
    public decimal UnitPrice => UnitPricePaisa / 100m;
    public decimal DiscountAmount => DiscountAmountPaisa / 100m;
    public decimal TaxableValue => TaxableValuePaisa / 100m;
    public decimal TaxAmount => TaxAmountPaisa / 100m;
    public decimal Cogs => CogsPaisa / 100m;
    public decimal LineTotal => LineTotalPaisa / 100m;
}
