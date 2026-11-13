using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MedicalShopManager.App.Modules.Billing.ViewModels;

namespace MedicalShopManager.App.Modules.Billing.Views;

/// <summary>
/// The POS page. Code-behind only does view work: moving focus when the ViewModel asks, the prescription file
/// dialog, opening the customer-facing window, and keeping the grid's selected row in step with the focused cell.
/// </summary>
public partial class BillingPage : UserControl
{
    private BillingViewModel? _viewModel;
    private BillingCustomerDisplayWindow? _customerWindow;

    public BillingPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ProductBox.Focus());
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _viewModel = e.NewValue as BillingViewModel;
        if (_viewModel is not null) _viewModel.FocusRequested += OnFocusRequested;
    }

    private void Detach()
    {
        if (_viewModel is not null) _viewModel.FocusRequested -= OnFocusRequested;
    }

    private void OnFocusRequested(object? sender, string target) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            switch (target)
            {
                case BillingViewModel.FocusTargets.Product:
                    if (ProductBox.IsVisible) ProductBox.Focus();
                    break;
                case BillingViewModel.FocusTargets.BillDiscount:
                    BillDiscountBox.Focus();
                    break;
                case BillingViewModel.FocusTargets.CustomCharge:
                    CustomChargeBox.Focus();
                    break;
                case BillingViewModel.FocusTargets.Customer:
                    CustomerBox.Focus();
                    break;
                case BillingViewModel.FocusTargets.Patient:
                    PatientBox.Focus();
                    break;
                case BillingViewModel.FocusTargets.Packs:
                    FocusSelectedPacks();
                    break;
            }
        });

    /// <summary>Puts the cursor in the PACKS cell of the selected line (after a batch is picked).</summary>
    private void FocusSelectedPacks()
    {
        if (LinesGrid.SelectedItem is not { } item) return;
        LinesGrid.ScrollIntoView(item);
        LinesGrid.UpdateLayout();
        if (LinesGrid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return;
        var packs = FindDescendant<TextBox>(row, box => Equals(box.Tag, "Packs"));
        packs?.Focus();
    }

    private static T? FindDescendant<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && match(typed)) return typed;
            var nested = FindDescendant(child, match);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>A cell editor got focus: select its row so Del / Alt+B act on it.</summary>
    private void OnGridGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: BillLineViewModel line } && !ReferenceEquals(LinesGrid.SelectedItem, line))
            LinesGrid.SelectedItem = line;
    }

    /// <summary>Typing in the customer box opens "Select a Customer" with the typed text.</summary>
    private void OnCustomerTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_viewModel is null || string.IsNullOrWhiteSpace(e.Text)) return;
        e.Handled = true;
        _viewModel.SelectCustomerCommand.Execute(e.Text);
    }

    private void OnCustomerBoxClicked(object sender, MouseButtonEventArgs e) => _viewModel?.SelectCustomerCommand.Execute(null);

    private void OnProductDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel?.SelectedProduct is { } product) _viewModel.PickProductCommand.Execute(product);
    }

    private void OnProfitClicked(object sender, MouseButtonEventArgs e) => _viewModel?.ToggleProfitCommand.Execute(null);

    private async void OnAttachPrescription(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Attach prescription",
            Filter = "Prescription (images, PDF)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.pdf|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await _viewModel.AttachPrescriptionAsync(dialog.FileName);
    }

    private void OnViewPrescription(object sender, RoutedEventArgs e)
    {
        var path = _viewModel?.PrescriptionFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(Window.GetWindow(this)!, "The prescription file was not found.", "Prescription", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Opens (or brings forward) the customer-facing display bound to this bill.</summary>
    private void OnOpenCustomerScreen(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (_customerWindow is { IsLoaded: true })
        {
            _customerWindow.DataContext = _viewModel;
            _customerWindow.Activate();
            return;
        }
        _customerWindow = new BillingCustomerDisplayWindow { DataContext = _viewModel };
        _customerWindow.Closed += (_, _) => _customerWindow = null;
        _customerWindow.Show();
    }
}
