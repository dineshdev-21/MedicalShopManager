using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MedicalShopManager.App.Modules.Reports.ViewModels;
using MedicalShopManager.Core.Modules.Reports;

namespace MedicalShopManager.App.Modules.Reports.Views;

/// <summary>
/// Generic report viewer. Code-behind formats the auto-generated grid columns from the column kinds that
/// <see cref="ReportGridBuilder"/> stores on the DataTable (header text, ₹ / date / % formats, right-aligned numbers)
/// and moves keyboard focus; all behaviour is in <see cref="ReportViewerViewModel"/>.
/// </summary>
public partial class ReportViewerPage : UserControl
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");
    private static readonly Style RightAligned = CreateRightAligned();

    public ReportViewerPage()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusFirstFilter();
    }

    private static Style CreateRightAligned()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 6, 0)));
        style.Seal();
        return style;
    }

    private void FocusFirstFilter()
    {
        if (FromPicker.IsVisible) FromPicker.Focus();
        else if (AsOfPicker.IsVisible) AsOfPicker.Focus();
        else ReportGrid.Focus();
    }

    private void OnFocusSearch(object sender, ExecutedRoutedEventArgs e)
    {
        QuickFilterBox.Focus();
        QuickFilterBox.SelectAll();
    }

    private void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName == ReportGridBuilder.StyleColumn)
        {
            e.Cancel = true;
            return;
        }
        if (sender is not DataGrid { ItemsSource: DataView view } || view.Table?.Columns[e.PropertyName] is not { } column) return;

        e.Column.Header = column.Caption;
        var kind = column.ExtendedProperties[ReportGridBuilder.KindProperty] is ReportColumnKind k ? k : ReportColumnKind.Text;
        if (e.Column is DataGridBoundColumn { Binding: Binding binding })
        {
            binding.ConverterCulture = India;
            binding.StringFormat = kind switch
            {
                ReportColumnKind.Money => "N2",
                ReportColumnKind.Integer or ReportColumnKind.Quantity => "N0",
                ReportColumnKind.Percent => "0.##'%'",
                ReportColumnKind.Date => "dd-MMM-yyyy",
                _ => null
            };
        }
        if (kind.IsNumeric() && e.Column is DataGridTextColumn text) text.ElementStyle = RightAligned;
        if (kind == ReportColumnKind.Text) e.Column.MaxWidth = 320;
    }
}
