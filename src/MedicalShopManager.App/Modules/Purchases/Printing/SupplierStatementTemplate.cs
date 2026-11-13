using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.Purchases.Printing;

/// <summary>Distributor statement (ledger) for a period: opening balance, purchases, payments, debit notes, closing balance.</summary>
public sealed class SupplierStatementTemplate : IDocumentTemplate<SupplierStatement>
{
    public const string TemplateName = "SupplierStatement";

    public string Name => TemplateName;
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5 };
    public PaperSize DefaultPaperSize => PaperSize.A4;

    public FlowDocument Render(SupplierStatement statement, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var size = context.BaseFontSize;
        var doc = PurchasePrintBlocks.NewDocument();
        PurchasePrintBlocks.AddHeader(doc, "DISTRIBUTOR STATEMENT", context);
        var supplier = statement.Supplier;
        doc.Blocks.Add(Text(supplier.Name + (string.IsNullOrWhiteSpace(supplier.Gstin) ? "" : $"   GSTIN: {supplier.Gstin}"), size, FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(supplier.Address)) doc.Blocks.Add(Text(supplier.Address, size - 1));
        doc.Blocks.Add(Text($"Period: {statement.From:dd-MM-yyyy} to {statement.To:dd-MM-yyyy}", size));

        var table = Table(size - 1, new[]
        {
            ("DATE", 0.9, TextAlignment.Left), ("TYPE", 0.9, TextAlignment.Left), ("DOC NO", 1.2, TextAlignment.Left),
            ("PARTICULARS", 2.6, TextAlignment.Left), ("DEBIT", 1.0, TextAlignment.Right), ("CREDIT", 1.0, TextAlignment.Right),
            ("BALANCE", 1.1, TextAlignment.Right)
        });
        foreach (var line in statement.Lines)
        {
            AddRow(table, new[]
            {
                (line.Date.ToString("dd-MM-yy"), TextAlignment.Left), (line.TypeLabel, TextAlignment.Left), (line.DocumentNumber, TextAlignment.Left),
                (line.Description, TextAlignment.Left), (line.DebitPaisa == 0 ? "" : Money(line.DebitPaisa), TextAlignment.Right),
                (line.CreditPaisa == 0 ? "" : Money(line.CreditPaisa), TextAlignment.Right), (Balance(line.BalancePaisa), TextAlignment.Right)
            }, line.Type == SupplierStatementEntryType.OpeningBalance ? FontWeights.SemiBold : null);
        }
        AddRow(table, new[]
        {
            ("", TextAlignment.Left), ("", TextAlignment.Left), ("", TextAlignment.Left), ("Closing balance", TextAlignment.Left),
            (Money(statement.TotalDebitPaisa), TextAlignment.Right), (Money(statement.TotalCreditPaisa), TextAlignment.Right),
            (Balance(statement.ClosingBalancePaisa), TextAlignment.Right)
        }, FontWeights.SemiBold);
        doc.Blocks.Add(table);
        doc.Blocks.Add(Text("Credit = bills (you owe more); Debit = payments and debit notes. Balance Cr = payable to the distributor.", size - 2));
        return doc;
    }

    private static string Balance(long paisa) => paisa >= 0 ? $"{Money(paisa)} Cr" : $"{Money(-paisa)} Dr";
}
