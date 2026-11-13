using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Modules.Reports;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>Catalogue plus per-user favourites (user id null = nobody signed in, stored as 0).</summary>
public sealed class ReportCatalogService : IReportCatalogService
{
    private readonly IReportFavouritesRepository _favourites;

    public ReportCatalogService(IReportFavouritesRepository favourites) => _favourites = favourites;

    public IReadOnlyList<ReportDefinition> Reports => ReportCatalog.All;

    public Task<IReadOnlySet<string>> GetFavouritesAsync(long? userId) => _favourites.GetAsync(userId ?? 0);

    public async Task<bool> SetFavouriteAsync(long? userId, string reportId, bool isFavourite)
    {
        var definition = ReportCatalog.Find(reportId) ?? throw new ReportException($"Unknown report '{reportId}'.");
        await _favourites.SetAsync(userId ?? 0, definition.Id, isFavourite);
        return isFavourite;
    }
}

/// <summary>
/// Validates filters and dispatches a run to the provider that owns the report. Database and programming errors are
/// logged and surfaced as a <see cref="ReportException"/> with a user-friendly message.
/// </summary>
public sealed class ReportRunner : IReportRunner
{
    private readonly IReadOnlyDictionary<string, IReportProvider> _providers;
    private readonly ILogger<ReportRunner> _logger;

    public ReportRunner(IEnumerable<IReportProvider> providers, ILogger<ReportRunner>? logger = null)
    {
        var map = new Dictionary<string, IReportProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            foreach (var id in provider.ReportIds)
            {
                if (!map.TryAdd(id, provider))
                    throw new InvalidOperationException($"Report '{id}' is provided twice ({map[id].GetType().Name}, {provider.GetType().Name}).");
            }
        }
        _providers = map;
        _logger = logger ?? NullLogger<ReportRunner>.Instance;
    }

    public bool CanRun(string reportId) => _providers.ContainsKey(reportId);

    public async Task<ReportResult> RunAsync(string reportId, ReportParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var definition = ReportCatalog.Find(reportId) ?? throw new ReportException($"Unknown report '{reportId}'.");
        var errors = parameters.Validate(definition);
        if (errors.Count > 0) throw new ReportException(string.Join(" ", errors));
        if (!_providers.TryGetValue(definition.Id, out var provider))
            throw new ReportException($"'{definition.Name}' is not available in this build.");

        try
        {
            var result = await provider.RunAsync(definition, parameters, cancellationToken);
            foreach (var sheet in result.Sheets) sheet.EnsureValid();
            return result;
        }
        catch (ReportException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Report {ReportId} failed for {Period}", definition.Id, parameters.PeriodLabel(definition));
            throw new ReportException($"Could not prepare '{definition.Name}'. {ex.Message}", ex);
        }
    }
}
