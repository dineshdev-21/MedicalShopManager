using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Reporting service.
/// </summary>
public interface IReportService
{
    Task<DailySalesReport> GetDailySalesReportAsync(DateTime date);
    Task<SalesReport> GetSalesReportAsync(DateTime from, DateTime to);
    Task<ProfitReport> GetProfitReportAsync(DateTime from, DateTime to);
    Task<StockReport> GetStockReportAsync();
    Task<GstReport> GetGstReportAsync(DateTime from, DateTime to);
    Task<ExpenseReport> GetExpenseReportAsync(DateTime from, DateTime to);
    Task<DashboardData> GetDashboardDataAsync();
}
