using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Infrastructure.Services;

/// <summary>
/// Reporting service providing analytics, financial statements, stock valuation, and dashboard data.
/// </summary>
public class ReportService : IReportService
{
    private readonly ISaleRepository _saleRepository;
    private readonly IPurchaseRepository _purchaseRepository;
    private readonly IMedicineRepository _medicineRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly IExpenseRepository _expenseRepository;
    private readonly IReturnRepository _returnRepository;
    private readonly ISupplierRepository _supplierRepository;
    private readonly ILogger<ReportService> _logger;

    public ReportService(
        ISaleRepository saleRepository,
        IPurchaseRepository purchaseRepository,
        IMedicineRepository medicineRepository,
        IBatchRepository batchRepository,
        IExpenseRepository expenseRepository,
        IReturnRepository returnRepository,
        ISupplierRepository supplierRepository,
        ILogger<ReportService> logger)
    {
        _saleRepository = saleRepository;
        _purchaseRepository = purchaseRepository;
        _medicineRepository = medicineRepository;
        _batchRepository = batchRepository;
        _expenseRepository = expenseRepository;
        _returnRepository = returnRepository;
        _supplierRepository = supplierRepository;
        _logger = logger;
    }

    public async Task<DailySalesReport> GetDailySalesReportAsync(DateTime date)
    {
        var start = date.Date;
        var end = date.Date.AddDays(1).AddTicks(-1);
        var sales = await _saleRepository.GetByDateRangeAsync(start, end);

        var invoiceCount = sales.Count;
        var totalSales = sales.Sum(s => s.Subtotal);
        var totalDiscount = sales.Sum(s => s.DiscountAmount);
        var totalTax = sales.Sum(s => s.CgstAmount + s.SgstAmount + s.IgstAmount);
        var netSales = sales.Sum(s => s.GrandTotal);

        // Approximate COGS from batch purchase price
        decimal totalCogs = 0;
        foreach (var sale in sales)
        {
            var fullSale = await _saleRepository.GetWithItemsAsync(sale.Id);
            if (fullSale != null)
            {
                totalCogs += fullSale.Items.Sum(i => i.CogsPaisa / 100m);
            }
        }

        var grossProfit = netSales - totalCogs;

        return new DailySalesReport(
            Date: start,
            InvoiceCount: invoiceCount,
            TotalSales: totalSales,
            TotalDiscount: totalDiscount,
            TotalTax: totalTax,
            NetSales: netSales,
            EstimatedCogs: totalCogs,
            GrossProfit: grossProfit
        );
    }

    public async Task<SalesReport> GetSalesReportAsync(DateTime from, DateTime to)
    {
        var sales = await _saleRepository.GetByDateRangeAsync(from, to);
        var returns = await _returnRepository.GetByDateRangeAsync(from, to);

        var totalSales = sales.Sum(s => s.GrandTotal);
        var totalReturns = returns.Where(r => r.ReturnType == ReturnType.SalesReturn).Sum(r => r.RefundAmountPaisa / 100m);
        var netSales = totalSales - totalReturns;

        decimal totalCogs = 0;
        var medicineSalesMap = new Dictionary<string, (int Qty, decimal Revenue)>();
        var paymentMap = new Dictionary<PaymentMethod, (int Count, decimal Total)>();

        foreach (var sale in sales)
        {
            var fullSale = await _saleRepository.GetWithItemsAsync(sale.Id);
            if (fullSale != null)
            {
                foreach (var item in fullSale.Items)
                {
                    totalCogs += item.CogsPaisa / 100m;
                    if (!medicineSalesMap.ContainsKey(item.MedicineName))
                        medicineSalesMap[item.MedicineName] = (0, 0);

                    var current = medicineSalesMap[item.MedicineName];
                    medicineSalesMap[item.MedicineName] = (current.Qty + item.Quantity, current.Revenue + (item.LineTotalPaisa / 100m));
                }
            }

            if (!paymentMap.ContainsKey(sale.PaymentMethod))
                paymentMap[sale.PaymentMethod] = (0, 0);

            var currPay = paymentMap[sale.PaymentMethod];
            paymentMap[sale.PaymentMethod] = (currPay.Count + 1, currPay.Total + sale.GrandTotal);
        }

        var dailyBreakdown = sales
            .GroupBy(s => s.InvoiceDate.Date)
            .OrderBy(g => g.Key)
            .Select(g => new SalesSummaryByDay(
                Date: g.Key,
                Sales: g.Sum(s => s.GrandTotal),
                Returns: 0,
                Net: g.Sum(s => s.GrandTotal)
            ))
            .ToList();

        var topMedicines = medicineSalesMap
            .OrderByDescending(kv => kv.Value.Revenue)
            .Take(10)
            .Select(kv => new SalesSummaryByMedicine(kv.Key, kv.Value.Qty, kv.Value.Revenue))
            .ToList();

        var paymentMethodBreakdown = paymentMap
            .Select(kv => new SalesSummaryByPaymentMethod(kv.Key, kv.Value.Count, kv.Value.Total))
            .ToList();

        return new SalesReport(
            From: from,
            To: to,
            InvoiceCount: sales.Count,
            TotalSales: totalSales,
            TotalReturns: totalReturns,
            NetSales: netSales,
            TotalCogs: totalCogs,
            GrossProfit: netSales - totalCogs,
            DailyBreakdown: dailyBreakdown,
            TopMedicines: topMedicines,
            PaymentMethodBreakdown: paymentMethodBreakdown
        );
    }

