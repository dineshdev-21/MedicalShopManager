using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IReturnRepository : IRepository<Return>
{
    Task<IReadOnlyList<Return>> GetByTypeAsync(ReturnType type);
    Task<IReadOnlyList<Return>> GetByDateRangeAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<Return>> GetByOriginalTransactionAsync(long transactionId);
}
