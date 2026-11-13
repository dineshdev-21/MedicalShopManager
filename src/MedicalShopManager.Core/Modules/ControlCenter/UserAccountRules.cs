using System.Text.RegularExpressions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>Input for creating a user from Control Center ▸ Users &amp; Permissions.</summary>
public sealed record NewUserRequest(string Username, string DisplayName, string Password, UserRole Role);

/// <summary>Changes to an existing user (username is immutable: it is the login and appears in the audit log).</summary>
public sealed record UserUpdateRequest(long UserId, string DisplayName, UserRole Role, bool IsActive);

/// <summary>Validation for user accounts and the guards that keep the shop administrable (pure).</summary>
public static partial class UserAccountRules
{
    public const int MinPasswordLength = 6;
    public const int MaxDisplayNameLength = 60;

    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$")]
    private static partial Regex UsernamePattern();

    public static IReadOnlyList<SettingsError> ValidateNewUser(NewUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<SettingsError>();
        if (!UsernamePattern().IsMatch(request.Username?.Trim() ?? string.Empty))
            errors.Add(new(nameof(request.Username), "Username must be 3–32 characters: letters, digits, '.', '_' or '-'."));
        errors.AddRange(ValidateDisplayName(request.DisplayName));
        if (PasswordError(request.Password, request.Username) is { } passwordError)
            errors.Add(new(nameof(request.Password), passwordError));
        if (!Enum.IsDefined(request.Role))
            errors.Add(new(nameof(request.Role), "Select a role."));
        return errors;
    }

    public static IReadOnlyList<SettingsError> ValidateDisplayName(string? displayName)
    {
        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length == 0) return new[] { new SettingsError("DisplayName", "Display name is required.") };
        return name.Length > MaxDisplayNameLength
            ? new[] { new SettingsError("DisplayName", $"Display name must be at most {MaxDisplayNameLength} characters.") }
            : Array.Empty<SettingsError>();
    }

    /// <summary>Null when acceptable: at least 6 characters, not all spaces, not the username.</summary>
    public static string? PasswordError(string? password, string? username = null)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinPasswordLength)
            return $"Password must be at least {MinPasswordLength} characters.";
        if (!string.IsNullOrWhiteSpace(username) && string.Equals(password.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase))
            return "Password cannot be the same as the username.";
        return null;
    }

    /// <summary>
    /// Guards on editing a user: at least one active administrator must remain, and a signed-in user cannot
    /// deactivate or demote themselves (they would lock themselves out mid-session). Null = allowed.
    /// </summary>
    public static string? UpdateGuard(IReadOnlyList<User> users, UserUpdateRequest request, long? currentUserId)
    {
        ArgumentNullException.ThrowIfNull(users);
        var target = users.FirstOrDefault(u => u.Id == request.UserId);
        if (target == null) return "The user no longer exists.";

        if (currentUserId == request.UserId)
        {
            if (!request.IsActive) return "You cannot deactivate your own account.";
            if (target.Role == UserRole.Administrator && request.Role != UserRole.Administrator)
                return "You cannot remove your own administrator role.";
        }

        var losesAdmin = target.IsActive && target.Role == UserRole.Administrator
                         && (!request.IsActive || request.Role != UserRole.Administrator);
        if (losesAdmin && users.Count(u => u.IsActive && u.Role == UserRole.Administrator) <= 1)
            return "At least one active administrator is required. Make another user an administrator first.";
        return null;
    }
}
