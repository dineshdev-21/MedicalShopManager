using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>
/// Stock valuation, near-expiry / expired by supplier and fast / slow / non-moving items. Stock "as on" a past date is
/// the batch's current quantity minus the StockMovements booked after that date (the ledger every module writes).
/// Values use the batch's own per-pack purchase rate and MRP and its pack-size snapshot.
/// </summary>
public sealed class StockReportsProvider : ReportProviderBase
{
    public StockReportsProvider(DatabaseManager db) : base(db) { }

    public override IReadOnlyCollection<string> ReportIds { get; } = new[]
    {
        Core.Modules.Reports.ReportIds.StockValuation, Core.Modules.Reports.ReportIds.ExpiryBySupplier, Core.Modules.Reports.ReportIds.StockMovement
    };

    public override Task<ReportResult> RunAsync(ReportDefinition definition, ReportParameters parameters, CancellationToken cancellationToken = default) =>
        definition.Id switch
        {
            Core.Modules.Reports.ReportIds.StockValuation => ValuationAsync(definition, parameters, cancellationToken),
            Core.Modules.Reports.ReportIds.ExpiryBySupplier => ExpiryAsync(definition, parameters, cancellationToken),
            _ => MovementAsync(definition, parameters, cancellationToken)
        };

    private sealed record StockBatch(long BatchId, long MedicineId, string Name, string? Manufacturer, string BatchNumber, DateTime Expiry,
        int UnitsPerPack, int ItemUnitsPerPack, long PurchaseRate, long Mrp, decimal Gst, long Units, long? SupplierId, string? Supplier,
        string? SupplierPhone)
    {
        public long CostValue => ReportMath.StockValue(PurchaseRate, Units, UnitsPerPack);
        public long MrpValue => ReportMath.StockValue(Mrp, Units, UnitsPerPack);
    }

    /// <summary>Batches with stock on hand at the end of <paramref name="asOf"/>.</summary>
    private Task<List<StockBatch>> BatchesAsync(DateTime asOf, CancellationToken ct) => ReportSql.QueryAsync(Db, @"
        SELECT * FROM (
            SELECT b.Id AS BatchId, b.MedicineId, m.Name, m.Manufacturer, b.BatchNumber, b.ExpiryDate, b.UnitsPerPack, m.UnitsPerPack AS ItemPack,
                   b.PurchaseRatePaisa, b.MrpPaisa, b.GstRatePercent,
                   b.QuantityRemaining - COALESCE((SELECT SUM(sm.QuantityIn - sm.QuantityOut) FROM StockMovements sm
                                                   WHERE sm.BatchId = b.Id AND sm.MovementDate >= @After), 0) AS Units,
                   COALESCE(b.SupplierId, m.DefaultSupplierId) AS SupplierId, su.Name AS SupplierName, su.Phone AS SupplierPhone
            FROM MedicineBatches b
            JOIN Medicines m ON m.Id = b.MedicineId
            LEFT JOIN Suppliers su ON su.Id = COALESCE(b.SupplierId, m.DefaultSupplierId)
        ) WHERE Units > 0
        ORDER BY Name COLLATE NOCASE, ExpiryDate, BatchId",
        r => new StockBatch(r.Long("BatchId"), r.Long("MedicineId"), r.Text("Name"), Blank(r.Str("Manufacturer")), r.Text("BatchNumber"),
            r.Day("ExpiryDate"), (int)Math.Max(r.Long("UnitsPerPack"), 1), (int)Math.Max(r.Long("ItemPack"), 1), r.Long("PurchaseRatePaisa"),
            r.Long("MrpPaisa"), r.Percent("GstRatePercent"), r.Long("Units"), r.LongOrNull("SupplierId"), Blank(r.Str("SupplierName")),
            Blank(r.Str("SupplierPhone"))),
        ct, ("@After", ReportSql.Date(asOf.Date.AddDays(1))));

    private async Task<ReportResult> ValuationAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var batches = await BatchesAsync(p.AsOf, ct);
        if (p.Choice(d, ReportChoiceKeys.ExpiryState) == ReportChoiceKeys.ExcludeExpired)
            batches = batches.Where(b => !ReportExpiry.IsExpired(b.Expiry, p.AsOf)).ToList();

