using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>
/// Finance ▸ Cash &amp; Bank (Localwell /cash-and-bank): the shop's money accounts (cash in hand, bank accounts, UPI wallets,
/// card POS) with opening balance and current balance derived from every money movement; add / edit an account
/// (Alt+N / Enter), transfers between accounts and add / reduce money entries (Ctrl+T), which account each bill payment
/// method books into, recent transfers with cancel, and the account statement (F3).
/// </summary>
public partial class FinanceCashBankViewModel : BaseViewModel
{
    private readonly ICashBankService _service;
    private readonly IAuthService _auth;
    private readonly INavigationService _navigation;
    private readonly ILogger<FinanceCashBankViewModel> _logger;
    private bool _loaded;

    public FinanceCashBankViewModel(ICashBankService service, IAuthService auth, INavigationService navigation, ILogger<FinanceCashBankViewModel> logger)
    {
        _service = service;
        _auth = auth;
        _navigation = navigation;
        _logger = logger;
        _editType = AccountTypes[1];
        _moveKind = MovementKinds[0];
    }

    public IReadOnlyList<FinanceOption<FinanceAccountType>> AccountTypes => FinanceUi.AccountTypes;
    public IReadOnlyList<FinanceOption<FinanceMovementKind>> MovementKinds => FinanceUi.MovementKinds;
    public ObservableCollection<FinanceAccount> Accounts { get; } = new();
    public ObservableCollection<FinanceAccount> ActiveAccounts { get; } = new();
    public ObservableCollection<FinanceMethodAccountRow> MethodAccounts { get; } = new();
    public ObservableCollection<FinanceMovement> Movements { get; } = new();
    public ObservableCollection<string> DialogErrors { get; } = new();

    [ObservableProperty] private bool _includeInactive;
    [ObservableProperty] private FinanceAccount? _selectedAccount;
    [ObservableProperty] private FinanceMovement? _selectedMovement;
    [ObservableProperty] private DateTime? _movementsFrom = FinanceUi.MonthStart(DateTime.Today);
    [ObservableProperty] private DateTime? _movementsTo = DateTime.Today;
    [ObservableProperty] private long _cashTotalPaisa;
    [ObservableProperty] private long _bankTotalPaisa;
    [ObservableProperty] private long _overallTotalPaisa;

    // ─── Account editor ───
    [ObservableProperty] private bool _isAccountEditorOpen;
    [ObservableProperty] private long _editId;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private FinanceOption<FinanceAccountType> _editType;
    [ObservableProperty] private string _editBankName = string.Empty;
    [ObservableProperty] private string _editAccountNumber = string.Empty;
    [ObservableProperty] private string _editIfsc = string.Empty;
    [ObservableProperty] private string _editHolder = string.Empty;
    [ObservableProperty] private string _editUpiId = string.Empty;
    [ObservableProperty] private decimal _editOpening;
    [ObservableProperty] private DateTime? _editOpeningDate = DateTime.Today;
    [ObservableProperty] private string _editNotes = string.Empty;

    // ─── Movement (transfer / add / reduce) ───
    [ObservableProperty] private bool _isMovementOpen;
    [ObservableProperty] private FinanceOption<FinanceMovementKind> _moveKind;
    [ObservableProperty] private DateTime? _moveDate = DateTime.Today;
    [ObservableProperty] private FinanceAccount? _moveFrom;
    [ObservableProperty] private FinanceAccount? _moveTo;
    [ObservableProperty] private decimal _moveAmount;
    [ObservableProperty] private string _moveReference = string.Empty;
    [ObservableProperty] private string _moveNotes = string.Empty;

    // ─── Cancel movement ───
    [ObservableProperty] private bool _isCancelMovementOpen;
    [ObservableProperty] private string _cancelReason = string.Empty;

