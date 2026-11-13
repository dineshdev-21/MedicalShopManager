namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>Stable report ids (persisted in ReportFavourites and used as navigation parameters).</summary>
public static class ReportIds
{
    public const string SalesRegister = "sales-register";
    public const string SaleReturns = "sale-returns";
    public const string DoctorSales = "doctor-sales";
    public const string PurchaseRegister = "purchase-register";
    public const string PurchaseReturns = "purchase-returns";
    public const string Gstr1 = "gstr-1";
    public const string Gstr3b = "gstr-3b";
    public const string HsnSummary = "hsn-summary";
    public const string DayBook = "day-book";
    public const string CashBook = "cash-book";
    public const string ExpenseSummary = "expense-summary";
    public const string ProfitLoss = "profit-loss";
    public const string ItemProfit = "item-profit";
    public const string BillProfit = "bill-profit";
    public const string StockValuation = "stock-valuation";
    public const string ExpiryBySupplier = "expiry-by-supplier";
    public const string StockMovement = "stock-movement";
    public const string H1Register = "h1-register";
    public const string NarcoticRegister = "narcotic-register";
    public const string CustomerOutstanding = "customer-outstanding";
    public const string SupplierOutstanding = "supplier-outstanding";
}

/// <summary>Choice keys and option keys used by the reports.</summary>
public static class ReportChoiceKeys
{
    public const string Status = "status";
    public const string GroupBy = "group";
    public const string Basis = "basis";
    public const string Schedule = "schedule";
    public const string Movement = "movement";
    public const string ExpiryState = "expiry";

    public const string All = "all";
    public const string Active = "active";
    public const string Cancelled = "cancelled";
    public const string Unpaid = "unpaid";
    public const string Batch = "batch";
    public const string Item = "item";
    public const string Supplier = "supplier";
    public const string IncludeExpired = "with-expired";
    public const string ExcludeExpired = "without-expired";
    public const string Expired = "expired";
    public const string NearExpiry = "near";
    public const string Fast = "fast";
    public const string Slow = "slow";
    public const string NonMoving = "non";
    public const string ScheduleXAndNarcotic = "x-nrx";
    public const string ScheduleX = "x";
    public const string Narcotic = "nrx";
}

/// <summary>Every report the app offers, in catalogue order.</summary>
public static class ReportCatalog
{
    private static ReportChoice DocStatus(string label = "Status") => new(ReportChoiceKeys.Status, label, new[]
    {
        new ReportChoiceOption(ReportChoiceKeys.Active, "Active only"),
        new ReportChoiceOption(ReportChoiceKeys.Cancelled, "Cancelled only"),
        new ReportChoiceOption(ReportChoiceKeys.All, "All")
    }, ReportChoiceKeys.Active);

