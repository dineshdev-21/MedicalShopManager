using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>
/// Shared plumbing for report providers. Status values of other modules' tables are compile-time constants here
/// (Billing SaleStatus, SalesReturns SaleReturnStatus, Purchases PurchaseStatus), never user input.
/// </summary>
public abstract class ReportProviderBase : IReportProvider
{
    protected const int SaleActive = 1;          // SaleStatus.Active (migration 10)
    protected const int SaleCancelled = 2;
    protected const int ReturnActive = 1;        // SaleReturnStatus.Active (migration 11)
    protected const int ReturnCancelled = 2;
    protected const int PurchaseConfirmed = (int)PurchaseStatus.Confirmed;

    protected ReportProviderBase(DatabaseManager db) => Db = db;

    protected DatabaseManager Db { get; }

    public abstract IReadOnlyCollection<string> ReportIds { get; }

    public abstract Task<ReportResult> RunAsync(ReportDefinition definition, ReportParameters parameters, CancellationToken cancellationToken = default);

    protected static ReportResult Result(ReportDefinition definition, ReportParameters parameters, IEnumerable<ReportTable> sheets,
        IEnumerable<ReportStat>? summary = null, IEnumerable<string>? notes = null) => new()
    {
        Definition = definition,
        Parameters = parameters,
        Sheets = sheets.Select(s => s.EnsureValid()).ToList(),
        Summary = summary?.ToList() ?? new List<ReportStat>(),
        Notes = notes?.ToList() ?? new List<string>()
    };

    /// <summary>
    /// Maps a status choice (active / cancelled / all) to the status value to filter on (0 = any).
    /// </summary>
    protected static int StatusFilter(string choice, int active, int cancelled) => choice switch
    {
        ReportChoiceKeys.Active => active,
        ReportChoiceKeys.Cancelled => cancelled,
        _ => 0
    };

    protected static string ModeLabel(long method) => (PaymentMethod)method switch
    {
        PaymentMethod.Cash => "Cash",
        PaymentMethod.UPI => "UPI",
        PaymentMethod.Card => "Card",
        PaymentMethod.BankTransfer => "Bank",
        PaymentMethod.Credit => "Credit",
        PaymentMethod.Split => "Split",
        _ => "Other"
    };

    protected static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
