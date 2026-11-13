using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>
/// Finance ▸ Expenses (Localwell /expenses/view-expenses + add-expense): expenses in a period with category, payee,
/// GST input (CGST / SGST or IGST), the account they were paid from and totals by category. Add (Alt+N), edit (Enter),
/// cancel (Del, with a reason; the number is kept), export (Ctrl+E) and manage categories (Ctrl+K). The GST split shown
/// in the form is the same calculation the save uses (PricingCalculator through the service).
/// </summary>
public partial class ExpensesViewModel : BaseViewModel
{
    private readonly IExpenseService _service;
    private readonly ICashBankService _cashBank;
    private readonly IAuthService _auth;
    private readonly IFinanceFileDialog _files;
    private readonly ILogger<ExpensesViewModel> _logger;
    private bool _loaded;
    private bool _suppressReload;
    private int _previewVersion;

    public ExpensesViewModel(IExpenseService service, ICashBankService cashBank, IAuthService auth, IFinanceFileDialog files,
        ILogger<ExpensesViewModel> logger)
    {
        _service = service;
        _cashBank = cashBank;
        _auth = auth;
        _files = files;
        _logger = logger;
        _selectedGstFilter = GstFilters[0];
        _editGstRate = GstRates[0];
        _editMode = ExpenseModes[0];
    }

    public IReadOnlyList<FinanceOption<bool?>> GstFilters => FinanceUi.GstFilters;
    public IReadOnlyList<FinanceOption<decimal>> GstRates => FinanceUi.GstRates;
    public IReadOnlyList<FinanceOption<FinancePaymentMode>> ExpenseModes => FinanceUi.ExpenseModes;

    public ObservableCollection<FinanceExpense> Rows { get; } = new();
    public ObservableCollection<ExpenseCategoryTotal> CategoryTotals { get; } = new();
    public ObservableCollection<FinanceOption<long?>> CategoryFilters { get; } = new();
    public ObservableCollection<FinanceOption<long?>> AccountFilters { get; } = new();
    public ObservableCollection<FinanceExpenseCategory> Categories { get; } = new();
    public ObservableCollection<FinanceExpenseCategory> AllCategories { get; } = new();
    public ObservableCollection<FinanceAccount> Accounts { get; } = new();
    public ObservableCollection<string> DialogErrors { get; } = new();

    [ObservableProperty] private DateTime? _fromDate = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;
    [ObservableProperty] private FinanceOption<long?>? _selectedCategoryFilter;
    [ObservableProperty] private FinanceOption<long?>? _selectedAccountFilter;
    [ObservableProperty] private FinanceOption<bool?> _selectedGstFilter;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _includeCancelled;
    [ObservableProperty] private FinanceExpense? _selectedRow;
    [ObservableProperty] private ExpenseTotals _totals = new(0, 0, 0, 0);

    // ─── Add / edit dialog ───
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private long _editingId;
    [ObservableProperty] private string _editorNumber = string.Empty;
    [ObservableProperty] private DateTime? _editDate = DateTime.Today;
    [ObservableProperty] private FinanceExpenseCategory? _editCategory;
    [ObservableProperty] private string _editPayee = string.Empty;
    [ObservableProperty] private string _editGstin = string.Empty;
    [ObservableProperty] private string _editInvoiceNumber = string.Empty;
    [ObservableProperty] private decimal _editAmount;
    [ObservableProperty] private bool _editIncludesGst = true;
    [ObservableProperty] private FinanceOption<decimal> _editGstRate;
    [ObservableProperty] private FinanceAccount? _editAccount;
    [ObservableProperty] private FinanceOption<FinancePaymentMode> _editMode;
    [ObservableProperty] private string _editReference = string.Empty;
    [ObservableProperty] private string _editDescription = string.Empty;
    [ObservableProperty] private string _editNotes = string.Empty;
    [ObservableProperty] private ExpenseAmounts _preview = new(0, 0, 0, 0, 0);

    // ─── Cancel dialog ───
    [ObservableProperty] private bool _isCancelDialogOpen;
    [ObservableProperty] private string _cancelReason = string.Empty;

    // ─── Categories dialog ───
    [ObservableProperty] private bool _isCategoriesOpen;
    [ObservableProperty] private string _newCategoryName = string.Empty;
    [ObservableProperty] private FinanceExpenseCategory? _selectedManageCategory;
    [ObservableProperty] private string _renameCategoryText = string.Empty;

