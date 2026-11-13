using System.Windows;
using System.Windows.Documents;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Reports;

namespace MedicalShopManager.App.Modules.Reports.Printing;

/// <summary>What gets printed: one sheet of a report (already filtered by the viewer's quick search).</summary>
public sealed record ReportPrintModel(ReportResult Result, ReportTable Sheet);

/// <summary>
/// Generic report print layout (A4 / A5): shop header, report title and period, headline figures, the sheet as a
/// table with a totals row, and the report's notes. Save as PDF by choosing "Microsoft Print to PDF" in the printer
/// dialog. Wide sheets shrink the font.
/// </summary>
public sealed class ReportDocumentTemplate : IDocumentTemplate<ReportPrintModel>
{
    public string Name => "Report";
    public IReadOnlyCollection<PaperSize> SupportedPaperSizes { get; } = new[] { PaperSize.A4, PaperSize.A5 };
    public PaperSize DefaultPaperSize => PaperSize.A4;

    public FlowDocument Render(ReportPrintModel model, PrintContext context)
    {
        var sheet = model.Sheet;
        var result = model.Result;
        var baseSize = context.BaseFontSize;
        var tableSize = sheet.Columns.Count switch { > 12 => baseSize * 0.62, > 8 => baseSize * 0.75, _ => baseSize * 0.9 };
        var doc = new FlowDocument();

        doc.Blocks.Add(DocumentBuilder.Text(context.Shop.Name, baseSize * 1.2, FontWeights.Bold, TextAlignment.Center));
        var shopLine = string.Join("  ·  ", new[] { context.Shop.Address, context.Shop.Gstin.Length > 0 ? "GSTIN " + context.Shop.Gstin : "",
            context.Shop.DrugLicenseNumber.Length > 0 ? "DL " + context.Shop.DrugLicenseNumber : "" }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (shopLine.Length > 0) doc.Blocks.Add(DocumentBuilder.Text(shopLine, baseSize * 0.8, align: TextAlignment.Center));
        doc.Blocks.Add(DocumentBuilder.Rule());

        var title = result.Title + (result.Sheets.Count > 1 ? " — " + sheet.Name : string.Empty);
        doc.Blocks.Add(DocumentBuilder.Text(title, baseSize * 1.1, FontWeights.SemiBold, margin: new Thickness(0, 4, 0, 0)));
        doc.Blocks.Add(DocumentBuilder.Text($"{result.PeriodLabel}    Printed {DateTime.Now:dd-MMM-yyyy HH:mm}", baseSize * 0.8));
        if (result.Summary.Count > 0)
            doc.Blocks.Add(DocumentBuilder.Text(string.Join("    ", result.Summary.Select(s => $"{s.Label}: {s.Display}")), baseSize * 0.85,
                FontWeights.SemiBold, margin: new Thickness(0, 4, 0, 2)));

        var table = DocumentBuilder.Table(tableSize, sheet.Columns
            .Select(c => (c.Header, c.Width, c.Kind.IsNumeric() ? TextAlignment.Right : TextAlignment.Left)).ToList());
        foreach (var row in sheet.Rows)
        {
            DocumentBuilder.AddRow(table, row.Values.Select((v, i) => (ReportFormat.Cell(v, sheet.Columns[i].Kind),
                sheet.Columns[i].Kind.IsNumeric() ? TextAlignment.Right : TextAlignment.Left)).ToList(),
                row.Style == ReportRowStyle.Normal ? null : FontWeights.SemiBold);
        }
        if (sheet.HasTotals && sheet.HasRows)
        {
            var totals = sheet.Totals();
            DocumentBuilder.AddRow(table, sheet.Columns.Select((c, i) => (i == 0 && totals[0] == null ? "Total" : ReportFormat.Cell(totals[i], c.Kind),
                c.Kind.IsNumeric() ? TextAlignment.Right : TextAlignment.Left)).ToList(), FontWeights.Bold);
        }
        doc.Blocks.Add(table);
        if (!sheet.HasRows)
            doc.Blocks.Add(DocumentBuilder.Text("No records for the selected filters.", baseSize * 0.9, align: TextAlignment.Center));

        foreach (var note in result.Notes)
            doc.Blocks.Add(DocumentBuilder.Text("• " + note, baseSize * 0.7, margin: new Thickness(0, 2, 0, 0)));
        return doc;
    }
}
