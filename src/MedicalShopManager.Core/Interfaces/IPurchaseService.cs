using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Purchase management service.
/// </summary>
public interface IPurchaseService
{
    Task<long> CreateDraftAsync(Purchase purchase);
    Task<Purchase> ConfirmPurchaseAsync(long purchaseId, long userId);
    Task<Purchase?> GetPurchaseAsync(long id);
    Task<IReadOnlyList<Purchase>> GetPurchasesBySupplierAsync(long supplierId);
    Task<IReadOnlyList<Purchase>> GetPurchasesByDateAsync(DateTime from, DateTime to);
    Task RecordPaymentAsync(long purchaseId, long amountPaisa, PaymentMethod method, string? reference, long userId);
}
