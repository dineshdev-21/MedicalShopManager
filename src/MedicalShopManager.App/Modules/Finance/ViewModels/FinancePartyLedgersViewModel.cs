using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>
/// Finance ▸ Party Ledgers: customer receivables or supplier payables as on a date with ageing (0-30 / 31-60 / 61-90 / 90+
/// days) on the left, and the selected party's ledger for a period (bills, payments, returns, cheques with debit / credit
/// and the running Dr / Cr balance) on the right. Alt+R receive from / pay the party, Ctrl+E export the ledger,
/// Ctrl+Shift+E export the outstanding list, Ctrl+P print, F2 preview, F5 refresh.
/// Opened on a party with <see cref="OpenPartyLedgerRequest"/>.
/// </summary>
public partial class FinancePartyLedgersViewModel : BaseViewModel, INavigationAware
{
    private readonly IPartyPaymentService _service;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<PartyLedger> _template;
    private readonly IFinanceFileDialog _files;
    private readonly INavigationService _navigation;
    private readonly ILogger<FinancePartyLedgersViewModel> _logger;
    private OpenPartyLedgerRequest? _request;
    private IReadOnlyList<PartyBalanceRow> _allRows = Array.Empty<PartyBalanceRow>();
    private PartyLedger? _ledger;
    private bool _loaded;
    private bool _suppressReload;

    public FinancePartyLedgersViewModel(IPartyPaymentService service, IPrintService print, IDocumentTemplate<PartyLedger> template,
        IFinanceFileDialog files, INavigationService navigation, ILogger<FinancePartyLedgersViewModel> logger)
    {
        _service = service;
        _print = print;
        _template = template;
        _files = files;
        _navigation = navigation;
        _logger = logger;
        _selectedPartyType = PartyTypes[0];
    }

    public IReadOnlyList<FinanceOption<PartyType>> PartyTypes => FinanceUi.LedgerPartyTypes;
    public ObservableCollection<PartyBalanceRow> Parties { get; } = new();
    public ObservableCollection<PartyLedgerLine> Lines { get; } = new();

    [ObservableProperty] private FinanceOption<PartyType> _selectedPartyType;
    [ObservableProperty] private DateTime? _asOfDate = DateTime.Today;
    [ObservableProperty] private bool _includeSettled;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private PartyBalanceRow? _selectedParty;
    [ObservableProperty] private DateTime? _fromDate = FinanceUi.FinancialYearStart(DateTime.Today);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;

    // ─── Outstanding totals (all parties listed) ───
    [ObservableProperty] private int _partyCount;
    [ObservableProperty] private long _days0To30Paisa;
    [ObservableProperty] private long _days31To60Paisa;
    [ObservableProperty] private long _days61To90Paisa;
    [ObservableProperty] private long _over90Paisa;
    [ObservableProperty] private long _creditsPaisa;
    [ObservableProperty] private long _netDuePaisa;

    // ─── Selected party's ledger ───
    [ObservableProperty] private string _openingLabel = string.Empty;
    [ObservableProperty] private long _totalDebitPaisa;
    [ObservableProperty] private long _totalCreditPaisa;
    [ObservableProperty] private string _closingLabel = string.Empty;

    public bool IsSupplier => SelectedPartyType.Value == PartyType.Supplier;
    public bool IsPartiesEmpty => !IsLoading && Parties.Count == 0;
    public bool HasParty => SelectedParty is not null;
    public bool IsLedgerEmpty => HasParty && Lines.Count == 0;
    public string NetDueTitle => IsSupplier ? "TOTAL PAYABLE" : "TOTAL RECEIVABLE";
    public string PayButtonText => IsSupplier ? "Pay supplier (Alt+R)" : "Receive payment (Alt+R)";
    public string LedgerHeading => SelectedParty is null ? "Select a party to see its ledger" : SelectedParty.Display;
    public string EmptyPartiesHint => IsSupplier
        ? "No supplier has an outstanding balance on this date. Tick \"Show settled\" to list every supplier."
        : "No customer has an outstanding balance on this date. Tick \"Show settled\" to list every customer.";

