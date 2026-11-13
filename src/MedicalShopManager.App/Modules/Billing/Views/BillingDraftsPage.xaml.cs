using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MedicalShopManager.App.Modules.Billing.ViewModels;

namespace MedicalShopManager.App.Modules.Billing.Views;

/// <summary>Unsaved drafts list (page for F8, also hosted as a popup on the billing screen).</summary>
public partial class BillingDraftsPage : UserControl
{
    public BillingDraftsPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => DraftsGrid.Focus());
        };
    }

    private void OnDraftDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is BillingDraftsViewModel vm) vm.OpenCommand.Execute(vm.SelectedDraft);
    }
}
