using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Stock;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.Stock.Printing;

/// <summary>Stock adjustment voucher: number, date, reason, notes, one row per batch with the signed quantity and value at cost.</summary>
public sealed class StockAdjustmentTemplate : IDocumentTemplate<StockAdjustment>
{
    public string Name => "StockAdjustment";
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5 };
    public PaperSize DefaultPaperSize => PaperSize.A4;

    public FlowDocument Render(StockAdjustment adjustment, PrintContext context)
    {
        var size = context.BaseFontSize;
        var shop = context.Shop;
        var doc = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };

        doc.Blocks.Add(Text("STOCK ADJUSTMENT" + (context.CopyLabel is { } copy ? $"  ({copy})" : string.Empty), size, FontWeights.SemiBold, TextAlignment.Center));
        doc.Blocks.Add(Text(shop.Name, size + 4, FontWeights.Bold));
        if (!string.IsNullOrWhiteSpace(shop.Address)) doc.Blocks.Add(Text(shop.Address, size));
        if (!string.IsNullOrWhiteSpace(shop.DrugLicenseNumber)) doc.Blocks.Add(Text($"DL: {shop.DrugLicenseNumber}", size - 1));
        doc.Blocks.Add(Rule());

        doc.Blocks.Add(Text($"No: {adjustment.AdjustmentNumber}    Date: {adjustment.AdjustmentDate:dd-MM-yyyy}    Reason: {adjustment.ReasonLabel}", size, FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(adjustment.Notes)) doc.Blocks.Add(Text($"Notes: {adjustment.Notes}", size));
        if (!string.IsNullOrWhiteSpace(adjustment.CreatedByName)) doc.Blocks.Add(Text($"Entered by: {adjustment.CreatedByName}", size - 1));

        var table = DocumentBuilder.Table(size - 0.5, new[]
        {
            ("#", 0.4, TextAlignment.Left), ("Item", 3.0, TextAlignment.Left), ("Batch", 1.3, TextAlignment.Left),
            ("Exp", 0.8, TextAlignment.Left), ("Before", 1.0, TextAlignment.Right), ("Change", 1.0, TextAlignment.Right),
            ("After", 1.0, TextAlignment.Right), ("Cost/Pack", 1.1, TextAlignment.Right), ("Value", 1.2, TextAlignment.Right)
        });
        var n = 1;
        foreach (var line in adjustment.Lines)
        {
            var upp = Math.Max(line.UnitsPerPack, 1);
            AddRow(table, new[]
            {
                ((n++).ToString(), TextAlignment.Left), (line.MedicineName, TextAlignment.Left), (line.BatchNumber, TextAlignment.Left),
                (line.ExpiryDate.ToString("MM/yy"), TextAlignment.Left),
                (Core.Pricing.PackQuantity.Format(line.BalanceBefore, upp), TextAlignment.Right),
                ((line.QuantityChange > 0 ? "+" : "") + Core.Pricing.PackQuantity.Format(line.QuantityChange, upp), TextAlignment.Right),
                (Core.Pricing.PackQuantity.Format(line.BalanceAfter, upp), TextAlignment.Right),
                (Money(line.RatePaisa), TextAlignment.Right), (Money(line.ValuePaisa), TextAlignment.Right)
            });
        }
        doc.Blocks.Add(table);
        doc.Blocks.Add(Rule());
        doc.Blocks.Add(Text($"Total value at cost: ₹{Money(adjustment.TotalValuePaisa)}", size, FontWeights.Bold, TextAlignment.Right));
        doc.Blocks.Add(Text("Quantities are packs + loose units.", size - 1.5));
        doc.Blocks.Add(Text("Authorised signatory", size - 1, align: TextAlignment.Right, margin: new Thickness(0, 24, 0, 0)));
        return doc;
    }
}