    public bool IsEmpty => !IsLoading && Accounts.Count == 0;
    public bool HasNoMovements => Movements.Count == 0;
    public bool HasDialogErrors => DialogErrors.Count > 0;
    public bool IsBankType => EditType.Value == FinanceAccountType.Bank;
    public bool IsUpiType => EditType.Value == FinanceAccountType.UpiWallet;
    public bool ShowMoveFrom => MoveKind.Value != FinanceMovementKind.AddMoney;
    public bool ShowMoveTo => MoveKind.Value != FinanceMovementKind.ReduceMoney;
    public string AccountEditorTitle => EditId > 0 ? $"Edit account: {EditName}" : "New account";
    public string ToggleActiveLabel => SelectedAccount is { IsActive: false } ? "Reactivate" : "Deactivate";
    public string CancelMovementTarget => SelectedMovement is null ? string.Empty
        : $"{SelectedMovement.KindLabel}  ·  {SelectedMovement.MovementDate:dd-MM-yyyy}  ·  {FinanceUi.Money(SelectedMovement.AmountPaisa)}  ·  " +
          $"{SelectedMovement.FromAccountName ?? "—"} → {SelectedMovement.ToAccountName ?? "—"}";

    partial void OnIncludeInactiveChanged(bool value) { if (_loaded) _ = RefreshAsync(); }
    partial void OnMovementsFromChanged(DateTime? value) { if (_loaded) _ = LoadMovementsAsync(); }
    partial void OnMovementsToChanged(DateTime? value) { if (_loaded) _ = LoadMovementsAsync(); }
    partial void OnSelectedAccountChanged(FinanceAccount? value) => OnPropertyChanged(nameof(ToggleActiveLabel));
    partial void OnSelectedMovementChanged(FinanceMovement? value) => OnPropertyChanged(nameof(CancelMovementTarget));
    partial void OnEditIdChanged(long value) => OnPropertyChanged(nameof(AccountEditorTitle));

    partial void OnEditTypeChanged(FinanceOption<FinanceAccountType> value)
    {
        OnPropertyChanged(nameof(IsBankType));
        OnPropertyChanged(nameof(IsUpiType));
    }

    partial void OnMoveKindChanged(FinanceOption<FinanceMovementKind> value)
    {
        OnPropertyChanged(nameof(ShowMoveFrom));
        OnPropertyChanged(nameof(ShowMoveTo));
    }

