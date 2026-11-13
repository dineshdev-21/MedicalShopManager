using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.DoctorsFollowups;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.Printing;

/// <summary>Doctor-wise sales for a period: bills, patients, sales, taxable, profit and commission per doctor, with totals.</summary>
public sealed class DoctorSalesSummaryTemplate : IDocumentTemplate<DoctorSalesSummary>
{
    public string Name => "DoctorSalesSummary";
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5 };
    public PaperSize DefaultPaperSize => PaperSize.A4;

    public FlowDocument Render(DoctorSalesSummary summary, PrintContext context)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(context);
        var size = context.BaseFontSize;
        var doc = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
        doc.Blocks.Add(Text(context.Shop.Name, size + 4, FontWeights.Bold));
        if (!string.IsNullOrWhiteSpace(context.Shop.Address)) doc.Blocks.Add(Text(context.Shop.Address, size - 1));
        doc.Blocks.Add(Text(summary.Title, size + 1, FontWeights.SemiBold));
        doc.Blocks.Add(Text($"Generated {summary.GeneratedAt:dd-MM-yyyy HH:mm}", size - 1));
        doc.Blocks.Add(Rule());

        var table = DocumentBuilder.Table(size - 1, new[]
        {
            ("#", 0.4, TextAlignment.Left), ("Doctor", 2.6, TextAlignment.Left), ("Reg. no", 1.2, TextAlignment.Left),
            ("Bills", 0.6, TextAlignment.Right), ("Sales", 1.3, TextAlignment.Right), ("Taxable", 1.3, TextAlignment.Right),
            ("Profit", 1.2, TextAlignment.Right), ("Comm. rule", 1.4, TextAlignment.Left), ("Commission", 1.3, TextAlignment.Right)
        });
        var n = 1;
        foreach (var row in summary.Rows)
        {
            var name = row.IsLinked ? row.DisplayName : row.DisplayName + " *";
            AddRow(table, new[]
            {
                ((n++).ToString(), TextAlignment.Left), (name, TextAlignment.Left), (row.RegistrationNo ?? "", TextAlignment.Left),
                (row.BillCount.ToString(), TextAlignment.Right), (Money(row.SalesPaisa), TextAlignment.Right),
                (Money(row.TaxablePaisa), TextAlignment.Right), (Money(row.ProfitPaisa), TextAlignment.Right),
                (row.CommissionLabel, TextAlignment.Left), (Money(row.CommissionPaisa), TextAlignment.Right)
            });
        }
        var t = summary.Totals;
        AddRow(table, new[]
        {
            ("", TextAlignment.Left), ("Total", TextAlignment.Left), ("", TextAlignment.Left), (t.Bills.ToString(), TextAlignment.Right),
            (Money(t.SalesPaisa), TextAlignment.Right), (Money(t.TaxablePaisa), TextAlignment.Right), (Money(t.ProfitPaisa), TextAlignment.Right),
            ("", TextAlignment.Left), (Money(t.CommissionPaisa), TextAlignment.Right)
        }, FontWeights.Bold);
        doc.Blocks.Add(table);
        doc.Blocks.Add(Rule());
        if (summary.Rows.Any(r => !r.IsLinked))
            doc.Blocks.Add(Text("* Doctor typed on the bill, not in the doctor master (no commission).", size - 2));
        doc.Blocks.Add(Text("Sales = bill totals incl. GST. Commission on the taxable value, or on profit (taxable − cost) where marked.", size - 2));
        return doc;
    }
}
