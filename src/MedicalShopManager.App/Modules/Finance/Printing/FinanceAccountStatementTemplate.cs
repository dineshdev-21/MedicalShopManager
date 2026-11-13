using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using static MedicalShopManager.App.Printing.DocumentBuilder;
using static MedicalShopManager.App.Modules.Finance.Printing.FinancePrintBlocks;

namespace MedicalShopManager.App.Modules.Finance.Printing;

/// <summary>
/// Account statement (cash book / bank book of one account) for a period: opening balance, every movement with money
/// in / out and the running balance, totals and closing balance. A4 portrait by default.
/// </summary>
public sealed class FinanceAccountStatementTemplate : IDocumentTemplate<AccountStatement>
{
    public const string TemplateName = "FinanceAccountStatement";

    public string Name => TemplateName;
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5 };
    public PaperSize DefaultPaperSize => PaperSize.A4;

    public FlowDocument Render(AccountStatement statement, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);
        var size = context.BaseFontSize;
        var doc = NewDocument();
        AddHeader(doc, "ACCOUNT STATEMENT", context);
        doc.Blocks.Add(Text($"{statement.Account.DisplayName}  ({statement.Account.TypeLabel})", size + 1, FontWeights.SemiBold));
        doc.Blocks.Add(Text($"Period: {statement.From:dd-MM-yyyy} to {statement.To:dd-MM-yyyy}", size));

        var table = Table(size - 1, new[]
        {
            ("DATE", 0.9, TextAlignment.Left), ("TYPE", 1.0, TextAlignment.Left), ("REF", 1.2, TextAlignment.Left),
            ("PARTY / DETAILS", 2.6, TextAlignment.Left), ("IN", 1.0, TextAlignment.Right), ("OUT", 1.0, TextAlignment.Right),
            ("BALANCE", 1.1, TextAlignment.Right)
        });
        AddRow(table, new[]
        {
            (statement.From.ToString("dd-MM-yy"), TextAlignment.Left), ("Opening", TextAlignment.Left), ("", TextAlignment.Left),
            ("", TextAlignment.Left), ("", TextAlignment.Right), ("", TextAlignment.Right), (Amount(statement.OpeningBalancePaisa), TextAlignment.Right)
        }, FontWeights.SemiBold);
        foreach (var line in statement.Lines)
            AddRow(table, new[]
            {
                (line.Date.ToString("dd-MM-yy"), TextAlignment.Left), (line.SourceLabel, TextAlignment.Left), (line.Reference ?? "", TextAlignment.Left),
                (string.Join(" · ", new[] { line.PartyName, line.Description }.Where(s => !string.IsNullOrWhiteSpace(s))), TextAlignment.Left),
                (line.InPaisa == 0 ? "" : Money(line.InPaisa), TextAlignment.Right), (line.OutPaisa == 0 ? "" : Money(line.OutPaisa), TextAlignment.Right),
                (Amount(line.BalancePaisa), TextAlignment.Right)
            });
        doc.Blocks.Add(table);
        doc.Blocks.Add(Rule());
        doc.Blocks.Add(Totals(size, false, new[]
        {
            ("Opening balance", Amount(statement.OpeningBalancePaisa), false),
            ("Total in", Money(statement.TotalInPaisa), false),
            ("Total out", Money(statement.TotalOutPaisa), false),
            ("Closing balance", Amount(statement.ClosingBalancePaisa), true)
        }));
        doc.Blocks.Add(Text($"Printed {DateTime.Now:dd-MM-yyyy HH:mm}", size - 2, margin: new Thickness(0, 8, 0, 0)));
        return doc;
    }
}
