using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface ISupplierRepository : IRepository<Supplier>
{
    Task<IReadOnlyList<Supplier>> SearchAsync(string query);
    Task<IReadOnlyList<Supplier>> GetActiveAsync();
    Task<Supplier?> GetByGstinAsync(string gstin);

    /// <summary>Total owed to all active suppliers, in paisa.</summary>
    Task<long> GetTotalOutstandingBalanceAsync();
}
