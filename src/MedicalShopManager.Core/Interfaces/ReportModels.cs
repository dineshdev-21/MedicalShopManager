using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public record DailySalesReport(
    DateTime Date,
    int InvoiceCount,
    decimal TotalSales,
    decimal TotalDiscount,
    decimal TotalTax,
    decimal NetSales,
    decimal EstimatedCogs,
    decimal GrossProfit
);

public record SalesReport(
    DateTime From,
    DateTime To,
    int InvoiceCount,
    decimal TotalSales,
    decimal TotalReturns,
    decimal NetSales,
    decimal TotalCogs,
    decimal GrossProfit,
    IReadOnlyList<SalesSummaryByDay> DailyBreakdown,
    IReadOnlyList<SalesSummaryByMedicine> TopMedicines,
    IReadOnlyList<SalesSummaryByPaymentMethod> PaymentMethodBreakdown
);

public record SalesSummaryByDay(DateTime Date, decimal Sales, decimal Returns, decimal Net);

public record SalesSummaryByMedicine(string MedicineName, int QuantitySold, decimal Revenue);

public record SalesSummaryByPaymentMethod(PaymentMethod Method, int Count, decimal Total);

public record ProfitReport(
    DateTime From,
    DateTime To,
    decimal NetSales,
    decimal Cogs,
    decimal GrossProfit,
    decimal GrossProfitMargin,
    decimal TotalExpenses,
    decimal EstimatedNetProfit,
    decimal TotalReturns,
    decimal TotalTaxCollected,
    string Disclaimer
);

public record StockReport(
    int TotalMedicines,
    int TotalBatches,
    int TotalUnits,
    decimal TotalStockValue,
    int LowStockCount,
    int NearExpiryCount,
    int ExpiredCount,
    IReadOnlyList<Medicine> LowStockMedicines,
    IReadOnlyList<MedicineBatch> NearExpiryBatches,
    IReadOnlyList<MedicineBatch> ExpiredBatches
);

public record GstReport(
    DateTime From,
    DateTime To,
    decimal TotalTaxableValue,
    decimal TotalCgst,
    decimal TotalSgst,
    decimal TotalIgst,
    decimal TotalTax,
    IReadOnlyList<GstRateBreakdown> RateBreakdown
);

public record GstRateBreakdown(decimal Rate, decimal TaxableValue, decimal Cgst, decimal Sgst, decimal Igst);

public record ExpenseReport(
    DateTime From,
    DateTime To,
    decimal TotalExpenses,
    IReadOnlyList<ExpenseCategorySummary> CategoryBreakdown
);

public record ExpenseCategorySummary(ExpenseCategory Category, decimal Total, int Count);

public record DashboardData(
    decimal TodaySales,
    decimal TodayPurchases,
    decimal TodayGrossProfit,
    int TotalActiveMedicines,
    int TotalStockUnits,
    int LowStockCount,
    int NearExpiryCount,
    int ExpiredCount,
    decimal PendingSupplierPayments,
    int TodayBillCount,
    IReadOnlyList<SalesSummaryByDay> Last7DaysSales,
    IReadOnlyList<SalesSummaryByDay> Last30DaysSales,
    IReadOnlyList<SalesSummaryByMedicine> TopSellingMedicines
);
