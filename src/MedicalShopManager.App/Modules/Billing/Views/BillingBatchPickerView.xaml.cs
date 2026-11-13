using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MedicalShopManager.App.Modules.Billing.ViewModels;

namespace MedicalShopManager.App.Modules.Billing.Views;

/// <summary>Batch popup: focuses the selected row when shown so arrow keys and Enter work at once.</summary>
public partial class BillingBatchPickerView : UserControl
{
    public BillingBatchPickerView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusSelectedRow);
        };
        DataContextChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusSelectedRow);
    }

    private void FocusSelectedRow()
    {
        if (!IsVisible) return;
        var item = BatchGrid.SelectedItem;
        if (item is null)
        {
            BatchGrid.Focus();
            return;
        }
        BatchGrid.ScrollIntoView(item);
        BatchGrid.UpdateLayout();
        if (BatchGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row) row.Focus();
        else BatchGrid.Focus();
    }

    private void OnBatchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is BillingBatchPickerViewModel vm) vm.PickCommand.Execute(vm.SelectedBatch);
    }
}
