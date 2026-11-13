using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>
/// Profit &amp; loss summary, item-wise and bill-wise profit. Profit is on values excluding GST: net sales = taxable
/// value of active bills − taxable value of active returns; cost = the cost recorded on each bill line at billing
/// (SaleItems.CogsPaisa) − the cost share of returned units (<see cref="ReportMath.ReturnedCost"/>).
/// </summary>
public sealed class ProfitReportsProvider : ReportProviderBase
{
    public ProfitReportsProvider(DatabaseManager db) : base(db) { }

    public override IReadOnlyCollection<string> ReportIds { get; } = new[]
    {
        Core.Modules.Reports.ReportIds.ProfitLoss, Core.Modules.Reports.ReportIds.ItemProfit, Core.Modules.Reports.ReportIds.BillProfit
    };

    public override Task<ReportResult> RunAsync(ReportDefinition definition, ReportParameters parameters, CancellationToken cancellationToken = default) =>
        definition.Id switch
        {
            Core.Modules.Reports.ReportIds.ProfitLoss => ProfitLossAsync(definition, parameters, cancellationToken),
            Core.Modules.Reports.ReportIds.ItemProfit => ItemProfitAsync(definition, parameters, cancellationToken),
            _ => BillProfitAsync(definition, parameters, cancellationToken)
        };

    /// <summary>A returned line with what is needed to cost it.</summary>
    private sealed record ReturnedLine(long MedicineId, string MedicineName, long? SaleId, long Units, long TaxablePaisa, long CostPaisa);

    /// <summary>Active return lines; <paramref name="byReturnDate"/> = returns dated in the period, else returns against <paramref name="saleIds"/>' bills.</summary>
    private Task<List<ReturnedLine>> ReturnedLinesAsync(ReportParameters p, CancellationToken ct, bool byReturnDate = true) =>
        ReportSql.QueryAsync(Db, $@"
            SELECT i.MedicineId, i.MedicineName, r.SaleId, i.Units, i.TaxablePaisa,
                   si.Quantity AS SoldUnits, si.CogsPaisa AS SoldCost, b.PurchaseRatePaisa, b.UnitsPerPack AS BatchPack
            FROM SaleReturnItems i
            JOIN SaleReturns r ON r.Id = i.SaleReturnId
            LEFT JOIN SaleItems si ON si.Id = i.SaleItemId
            LEFT JOIN MedicineBatches b ON b.Id = i.BatchId
            WHERE r.Status = {ReturnActive} AND {(byReturnDate
                ? "r.ReturnDate >= @From AND r.ReturnDate < @ToEx"
                : $"r.SaleId IN (SELECT Id FROM Sales WHERE Status = {SaleActive} AND InvoiceDate >= @From AND InvoiceDate < @ToEx)")}",
            r => new ReturnedLine(r.Long("MedicineId"), r.Text("MedicineName"), r.LongOrNull("SaleId"), r.Long("Units"), r.Long("TaxablePaisa"),
                ReportMath.ReturnedCost(r.Long("SoldCost"), r.Long("SoldUnits"), r.Long("Units"), r.Long("PurchaseRatePaisa"),
                    (int)Math.Max(r.Long("BatchPack"), 1))),
            ct, ReportSql.Range(p));

    private async Task<ReportResult> ProfitLossAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var sales = (await ReportSql.QueryAsync(Db, $@"
            SELECT COALESCE(SUM(si.TaxableValuePaisa), 0) AS Taxable, COALESCE(SUM(si.CogsPaisa), 0) AS Cost
            FROM SaleItems si JOIN Sales s ON s.Id = si.SaleId
            WHERE s.Status = {SaleActive} AND s.InvoiceDate >= @From AND s.InvoiceDate < @ToEx",
            r => (Taxable: r.Long("Taxable"), Cost: r.Long("Cost")), ct, ReportSql.Range(p))).Single();
        var returns = await ReturnedLinesAsync(p, ct);
        var expenses = await ReportSql.QueryAsync(Db, @"
            SELECT COALESCE(c.Name, '') AS CategoryName, e.Category,
                   SUM(CASE WHEN e.TaxablePaisa > 0 THEN e.TaxablePaisa ELSE e.AmountPaisa - (e.CgstPaisa + e.SgstPaisa + e.IgstPaisa) END) AS Amount
            FROM Expenses e LEFT JOIN FinanceExpenseCategories c ON c.Id = e.CategoryId
            WHERE e.IsCancelled = 0 AND e.ExpenseDate >= @From AND e.ExpenseDate < @ToEx
            GROUP BY e.CategoryId, e.Category ORDER BY Amount DESC",
            r => (Category: Blank(r.Str("CategoryName")) ?? ((Core.Enums.ExpenseCategory)r.Long("Category")).ToString(), Amount: r.Long("Amount")),
            ct, ReportSql.Range(p));

