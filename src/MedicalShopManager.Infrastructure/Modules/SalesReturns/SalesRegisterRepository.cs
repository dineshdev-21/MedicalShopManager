using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.SalesReturns;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.SalesReturns;

/// <summary>
/// Sales register queries. Reads Billing's Sales / SaleItems (never writes them) and adds the value of active sale
/// returns per bill from this module's SaleReturns.
/// </summary>
public sealed class SalesRegisterRepository : ISalesRegisterRepository
{
    private const string SelectRows = @"
        SELECT s.Id, s.InvoiceNumber, s.InvoiceDate, s.CustomerId, s.CustomerName, s.CustomerPhone, s.GrandTotalPaisa,
               s.AmountPaidPaisa, s.BalanceDuePaisa, s.CgstAmountPaisa + s.SgstAmountPaisa + s.IgstAmountPaisa AS TaxPaisa,
               s.PaymentMethod, s.Status, s.DueDate, u.DisplayName AS CreatedByName,
               (SELECT COUNT(*) FROM SaleItems si WHERE si.SaleId = s.Id) AS ItemCount,
               COALESCE((SELECT SUM(r.NetAmountPaisa) FROM SaleReturns r WHERE r.SaleId = s.Id AND r.Status = 1), 0) AS ReturnedPaisa
        FROM Sales s
        LEFT JOIN Users u ON u.Id = s.CreatedByUserId";

    private readonly DatabaseManager _db;

    public SalesRegisterRepository(DatabaseManager db) => _db = db;

    public async Task<IReadOnlyList<SalesRegisterRow>> QueryAsync(SalesRegisterFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var search = SalesReturnsSql.LikeOrNull(filter.SearchText);
        using var conn = _db.CreateNewConnection();
        // A bill-number / customer search looks across all dates; otherwise the date range applies.
        using var cmd = conn.Command(null, SelectRows + $@"
            WHERE (@Search IS NOT NULL OR (s.InvoiceDate >= @From AND s.InvoiceDate < @To))
              AND (@CustomerId IS NULL OR s.CustomerId = @CustomerId)
              AND (@Mode IS NULL OR s.PaymentMethod = @Mode)
              AND (@Search IS NULL OR s.InvoiceNumber LIKE @Search ESCAPE '\' OR s.CustomerName LIKE @Search ESCAPE '\'
                   OR s.CustomerPhone LIKE @Search ESCAPE '\')
              AND {StatusClause(filter.Status)}
            ORDER BY s.InvoiceDate DESC, s.Id DESC
            LIMIT @Limit");
        cmd.AddDate("@From", filter.From.Date);
        cmd.AddDate("@To", filter.To.Date.AddDays(1));
        cmd.Add("@CustomerId", filter.CustomerId);
        cmd.Add("@Mode", filter.Mode is { } mode ? (int)mode : null);
        cmd.Add("@Search", search);
        cmd.Add("@Limit", Math.Clamp(filter.Limit, 1, 10000));
        return await ReadRowsAsync(cmd);
    }

    public async Task<IReadOnlyList<SalesRegisterRow>> SearchBillsAsync(string text, int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<SalesRegisterRow>();
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectRows + @"
            WHERE s.Status = 1
              AND (s.InvoiceNumber LIKE @Search ESCAPE '\' OR s.CustomerName LIKE @Search ESCAPE '\' OR s.CustomerPhone LIKE @Search ESCAPE '\')
            ORDER BY CASE WHEN s.InvoiceNumber = @Exact COLLATE NOCASE THEN 0 ELSE 1 END, s.InvoiceDate DESC, s.Id DESC
            LIMIT @Limit");
        cmd.Add("@Search", SalesReturnsSql.Like(text));
        cmd.Add("@Exact", text.Trim());
        cmd.Add("@Limit", Math.Clamp(limit, 1, 200));
        return await ReadRowsAsync(cmd);
    }

    /// <summary>SQL for a status filter (constant fragments only; same rule as <see cref="SalesRegisterRules.Matches"/>).</summary>
    private static string StatusClause(SalesRegisterStatus status) => status switch
    {
        SalesRegisterStatus.Paid => "(s.Status = 1 AND s.BalanceDuePaisa <= 0)",
        SalesRegisterStatus.Partial => "(s.Status = 1 AND s.BalanceDuePaisa > 0 AND s.AmountPaidPaisa > 0)",
        SalesRegisterStatus.Unpaid => "(s.Status = 1 AND s.BalanceDuePaisa > 0 AND s.AmountPaidPaisa <= 0)",
        SalesRegisterStatus.Cancelled => "(s.Status = 2)",
        _ => "1 = 1"
    };

    private static async Task<IReadOnlyList<SalesRegisterRow>> ReadRowsAsync(SqliteCommand cmd)
    {
        var rows = new List<SalesRegisterRow>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows.Add(new SalesRegisterRow
            {
                SaleId = r.Long("Id"),
                InvoiceNumber = r.Str("InvoiceNumber") ?? string.Empty,
                InvoiceDate = r.Date("InvoiceDate"),
                CustomerId = r.LongOrNull("CustomerId"),
                CustomerName = r.Str("CustomerName"),
                CustomerPhone = r.Str("CustomerPhone"),
                ItemCount = r.Int("ItemCount"),
                GrandTotalPaisa = r.Long("GrandTotalPaisa"),
                AmountPaidPaisa = r.Long("AmountPaidPaisa"),
                BalanceDuePaisa = r.Long("BalanceDuePaisa"),
                TaxPaisa = r.Long("TaxPaisa"),
                PaymentMethod = (PaymentMethod)r.Int("PaymentMethod"),
                IsCancelled = r.Int("Status", 1) == 2,
                DueDate = r.DateOrNull("DueDate"),
                ReturnedPaisa = r.Long("ReturnedPaisa"),
                CreatedByName = r.Str("CreatedByName")
            });
        }
        return rows;
    }
}
