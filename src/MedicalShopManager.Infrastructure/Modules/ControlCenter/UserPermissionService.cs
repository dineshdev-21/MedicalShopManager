using System.Text.Json;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.Infrastructure.Modules.ControlCenter;

/// <summary><see cref="IUserPermissionService"/>: role defaults (<see cref="PermissionRules"/>) + stored per-user overrides.</summary>
public sealed class UserPermissionService : IUserPermissionService
{
    private readonly IUserRepository _users;
    private readonly UserPermissionRepository _repository;
    private readonly IAuthService _auth;
    private readonly IAuditService _audit;

    public UserPermissionService(IUserRepository users, UserPermissionRepository repository, IAuthService auth, IAuditService audit)
    {
        _users = users;
        _repository = repository;
        _auth = auth;
        _audit = audit;
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetEffectiveAsync(long userId)
    {
        var user = await _users.GetByIdAsync(userId);
        if (user == null || !user.IsActive)
            return PermissionCatalog.All.ToDictionary(p => p.Key, _ => false);
        return PermissionRules.Effective(user.Role, await _repository.GetOverridesAsync(userId));
    }

    public async Task<bool> IsPermittedAsync(long userId, string permissionKey)
    {
        if (PermissionCatalog.Find(permissionKey) == null)
            throw new ArgumentException($"Unknown permission '{permissionKey}'.", nameof(permissionKey));
        var effective = await GetEffectiveAsync(userId);
        return effective.TryGetValue(permissionKey, out var permitted) && permitted;
    }

    public Task<bool> IsCurrentUserPermittedAsync(string permissionKey) =>
        _auth.CurrentUser is { } user ? IsPermittedAsync(user.Id, permissionKey) : Task.FromResult(false);

    public async Task SavePermissionsAsync(long userId, IReadOnlyDictionary<string, bool> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var user = await _users.GetByIdAsync(userId)
            ?? throw new ControlCenterValidationException("UserId", "The user no longer exists.");
        if (PermissionRules.IsFullAccess(user.Role))
            throw new ControlCenterValidationException("Role", "Administrators always have every permission; change the role to restrict this user.");

        var unknown = desired.Keys.Where(k => PermissionCatalog.Find(k) == null).ToList();
        if (unknown.Count > 0)
            throw new ControlCenterValidationException(unknown.Select(k => new SettingsError(k, $"Unknown permission '{k}'.")).ToList());

        var before = await _repository.GetOverridesAsync(userId);
        var overrides = PermissionRules.Diff(user.Role, desired);
        await _repository.ReplaceAsync(userId, overrides);
        await _audit.LogAsync("ControlCenter.PermissionsUpdated", "User", userId,
            JsonSerializer.Serialize(before), JsonSerializer.Serialize(overrides), $"Permissions of {user.Username}");
    }

    public async Task ResetToRoleDefaultsAsync(long userId)
    {
        var before = await _repository.GetOverridesAsync(userId);
        await _repository.ReplaceAsync(userId, new Dictionary<string, bool>());
        await _audit.LogAsync("ControlCenter.PermissionsReset", "User", userId, JsonSerializer.Serialize(before), "{}");
    }
}
