using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Numbering;

namespace MedicalShopManager.Infrastructure.Numbering;

/// <summary>
/// Allocates document numbers on the caller's connection and transaction, so the number and the document row
/// commit or roll back together (MASTER-SPEC §4.11). Counters live in DocumentSequenceCounters per
/// (DocumentType, Period): Period is the financial-year start year for sequences that reset every FY, else 0.
/// A back-dated document therefore takes the next number of its <b>own</b> financial year and can never collide
/// with numbers issued after the reset. The consuming UPDATE takes SQLite's write lock, so concurrent postings
/// never receive the same number.
/// </summary>
public static class DocumentSequenceAllocator
{
    /// <summary>Counter period for a document date.</summary>
    public static int PeriodFor(bool resetEachFinancialYear, DateTime documentDate) =>
        resetEachFinancialYear ? FinancialYear.For(documentDate).StartYear : 0;

    /// <summary>Allocates and consumes the next number for <paramref name="type"/> inside <paramref name="transaction"/>.</summary>
    public static async Task<string> AllocateAsync(SqliteConnection connection, SqliteTransaction transaction,
        DocumentType type, DateTime documentDate)
    {
        var sequence = await LoadAsync(connection, transaction, type, documentDate)
            ?? throw new InvalidOperationException($"No document sequence is configured for {type}.");

        using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = @"
            INSERT OR IGNORE INTO DocumentSequenceCounters (DocumentType, Period, NextNumber) VALUES (@Type, @Period, 1);
            UPDATE DocumentSequenceCounters SET NextNumber = NextNumber + 1
            WHERE DocumentType = @Type AND Period = @Period
            RETURNING NextNumber - 1;";
        consume.Parameters.AddWithValue("@Type", (int)type);
        consume.Parameters.AddWithValue("@Period", sequence.Period);
        var allocated = Convert.ToInt64(await consume.ExecuteScalarAsync());

        return DocumentNumberFormatter.Format(sequence.Template, sequence.Prefix, allocated, sequence.Padding, documentDate);
    }

    /// <summary>Sequence configuration plus the next number of the period containing <paramref name="asOf"/>.</summary>
    public static async Task<DocumentSequence?> LoadAsync(SqliteConnection connection, SqliteTransaction? transaction,
        DocumentType type, DateTime asOf)
    {
        DocumentSequence sequence;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT * FROM DocumentSequences WHERE DocumentType = @Type";
            cmd.Parameters.AddWithValue("@Type", (int)type);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            sequence = Map(reader);
        }

        sequence.Period = PeriodFor(sequence.ResetEachFinancialYear, asOf);
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT NextNumber FROM DocumentSequenceCounters WHERE DocumentType = @Type AND Period = @Period";
            cmd.Parameters.AddWithValue("@Type", (int)type);
            cmd.Parameters.AddWithValue("@Period", sequence.Period);
            var next = await cmd.ExecuteScalarAsync();
            sequence.NextNumber = next is null or DBNull ? 1 : Convert.ToInt64(next);
        }
        return sequence;
    }

    /// <summary>Formats the number <paramref name="sequence"/>'s next allocation would get, without writing anything.</summary>
    public static string Preview(DocumentSequence sequence, DateTime documentDate) =>
        DocumentNumberFormatter.Format(sequence.Template, sequence.Prefix, sequence.NextNumber, sequence.Padding, documentDate);

    private static DocumentSequence Map(SqliteDataReader reader) => new()
    {
        DocumentType = (DocumentType)reader.GetInt32(reader.GetOrdinal("DocumentType")),
        Prefix = reader.GetString(reader.GetOrdinal("Prefix")),
        Template = reader.GetString(reader.GetOrdinal("Template")),
        Padding = reader.GetInt32(reader.GetOrdinal("Padding")),
        ResetEachFinancialYear = reader.GetInt32(reader.GetOrdinal("ResetEachFinancialYear")) == 1,
        UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")))
    };
}
