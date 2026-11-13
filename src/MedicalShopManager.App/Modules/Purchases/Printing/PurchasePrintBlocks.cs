using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.Purchases.Printing;

/// <summary>Layout pieces shared by the Purchases print templates (shop header, label/value lines, totals).</summary>
internal static class PurchasePrintBlocks
{
    public static FlowDocument NewDocument() => new() { FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };

    /// <summary>Title, then shop name, address and IDs (GSTIN, DL no.) as on every pharmacy document.</summary>
    public static void AddHeader(FlowDocument doc, string title, PrintContext context)
    {
        var size = context.BaseFontSize;
        var shop = context.Shop;
        var heading = context.CopyLabel is { } copy ? $"{title}  ({copy})" : title;
        doc.Blocks.Add(Text(heading, size + 1, FontWeights.SemiBold, TextAlignment.Center));
        doc.Blocks.Add(Text(shop.Name, size + 4, FontWeights.Bold));
        if (!string.IsNullOrWhiteSpace(shop.Address)) doc.Blocks.Add(Text(shop.Address, size));
        var ids = new[]
        {
            string.IsNullOrWhiteSpace(shop.Phone) ? null : $"Ph: {shop.Phone}",
            shop.IsGstRegistered ? $"GSTIN: {shop.Gstin}" : null,
            string.IsNullOrWhiteSpace(shop.DrugLicenseNumber) ? null : $"DL: {shop.DrugLicenseNumber}"
        }.Where(s => s != null);
        var line = string.Join("  |  ", ids);
        if (line.Length > 0) doc.Blocks.Add(Text(line, size - 1));
        doc.Blocks.Add(Rule());
    }

    /// <summary>Right-aligned "label: amount" rows for a totals block.</summary>
    public static Table Totals(double size, IEnumerable<(string Label, string Value, bool Bold)> rows)
    {
        var table = Table(size, new[] { ("", 3.0, TextAlignment.Right), ("", 1.0, TextAlignment.Right) });
        table.RowGroups[0].Rows.Clear();
        foreach (var (label, value, bold) in rows)
            AddRow(table, new[] { (label, TextAlignment.Right), (value, TextAlignment.Right) }, bold ? FontWeights.SemiBold : null);
        return table;
    }

    public static string Signed(long paisa) => paisa == 0 ? "0.00" : (paisa > 0 ? "+" : "-") + Money(Math.Abs(paisa));
}
