namespace MedicalShopManager.Core.Numbering;

/// <summary>
/// Formats document numbers from a sequence template. Tokens (case-sensitive):
/// <c>{PREFIX}</c>, <c>{SEQ}</c> (zero-padded), <c>{FY}</c> ("2026-27"), <c>{FY2}</c> ("2627"), <c>{YYYY}</c> (calendar year).
/// Pure; the allocation itself lives in Infrastructure (DocumentSequenceAllocator).
/// </summary>
public static class DocumentNumberFormatter
{
    public const string SeqToken = "{SEQ}";
    public const string DefaultTemplate = "{PREFIX}/{FY2}/{SEQ}";

    /// <summary>GST rules cap a tax-invoice number at 16 characters.</summary>
    public const int MaxGstDocumentNumberLength = 16;

    public static string Format(string template, string prefix, long sequence, int padding, DateTime documentDate)
    {
        if (string.IsNullOrWhiteSpace(template)) throw new ArgumentException("Template is required.", nameof(template));
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence numbers start at 1.");
        if (padding is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(padding), "Padding must be 1–12.");

        var fy = FinancialYear.For(documentDate);
        return template
            .Replace("{PREFIX}", prefix ?? string.Empty, StringComparison.Ordinal)
            .Replace("{FY2}", fy.ShortLabel, StringComparison.Ordinal)
            .Replace("{FY}", fy.Label, StringComparison.Ordinal)
            .Replace("{YYYY}", documentDate.Year.ToString("D4"), StringComparison.Ordinal)
            .Replace(SeqToken, sequence.ToString().PadLeft(padding, '0'), StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates a template before it is saved. A per-FY reset restarts at 1, so the template must then contain
    /// {FY} or {FY2}, otherwise numbers would repeat and break the documents' UNIQUE constraints.
    /// Returns null when valid, otherwise a user-facing message.
    /// </summary>
    public static string? Validate(string template, string prefix, int padding, bool resetEachFinancialYear)
    {
        if (string.IsNullOrWhiteSpace(template)) return "Number format is required.";
        if (!template.Contains(SeqToken, StringComparison.Ordinal)) return "Number format must contain {SEQ}.";
        if (padding is < 1 or > 12) return "Number padding must be between 1 and 12 digits.";
        if (resetEachFinancialYear && !template.Contains("{FY", StringComparison.Ordinal))
            return "A sequence that resets every financial year must include {FY} or {FY2} in its format.";

        var sample = Format(template, prefix, 1, padding, new DateTime(2026, 4, 1));
        if (sample.Length > MaxGstDocumentNumberLength)
            return $"Numbers like '{sample}' exceed the GST limit of {MaxGstDocumentNumberLength} characters.";

        // GST rule 46(b): letters, digits, '-' and '/' only (also catches unknown or mistyped {TOKENS}).
        return sample.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '/')
            ? null
            : $"Numbers like '{sample}' may contain only letters, digits, '-' and '/'.";
    }
}
