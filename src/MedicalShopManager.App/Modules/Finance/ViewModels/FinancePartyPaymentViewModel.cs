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
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>
/// Receive payment from a customer (<see cref="FinanceReceivePaymentViewModel"/>) or pay a supplier
/// (<see cref="FinancePaySupplierViewModel"/>): find the party (F3), see the open bills and opening balance with their
/// age, type the amount (it settles the oldest dues first, MASTER-SPEC §3.5) or type what to pay against each bill, pick
/// the mode and account (cash / UPI / card / bank) or enter cheque details, and save (Alt+S) or save and print (Ctrl+P).
/// Anything not applied to a bill stays with the party on account (advance).
/// </summary>
public abstract partial class FinancePartyPaymentViewModel : BaseViewModel, INavigationAware
{
    private readonly IPartyPaymentService _service;
    private readonly ICashBankService _cashBank;
    private readonly IAuthService _auth;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<FinanceVoucher> _template;
    private readonly INavigationService _navigation;
    private readonly ILogger _logger;
    private IReadOnlyList<PaymentMethodAccount> _methodAccounts = Array.Empty<PaymentMethodAccount>();
    private OpenPartyPaymentRequest? _request;
    private bool _allocating;
    private int _searchVersion;

    protected FinancePartyPaymentViewModel(IPartyPaymentService service, ICashBankService cashBank, IAuthService auth, IPrintService print,
        IDocumentTemplate<FinanceVoucher> template, INavigationService navigation, ILogger logger)
    {
        _service = service;
        _cashBank = cashBank;
        _auth = auth;
        _print = print;
        _template = template;
        _navigation = navigation;
        _logger = logger;
        _selectedMode = Modes[0];
    }

    public abstract FinanceVoucherKind Kind { get; }
    public PartyType PartyType => Kind == FinanceVoucherKind.Receipt ? PartyType.Customer : PartyType.Supplier;
    public bool IsReceipt => Kind == FinanceVoucherKind.Receipt;
    public string Title => IsReceipt ? "Receive Payment" : "Pay Supplier";
    public string Subtitle => IsReceipt
        ? "Money received from a customer against credit bills (Payment In). The oldest dues are settled first."
        : "Money paid to a supplier against purchase bills (Payment Out). The oldest bills are settled first.";
    public string PartyLabel => IsReceipt ? "CUSTOMER *" : "SUPPLIER *";
    public string AccountLabel => IsReceipt ? "RECEIVED INTO *" : "PAID FROM *";
    public string AmountLabel => IsReceipt ? "AMOUNT RECEIVED ₹ *" : "AMOUNT PAID ₹ *";
    public string SaveLabel => IsReceipt ? "Save receipt (Alt+S)" : "Save payment (Alt+S)";

    public IReadOnlyList<FinanceOption<FinancePaymentMode>> Modes => FinanceUi.VoucherModes;
    public ObservableCollection<FinanceParty> PartyResults { get; } = new();
    public ObservableCollection<FinanceDueRow> Dues { get; } = new();
    public ObservableCollection<FinanceAccount> Accounts { get; } = new();
    public ObservableCollection<string> FormErrors { get; } = new();

    [ObservableProperty] private string _partySearchText = string.Empty;
    [ObservableProperty] private FinanceParty? _selectedPartyResult;
    [ObservableProperty] private FinanceParty? _party;
    [ObservableProperty] private DateTime? _paymentDate = DateTime.Today;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private bool _autoAllocate = true;
    [ObservableProperty] private FinanceOption<FinancePaymentMode> _selectedMode;
    [ObservableProperty] private FinanceAccount? _selectedAccount;
    [ObservableProperty] private string _chequeNumber = string.Empty;
    [ObservableProperty] private DateTime? _chequeDate = DateTime.Today;
    [ObservableProperty] private string _chequeBank = string.Empty;
    [ObservableProperty] private string _reference = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _nextNumber = string.Empty;
    [ObservableProperty] private long _totalDuePaisa;
    [ObservableProperty] private long _allocatedPaisa;
    [ObservableProperty] private FinanceVoucher? _lastVoucher;

