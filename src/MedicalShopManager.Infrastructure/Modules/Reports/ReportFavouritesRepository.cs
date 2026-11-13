using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>ReportFavourites (migration 18): one row per starred (user, report).</summary>
public sealed class ReportFavouritesRepository : IReportFavouritesRepository
{
    private readonly DatabaseManager _db;

    public ReportFavouritesRepository(DatabaseManager db) => _db = db;

    public async Task<IReadOnlySet<string>> GetAsync(long userId)
    {
        var ids = await ReportSql.QueryAsync(_db, "SELECT ReportId FROM ReportFavourites WHERE UserId = @U ORDER BY CreatedAt, ReportId",
            r => r.Text("ReportId"), CancellationToken.None, ("@U", userId));
        return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetAsync(long userId, string reportId, bool isFavourite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        using var conn = _db.CreateNewConnection();
        using var cmd = isFavourite
            ? conn.Command("INSERT OR IGNORE INTO ReportFavourites (UserId, ReportId, CreatedAt) VALUES (@U, @R, @At)",
                ("@U", userId), ("@R", reportId), ("@At", DateTime.UtcNow.ToString("o")))
            : conn.Command("DELETE FROM ReportFavourites WHERE UserId = @U AND ReportId = @R", ("@U", userId), ("@R", reportId));
        await cmd.ExecuteNonQueryAsync();
    }
}
