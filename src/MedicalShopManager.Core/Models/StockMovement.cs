using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Models;

/// <summary>
/// Records every stock quantity change for full traceability.
/// </summary>
public class StockMovement
{
    private string? _reference;

    public long Id { get; set; }
    public DateTime MovementDate { get; set; } = DateTime.Now;
    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public long BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public MovementType MovementType { get; set; }
    
    /// <summary>Quantity added to stock (0 for outgoing movements).</summary>
    public int QuantityIn { get; set; }
    
    /// <summary>Quantity removed from stock (0 for incoming movements).</summary>
    public int QuantityOut { get; set; }

    /// <summary>Signed net quantity (+ for in, - for out). Setting sets QuantityIn/QuantityOut accordingly.</summary>
    public int Quantity
    {
        get => QuantityIn > 0 ? QuantityIn : -QuantityOut;
        set
        {
            if (value >= 0)
            {
                QuantityIn = value;
                QuantityOut = 0;
            }
            else
            {
                QuantityIn = 0;
                QuantityOut = -value;
            }
        }
    }
    
    /// <summary>Balance after this movement.</summary>
    public int BalanceAfter { get; set; }
    
    /// <summary>Reference to the transaction that caused this movement (sale ID, purchase ID, etc.).</summary>
    public string? Reference
    {
        get => _reference ?? (ReferenceType != null && ReferenceId != null ? $"{ReferenceType}:{ReferenceId}" : ReferenceType);
        set => _reference = value;
    }

    public string? ReferenceType { get; set; }
    public long? ReferenceId { get; set; }
    
    public string? Reason { get; set; }

    /// <summary>Alias for Reason/audit notes.</summary>
    public string? Notes
    {
        get => Reason;
        set => Reason = value;
    }

    public long? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
