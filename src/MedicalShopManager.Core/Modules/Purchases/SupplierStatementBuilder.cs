namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>
/// Builds a running-balance supplier statement from ledger entries (positive balance = the shop owes the distributor;
/// purchases credit the distributor, payments and debit notes debit it, refunds received credit it back).
/// Balances are derived from the source documents every time (lesson 7), never stored.
/// </summary>
public static class SupplierStatementBuilder
{
    public static SupplierStatement Build(Distributor supplier, IReadOnlyList<SupplierLedgerEntry> entries, DateTime from, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentNullException.ThrowIfNull(entries);
        if (to.Date < from.Date) throw new PurchaseRuleException("The statement end date is before its start date.");

        var ordered = entries.OrderBy(e => e.Date.Date).ThenBy(e => e.SortKey).ToList();
        var opening = supplier.OpeningBalancePaisa
            + ordered.Where(e => e.Date.Date < from.Date).Sum(e => e.CreditPaisa - e.DebitPaisa);

        var running = opening;
        var lines = new List<SupplierStatementLine>
        {
            new(from.Date, SupplierStatementEntryType.OpeningBalance, string.Empty, "Opening balance", 0, 0, opening)
        };
        foreach (var e in ordered.Where(e => e.Date.Date >= from.Date && e.Date.Date <= to.Date))
        {
            running += e.CreditPaisa - e.DebitPaisa;
            lines.Add(new SupplierStatementLine(e.Date, e.Type, e.DocumentNumber, e.Description, e.DebitPaisa, e.CreditPaisa, running));
        }

        return new SupplierStatement(supplier, from.Date, to.Date, opening, lines, running);
    }
}
