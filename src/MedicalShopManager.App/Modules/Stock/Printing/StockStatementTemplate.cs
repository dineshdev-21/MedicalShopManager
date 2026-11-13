using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Stock;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.Stock.Printing;

/// <summary>Stock statement: the filtered stock list with stock (packs + loose), MRP, nearest expiry and value at cost.</summary>
public sealed class StockStatementTemplate : IDocumentTemplate<StockStatement>
{
    public string Name => "StockStatement";
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5 };
    public PaperSize DefaultPaperSize => PaperSize.A4;

    public FlowDocument Render(StockStatement statement, PrintContext context)
    {
        var size = context.BaseFontSize;
        var doc = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
        doc.Blocks.Add(Text(context.Shop.Name, size + 4, FontWeights.Bold));
        doc.Blocks.Add(Text(statement.Title, size + 1, FontWeights.SemiBold));
        doc.Blocks.Add(Text($"Generated {statement.GeneratedAt:dd-MM-yyyy HH:mm}", size - 1));
        doc.Blocks.Add(Rule());

        var table = DocumentBuilder.Table(size - 1, new[]
        {
            ("#", 0.4, TextAlignment.Left), ("Item", 3.0, TextAlignment.Left), ("Company", 1.6, TextAlignment.Left),
            ("Pack", 0.7, TextAlignment.Left), ("Rack", 0.7, TextAlignment.Left), ("Stock", 0.9, TextAlignment.Right),
            ("Exp", 0.7, TextAlignment.Left), ("MRP", 1.0, TextAlignment.Right), ("Value", 1.2, TextAlignment.Right)
        });
        var n = 1;
        foreach (var item in statement.Items)
        {
            AddRow(table, new[]
            {
                ((n++).ToString(), TextAlignment.Left), (item.Name, TextAlignment.Left), (item.Manufacturer ?? "", TextAlignment.Left),
                (item.PackDisplay, TextAlignment.Left), (item.RackLocation ?? "", TextAlignment.Left), (item.StockDisplay, TextAlignment.Right),
                (item.ExpiryLabel, TextAlignment.Left), (Money(item.MrpPaisa), TextAlignment.Right), (Money(item.StockValuePaisa), TextAlignment.Right)
            });
        }
        doc.Blocks.Add(table);
        doc.Blocks.Add(Rule());
        var s = statement.Summary;
        doc.Blocks.Add(Text($"Items: {s.ItemCount}   In stock: {s.InStockCount}   Low: {s.LowStockCount}   Out: {s.OutOfStockCount}", size - 1));
        doc.Blocks.Add(Text($"Stock value at cost: ₹{Money(s.StockValuePaisa)}    At MRP: ₹{Money(s.MrpValuePaisa)}", size, FontWeights.Bold, TextAlignment.Right));
        return doc;
    }
}
