using System.Globalization;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Reports;

namespace MedicalShopManager.Infrastructure.Modules.Reports;

/// <summary>
/// Excel export of a report (ClosedXML), one worksheet per sheet. Ordinary sheets get a title block (shop, report,
/// period), a bold header row, typed cells (money as rupees "#,##0.00", dates as dates) and a totals row. GST portal
/// sheets (<see cref="ReportTable.PortalFormat"/>) are written exactly as the offline tool expects: header in row 1,
/// dates as dd-MMM-yyyy text, no title and no totals. Text starting with = + - @ is prefixed with ' (lesson 3).
/// Every export is recorded in the audit log.
/// </summary>
public sealed class ReportExcelExporter : IReportExporter
{
    private const string MoneyFormat = "#,##0.00";
    private const string DateFormat = "dd-mmm-yyyy";
    private readonly IAuditService? _audit;
    private readonly ILogger<ReportExcelExporter> _logger;

    public ReportExcelExporter(IAuditService? audit = null, ILogger<ReportExcelExporter>? logger = null)
    {
        _audit = audit;
        _logger = logger ?? NullLogger<ReportExcelExporter>.Instance;
    }

    public async Task ExportExcelAsync(ReportResult result, string filePath, IReadOnlyList<ReportTable>? sheets = null, string? shopName = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(filePath)) throw new ReportException("Choose a file name for the Excel export.");
        var toWrite = sheets ?? result.Sheets;
        if (toWrite.Count == 0) throw new ReportException("There is nothing to export.");

        try
        {
            using (var workbook = new XLWorkbook())
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sheet in toWrite)
                {
                    var ws = workbook.Worksheets.Add(UniqueSheetName(sheet.Name, names));
                    if (sheet.PortalFormat) WritePortalSheet(ws, sheet);
                    else WriteSheet(ws, sheet, result, shopName);
                }
                if (result.Notes.Count > 0)
                {
                    var ws = workbook.Worksheets.Add(UniqueSheetName("Notes", names));
                    ws.Cell(1, 1).Value = "Notes";
                    ws.Cell(1, 1).Style.Font.Bold = true;
                    for (var i = 0; i < result.Notes.Count; i++) ws.Cell(i + 2, 1).Value = Sanitize(result.Notes[i]);
                    ws.Column(1).Width = 120;
                }
                var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                workbook.SaveAs(filePath);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Excel export of {Report} to {Path} failed", result.Definition.Id, filePath);
            throw new ReportException($"Could not save '{Path.GetFileName(filePath)}'. Close it if it is open in Excel and try again.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ReportException($"You do not have permission to save to '{filePath}'. Choose another folder.", ex);
        }

        if (_audit != null)
            await _audit.LogAsync("Report exported", "Report", null, null,
                $"{result.Definition.Name} ({result.PeriodLabel}) → {Path.GetFileName(filePath)}; {toWrite.Sum(s => s.Rows.Count)} rows");
    }

    /// <summary>Neutralises spreadsheet formula injection: text starting with = + - @ (or tab / CR) gets a leading quote.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;
    }

    /// <summary>Excel sheet name: no : \ / ? * [ ], at most 31 characters, unique in the workbook.</summary>
    public static string UniqueSheetName(string name, ISet<string> used)
    {
        var clean = new string((string.IsNullOrWhiteSpace(name) ? "Sheet" : name).Select(c => ":\\/?*[]".Contains(c) ? '-' : c).ToArray()).Trim('\'');
        if (clean.Length > 31) clean = clean[..31];
        var candidate = clean;
        for (var i = 2; !used.Add(candidate); i++)
        {
            var suffix = $" ({i})";
            candidate = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
        }
        return candidate;
    }

    private static void WritePortalSheet(IXLWorksheet ws, ReportTable sheet)
    {
        for (var c = 0; c < sheet.Columns.Count; c++)
        {
            ws.Cell(1, c + 1).Value = sheet.Columns[c].Header;
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }
        for (var r = 0; r < sheet.Rows.Count; r++)
            for (var c = 0; c < sheet.Columns.Count; c++)
                SetCell(ws.Cell(r + 2, c + 1), sheet.Rows[r].Values[c], sheet.Columns[c].Kind, portal: true);
        SizeColumns(ws, sheet);
    }

    private static void WriteSheet(IXLWorksheet ws, ReportTable sheet, ReportResult result, string? shopName)
    {
        var row = 1;
        if (!string.IsNullOrWhiteSpace(shopName))
        {
            ws.Cell(row, 1).Value = Sanitize(shopName);
            ws.Cell(row++, 1).Style.Font.Bold = true;
        }
        ws.Cell(row, 1).Value = Sanitize(result.Title + (result.Sheets.Count > 1 ? " — " + sheet.Name : string.Empty));
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row++, 1).Style.Font.FontSize = 14;
        ws.Cell(row++, 1).Value = Sanitize(result.PeriodLabel);
        ws.Cell(row++, 1).Value = "Generated " + result.GeneratedAt.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture);
        row++;

        var headerRow = row;
        for (var c = 0; c < sheet.Columns.Count; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = sheet.Columns[c].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(232, 240, 254);
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            if (sheet.Columns[c].Kind.IsNumeric()) cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        foreach (var dataRow in sheet.Rows)
        {
            row++;
            for (var c = 0; c < sheet.Columns.Count; c++)
                SetCell(ws.Cell(row, c + 1), dataRow.Values[c], sheet.Columns[c].Kind, portal: false);
            if (dataRow.Style != ReportRowStyle.Normal) ws.Row(row).Style.Font.Bold = true;
        }
        if (sheet.HasTotals && sheet.HasRows)
        {
            row++;
            var totals = sheet.Totals();
            ws.Cell(row, 1).Value = "Total";
            for (var c = 0; c < sheet.Columns.Count; c++)
                if (totals[c] != null) SetCell(ws.Cell(row, c + 1), totals[c], sheet.Columns[c].Kind, portal: false);
            ws.Row(row).Style.Font.Bold = true;
            ws.Range(row, 1, row, sheet.Columns.Count).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }
        ws.SheetView.FreezeRows(headerRow);
        SizeColumns(ws, sheet);
    }

    private static void SetCell(IXLCell cell, object? value, ReportColumnKind kind, bool portal)
    {
        switch (value)
        {
            case null:
                return;
            case DateTime date when portal:
                cell.Value = ReportFormat.Date(date);
                return;
            case DateTime date:
                cell.Value = date;
                cell.Style.NumberFormat.Format = DateFormat;
                return;
            case long paisa when kind == ReportColumnKind.Money:
                cell.Value = (double)ReportFormat.Rupees(paisa);
                cell.Style.NumberFormat.Format = MoneyFormat;
                return;
            case long number:
                cell.Value = number;
                return;
            case decimal percent:
                cell.Value = (double)percent;
                return;
            default:
                cell.Value = Sanitize(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }

    /// <summary>Fixed widths from the column's relative width and header length (no font measuring, so it works headless).</summary>
    private static void SizeColumns(IXLWorksheet ws, ReportTable sheet)
    {
        for (var c = 0; c < sheet.Columns.Count; c++)
        {
            var column = sheet.Columns[c];
            var longest = sheet.Rows.Take(500).Select(r => ReportFormat.Cell(r.Values[c], column.Kind).Length).DefaultIfEmpty(0).Max();
            ws.Column(c + 1).Width = Math.Clamp(Math.Max(Math.Max(column.Header.Length, longest) + 2, column.Width * 10), 8, 60);
        }
    }
}