        ReportTable table;
        if (p.Choice(d, ReportChoiceKeys.GroupBy) == ReportChoiceKeys.Item)
        {
            var rows = batches.GroupBy(b => b.MedicineId).Select(g =>
            {
                var first = g.First();
                var units = g.Sum(b => b.Units);
                var cost = g.Sum(b => b.CostValue);
                var mrp = g.Sum(b => b.MrpValue);
                return ReportRow.Of(first.Name, first.Manufacturer, (long)g.Count(), units, ReportFormat.Quantity(units, first.ItemUnitsPerPack),
                    cost, mrp, mrp - cost);
            }).ToList();
            table = new ReportTable("Item-wise stock", new[]
            {
                ReportColumn.Text("Item", 2.2), ReportColumn.Text("Manufacturer", 1.4), ReportColumn.Count("Batches"), ReportColumn.Qty("Units"),
                ReportColumn.Text("Stock (packs + loose)", 0.9), ReportColumn.Money("Value @ purchase rate"), ReportColumn.Money("Value @ MRP"),
                ReportColumn.Money("MRP − cost")
            }, rows);
        }
        else
        {
            table = new ReportTable("Batch-wise stock", new[]
            {
                ReportColumn.Text("Item", 2.2), ReportColumn.Text("Manufacturer", 1.3), ReportColumn.Text("Batch", 0.9), ReportColumn.Date("Expiry"),
                ReportColumn.Count("Pack", total: false), ReportColumn.Qty("Units"), ReportColumn.Text("Stock (packs + loose)", 0.9),
                ReportColumn.Money("Purchase rate / pack", total: false), ReportColumn.Money("Value @ purchase rate"),
                ReportColumn.Money("MRP / pack", total: false), ReportColumn.Money("Value @ MRP"), ReportColumn.Percent("GST %")
            }, batches.Select(b => ReportRow.Of(b.Name, b.Manufacturer, b.BatchNumber, b.Expiry, (long)b.UnitsPerPack, b.Units,
                ReportFormat.Quantity(b.Units, b.UnitsPerPack), b.PurchaseRate, b.CostValue, b.Mrp, b.MrpValue, b.Gst)).ToList());
        }

