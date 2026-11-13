using System.Globalization;
using System.Text;

namespace MedicalShopManager.Core.Modules.Finance;

/// <summary>
/// CSV exports of Finance statements (opened in Excel). Text cells that start with = + - @ are prefixed with a quote
/// so they cannot run as formulas (lesson 3); amounts are plain rupees with two decimals.
/// </summary>
public static class FinanceCsv
{
    public static string AccountStatement(AccountStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var sb = new StringBuilder();
        Row(sb, "Account statement", statement.Account.DisplayName);
        Row(sb, "Period", $"{statement.From:dd-MM-yyyy} to {statement.To:dd-MM-yyyy}");
        Row(sb);
        Row(sb, "Date", "Type", "Reference", "Party", "Description", "In", "Out", "Balance");
        Row(sb, statement.From.ToString("dd-MM-yyyy"), "Opening balance", "", "", "", "", "", Amount(statement.OpeningBalancePaisa));
        foreach (var line in statement.Lines)
            Row(sb, line.Date.ToString("dd-MM-yyyy"), line.SourceLabel, line.Reference, line.PartyName, line.Description,
                line.InPaisa == 0 ? "" : Amount(line.InPaisa), line.OutPaisa == 0 ? "" : Amount(line.OutPaisa), Amount(line.BalancePaisa));
        Row(sb, statement.To.ToString("dd-MM-yyyy"), "Closing balance", "", "", "", Amount(statement.TotalInPaisa),
            Amount(statement.TotalOutPaisa), Amount(statement.ClosingBalancePaisa));
        return sb.ToString();
    }

    public static string PartyLedger(PartyLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var sb = new StringBuilder();
        Row(sb, "Party ledger", ledger.PartyName);
        Row(sb, "Period", $"{ledger.From:dd-MM-yyyy} to {ledger.To:dd-MM-yyyy}");
        Row(sb);
        Row(sb, "Date", "Type", "Reference", "Description", "Debit", "Credit", "Balance");
        Row(sb, ledger.From.ToString("dd-MM-yyyy"), "Opening balance", "", "", "", "", FinanceRules.DrCr(ledger.OpeningBalancePaisa));
        foreach (var line in ledger.Lines)
            Row(sb, line.Date.ToString("dd-MM-yyyy"), line.SourceLabel, line.Reference, line.Description,
                line.DebitPaisa == 0 ? "" : Amount(line.DebitPaisa), line.CreditPaisa == 0 ? "" : Amount(line.CreditPaisa),
                FinanceRules.DrCr(line.BalancePaisa));
        Row(sb, ledger.To.ToString("dd-MM-yyyy"), "Closing balance", "", "", Amount(ledger.TotalDebitPaisa), Amount(ledger.TotalCreditPaisa),
            FinanceRules.DrCr(ledger.ClosingBalancePaisa));
        return sb.ToString();
    }

    public static string PartyBalances(IReadOnlyList<PartyBalanceRow> rows, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        Row(sb, "Outstanding as on", asOf.ToString("dd-MM-yyyy"));
        Row(sb, "Party", "Phone", "GSTIN", "0-30 days", "31-60 days", "61-90 days", "Over 90 days", "Credits / advance", "Net due");
        foreach (var r in rows)
            Row(sb, r.Name, r.Phone, r.Gstin, Amount(r.Ageing.Days0To30Paisa), Amount(r.Ageing.Days31To60Paisa),
                Amount(r.Ageing.Days61To90Paisa), Amount(r.Ageing.Over90Paisa), Amount(r.CreditsPaisa), Amount(r.NetDuePaisa));
        return sb.ToString();
    }

    public static string Expenses(IReadOnlyList<FinanceExpense> expenses)
    {
        ArgumentNullException.ThrowIfNull(expenses);
        var sb = new StringBuilder();
        Row(sb, "Number", "Date", "Category", "Payee", "GSTIN", "Bill no", "Taxable", "CGST", "SGST", "IGST", "Amount", "Account", "Mode", "Description", "Status");
        foreach (var e in expenses)
            Row(sb, e.NumberDisplay, e.ExpenseDate.ToString("dd-MM-yyyy"), e.CategoryName, e.PayeeName, e.PayeeGstin, e.InvoiceNumber,
                Amount(e.TaxablePaisa), Amount(e.CgstPaisa), Amount(e.SgstPaisa), Amount(e.IgstPaisa), Amount(e.AmountPaisa),
                e.AccountName, e.ModeLabel, e.Description, e.IsCancelled ? "Cancelled" : "");
        return sb.ToString();
    }

    public static string Amount(long paisa) => (paisa / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Quotes a cell when needed and neutralises spreadsheet formulas.</summary>
    public static string Cell(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' && !IsNumber(text)) text = "'" + text;
        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    private static bool IsNumber(string text) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _);

    private static void Row(StringBuilder sb, params string?[] cells) => sb.AppendLine(string.Join(",", cells.Select(Cell)));
}
