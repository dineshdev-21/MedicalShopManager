using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MedicalShopManager.App.Modules.Finance.Views;

/// <summary>
/// Receive / pay form shared by the two Finance payment pages. View-only keyboard handling: F3 focuses the party search,
/// Down moves from the search box into the results, and Enter on a result picks it and moves to the amount.
/// </summary>
public partial class FinancePartyPaymentPanel : UserControl
{
    public FinancePartyPaymentPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PartySearchBox.Focus());
        PreviewKeyDown += OnPanelKeyDown;
        PartyList.PreviewKeyDown += OnListKeyDown;
    }

    private void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F3 || Keyboard.Modifiers != ModifierKeys.None) return;
        PartySearchBox.Focus();
        PartySearchBox.SelectAll();
        e.Handled = true;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Enter) || PartyList.Items.Count == 0) return;
        if (PartyList.SelectedIndex < 0) PartyList.SelectedIndex = 0;
        if (e.Key == Key.Enter)
        {
            MoveToForm();
        }
        else if (PartyList.ItemContainerGenerator.ContainerFromIndex(PartyList.SelectedIndex) is ListBoxItem item)
        {
            item.Focus();
        }
        e.Handled = true;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        MoveToForm();
        e.Handled = true;
    }

    /// <summary>After a party is picked, focus moves to the first field of the form (the amount).</summary>
    private void MoveToForm() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        PartyList.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    });
}
