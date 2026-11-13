using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IAuditLogRepository
{
    Task InsertAsync(AuditLog log);
    Task<IReadOnlyList<AuditLog>> GetByDateRangeAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<AuditLog>> GetByUserAsync(long userId);
    Task<IReadOnlyList<AuditLog>> GetByEntityAsync(string entityType, long entityId);
    Task<IReadOnlyList<AuditLog>> GetRecentAsync(int limit = 100);
    Task<int> CountAsync();
}