    public async Task<ProfitReport> GetProfitReportAsync(DateTime from, DateTime to)
    {
        var salesReport = await GetSalesReportAsync(from, to);
        var totalExpensesPaisa = await _expenseRepository.GetTotalByDateRangeAsync(from, to);
        var totalExpenses = totalExpensesPaisa / 100m;

        var netProfit = salesReport.GrossProfit - totalExpenses;
        var profitMargin = salesReport.NetSales > 0 ? (salesReport.GrossProfit / salesReport.NetSales) * 100m : 0;

        var sales = await _saleRepository.GetByDateRangeAsync(from, to);
        var totalTax = sales.Sum(s => s.CgstAmount + s.SgstAmount + s.IgstAmount);

        return new ProfitReport(
            From: from,
            To: to,
            NetSales: salesReport.NetSales,
            Cogs: salesReport.TotalCogs,
            GrossProfit: salesReport.GrossProfit,
            GrossProfitMargin: Math.Round(profitMargin, 2),
            TotalExpenses: totalExpenses,
            EstimatedNetProfit: netProfit,
            TotalReturns: salesReport.TotalReturns,
            TotalTaxCollected: totalTax,
            Disclaimer: "Estimated COGS based on batch purchase price. Consult accountant for tax filings."
        );
    }

    public async Task<StockReport> GetStockReportAsync()
    {
        var allBatches = await _batchRepository.GetAllAsync();
        var nearExpiry = await _batchRepository.GetNearExpiryAsync(90);
        var expired = await _batchRepository.GetExpiredAsync();
        var lowStockMedicines = await _medicineRepository.GetLowStockAsync();

        var activeBatches = allBatches.Where(b => b.IsActive && b.QuantityRemaining > 0).ToList();
        var totalUnits = activeBatches.Sum(b => b.QuantityRemaining);
        var totalStockValue = activeBatches.Sum(b =>
            PricingCalculator.AmountForUnits(b.PurchaseRatePaisa, b.QuantityRemaining, b.UnitsPerPack)) / 100m;
        var totalMedicines = await _medicineRepository.CountAsync();

        return new StockReport(
            TotalMedicines: totalMedicines,
            TotalBatches: activeBatches.Count,
            TotalUnits: totalUnits,
            TotalStockValue: totalStockValue,
            LowStockCount: lowStockMedicines.Count,
            NearExpiryCount: nearExpiry.Count,
            ExpiredCount: expired.Count,
            LowStockMedicines: lowStockMedicines,
            NearExpiryBatches: nearExpiry,
            ExpiredBatches: expired
        );
    }

    public async Task<GstReport> GetGstReportAsync(DateTime from, DateTime to)
    {
        var sales = await _saleRepository.GetByDateRangeAsync(from, to);

        var totalTaxable = sales.Sum(s => s.TaxableAmount);
        var totalCgst = sales.Sum(s => s.CgstAmount);
        var totalSgst = sales.Sum(s => s.SgstAmount);
        var totalIgst = sales.Sum(s => s.IgstAmount);

        var rateMap = new Dictionary<decimal, (decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst)>();

        foreach (var sale in sales)
        {
            var fullSale = await _saleRepository.GetWithItemsAsync(sale.Id);
            if (fullSale != null)
            {
                foreach (var item in fullSale.Items)
                {
                    var rate = item.GstRatePercent;
                    if (!rateMap.ContainsKey(rate))
                        rateMap[rate] = (0, 0, 0, 0);

                    var curr = rateMap[rate];
                    rateMap[rate] = (
                        curr.Taxable + (item.TaxableValuePaisa / 100m),
                        curr.Cgst + (item.CgstAmountPaisa / 100m),
                        curr.Sgst + (item.SgstAmountPaisa / 100m),
                        curr.Igst + (item.IgstAmountPaisa / 100m)
                    );
                }
            }
        }

        var rateBreakdown = rateMap
            .OrderBy(kv => kv.Key)
            .Select(kv => new GstRateBreakdown(kv.Key, kv.Value.Taxable, kv.Value.Cgst, kv.Value.Sgst, kv.Value.Igst))
            .ToList();

        return new GstReport(
            From: from,
            To: to,
            TotalTaxableValue: totalTaxable,
            TotalCgst: totalCgst,
            TotalSgst: totalSgst,
            TotalIgst: totalIgst,
            TotalTax: totalCgst + totalSgst + totalIgst,
            RateBreakdown: rateBreakdown
        );
    }

