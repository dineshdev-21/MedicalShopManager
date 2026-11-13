using MedicalShopManager.Core.Enums;

namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>Stock of a scheduled item before the register period (units).</summary>
public sealed record DrugRegisterOpening(long MedicineId, string MedicineName, DrugSchedule Schedule, long OpeningUnits);

/// <summary>One receipt / supply of a scheduled item (a StockMovements row enriched with its document).</summary>
public sealed record DrugRegisterEntry
{
    public long MedicineId { get; init; }
    public string MedicineName { get; init; } = string.Empty;
    public DrugSchedule Schedule { get; init; }
    public DateTime Date { get; init; }
    public string Particulars { get; init; } = string.Empty;
    public string? VoucherNumber { get; init; }
    public string? PartyName { get; init; }
    public string? PartyAddress { get; init; }
    public string? Prescriber { get; init; }
    public string? PrescriberRegNo { get; init; }
    public string? BatchNumber { get; init; }
    public long QuantityIn { get; init; }
    public long QuantityOut { get; init; }
    public long SortKey { get; init; }
}

/// <summary>
/// Narcotic / Schedule X register (Drugs Rules 65(10), NDPS stock register): per item an opening balance, every
/// receipt and supply in date order with the running balance, and a closing balance row.
/// </summary>
public static class DrugRegisterBuilder
{
    public static readonly ReportColumn[] Columns =
    {
        ReportColumn.Date("Date"), ReportColumn.Text("Item", 2), ReportColumn.Text("Schedule", 0.6), ReportColumn.Text("Batch", 0.8),
        ReportColumn.Text("Particulars", 1.2), ReportColumn.Text("Voucher no."), ReportColumn.Text("Patient / supplier", 1.5),
        ReportColumn.Text("Address", 1.5), ReportColumn.Text("Prescriber", 1.3), ReportColumn.Text("Prescriber reg. no.", 0.9),
        ReportColumn.Qty("Qty in"), ReportColumn.Qty("Qty out"), ReportColumn.Qty("Balance", total: false)
    };

    public static ReportTable Build(string name, DateTime from, IReadOnlyList<DrugRegisterOpening> openings, IReadOnlyList<DrugRegisterEntry> entries)
    {
        var rows = new List<ReportRow>();
        var openingById = openings.ToDictionary(o => o.MedicineId);
        var ids = openings.Where(o => o.OpeningUnits != 0).Select(o => o.MedicineId)
            .Concat(entries.Select(e => e.MedicineId)).Distinct();
        var items = ids.Select(id =>
        {
            var first = entries.FirstOrDefault(e => e.MedicineId == id);
            openingById.TryGetValue(id, out var opening);
            return (Id: id, Name: opening?.MedicineName ?? first?.MedicineName ?? $"#{id}",
                Schedule: opening?.Schedule ?? first?.Schedule ?? DrugSchedule.None, Opening: opening?.OpeningUnits ?? 0);
        }).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id);

        foreach (var item in items)
        {
            var balance = item.Opening;
            var schedule = item.Schedule.ToLabel();
            rows.Add(ReportRow.Opening(from.Date, item.Name, schedule, null, "Opening balance", null, null, null, null, null, null, null, balance));
            long totalIn = 0, totalOut = 0;
            foreach (var e in entries.Where(e => e.MedicineId == item.Id).OrderBy(e => e.Date).ThenBy(e => e.SortKey))
            {
                balance += e.QuantityIn - e.QuantityOut;
                totalIn += e.QuantityIn;
                totalOut += e.QuantityOut;
                rows.Add(ReportRow.Of(e.Date.Date, item.Name, schedule, e.BatchNumber, e.Particulars, e.VoucherNumber, e.PartyName,
                    e.PartyAddress, e.Prescriber, e.PrescriberRegNo, e.QuantityIn == 0 ? null : e.QuantityIn,
                    e.QuantityOut == 0 ? null : e.QuantityOut, balance));
            }
            rows.Add(ReportRow.Subtotal(null, item.Name, schedule, null, "Closing balance", null, null, null, null, null, totalIn, totalOut, balance));
        }
        return new ReportTable(name, Columns, rows).EnsureValid();
    }
}
