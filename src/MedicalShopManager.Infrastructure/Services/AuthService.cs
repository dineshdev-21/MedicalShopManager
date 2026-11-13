using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Services;

/// <summary>
/// Authentication and authorization service using BCrypt hashing and role-based permissions.
/// </summary>
public class AuthService : IAuthService
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger<AuthService> _logger;
    private User? _currentUser;

    public User? CurrentUser => _currentUser;
    public event EventHandler<User?>? CurrentUserChanged;

    public AuthService(IUserRepository userRepository, ILogger<AuthService> logger)
    {
        _userRepository = userRepository;
        _logger = logger;
    }

    public void SetCurrentUser(User? user)
    {
        _currentUser = user;
        CurrentUserChanged?.Invoke(this, user);
    }

    public async Task<User?> AuthenticateAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        var user = await _userRepository.GetByUsernameAsync(username.Trim());
        if (user == null || !user.IsActive)
        {
            _logger.LogWarning("Authentication failed for username: {Username}", username);
            return null;
        }

        bool verified;
        try
        {
            verified = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying password hash for user {Username}", username);
            return null;
        }

        if (!verified)
        {
            _logger.LogWarning("Invalid password attempt for username: {Username}", username);
            return null;
        }

        await _userRepository.UpdateLastLoginAsync(user.Id);
        user.LastLoginAt = DateTime.UtcNow;
        SetCurrentUser(user);
        _logger.LogInformation("User {Username} authenticated successfully with role {Role}", username, user.Role);
        return user;
    }

    public async Task<User> CreateUserAsync(string username, string displayName, string password, UserRole role)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be empty", nameof(username));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be empty", nameof(password));

        var existing = await _userRepository.GetByUsernameAsync(username.Trim());
        if (existing != null)
            throw new InvalidOperationException($"User with username '{username}' already exists");

        var hash = BCrypt.Net.BCrypt.HashPassword(password);
        var user = new User
        {
            Username = username.Trim(),
            DisplayName = displayName.Trim(),
            PasswordHash = hash,
            Role = role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var id = await _userRepository.InsertAsync(user);
        user.Id = id;
        _logger.LogInformation("Created new user {Username} with role {Role}", username, role);
        return user;
    }

    public async Task ChangePasswordAsync(long userId, string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
            throw new ArgumentException("New password must be at least 4 characters long", nameof(newPassword));

        var user = await _userRepository.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("Current password is incorrect");

        var newHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.PasswordHash = newHash;
        user.UpdatedAt = DateTime.UtcNow;

        await _userRepository.UpdateAsync(user);
        _logger.LogInformation("Password changed successfully for user ID {UserId}", userId);
    }

    public Task<bool> HasPermissionAsync(long userId, string permission)
    {
        if (_currentUser == null || _currentUser.Id != userId)
            return Task.FromResult(false);

        // Administrator has all permissions
        if (_currentUser.Role == UserRole.Administrator)
            return Task.FromResult(true);

        return Task.FromResult(_currentUser.Role switch
        {
            UserRole.Pharmacist => permission switch
            {
                "DispensePrescription" => true,
                "ViewMedicines" => true,
                "EditMedicines" => true,
                "ManageBatches" => true,
                "NewBill" => true,
                "ViewSales" => true,
                "ViewReports" => true,
                _ => false
            },
            UserRole.BillingOperator => permission switch
            {
                "NewBill" => true,
                "ViewSales" => true,
                "ViewMedicines" => true,
                _ => false
            },
            UserRole.InventoryManager => permission switch
            {
                "ViewMedicines" => true,
                "EditMedicines" => true,
                "ManageBatches" => true,
                "Purchases" => true,
                "Suppliers" => true,
                "StockAdjustment" => true,
                _ => false
            },
            _ => false
        });
    }
}
