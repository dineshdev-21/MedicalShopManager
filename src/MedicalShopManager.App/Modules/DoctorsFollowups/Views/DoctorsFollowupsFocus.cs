using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace MedicalShopManager.App.Modules.DoctorsFollowups.Views;

/// <summary>
/// Keyboard-first helpers for the doctors and follow-up screens:
/// <list type="bullet">
/// <item><c>KeyboardForm="True"</c> on a container: Enter moves to the next field (unless the field binds Enter
/// itself, is a button, a multi-line box or an open drop-down), the field being typed in is committed before Alt / Ctrl
/// shortcuts run, and text boxes select their text on focus so typing replaces it.</item>
/// <item><c>FocusFirstOnShow="True"</c> on a dialog panel: when it becomes visible, focus moves to its first field.</item>
/// </list>
/// </summary>
public static class DoctorsFollowupsFocus
{
    public static readonly DependencyProperty KeyboardFormProperty = DependencyProperty.RegisterAttached(
        "KeyboardForm", typeof(bool), typeof(DoctorsFollowupsFocus), new PropertyMetadata(false, OnKeyboardFormChanged));

    public static readonly DependencyProperty FocusFirstOnShowProperty = DependencyProperty.RegisterAttached(
        "FocusFirstOnShow", typeof(bool), typeof(DoctorsFollowupsFocus), new PropertyMetadata(false, OnFocusFirstChanged));

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
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((key == Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            && Keyboard.FocusedElement is TextBox focused)
            focused.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.OriginalSource is not UIElement source) return;
        if (source is TextBox { AcceptsReturn: true } || source is ButtonBase || source is DataGridCell || source is ListBoxItem) return;
        if (source is ComboBox { IsDropDownOpen: true }) return;
        if (HasEnterBinding(source)) return;
        e.Handled = source.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

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
        if (e.NewFocus is TextBox { AcceptsReturn: false } box) box.SelectAll();
    }

    private static void OnFocusFirstChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.IsVisibleChanged -= OnVisibleChanged;
        if (e.NewValue is true) element.IsVisibleChanged += OnVisibleChanged;
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element || e.NewValue is not true) return;
        element.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            () => element.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
    }
}