    public bool HasParty => Party is not null;
    public bool HasNoParty => Party is null;
    public bool HasDues => Dues.Count > 0;
    public bool HasNoDues => Party is not null && Dues.Count == 0;
    public bool HasFormErrors => FormErrors.Count > 0;
    public bool IsCheque => SelectedMode.Value == FinancePaymentMode.Cheque;
    public bool ShowAccount => !IsCheque || !IsReceipt;
    public bool HasLastVoucher => LastVoucher is not null;
    public long AmountPaisa => FinanceUi.Paisa(Amount);
    public long OnAccountPaisa => Math.Max(0, AmountPaisa - AllocatedPaisa);
    public string PartySummary => Party is null ? string.Empty
        : $"{Party.Name}" + (string.IsNullOrWhiteSpace(Party.Phone) ? "" : $"  ·  {Party.Phone}") +
          (string.IsNullOrWhiteSpace(Party.Gstin) ? "" : $"  ·  GSTIN {Party.Gstin}") +
          $"  ·  {(Party.NetDuePaisa >= 0 ? (IsReceipt ? "owes" : "to pay") : "advance")} {FinanceUi.Money(Math.Abs(Party.NetDuePaisa))}";
    public string AllocationSummary => AmountPaisa <= 0 ? "Enter the amount."
        : AllocatedPaisa > AmountPaisa
            ? $"Bills selected add up to {FinanceUi.Money(AllocatedPaisa)}, more than the amount {FinanceUi.Money(AmountPaisa)}."
            : $"Against bills {FinanceUi.Money(AllocatedPaisa)}" + (OnAccountPaisa > 0 ? $"  ·  on account (advance) {FinanceUi.Money(OnAccountPaisa)}" : "");
    public string LastVoucherSummary => LastVoucher is null ? string.Empty
        : $"Last saved: {LastVoucher.VoucherNumber}  ·  {LastVoucher.PartyName}  ·  {FinanceUi.Money(LastVoucher.AmountPaisa)}  ·  {LastVoucher.ModeLabel}";

    partial void OnPartyChanged(FinanceParty? value)
    {
        OnPropertyChanged(nameof(HasParty));
        OnPropertyChanged(nameof(HasNoParty));
        OnPropertyChanged(nameof(PartySummary));
        OnPropertyChanged(nameof(HasNoDues));
    }

    partial void OnSelectedPartyResultChanged(FinanceParty? value)
    {
        if (value is not null) _ = SelectPartyAsync(value.PartyId);
    }

    partial void OnPartySearchTextChanged(string value) => _ = SearchPartiesAsync();

    partial void OnAmountChanged(decimal value)
    {
        if (AutoAllocate) AllocateOldestFirst();
        UpdateTotals();
    }

    partial void OnAutoAllocateChanged(bool value)
    {
        if (value) AllocateOldestFirst();
        UpdateTotals();
    }

    partial void OnSelectedModeChanged(FinanceOption<FinancePaymentMode> value)
    {
        OnPropertyChanged(nameof(IsCheque));
        OnPropertyChanged(nameof(ShowAccount));
        SelectAccountForMode();
    }

    partial void OnPaymentDateChanged(DateTime? value) => _ = PeekNumberAsync();
    partial void OnLastVoucherChanged(FinanceVoucher? value)
    {
        OnPropertyChanged(nameof(HasLastVoucher));
        OnPropertyChanged(nameof(LastVoucherSummary));
    }

    public void OnNavigatedTo(object? parameter) => _request = parameter as OpenPartyPaymentRequest;

    public override async Task LoadAsync()
    {
        try
        {
            var accounts = await _cashBank.GetAccountsAsync();
            _methodAccounts = await _cashBank.GetMethodAccountsAsync();
            Accounts.Clear();
            foreach (var a in accounts) Accounts.Add(a);
            if (_request is { } request && request.PartyType == PartyType)
            {
                if (request.Mode is { } mode) SelectedMode = Modes.First(m => m.Value == mode);
                await SelectPartyAsync(request.PartyId);
            }
            SelectAccountForMode();
            await PeekNumberAsync();
            await SearchPartiesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the payment screen failed");
            SetError(FinanceUi.Message(ex, "load the screen"));
        }
    }

