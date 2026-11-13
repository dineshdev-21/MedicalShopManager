namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>Catalogue groups (Localwell "Business Reports" categories, adapted to an offline single-shop app).</summary>
public enum ReportCategory
{
    Sales = 1,
    Purchases = 2,
    Gst = 3,
    Accounts = 4,
    Profit = 5,
    Stock = 6,
    Compliance = 7,
    Parties = 8
}

/// <summary>How a report column is typed, totalled, displayed and exported.</summary>
public enum ReportColumnKind
{
    /// <summary>string</summary>
    Text,
    /// <summary>DateTime (date only)</summary>
    Date,
    /// <summary>long count</summary>
    Integer,
    /// <summary>long paisa; shown and exported as rupees with 2 decimals</summary>
    Money,
    /// <summary>long units</summary>
    Quantity,
    /// <summary>decimal percent (12 = 12 %)</summary>
    Percent
}

/// <summary>Visual weight of a row (opening / subtotal rows are bold and excluded from column totals).</summary>
public enum ReportRowStyle
{
    Normal,
    Opening,
    Subtotal
}

/// <summary>Quick date presets offered next to every date-range report.</summary>
public enum ReportDatePreset
{
    Today,
    Yesterday,
    ThisMonth,
    LastMonth,
    Last30Days,
    Last90Days,
    ThisFinancialYear,
    LastFinancialYear
}

public static class ReportEnumLabels
{
    public static string ToLabel(this ReportCategory category) => category switch
    {
        ReportCategory.Sales => "Sales",
        ReportCategory.Purchases => "Purchases",
        ReportCategory.Gst => "GST",
        ReportCategory.Accounts => "Accounts",
        ReportCategory.Profit => "Profit",
        ReportCategory.Stock => "Stock & Expiry",
        ReportCategory.Compliance => "Drug registers",
        ReportCategory.Parties => "Parties & expenses",
        _ => category.ToString()
    };

    public static string ToLabel(this ReportDatePreset preset) => preset switch
    {
        ReportDatePreset.Today => "Today",
        ReportDatePreset.Yesterday => "Yesterday",
        ReportDatePreset.ThisMonth => "This month",
        ReportDatePreset.LastMonth => "Last month",
        ReportDatePreset.Last30Days => "Last 30 days",
        ReportDatePreset.Last90Days => "Last 90 days",
        ReportDatePreset.ThisFinancialYear => "This FY",
        ReportDatePreset.LastFinancialYear => "Last FY",
        _ => preset.ToString()
    };

    public static bool IsNumeric(this ReportColumnKind kind) =>
        kind is ReportColumnKind.Integer or ReportColumnKind.Money or ReportColumnKind.Quantity or ReportColumnKind.Percent;
}
