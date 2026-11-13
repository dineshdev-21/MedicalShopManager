using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.Billing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>
/// Customers directory (Sales ▸ Customers): individual / business customers with discount, credit days and the
/// balance derived from their sales. F2 adds, F4 / Enter edits, Del deletes (deactivates; refused while a balance
/// is open).
/// Moved here from App/ViewModels (owned by Billing) and switched to <see cref="IBillingCustomerService"/>.
/// </summary>
public sealed partial class CustomersViewModel : BaseViewModel
{
    private readonly IBillingCustomerService _customers;
    private readonly ILogger<CustomersViewModel> _logger;
    private CancellationTokenSource? _searchCts;

    public CustomersViewModel(IBillingCustomerService customers, BillingCustomerEditorViewModel editor, ILogger<CustomersViewModel> logger)
    {
        _customers = customers;
        _logger = logger;
        Editor = editor;
        Editor.Saved += async (_, customer) =>
        {
            IsEditorOpen = false;
            SetStatus($"Customer '{customer.Name}' saved.");
            await SearchNowAsync(SearchQuery, CancellationToken.None);
            SelectedCustomer = Customers.FirstOrDefault(c => c.Id == customer.Id);
        };
        Editor.Closed += (_, _) => IsEditorOpen = false;
    }

    public BillingCustomerEditorViewModel Editor { get; }
    public ObservableCollection<BillingCustomer> Customers { get; } = new();

    [ObservableProperty] private BillingCustomer? _selectedCustomer;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isEditorOpen;

    public bool IsEmpty => !IsLoading && Customers.Count == 0;
    public long TotalReceivablePaisa => Customers.Where(c => c.OutstandingBalancePaisa > 0).Sum(c => c.OutstandingBalancePaisa);

    public override Task LoadAsync() => SearchNowAsync(SearchQuery, CancellationToken.None);

    partial void OnSearchQueryChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = DebouncedAsync(value, _searchCts.Token);
    }

    private async Task DebouncedAsync(string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            await SearchNowAsync(text, token);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer keystroke
        }
    }

    private async Task SearchNowAsync(string text, CancellationToken token)
    {
        IsLoading = true;
        try
        {
            var list = await _customers.SearchAsync(text);
            token.ThrowIfCancellationRequested();
            Customers.Clear();
            foreach (var customer in list) Customers.Add(customer);
            ClearError();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading customers failed");
            SetError($"Failed to load customers: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(TotalReceivablePaisa));
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => SearchNowAsync(SearchQuery, CancellationToken.None);

    [RelayCommand]
    private void Add()
    {
        Editor.StartNew(null);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void Edit(BillingCustomer? customer)
    {
        customer ??= SelectedCustomer;
        if (customer is null)
        {
            SetError("Select a customer to edit.");
            return;
        }
        Editor.Load(customer);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private async Task DeleteAsync(BillingCustomer? customer)
    {
        customer ??= SelectedCustomer;
        if (customer is null) return;
        try
        {
            await _customers.DeactivateAsync(customer.Id);
            Customers.Remove(customer);
            SetStatus($"Customer '{customer.Name}' deleted.");
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (BillingValidationException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting customer {CustomerId} failed", customer.Id);
            SetError($"The customer could not be deleted: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CloseEditor() => IsEditorOpen = false;
}
