using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Infrastructure.Database;
using static MedicalShopManager.Infrastructure.Modules.Finance.FinanceSql;

namespace MedicalShopManager.Infrastructure.Modules.Finance;

/// <summary>
/// Customers and suppliers as payment parties: search, open documents, receivable / payable ageing and party ledgers.
/// Reads Billing's Sales / Customers, SalesReturns' SaleReturns / SaleReturnRefunds and Purchases' Purchases /
/// Suppliers / PurchaseReturns; never writes them.
/// </summary>
public sealed class FinancePartyRepository : IFinancePartyRepository
{
    private readonly DatabaseManager _db;

    public FinancePartyRepository(DatabaseManager db) => _db = db;

    public async Task<IReadOnlyList<FinanceParty>> SearchAsync(PartyType partyType, string? text, int limit = 30)
    {
        using var conn = _db.CreateNewConnection();
        var rows = await LoadPartiesAsync(conn, null, partyType, null, text, Math.Clamp(limit, 1, 500));
        var dues = await LoadOpenBillsAsync(conn, null, partyType, null);
        return rows.Select(p => new FinanceParty(partyType, p.Id, p.Name, p.Phone, p.Gstin, NetDue(p, dues))).ToList();
    }

    public async Task<FinanceParty?> GetAsync(PartyType partyType, long partyId)
    {
        using var conn = _db.CreateNewConnection();
        var party = (await LoadPartiesAsync(conn, null, partyType, partyId, null, 1)).FirstOrDefault();
        if (party is null) return null;
        var dues = await LoadOpenBillsAsync(conn, null, partyType, partyId);
        return new FinanceParty(partyType, party.Id, party.Name, party.Phone, party.Gstin, NetDue(party, dues));
    }

    public async Task<IReadOnlyList<DueDocument>> GetDuesAsync(PartyType partyType, long partyId)
    {
        using var conn = _db.CreateNewConnection();
        return await LoadDuesAsync(conn, null, partyType, partyId);
    }

    /// <summary>Open documents of one party (opening balance first, then oldest bills), read on the caller's transaction.</summary>
    internal static async Task<IReadOnlyList<DueDocument>> LoadDuesAsync(SqliteConnection conn, SqliteTransaction? tx, PartyType partyType, long partyId)
    {
        RequireSupported(partyType);
        var party = (await LoadPartiesAsync(conn, tx, partyType, partyId, null, 1)).FirstOrDefault();
        if (party is null) return Array.Empty<DueDocument>();
        var dues = new List<DueDocument>();
        if (party.OpeningOutstanding > 0)
            dues.Add(new DueDocument
            {
                Target = AllocationTarget.OpeningBalance, DocumentId = 0, Number = "Opening balance", Date = party.OpeningDate,
                TotalPaisa = party.OpeningOutstanding, BalancePaisa = party.OpeningOutstanding
            });
        if ((await LoadOpenBillsAsync(conn, tx, partyType, partyId)).TryGetValue(partyId, out var bills)) dues.AddRange(bills);
        return FinanceRules.OldestFirst(dues).ToList();
    }

