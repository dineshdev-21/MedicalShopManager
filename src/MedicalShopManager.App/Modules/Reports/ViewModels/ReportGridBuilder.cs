using System.Data;
using MedicalShopManager.Core.Modules.Reports;

namespace MedicalShopManager.App.Modules.Reports.ViewModels;

/// <summary>
/// Turns a <see cref="ReportTable"/> into a typed DataTable for the WPF DataGrid (auto-generated columns, sortable by
/// value). Column names are safe binding paths (c0, c1 …); the header is the caption and the kind is kept in
/// ExtendedProperties for formatting. Money is shown in rupees (decimal). A hidden "__style" column carries the row
/// style (opening / subtotal rows are bold).
/// </summary>
public static class ReportGridBuilder
{
    public const string StyleColumn = "__style";
    public const string KindProperty = "kind";

    public static DataTable Build(ReportTable sheet)
    {
        var table = new DataTable(sheet.Name);
        for (var i = 0; i < sheet.Columns.Count; i++)
        {
            var column = sheet.Columns[i];
            var data = new DataColumn(ColumnName(i), ClrType(column.Kind)) { Caption = column.Header, AllowDBNull = true };
            data.ExtendedProperties[KindProperty] = column.Kind;
            table.Columns.Add(data);
        }
        table.Columns.Add(new DataColumn(StyleColumn, typeof(int)));

        table.BeginLoadData();
        foreach (var row in sheet.Rows)
        {
            var values = new object[sheet.Columns.Count + 1];
            for (var i = 0; i < sheet.Columns.Count; i++)
                values[i] = ToCell(row.Values[i], sheet.Columns[i].Kind);
            values[^1] = (int)row.Style;
            table.Rows.Add(values);
        }
        table.EndLoadData();
        return table;
    }

    public static string ColumnName(int index) => "c" + index;

    public static Type ClrType(ReportColumnKind kind) => kind switch
    {
        ReportColumnKind.Date => typeof(DateTime),
        ReportColumnKind.Integer or ReportColumnKind.Quantity => typeof(long),
        ReportColumnKind.Money or ReportColumnKind.Percent => typeof(decimal),
        _ => typeof(string)
    };

    public static object ToCell(object? value, ReportColumnKind kind) => value switch
    {
        null => DBNull.Value,
        long paisa when kind == ReportColumnKind.Money => ReportFormat.Rupees(paisa),
        _ => value
    };

    /// <summary>Totals of the money / quantity columns that are totalled, for the footer.</summary>
    public static IReadOnlyList<ReportFigure> Totals(ReportTable sheet)
    {
        if (!sheet.HasTotals || !sheet.HasRows) return Array.Empty<ReportFigure>();
        var totals = sheet.Totals();
        return sheet.Columns.Select((c, i) => (c, Total: totals[i]))
            .Where(x => x.Total != null)
            .Select(x => new ReportFigure(x.c.Header, ReportFormat.Cell(x.Total, x.c.Kind)))
            .ToList();
    }
}
