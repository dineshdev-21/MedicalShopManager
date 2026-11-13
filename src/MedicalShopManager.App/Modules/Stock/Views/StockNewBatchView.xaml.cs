using System.Windows.Controls;

namespace MedicalShopManager.App.Modules.Stock.Views;

public partial class StockNewBatchView : UserControl
{
    public StockNewBatchView()
    {
        InitializeComponent();
        StockDialogFocus.Attach(this);
    }
}