    public override async Task LoadAsync()
    {
        _loaded = true;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var accounts = await _service.GetAccountsAsync(IncludeInactive);
            var all = IncludeInactive ? accounts : await _service.GetAccountsAsync(includeInactive: true);
            var mappings = await _service.GetMethodAccountsAsync();
            var selectedId = SelectedAccount?.Id;

            Accounts.Clear();
            foreach (var a in accounts) Accounts.Add(a);
            ActiveAccounts.Clear();
            foreach (var a in all.Where(a => a.IsActive)) ActiveAccounts.Add(a);
            CashTotalPaisa = all.Where(a => a.AccountType == FinanceAccountType.Cash).Sum(a => a.BalancePaisa);
            BankTotalPaisa = all.Where(a => a.AccountType != FinanceAccountType.Cash).Sum(a => a.BalancePaisa);
            OverallTotalPaisa = CashTotalPaisa + BankTotalPaisa;

            MethodAccounts.Clear();
            foreach (var mapping in mappings.Where(m => FinanceUi.MappedMethods.Contains(m.Method)))
                MethodAccounts.Add(new FinanceMethodAccountRow(mapping, ActiveAccounts.FirstOrDefault(a => a.Id == mapping.AccountId), SaveMethodAccountAsync));

            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == selectedId) ?? Accounts.FirstOrDefault();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading accounts failed");
            SetError(FinanceUi.Message(ex, "load the accounts"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
        await LoadMovementsAsync();
    }

    private async Task LoadMovementsAsync()
    {
        if (MovementsFrom is null || MovementsTo is null) return;
        try
        {
            var list = await _service.GetMovementsAsync(MovementsFrom.Value.Date, MovementsTo.Value.Date);
            Movements.Clear();
            foreach (var m in list) Movements.Add(m);
            OnPropertyChanged(nameof(HasNoMovements));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading transfers failed");
            SetError(FinanceUi.Message(ex, "load the transfers"));
        }
    }

    private async Task SaveMethodAccountAsync(FinanceMethodAccountRow row, FinanceAccount? previous)
    {
        if (row.Account is not { } account) return;
        try
        {
            await _service.SetMethodAccountAsync(row.Method, account.Id, _auth.CurrentUser?.Id);
            SetStatus($"{row.Label} payments from bills now book into {account.Name}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Changing the {Method} account failed", row.Method);
            row.Revert(previous);
            SetError(FinanceUi.Message(ex, "change the account"));
        }
    }

    // ───────────────────────── Account editor ─────────────────────────

    [RelayCommand]
    private void NewAccount()
    {
        EditId = 0;
        EditName = EditBankName = EditAccountNumber = EditIfsc = EditHolder = EditUpiId = EditNotes = string.Empty;
        EditType = AccountTypes.First(t => t.Value == FinanceAccountType.Bank);
        EditOpening = 0;
        EditOpeningDate = DateTime.Today;
        SetDialogErrors(Array.Empty<string>());
        IsAccountEditorOpen = true;
    }

    [RelayCommand]
    private void EditAccount(FinanceAccount? account)
    {
        account ??= SelectedAccount;
        if (account is null) return;
        SelectedAccount = account;
        EditId = account.Id;
        EditName = account.Name;
        EditType = AccountTypes.First(t => t.Value == account.AccountType);
        EditBankName = account.BankName ?? string.Empty;
        EditAccountNumber = account.AccountNumber ?? string.Empty;
        EditIfsc = account.Ifsc ?? string.Empty;
        EditHolder = account.HolderName ?? string.Empty;
        EditUpiId = account.UpiId ?? string.Empty;
        EditOpening = FinanceUi.Rupees(account.OpeningBalancePaisa);
        EditOpeningDate = account.OpeningDate;
        EditNotes = account.Notes ?? string.Empty;
        OnPropertyChanged(nameof(AccountEditorTitle));
        SetDialogErrors(Array.Empty<string>());
        IsAccountEditorOpen = true;
    }

    [RelayCommand]
    private async Task SaveAccountAsync()
    {
        if (IsLoading) return;
        if (IsMovementOpen)
        {
            await SaveMovementAsync();
            return;
        }
        if (!IsAccountEditorOpen) return;
        IsLoading = true;
        try
        {
            var isBank = EditType.Value == FinanceAccountType.Bank;
            var saved = await _service.SaveAccountAsync(new FinanceAccount
            {
                Id = EditId,
                Name = EditName.Trim(),
                AccountType = EditType.Value,
                BankName = isBank ? FinanceUi.Blank(EditBankName) : null,
                AccountNumber = isBank ? FinanceUi.Blank(EditAccountNumber) : null,
                Ifsc = isBank ? FinanceUi.Blank(EditIfsc) : null,
                HolderName = isBank ? FinanceUi.Blank(EditHolder) : null,
                UpiId = EditType.Value == FinanceAccountType.UpiWallet ? FinanceUi.Blank(EditUpiId) : null,
                OpeningBalancePaisa = FinanceUi.Paisa(EditOpening),
                OpeningDate = (EditOpeningDate ?? DateTime.Today).Date,
                Notes = FinanceUi.Blank(EditNotes)
            }, _auth.CurrentUser?.Id);
            IsAccountEditorOpen = false;
            IsLoading = false;
            await RefreshAsync();
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == saved.Id) ?? SelectedAccount;
            SetStatus($"Account {saved.Name} saved.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving account failed");
            SetDialogErrors(FinanceUi.Messages(ex, "save the account"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleActiveAsync(FinanceAccount? account)
    {
        account ??= SelectedAccount;
        if (account is null || IsLoading) return;
        IsLoading = true;
        try
        {
            await _service.SetAccountActiveAsync(account.Id, !account.IsActive, _auth.CurrentUser?.Id);
            IsLoading = false;
            await RefreshAsync();
            SetStatus(account.IsActive ? $"{account.Name} deactivated. Its history stays in the statements." : $"{account.Name} is active again.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Changing account {Id} status failed", account.Id);
            SetError(FinanceUi.Message(ex, "change the account"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenStatement(FinanceAccount? account)
    {
        account ??= SelectedAccount;
        if (account is null) return;
        _navigation.NavigateTo(FinancePages.AccountStatement, new OpenAccountStatementRequest(account.Id));
    }

    // ───────────────────────── Transfers / adjustments ─────────────────────────

    [RelayCommand]
    private void NewMovement() => OpenMovement(FinanceMovementKind.Transfer);

    [RelayCommand]
    private void NewAddMoney() => OpenMovement(FinanceMovementKind.AddMoney);

    [RelayCommand]
    private void NewReduceMoney() => OpenMovement(FinanceMovementKind.ReduceMoney);

    private void OpenMovement(FinanceMovementKind kind)
    {
        MoveKind = MovementKinds.First(k => k.Value == kind);
        MoveDate = DateTime.Today;
        var cash = ActiveAccounts.FirstOrDefault(a => a.AccountType == FinanceAccountType.Cash);
        MoveFrom = kind == FinanceMovementKind.Transfer ? cash : SelectedAccount is { IsActive: true } s ? s : cash;
        MoveTo = kind == FinanceMovementKind.Transfer
            ? ActiveAccounts.FirstOrDefault(a => a.AccountType == FinanceAccountType.Bank) ?? ActiveAccounts.FirstOrDefault(a => a.Id != MoveFrom?.Id)
            : MoveFrom;
        MoveAmount = 0;
        MoveReference = MoveNotes = string.Empty;
        SetDialogErrors(Array.Empty<string>());
        IsMovementOpen = true;
    }

    [RelayCommand]
    private async Task SaveMovementAsync()
    {
        if (!IsMovementOpen || IsLoading) return;
        IsLoading = true;
        try
        {
            var kind = MoveKind.Value;
            await _service.RecordMovementAsync(new FinanceMovement
            {
                Kind = kind,
                MovementDate = (MoveDate ?? DateTime.Today).Date,
                FromAccountId = kind == FinanceMovementKind.AddMoney ? null : MoveFrom?.Id,
                ToAccountId = kind == FinanceMovementKind.ReduceMoney ? null : MoveTo?.Id,
                AmountPaisa = FinanceUi.Paisa(MoveAmount),
                Reference = FinanceUi.Blank(MoveReference),
                Notes = FinanceUi.Blank(MoveNotes)
            }, _auth.CurrentUser?.Id);
            IsMovementOpen = false;
            IsLoading = false;
            await RefreshAsync();
            SetStatus(kind switch
            {
                FinanceMovementKind.Transfer => $"Transferred {FinanceUi.Money(FinanceUi.Paisa(MoveAmount))} from {MoveFrom?.Name} to {MoveTo?.Name}.",
                FinanceMovementKind.AddMoney => $"Added {FinanceUi.Money(FinanceUi.Paisa(MoveAmount))} to {MoveTo?.Name}.",
                _ => $"Took {FinanceUi.Money(FinanceUi.Paisa(MoveAmount))} out of {MoveFrom?.Name}."
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording transfer failed");
            SetDialogErrors(FinanceUi.Messages(ex, "record the entry"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void BeginCancelMovement(FinanceMovement? movement)
    {
        movement ??= SelectedMovement;
        if (movement is null) return;
        if (movement.IsCancelled)
        {
            SetError("This entry is already cancelled.");
            return;
        }
        SelectedMovement = movement;
        CancelReason = string.Empty;
        SetDialogErrors(Array.Empty<string>());
        IsCancelMovementOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmCancelMovementAsync()
    {
        var movement = SelectedMovement;
        if (movement is null || IsLoading) return;
        IsLoading = true;
        try
        {
            await _service.CancelMovementAsync(movement.Id, CancelReason, _auth.CurrentUser?.Id);
            IsCancelMovementOpen = false;
            IsLoading = false;
            await RefreshAsync();
            SetStatus($"{movement.KindLabel} of {FinanceUi.Money(movement.AmountPaisa)} cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cancelling transfer {Id} failed", movement.Id);
            SetDialogErrors(FinanceUi.Messages(ex, "cancel the entry"));
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
        IsAccountEditorOpen = false;
        IsMovementOpen = false;
        IsCancelMovementOpen = false;
        SetDialogErrors(Array.Empty<string>());
    }

    private void SetDialogErrors(IEnumerable<string> messages)
    {
        DialogErrors.Clear();
        foreach (var message in messages) DialogErrors.Add(message);
        OnPropertyChanged(nameof(HasDialogErrors));
    }
}
