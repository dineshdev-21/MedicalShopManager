using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>
/// View Purchases / purchase register (spec 04 §2, §6): filters by date range, distributor, status and text; columns
/// date, purchase no, bill no, distributor, items, taxable, GST, total, paid, balance, due date, status. Open (Enter),
/// new (F2), record payment, delete with stock reversal, return items, print register.
/// </summary>
public sealed partial class PurchasesViewModel : BaseViewModel, INavigationAware
{
    private readonly IPurchaseEntryService _entry;
    private readonly IDistributorService _distributors;
    private readonly INavigationService _navigation;
    private readonly IAuthService _auth;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<PurchaseRegisterReport> _registerTemplate;
    private readonly IDocumentTemplate<PurchaseBill> _voucherTemplate;
    private readonly ILogger<PurchasesViewModel> _logger;
    private long? _pendingSupplierId;
    private bool _loaded;

    public PurchasesViewModel(IPurchaseEntryService entry, IDistributorService distributors, INavigationService navigation, IAuthService auth,
        IPrintService print, IDocumentTemplate<PurchaseRegisterReport> registerTemplate, IDocumentTemplate<PurchaseBill> voucherTemplate,
        ILogger<PurchasesViewModel> logger)
    {
        _entry = entry;
        _distributors = distributors;
        _navigation = navigation;
        _auth = auth;
        _print = print;
        _registerTemplate = registerTemplate;
        _voucherTemplate = voucherTemplate;
        _logger = logger;
    }

    public ObservableCollection<PurchaseListRow> Purchases { get; } = new();
    public ObservableCollection<PurchaseOption<long?>> SupplierOptions { get; } = new();
    public IReadOnlyList<PurchaseOption<PurchaseListStatusFilter>> StatusOptions => PurchaseUi.StatusFilters;
    public IReadOnlyList<PurchaseOption<PaymentMethod>> PaymentMethods => PurchaseUi.PaymentMethods;

