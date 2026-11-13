using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Models;

/// <summary>
/// Records a return (sales return, purchase return, or write-off).
/// </summary>
public class Return
{
    public long Id { get; set; }
    
    /// <summary>Original sale ID or purchase ID this return references.</summary>
    public long? OriginalTransactionId { get; set; }
    
    /// <summary>Human-readable reference to original invoice/purchase.</summary>
    public string? OriginalReference { get; set; }
    
    public ReturnType ReturnType { get; set; }
    public DateTime ReturnDate { get; set; } = DateTime.Now;
    
    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public long? BatchId { get; set; }
    public string? BatchNumber { get; set; }
    
    public int Quantity { get; set; }
    
    /// <summary>Refund or credit amount in paisa.</summary>
    public long RefundAmountPaisa { get; set; }
    
    public string? Reason { get; set; }
    
    /// <summary>Whether stock has been assessed as safe and returned to sellable inventory.</summary>
    public bool ReturnedToStock { get; set; }
    
    public long? ProcessedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public decimal RefundAmount => RefundAmountPaisa / 100m;
}
