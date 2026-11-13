using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Models;

/// <summary>
/// Numbering configuration for one <see cref="DocumentType"/> (tables DocumentSequences + DocumentSequenceCounters,
/// migration 3). Read and changed through IDocumentNumberService; numbers are allocated inside a posting transaction.
/// </summary>
public class DocumentSequence
{
    public DocumentType DocumentType { get; set; }
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Format template, see DocumentNumberFormatter for tokens. Default "{PREFIX}/{FY2}/{SEQ}".</summary>
    public string Template { get; set; } = "{PREFIX}/{FY2}/{SEQ}";

    /// <summary>Zero-padding width of {SEQ}.</summary>
    public int Padding { get; set; } = 5;

    /// <summary>Restart at 1 in every financial year (April). The template must then contain {FY} or {FY2}.</summary>
    public bool ResetEachFinancialYear { get; set; }

    /// <summary>
    /// Next number of the period this object was loaded for (the financial year of the date passed to
    /// IDocumentNumberService.GetSequenceAsync, or the single running series when the sequence does not reset).
    /// </summary>
    public long NextNumber { get; set; } = 1;

    /// <summary>Counter period <see cref="NextNumber"/> belongs to: FY start year when resetting, else 0.</summary>
    public int Period { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
