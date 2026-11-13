using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using static MedicalShopManager.App.Printing.DocumentBuilder;
using static MedicalShopManager.App.Modules.Finance.Printing.FinancePrintBlocks;

namespace MedicalShopManager.App.Modules.Finance.Printing;

/// <summary>
/// Customer / supplier ledger statement for a period (sent to the party to confirm the balance): opening balance,
/// bills, payments, returns and cheques with debit / credit and the running Dr / Cr balance, and the closing balance.
/// </summary>
public sealed class FinancePartyLedgerTemplate : IDocumentTemplate<PartyLedger>
{
    public const string TemplateName = "FinancePartyLedger";

    public string Name => TemplateName;
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5 };
    public PaperSize DefaultPaperSize => PaperSize.A4;

    public FlowDocument Render(PartyLedger ledger, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(context);
        var size = context.BaseFontSize;
        var doc = NewDocument();
        AddHeader(doc, ledger.PartyType == PartyType.Supplier ? "SUPPLIER LEDGER" : "CUSTOMER LEDGER", context);
        doc.Blocks.Add(Text(ledger.PartyName, size + 1, FontWeights.SemiBold));
        var ids = string.Join("   ", new[]
        {
            string.IsNullOrWhiteSpace(ledger.Phone) ? null : $"Ph: {ledger.Phone}",
            string.IsNullOrWhiteSpace(ledger.Gstin) ? null : $"GSTIN: {ledger.Gstin}"
        }.Where(s => s is not null));
        if (ids.Length > 0) doc.Blocks.Add(Text(ids, size));
        doc.Blocks.Add(Text($"Period: {ledger.From:dd-MM-yyyy} to {ledger.To:dd-MM-yyyy}", size));

        var table = Table(size - 1, new[]
        {
            ("DATE", 0.9, TextAlignment.Left), ("TYPE", 1.1, TextAlignment.Left), ("REF", 1.3, TextAlignment.Left),
            ("DETAILS", 2.4, TextAlignment.Left), ("DEBIT", 1.0, TextAlignment.Right), ("CREDIT", 1.0, TextAlignment.Right),
            ("BALANCE", 1.3, TextAlignment.Right)
        });
        AddRow(table, new[]
        {
            (ledger.From.ToString("dd-MM-yy"), TextAlignment.Left), ("Opening", TextAlignment.Left), ("", TextAlignment.Left), ("", TextAlignment.Left),
            ("", TextAlignment.Right), ("", TextAlignment.Right), (FinanceRules.DrCr(ledger.OpeningBalancePaisa).Replace("₹", ""), TextAlignment.Right)
        }, FontWeights.SemiBold);
        foreach (var line in ledger.Lines)
            AddRow(table, new[]
            {
                (line.Date.ToString("dd-MM-yy"), TextAlignment.Left), (line.SourceLabel, TextAlignment.Left), (line.Reference ?? "", TextAlignment.Left),
                (line.Description ?? "", TextAlignment.Left), (line.DebitPaisa == 0 ? "" : Money(line.DebitPaisa), TextAlignment.Right),
                (line.CreditPaisa == 0 ? "" : Money(line.CreditPaisa), TextAlignment.Right), (line.BalanceLabel.Replace("₹", ""), TextAlignment.Right)
            });
        doc.Blocks.Add(table);
        doc.Blocks.Add(Rule());
        doc.Blocks.Add(Totals(size, false, new[]
        {
            ("Total debit", Money(ledger.TotalDebitPaisa), false),
            ("Total credit", Money(ledger.TotalCreditPaisa), false),
            ("Closing balance", FinanceRules.DrCr(ledger.ClosingBalancePaisa).Replace("₹", ""), true)
        }));
        doc.Blocks.Add(Text("Dr = amount due to the shop, Cr = amount the shop owes. Please report any difference within 7 days.",
            size - 2, margin: new Thickness(0, 8, 0, 0)));
        AddSignature(doc, context, "Party's confirmation");
        return doc;
    }
}
