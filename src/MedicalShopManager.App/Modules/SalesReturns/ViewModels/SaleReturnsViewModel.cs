using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Modules.SalesReturns;

namespace MedicalShopManager.App.Modules.SalesReturns.ViewModels;

/// <summary>
/// Invoices ▸ Sale Returns (Localwell /transactions/sale-returns): every return / credit note in a date range with
/// taxable, GST, value, refunded and open credit, and footer totals (cancelled excluded). The selected return's lines
/// and refunds show below. Actions: print / preview the credit note (Ctrl+P / F2), refund open credit (F4), cancel the
/// return (Del, with a reason; restocked units are taken back out), open the original bill, new return (Ctrl+Alt+T).
/// <see cref="SaleCreditNotesViewModel"/> reuses this screen for open customer credit.
/// </summary>
public partial class SaleReturnsViewModel : BaseViewModel
{
    private const string NewBillPageKey = "NewBill";

    private readonly ISaleReturnService _service;
    private readonly IAuthService _auth;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<SaleReturnDocument> _template;
    private readonly INavigationService _navigation;
    private readonly ILogger<SaleReturnsViewModel> _logger;
    private bool _loaded;
    private bool _suppressReload;
    private int _detailVersion;

    public SaleReturnsViewModel(ISaleReturnService service, IAuthService auth, IPrintService print,
        IDocumentTemplate<SaleReturnDocument> template, INavigationService navigation, ILogger<SaleReturnsViewModel> logger)
    {
        _service = service;
        _auth = auth;
        _print = print;
        _template = template;
        _navigation = navigation;
        _logger = logger;
        _selectedModeFilter = ModeFilters[0];
        _selectedRefundMethod = RefundMethods[0];
    }

    /// <summary>False: returns register. True: credit notes (open credit first).</summary>
    public virtual bool IsCreditNotesView => false;
    public string Title => IsCreditNotesView ? "Credit Notes" : "Sale Returns";
    public string Subtitle => IsCreditNotesView
        ? "Credit notes issued to customers for returned goods. Refund open credit with F4."
        : "Every sale return and its credit note. Enter shows the lines; Del cancels a return.";

    public IReadOnlyList<SalesReturnsOption<SaleRefundMode?>> ModeFilters => SalesReturnsUi.RefundModeFilters;
    public IReadOnlyList<SalesReturnsOption<PaymentMethod>> RefundMethods => SalesReturnsUi.RefundMethods;
    public ObservableCollection<SaleReturnRow> Rows { get; } = new();
    public ObservableCollection<string> DialogErrors { get; } = new();

    [ObservableProperty] private DateTime? _fromDate = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;
    [ObservableProperty] private SalesReturnsOption<SaleRefundMode?> _selectedModeFilter;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _openCreditOnly;
    [ObservableProperty] private bool _includeCancelled = true;
    [ObservableProperty] private SaleReturnRow? _selectedRow;
    [ObservableProperty] private SaleReturnDocument? _selectedDocument;
    [ObservableProperty] private SaleReturnTotals _totals = new();

    // ─── Cancel dialog ───
    [ObservableProperty] private bool _isCancelDialogOpen;
    [ObservableProperty] private string _cancelReason = string.Empty;

    // ─── Refund dialog ───
    [ObservableProperty] private bool _isRefundDialogOpen;
    [ObservableProperty] private decimal _refundAmount;
    [ObservableProperty] private SalesReturnsOption<PaymentMethod> _selectedRefundMethod;
    [ObservableProperty] private string _refundReference = string.Empty;
    [ObservableProperty] private DateTime? _refundDate = DateTime.Today;

    public bool IsEmpty => !IsLoading && Rows.Count == 0;
    public bool HasDocument => SelectedDocument is not null;
    public bool HasDialogErrors => DialogErrors.Count > 0;
    public bool ShowDateRange => !OpenCreditOnly;
    public string DialogTarget => SelectedRow is null ? string.Empty
        : $"{SelectedRow.CreditNoteNumber}  ·  {SelectedRow.CustomerDisplay}  ·  value ₹{SelectedRow.NetAmountPaisa / 100m:N2}  ·  open ₹{SelectedRow.CreditBalancePaisa / 100m:N2}";
    public string EmptyTitle => OpenCreditOnly ? "No open credit" : "No sale returns";
    public string EmptyHint => OpenCreditOnly
        ? "Every credit note has been refunded or settled."
        : "No returns in this period. Press Ctrl+Alt+T to return items against a bill or without one.";

