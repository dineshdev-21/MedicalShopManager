using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Document numbering per <see cref="DocumentType"/> (prefix, template, padding, optional Indian FY reset).
/// Posting code that inserts a document inside a SQLite transaction must allocate with
/// Infrastructure's DocumentSequenceAllocator on that same transaction; <see cref="AllocateNextAsync"/>
/// is only for documents whose insert is not transactional with the number.
/// </summary>
public interface IDocumentNumberService
{
    /// <summary>The number the next document would get, without consuming it (for display on an unsaved bill).</summary>
    Task<string> PeekNextAsync(DocumentType type, DateTime? documentDate = null);

    /// <summary>Allocates and consumes the next number in its own transaction.</summary>
    Task<string> AllocateNextAsync(DocumentType type, DateTime? documentDate = null);

    /// <summary>Configuration plus the next number of the period containing <paramref name="asOf"/> (default today).</summary>
    Task<DocumentSequence?> GetSequenceAsync(DocumentType type, DateTime? asOf = null);

    /// <summary>All sequences with the next number of the current period.</summary>
    Task<IReadOnlyList<DocumentSequence>> GetAllAsync();

    /// <summary>
    /// Saves prefix / template / padding / reset flag after validation, and sets the next number of the period
    /// containing <paramref name="asOf"/> (default today) — Control Center's "Prefix &amp; Sequences".
    /// </summary>
    Task UpdateSequenceAsync(DocumentSequence sequence, DateTime? asOf = null);
}
