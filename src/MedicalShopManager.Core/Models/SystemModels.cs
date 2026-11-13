using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Models;

/// <summary>
/// Application user with role-based access control.
/// </summary>
public class User
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    
    /// <summary>BCrypt hashed password. Never store plain text.</summary>
    public string PasswordHash { get; set; } = string.Empty;
    
    public UserRole Role { get; set; } = UserRole.BillingOperator;
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Records important actions for audit trail.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }
    public long? UserId { get; set; }
    public string? Username { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = string.Empty;
    
    /// <summary>Type of record affected (e.g., "Sale", "Medicine", "StockAdjustment").</summary>
    public string? EntityType { get; set; }
    
    /// <summary>ID of the affected record.</summary>
    public long? EntityId { get; set; }
    
    /// <summary>JSON representation of previous values, if applicable.</summary>
    public string? OldValues { get; set; }
    
    /// <summary>JSON representation of new values, if applicable.</summary>
    public string? NewValues { get; set; }
    
    /// <summary>Reason for the action (required for sensitive adjustments).</summary>
    public string? Reason { get; set; }
    
    public string? IpAddress { get; set; }
}

/// <summary>
/// Application setting stored as key-value pair.
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Tracks pending Excel synchronization tasks.
/// </summary>
public class ExcelSyncQueueItem
{
    public long Id { get; set; }
    public SyncEntityType EntityType { get; set; }
    public long? EntityId { get; set; }
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
