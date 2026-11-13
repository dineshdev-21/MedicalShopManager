using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Modules.Billing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>
/// "Select a Customer" modal (spec 02 §C): search by name / mobile / GSTIN (25 rows), arrow keys + Enter select,
/// F2 create new (pre-filled with the search text), F4 edit selected, Del delete (deactivate) selected, Esc close.
/// The add / edit form is the hosted <see cref="Editor"/>.
/// </summary>
public sealed partial class BillingCustomerPickerViewModel : ObservableObject
{
    private readonly IBillingCustomerService _customers;
    private readonly ILogger<BillingCustomerPickerViewModel> _logger;
    private CancellationTokenSource? _searchCts;

    public BillingCustomerPickerViewModel(IBillingCustomerService customers, BillingCustomerEditorViewModel editor,
        ILogger<BillingCustomerPickerViewModel> logger)
    {
        _customers = customers;
        _logger = logger;
        Editor = editor;
        Editor.Saved += (_, customer) =>
        {
            IsEditorOpen = false;
            Selected?.Invoke(this, customer);
        };
        Editor.Closed += (_, _) => IsEditorOpen = false;
    }

    public event EventHandler<BillingCustomer>? Selected;
    public event EventHandler? Closed;

    public BillingCustomerEditorViewModel Editor { get; }
    public ObservableCollection<BillingCustomer> Results { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private BillingCustomer? _selectedCustomer;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _message;

    public bool IsEmpty => !IsBusy && Results.Count == 0;
    public string CreateNewLabel => string.IsNullOrWhiteSpace(SearchText) ? "Create New Customer (F2)" : $"Create New Customer '{SearchText.Trim()}' (F2)";

    public async Task OpenAsync(string? initialText)
    {
        IsEditorOpen = false;
        Message = null;
        SearchText = initialText ?? string.Empty;
        await SearchNowAsync(SearchText, CancellationToken.None);
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(CreateNewLabel));
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = DebouncedSearchAsync(value, _searchCts.Token);
    }

    private async Task DebouncedSearchAsync(string text, CancellationToken token)
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
        IsBusy = true;
        try
        {
            var list = await _customers.SearchAsync(text);
            token.ThrowIfCancellationRequested();
            Results.Clear();
            foreach (var customer in list) Results.Add(customer);
            SelectedCustomer = Results.FirstOrDefault();
            Message = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Customer search failed for '{Text}'", text);
            Message = $"Customer search failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private void Select(BillingCustomer? customer)
    {
        customer ??= SelectedCustomer;
        if (customer is null)
        {
            CreateNew();
            return;
        }
        Selected?.Invoke(this, customer);
    }

    [RelayCommand]
    private void CreateNew()
    {
        Editor.StartNew(SearchText);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (SelectedCustomer is null)
        {
            Message = "Select a customer to edit.";
            return;
        }
        Editor.Load(SelectedCustomer);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedCustomer is not { } customer)
        {
            Message = "Select a customer to delete.";
            return;
        }
        try
        {
            await _customers.DeactivateAsync(customer.Id);
            Results.Remove(customer);
            SelectedCustomer = Results.FirstOrDefault();
            Message = $"Customer '{customer.Name}' deleted.";
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (BillingValidationException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting customer {CustomerId} failed", customer.Id);
            Message = $"The customer could not be deleted: {ex.Message}";
        }
    }

    [RelayCommand]
    private void MoveSelection(string? direction)
    {
        if (Results.Count == 0) return;
        var index = SelectedCustomer is null ? -1 : Results.IndexOf(SelectedCustomer);
        index = direction == "Up" ? Math.Max(index - 1, 0) : Math.Min(index + 1, Results.Count - 1);
        SelectedCustomer = Results[index];
    }

    [RelayCommand]
    private void Close()
    {
        if (IsEditorOpen)
        {
            IsEditorOpen = false;
            return;
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
