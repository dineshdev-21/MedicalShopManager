using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IUserRepository : IRepository<User>
{
    Task<User?> GetByUsernameAsync(string username);
    Task<IReadOnlyList<User>> GetByRoleAsync(UserRole role);
    Task<bool> UpdatePasswordAsync(long userId, string newPasswordHash);
    Task<bool> UpdateLastLoginAsync(long userId);
}
