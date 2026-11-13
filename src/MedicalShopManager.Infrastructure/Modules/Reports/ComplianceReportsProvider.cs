using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Reports;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>
/// Statutory drug registers. Schedule H1 (Drugs Rules 65(3)): every supply with patient, prescriber, drug, batch and
/// quantity, from the schedule snapshot Billing stores on each bill line. Narcotic / Schedule X: a stock register per
/// item built from the StockMovements ledger (opening, receipts, supplies, returns, adjustments, running balance),
/// enriched with the bill / purchase / return each movement came from (Reference "Sale:{id}", "Purchase:{id}",
/// "SaleReturn:{id}", "DebitNote:{id}").
/// </summary>
public sealed class ComplianceReportsProvider : ReportProviderBase
{
    public ComplianceReportsProvider(DatabaseManager db) : base(db) { }

    public override IReadOnlyCollection<string> ReportIds { get; } =
        new[] { Core.Modules.Reports.ReportIds.H1Register, Core.Modules.Reports.ReportIds.NarcoticRegister };

    public override Task<ReportResult> RunAsync(ReportDefinition definition, ReportParameters parameters, CancellationToken cancellationToken = default) =>
        definition.Id == Core.Modules.Reports.ReportIds.H1Register
            ? H1RegisterAsync(definition, parameters, cancellationToken)
            : NarcoticRegisterAsync(definition, parameters, cancellationToken);

    private async Task<ReportResult> H1RegisterAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var entries = await ReportSql.QueryAsync(Db, $@"
            SELECT s.InvoiceDate, s.InvoiceNumber,
                   COALESCE(NULLIF(TRIM(s.PatientName), ''), NULLIF(TRIM(s.CustomerName), '')) AS Patient,
                   COALESCE(NULLIF(TRIM(s.PatientAddress), ''), cu.Address) AS PatientAddress,
                   COALESCE(NULLIF(TRIM(doc.Name), ''), NULLIF(TRIM(s.DoctorName), '')) AS Doctor,
                   COALESCE(NULLIF(TRIM(s.DoctorRegNo), ''), doc.RegistrationNo) AS RegNo, doc.Address AS DoctorAddress,
                   s.PrescriptionNumber, si.MedicineName, COALESCE(si.Manufacturer, m.Manufacturer) AS Manufacturer, si.BatchNumber,
                   si.ExpiryDate, si.Quantity, si.UnitsPerPack
            FROM SaleItems si
            JOIN Sales s ON s.Id = si.SaleId
            LEFT JOIN Customers cu ON cu.Id = s.CustomerId
            LEFT JOIN Doctors doc ON doc.Id = s.DoctorId
            LEFT JOIN Medicines m ON m.Id = si.MedicineId
            WHERE s.Status = {SaleActive} AND si.Schedule = {(int)DrugSchedule.H1}
              AND s.InvoiceDate >= @From AND s.InvoiceDate < @ToEx
            ORDER BY s.InvoiceDate, s.Id, si.Id",
            r => (Date: r.Day("InvoiceDate"), Bill: r.Text("InvoiceNumber"), Patient: Blank(r.Str("Patient")), Address: Blank(r.Str("PatientAddress")),
                Doctor: Blank(r.Str("Doctor")), RegNo: Blank(r.Str("RegNo")), DoctorAddress: Blank(r.Str("DoctorAddress")),
                Rx: Blank(r.Str("PrescriptionNumber")), Drug: r.Text("MedicineName"), Manufacturer: Blank(r.Str("Manufacturer")),
                Batch: r.Text("BatchNumber"), Expiry: r.DayOrNull("ExpiryDate"), Units: r.Long("Quantity"),
                Pack: (int)Math.Max(r.Long("UnitsPerPack"), 1)),
            ct, ReportSql.Range(p));

