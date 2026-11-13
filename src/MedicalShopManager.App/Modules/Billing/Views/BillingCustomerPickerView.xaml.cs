using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MedicalShopManager.App.Modules.Billing.ViewModels;

namespace MedicalShopManager.App.Modules.Billing.Views;

/// <summary>"Select a Customer": the search box takes focus whenever the list is shown.</summary>
public partial class BillingCustomerPickerView : UserControl
{
    public BillingCustomerPickerView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) FocusSearch();
        };
        SearchBox.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) FocusSearch();
        };
    }

    private void FocusSearch() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    });

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is BillingCustomerPickerViewModel vm) vm.SelectCommand.Execute(vm.SelectedCustomer);
    }
}