    public async Task<IReadOnlyList<PartyBalanceRow>> GetBalancesAsync(PartyType partyType, DateTime asOf, bool includeSettled = false)
    {
        RequireSupported(partyType);
        using var conn = _db.CreateNewConnection();
        var parties = await LoadPartiesAsync(conn, null, partyType, null, null, int.MaxValue);
        var bills = await LoadOpenBillsAsync(conn, null, partyType, null);
        var rows = new List<PartyBalanceRow>();
        foreach (var p in parties)
        {
            var dues = new List<DueDocument>();
            if (p.OpeningOutstanding > 0)
                dues.Add(new DueDocument { Target = AllocationTarget.OpeningBalance, Date = p.OpeningDate, BalancePaisa = p.OpeningOutstanding });
            if (bills.TryGetValue(p.Id, out var partyBills)) dues.AddRange(partyBills);
            var credits = p.Credits + Math.Max(0, -p.OpeningOutstanding);
            var row = new PartyBalanceRow
            {
                PartyType = partyType, PartyId = p.Id, Name = p.Name, Phone = p.Phone, Gstin = p.Gstin,
                Ageing = FinanceRules.Ageing(dues, asOf), CreditsPaisa = credits,
                OpenDocumentCount = dues.Count(d => d.Target != AllocationTarget.OpeningBalance),
                OldestDueDate = dues.Count == 0 ? null : dues.Min(d => d.Date)
            };
            if (includeSettled || row.Ageing.TotalPaisa != 0 || row.CreditsPaisa != 0) rows.Add(row);
        }
        return rows.OrderByDescending(r => r.NetDuePaisa).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ───────────────────────── Ledger ─────────────────────────

    public async Task<PartyLedger> GetLedgerAsync(PartyType partyType, long partyId, DateTime from, DateTime to)
    {
        RequireSupported(partyType);
        using var conn = _db.CreateNewConnection();
        var party = (await LoadPartiesAsync(conn, null, partyType, partyId, null, 1)).FirstOrDefault()
            ?? throw new FinanceValidationException(partyType == PartyType.Customer ? "The customer was not found." : "The supplier was not found.");

        var all = partyType == PartyType.Customer
            ? await CustomerLinesAsync(conn, partyId)
            : await SupplierLinesAsync(conn, partyId);

        var opening = all.Where(l => l.Date.Date < from.Date).Sum(l => l.DebitPaisa - l.CreditPaisa);
        var inRange = all.Where(l => l.Date.Date >= from.Date && l.Date.Date <= to.Date);
        return new PartyLedger
        {
            PartyType = partyType, PartyId = partyId, PartyName = party.Name, Phone = party.Phone, Gstin = party.Gstin,
            From = from.Date, To = to.Date, OpeningBalancePaisa = opening,
            Lines = FinanceRules.WithRunningBalance(opening, inRange)
        };
    }

    private static async Task<List<PartyLedgerLine>> CustomerLinesAsync(SqliteConnection conn, long id)
    {
        var lines = new List<PartyLedgerLine>();
        var sql = $@"
            SELECT COALESCE(c.OpeningBalanceDate, substr(c.CreatedAt, 1, 10)) AS D, {(int)PartyEntrySource.OpeningBalance} AS Src,
                   NULL AS Ref, 'Opening balance' AS Descr, c.OpeningBalancePaisa AS Amt, 0 AS SortKey
            FROM Customers c WHERE c.Id = @P AND c.OpeningBalancePaisa <> 0
            UNION ALL
            SELECT s.InvoiceDate, {(int)PartyEntrySource.Sale}, s.InvoiceNumber, 'Sale bill', s.GrandTotalPaisa, 100000000000 + s.Id
            FROM Sales s WHERE s.CustomerId = @P AND s.Status = {FinancePartySql.SaleActive}
            UNION ALL
            SELECT p.PaymentDate, {(int)PartyEntrySource.PaidWithBill}, s.InvoiceNumber, COALESCE(p.Notes, 'Paid with bill'), -p.AmountPaisa,
                   100000000000 + s.Id * 10 + 1
            FROM Payments p JOIN Sales s ON s.Id = p.TransactionId
            WHERE p.PaymentType = {(int)PaymentType.SalePayment} AND p.VoucherId IS NULL AND s.CustomerId = @P AND s.Status = {FinancePartySql.SaleActive}
            UNION ALL
            SELECT s.InvoiceDate, {(int)PartyEntrySource.PaidWithBill}, s.InvoiceNumber, 'Paid with bill',
                   -(s.AmountPaidPaisa - COALESCE((SELECT SUM(p.AmountPaisa) FROM Payments p
                                                   WHERE p.TransactionId = s.Id AND p.PaymentType = {(int)PaymentType.SalePayment}), 0)),
                   100000000000 + s.Id * 10 + 2
            FROM Sales s WHERE s.CustomerId = @P AND s.Status = {FinancePartySql.SaleActive}
              AND s.AmountPaidPaisa > COALESCE((SELECT SUM(p.AmountPaisa) FROM Payments p
                                                WHERE p.TransactionId = s.Id AND p.PaymentType = {(int)PaymentType.SalePayment}), 0)
            UNION ALL
            SELECT r.ReturnDate, {(int)PartyEntrySource.SaleReturn}, r.CreditNoteNumber,
                   'Credit note' || CASE WHEN r.SaleInvoiceNumber IS NULL THEN '' ELSE ' against ' || r.SaleInvoiceNumber END, -r.NetAmountPaisa,
                   200000000000 + r.Id
            FROM SaleReturns r WHERE r.CustomerId = @P AND r.Status = {FinancePartySql.SaleReturnActive}
            UNION ALL
            SELECT f.RefundDate, {(int)PartyEntrySource.Refund}, r.CreditNoteNumber, 'Refund of credit note', f.AmountPaisa, 300000000000 + f.Id
            FROM SaleReturnRefunds f JOIN SaleReturns r ON r.Id = f.SaleReturnId
            WHERE r.CustomerId = @P AND r.Status = {FinancePartySql.SaleReturnActive}
            {VoucherLinesSql(PartyType.Customer)}";
        await ReadLinesAsync(conn, sql, id, lines);
        return lines;
    }

    private static async Task<List<PartyLedgerLine>> SupplierLinesAsync(SqliteConnection conn, long id)
    {
        var lines = new List<PartyLedgerLine>();
        var supplierTypes = $"{(int)PaymentType.SupplierPayment}, {(int)PaymentType.PurchasePayment}";
        var sql = $@"
            SELECT substr(su.CreatedAt, 1, 10) AS D, {(int)PartyEntrySource.OpeningBalance} AS Src, NULL AS Ref, 'Opening balance' AS Descr,
                   -su.OpeningBalancePaisa AS Amt, 0 AS SortKey
            FROM Suppliers su WHERE su.Id = @P AND su.OpeningBalancePaisa <> 0
            UNION ALL
            SELECT pu.PurchaseDate, {(int)PartyEntrySource.Purchase}, COALESCE(NULLIF(pu.SupplierInvoiceNumber, ''), pu.PurchaseNumber),
                   'Purchase bill' || CASE WHEN pu.PurchaseNumber IS NULL THEN '' ELSE ' (' || pu.PurchaseNumber || ')' END,
                   -pu.GrandTotalPaisa, 100000000000 + pu.Id
            FROM Purchases pu WHERE pu.SupplierId = @P AND pu.Status = {FinancePartySql.PurchaseConfirmed}
            UNION ALL
            SELECT p.PaymentDate, {(int)PartyEntrySource.PaidWithBill}, COALESCE(NULLIF(pu.SupplierInvoiceNumber, ''), pu.PurchaseNumber),
                   COALESCE(p.Notes, 'Payment'), p.AmountPaisa, 100000000000 + pu.Id * 10 + 1
            FROM Payments p JOIN Purchases pu ON pu.Id = p.TransactionId
            WHERE p.PaymentType IN ({supplierTypes}) AND p.VoucherId IS NULL AND pu.SupplierId = @P AND pu.Status = {FinancePartySql.PurchaseConfirmed}
            UNION ALL
            SELECT pu.PurchaseDate, {(int)PartyEntrySource.PaidWithBill}, COALESCE(NULLIF(pu.SupplierInvoiceNumber, ''), pu.PurchaseNumber),
                   'Paid with bill',
                   pu.AmountPaidPaisa - COALESCE((SELECT SUM(p.AmountPaisa) FROM Payments p WHERE p.TransactionId = pu.Id AND p.PaymentType IN ({supplierTypes})), 0),
                   100000000000 + pu.Id * 10 + 2
            FROM Purchases pu WHERE pu.SupplierId = @P AND pu.Status = {FinancePartySql.PurchaseConfirmed}
              AND pu.AmountPaidPaisa > COALESCE((SELECT SUM(p.AmountPaisa) FROM Payments p WHERE p.TransactionId = pu.Id AND p.PaymentType IN ({supplierTypes})), 0)
            UNION ALL
            SELECT r.ReturnDate, {(int)PartyEntrySource.PurchaseReturn}, r.ReturnNumber, 'Debit note (purchase return)', r.NetAmountPaisa,
                   200000000000 + r.Id * 10
            FROM PurchaseReturns r WHERE r.SupplierId = @P AND r.IsCancelled = 0
            UNION ALL
            SELECT r.ReturnDate, {(int)PartyEntrySource.RefundReceived}, r.ReturnNumber, 'Refund received against debit note', -r.RefundReceivedPaisa,
                   200000000000 + r.Id * 10 + 1
            FROM PurchaseReturns r WHERE r.SupplierId = @P AND r.IsCancelled = 0 AND r.RefundReceivedPaisa > 0
            {VoucherLinesSql(PartyType.Supplier)}";
        await ReadLinesAsync(conn, sql, id, lines);
        return lines;
    }

    /// <summary>Receipts credit a customer (payments debit a supplier); a bounced cheque reverses the line on its bounce date.</summary>
    private static string VoucherLinesSql(PartyType partyType)
    {
        var receipt = partyType == PartyType.Customer;
        var sign = receipt ? "-" : "";
        var reverse = receipt ? "" : "-";
        var source = receipt ? PartyEntrySource.Receipt : PartyEntrySource.PaymentVoucher;
        return $@"
            UNION ALL
            SELECT v.VoucherDate, {(int)source}, v.VoucherNumber,
                   CASE v.Mode WHEN {(int)FinancePaymentMode.Cheque} THEN 'Cheque ' || COALESCE(ch.ChequeNumber, '') ELSE 'Payment' END
                       || CASE WHEN v.Reference IS NULL THEN '' ELSE ' Ref ' || v.Reference END,
                   {sign}v.AmountPaisa, 400000000000 + v.Id * 10
            FROM FinanceVouchers v LEFT JOIN FinanceCheques ch ON ch.Id = v.ChequeId
            WHERE v.PartyType = {(int)partyType} AND v.PartyId = @P AND v.Status IN ({(int)FinanceVoucherStatus.Active}, {(int)FinanceVoucherStatus.Bounced})
            UNION ALL
            SELECT substr(v.ReversedAt, 1, 10), {(int)PartyEntrySource.ChequeBounced}, v.VoucherNumber,
                   'Cheque ' || COALESCE(ch.ChequeNumber, '') || ' bounced' || CASE WHEN v.ReversalReason IS NULL THEN '' ELSE ': ' || v.ReversalReason END,
                   {reverse}v.AmountPaisa, 400000000000 + v.Id * 10 + 1
            FROM FinanceVouchers v LEFT JOIN FinanceCheques ch ON ch.Id = v.ChequeId
            WHERE v.PartyType = {(int)partyType} AND v.PartyId = @P AND v.Status = {(int)FinanceVoucherStatus.Bounced}";
    }

    /// <summary>Reads (D, Src, Ref, Descr, Amt, SortKey): Amt &gt; 0 is a debit (party owes more), &lt; 0 a credit.</summary>
    private static async Task ReadLinesAsync(SqliteConnection conn, string sql, long partyId, List<PartyLedgerLine> lines)
    {
        using var cmd = conn.Command(null, sql, ("@P", partyId));
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var amount = r.Long("Amt");
            if (amount == 0) continue;
            lines.Add(new PartyLedgerLine
            {
                Date = r.Date("D").Date, Source = (PartyEntrySource)r.Int("Src"), Reference = r.Str("Ref"), Description = r.Str("Descr"),
                DebitPaisa = Math.Max(0, amount), CreditPaisa = Math.Max(0, -amount), SortKey = r.Long("SortKey")
            });
        }
    }

