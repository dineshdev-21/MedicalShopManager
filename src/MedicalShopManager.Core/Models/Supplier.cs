namespace MedicalShopManager.Core.Models;

/// <summary>
/// Represents a medicine supplier.
/// </summary>
public class Supplier
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Gstin { get; set; }

    /// <summary>Wholesale drug licence number (Form 20B/21B).</summary>
    public string? DrugLicenseNumber { get; set; }

    public bool IsActive { get; set; } = true;
    
    /// <summary>Opening balance in paisa (amount owed to supplier at start).</summary>
    public long OpeningBalancePaisa { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Amount owed to the supplier in paisa: opening balance plus unpaid balances of confirmed purchases.
    /// Computed by the repository; never written.
    /// </summary>
    public long OutstandingBalancePaisa { get; set; }

    // Display
    public decimal OpeningBalance => OpeningBalancePaisa / 100m;
    public decimal OutstandingBalance => OutstandingBalancePaisa / 100m;
}
