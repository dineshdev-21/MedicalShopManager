using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Services;

/// <summary>
/// Audit service recording user actions, changes, and system operations.
/// </summary>
public class AuditService : IAuditService
{
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IAuthService _authService;
    private readonly ILogger<AuditService> _logger;

    public AuditService(
        IAuditLogRepository auditLogRepository,
        IAuthService authService,
        ILogger<AuditService> logger)
    {
        _auditLogRepository = auditLogRepository;
        _authService = authService;
        _logger = logger;
    }

    public async Task LogAsync(
        string action,
        string? entityType = null,
        long? entityId = null,
        string? oldValues = null,
        string? newValues = null,
        string? reason = null)
    {
        try
        {
            var currentUser = _authService.CurrentUser;
            var log = new AuditLog
            {
                UserId = currentUser?.Id,
                Username = currentUser?.Username ?? "System",
                Timestamp = DateTime.UtcNow,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                OldValues = oldValues,
                NewValues = newValues,
                Reason = reason
            };

            await _auditLogRepository.InsertAsync(log);
            _logger.LogDebug("Audit logged: {Action} on {EntityType} {EntityId} by {Username}",
                action, entityType, entityId, log.Username);
        }
        catch (Exception ex)
        {
            // Never let audit logging failure crash business operations, but log critical warning
            _logger.LogError(ex, "Failed to record audit log for action {Action}", action);
        }
    }

    public async Task<IReadOnlyList<AuditLog>> GetLogsAsync(DateTime from, DateTime to)
    {
        return await _auditLogRepository.GetByDateRangeAsync(from, to);
    }

    public async Task<IReadOnlyList<AuditLog>> GetLogsByUserAsync(long userId)
    {
        return await _auditLogRepository.GetByUserAsync(userId);
    }
}
