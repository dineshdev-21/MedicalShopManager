using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>
/// Finance ▸ Cheques (Localwell /cash-and-bank/cheque-management): cheques received from customers and issued to suppliers,
/// open ones first (post-dated highlighted). Deposit (Alt+D), clear (Alt+C: the money enters / leaves the bank account on
/// that date), bounce (Alt+B: the receipt / payment is reversed and the bills are due again) or cancel (Del).
/// </summary>
public partial class FinanceChequesViewModel : BaseViewModel
{
    private readonly IChequeService _service;
    private readonly ICashBankService _cashBank;
    private readonly IAuthService _auth;
    private readonly ILogger<FinanceChequesViewModel> _logger;
    private bool _loaded;

    public FinanceChequesViewModel(IChequeService service, ICashBankService cashBank, IAuthService auth, ILogger<FinanceChequesViewModel> logger)
    {
        _service = service;
        _cashBank = cashBank;
        _auth = auth;
        _logger = logger;
        _selectedDirection = Directions[0];
        _selectedStatus = Statuses[0];
    }

    public IReadOnlyList<FinanceOption<ChequeDirection?>> Directions => FinanceUi.ChequeDirections;
    public IReadOnlyList<FinanceOption<ChequeStatus?>> Statuses => FinanceUi.ChequeStatuses;
    public ObservableCollection<FinanceCheque> Rows { get; } = new();
    public ObservableCollection<FinanceAccount> BankAccounts { get; } = new();
    public ObservableCollection<string> DialogErrors { get; } = new();

    [ObservableProperty] private bool _openOnly = true;
    [ObservableProperty] private DateTime? _fromDate = DateTime.Today.AddDays(-90);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today.AddDays(90);
    [ObservableProperty] private FinanceOption<ChequeDirection?> _selectedDirection;
    [ObservableProperty] private FinanceOption<ChequeStatus?> _selectedStatus;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FinanceCheque? _selectedRow;
    [ObservableProperty] private long _pendingInPaisa;
    [ObservableProperty] private long _pendingOutPaisa;
    [ObservableProperty] private int _postDatedCount;

    // ─── Status dialog ───
    [ObservableProperty] private bool _isStatusDialogOpen;
    [ObservableProperty] private ChequeStatus _newStatus;
    [ObservableProperty] private DateTime? _statusDate = DateTime.Today;
    [ObservableProperty] private FinanceAccount? _statusAccount;
    [ObservableProperty] private string _statusNote = string.Empty;

    public bool IsEmpty => !IsLoading && Rows.Count == 0;
    public bool HasDialogErrors => DialogErrors.Count > 0;
    public bool ShowDateRange => !OpenOnly;
    public bool StatusNeedsAccount => NewStatus is ChequeStatus.Deposited or ChequeStatus.Cleared;
    public bool StatusNeedsNote => NewStatus is ChequeStatus.Bounced or ChequeStatus.Cancelled;
    public string StatusDialogTitle => NewStatus switch
    {
        ChequeStatus.Deposited => "Deposit cheque",
        ChequeStatus.Cleared => "Cheque cleared",
        ChequeStatus.Bounced => "Cheque bounced",
        _ => "Cancel cheque"
    };
    public string StatusDialogHint => NewStatus switch
    {
        ChequeStatus.Deposited => "The money moves only when the cheque clears.",
        ChequeStatus.Cleared => "The amount enters (received) or leaves (issued) the bank account on this date.",
        ChequeStatus.Bounced => "The receipt / payment is reversed: the bills it settled are due again and the party's ledger shows the bounce.",
        _ => "The receipt / payment is cancelled and the bills it settled are due again."
    };
    public string StatusTarget => SelectedRow is null ? string.Empty
        : $"Cheque {SelectedRow.ChequeNumber}  ·  {SelectedRow.DirectionLabel}  ·  {SelectedRow.PartyName}  ·  {FinanceUi.Money(SelectedRow.AmountPaisa)}  ·  dated {SelectedRow.ChequeDate:dd-MM-yyyy}";

