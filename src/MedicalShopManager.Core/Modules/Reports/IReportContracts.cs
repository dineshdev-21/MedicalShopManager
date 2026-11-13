namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>
/// Builds one or more catalogue reports from the database (read-only). Infrastructure registers one provider per report
/// group; <see cref="IReportRunner"/> dispatches by <see cref="ReportDefinition.Id"/>.
/// </summary>
public interface IReportProvider
{
    IReadOnlyCollection<string> ReportIds { get; }

    Task<ReportResult> RunAsync(ReportDefinition definition, ReportParameters parameters, CancellationToken cancellationToken = default);
}

/// <summary>Runs catalogue reports: validates the filters, then calls the provider that owns the report.</summary>
public interface IReportRunner
{
    Task<ReportResult> RunAsync(string reportId, ReportParameters parameters, CancellationToken cancellationToken = default);
}

/// <summary>Per-user starred reports (table ReportFavourites, migration 18).</summary>
public interface IReportFavouritesRepository
{
    Task<IReadOnlySet<string>> GetAsync(long userId);
    Task SetAsync(long userId, string reportId, bool isFavourite);
}

/// <summary>Catalogue with the current user's favourites.</summary>
public interface IReportCatalogService
{
    IReadOnlyList<ReportDefinition> Reports { get; }
    Task<IReadOnlySet<string>> GetFavouritesAsync(long? userId);
    /// <summary>Stars or un-stars a report; returns the new state.</summary>
    Task<bool> SetFavouriteAsync(long? userId, string reportId, bool isFavourite);
}

/// <summary>Writes a report to an .xlsx workbook (one worksheet per sheet) and records the export in the audit log.</summary>
public interface IReportExporter
{
    /// <param name="sheets">Sheets to write (e.g. the viewer's filtered sheet); null = every sheet of the result.</param>
    Task ExportExcelAsync(ReportResult result, string filePath, IReadOnlyList<ReportTable>? sheets = null, string? shopName = null);
}

/// <summary>A user-facing problem running a report (bad filters, missing data).</summary>
public sealed class ReportException : Exception
{
    public ReportException(string message) : base(message) { }
    public ReportException(string message, Exception inner) : base(message, inner) { }
}
