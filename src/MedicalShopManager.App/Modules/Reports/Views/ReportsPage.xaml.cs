using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MedicalShopManager.App.Modules.Reports.Views;

/// <summary>Reports ▸ Business Reports catalogue. Code-behind only moves keyboard focus.</summary>
public partial class ReportsPage : UserControl
{
    public ReportsPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) => SearchBox.Focus();

    private void OnFocusSearch(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>↓ from the search box moves into the report list on the selected report.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || ReportList.Items.Count == 0) return;
        if (ReportList.SelectedIndex < 0) ReportList.SelectedIndex = 0;
        ReportList.UpdateLayout();
        if (ReportList.ItemContainerGenerator.ContainerFromIndex(ReportList.SelectedIndex) is ListBoxItem item) item.Focus();
        e.Handled = true;
    }
}
