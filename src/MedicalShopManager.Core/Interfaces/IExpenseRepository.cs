using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IExpenseRepository : IRepository<Expense>
{
    Task<IReadOnlyList<Expense>> GetByDateRangeAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<Expense>> GetByCategoryAsync(ExpenseCategory category);
    Task<long> GetTotalByDateRangeAsync(DateTime from, DateTime to);
}
