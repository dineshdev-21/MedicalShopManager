using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Numbering;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Numbering;

/// <summary>
/// <see cref="IDocumentNumberService"/> over the DocumentSequences table. Repositories that post a document in
/// their own transaction call <see cref="DocumentSequenceAllocator.AllocateAsync"/> directly instead.
/// </summary>
public class DocumentNumberService : IDocumentNumberService
{
    private readonly DatabaseManager _db;
    private readonly ILogger<DocumentNumberService> _logger;

    public DocumentNumberService(DatabaseManager db, ILogger<DocumentNumberService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<string> PeekNextAsync(DocumentType type, DateTime? documentDate = null)
    {
        var date = documentDate ?? DateTime.Now;
        var sequence = await GetSequenceAsync(type, date)
            ?? throw new InvalidOperationException($"No document sequence is configured for {type}.");
        return DocumentSequenceAllocator.Preview(sequence, date);
    }

    public async Task<string> AllocateNextAsync(DocumentType type, DateTime? documentDate = null)
    {
        using var conn = _db.CreateNewConnection();
        using var transaction = conn.BeginTransaction();
        try
        {
            var number = await DocumentSequenceAllocator.AllocateAsync(conn, transaction, type, documentDate ?? DateTime.Now);
            transaction.Commit();
            return number;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<DocumentSequence?> GetSequenceAsync(DocumentType type, DateTime? asOf = null)
    {
        using var conn = _db.CreateNewConnection();
        return await DocumentSequenceAllocator.LoadAsync(conn, null, type, asOf ?? DateTime.Now);
    }

    public async Task<IReadOnlyList<DocumentSequence>> GetAllAsync()
    {
        using var conn = _db.CreateNewConnection();
        var list = new List<DocumentSequence>();
        foreach (var type in Enum.GetValues<DocumentType>())
        {
            var sequence = await DocumentSequenceAllocator.LoadAsync(conn, null, type, DateTime.Now);
            if (sequence != null) list.Add(sequence);
        }
        return list;
    }

    public async Task UpdateSequenceAsync(DocumentSequence sequence, DateTime? asOf = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var prefix = sequence.Prefix?.Trim() ?? string.Empty;
        var template = sequence.Template?.Trim() ?? string.Empty;

        var error = DocumentNumberFormatter.Validate(template, prefix, sequence.Padding, sequence.ResetEachFinancialYear);
        if (error != null) throw new ArgumentException(error, nameof(sequence));
        if (sequence.NextNumber < 1) throw new ArgumentException("Next number must be at least 1.", nameof(sequence));

        using var conn = _db.CreateNewConnection();
        using var transaction = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                UPDATE DocumentSequences SET Prefix = @Prefix, Template = @Template, Padding = @Padding,
                    ResetEachFinancialYear = @Reset, UpdatedAt = @Now
                WHERE DocumentType = @Type";
            cmd.Parameters.AddWithValue("@Type", (int)sequence.DocumentType);
            cmd.Parameters.AddWithValue("@Prefix", prefix);
            cmd.Parameters.AddWithValue("@Template", template);
            cmd.Parameters.AddWithValue("@Padding", sequence.Padding);
            cmd.Parameters.AddWithValue("@Reset", sequence.ResetEachFinancialYear ? 1 : 0);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o"));
            if (await cmd.ExecuteNonQueryAsync() == 0)
                throw new InvalidOperationException($"No document sequence is configured for {sequence.DocumentType}.");
        }

        var period = DocumentSequenceAllocator.PeriodFor(sequence.ResetEachFinancialYear, asOf ?? DateTime.Now);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO DocumentSequenceCounters (DocumentType, Period, NextNumber) VALUES (@Type, @Period, @Next)
                ON CONFLICT (DocumentType, Period) DO UPDATE SET NextNumber = excluded.NextNumber;";
            cmd.Parameters.AddWithValue("@Type", (int)sequence.DocumentType);
            cmd.Parameters.AddWithValue("@Period", period);
            cmd.Parameters.AddWithValue("@Next", sequence.NextNumber);
            await cmd.ExecuteNonQueryAsync();
        }

        transaction.Commit();
        sequence.Period = period;

        _logger.LogInformation("Document sequence {Type} updated: prefix {Prefix}, template {Template}, next {Next} (period {Period})",
            sequence.DocumentType, prefix, template, sequence.NextNumber, period);
    }
}
