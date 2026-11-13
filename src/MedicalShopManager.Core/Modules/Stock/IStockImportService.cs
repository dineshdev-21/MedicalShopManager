namespace MedicalShopManager.Core.Modules.Stock;

/// <summary>Opening-stock / item-master CSV import: mapping suggestion, validated preview, then commit.</summary>
public interface IStockImportService
{
    /// <summary>Parses the file and suggests a column mapping from its header row.</summary>
    IReadOnlyList<StockImportColumn> SuggestMapping(string csvText);

    /// <summary>Validates every row against the mapping and the database (existing items, duplicate batches). Writes nothing.</summary>
    Task<StockImportPreview> PreviewAsync(string csvText, IReadOnlyList<StockImportColumn> mapping);

    /// <summary>Creates missing items and opening batches for the valid rows of <paramref name="preview"/>.</summary>
    Task<StockImportResult> ImportAsync(StockImportPreview preview);
}