    public bool IsEmpty => !IsLoading && Rows.Count == 0;
    public bool HasDialogErrors => DialogErrors.Count > 0;
    public bool HasGst => EditGstRate.Value > 0;
    public string EditorTitle => EditingId > 0 ? $"Edit expense {EditorNumber}" : $"New expense  ·  {EditorNumber}";
    public string AmountLabel => !HasGst ? "AMOUNT ₹ *" : EditIncludesGst ? "AMOUNT INCL. GST ₹ *" : "TAXABLE AMOUNT ₹ *";
    public string PreviewText => !HasGst
        ? $"Total paid {FinanceUi.Money(Preview.TotalPaisa)}"
        : Preview.IgstPaisa > 0
            ? $"Taxable {FinanceUi.Money(Preview.TaxablePaisa)}  +  IGST {FinanceUi.Money(Preview.IgstPaisa)}  =  {FinanceUi.Money(Preview.TotalPaisa)}"
            : $"Taxable {FinanceUi.Money(Preview.TaxablePaisa)}  +  CGST {FinanceUi.Money(Preview.CgstPaisa)}  +  SGST {FinanceUi.Money(Preview.SgstPaisa)}  =  {FinanceUi.Money(Preview.TotalPaisa)}";
    public string CancelTarget => SelectedRow is null ? string.Empty
        : $"{SelectedRow.NumberDisplay}  ·  {SelectedRow.CategoryName}  ·  {FinanceUi.Money(SelectedRow.AmountPaisa)}  ·  {SelectedRow.ExpenseDate:dd-MM-yyyy}";

    partial void OnFromDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnToDateChanged(DateTime? value) => ReloadOnFilterChange();
    partial void OnSelectedCategoryFilterChanged(FinanceOption<long?>? value) => ReloadOnFilterChange();
    partial void OnSelectedAccountFilterChanged(FinanceOption<long?>? value) => ReloadOnFilterChange();
    partial void OnSelectedGstFilterChanged(FinanceOption<bool?> value) => ReloadOnFilterChange();
    partial void OnIncludeCancelledChanged(bool value) => ReloadOnFilterChange();
    partial void OnSelectedRowChanged(FinanceExpense? value) => OnPropertyChanged(nameof(CancelTarget));
    partial void OnEditingIdChanged(long value) => OnPropertyChanged(nameof(EditorTitle));
    partial void OnEditorNumberChanged(string value) => OnPropertyChanged(nameof(EditorTitle));
    partial void OnEditAmountChanged(decimal value) => _ = RefreshPreviewAsync();
    partial void OnEditGstinChanged(string value) => _ = RefreshPreviewAsync();
    partial void OnPreviewChanged(ExpenseAmounts value) => OnPropertyChanged(nameof(PreviewText));
    partial void OnSelectedManageCategoryChanged(FinanceExpenseCategory? value) => RenameCategoryText = value?.Name ?? string.Empty;

    partial void OnEditIncludesGstChanged(bool value)
    {
        OnPropertyChanged(nameof(AmountLabel));
        _ = RefreshPreviewAsync();
    }

    partial void OnEditGstRateChanged(FinanceOption<decimal> value)
    {
        OnPropertyChanged(nameof(HasGst));
        OnPropertyChanged(nameof(AmountLabel));
        _ = RefreshPreviewAsync();
    }

    /// <summary>Choosing an account suggests its natural mode (cash in hand → cash, UPI wallet → UPI, bank → transfer).</summary>
    partial void OnEditAccountChanged(FinanceAccount? value)
    {
        if (value is null) return;
        var mode = ExpenseModes.FirstOrDefault(m => m.Value == value.AccountType.DefaultMode());
        if (mode is not null) EditMode = mode;
    }

    public override async Task LoadAsync()
    {
        try
        {
            await LoadMastersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading expense categories / accounts failed");
            SetError(FinanceUi.Message(ex, "load the categories and accounts"));
        }
        _loaded = true;
        await RefreshAsync();
    }

    private async Task LoadMastersAsync()
    {
        var categories = await _service.GetCategoriesAsync(includeInactive: true);
        var accounts = await _cashBank.GetAccountsAsync();
        _suppressReload = true;
        try
        {
            var categoryFilter = SelectedCategoryFilter?.Value;
            var accountFilter = SelectedAccountFilter?.Value;
            AllCategories.Clear();
            Categories.Clear();
            CategoryFilters.Clear();
            CategoryFilters.Add(new FinanceOption<long?>(null, "All categories"));
            foreach (var c in categories)
            {
                AllCategories.Add(c);
                if (!c.IsActive) continue;
                Categories.Add(c);
                CategoryFilters.Add(new FinanceOption<long?>(c.Id, c.Name));
            }
            Accounts.Clear();
            AccountFilters.Clear();
            AccountFilters.Add(new FinanceOption<long?>(null, "All accounts"));
            foreach (var a in accounts)
            {
                Accounts.Add(a);
                AccountFilters.Add(new FinanceOption<long?>(a.Id, a.DisplayName));
            }
            SelectedCategoryFilter = CategoryFilters.FirstOrDefault(o => o.Value == categoryFilter) ?? CategoryFilters[0];
            SelectedAccountFilter = AccountFilters.FirstOrDefault(o => o.Value == accountFilter) ?? AccountFilters[0];
        }
        finally
        {
            _suppressReload = false;
        }
    }

