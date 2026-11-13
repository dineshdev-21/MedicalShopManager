using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Models;

/// <summary>
/// Records a payment transaction.
/// </summary>
public class Payment
{
    public long Id { get; set; }
    
    /// <summary>Reference to sale, purchase, or other transaction.</summary>
    public long? TransactionId { get; set; }
    
    /// <summary>Human-readable reference (invoice number, purchase ref, etc.).</summary>
    public string? TransactionReference { get; set; }
    
    public DateTime PaymentDate { get; set; } = DateTime.Now;
    public PaymentType PaymentType { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    
    /// <summary>Amount in paisa.</summary>
    public long AmountPaisa { get; set; }
    
    /// <summary>External reference number (UPI ref, card auth code, etc.).</summary>
    public string? ReferenceNumber { get; set; }
    
    public string? Notes { get; set; }
    public long? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Display
    public decimal Amount => AmountPaisa / 100m;
}
