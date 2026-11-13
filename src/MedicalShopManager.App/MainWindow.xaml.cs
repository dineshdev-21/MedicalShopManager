using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.ViewModels;

namespace MedicalShopManager.App;

/// <summary>Shell window. FROZEN for module agents: pages and shortcuts come from INavigationRegistry.</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel, INavigationRegistry registry, ILogger<MainWindow> logger)
    {
        InitializeComponent();
        DataContext = viewModel;
        RegisterPageShortcuts(viewModel, registry, logger);
        Loaded += async (_, _) => await viewModel.InitializeCommand.ExecuteAsync(null);
    }

    /// <summary>Window-level shortcuts declared on NavItems (e.g. Ctrl+Alt+S → New Bill).</summary>
    private void RegisterPageShortcuts(MainViewModel viewModel, INavigationRegistry registry, ILogger logger)
    {
        var converter = new KeyGestureConverter();
        foreach (var item in registry.Items.Where(i => !string.IsNullOrWhiteSpace(i.KeyGesture)))
        {
            try
            {
                var gesture = (KeyGesture)converter.ConvertFromInvariantString(item.KeyGesture!)!;
                InputBindings.Add(new KeyBinding(viewModel.NavigateToCommand, gesture) { CommandParameter = item.Key });
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                logger.LogError(ex, "Invalid shortcut '{Gesture}' on page '{Key}' ignored", item.KeyGesture, item.Key);
            }
        }
    }
}
