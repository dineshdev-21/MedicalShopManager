namespace MedicalShopManager.Core.Models;

/// <summary>
/// Represents a customer with optional tracking for purchase history.
/// </summary>
public class Customer
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }

    /// <summary>Preferred/regular prescribing doctor.</summary>
    public string? DoctorName { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Unpaid credit-sale dues in paisa. Computed by the repository from Sales.BalanceDuePaisa; never written.</summary>
    public long OutstandingBalancePaisa { get; set; }

    // Populated by service
    public decimal TotalPurchases { get; set; }
    public int TotalInvoices { get; set; }

    public decimal OutstandingBalance => OutstandingBalancePaisa / 100m;
}