        var returnedTaxable = returns.Sum(x => x.TaxablePaisa);
        var returnedCost = returns.Sum(x => x.CostPaisa);
        var netSales = sales.Taxable - returnedTaxable;
        var netCost = sales.Cost - returnedCost;
        var gross = netSales - netCost;
        var expenseTotal = expenses.Sum(e => e.Amount);
        var net = gross - expenseTotal;

        var statement = new ReportTable("Statement", new[] { ReportColumn.Text("Particulars", 3), ReportColumn.Money("Amount", total: false) }, new[]
        {
            ReportRow.Of("Sales (excluding GST)", sales.Taxable),
            ReportRow.Of("Less: sale returns (excluding GST)", -returnedTaxable),
            ReportRow.Subtotal("Net sales", netSales),
            ReportRow.Of("Cost of goods sold", sales.Cost),
            ReportRow.Of("Less: cost of goods returned", -returnedCost),
            ReportRow.Subtotal("Net cost of goods sold", netCost),
            ReportRow.Subtotal("Gross profit", gross),
            ReportRow.Of("Expenses (excluding GST)", -expenseTotal),
            ReportRow.Subtotal("Net profit", net)
        }) { ShowTotals = false };
        var expenseSheet = new ReportTable("Expenses", new[] { ReportColumn.Text("Category", 2), ReportColumn.Money("Amount") },
            expenses.Select(e => ReportRow.Of(e.Category, e.Amount)).ToList());

