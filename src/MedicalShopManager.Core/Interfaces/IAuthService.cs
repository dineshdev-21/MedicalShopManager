using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Authentication and authorization service.
/// </summary>
public interface IAuthService
{
    Task<User?> AuthenticateAsync(string username, string password);
    Task<User> CreateUserAsync(string username, string displayName, string password, UserRole role);
    Task ChangePasswordAsync(long userId, string currentPassword, string newPassword);
    Task<bool> HasPermissionAsync(long userId, string permission);
    User? CurrentUser { get; }
    void SetCurrentUser(User? user);
    event EventHandler<User?>? CurrentUserChanged;
}