    private void ReloadOnFilterChange()
    {
        if (_loaded && !_suppressReload) _ = RefreshAsync();
    }

    private ExpenseFilter Filter() => new()
    {
        From = (FromDate ?? DateTime.Today).Date,
        To = (ToDate ?? DateTime.Today).Date,
        CategoryId = SelectedCategoryFilter?.Value,
        AccountId = SelectedAccountFilter?.Value,
        WithGst = SelectedGstFilter.Value,
        SearchText = FinanceUi.Blank(SearchText),
        IncludeCancelled = IncludeCancelled
    };

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
            var filter = Filter();
            var (rows, totals) = await _service.GetExpensesAsync(filter);
            var byCategory = await _service.GetCategoryTotalsAsync(filter.From, filter.To);
            var selectedId = SelectedRow?.Id;
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            CategoryTotals.Clear();
            foreach (var total in byCategory) CategoryTotals.Add(total);
            Totals = totals;
            SelectedRow = Rows.FirstOrDefault(r => r.Id == selectedId) ?? Rows.FirstOrDefault();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading expenses failed");
            SetError(FinanceUi.Message(ex, "load the expenses"));
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

    // ───────────────────────── Add / edit ─────────────────────────

    [RelayCommand]
    private async Task NewExpenseAsync()
    {
        EditingId = 0;
        EditDate = DateTime.Today;
        EditCategory = Categories.FirstOrDefault();
        EditPayee = EditGstin = EditInvoiceNumber = EditReference = EditDescription = EditNotes = string.Empty;
        EditAmount = 0;
        EditIncludesGst = true;
        EditGstRate = GstRates[0];
        EditAccount = Accounts.FirstOrDefault(a => a.AccountType == FinanceAccountType.Cash) ?? Accounts.FirstOrDefault();
        await OpenEditorAsync();
    }

    [RelayCommand]
    private async Task EditExpenseAsync(FinanceExpense? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        if (row.IsCancelled)
        {
            SetError($"Expense {row.NumberDisplay} is cancelled and cannot be edited.");
            return;
        }
        SelectedRow = row;
        EditingId = row.Id;
        EditDate = row.ExpenseDate;
        EditCategory = Categories.FirstOrDefault(c => c.Id == row.CategoryId) ?? AllCategories.FirstOrDefault(c => c.Id == row.CategoryId);
        EditPayee = row.PayeeName ?? string.Empty;
        EditGstin = row.PayeeGstin ?? string.Empty;
        EditInvoiceNumber = row.InvoiceNumber ?? string.Empty;
        EditGstRate = GstRates.FirstOrDefault(r => r.Value == row.GstRatePercent) ?? GstRates[0];
        EditIncludesGst = true;
        EditAmount = FinanceUi.Rupees(row.AmountPaisa);
        EditAccount = Accounts.FirstOrDefault(a => a.Id == row.AccountId);
        EditMode = ExpenseModes.FirstOrDefault(m => m.Value == row.Mode) ?? ExpenseModes[0];
        EditReference = row.Reference ?? string.Empty;
        EditDescription = row.Description ?? string.Empty;
        EditNotes = row.Notes ?? string.Empty;
        await OpenEditorAsync();
    }

    private async Task OpenEditorAsync()
    {
        SetDialogErrors(Array.Empty<string>());
        try
        {
            EditorNumber = EditingId > 0 ? SelectedRow?.NumberDisplay ?? string.Empty : await _service.PeekNumberAsync(EditDate ?? DateTime.Today);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Peeking the expense number failed");
            EditorNumber = string.Empty;
        }
        await RefreshPreviewAsync();
        IsEditorOpen = true;
    }

    private async Task RefreshPreviewAsync()
    {
        var version = ++_previewVersion;
        try
        {
            var preview = await _service.PreviewAmountsAsync(FinanceUi.Paisa(EditAmount), EditGstRate.Value, EditIncludesGst, FinanceUi.Blank(EditGstin));
            if (version == _previewVersion) Preview = preview;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Expense GST preview failed");
        }
    }

