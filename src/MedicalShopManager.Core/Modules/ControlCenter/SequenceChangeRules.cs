using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Numbering;

namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>One row of DocumentSequenceCounters: the next number of a counter period (FY start year, or 0).</summary>
public readonly record struct SequenceCounter(int Period, long NextNumber)
{
    /// <summary>Highest number handed out in this period (0 when none).</summary>
    public long LastIssued => Math.Max(0, NextNumber - 1);
}

/// <summary>A sequence as the Prefix &amp; Sequences screen shows it.</summary>
public sealed record SequenceOverview(
    DocumentSequence Sequence,
    string NextNumberPreview,
    long LastIssuedNumber,
    string? LastIssuedPreview,
    long MinimumNextNumber);

/// <summary>
/// Rules for editing a document sequence (contract §8): the formatter's own checks (template has {SEQ}, FY token when
/// resetting, ≤ 16 characters, GST-safe characters) plus "never re-issue a number". The last issued number of a period
/// is the counter's next − 1; lowering the next number to it or below would re-issue a number already printed on a
/// document. Switching the FY reset moves the sequence to another counter period, so:
/// <list type="bullet">
/// <item>reset → no reset: the single running series must continue above every number issued in any FY;</item>
/// <item>no reset → reset with an old template that already contained {FY}/{FY2}: the new FY series must continue
/// above the running series (same formatted text otherwise).</item>
/// </list>
/// Conservative by design: if the next number was raised earlier without issuing, it cannot be lowered again.
/// </summary>
public static class SequenceChangeRules
{
    public const int MaxPrefixLength = 10;

    /// <summary>Counter period for a date: FY start year when the sequence resets each April, else 0.</summary>
    public static int PeriodFor(bool resetEachFinancialYear, DateTime asOf) =>
        resetEachFinancialYear ? FinancialYear.For(asOf).StartYear : 0;

    public static long LastIssued(IReadOnlyList<SequenceCounter> counters, int period) =>
        counters.Where(c => c.Period == period).Select(c => c.LastIssued).DefaultIfEmpty(0).Max();

    private static bool HasFyToken(string? template) => template?.Contains("{FY", StringComparison.Ordinal) == true;

    /// <summary>The lowest next number <paramref name="proposed"/> may take without re-issuing a number.</summary>
    public static long MinimumNextNumber(DocumentSequence current, DocumentSequence proposed,
        IReadOnlyList<SequenceCounter> counters, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(counters);

        var target = PeriodFor(proposed.ResetEachFinancialYear, asOf);
        var floor = LastIssued(counters, target);

        if (current.ResetEachFinancialYear && !proposed.ResetEachFinancialYear)
            floor = Math.Max(floor, counters.Select(c => c.LastIssued).DefaultIfEmpty(0).Max());
        if (!current.ResetEachFinancialYear && proposed.ResetEachFinancialYear && HasFyToken(current.Template))
            floor = Math.Max(floor, LastIssued(counters, 0));

        return floor + 1;
    }

    /// <summary>Validates a proposed change. Empty list = valid.</summary>
    public static IReadOnlyList<SettingsError> Validate(DocumentSequence current, DocumentSequence proposed,
        IReadOnlyList<SequenceCounter> counters, DateTime asOf)
    {
        var errors = new List<SettingsError>();
        var prefix = proposed.Prefix?.Trim() ?? string.Empty;
        var template = proposed.Template?.Trim() ?? string.Empty;

        if (prefix.Length > MaxPrefixLength)
            errors.Add(new(nameof(DocumentSequence.Prefix), $"Prefix must be at most {MaxPrefixLength} characters."));
        if (template.Contains("{PREFIX}", StringComparison.Ordinal) && prefix.Length == 0)
            errors.Add(new(nameof(DocumentSequence.Prefix), "Enter a prefix, or remove {PREFIX} from the number format."));

        string? formatError;
        try
        {
            formatError = DocumentNumberFormatter.Validate(template, prefix, proposed.Padding, proposed.ResetEachFinancialYear);
        }
        catch (ArgumentException ex)
        {
            formatError = ex.Message;
        }
        if (formatError != null) errors.Add(new(nameof(DocumentSequence.Template), formatError));

        if (proposed.NextNumber < 1)
        {
            errors.Add(new(nameof(DocumentSequence.NextNumber), "Next number must be at least 1."));
        }
        else
        {
            var minimum = MinimumNextNumber(current, proposed, counters, asOf);
            if (proposed.NextNumber < minimum)
                errors.Add(new(nameof(DocumentSequence.NextNumber),
                    $"Next number must be {minimum} or more: number {minimum - 1} has already been issued and cannot be used again."));
        }
        return errors;
    }

    /// <summary>Overview row: next-number preview, last issued number and its formatted text, minimum allowed next.</summary>
    public static SequenceOverview Describe(DocumentSequence sequence, IReadOnlyList<SequenceCounter> counters, DateTime asOf)
    {
        var period = PeriodFor(sequence.ResetEachFinancialYear, asOf);
        var last = LastIssued(counters, period);
        return new SequenceOverview(
            sequence,
            Preview(sequence, sequence.NextNumber, asOf) ?? "(invalid format)",
            last,
            last > 0 ? Preview(sequence, last, asOf) : null,
            MinimumNextNumber(sequence, sequence, counters, asOf));
    }

    /// <summary>Formatted number, or null when the format is invalid (for live preview while typing).</summary>
    public static string? Preview(DocumentSequence sequence, long number, DateTime asOf)
    {
        try
        {
            return DocumentNumberFormatter.Format(sequence.Template?.Trim() ?? string.Empty, sequence.Prefix?.Trim() ?? string.Empty,
                Math.Max(1, number), sequence.Padding, asOf);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Friendly name of a document type for screens.</summary>
    public static string DisplayName(DocumentType type) => type switch
    {
        DocumentType.Sale => "Sale invoice",
        DocumentType.SaleReturn => "Sale return",
        DocumentType.CreditNote => "Credit note",
        DocumentType.Purchase => "Purchase",
        DocumentType.PurchaseReturn => "Purchase return",
        DocumentType.DebitNote => "Debit note",
        DocumentType.PurchaseOrder => "Purchase order",
        DocumentType.Receipt => "Payment in (receipt)",
        DocumentType.Payment => "Payment out",
        DocumentType.Expense => "Expense",
        DocumentType.StockAdjustment => "Stock adjustment",
        DocumentType.StockAudit => "Stock audit",
        _ => type.ToString()
    };

    /// <summary>Common number formats offered in the editor (any template passing validation is accepted).</summary>
    public static IReadOnlyList<string> TemplatePresets { get; } = new[]
    {
        "{PREFIX}/{FY2}/{SEQ}",
        "{PREFIX}-{FY2}-{SEQ}",
        "{PREFIX}/{FY}/{SEQ}",
        "{PREFIX}-{YYYY}-{SEQ}",
        "{PREFIX}{SEQ}",
        "{PREFIX}/{SEQ}"
    };
}