        var rows = entries.Select((e, i) => ReportRow.Of((long)(i + 1), e.Date, e.Bill, e.Patient, e.Address, e.Doctor, e.RegNo, e.DoctorAddress,
            e.Rx, e.Drug, e.Manufacturer, e.Batch, e.Expiry, e.Units, ReportFormat.Quantity(e.Units, e.Pack))).ToList();
        var table = new ReportTable("Schedule H1 register", new[]
        {
            ReportColumn.Count("Sr. no.", total: false), ReportColumn.Date("Date"), ReportColumn.Text("Bill no."), ReportColumn.Text("Patient name", 1.5),
            ReportColumn.Text("Patient address", 1.6), ReportColumn.Text("Prescriber", 1.4), ReportColumn.Text("Prescriber reg. no.", 0.9),
            ReportColumn.Text("Prescriber address", 1.4), ReportColumn.Text("Rx no.", 0.8), ReportColumn.Text("Drug name", 2),
            ReportColumn.Text("Manufacturer", 1.2), ReportColumn.Text("Batch", 0.8), ReportColumn.Date("Expiry"), ReportColumn.Qty("Qty supplied (units)"),
            ReportColumn.Text("Qty (packs + loose)", 0.8)
        }, rows) { ShowTotals = false };

        var notes = new List<string> { "Keep this register for three years (Drugs Rules 65(3)(c)). Quantity is in units; packs + loose shown alongside." };
        var missing = entries.Count(e => e.Patient == null || e.Doctor == null);
        if (missing > 0)
            notes.Add($"{missing} entr{(missing == 1 ? "y has" : "ies have")} no patient or prescriber name — complete them on the bill (the register requires both).");
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Entries", (long)entries.Count, ReportColumnKind.Integer),
            new ReportStat("Bills", (long)entries.Select(e => e.Bill).Distinct().Count(), ReportColumnKind.Integer),
            new ReportStat("Incomplete entries", (long)missing, ReportColumnKind.Integer)
        }, notes);
    }

    private static string ScheduleList(string choice) => choice switch
    {
        ReportChoiceKeys.ScheduleX => $"({(int)DrugSchedule.X})",
        ReportChoiceKeys.Narcotic => $"({(int)DrugSchedule.NRx})",
        _ => $"({(int)DrugSchedule.X}, {(int)DrugSchedule.NRx})"
    };

    public static string Particulars(MovementType type, long quantityIn) => type switch
    {
        MovementType.PurchaseReceipt => "Received (purchase)",
        MovementType.SaleDispatch => "Supplied (sale)",
        MovementType.SaleReturn => quantityIn > 0 ? "Returned by patient" : "Return cancelled",
        MovementType.PurchaseReturn => "Returned to supplier",
        MovementType.Damaged => "Damaged / destroyed",
        MovementType.Expired => "Expired",
        MovementType.WriteOff => "Written off",
        MovementType.OpeningStock => "Opening stock",
        MovementType.StockAdjustment => "Stock adjustment",
        _ => "Other"
    };

    private async Task<ReportResult> NarcoticRegisterAsync(ReportDefinition d, ReportParameters p, CancellationToken ct)
    {
        var schedules = ScheduleList(p.Choice(d, ReportChoiceKeys.Schedule));
        var openings = await ReportSql.QueryAsync(Db, $@"
            SELECT m.Id, m.Name, m.Schedule,
                   COALESCE((SELECT SUM(sm.QuantityIn - sm.QuantityOut) FROM StockMovements sm
                             WHERE sm.MedicineId = m.Id AND sm.MovementDate < @From), 0) AS Opening
            FROM Medicines m WHERE m.Schedule IN {schedules}",
            r => new DrugRegisterOpening(r.Long("Id"), r.Text("Name"), (DrugSchedule)r.Long("Schedule"), r.Long("Opening")),
            ct, ReportSql.Range(p));

        var entries = await ReportSql.QueryAsync(Db, $@"
            SELECT sm.Id, sm.MovementDate, sm.MedicineId, m.Name, m.Schedule, sm.BatchNumber, sm.MovementType, sm.QuantityIn, sm.QuantityOut,
                   sm.Reference, sm.Reason,
                   s.InvoiceNumber, COALESCE(NULLIF(TRIM(s.PatientName), ''), NULLIF(TRIM(s.CustomerName), '')) AS Patient,
                   COALESCE(NULLIF(TRIM(s.PatientAddress), ''), cu.Address) AS PatientAddress,
                   COALESCE(NULLIF(TRIM(doc.Name), ''), NULLIF(TRIM(s.DoctorName), '')) AS Doctor,
                   COALESCE(NULLIF(TRIM(s.DoctorRegNo), ''), doc.RegistrationNo) AS RegNo,
                   COALESCE(NULLIF(pu.SupplierInvoiceNumber, ''), pu.PurchaseNumber) AS PurchaseNo, pu.SupplierName, su.Address AS SupplierAddress,
                   sr.ReturnNumber, sr.CustomerName AS ReturnCustomer, pr.ReturnNumber AS DebitNote, pr.SupplierName AS DebitSupplier
            FROM StockMovements sm
            JOIN Medicines m ON m.Id = sm.MedicineId
            LEFT JOIN Sales s ON sm.Reference LIKE 'Sale:%' AND s.Id = CAST(substr(sm.Reference, 6) AS INTEGER)
            LEFT JOIN Customers cu ON cu.Id = s.CustomerId
            LEFT JOIN Doctors doc ON doc.Id = s.DoctorId
            LEFT JOIN Purchases pu ON sm.Reference LIKE 'Purchase:%' AND pu.Id = CAST(substr(sm.Reference, 10) AS INTEGER)
            LEFT JOIN Suppliers su ON su.Id = pu.SupplierId
            LEFT JOIN SaleReturns sr ON sm.Reference LIKE 'SaleReturn:%' AND sr.Id = CAST(substr(sm.Reference, 12) AS INTEGER)
            LEFT JOIN PurchaseReturns pr ON sm.Reference LIKE 'DebitNote:%' AND pr.Id = CAST(substr(sm.Reference, 11) AS INTEGER)
            WHERE m.Schedule IN {schedules} AND sm.MovementDate >= @From AND sm.MovementDate < @ToEx
            ORDER BY sm.MovementDate, sm.Id",
            r =>
            {
                var type = (MovementType)r.Long("MovementType");
                var quantityIn = r.Long("QuantityIn");
                return new DrugRegisterEntry
                {
                    MedicineId = r.Long("MedicineId"), MedicineName = r.Text("Name"), Schedule = (DrugSchedule)r.Long("Schedule"),
                    Date = r.Day("MovementDate"), Particulars = Particulars(type, quantityIn),
                    VoucherNumber = Blank(r.Str("InvoiceNumber")) ?? Blank(r.Str("PurchaseNo")) ?? Blank(r.Str("ReturnNumber"))
                                    ?? Blank(r.Str("DebitNote")) ?? Blank(r.Str("Reference")),
                    PartyName = Blank(r.Str("Patient")) ?? Blank(r.Str("SupplierName")) ?? Blank(r.Str("ReturnCustomer"))
                                ?? Blank(r.Str("DebitSupplier")) ?? Blank(r.Str("Reason")),
                    PartyAddress = Blank(r.Str("PatientAddress")) ?? Blank(r.Str("SupplierAddress")),
                    Prescriber = Blank(r.Str("Doctor")), PrescriberRegNo = Blank(r.Str("RegNo")),
                    BatchNumber = Blank(r.Str("BatchNumber")), QuantityIn = quantityIn, QuantityOut = r.Long("QuantityOut"), SortKey = r.Long("Id")
                };
            },
            ct, ReportSql.Range(p));

        var table = DrugRegisterBuilder.Build("Narcotic & Schedule X register", p.From, openings, entries) with { ShowTotals = false };
        var notes = new List<string> { "Quantities are in units. Balances come from the stock ledger; record every destruction or loss as a stock adjustment." };
        var noPrescriber = entries.Count(e => e.QuantityOut > 0 && e.Particulars.StartsWith("Supplied", StringComparison.Ordinal) && e.Prescriber == null);
        if (noPrescriber > 0)
            notes.Add($"{noPrescriber} suppl{(noPrescriber == 1 ? "y has" : "ies have")} no prescriber recorded.");
        return Result(d, p, new[] { table }, new[]
        {
            new ReportStat("Items", (long)table.Rows.Count(r => r.Style == ReportRowStyle.Opening), ReportColumnKind.Integer),
            new ReportStat("Received (units)", entries.Sum(e => e.QuantityIn), ReportColumnKind.Quantity),
            new ReportStat("Supplied (units)", entries.Sum(e => e.QuantityOut), ReportColumnKind.Quantity)
        }, notes);
    }
}