    public async Task<ExpenseReport> GetExpenseReportAsync(DateTime from, DateTime to)
    {
        var expenses = await _expenseRepository.GetByDateRangeAsync(from, to);
        var totalExpenses = expenses.Sum(e => e.Amount);

        var categoryBreakdown = expenses
            .GroupBy(e => e.Category)
            .Select(g => new ExpenseCategorySummary(g.Key, g.Sum(e => e.Amount), g.Count()))
            .OrderByDescending(c => c.Total)
            .ToList();

        return new ExpenseReport(
            From: from,
            To: to,
            TotalExpenses: totalExpenses,
            CategoryBreakdown: categoryBreakdown
        );
    }

    public async Task<DashboardData> GetDashboardDataAsync()
    {
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);

        var todaySalesList = await _saleRepository.GetByDateRangeAsync(today, tomorrow);
        var todaySalesTotal = todaySalesList.Sum(s => s.GrandTotal);

        var todayPurchasesList = await _purchaseRepository.GetByDateRangeAsync(today, tomorrow);
        var todayPurchasesTotal = todayPurchasesList.Sum(p => p.GrandTotal);

        // Calculate today's gross profit
        decimal todayCogs = 0;
        foreach (var sale in todaySalesList)
        {
            var fullSale = await _saleRepository.GetWithItemsAsync(sale.Id);
            if (fullSale != null)
                todayCogs += fullSale.Items.Sum(i => i.CogsPaisa / 100m);
        }
        var todayGrossProfit = todaySalesTotal - todayCogs;

        var allBatches = await _batchRepository.GetAllAsync();
        var activeBatches = allBatches.Where(b => b.IsActive && b.QuantityRemaining > 0).ToList();
        var totalUnits = activeBatches.Sum(b => b.QuantityRemaining);

        var lowStockMedicines = await _medicineRepository.GetLowStockAsync();
        var nearExpiry = await _batchRepository.GetNearExpiryAsync(90);
        var expired = await _batchRepository.GetExpiredAsync();
        var totalMedicines = await _medicineRepository.CountAsync();

        var pendingSupplierPayments = (await _supplierRepository.GetTotalOutstandingBalanceAsync()) / 100m;

        // Last 7 days trend
        var sevenDaysAgo = today.AddDays(-6);
        var last7Sales = await _saleRepository.GetByDateRangeAsync(sevenDaysAgo, tomorrow);
        var last7Breakdown = Enumerable.Range(0, 7)
            .Select(i => sevenDaysAgo.AddDays(i))
            .Select(d => new SalesSummaryByDay(
                Date: d,
                Sales: last7Sales.Where(s => s.InvoiceDate.Date == d.Date).Sum(s => s.GrandTotal),
                Returns: 0,
                Net: last7Sales.Where(s => s.InvoiceDate.Date == d.Date).Sum(s => s.GrandTotal)
            ))
            .ToList();

        // Last 30 days trend
        var thirtyDaysAgo = today.AddDays(-29);
        var last30Sales = await _saleRepository.GetByDateRangeAsync(thirtyDaysAgo, tomorrow);
        var last30Breakdown = Enumerable.Range(0, 30)
            .Select(i => thirtyDaysAgo.AddDays(i))
            .Select(d => new SalesSummaryByDay(
                Date: d,
                Sales: last30Sales.Where(s => s.InvoiceDate.Date == d.Date).Sum(s => s.GrandTotal),
                Returns: 0,
                Net: last30Sales.Where(s => s.InvoiceDate.Date == d.Date).Sum(s => s.GrandTotal)
            ))
            .ToList();

        // Top 5 selling medicines
        var medQtyMap = new Dictionary<string, (int Qty, decimal Revenue)>();
        foreach (var sale in last30Sales)
        {
            var full = await _saleRepository.GetWithItemsAsync(sale.Id);
            if (full != null)
            {
                foreach (var itm in full.Items)
                {
                    if (!medQtyMap.ContainsKey(itm.MedicineName))
                        medQtyMap[itm.MedicineName] = (0, 0);
                    var cur = medQtyMap[itm.MedicineName];
                    medQtyMap[itm.MedicineName] = (cur.Qty + itm.Quantity, cur.Revenue + (itm.LineTotalPaisa / 100m));
                }
            }
        }

        var topMedicines = medQtyMap
            .OrderByDescending(kv => kv.Value.Revenue)
            .Take(5)
            .Select(kv => new SalesSummaryByMedicine(kv.Key, kv.Value.Qty, kv.Value.Revenue))
            .ToList();

        return new DashboardData(
            TodaySales: todaySalesTotal,
            TodayPurchases: todayPurchasesTotal,
            TodayGrossProfit: todayGrossProfit,
            TotalActiveMedicines: totalMedicines,
            TotalStockUnits: totalUnits,
            LowStockCount: lowStockMedicines.Count,
            NearExpiryCount: nearExpiry.Count,
            ExpiredCount: expired.Count,
            PendingSupplierPayments: pendingSupplierPayments,
            TodayBillCount: todaySalesList.Count,
            Last7DaysSales: last7Breakdown,
            Last30DaysSales: last30Breakdown,
            TopSellingMedicines: topMedicines
        );
    }
}
