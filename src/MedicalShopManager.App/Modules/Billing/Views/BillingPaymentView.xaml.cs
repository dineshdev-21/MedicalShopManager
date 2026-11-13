using System.Windows.Controls;

namespace MedicalShopManager.App.Modules.Billing.Views;

/// <summary>3-step payment wizard (each step focuses its first field through <see cref="BillingFocus"/>).</summary>
public partial class BillingPaymentView : UserControl
{
    public BillingPaymentView() => InitializeComponent();
}
