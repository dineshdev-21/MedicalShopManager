using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IPaymentRepository : IRepository<Payment>
{
    Task<IReadOnlyList<Payment>> GetByTransactionAsync(long transactionId, PaymentType type);
    Task<IReadOnlyList<Payment>> GetByDateRangeAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<Payment>> GetByMethodAsync(PaymentMethod method);
}
