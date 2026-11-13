using System.Windows.Controls;

namespace MedicalShopManager.App.Modules.Stock.Views;

public partial class StockItemEditorView : UserControl
{
    public StockItemEditorView()
    {
        InitializeComponent();
        StockDialogFocus.Attach(this);
    }
}