    // ───────────────────────── Party ─────────────────────────

    [RelayCommand]
    private async Task SearchPartiesAsync()
    {
        var version = ++_searchVersion;
        try
        {
            var results = await _service.SearchPartiesAsync(PartyType, FinanceUi.Blank(PartySearchText));
            if (version != _searchVersion) return;
            PartyResults.Clear();
            foreach (var p in results) PartyResults.Add(p);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Party search failed");
            SetError(FinanceUi.Message(ex, "search"));
        }
    }

    private async Task SelectPartyAsync(long partyId)
    {
        try
        {
            var party = await _service.GetPartyAsync(PartyType, partyId);
            if (party is null)
            {
                SetError(IsReceipt ? "The customer was not found." : "The supplier was not found.");
                return;
            }
            Party = party;
            await LoadDuesAsync();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading party {PartyId} failed", partyId);
            SetError(FinanceUi.Message(ex, "load the dues"));
        }
    }

    private async Task LoadDuesAsync()
    {
        Dues.Clear();
        if (Party is null) return;
        var today = DateTime.Today;
        foreach (var due in await _service.GetDuesAsync(PartyType, Party.PartyId))
            Dues.Add(new FinanceDueRow(due, today, OnDueEdited));
        TotalDuePaisa = FinanceRules.TotalDue(Dues.Select(d => d.Due));
        OnPropertyChanged(nameof(HasDues));
        OnPropertyChanged(nameof(HasNoDues));
        if (AutoAllocate) AllocateOldestFirst();
        UpdateTotals();
    }

    [RelayCommand]
    private void ChangeParty()
    {
        Party = null;
        SelectedPartyResult = null;
        Dues.Clear();
        TotalDuePaisa = 0;
        OnPropertyChanged(nameof(HasDues));
        UpdateTotals();
    }

    // ───────────────────────── Allocation ─────────────────────────

    private void OnDueEdited(FinanceDueRow row)
    {
        if (_allocating) return;
        AutoAllocate = false;
        UpdateTotals();
    }

    private void AllocateOldestFirst()
    {
        _allocating = true;
        try
        {
            var allocations = FinanceRules.AllocateOldestFirst(AmountPaisa, Dues.Select(d => d.Due));
            foreach (var row in Dues)
            {
                var share = allocations.FirstOrDefault(a => a.Target == row.Due.Target && a.DocumentId == row.Due.DocumentId);
                row.SetSilently(share?.AmountPaisa ?? 0);
            }
        }
        finally
        {
            _allocating = false;
        }
    }

    [RelayCommand]
    private void PayFullDue()
    {
        if (TotalDuePaisa <= 0) return;
        AutoAllocate = true;
        Amount = FinanceUi.Rupees(TotalDuePaisa);
    }

    [RelayCommand]
    private void PayRowInFull(FinanceDueRow? row)
    {
        if (row is null) return;
        row.PayNow = FinanceUi.Rupees(row.BalancePaisa);
        if (AllocatedPaisa > AmountPaisa) Amount = FinanceUi.Rupees(AllocatedPaisa);
    }

    [RelayCommand]
    private void ResetAllocation() => AutoAllocate = true;

    private void UpdateTotals()
    {
        AllocatedPaisa = Dues.Sum(d => Math.Max(0, d.PayNowPaisa));
        OnPropertyChanged(nameof(AmountPaisa));
        OnPropertyChanged(nameof(OnAccountPaisa));
        OnPropertyChanged(nameof(AllocationSummary));
    }

    // ───────────────────────── Mode / account ─────────────────────────

