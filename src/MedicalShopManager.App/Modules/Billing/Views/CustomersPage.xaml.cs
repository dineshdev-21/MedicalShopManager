using System.Windows.Controls;
using System.Windows.Input;
using MedicalShopManager.App.Modules.Billing.ViewModels;

namespace MedicalShopManager.App.Modules.Billing.Views;

/// <summary>Customers directory (double-click or Enter edits).</summary>
public partial class CustomersPage : UserControl
{
    public CustomersPage() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CustomersViewModel vm) vm.EditCommand.Execute(vm.SelectedCustomer);
    }
}
