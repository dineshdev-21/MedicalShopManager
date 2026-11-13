using System.Windows;

namespace MedicalShopManager.App.Modules.Billing.Views;

/// <summary>
/// Customer-facing second display (spec 02 "/sale/customer-facing-screen"), bound to the live billing ViewModel:
/// welcome when idle, the current lines and total while billing, the saved message after Save. It opens on the
/// second monitor when one exists (to the right of the primary screen), otherwise beside the main window.
/// </summary>
public partial class BillingCustomerDisplayWindow : Window
{
    public BillingCustomerDisplayWindow()
    {
        InitializeComponent();
        if (SystemParameters.VirtualScreenWidth > SystemParameters.PrimaryScreenWidth)
        {
            Left = SystemParameters.PrimaryScreenWidth + 20;
            Top = 20;
            Loaded += (_, _) => WindowState = WindowState.Maximized;
        }
        else
        {
            Left = Math.Max(SystemParameters.WorkArea.Right - Width - 20, 0);
            Top = 40;
        }
    }
}
