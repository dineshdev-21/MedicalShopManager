using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MedicalShopManager.App.Printing;

/// <summary>Modal preview of a rendered FlowDocument with Print / Close buttons (Ctrl+P / Esc).</summary>
internal sealed class PrintPreviewWindow : Window
{
    public PrintPreviewWindow(FlowDocument document, string title, Func<Task<bool>> print)
    {
        Title = title;
        Width = 900;
        Height = 1000;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var printButton = new Button { Content = "Print (Ctrl+P)", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        printButton.Click += async (_, _) =>
        {
            if (await print()) Close();
        };
        var closeButton = new Button { Content = "Close (Esc)", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        closeButton.Click += (_, _) => Close();

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right };
        toolbar.Children.Add(printButton);
        toolbar.Children.Add(closeButton);

        var panel = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        panel.Children.Add(toolbar);
        panel.Children.Add(new FlowDocumentReader { Document = document, ViewingMode = FlowDocumentReaderViewingMode.Page });
        Content = panel;

        KeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.P && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true;
                if (await print()) Close();
            }
        };
    }
}
