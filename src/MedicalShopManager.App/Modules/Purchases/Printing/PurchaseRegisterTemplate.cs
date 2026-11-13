using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.Purchases.Printing;

/// <summary>Purchase register for a period (the purchase list as filtered): one row per bill plus totals of confirmed bills.</summary>
public sealed class PurchaseRegisterTemplate : IDocumentTemplate<PurchaseRegisterReport>
{
    public const string TemplateName = "PurchaseRegister";

    public string Name => TemplateName;
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5 };
    public PaperSize DefaultPaperSize => PaperSize.A4;

    public FlowDocument Render(PurchaseRegisterReport report, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(report);
        var size = context.BaseFontSize;
        var doc = PurchasePrintBlocks.NewDocument();
        PurchasePrintBlocks.AddHeader(doc, "PURCHASE REGISTER", context);
        doc.Blocks.Add(Text($"Period: {report.PeriodLabel}   Distributor: {report.SupplierName ?? "All"}   Status: {report.StatusLabel}", size));

        var table = Table(size - 1, new[]
        {
            ("DATE", 0.9, TextAlignment.Left), ("PURCHASE NO", 1.2, TextAlignment.Left), ("BILL NO", 1.0, TextAlignment.Left),
            ("DISTRIBUTOR", 2.2, TextAlignment.Left), ("ITEMS", 0.5, TextAlignment.Right), ("TAXABLE", 1.1, TextAlignment.Right),
            ("GST", 0.9, TextAlignment.Right), ("TOTAL", 1.1, TextAlignment.Right), ("PAID", 1.0, TextAlignment.Right),
            ("BALANCE", 1.0, TextAlignment.Right), ("STATUS", 0.8, TextAlignment.Left)
        });
        foreach (var row in report.Rows)
        {
            AddRow(table, new[]
            {
                (row.InvoiceDate.ToString("dd-MM-yy"), TextAlignment.Left), (row.PurchaseNumber ?? "-", TextAlignment.Left),
                (row.SupplierInvoiceNumber, TextAlignment.Left), (row.SupplierName, TextAlignment.Left), (row.ItemCount.ToString(), TextAlignment.Right),
                (Money(row.TaxablePaisa), TextAlignment.Right), (Money(row.TaxAmountPaisa), TextAlignment.Right),
                (Money(row.GrandTotalPaisa), TextAlignment.Right), (Money(row.AmountPaidPaisa), TextAlignment.Right),
                (Money(row.BalancePaisa), TextAlignment.Right), (row.StatusLabel, TextAlignment.Left)
            }, row.Status == PurchaseStatus.Cancelled ? FontWeights.Light : null);
        }
        var s = report.Summary;
        AddRow(table, new[]
        {
            ("", TextAlignment.Left), ("", TextAlignment.Left), ("", TextAlignment.Left), ($"Total ({s.Count} confirmed bills)", TextAlignment.Left),
            ("", TextAlignment.Right), (Money(s.TaxablePaisa), TextAlignment.Right), (Money(s.TaxPaisa), TextAlignment.Right),
            (Money(s.GrandTotalPaisa), TextAlignment.Right), (Money(s.PaidPaisa), TextAlignment.Right), (Money(s.BalancePaisa), TextAlignment.Right),
            ("", TextAlignment.Left)
        }, FontWeights.SemiBold);
        doc.Blocks.Add(table);
        doc.Blocks.Add(Text($"Printed {DateTime.Now:dd-MM-yyyy HH:mm}. Drafts and cancelled bills are listed but not totalled.", size - 2));
        return doc;
    }
}
