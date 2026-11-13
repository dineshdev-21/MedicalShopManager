using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>
/// Finance ▸ Account Statement (Localwell /cash-and-bank/account-statement): every movement of one account in a period
/// (sales tenders, receipts, supplier payments, refunds, expenses, cheques cleared, transfers) with money in / out, the
/// running balance, opening and closing balance. Search filters the lines on screen; export (Ctrl+E) and print (Ctrl+P).
/// Opened from Cash &amp; Bank with <see cref="OpenAccountStatementRequest"/>.
/// </summary>
public partial class FinanceAccountStatementViewModel : BaseViewModel, INavigationAware
{
    private readonly ICashBankService _service;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<AccountStatement> _template;
    private readonly IFinanceFileDialog _files;
    private readonly ILogger<FinanceAccountStatementViewModel> _logger;
    private OpenAccountStatementRequest? _request;
    private bool _loaded;
    private bool _suppressReload;
    private AccountStatement? _statement;

    public FinanceAccountStatementViewModel(ICashBankService service, IPrintService print, IDocumentTemplate<AccountStatement> template,
        IFinanceFileDialog files, ILogger<FinanceAccountStatementViewModel> logger)
    {
        _service = service;
        _print = print;
        _template = template;
        _files = files;
        _logger = logger;
    }

    public ObservableCollection<FinanceAccount> Accounts { get; } = new();
    public ObservableCollection<AccountStatementLine> Lines { get; } = new();

    [ObservableProperty] private FinanceAccount? _selectedAccount;
    [ObservableProperty] private DateTime? _fromDate = FinanceUi.MonthStart(DateTime.Today);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private long _openingPaisa;
    [ObservableProperty] private long _totalInPaisa;
    [ObservableProperty] private long _totalOutPaisa;
    [ObservableProperty] private long _closingPaisa;

    public bool IsEmpty => !IsLoading && Lines.Count == 0;
    public string Heading => SelectedAccount is null ? "Account Statement" : $"Account Statement  ·  {SelectedAccount.DisplayName}";

    partial void OnSelectedAccountChanged(FinanceAccount? value)
    {
        OnPropertyChanged(nameof(Heading));
        ReloadOnFilterChange();
    }

    partial void OnFromDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnToDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnSearchTextChanged(string value) => ApplySearch();

    public void OnNavigatedTo(object? parameter) => _request = parameter as OpenAccountStatementRequest;

    public override async Task LoadAsync()
    {
        try
        {
            var accounts = await _service.GetAccountsAsync(includeInactive: true);
            _suppressReload = true;
            Accounts.Clear();
            foreach (var a in accounts) Accounts.Add(a);
            if (_request is { } request)
            {
                if (request.From is { } from) FromDate = from;
                if (request.To is { } to) ToDate = to;
            }
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == _request?.AccountId) ?? Accounts.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading accounts failed");
            SetError(FinanceUi.Message(ex, "load the accounts"));
        }
        finally
        {
            _suppressReload = false;
        }
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
        if (SelectedAccount is null)
        {
            SetError("Add an account in Cash & Bank first.");
            return;
        }
        if (FromDate is null || ToDate is null)
        {
            SetError("Choose both dates.");
            return;
        }
        IsLoading = true;
        try
        {
            _statement = await _service.GetStatementAsync(SelectedAccount.Id, FromDate.Value.Date, ToDate.Value.Date);
            OpeningPaisa = _statement.OpeningBalancePaisa;
            TotalInPaisa = _statement.TotalInPaisa;
            TotalOutPaisa = _statement.TotalOutPaisa;
            ClosingPaisa = _statement.ClosingBalancePaisa;
            ApplySearch();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the statement failed");
            SetError(FinanceUi.Message(ex, "load the statement"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>Filters the loaded lines by reference / party / details / type (running balances stay those of the full statement).</summary>
    private void ApplySearch()
    {
        Lines.Clear();
        if (_statement is null) return;
        var text = SearchText.Trim();
        foreach (var line in _statement.Lines)
        {
            if (text.Length > 0 && !Contains(line.Reference, text) && !Contains(line.PartyName, text) && !Contains(line.Description, text)
                && !Contains(line.SourceLabel, text))
                continue;
            Lines.Add(line);
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static bool Contains(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    [RelayCommand]
    private Task ThisMonthAsync() => SetRangeAsync(FinanceUi.MonthStart(DateTime.Today), DateTime.Today);

    [RelayCommand]
    private Task LastMonthAsync()
    {
        var start = FinanceUi.MonthStart(DateTime.Today).AddMonths(-1);
        return SetRangeAsync(start, start.AddMonths(1).AddDays(-1));
    }

    [RelayCommand]
    private Task ThisYearAsync() => SetRangeAsync(FinanceUi.FinancialYearStart(DateTime.Today), DateTime.Today);

    private Task SetRangeAsync(DateTime from, DateTime to)
    {
        _suppressReload = true;
        FromDate = from;
        ToDate = to;
        _suppressReload = false;
        return RefreshAsync();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_statement is null) return;
        try
        {
            var path = _files.AskCsvPath($"{_statement.Account.Name} statement {_statement.From:dd-MM-yyyy} to {_statement.To:dd-MM-yyyy}");
            if (path is null) return;
            await FinanceFileDialog.WriteAsync(path, FinanceCsv.AccountStatement(_statement));
            SetStatus($"Statement exported to {path}.");
            _files.Open(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting the statement failed");
            SetError(FinanceUi.Message(ex, "export the statement"));
        }
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (_statement is null) return;
        try
        {
            if (await _print.PrintAsync(_template, _statement)) SetStatus("Statement sent to the printer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing the statement failed");
            SetError(FinanceUi.Message(ex, "print the statement"));
        }
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (_statement is null) return;
        try
        {
            await _print.PreviewAsync(_template, _statement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Previewing the statement failed");
            SetError(FinanceUi.Message(ex, "preview the statement"));
        }
    }
}
