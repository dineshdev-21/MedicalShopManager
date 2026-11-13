using System.Windows.Controls;
using System.Windows.Input;
using MedicalShopManager.App.Modules.Purchases.ViewModels;

namespace MedicalShopManager.App.Modules.Purchases.Views;

/// <summary>Purchase list / register (double-click or Enter opens a purchase).</summary>
public partial class PurchasesPage : UserControl
{
    public PurchasesPage() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PurchasesViewModel vm) vm.OpenCommand.Execute(vm.SelectedPurchase);
    }
}
