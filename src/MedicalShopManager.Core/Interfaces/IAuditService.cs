using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Audit logging service.
/// </summary>
public interface IAuditService
{
    Task LogAsync(string action, string? entityType = null, long? entityId = null,
        string? oldValues = null, string? newValues = null, string? reason = null);
    Task<IReadOnlyList<AuditLog>> GetLogsAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<AuditLog>> GetLogsByUserAsync(long userId);
}
