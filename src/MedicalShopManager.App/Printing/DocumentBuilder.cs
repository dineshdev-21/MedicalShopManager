using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MedicalShopManager.App.Printing;

/// <summary>Small helpers so templates read as layout, not FlowDocument plumbing.</summary>
public static class DocumentBuilder
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>₹ amount with Indian digit grouping, e.g. 123456.78 → "1,23,456.78".</summary>
    public static string Money(long paisa) => (paisa / 100m).ToString("N2", India);

    public static Paragraph Text(string text, double fontSize, FontWeight? weight = null, TextAlignment align = TextAlignment.Left,
        Thickness? margin = null) => new(new Run(text))
    {
        FontSize = fontSize,
        FontWeight = weight ?? FontWeights.Normal,
        TextAlignment = align,
        Margin = margin ?? new Thickness(0)
    };

    public static Paragraph Rule(double thickness = 1) => new()
    {
        BorderBrush = Brushes.Black,
        BorderThickness = new Thickness(0, 0, 0, thickness),
        Margin = new Thickness(0, 2, 0, 2),
        FontSize = 1
    };

    /// <summary>A table with the given relative column widths and a bold header row.</summary>
    public static Table Table(double fontSize, IReadOnlyList<(string Header, double Width, TextAlignment Align)> columns)
    {
        var table = new Table { CellSpacing = 0, FontSize = fontSize, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var column in columns)
            table.Columns.Add(new TableColumn { Width = new GridLength(column.Width, GridUnitType.Star) });

        var group = new TableRowGroup();
        var header = new TableRow { FontWeight = FontWeights.SemiBold };
        foreach (var column in columns)
            header.Cells.Add(Cell(column.Header, column.Align, bottomBorder: true));
        group.Rows.Add(header);
        table.RowGroups.Add(group);
        return table;
    }

    public static void AddRow(Table table, IReadOnlyList<(string Text, TextAlignment Align)> cells, FontWeight? weight = null)
    {
        var row = new TableRow { FontWeight = weight ?? FontWeights.Normal };
        foreach (var (text, align) in cells)
            row.Cells.Add(Cell(text, align));
        table.RowGroups[0].Rows.Add(row);
    }

    private static TableCell Cell(string text, TextAlignment align, bool bottomBorder = false) => new(new Paragraph(new Run(text)) { Margin = new Thickness(0) })
    {
        TextAlignment = align,
        Padding = new Thickness(2, 1, 2, 1),
        BorderBrush = Brushes.Black,
        BorderThickness = bottomBorder ? new Thickness(0, 0, 0, 1) : new Thickness(0)
    };
}