        var expired = batches.Where(b => ReportExpiry.IsExpired(b.Expiry, p.AsOf)).Sum(b => b.CostValue);
        var notes = new List<string> { "Purchase rate is the batch's net purchase rate per pack (excluding GST)." };
        if (p.AsOf.Date < DateTime.Today)
            notes.Add("Stock on a past date is worked back from today's stock using the stock ledger; rates are the batches' current rates.");
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Batches", (long)batches.Count, ReportColumnKind.Integer),
            new ReportStat("Value @ purchase rate", batches.Sum(b => b.CostValue)),
            new ReportStat("Value @ MRP", batches.Sum(b => b.MrpValue)),
            new ReportStat("Expired stock (cost)", expired)
        }, notes);
    }

    private async Task<ReportResult> ExpiryAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var days = p.NumberOr(d);
        var filter = p.Choice(d, ReportChoiceKeys.ExpiryState);
        var batches = (await BatchesAsync(p.AsOf, ct))
            .Where(b => ReportExpiry.Matches(filter, b.Expiry, p.AsOf, days))
            .OrderBy(b => b.Supplier ?? "￿", StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Expiry).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        const string NoSupplier = "(supplier not recorded)";

        var detail = new ReportTable("Batches", new[]
        {
            ReportColumn.Text("Supplier", 1.8), ReportColumn.Text("Item", 2.2), ReportColumn.Text("Manufacturer", 1.2), ReportColumn.Text("Batch", 0.9),
            ReportColumn.Date("Expiry"), ReportColumn.Count("Days left", total: false), ReportColumn.Text("Status", 0.8), ReportColumn.Qty("Units"),
            ReportColumn.Text("Stock (packs + loose)", 0.9), ReportColumn.Money("Value @ purchase rate"), ReportColumn.Money("Value @ MRP")
        }, batches.Select(b => ReportRow.Of(b.Supplier ?? NoSupplier, b.Name, b.Manufacturer, b.BatchNumber, b.Expiry,
            ReportExpiry.DaysLeft(b.Expiry, p.AsOf), ReportExpiry.StatusLabel(b.Expiry, p.AsOf, days), b.Units,
            ReportFormat.Quantity(b.Units, b.UnitsPerPack), b.CostValue, b.MrpValue)).ToList());

        var bySupplier = new ReportTable("By supplier", new[]
        {
            ReportColumn.Text("Supplier", 2), ReportColumn.Text("Phone"), ReportColumn.Count("Batches"),
            ReportColumn.Money("Expired (cost)"), ReportColumn.Money("Near expiry (cost)"), ReportColumn.Money("Total @ purchase rate"),
            ReportColumn.Money("Total @ MRP")
        }, batches.GroupBy(b => b.Supplier ?? NoSupplier).Select(g => ReportRow.Of(g.Key, g.First().SupplierPhone, (long)g.Count(),
            g.Where(b => ReportExpiry.IsExpired(b.Expiry, p.AsOf)).Sum(b => b.CostValue),
            g.Where(b => !ReportExpiry.IsExpired(b.Expiry, p.AsOf)).Sum(b => b.CostValue),
            g.Sum(b => b.CostValue), g.Sum(b => b.MrpValue))).ToList());

        return Result(d, p, new[] { bySupplier, detail }, new[]
        {
            new ReportStat("Batches", (long)batches.Count, ReportColumnKind.Integer),
            new ReportStat("Expired (cost)", bySupplier.Total("Expired (cost)")),
            new ReportStat($"Expiring in {days} days (cost)", bySupplier.Total("Near expiry (cost)"))
        }, new[] { "Supplier is the one the batch was purchased from, else the item's default supplier. A batch is expired from its expiry date." });
    }

    private async Task<ReportResult> MovementAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var periodDays = (int)ReportMath.DaysBetween(p.From, p.To) + 1;
        var medicines = await ReportSql.QueryAsync(Db, "SELECT Id, Name, Manufacturer, UnitsPerPack FROM Medicines",
            r => (Id: r.Long("Id"), Name: r.Text("Name"), Manufacturer: Blank(r.Str("Manufacturer")), Pack: (int)Math.Max(r.Long("UnitsPerPack"), 1)), ct);
        var stock = (await BatchesAsync(p.To, ct)).GroupBy(b => b.MedicineId)
            .ToDictionary(g => g.Key, g => (Units: g.Sum(b => b.Units), Value: g.Sum(b => b.CostValue)));
        var sales = (await ReportSql.QueryAsync(Db, $@"
            SELECT si.MedicineId, SUM(si.Quantity) AS Units, SUM(si.TaxableValuePaisa) AS Taxable
            FROM SaleItems si JOIN Sales s ON s.Id = si.SaleId
            WHERE s.Status = {SaleActive} AND s.InvoiceDate >= @From AND s.InvoiceDate < @ToEx
            GROUP BY si.MedicineId",
            r => (Id: r.Long("MedicineId"), Units: r.Long("Units"), Value: r.Long("Taxable")), ct, ReportSql.Range(p))).ToDictionary(x => x.Id);
        var lastSale = (await ReportSql.QueryAsync(Db, $@"
            SELECT si.MedicineId, MAX(s.InvoiceDate) AS LastSale
            FROM SaleItems si JOIN Sales s ON s.Id = si.SaleId
            WHERE s.Status = {SaleActive} AND s.InvoiceDate < @ToEx
            GROUP BY si.MedicineId",
            r => (Id: r.Long("MedicineId"), Last: r.DayOrNull("LastSale")), ct, ReportSql.Range(p))).ToDictionary(x => x.Id, x => x.Last);

        var inputs = medicines.Select(m => new ItemMovementInput
        {
            MedicineId = m.Id, Name = m.Name,
            SalesValuePaisa = sales.TryGetValue(m.Id, out var s) ? s.Value : 0,
            UnitsSold = sales.TryGetValue(m.Id, out var s2) ? s2.Units : 0,
            StockUnits = stock.TryGetValue(m.Id, out var st) ? st.Units : 0
        }).ToList();
        var classes = MovementClassifier.Classify(inputs);
        var filter = p.Choice(d, ReportChoiceKeys.Movement);

        var rows = medicines.Where(m => classes.ContainsKey(m.Id) && MovementClassifier.Matches(filter, classes[m.Id]))
            .Select(m =>
            {
                var cls = classes[m.Id];
                sales.TryGetValue(m.Id, out var s);
                stock.TryGetValue(m.Id, out var st);
                lastSale.TryGetValue(m.Id, out var last);
                return (Class: cls, Value: s.Value, Name: m.Name, Row: ReportRow.Of(m.Name, m.Manufacturer, cls.ToLabel(), s.Units, s.Value, st.Units,
                    ReportFormat.Quantity(st.Units, m.Pack), st.Value, last, last is { } l ? ReportMath.DaysBetween(l, p.To) : null,
                    MovementClassifier.DaysOfCover(st.Units, s.Units, periodDays)));
            })
            .OrderBy(x => x.Class).ThenByDescending(x => x.Value).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Row).ToList();

        var table = new ReportTable("Item movement", new[]
        {
            ReportColumn.Text("Item", 2.2), ReportColumn.Text("Manufacturer", 1.3), ReportColumn.Text("Class", 0.8), ReportColumn.Qty("Units sold"),
            ReportColumn.Money("Sales (excl. GST)"), ReportColumn.Qty("Stock units"), ReportColumn.Text("Stock (packs + loose)", 0.9),
            ReportColumn.Money("Stock value (cost)"), ReportColumn.Date("Last sold"), ReportColumn.Count("Days since sale", total: false),
            ReportColumn.Count("Days of cover", total: false)
        }, rows);
        long ValueOf(MovementClass c) => medicines.Where(m => classes.TryGetValue(m.Id, out var x) && x == c)
            .Sum(m => stock.TryGetValue(m.Id, out var st) ? st.Value : 0);
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Fast-moving items", (long)classes.Count(c => c.Value == MovementClass.Fast), ReportColumnKind.Integer),
            new ReportStat("Slow-moving items", (long)classes.Count(c => c.Value == MovementClass.Slow), ReportColumnKind.Integer),
            new ReportStat("Non-moving items", (long)classes.Count(c => c.Value == MovementClass.NonMoving), ReportColumnKind.Integer),
            new ReportStat("Non-moving stock (cost)", ValueOf(MovementClass.NonMoving))
        }, new[] { "Fast = items making up the first 80 % of sales value in the period; slow = the rest that sold; non-moving = in stock but not sold in the period." });
    }
}
