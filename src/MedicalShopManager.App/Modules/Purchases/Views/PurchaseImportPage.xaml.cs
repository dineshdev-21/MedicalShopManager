using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MedicalShopManager.App.Modules.Purchases.ViewModels;

namespace MedicalShopManager.App.Modules.Purchases.Views;

/// <summary>Supplier bill import. The Open-file dialog lives here (a view concern); the ViewModel reads and maps the file.</summary>
public partial class PurchaseImportPage : UserControl
{
    public PurchaseImportPage()
    {
        InitializeComponent();
        InputBindings.Add(new KeyBinding(new RelayCommand(Browse), Key.O, ModifierKeys.Control));
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e) => Browse();

    private async void Browse()
    {
        if (DataContext is not PurchaseImportViewModel vm) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose the distributor's bill file",
            Filter = "Bill files (*.csv;*.xlsx;*.txt)|*.csv;*.xlsx;*.xlsm;*.txt;*.tsv|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await vm.LoadFileAsync(dialog.FileName);
    }
}