    partial void OnSelectedPartyTypeChanged(FinanceOption<PartyType> value)
    {
        OnPropertyChanged(nameof(IsSupplier));
        OnPropertyChanged(nameof(NetDueTitle));
        OnPropertyChanged(nameof(PayButtonText));
        OnPropertyChanged(nameof(EmptyPartiesHint));
        ReloadOnFilterChange();
    }

    partial void OnAsOfDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnIncludeSettledChanged(bool value) => ReloadOnFilterChange();
    partial void OnSearchTextChanged(string value) => ApplySearch(SelectedParty?.PartyId);

    partial void OnSelectedPartyChanged(PartyBalanceRow? value)
    {
        OnPropertyChanged(nameof(HasParty));
        OnPropertyChanged(nameof(LedgerHeading));
        if (_loaded && !_suppressReload) _ = LoadLedgerAsync();
    }

    partial void OnFromDateChanged(DateTime? value)
    {
        if (_loaded && !_suppressReload) _ = LoadLedgerAsync();
    }

    partial void OnToDateChanged(DateTime? value)
    {
        if (_loaded && !_suppressReload) _ = LoadLedgerAsync();
    }

    public void OnNavigatedTo(object? parameter) => _request = parameter as OpenPartyLedgerRequest;

    public override async Task LoadAsync()
    {
        if (_request is { } request)
        {
            _suppressReload = true;
            SelectedPartyType = PartyTypes.FirstOrDefault(t => t.Value == request.PartyType) ?? PartyTypes[0];
            // The requested party must be listed even when it owes nothing.
            IncludeSettled = true;
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
        if (AsOfDate is null)
        {
            SetError("Choose the \"as on\" date.");
            return;
        }
        IsLoading = true;
        try
        {
            var keepId = _request?.PartyId ?? SelectedParty?.PartyId;
            _request = null;
            _allRows = await _service.GetBalancesAsync(SelectedPartyType.Value, AsOfDate.Value.Date, IncludeSettled);
            ApplySearch(keepId);
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading party balances failed");
            SetError(FinanceUi.Message(ex, "load the outstanding list"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsPartiesEmpty));
        }
        await LoadLedgerAsync();
    }

    /// <summary>Filters the loaded parties by name / phone / GSTIN and totals what is listed.</summary>
    private void ApplySearch(long? keepPartyId)
    {
        var text = SearchText.Trim();
        var rows = text.Length == 0
            ? _allRows
            : _allRows.Where(r => Contains(r.Name, text) || Contains(r.Phone, text) || Contains(r.Gstin, text)).ToList();

        _suppressReload = true;
        Parties.Clear();
        foreach (var row in rows) Parties.Add(row);
        var previous = SelectedParty;
        SelectedParty = Parties.FirstOrDefault(r => r.PartyId == keepPartyId) ?? Parties.FirstOrDefault();
        _suppressReload = false;

        var totals = FinanceRules.Totals(rows.ToList());
        PartyCount = totals.PartyCount;
        Days0To30Paisa = totals.Ageing.Days0To30Paisa;
        Days31To60Paisa = totals.Ageing.Days31To60Paisa;
        Days61To90Paisa = totals.Ageing.Days61To90Paisa;
        Over90Paisa = totals.Ageing.Over90Paisa;
        CreditsPaisa = totals.CreditsPaisa;
        NetDuePaisa = totals.NetDuePaisa;
        OnPropertyChanged(nameof(IsPartiesEmpty));

        // Typing in the search box (outside a refresh) can change the selected party: reload its ledger.
        if (_loaded && !IsLoading && !ReferenceEquals(previous, SelectedParty)) _ = LoadLedgerAsync();
    }

    private static bool Contains(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private async Task LoadLedgerAsync()
    {
        var party = SelectedParty;
        if (party is null || FromDate is null || ToDate is null)
        {
            ShowLedger(null);
            if (party is not null) SetError("Choose both ledger dates.");
            return;
        }
        if (FromDate.Value.Date > ToDate.Value.Date)
        {
            ShowLedger(null);
            SetError("The ledger's From date is after its To date.");
            return;
        }
        try
        {
            var ledger = await _service.GetLedgerAsync(party.PartyType, party.PartyId, FromDate.Value.Date, ToDate.Value.Date);
            // A slower, older request must not overwrite the ledger of the party selected since.
            if (!ReferenceEquals(party, SelectedParty)) return;
            ShowLedger(ledger);
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the ledger of {PartyType} {PartyId} failed", party.PartyType, party.PartyId);
            ShowLedger(null);
            SetError(FinanceUi.Message(ex, "load the ledger"));
        }
    }

    private void ShowLedger(PartyLedger? ledger)
    {
        _ledger = ledger;
        Lines.Clear();
        if (ledger is not null)
            foreach (var line in ledger.Lines) Lines.Add(line);
        OpeningLabel = ledger is null ? string.Empty : FinanceRules.DrCr(ledger.OpeningBalancePaisa);
        TotalDebitPaisa = ledger?.TotalDebitPaisa ?? 0;
        TotalCreditPaisa = ledger?.TotalCreditPaisa ?? 0;
        ClosingLabel = ledger is null ? string.Empty : FinanceRules.DrCr(ledger.ClosingBalancePaisa);
        OnPropertyChanged(nameof(IsLedgerEmpty));
    }

    [RelayCommand]
    private void ThisMonth() => SetLedgerRange(FinanceUi.MonthStart(DateTime.Today), DateTime.Today);

    [RelayCommand]
    private void ThisYear() => SetLedgerRange(FinanceUi.FinancialYearStart(DateTime.Today), DateTime.Today);

    [RelayCommand]
    private void LastYear()
    {
        var start = FinanceUi.FinancialYearStart(DateTime.Today).AddYears(-1);
        SetLedgerRange(start, start.AddYears(1).AddDays(-1));
    }

    private void SetLedgerRange(DateTime from, DateTime to)
    {
        _suppressReload = true;
        FromDate = from;
        ToDate = to;
        _suppressReload = false;
        _ = LoadLedgerAsync();
    }

    /// <summary>Receive payment from the selected customer / pay the selected supplier.</summary>
    [RelayCommand]
    private void Settle(PartyBalanceRow? row)
    {
        row ??= SelectedParty;
        if (row is null)
        {
            SetError("Select a party first.");
            return;
        }
        var page = row.PartyType == PartyType.Supplier ? FinancePages.PaySupplier : FinancePages.ReceivePayment;
        _navigation.NavigateTo(page, new OpenPartyPaymentRequest(row.PartyType, row.PartyId));
    }

    [RelayCommand]
    private async Task ExportLedgerAsync()
    {
        if (_ledger is null)
        {
            SetError("Select a party first.");
            return;
        }
        try
        {
            var path = _files.AskCsvPath($"{_ledger.PartyName} ledger {_ledger.From:dd-MM-yyyy} to {_ledger.To:dd-MM-yyyy}");
            if (path is null) return;
            await FinanceFileDialog.WriteAsync(path, FinanceCsv.PartyLedger(_ledger));
            SetStatus($"Ledger exported to {path}.");
            _files.Open(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting the ledger failed");
            SetError(FinanceUi.Message(ex, "export the ledger"));
        }
    }

    [RelayCommand]
    private async Task ExportBalancesAsync()
    {
        if (Parties.Count == 0 || AsOfDate is null)
        {
            SetError("There is nothing to export.");
            return;
        }
        try
        {
            var what = IsSupplier ? "Supplier payables" : "Customer receivables";
            var path = _files.AskCsvPath($"{what} as on {AsOfDate.Value:dd-MM-yyyy}");
            if (path is null) return;
            await FinanceFileDialog.WriteAsync(path, FinanceCsv.PartyBalances(Parties.ToList(), AsOfDate.Value.Date));
            SetStatus($"{what} exported to {path}.");
            _files.Open(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting the outstanding list failed");
            SetError(FinanceUi.Message(ex, "export the outstanding list"));
        }
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (_ledger is null)
        {
            SetError("Select a party first.");
            return;
        }
        try
        {
            if (await _print.PrintAsync(_template, _ledger)) SetStatus($"Ledger of {_ledger.PartyName} sent to the printer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing the ledger failed");
            SetError(FinanceUi.Message(ex, "print the ledger"));
        }
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (_ledger is null)
        {
            SetError("Select a party first.");
            return;
        }
        try
        {
            await _print.PreviewAsync(_template, _ledger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Previewing the ledger failed");
            SetError(FinanceUi.Message(ex, "preview the ledger"));
        }
    }
}
