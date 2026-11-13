using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using static MedicalShopManager.App.Printing.DocumentBuilder;

namespace MedicalShopManager.App.Modules.Finance.Printing;

/// <summary>Shared blocks of the Finance printouts: shop header, label / value totals, signature line.</summary>
internal static class FinancePrintBlocks
{
    public static FlowDocument NewDocument() => new() { FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };

    public static void AddHeader(FlowDocument doc, string title, PrintContext context)
    {
        var size = context.BaseFontSize;
        var shop = context.Shop;
        var heading = context.CopyLabel is { } copy ? $"{title}  ({copy})" : title;
        doc.Blocks.Add(Text(heading, size + 1, FontWeights.SemiBold, TextAlignment.Center));
        doc.Blocks.Add(Text(shop.Name, size + 4, FontWeights.Bold, context.IsThermal ? TextAlignment.Center : TextAlignment.Left));
        if (!string.IsNullOrWhiteSpace(shop.Address)) doc.Blocks.Add(Text(shop.Address, size));
        var ids = new[]
        {
            string.IsNullOrWhiteSpace(shop.Phone) ? null : $"Ph: {shop.Phone}",
            shop.IsGstRegistered ? $"GSTIN: {shop.Gstin}" : null,
            string.IsNullOrWhiteSpace(shop.DrugLicenseNumber) ? null : $"DL: {shop.DrugLicenseNumber}"
        }.Where(s => s is not null);
        var line = string.Join("  |  ", ids);
        if (line.Length > 0) doc.Blocks.Add(Text(line, size - 1));
        doc.Blocks.Add(Rule());
    }

    /// <summary>Right-aligned label / value rows (totals block).</summary>
    public static Table Totals(double size, bool thermal, IEnumerable<(string Label, string Value, bool Bold)> rows)
    {
        var table = Table(size, new[] { ("", thermal ? 2.0 : 3.0, TextAlignment.Right), ("", 1.2, TextAlignment.Right) });
        table.RowGroups[0].Rows.Clear();
        foreach (var (label, value, bold) in rows)
            AddRow(table, new[] { (label, TextAlignment.Right), (value, TextAlignment.Right) }, bold ? FontWeights.SemiBold : null);
        return table;
    }

    public static void AddSignature(FlowDocument doc, PrintContext context, string leftLabel)
    {
        doc.Blocks.Add(Text(context.IsThermal
                ? $"For {context.Shop.Name}"
                : $"{leftLabel}: ____________________                    For {context.Shop.Name}",
            context.BaseFontSize, margin: new Thickness(0, 18, 0, 0)));
    }

    public static string Amount(long paisa) => (paisa < 0 ? "-" : "") + Money(Math.Abs(paisa));
}
