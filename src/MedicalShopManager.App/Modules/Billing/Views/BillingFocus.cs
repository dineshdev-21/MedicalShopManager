using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace MedicalShopManager.App.Modules.Billing.Views;

/// <summary>
/// Keyboard-first helpers for the billing screens (spec 02 "Front - Enter | Back - Shift + Tab"):
/// <list type="bullet">
/// <item><c>KeyboardForm="True"</c> on a container: Enter moves to the next field (unless the field has its own Enter
/// binding or is an open drop-down), and text boxes select their text on focus so a typed number replaces it.</item>
/// <item><c>FocusFirstOnShow="True"</c> on a dialog: when it becomes visible, focus moves to its first field.</item>
/// </list>
/// </summary>
public static class BillingFocus
{
    public static readonly DependencyProperty KeyboardFormProperty = DependencyProperty.RegisterAttached(
        "KeyboardForm", typeof(bool), typeof(BillingFocus), new PropertyMetadata(false, OnKeyboardFormChanged));

    public static readonly DependencyProperty FocusFirstOnShowProperty = DependencyProperty.RegisterAttached(
        "FocusFirstOnShow", typeof(bool), typeof(BillingFocus), new PropertyMetadata(false, OnFocusFirstChanged));

    public static bool GetKeyboardForm(DependencyObject element) => (bool)element.GetValue(KeyboardFormProperty);
    public static void SetKeyboardForm(DependencyObject element, bool value) => element.SetValue(KeyboardFormProperty, value);
    public static bool GetFocusFirstOnShow(DependencyObject element) => (bool)element.GetValue(FocusFirstOnShowProperty);
    public static void SetFocusFirstOnShow(DependencyObject element, bool value) => element.SetValue(FocusFirstOnShowProperty, value);

    private static void OnKeyboardFormChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewKeyDown -= OnPreviewKeyDown;
        element.GotKeyboardFocus -= OnGotKeyboardFocus;
        if (e.NewValue is true)
        {
            element.PreviewKeyDown += OnPreviewKeyDown;
            element.GotKeyboardFocus += OnGotKeyboardFocus;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.OriginalSource is not UIElement source) return;
        if (source is TextBox { AcceptsReturn: true } || source is ButtonBase || source is DataGridCell) return;
        if (source is ComboBox { IsDropDownOpen: true }) return;
        if (HasEnterBinding(source)) return;
        e.Handled = source.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    /// <summary>True when the element (or its templated parent chain up to a control) binds Enter itself.</summary>
    private static bool HasEnterBinding(UIElement source)
    {
        DependencyObject? current = source;
        for (var depth = 0; current is not null && depth < 4; depth++)
        {
            if (current is UIElement ui && ui.InputBindings.OfType<KeyBinding>().Any(k => k.Key == Key.Enter && k.Modifiers == ModifierKeys.None))
                return true;
            current = current is FrameworkElement fe ? fe.TemplatedParent ?? fe.Parent : null;
        }
        return false;
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox { IsReadOnly: false } box)
            box.Dispatcher.BeginInvoke(DispatcherPriority.Input, box.SelectAll);
    }

    private static void OnFocusFirstChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        element.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
                element.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    () => element.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
        };
    }
}