    [ObservableProperty] private DateTime? _fromDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-2);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;
    [ObservableProperty] private PurchaseOption<long?>? _selectedSupplier;
    [ObservableProperty] private PurchaseOption<PurchaseListStatusFilter>? _selectedStatus = PurchaseUi.StatusFilters[0];
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private PurchaseListRow? _selectedPurchase;
    [ObservableProperty] private PurchaseListSummary _summary = PurchaseListSummary.From(Array.Empty<PurchaseListRow>());

    // Payment dialog
    [ObservableProperty] private bool _isPaymentDialogOpen;
    [ObservableProperty] private decimal _paymentAmount;
    [ObservableProperty] private PurchaseOption<PaymentMethod>? _paymentMethod = PurchaseUi.PaymentMethods[0];
    [ObservableProperty] private string _paymentReference = string.Empty;
    [ObservableProperty] private DateTime? _paymentDate = DateTime.Today;
    [ObservableProperty] private string? _dialogError;

    // Delete dialog
    [ObservableProperty] private bool _isDeleteDialogOpen;
    [ObservableProperty] private string _deleteReason = string.Empty;

    public bool IsEmpty => !IsLoading && Purchases.Count == 0;
    public bool HasSelection => SelectedPurchase != null;

    partial void OnSelectedPurchaseChanged(PurchaseListRow? value) => OnPropertyChanged(nameof(HasSelection));

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is PurchaseListRequest request) _pendingSupplierId = request.SupplierId;
    }

    public override async Task LoadAsync()
    {
        try
        {
            var distributors = await _distributors.SearchAsync(null, includeInactive: true);
            SupplierOptions.Clear();
            foreach (var option in PurchaseUi.SupplierFilter(distributors)) SupplierOptions.Add(option);
            SelectedSupplier = SupplierOptions.FirstOrDefault(o => o.Value == _pendingSupplierId) ?? SupplierOptions[0];
            _pendingSupplierId = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading distributors failed");
            SetError(PurchaseUi.Message(ex, "load distributors"));
        }
        _loaded = true;
        await RefreshAsync();
    }

    partial void OnSelectedSupplierChanged(PurchaseOption<long?>? value) => _ = RefreshIfLoadedAsync();
    partial void OnSelectedStatusChanged(PurchaseOption<PurchaseListStatusFilter>? value) => _ = RefreshIfLoadedAsync();

    private Task RefreshIfLoadedAsync() => _loaded ? RefreshAsync() : Task.CompletedTask;

    private PurchaseListFilter? BuildFilter()
    {
        if (FromDate is null || ToDate is null)
        {
            SetError("Choose both dates.");
            return null;
        }
        return new PurchaseListFilter(FromDate.Value.Date, ToDate.Value.Date, SelectedSupplier?.Value,
            SelectedStatus?.Value ?? PurchaseListStatusFilter.All, string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim());
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var filter = BuildFilter();
        if (filter == null) return;
        IsLoading = true;
        try
        {
            var selectedId = SelectedPurchase?.Id;
            var rows = await _entry.ListAsync(filter);
            Purchases.Clear();
            foreach (var row in rows) Purchases.Add(row);
            Summary = PurchaseListSummary.From(rows);
            SelectedPurchase = Purchases.FirstOrDefault(p => p.Id == selectedId) ?? Purchases.FirstOrDefault();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading purchases failed");
            SetError(PurchaseUi.Message(ex, "load purchases"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private void New() => _navigation.NavigateTo(PurchasePages.Entry);

    [RelayCommand]
    private void Open(PurchaseListRow? row)
    {
        row ??= SelectedPurchase;
        if (row == null) return;
        _navigation.NavigateTo(PurchasePages.Entry, new OpenPurchaseRequest(row.Id));
    }

    [RelayCommand]
    private void Edit(PurchaseListRow? row)
    {
        row ??= SelectedPurchase;
        if (row == null || row.Status == PurchaseStatus.Cancelled) return;
        _navigation.NavigateTo(PurchasePages.Entry, new OpenPurchaseRequest(row.Id, Edit: true));
    }

    [RelayCommand]
    private void Import() => _navigation.NavigateTo(PurchasePages.Import);

    [RelayCommand]
    private void ReturnItems(PurchaseListRow? row)
    {
        row ??= SelectedPurchase;
        if (row is not { Status: PurchaseStatus.Confirmed }) return;
        _navigation.NavigateTo(PurchasePages.Return, new PurchaseReturnRequest(row.SupplierId, row.Id));
    }

    [RelayCommand]
    private void OpenPaymentDialog(PurchaseListRow? row)
    {
        row ??= SelectedPurchase;
        if (row is not { Status: PurchaseStatus.Confirmed, BalancePaisa: > 0 })
        {
            SetError("Select a saved purchase with a balance due.");
            return;
        }
        SelectedPurchase = row;
        PaymentAmount = row.BalancePaisa / 100m;
        PaymentMethod = PurchaseUi.PaymentMethods[0];
        PaymentReference = string.Empty;
        PaymentDate = DateTime.Today;
        DialogError = null;
        IsPaymentDialogOpen = true;
    }

    [RelayCommand]
    private void ClosePaymentDialog() => IsPaymentDialogOpen = false;

    [RelayCommand]
    private async Task SavePaymentAsync()
    {
        if (SelectedPurchase == null) return;
        if (PaymentAmount <= 0 || PaymentMethod == null)
        {
            DialogError = "Enter the amount and how it was paid.";
            return;
        }
        try
        {
            var payment = new PurchasePaymentInput(PurchaseUi.Paisa(PaymentAmount), PaymentMethod.Value,
                string.IsNullOrWhiteSpace(PaymentReference) ? null : PaymentReference.Trim(), PaymentDate ?? DateTime.Today);
            await _entry.RecordPaymentAsync(SelectedPurchase.Id, payment, _auth.CurrentUser?.Id);
            IsPaymentDialogOpen = false;
            SetStatus($"Payment of ₹{PaymentAmount:N2} recorded for bill {SelectedPurchase.SupplierInvoiceNumber}.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording payment failed");
            DialogError = PurchaseUi.Message(ex, "record the payment");
        }
    }

    [RelayCommand]
    private void AskDelete(PurchaseListRow? row)
    {
        row ??= SelectedPurchase;
        if (row == null || row.Status == PurchaseStatus.Cancelled) return;
        SelectedPurchase = row;
        DeleteReason = string.Empty;
        DialogError = null;
        IsDeleteDialogOpen = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsDeleteDialogOpen = false;

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedPurchase == null) return;
        try
        {
            var label = SelectedPurchase.PurchaseNumber ?? $"draft of bill {SelectedPurchase.SupplierInvoiceNumber}";
            await _entry.DeleteAsync(SelectedPurchase.Id, DeleteReason, _auth.CurrentUser?.Id);
            IsDeleteDialogOpen = false;
            SetStatus($"Purchase {label} deleted.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deleting purchase failed");
            DialogError = PurchaseUi.Message(ex, "delete the purchase");
        }
    }

    [RelayCommand]
    private async Task PrintRegisterAsync()
    {
        var filter = BuildFilter();
        if (filter == null) return;
        try
        {
            var report = PurchaseRegisterReport.Create(filter, SelectedSupplier?.Value is null ? null : SelectedSupplier.Label, Purchases.ToList());
            await _print.PreviewAsync(_registerTemplate, report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing purchase register failed");
            SetError(PurchaseUi.Message(ex, "print the register"));
        }
    }

    [RelayCommand]
    private async Task PrintPurchaseAsync(PurchaseListRow? row)
    {
        row ??= SelectedPurchase;
        if (row == null) return;
        try
        {
            var bill = await _entry.GetAsync(row.Id) ?? throw new PurchaseRuleException("This purchase no longer exists.");
            await _print.PreviewAsync(_voucherTemplate, bill);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing purchase failed");
            SetError(PurchaseUi.Message(ex, "print the purchase"));
        }
    }
}
