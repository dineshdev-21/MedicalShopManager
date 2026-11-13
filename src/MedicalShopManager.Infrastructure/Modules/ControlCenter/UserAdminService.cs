using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.Infrastructure.Modules.ControlCenter;

/// <summary>
/// User accounts for Control Center. Creation goes through <see cref="IAuthService.CreateUserAsync"/> (BCrypt);
/// edits and password resets go through <see cref="IUserRepository"/>. Every change is audited (never the password).
/// </summary>
public sealed class UserAdminService : IUserAdminService
{
    private readonly IUserRepository _users;
    private readonly IAuthService _auth;
    private readonly IAuditService _audit;
    private readonly ILogger<UserAdminService> _logger;

    public UserAdminService(IUserRepository users, IAuthService auth, IAuditService audit, ILogger<UserAdminService> logger)
    {
        _users = users;
        _auth = auth;
        _audit = audit;
        _logger = logger;
    }

    public Task<IReadOnlyList<User>> GetUsersAsync() => _users.GetAllAsync();

    public async Task<User> CreateUserAsync(NewUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = UserAccountRules.ValidateNewUser(request);
        if (errors.Count > 0) throw new ControlCenterValidationException(errors);

        var username = request.Username.Trim();
        if (await _users.GetByUsernameAsync(username) != null)
            throw new ControlCenterValidationException(nameof(request.Username), $"The username '{username}' is already taken.");

        var user = await _auth.CreateUserAsync(username, request.DisplayName.Trim(), request.Password, request.Role);
        await _audit.LogAsync("ControlCenter.UserCreated", "User", user.Id, null,
            JsonSerializer.Serialize(new { user.Username, user.DisplayName, Role = user.Role.ToString() }));
        _logger.LogInformation("User {Username} created from Control Center", username);
        return user;
    }

    public async Task UpdateUserAsync(UserUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nameErrors = UserAccountRules.ValidateDisplayName(request.DisplayName);
        if (nameErrors.Count > 0) throw new ControlCenterValidationException(nameErrors);
        if (!Enum.IsDefined(request.Role)) throw new ControlCenterValidationException("Role", "Select a role.");

        var all = await _users.GetAllAsync();
        if (UserAccountRules.UpdateGuard(all, request, _auth.CurrentUser?.Id) is { } guard)
            throw new ControlCenterValidationException("User", guard);

        var user = all.First(u => u.Id == request.UserId);
        var before = JsonSerializer.Serialize(new { user.DisplayName, Role = user.Role.ToString(), user.IsActive });
        user.DisplayName = request.DisplayName.Trim();
        user.Role = request.Role;
        user.IsActive = request.IsActive;
        if (!await _users.UpdateAsync(user))
            throw new ControlCenterValidationException("User", "The user could not be updated; it may have been removed.");

        await _audit.LogAsync("ControlCenter.UserUpdated", "User", user.Id, before,
            JsonSerializer.Serialize(new { user.DisplayName, Role = user.Role.ToString(), user.IsActive }));
    }

    public async Task ResetPasswordAsync(long userId, string newPassword)
    {
        var user = await _users.GetByIdAsync(userId)
            ?? throw new ControlCenterValidationException("User", "The user no longer exists.");
        if (UserAccountRules.PasswordError(newPassword, user.Username) is { } error)
            throw new ControlCenterValidationException("Password", error);

        var hash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        if (!await _users.UpdatePasswordAsync(userId, hash))
            throw new ControlCenterValidationException("User", "The password could not be changed.");
        await _audit.LogAsync("ControlCenter.PasswordReset", "User", userId, null, null, $"Password reset for {user.Username}");
    }
}
