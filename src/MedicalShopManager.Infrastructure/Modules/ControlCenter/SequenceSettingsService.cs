using System.Text.Json;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.ControlCenter;

/// <summary>
/// Prefix &amp; Sequences. Reads DocumentSequenceCounters (foundation table, read-only here) to know the last issued
/// number of every period, validates with <see cref="SequenceChangeRules"/> and saves through
/// <see cref="IDocumentNumberService.UpdateSequenceAsync"/> (which owns the write and its transaction).
/// </summary>
public sealed class SequenceSettingsService : ISequenceSettingsService
{
    private readonly DatabaseManager _db;
    private readonly IDocumentNumberService _numbers;
    private readonly IAuditService _audit;
    private readonly ILogger<SequenceSettingsService> _logger;

    public SequenceSettingsService(DatabaseManager db, IDocumentNumberService numbers, IAuditService audit,
        ILogger<SequenceSettingsService> logger)
    {
        _db = db;
        _numbers = numbers;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SequenceOverview>> GetOverviewAsync(DateTime? asOf = null)
    {
        var date = asOf ?? DateTime.Now;
        var result = new List<SequenceOverview>();
        foreach (var type in Enum.GetValues<DocumentType>())
        {
            var sequence = await _numbers.GetSequenceAsync(type, date);
            if (sequence == null) continue;
            result.Add(SequenceChangeRules.Describe(sequence, await GetCountersAsync(type), date));
        }
        return result;
    }

    public async Task SaveAsync(DocumentSequence proposed, DateTime? asOf = null)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var date = asOf ?? DateTime.Now;
        var current = await _numbers.GetSequenceAsync(proposed.DocumentType, date)
            ?? throw new ControlCenterValidationException(nameof(DocumentSequence.DocumentType),
                $"No numbering is configured for {SequenceChangeRules.DisplayName(proposed.DocumentType)}.");

        var normalized = new DocumentSequence
        {
            DocumentType = proposed.DocumentType,
            Prefix = proposed.Prefix?.Trim() ?? string.Empty,
            Template = proposed.Template?.Trim() ?? string.Empty,
            Padding = proposed.Padding,
            ResetEachFinancialYear = proposed.ResetEachFinancialYear,
            NextNumber = proposed.NextNumber
        };

        var errors = SequenceChangeRules.Validate(current, normalized, await GetCountersAsync(proposed.DocumentType), date);
        if (errors.Count > 0) throw new ControlCenterValidationException(errors);

        try
        {
            await _numbers.UpdateSequenceAsync(normalized, date);
        }
        catch (ArgumentException ex)
        {
            // The foundation validator is the final word; surface its message the same way as ours.
            throw new ControlCenterValidationException(nameof(DocumentSequence.Template), ex.Message);
        }

        await _audit.LogAsync("ControlCenter.SequenceUpdated", "DocumentSequence", (long)proposed.DocumentType,
            Snapshot(current), Snapshot(normalized));
        _logger.LogInformation("Sequence {Type} saved from Control Center", proposed.DocumentType);
    }

    private async Task<IReadOnlyList<SequenceCounter>> GetCountersAsync(DocumentType type)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Period, NextNumber FROM DocumentSequenceCounters WHERE DocumentType = @Type ORDER BY Period";
        cmd.Parameters.AddWithValue("@Type", (int)type);
        var list = new List<SequenceCounter>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(new SequenceCounter(reader.GetInt32(0), reader.GetInt64(1)));
        return list;
    }

    private static string Snapshot(DocumentSequence s) => JsonSerializer.Serialize(new
    {
        s.Prefix,
        s.Template,
        s.Padding,
        s.ResetEachFinancialYear,
        s.NextNumber
    });
}