    partial void OnOpenOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDateRange));
        Reload();
    }

    partial void OnFromDateChanged(DateTime? value) => Reload();
    partial void OnToDateChanged(DateTime? value) => Reload();
    partial void OnSelectedDirectionChanged(FinanceOption<ChequeDirection?> value) => Reload();
    partial void OnSelectedStatusChanged(FinanceOption<ChequeStatus?> value) => Reload();
    partial void OnSelectedRowChanged(FinanceCheque? value) => OnPropertyChanged(nameof(StatusTarget));

    partial void OnNewStatusChanged(ChequeStatus value)
    {
        OnPropertyChanged(nameof(StatusNeedsAccount));
        OnPropertyChanged(nameof(StatusNeedsNote));
        OnPropertyChanged(nameof(StatusDialogTitle));
        OnPropertyChanged(nameof(StatusDialogHint));
    }

    private void Reload()
    {
        if (_loaded) _ = RefreshAsync();
    }

    public override async Task LoadAsync()
    {
        try
        {
            var accounts = await _cashBank.GetAccountsAsync();
            BankAccounts.Clear();
            foreach (var a in accounts.Where(a => a.AccountType != FinanceAccountType.Cash)) BankAccounts.Add(a);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading bank accounts failed");
            SetError(FinanceUi.Message(ex, "load the bank accounts"));
        }
        _loaded = true;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!OpenOnly && (FromDate is null || ToDate is null))
        {
            SetError("Choose both dates.");
            return;
        }
        IsLoading = true;
        try
        {
            var rows = await _service.GetChequesAsync(new ChequeFilter
            {
                OpenOnly = OpenOnly,
                From = (FromDate ?? DateTime.Today).Date,
                To = (ToDate ?? DateTime.Today).Date,
                Direction = SelectedDirection.Value,
                Status = SelectedStatus.Value,
                SearchText = FinanceUi.Blank(SearchText)
            });
            var selectedId = SelectedRow?.Id;
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            var open = rows.Where(c => c.IsOpen).ToList();
            PendingInPaisa = open.Where(c => c.Direction == ChequeDirection.Received).Sum(c => c.AmountPaisa);
            PendingOutPaisa = open.Where(c => c.Direction == ChequeDirection.Issued).Sum(c => c.AmountPaisa);
            PostDatedCount = open.Count(c => c.IsPostDated(DateTime.Today));
            SelectedRow = Rows.FirstOrDefault(r => r.Id == selectedId) ?? Rows.FirstOrDefault();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading cheques failed");
            SetError(FinanceUi.Message(ex, "load the cheques"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private void BeginDeposit(FinanceCheque? row) => BeginChange(row, ChequeStatus.Deposited);

    [RelayCommand]
    private void BeginClear(FinanceCheque? row) => BeginChange(row, ChequeStatus.Cleared);

    [RelayCommand]
    private void BeginBounce(FinanceCheque? row) => BeginChange(row, ChequeStatus.Bounced);

    [RelayCommand]
    private void BeginCancel(FinanceCheque? row) => BeginChange(row, ChequeStatus.Cancelled);

    private void BeginChange(FinanceCheque? row, ChequeStatus status)
    {
        row ??= SelectedRow;
        if (row is null) return;
        if (!FinanceRules.CanTransition(row.Status, status))
        {
            SetError($"A {row.StatusLabel.ToLowerInvariant()} cheque cannot be marked {status.ToLabel().ToLowerInvariant()}.");
            return;
        }
        SelectedRow = row;
        NewStatus = status;
        StatusDate = status == ChequeStatus.Cleared && row.ChequeDate.Date > DateTime.Today ? row.ChequeDate.Date : DateTime.Today;
        StatusAccount = BankAccounts.FirstOrDefault(a => a.Id == row.AccountId) ?? BankAccounts.FirstOrDefault();
        StatusNote = string.Empty;
        SetDialogErrors(Array.Empty<string>());
        IsStatusDialogOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmChangeAsync()
    {
        var row = SelectedRow;
        if (row is null || IsLoading) return;
        IsLoading = true;
        try
        {
            await _service.ChangeStatusAsync(new ChequeStatusChange
            {
                ChequeId = row.Id,
                NewStatus = NewStatus,
                Date = (StatusDate ?? DateTime.Today).Date,
                AccountId = StatusNeedsAccount ? StatusAccount?.Id : null,
                Note = FinanceUi.Blank(StatusNote),
                UserId = _auth.CurrentUser?.Id
            });
            IsStatusDialogOpen = false;
            IsLoading = false;
            await RefreshAsync();
            SetStatus(NewStatus switch
            {
                ChequeStatus.Deposited => $"Cheque {row.ChequeNumber} deposited into {StatusAccount?.Name}.",
                ChequeStatus.Cleared => $"Cheque {row.ChequeNumber} cleared: {FinanceUi.Money(row.AmountPaisa)} " +
                                        (row.Direction == ChequeDirection.Received ? "added to " : "paid from ") + StatusAccount?.Name + ".",
                ChequeStatus.Bounced => $"Cheque {row.ChequeNumber} bounced. {row.VoucherNumber} is reversed and {row.PartyName}'s bills are due again.",
                _ => $"Cheque {row.ChequeNumber} cancelled."
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Changing cheque {Id} failed", row.Id);
            SetDialogErrors(FinanceUi.Messages(ex, "update the cheque"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void CloseDialogs()
    {
        IsStatusDialogOpen = false;
        SetDialogErrors(Array.Empty<string>());
    }

    private void SetDialogErrors(IEnumerable<string> messages)
    {
        DialogErrors.Clear();
        foreach (var message in messages) DialogErrors.Add(message);
        OnPropertyChanged(nameof(HasDialogErrors));
    }
}
