using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Point-of-sale billing service.
/// </summary>
public interface IBillingService
{
    /// <summary>
    /// Process a complete sale in a single transaction.
    /// Returns the created Sale with invoice number.
    /// </summary>
    Task<Sale> ProcessSaleAsync(Sale sale, long userId);
    
    Task<Sale?> GetSaleAsync(long id);
    Task<Sale?> GetSaleByInvoiceAsync(string invoiceNumber);
    Task<IReadOnlyList<Sale>> GetSalesByDateAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<Sale>> SearchSalesAsync(string query);
    Task<string> GenerateNextInvoiceNumberAsync();
    
    /// <summary>
    /// Calculate GST for a line item.
    /// </summary>
    (long cgst, long sgst, long igst) CalculateGst(long taxableAmountPaisa, decimal gstRatePercent, bool isInterState = false);
}
