using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>
/// Finance ▸ Payments In / Out (Localwell payment-in / payment-out lists): every receipt and supplier payment in a period
/// with party, mode, account, cheque and the bills it settled. Print (Ctrl+P / Enter preview), cancel (Del, with a reason:
/// the bills are due again; a cleared cheque cannot be cancelled), new receipt (Alt+R) or payment (Alt+P).
/// </summary>
public partial class FinanceVouchersViewModel : BaseViewModel
{
    private readonly IPartyPaymentService _service;
    private readonly IAuthService _auth;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<FinanceVoucher> _template;
    private readonly INavigationService _navigation;
    private readonly ILogger<FinanceVouchersViewModel> _logger;
    private bool _loaded;
    private bool _suppressReload;

    public FinanceVouchersViewModel(IPartyPaymentService service, IAuthService auth, IPrintService print, IDocumentTemplate<FinanceVoucher> template,
        INavigationService navigation, ILogger<FinanceVouchersViewModel> logger)
    {
        _service = service;
        _auth = auth;
        _print = print;
        _template = template;
        _navigation = navigation;
        _logger = logger;
        _selectedKind = KindFilters[0];
    }

    public IReadOnlyList<FinanceOption<FinanceVoucherKind?>> KindFilters => FinanceUi.VoucherKindFilters;
    public ObservableCollection<FinanceVoucher> Rows { get; } = new();
    public ObservableCollection<string> DialogErrors { get; } = new();

    [ObservableProperty] private DateTime? _fromDate = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;
    [ObservableProperty] private FinanceOption<FinanceVoucherKind?> _selectedKind;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _includeReversed = true;
    [ObservableProperty] private FinanceVoucher? _selectedRow;
    [ObservableProperty] private long _receivedPaisa;
    [ObservableProperty] private long _paidPaisa;
    [ObservableProperty] private int _activeCount;

    [ObservableProperty] private bool _isCancelDialogOpen;
    [ObservableProperty] private string _cancelReason = string.Empty;

    public bool IsEmpty => !IsLoading && Rows.Count == 0;
    public bool HasDialogErrors => DialogErrors.Count > 0;
    public long NetPaisa => ReceivedPaisa - PaidPaisa;
    public string CancelTarget => SelectedRow is null ? string.Empty
        : $"{SelectedRow.VoucherNumber}  ·  {SelectedRow.KindLabel}  ·  {SelectedRow.PartyName}  ·  {FinanceUi.Money(SelectedRow.AmountPaisa)}  ·  against {SelectedRow.AllocationSummary}";

    partial void OnFromDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnToDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnSelectedKindChanged(FinanceOption<FinanceVoucherKind?> value) => ReloadOnFilterChange();
    partial void OnIncludeReversedChanged(bool value) => ReloadOnFilterChange();
    partial void OnSelectedRowChanged(FinanceVoucher? value) => OnPropertyChanged(nameof(CancelTarget));
    partial void OnReceivedPaisaChanged(long value) => OnPropertyChanged(nameof(NetPaisa));
    partial void OnPaidPaisaChanged(long value) => OnPropertyChanged(nameof(NetPaisa));

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
            var rows = await _service.GetVouchersAsync(new VoucherFilter
            {
                From = FromDate.Value.Date,
                To = ToDate.Value.Date,
                Kind = SelectedKind.Value,
                SearchText = FinanceUi.Blank(SearchText),
                IncludeReversed = IncludeReversed
            });
            var selectedId = SelectedRow?.Id;
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            var active = rows.Where(v => v.IsActive).ToList();
            ActiveCount = active.Count;
            ReceivedPaisa = active.Where(v => v.Kind == FinanceVoucherKind.Receipt).Sum(v => v.AmountPaisa);
            PaidPaisa = active.Where(v => v.Kind == FinanceVoucherKind.Payment).Sum(v => v.AmountPaisa);
            SelectedRow = Rows.FirstOrDefault(r => r.Id == selectedId) ?? Rows.FirstOrDefault();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading receipts / payments failed");
            SetError(FinanceUi.Message(ex, "load the receipts and payments"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private Task TodayAsync() => SetRangeAsync(DateTime.Today, DateTime.Today);

    [RelayCommand]
    private Task ThisMonthAsync() => SetRangeAsync(FinanceUi.MonthStart(DateTime.Today), DateTime.Today);

    private Task SetRangeAsync(DateTime from, DateTime to)
    {
        _suppressReload = true;
        FromDate = from;
        ToDate = to;
        _suppressReload = false;
        return RefreshAsync();
    }

    [RelayCommand]
    private void NewReceipt() => _navigation.NavigateTo(FinancePages.ReceivePayment);

    [RelayCommand]
    private void NewPayment() => _navigation.NavigateTo(FinancePages.PaySupplier);

    [RelayCommand]
    private void OpenLedger(FinanceVoucher? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        _navigation.NavigateTo(FinancePages.PartyLedgers, new OpenPartyLedgerRequest(row.PartyType, row.PartyId));
    }

    [RelayCommand]
    private async Task PrintAsync(FinanceVoucher? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        try
        {
            if (await _print.PrintAsync(_template, row)) SetStatus($"{row.VoucherNumber} sent to the printer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing voucher {Id} failed", row.Id);
            SetError(FinanceUi.Message(ex, "print"));
        }
    }

    [RelayCommand]
    private async Task PreviewAsync(FinanceVoucher? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        try
        {
            await _print.PreviewAsync(_template, row);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Previewing voucher {Id} failed", row.Id);
            SetError(FinanceUi.Message(ex, "preview"));
        }
    }

    [RelayCommand]
    private void BeginCancel(FinanceVoucher? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        if (!row.IsActive)
        {
            SetError($"{row.VoucherNumber} is already {row.StatusLabel.ToLowerInvariant()}.");
            return;
        }
        if (row.ChequeStatus == ChequeStatus.Cleared)
        {
            SetError($"The cheque of {row.VoucherNumber} has cleared, so it cannot be cancelled.");
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
            await _service.CancelVoucherAsync(row.Id, CancelReason, _auth.CurrentUser?.Id);
            IsCancelDialogOpen = false;
            IsLoading = false;
            await RefreshAsync();
            SetStatus($"{row.VoucherNumber} cancelled. {row.AllocationSummary} {(row.Allocations.Count == 1 ? "is" : "are")} due again.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cancelling voucher {Id} failed", row.Id);
            SetDialogErrors(FinanceUi.Messages(ex, "cancel"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void CloseDialogs()
    {
        IsCancelDialogOpen = false;
        SetDialogErrors(Array.Empty<string>());
    }

    private void SetDialogErrors(IEnumerable<string> messages)
    {
        DialogErrors.Clear();
        foreach (var message in messages) DialogErrors.Add(message);
        OnPropertyChanged(nameof(HasDialogErrors));
    }
}
