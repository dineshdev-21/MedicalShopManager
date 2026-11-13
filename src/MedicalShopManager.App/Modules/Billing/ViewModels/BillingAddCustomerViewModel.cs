using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>
/// Window shortcut Ctrl+Alt+C "Add customer" (module contract §5): opens the Add New Customer form on its own page;
/// after saving (or closing) it goes to the Customers list.
/// </summary>
public sealed partial class BillingAddCustomerViewModel : BaseViewModel
{
    private readonly INavigationService _navigation;

    public BillingAddCustomerViewModel(BillingCustomerEditorViewModel editor, INavigationService navigation)
    {
        _navigation = navigation;
        Editor = editor;
        Editor.Saved += (_, _) => _navigation.NavigateTo(BillingModule.CustomersKey);
        Editor.Closed += (_, _) => _navigation.NavigateTo(BillingModule.CustomersKey);
    }

    public BillingCustomerEditorViewModel Editor { get; }

    public override Task LoadAsync()
    {
        Editor.StartNew(null);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Close() => _navigation.NavigateTo(BillingModule.CustomersKey);
}
