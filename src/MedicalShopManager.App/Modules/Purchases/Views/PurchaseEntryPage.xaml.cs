using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MedicalShopManager.App.Modules.Purchases.ViewModels;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.Views;

/// <summary>Add / view / edit purchase. Code-behind only moves focus and forwards mouse double-clicks.</summary>
public partial class PurchaseEntryPage : UserControl
{
    private PurchaseEntryViewModel? _vm;

    public PurchaseEntryPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_vm is { SelectedDistributor: null, IsEditing: true }) DistributorBox.Focus();
            else if (ProductBox.IsVisible) ProductBox.Focus();
        });
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.FocusRequested -= OnFocusRequested;
        _vm = e.NewValue as PurchaseEntryViewModel;
        if (_vm != null) _vm.FocusRequested += OnFocusRequested;
    }

    private void OnFocusRequested(object? sender, string target) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        UIElement? element = target switch
        {
            "Batch" => BatchBox,
            "Invoice" => InvoiceBox,
            "Distributor" => DistributorBox,
            _ => ProductBox
        };
        element?.Focus();
    });

    /// <summary>Space in an empty distributor box lists every distributor (spec 04).</summary>
    private void OnDistributorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && string.IsNullOrEmpty(DistributorBox.Text) && _vm != null)
        {
            _vm.ShowAllDistributorsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDistributorDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm != null && sender is ListBox { SelectedItem: Distributor d }) _vm.PickDistributorCommand.Execute(d);
    }

    private void OnProductDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm != null && sender is ListBox { SelectedItem: Medicine m }) _vm.PickProductCommand.Execute(m);
    }

    private void OnLineDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm != null && LinesGrid.SelectedItem is PurchaseBillLine line) _vm.EditLineCommand.Execute(line);
    }
}