    // ───────────────────────── Shared loaders ─────────────────────────

    private sealed record PartyRow(long Id, string Name, string? Phone, string? Gstin, long OpeningOutstanding, DateTime OpeningDate, long Credits);

    private static long NetDue(PartyRow party, IReadOnlyDictionary<long, List<DueDocument>> bills) =>
        party.OpeningOutstanding - party.Credits + (bills.TryGetValue(party.Id, out var list) ? list.Sum(b => b.BalancePaisa) : 0);

    private static async Task<List<PartyRow>> LoadPartiesAsync(SqliteConnection conn, SqliteTransaction? tx, PartyType partyType,
        long? partyId, string? text, int limit)
    {
        RequireSupported(partyType);
        var alias = partyType == PartyType.Customer ? "c" : "s";
        var baseSql = partyType == PartyType.Customer ? FinancePartySql.CustomerParties : FinancePartySql.SupplierParties;
        var sql = $@"{baseSql}
            WHERE (@Id IS NULL OR {alias}.Id = @Id)
              AND (@Search IS NULL OR {alias}.Name LIKE @Search ESCAPE '\' OR {alias}.Phone LIKE @Search ESCAPE '\' OR {alias}.Gstin LIKE @Search ESCAPE '\')
            ORDER BY {alias}.Name COLLATE NOCASE
            LIMIT @Limit";
        using var cmd = conn.Command(tx, sql, ("@Id", partyId), ("@Search", LikeOrNull(text)), ("@Limit", limit));
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<PartyRow>();
        while (await r.ReadAsync())
        {
            var credits = r.Long("OpenNotes") + r.Long("AdvancePaisa") + r.Long("OverpaidPaisa");
            list.Add(new PartyRow(r.Long("Id"), r.Str("Name") ?? string.Empty, r.Str("Phone"), r.Str("Gstin"),
                r.Long("OpeningOutstanding"), r.DateOrNull("OpeningDate")?.Date ?? new DateTime(2000, 1, 1), credits));
        }
        return list;
    }

    private static async Task<Dictionary<long, List<DueDocument>>> LoadOpenBillsAsync(SqliteConnection conn, SqliteTransaction? tx,
        PartyType partyType, long? partyId)
    {
        RequireSupported(partyType);
        var target = partyType == PartyType.Customer ? AllocationTarget.Sale : AllocationTarget.Purchase;
        var baseSql = partyType == PartyType.Customer ? FinancePartySql.OpenSales : FinancePartySql.OpenPurchases;
        using var cmd = conn.Command(tx, $"SELECT * FROM ({baseSql}) WHERE (@P IS NULL OR PartyId = @P) ORDER BY DocDate, Id", ("@P", partyId));
        using var r = await cmd.ExecuteReaderAsync();
        var map = new Dictionary<long, List<DueDocument>>();
        while (await r.ReadAsync())
        {
            var party = r.Long("PartyId");
            if (!map.TryGetValue(party, out var list)) map[party] = list = new List<DueDocument>();
            list.Add(new DueDocument
            {
                Target = target, DocumentId = r.Long("Id"), Number = r.Str("Number") ?? $"#{r.Long("Id")}", Date = r.Date("DocDate").Date,
                DueDate = r.DateOrNull("DueDate"), TotalPaisa = r.Long("Total"), PaidPaisa = r.Long("Paid"), BalancePaisa = r.Long("Balance")
            });
        }
        return map;
    }

    private static void RequireSupported(PartyType partyType)
    {
        if (partyType is not (PartyType.Customer or PartyType.Supplier))
            throw new FinanceValidationException("Ledgers are kept for customers and suppliers only.");
    }
}