    public static IReadOnlyList<ReportDefinition> All { get; } = new[]
    {
        // ─── Sales ───
        new ReportDefinition(ReportIds.SalesRegister, "Sales Register", ReportCategory.Sales,
            "Bill-wise sales with taxable value, CGST / SGST / IGST, round-off, paid and due.",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth, DocStatus()),
            new[] { "invoice", "bill", "sale book", "gst" }),
        new ReportDefinition(ReportIds.SaleReturns, "Sale Returns Register", ReportCategory.Sales,
            "Sale returns and credit notes with GST reversed, refunded and open credit.",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth, DocStatus()),
            new[] { "credit note", "return", "refund" }),
        new ReportDefinition(ReportIds.DoctorSales, "Doctor-wise Sales", ReportCategory.Sales,
            "Bills, patients, sales value and profit per prescribing doctor.",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth),
            new[] { "prescriber", "doctor", "referral", "commission" }),

        // ─── Purchases ───
        new ReportDefinition(ReportIds.PurchaseRegister, "Purchase Register", ReportCategory.Purchases,
            "Confirmed purchase bills with taxable value, input GST, other charges, paid and balance.",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth, new ReportChoice(ReportChoiceKeys.Status, "Payment", new[]
            {
                new ReportChoiceOption(ReportChoiceKeys.All, "All bills"),
                new ReportChoiceOption(ReportChoiceKeys.Unpaid, "With balance due")
            }, ReportChoiceKeys.All)),
            new[] { "purchase book", "distributor", "supplier", "itc" }),
        new ReportDefinition(ReportIds.PurchaseReturns, "Purchase Returns Register", ReportCategory.Purchases,
            "Purchase and expiry returns (debit notes) with GST and refund received.",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth, DocStatus()),
            new[] { "debit note", "expiry return", "breakage" }),

        // ─── GST ───
        new ReportDefinition(ReportIds.Gstr1, "GSTR-1", ReportCategory.Gst,
            "Outward supplies in GST portal format: B2B, B2C large, B2C small, credit notes, nil-rated, HSN and documents.",
            ReportParameterSpec.DateRange(ReportDatePreset.LastMonth),
            new[] { "gst return", "b2b", "b2cs", "b2cl", "cdnr", "hsn", "outward" }),
        new ReportDefinition(ReportIds.Gstr3b, "GSTR-3B Summary", ReportCategory.Gst,
            "Outward tax liability (3.1), inter-state B2C supplies (3.2), eligible ITC (4) and exempt inward supplies (5).",
            ReportParameterSpec.DateRange(ReportDatePreset.LastMonth),
            new[] { "gst return", "itc", "tax payable", "liability" }),
        new ReportDefinition(ReportIds.HsnSummary, "HSN-wise Summary", ReportCategory.Gst,
            "Sales (net of returns) and purchases grouped by HSN code and GST rate.",
            ReportParameterSpec.DateRange(ReportDatePreset.LastMonth),
            new[] { "hsn", "sac", "gst rate" }),

        // ─── Accounts ───
        new ReportDefinition(ReportIds.DayBook, "Day Book", ReportCategory.Accounts,
            "Every voucher of the period in date order: sales, returns, purchases, receipts, payments and expenses.",
            ReportParameterSpec.DateRange(ReportDatePreset.Today) with { MaxRangeDays = 92 },
            new[] { "daybook", "journal", "vouchers", "transactions" }),
        new ReportDefinition(ReportIds.CashBook, "Cash Book", ReportCategory.Accounts,
            "Cash received and paid with opening, running and closing cash balance.",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth),
            new[] { "cash in hand", "cash register", "galla" }),
        new ReportDefinition(ReportIds.ExpenseSummary, "Expense Summary", ReportCategory.Parties,
            "Expenses by category with GST, plus every expense voucher of the period.",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth),
            new[] { "expense", "overheads", "rent", "salary" }),

        // ─── Profit ───
        new ReportDefinition(ReportIds.ProfitLoss, "Profit & Loss Summary", ReportCategory.Profit,
            "Net sales, cost of goods sold, gross profit, expenses and net profit (all excluding GST).",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth),
            new[] { "p&l", "pnl", "income", "net profit" }),
        new ReportDefinition(ReportIds.ItemProfit, "Item-wise Profit", ReportCategory.Profit,
            "Units sold and returned, net sales, cost and margin per item.",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth),
            new[] { "margin", "product profit", "gross profit" }),
        new ReportDefinition(ReportIds.BillProfit, "Bill-wise Profit", ReportCategory.Profit,
            "Sales value, cost and margin of every bill (returns deducted).",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth),
            new[] { "margin", "invoice profit" }),

        // ─── Stock ───
        new ReportDefinition(ReportIds.StockValuation, "Stock Valuation", ReportCategory.Stock,
            "Closing stock valued at purchase rate and at MRP, batch-wise or item-wise.",
            ReportParameterSpec.AsOf(
                new ReportChoice(ReportChoiceKeys.GroupBy, "Show", new[]
                {
                    new ReportChoiceOption(ReportChoiceKeys.Batch, "Batch-wise"),
                    new ReportChoiceOption(ReportChoiceKeys.Item, "Item-wise")
                }, ReportChoiceKeys.Batch),
                new ReportChoice(ReportChoiceKeys.ExpiryState, "Expired stock", new[]
                {
                    new ReportChoiceOption(ReportChoiceKeys.IncludeExpired, "Include expired"),
                    new ReportChoiceOption(ReportChoiceKeys.ExcludeExpired, "Exclude expired")
                }, ReportChoiceKeys.IncludeExpired)),
            new[] { "closing stock", "inventory value", "stock value", "mrp value" }),
        new ReportDefinition(ReportIds.ExpiryBySupplier, "Near-expiry & Expired by Supplier", ReportCategory.Stock,
            "Batches in stock that have expired or expire soon, grouped by the supplier to return them to.",
            ReportParameterSpec.AsOf(new ReportChoice(ReportChoiceKeys.ExpiryState, "Batches", new[]
            {
                new ReportChoiceOption(ReportChoiceKeys.All, "Expired + near expiry"),
                new ReportChoiceOption(ReportChoiceKeys.Expired, "Expired only"),
                new ReportChoiceOption(ReportChoiceKeys.NearExpiry, "Near expiry only")
            }, ReportChoiceKeys.All)) with { Number = new ReportNumberInput("Expiring within (days)", 90, 1, 730) },
            new[] { "expiry", "short expiry", "breakage", "return to distributor" }),
        new ReportDefinition(ReportIds.StockMovement, "Fast / Slow / Non-moving Items", ReportCategory.Stock,
            "Items classified by sales in the period: fast (top 80 % of sales value), slow, and non-moving stock.",
            ReportParameterSpec.DateRange(ReportDatePreset.Last90Days, new ReportChoice(ReportChoiceKeys.Movement, "Class", new[]
            {
                new ReportChoiceOption(ReportChoiceKeys.All, "All"),
                new ReportChoiceOption(ReportChoiceKeys.Fast, "Fast-moving"),
                new ReportChoiceOption(ReportChoiceKeys.Slow, "Slow-moving"),
                new ReportChoiceOption(ReportChoiceKeys.NonMoving, "Non-moving")
            }, ReportChoiceKeys.All)),
            new[] { "fsn", "dead stock", "slow moving", "fast moving", "non moving" }),

        // ─── Drug registers ───
        new ReportDefinition(ReportIds.H1Register, "Schedule H1 Register", ReportCategory.Compliance,
            "Statutory register of Schedule H1 drugs supplied: date, patient, prescriber, drug, batch and quantity.",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth),
            new[] { "h1", "antibiotic", "drug register", "prescription register" }),
        new ReportDefinition(ReportIds.NarcoticRegister, "Narcotic & Schedule X Register", ReportCategory.Compliance,
            "Receipts and supplies of Schedule X and narcotic (NRx) drugs with running balance per item.",
            ReportParameterSpec.DateRange(ReportDatePreset.ThisMonth, new ReportChoice(ReportChoiceKeys.Schedule, "Schedule", new[]
            {
                new ReportChoiceOption(ReportChoiceKeys.ScheduleXAndNarcotic, "Schedule X + Narcotic"),
                new ReportChoiceOption(ReportChoiceKeys.ScheduleX, "Schedule X only"),
                new ReportChoiceOption(ReportChoiceKeys.Narcotic, "Narcotic (NRx) only")
            }, ReportChoiceKeys.ScheduleXAndNarcotic)),
            new[] { "ndps", "schedule x", "psychotropic", "drug register" }),

        // ─── Parties ───
        new ReportDefinition(ReportIds.CustomerOutstanding, "Customer Outstanding", ReportCategory.Parties,
            "Amount receivable from each customer today, with ageing (0–30, 31–60, 61–90, over 90 days).",
            new ReportParameterSpec(),
            new[] { "receivable", "dues", "credit", "udhar", "ageing" }),
        new ReportDefinition(ReportIds.SupplierOutstanding, "Supplier Outstanding", ReportCategory.Parties,
            "Amount payable to each supplier today, with ageing and the oldest unpaid bill.",
            new ReportParameterSpec(),
            new[] { "payable", "dues", "distributor", "ageing" }),
    };

    public static ReportDefinition? Find(string? id) =>
        id == null ? null : All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public static ReportDefinition Get(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"Unknown report '{id}'.");

    /// <summary>Reports in <paramref name="category"/> (null = all) matching <paramref name="text"/>, catalogue order.</summary>
    public static IReadOnlyList<ReportDefinition> Search(string? text, ReportCategory? category = null) =>
        All.Where(d => (category == null || d.Category == category) && d.Matches(text)).ToList();
}
