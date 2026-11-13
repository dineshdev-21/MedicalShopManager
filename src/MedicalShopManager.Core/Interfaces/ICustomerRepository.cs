using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface ICustomerRepository : IRepository<Customer>
{
    Task<IReadOnlyList<Customer>> SearchAsync(string query);
    Task<Customer?> GetByPhoneAsync(string phone);
}
