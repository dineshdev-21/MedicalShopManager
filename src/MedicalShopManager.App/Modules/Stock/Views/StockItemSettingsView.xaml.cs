using System.Windows.Controls;

namespace MedicalShopManager.App.Modules.Stock.Views;

public partial class StockItemSettingsView : UserControl
{
    public StockItemSettingsView()
    {
        InitializeComponent();
        StockDialogFocus.Attach(this);
    }
}
