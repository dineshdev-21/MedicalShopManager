using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MedicalShopManager.App.Modules.Stock.Views;

/// <summary>Keyboard-first dialogs: when a Stock dialog becomes visible, focus moves to its first field (lowest TabIndex).</summary>
internal static class StockDialogFocus
{
    public static void Attach(FrameworkElement dialog)
    {
        dialog.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                dialog.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    () => dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
        };
    }
}