    /// <summary>Cash → the account cash payments book into, UPI → the UPI account, and so on (Cash &amp; Bank's method map).</summary>
    private void SelectAccountForMode()
    {
        if (Accounts.Count == 0) return;
        var mode = SelectedMode.Value;
        if (mode == FinancePaymentMode.Cheque)
        {
            SelectedAccount = IsReceipt ? null : Accounts.FirstOrDefault(a => a.AccountType == FinanceAccountType.Bank) ?? SelectedAccount;
            return;
        }
        var mapped = _methodAccounts.FirstOrDefault(m => m.Method == mode.ToPaymentMethod())?.AccountId;
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == mapped)
                          ?? Accounts.FirstOrDefault(a => a.AccountType.DefaultMode() == mode)
                          ?? Accounts.FirstOrDefault();
    }

    private async Task PeekNumberAsync()
    {
        try
        {
            NextNumber = await _service.PeekNumberAsync(Kind, (PaymentDate ?? DateTime.Today).Date);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Peeking the voucher number failed");
            NextNumber = string.Empty;
        }
    }

    // ───────────────────────── Save / print ─────────────────────────

    [RelayCommand]
    private Task SaveAsync() => PostAsync(print: false);

    [RelayCommand]
    private Task SaveAndPrintAsync() => PostAsync(print: true);

    private async Task PostAsync(bool print)
    {
        if (IsLoading) return;
        if (Party is null)
        {
            SetFormErrors(new[] { IsReceipt ? "Choose the customer who paid (F3 to search)." : "Choose the supplier you paid (F3 to search)." });
            return;
        }
        IsLoading = true;
        try
        {
            var isCheque = IsCheque;
            var voucher = await _service.PostAsync(new VoucherRequest
            {
                Kind = Kind,
                PartyType = PartyType,
                PartyId = Party.PartyId,
                Date = (PaymentDate ?? DateTime.Today).Date,
                AmountPaisa = AmountPaisa,
                Mode = SelectedMode.Value,
                AccountId = isCheque && IsReceipt ? null : SelectedAccount?.Id,
                Cheque = isCheque ? new ChequeDetails(ChequeNumber.Trim(), (ChequeDate ?? DateTime.Today).Date, FinanceUi.Blank(ChequeBank)) : null,
                Reference = FinanceUi.Blank(Reference),
                Notes = FinanceUi.Blank(Notes),
                Allocations = AutoAllocate ? null : Dues.Where(d => d.PayNowPaisa > 0).Select(d => d.ToAllocation()).ToList(),
                UserId = _auth.CurrentUser?.Id
            });
            LastVoucher = voucher;
            SetFormErrors(Array.Empty<string>());
            ResetForm();
            IsLoading = false;
            await SelectPartyAsync(voucher.PartyId);
            await PeekNumberAsync();
            SetStatus($"{voucher.VoucherNumber} saved: {FinanceUi.Money(voucher.AmountPaisa)} " +
                      (IsReceipt ? $"received from {voucher.PartyName}" : $"paid to {voucher.PartyName}") +
                      (voucher.Allocations.Count > 0 ? $" against {voucher.AllocationSummary}" : " on account") +
                      (isCheque ? ". The cheque is in Cheques until it clears." : "."));
            if (print) await PrintVoucherAsync(voucher);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Posting {Kind} failed", Kind);
            SetFormErrors(FinanceUi.Messages(ex, IsReceipt ? "save the receipt" : "save the payment"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ResetForm()
    {
        AutoAllocate = true;
        Amount = 0;
        ChequeNumber = ChequeBank = Reference = Notes = string.Empty;
        ChequeDate = DateTime.Today;
        PaymentDate = DateTime.Today;
    }

    [RelayCommand]
    private async Task PrintLastAsync()
    {
        if (LastVoucher is not null) await PrintVoucherAsync(LastVoucher);
    }

    private async Task PrintVoucherAsync(FinanceVoucher voucher)
    {
        try
        {
            if (await _print.PrintAsync(_template, voucher)) SetStatus($"{voucher.VoucherNumber} sent to the printer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing voucher failed");
            SetError(FinanceUi.Message(ex, "print the receipt"));
        }
    }

    [RelayCommand]
    private void OpenLedger()
    {
        if (Party is null) return;
        _navigation.NavigateTo(FinancePages.PartyLedgers, new OpenPartyLedgerRequest(PartyType, Party.PartyId));
    }

    [RelayCommand]
    private void OpenRegister() => _navigation.NavigateTo(FinancePages.Vouchers);

    private void SetFormErrors(IEnumerable<string> messages)
    {
        FormErrors.Clear();
        foreach (var message in messages) FormErrors.Add(message);
        OnPropertyChanged(nameof(HasFormErrors));
    }
}