    partial void OnFromDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnToDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnSelectedModeFilterChanged(SalesReturnsOption<SaleRefundMode?> value) => ReloadOnFilterChange();
    partial void OnIncludeCancelledChanged(bool value) => ReloadOnFilterChange();
    partial void OnOpenCreditOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDateRange));
        ReloadOnFilterChange();
    }

    partial void OnSelectedRowChanged(SaleReturnRow? value)
    {
        OnPropertyChanged(nameof(DialogTarget));
        _ = LoadDetailAsync(value);
    }

    partial void OnSelectedDocumentChanged(SaleReturnDocument? value) => OnPropertyChanged(nameof(HasDocument));

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
        if (!OpenCreditOnly && (FromDate is null || ToDate is null))
        {
            SetError("Choose both dates.");
            return;
        }
        IsLoading = true;
        try
        {
            var filter = new SaleReturnFilter
            {
                From = (FromDate ?? DateTime.Today).Date,
                To = (ToDate ?? DateTime.Today).Date,
                RefundMode = SelectedModeFilter.Value,
                SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                OpenCreditOnly = OpenCreditOnly,
                IncludeCancelled = IncludeCancelled
            };
            var (rows, totals) = await _service.ListAsync(filter);
            var selectedId = SelectedRow?.Id;
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            Totals = totals;
            SelectedRow = Rows.FirstOrDefault(r => r.Id == selectedId) ?? Rows.FirstOrDefault();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading sale returns failed");
            SetError(SalesReturnsUi.Message(ex, "load the sale returns"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyHint));
        }
    }

    private async Task LoadDetailAsync(SaleReturnRow? row)
    {
        var version = ++_detailVersion;
        if (row is null)
        {
            SelectedDocument = null;
            return;
        }
        try
        {
            var doc = await _service.GetAsync(row.Id);
            if (version == _detailVersion) SelectedDocument = doc;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading sale return {Id} failed", row.Id);
            SetError(SalesReturnsUi.Message(ex, "load the return"));
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
    private void NewReturn() => _navigation.NavigateTo(SalesReturnsPages.Entry);

    [RelayCommand]
    private void OpenBill(SaleReturnRow? row)
    {
        row ??= SelectedRow;
        if (row?.SaleId is not { } saleId)
        {
            SetError("This return was made without a bill.");
            return;
        }
        _navigation.NavigateTo(NewBillPageKey, new OpenSaleRequest(saleId));
    }

    private async Task<SaleReturnDocument?> DocumentForAsync(SaleReturnRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return null;
        return SelectedDocument?.Id == row.Id ? SelectedDocument : await _service.GetAsync(row.Id);
    }

    [RelayCommand]
    private async Task PrintAsync(SaleReturnRow? row)
    {
        try
        {
            var doc = await DocumentForAsync(row);
            if (doc is null) return;
            if (await _print.PrintAsync(_template, doc))
                SetStatus($"Credit note {doc.CreditNoteNumber} sent to the printer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing credit note failed");
            SetError(SalesReturnsUi.Message(ex, "print the credit note"));
        }
    }

    [RelayCommand]
    private async Task PreviewAsync(SaleReturnRow? row)
    {
        try
        {
            var doc = await DocumentForAsync(row);
            if (doc is not null) await _print.PreviewAsync(_template, doc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Previewing credit note failed");
            SetError(SalesReturnsUi.Message(ex, "preview the credit note"));
        }
    }

    // ───────────────────────── Cancel ─────────────────────────

    [RelayCommand]
    private void BeginCancel(SaleReturnRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        if (row.IsCancelled)
        {
            SetError($"Sale return {row.ReturnNumber} is already cancelled.");
            return;
        }
        SelectedRow = row;
        CancelReason = string.Empty;
        SetDialogErrors(Array.Empty<string>());
        IsCancelDialogOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmCancelAsync()
    {
        var row = SelectedRow;
        if (row is null || IsLoading) return;
        IsLoading = true;
        try
        {
            await _service.CancelAsync(row.Id, CancelReason, _auth.CurrentUser?.Id);
            IsCancelDialogOpen = false;
            SetStatus($"Sale return {row.ReturnNumber} cancelled. Restocked units were taken back out of stock.");
            IsLoading = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cancelling sale return {Id} failed", row.Id);
            SetDialogErrors(SalesReturnsUi.Messages(ex, "cancel the return"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ───────────────────────── Refund open credit ─────────────────────────

    [RelayCommand]
    private void BeginRefund(SaleReturnRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        if (!row.HasOpenCredit)
        {
            SetError($"Credit note {row.CreditNoteNumber} has no open credit to refund.");
            return;
        }
        SelectedRow = row;
        RefundAmount = row.CreditBalancePaisa / 100m;
        SelectedRefundMethod = RefundMethods[0];
        RefundReference = string.Empty;
        RefundDate = DateTime.Today;
        SetDialogErrors(Array.Empty<string>());
        IsRefundDialogOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmRefundAsync()
    {
        var row = SelectedRow;
        if (row is null || IsLoading) return;
        IsLoading = true;
        try
        {
            var note = await _service.RefundCreditAsync(new CreditRefundRequest
            {
                SaleReturnId = row.Id,
                AmountPaisa = SalesReturnsUi.Paisa(RefundAmount),
                Method = SelectedRefundMethod.Value,
                Reference = string.IsNullOrWhiteSpace(RefundReference) ? null : RefundReference.Trim(),
                RefundDate = (RefundDate ?? DateTime.Today).Date,
                UserId = _auth.CurrentUser?.Id
            });
            IsRefundDialogOpen = false;
            SetStatus($"Refunded ₹{RefundAmount:N2} against credit note {note.CreditNoteNumber}; ₹{note.CreditBalancePaisa / 100m:N2} still open.");
            IsLoading = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refunding credit note {Id} failed", row.Id);
            SetDialogErrors(SalesReturnsUi.Messages(ex, "record the refund"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Esc: closes an open dialog.</summary>
    [RelayCommand]
    private void CloseDialogs()
    {
        IsCancelDialogOpen = false;
        IsRefundDialogOpen = false;
        SetDialogErrors(Array.Empty<string>());
    }

    private void SetDialogErrors(IEnumerable<string> messages)
    {
        DialogErrors.Clear();
        foreach (var message in messages) DialogErrors.Add(message);
        OnPropertyChanged(nameof(HasDialogErrors));
    }
}
