using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Models;

/// <summary>
/// Records an operating expense.
/// </summary>
public class Expense
{
    public long Id { get; set; }
    public DateTime ExpenseDate { get; set; } = DateTime.Today;
    public ExpenseCategory Category { get; set; }
    public string? Description { get; set; }
    
    /// <summary>Amount in paisa.</summary>
    public long AmountPaisa { get; set; }
    
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
    public string? ReferenceNumber { get; set; }
    public string? Notes { get; set; }
    public long? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public decimal Amount => AmountPaisa / 100m;
}
