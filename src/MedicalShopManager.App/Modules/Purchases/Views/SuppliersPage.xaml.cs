using System.Windows.Controls;
using System.Windows.Input;
using MedicalShopManager.App.Modules.Purchases.ViewModels;

namespace MedicalShopManager.App.Modules.Purchases.Views;

/// <summary>Distributors master (double-click or Enter edits).</summary>
public partial class SuppliersPage : UserControl
{
    public SuppliersPage() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SuppliersViewModel vm) vm.EditCommand.Execute(vm.SelectedSupplier);
    }
}
