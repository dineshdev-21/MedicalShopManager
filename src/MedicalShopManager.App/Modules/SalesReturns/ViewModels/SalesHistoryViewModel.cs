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
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Modules.SalesReturns;

namespace MedicalShopManager.App.Modules.SalesReturns.ViewModels;

/// <summary>
/// Invoices ▸ Sales: the sales register (Localwell /sale/view-sales and /transactions/sales). Filters by date range,
/// customer, payment status and mode, or searches by bill number / customer / mobile across all dates. Columns: bill
/// no, date, customer, items, amount, paid, due, mode, status, returned; footer totals exclude cancelled bills.
/// Actions: view (opens the bill on the billing screen), print / preview (sale invoice template), return (sale return
/// screen on that bill). Keys: Enter view, Ctrl+P print, F2 preview, Alt+R return, F5 refresh.
/// </summary>
public partial class SalesHistoryViewModel : BaseViewModel, INavigationAware
{
    private const string NewBillPageKey = "NewBill";

    private readonly ISalesRegisterService _register;
    private readonly IBillingService _billing;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<Sale> _invoiceTemplate;
    private readonly INavigationService _navigation;
    private readonly ILogger<SalesHistoryViewModel> _logger;
    private bool _loaded;
    private bool _suppressReload;

    public SalesHistoryViewModel(ISalesRegisterService register, IBillingService billing, IPrintService print,
        IDocumentTemplate<Sale> invoiceTemplate, INavigationService navigation, ILogger<SalesHistoryViewModel> logger)
    {
        _register = register;
        _billing = billing;
        _print = print;
        _invoiceTemplate = invoiceTemplate;
        _navigation = navigation;
        _logger = logger;
        _selectedStatus = StatusOptions[0];
        _selectedMode = ModeOptions[0];
    }

    public IReadOnlyList<SalesReturnsOption<SalesRegisterStatus>> StatusOptions => SalesReturnsUi.StatusFilters;
    public IReadOnlyList<SalesReturnsOption<PaymentMethod?>> ModeOptions => SalesReturnsUi.ModeFilters;
    public ObservableCollection<SalesRegisterRow> Rows { get; } = new();

    [ObservableProperty] private DateTime? _fromDate = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;
    [ObservableProperty] private SalesReturnsOption<SalesRegisterStatus> _selectedStatus;
    [ObservableProperty] private SalesReturnsOption<PaymentMethod?> _selectedMode;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private SalesRegisterRow? _selectedRow;
    [ObservableProperty] private SalesRegisterTotals _totals = new();
    [ObservableProperty] private long? _customerId;
    [ObservableProperty] private string? _customerName;

    public bool IsEmpty => !IsLoading && Rows.Count == 0;
    public bool HasCustomerFilter => CustomerId is > 0;
    public string CustomerFilterLabel => HasCustomerFilter ? $"Customer: {CustomerName ?? "#" + CustomerId}" : string.Empty;
    public string EmptyHint => string.IsNullOrWhiteSpace(SearchText)
        ? "No bills in this period. Change the dates or filters, or press Ctrl+Alt+S for a new bill."
        : $"No bill matches \"{SearchText.Trim()}\". Search looks in bill numbers, customer names and mobile numbers across all dates.";

    partial void OnFromDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnToDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnSelectedStatusChanged(SalesReturnsOption<SalesRegisterStatus> value) => ReloadOnFilterChange();
    partial void OnSelectedModeChanged(SalesReturnsOption<PaymentMethod?> value) => ReloadOnFilterChange();
    partial void OnCustomerIdChanged(long? value)
    {
        OnPropertyChanged(nameof(HasCustomerFilter));
        OnPropertyChanged(nameof(CustomerFilterLabel));
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is SalesRegisterCustomerRequest customer)
        {
            CustomerId = customer.CustomerId;
            CustomerName = customer.CustomerName;
            _suppressReload = true;
            FromDate = DateTime.Today.AddYears(-1);
            _suppressReload = false;
        }
    }

    public override async Task LoadAsync()
    {
        _loaded = true;
        await RefreshAsync();
    }

    private void ReloadOnFilterChange()
    {
        if (_loaded && !_suppressReload) _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (FromDate is null || ToDate is null)
        {
            SetError("Choose both dates.");
            return;
        }
        IsLoading = true;
        try
        {
            var filter = new SalesRegisterFilter
            {
                From = FromDate.Value.Date,
                To = ToDate.Value.Date,
                CustomerId = CustomerId,
                Status = SelectedStatus.Value,
                Mode = SelectedMode.Value,
                SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim()
            };
            var (rows, totals) = await _register.QueryAsync(filter);
            var selectedId = SelectedRow?.SaleId;
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            Totals = totals;
            SelectedRow = Rows.FirstOrDefault(r => r.SaleId == selectedId) ?? Rows.FirstOrDefault();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the sales register failed");
            SetError(SalesReturnsUi.Message(ex, "load the sales register"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyHint));
        }
    }

    [RelayCommand]
    private Task TodayAsync() => SetRangeAsync(DateTime.Today, DateTime.Today);

    [RelayCommand]
    private Task ThisMonthAsync() => SetRangeAsync(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today);

    [RelayCommand]
    private Task Last30DaysAsync() => SetRangeAsync(DateTime.Today.AddDays(-30), DateTime.Today);

    private Task SetRangeAsync(DateTime from, DateTime to)
    {
        _suppressReload = true;
        FromDate = from;
        ToDate = to;
        _suppressReload = false;
        return RefreshAsync();
    }

    [RelayCommand]
    private Task ClearCustomerFilterAsync()
    {
        CustomerId = null;
        CustomerName = null;
        return RefreshAsync();
    }

    /// <summary>Opens the bill on the billing screen (view / print / edit there).</summary>
    [RelayCommand]
    private void View(SalesRegisterRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        _navigation.NavigateTo(NewBillPageKey, new OpenSaleRequest(row.SaleId));
    }

    [RelayCommand]
    private void Return(SalesRegisterRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        if (!row.CanReturn)
        {
            SetError($"Bill {row.InvoiceNumber} is cancelled; nothing can be returned against it.");
            return;
        }
        _navigation.NavigateTo(SalesReturnsPages.Entry, new StartSaleReturnRequest(row.SaleId));
    }

    [RelayCommand]
    private async Task PrintAsync(SalesRegisterRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        try
        {
            var sale = await _billing.GetSaleAsync(row.SaleId);
            if (sale is null)
            {
                SetError($"Bill {row.InvoiceNumber} was not found.");
                return;
            }
            if (await _print.PrintAsync(_invoiceTemplate, sale))
                SetStatus($"Invoice {sale.InvoiceNumber} sent to the printer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing invoice {SaleId} failed", row.SaleId);
            SetError(SalesReturnsUi.Message(ex, "print the invoice"));
        }
    }

    [RelayCommand]
    private async Task PreviewAsync(SalesRegisterRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        try
        {
            var sale = await _billing.GetSaleAsync(row.SaleId);
            if (sale is null)
            {
                SetError($"Bill {row.InvoiceNumber} was not found.");
                return;
            }
            await _print.PreviewAsync(_invoiceTemplate, sale);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Previewing invoice {SaleId} failed", row.SaleId);
            SetError(SalesReturnsUi.Message(ex, "preview the invoice"));
        }
    }
}