        return Result(d, p, new[] { statement, expenseSheet }, new[]
        {
            new ReportStat("Net sales", netSales),
            new ReportStat("Gross profit", gross),
            new ReportStat("Gross margin", ReportMath.MarginPercent(gross, netSales), ReportColumnKind.Percent),
            new ReportStat("Net profit", net)
        }, new[] { "Cost is the purchase cost recorded on each bill line when it was sold. Stock write-offs and adjustments are not included." });
    }

    private async Task<ReportResult> ItemProfitAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var sold = await ReportSql.QueryAsync(Db, $@"
            SELECT si.MedicineId, MAX(si.MedicineName) AS Name, MAX(COALESCE(m.Manufacturer, si.Manufacturer)) AS Manufacturer,
                   SUM(si.Quantity) AS Units, SUM(si.TaxableValuePaisa) AS Taxable, SUM(si.CogsPaisa) AS Cost
            FROM SaleItems si JOIN Sales s ON s.Id = si.SaleId LEFT JOIN Medicines m ON m.Id = si.MedicineId
            WHERE s.Status = {SaleActive} AND s.InvoiceDate >= @From AND s.InvoiceDate < @ToEx
            GROUP BY si.MedicineId",
            r => (Id: r.Long("MedicineId"), Name: r.Text("Name"), Manufacturer: Blank(r.Str("Manufacturer")), Units: r.Long("Units"),
                Taxable: r.Long("Taxable"), Cost: r.Long("Cost")), ct, ReportSql.Range(p));
        var returns = (await ReturnedLinesAsync(p, ct)).GroupBy(x => x.MedicineId)
            .ToDictionary(g => g.Key, g => (Name: g.First().MedicineName, Units: g.Sum(x => x.Units), Taxable: g.Sum(x => x.TaxablePaisa), Cost: g.Sum(x => x.CostPaisa)));

        var ids = sold.Select(s => s.Id).Union(returns.Keys);
        var soldById = sold.ToDictionary(s => s.Id);
        var rows = ids.Select(id =>
        {
            soldById.TryGetValue(id, out var s);
            returns.TryGetValue(id, out var r);
            var netSales = s.Taxable - r.Taxable;
            var profit = netSales - (s.Cost - r.Cost);
            return (Name: s.Name ?? r.Name ?? $"#{id}", Row: ReportRow.Of(s.Name ?? r.Name ?? $"#{id}", s.Manufacturer, s.Units, r.Units, s.Units - r.Units,
                netSales, s.Cost - r.Cost, profit, ReportMath.MarginPercent(profit, netSales)), Profit: profit);
        }).OrderByDescending(x => x.Profit).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.Row).ToList();

        var table = new ReportTable("Item-wise profit", new[]
        {
            ReportColumn.Text("Item", 2.2), ReportColumn.Text("Manufacturer", 1.4), ReportColumn.Qty("Units sold"), ReportColumn.Qty("Units returned"),
            ReportColumn.Qty("Net units"), ReportColumn.Money("Net sales"), ReportColumn.Money("Cost"), ReportColumn.Money("Profit"), ReportColumn.Percent("Margin %")
        }, rows);
        var netTotal = table.Total("Net sales");
        var profitTotal = table.Total("Profit");
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Items", (long)rows.Count, ReportColumnKind.Integer),
            new ReportStat("Net sales", netTotal),
            new ReportStat("Profit", profitTotal),
            new ReportStat("Margin", ReportMath.MarginPercent(profitTotal, netTotal), ReportColumnKind.Percent)
        }, new[] { "Values exclude GST. Returns dated in the period are deducted from the item they were made for." });
    }

    private async Task<ReportResult> BillProfitAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var bills = await ReportSql.QueryAsync(Db, $@"
            SELECT s.Id, s.InvoiceDate, s.InvoiceNumber, s.CustomerName, s.GrandTotalPaisa,
                   COUNT(si.Id) AS Items, COALESCE(SUM(si.TaxableValuePaisa), 0) AS Taxable, COALESCE(SUM(si.CogsPaisa), 0) AS Cost
            FROM Sales s LEFT JOIN SaleItems si ON si.SaleId = s.Id
            WHERE s.Status = {SaleActive} AND s.InvoiceDate >= @From AND s.InvoiceDate < @ToEx
            GROUP BY s.Id ORDER BY s.InvoiceDate, s.Id",
            r => (Id: r.Long("Id"), Date: r.Day("InvoiceDate"), Number: r.Text("InvoiceNumber"), Customer: ReportSql.PartyOrWalkIn(r.Str("CustomerName")),
                Value: r.Long("GrandTotalPaisa"), Items: r.Long("Items"), Taxable: r.Long("Taxable"), Cost: r.Long("Cost")), ct, ReportSql.Range(p));
        var returns = (await ReturnedLinesAsync(p, ct, byReturnDate: false)).Where(x => x.SaleId != null)
            .GroupBy(x => x.SaleId!.Value).ToDictionary(g => g.Key, g => (Taxable: g.Sum(x => x.TaxablePaisa), Cost: g.Sum(x => x.CostPaisa)));

        var rows = bills.Select(b =>
        {
            returns.TryGetValue(b.Id, out var r);
            var netSales = b.Taxable - r.Taxable;
            var cost = b.Cost - r.Cost;
            var profit = netSales - cost;
            return ReportRow.Of(b.Date, b.Number, b.Customer, b.Items, b.Value, b.Taxable, r.Taxable, netSales, cost, profit,
                ReportMath.MarginPercent(profit, netSales));
        }).ToList();

        var table = new ReportTable("Bill-wise profit", new[]
        {
            ReportColumn.Date("Date"), ReportColumn.Text("Bill no."), ReportColumn.Text("Customer", 1.8), ReportColumn.Count("Items"),
            ReportColumn.Money("Bill amount"), ReportColumn.Money("Taxable"), ReportColumn.Money("Returned"), ReportColumn.Money("Net sales"),
            ReportColumn.Money("Cost"), ReportColumn.Money("Profit"), ReportColumn.Percent("Margin %")
        }, rows);
        var netTotal = table.Total("Net sales");
        var profitTotal = table.Total("Profit");
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Bills", (long)rows.Count, ReportColumnKind.Integer),
            new ReportStat("Net sales", netTotal),
            new ReportStat("Profit", profitTotal),
            new ReportStat("Margin", ReportMath.MarginPercent(profitTotal, netTotal), ReportColumnKind.Percent)
        }, new[] { "Values exclude GST. Every active return made against a bill is deducted from that bill, whatever its date." });
    }
}
