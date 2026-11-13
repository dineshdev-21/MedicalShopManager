using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>
/// Distributors master (Ctrl+Alt+D): search, add / edit (GSTIN, drug licence, state, credit days, opening balance),
/// deactivate (history kept), outstanding / overdue derived from bills, payment on account (spread oldest-due first),
/// statement and purchase history.
/// </summary>
public sealed partial class SuppliersViewModel : BaseViewModel
{
    private readonly IDistributorService _distributors;
    private readonly IPurchaseEntryService _entry;
    private readonly INavigationService _navigation;
    private readonly IAuthService _auth;
    private readonly ILogger<SuppliersViewModel> _logger;
    private CancellationTokenSource? _search;

    public SuppliersViewModel(IDistributorService distributors, IPurchaseEntryService entry, INavigationService navigation, IAuthService auth,
        ILogger<SuppliersViewModel> logger)
    {
        _distributors = distributors;
        _entry = entry;
        _navigation = navigation;
        _auth = auth;
        _logger = logger;
    }

    public ObservableCollection<Distributor> Suppliers { get; } = new();
    public PurchaseDistributorEditorViewModel Editor { get; } = new();
    public IReadOnlyList<PurchaseOption<PaymentMethod>> PaymentMethods => PurchaseUi.PaymentMethods;

    [ObservableProperty] private Distributor? _selectedSupplier;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _includeInactive;
    [ObservableProperty] private long _totalOutstandingPaisa;
    [ObservableProperty] private long _totalOverduePaisa;
    [ObservableProperty] private bool _isEditorOpen;

    // Payment on account
    [ObservableProperty] private bool _isPaymentDialogOpen;
    [ObservableProperty] private decimal _paymentAmount;
    [ObservableProperty] private PurchaseOption<PaymentMethod>? _paymentMethod = PurchaseUi.PaymentMethods[0];
    [ObservableProperty] private string _paymentReference = string.Empty;
    [ObservableProperty] private DateTime? _paymentDate = DateTime.Today;
    [ObservableProperty] private string? _dialogError;

    public bool IsEmpty => !IsLoading && Suppliers.Count == 0;
    public string ActivateLabel => SelectedSupplier is { IsActive: false } ? "Activate" : "Deactivate";

    partial void OnSelectedSupplierChanged(Distributor? value) => OnPropertyChanged(nameof(ActivateLabel));
    partial void OnSearchQueryChanged(string value) => _ = DebouncedRefreshAsync();
    partial void OnIncludeInactiveChanged(bool value) => _ = RefreshAsync();

    public override Task LoadAsync() => RefreshAsync();

    private async Task DebouncedRefreshAsync()
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            await RefreshAsync();
        }
        catch (TaskCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var selectedId = SelectedSupplier?.Id;
            var list = await _distributors.SearchAsync(string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery.Trim(), IncludeInactive);
            Suppliers.Clear();
            foreach (var s in list) Suppliers.Add(s);
            SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == selectedId) ?? Suppliers.FirstOrDefault();
            TotalOutstandingPaisa = list.Where(s => s.IsActive).Sum(s => s.OutstandingBalancePaisa);
            TotalOverduePaisa = list.Where(s => s.IsActive).Sum(s => s.OverduePaisa);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading distributors failed");
            SetError(PurchaseUi.Message(ex, "load distributors"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private void Add()
    {
        Editor.Load(new Distributor { Name = SearchQuery.Trim() });
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void Edit(Distributor? supplier)
    {
        supplier ??= SelectedSupplier;
        if (supplier == null) return;
        Editor.Load(supplier);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CloseEditor() => IsEditorOpen = false;

    [RelayCommand]
    private async Task SaveAsync()
    {
        Editor.Errors.Clear();
        try
        {
            var distributor = Editor.ToDistributor();
            var isNew = distributor.Id == 0;
            var id = await _distributors.SaveAsync(distributor);
            IsEditorOpen = false;
            SetStatus(isNew ? $"Distributor '{distributor.Name}' added." : $"Distributor '{distributor.Name}' updated.");
            await RefreshAsync();
            SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == id) ?? SelectedSupplier;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving distributor failed");
            foreach (var message in PurchaseUi.Messages(ex, "save the distributor")) Editor.Errors.Add(message);
        }
    }

    [RelayCommand]
    private async Task ToggleActiveAsync(Distributor? supplier)
    {
        supplier ??= SelectedSupplier;
        if (supplier == null) return;
        try
        {
            await _distributors.SetActiveAsync(supplier.Id, !supplier.IsActive);
            SetStatus(supplier.IsActive ? $"'{supplier.Name}' deactivated; its bills and statement are kept." : $"'{supplier.Name}' is active again.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Changing distributor status failed");
            SetError(PurchaseUi.Message(ex, "change the distributor"));
        }
    }

    [RelayCommand]
    private void Statement(Distributor? supplier)
    {
        supplier ??= SelectedSupplier;
        if (supplier == null) return;
        _navigation.NavigateTo(PurchasePages.SupplierStatement, new SupplierStatementRequest(supplier.Id));
    }

    [RelayCommand]
    private void ViewPurchases(Distributor? supplier)
    {
        supplier ??= SelectedSupplier;
        if (supplier == null) return;
        _navigation.NavigateTo(PurchasePages.List, new PurchaseListRequest(supplier.Id));
    }

    [RelayCommand]
    private void ReturnGoods(Distributor? supplier)
    {
        supplier ??= SelectedSupplier;
        if (supplier == null) return;
        _navigation.NavigateTo(PurchasePages.Return, new PurchaseReturnRequest(supplier.Id));
    }

    [RelayCommand]
    private void OpenPaymentDialog(Distributor? supplier)
    {
        supplier ??= SelectedSupplier;
        if (supplier == null) return;
        SelectedSupplier = supplier;
        PaymentAmount = Math.Max(0, supplier.OutstandingBalancePaisa) / 100m;
        PaymentMethod = PurchaseUi.PaymentMethods[0];
        PaymentReference = string.Empty;
        PaymentDate = DateTime.Today;
        DialogError = null;
        IsPaymentDialogOpen = true;
    }

    [RelayCommand]
    private void ClosePaymentDialog() => IsPaymentDialogOpen = false;

    /// <summary>Payment on account: spread over the distributor's unpaid bills, oldest due first.</summary>
    [RelayCommand]
    private async Task SavePaymentAsync()
    {
        if (SelectedSupplier == null) return;
        if (PaymentAmount <= 0 || PaymentMethod == null)
        {
            DialogError = "Enter the amount and how it was paid.";
            return;
        }
        try
        {
            var payment = new PurchasePaymentInput(PurchaseUi.Paisa(PaymentAmount), PaymentMethod.Value,
                string.IsNullOrWhiteSpace(PaymentReference) ? null : PaymentReference.Trim(), PaymentDate ?? DateTime.Today);
            var allocations = await _entry.RecordSupplierPaymentAsync(SelectedSupplier.Id, payment, _auth.CurrentUser?.Id);
            IsPaymentDialogOpen = false;
            SetStatus($"₹{PaymentAmount:N2} paid to {SelectedSupplier.Name}, settled against {allocations.Count} bill(s): "
                      + string.Join(", ", allocations.Select(a => $"{a.SupplierInvoiceNumber} ₹{a.AmountPaisa / 100m:N2}")));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording supplier payment failed");
            DialogError = PurchaseUi.Message(ex, "record the payment");
        }
    }
}
