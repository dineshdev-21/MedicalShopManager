using System.Text.Json;
using MedicalShopManager.Core.Interfaces;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>Audit-log helpers of the Finance services (every money movement and master change is logged).</summary>
internal static class FinanceAudit
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Json(object? value) => value is null ? string.Empty : JsonSerializer.Serialize(value, Options);

    /// <summary>Logs without letting an audit failure undo a posting that has already committed.</summary>
    public static async Task LogAsync(IAuditService audit, string action, string entityType, long? entityId,
        object? oldValues = null, object? newValues = null, string? reason = null)
    {
        try
        {
            await audit.LogAsync(action, entityType, entityId,
                oldValues is null ? null : Json(oldValues), newValues is null ? null : Json(newValues), reason);
        }
        catch (Exception ex)
        {
            // The money is already posted; an audit-log write failure (e.g. disk full) must not report the posting as failed.
            System.Diagnostics.Trace.TraceError($"Finance audit log '{action}' for {entityType} {entityId} failed: {ex}");
        }
    }
}