    [RelayCommand]
    private async Task SaveExpenseAsync()
    {
        if (!IsEditorOpen || IsLoading) return;
        IsLoading = true;
        try
        {
            var saved = await _service.SaveAsync(new ExpenseRequest
            {
                Id = EditingId,
                ExpenseDate = (EditDate ?? DateTime.Today).Date,
                CategoryId = EditCategory?.Id ?? 0,
                Description = FinanceUi.Blank(EditDescription),
                PayeeName = FinanceUi.Blank(EditPayee),
                PayeeGstin = FinanceUi.Blank(EditGstin),
                InvoiceNumber = FinanceUi.Blank(EditInvoiceNumber),
                AmountPaisa = FinanceUi.Paisa(EditAmount),
                GstRatePercent = EditGstRate.Value,
                AmountIncludesGst = EditIncludesGst,
                AccountId = EditAccount?.Id ?? 0,
                Mode = EditMode.Value,
                Reference = FinanceUi.Blank(EditReference),
                Notes = FinanceUi.Blank(EditNotes),
                UserId = _auth.CurrentUser?.Id
            });
            IsEditorOpen = false;
            IsLoading = false;
            await RefreshAsync();
            SelectedRow = Rows.FirstOrDefault(r => r.Id == saved.Id) ?? SelectedRow;
            SetStatus($"Expense {saved.NumberDisplay} saved: {saved.CategoryName} {FinanceUi.Money(saved.AmountPaisa)} from {saved.AccountName ?? "the account"}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving expense failed");
            SetDialogErrors(FinanceUi.Messages(ex, "save the expense"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ───────────────────────── Cancel ─────────────────────────

    [RelayCommand]
    private void BeginCancel(FinanceExpense? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        if (row.IsCancelled)
        {
            SetError($"Expense {row.NumberDisplay} is already cancelled.");
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
            IsLoading = false;
            await RefreshAsync();
            SetStatus($"Expense {row.NumberDisplay} cancelled. {FinanceUi.Money(row.AmountPaisa)} is back in {row.AccountName ?? "the account"}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cancelling expense {Id} failed", row.Id);
            SetDialogErrors(FinanceUi.Messages(ex, "cancel the expense"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ───────────────────────── Categories ─────────────────────────

    [RelayCommand]
    private void ManageCategories()
    {
        NewCategoryName = string.Empty;
        SelectedManageCategory = AllCategories.FirstOrDefault();
        SetDialogErrors(Array.Empty<string>());
        IsCategoriesOpen = true;
    }

    [RelayCommand]
    private Task AddCategoryAsync() => CategoryActionAsync(async userId =>
    {
        var added = await _service.AddCategoryAsync(NewCategoryName, userId);
        NewCategoryName = string.Empty;
        return $"Category \"{added.Name}\" added.";
    }, "add the category");

    [RelayCommand]
    private Task RenameCategoryAsync() => CategoryActionAsync(async userId =>
    {
        var category = SelectedManageCategory ?? throw new FinanceValidationException("Choose the category to rename.");
        await _service.RenameCategoryAsync(category.Id, RenameCategoryText, userId);
        return $"Category renamed to \"{RenameCategoryText.Trim()}\".";
    }, "rename the category");

    [RelayCommand]
    private Task ToggleCategoryAsync() => CategoryActionAsync(async userId =>
    {
        var category = SelectedManageCategory ?? throw new FinanceValidationException("Choose a category.");
        await _service.SetCategoryActiveAsync(category.Id, !category.IsActive, userId);
        return category.IsActive ? $"\"{category.Name}\" hidden from new expenses." : $"\"{category.Name}\" is available again.";
    }, "change the category");

    private async Task CategoryActionAsync(Func<long?, Task<string>> action, string what)
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var selectedId = SelectedManageCategory?.Id;
            var message = await action(_auth.CurrentUser?.Id);
            await LoadMastersAsync();
            SelectedManageCategory = AllCategories.FirstOrDefault(c => c.Id == selectedId) ?? AllCategories.FirstOrDefault();
            SetDialogErrors(Array.Empty<string>());
            SetStatus(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Expense category action failed");
            SetDialogErrors(FinanceUi.Messages(ex, what));
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ───────────────────────── Export / dialogs ─────────────────────────

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Rows.Count == 0)
        {
            SetError("There are no expenses to export in this period.");
            return;
        }
        try
        {
            var path = _files.AskCsvPath($"Expenses {FromDate:dd-MM-yyyy} to {ToDate:dd-MM-yyyy}");
            if (path is null) return;
            await FinanceFileDialog.WriteAsync(path, FinanceCsv.Expenses(Rows.ToList()));
            SetStatus($"Exported {Rows.Count} expenses to {path}.");
            _files.Open(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting expenses failed");
            SetError(FinanceUi.Message(ex, "export the expenses"));
        }
    }

    /// <summary>Esc: closes an open dialog.</summary>
    [RelayCommand]
    private void CloseDialogs()
    {
        IsEditorOpen = false;
        IsCancelDialogOpen = false;
        IsCategoriesOpen = false;
        SetDialogErrors(Array.Empty<string>());
    }

    private void SetDialogErrors(IEnumerable<string> messages)
    {
        DialogErrors.Clear();
        foreach (var message in messages) DialogErrors.Add(message);
        OnPropertyChanged(nameof(HasDialogErrors));
    }
}
