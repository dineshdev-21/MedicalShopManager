using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>
/// Distributor statement: opening balance at the start date, every bill, payment, debit note and refund in the
/// period with a running balance, and the closing balance (all derived from the documents). Printable.
/// </summary>
public sealed partial class PurchaseSupplierStatementViewModel : BaseViewModel, INavigationAware
{
    private readonly IDistributorService _distributors;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<SupplierStatement> _template;
    private readonly ILogger<PurchaseSupplierStatementViewModel> _logger;
    private long? _pendingSupplierId;
    private bool _loaded;

    public PurchaseSupplierStatementViewModel(IDistributorService distributors, IPrintService print, IDocumentTemplate<SupplierStatement> template,
        ILogger<PurchaseSupplierStatementViewModel> logger)
    {
        _distributors = distributors;
        _print = print;
        _template = template;
        _logger = logger;
    }

    public ObservableCollection<Distributor> Suppliers { get; } = new();
    public ObservableCollection<SupplierStatementLine> Lines { get; } = new();

    [ObservableProperty] private Distributor? _selectedSupplier;
    [ObservableProperty] private DateTime? _fromDate = FinancialYearStart(DateTime.Today);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;
    [ObservableProperty] private SupplierStatement? _statement;

    public bool IsEmpty => !IsLoading && Lines.Count <= 1;
    public bool HasStatement => Statement != null;

    /// <summary>Indian financial year starts on 1 April.</summary>
    private static DateTime FinancialYearStart(DateTime date) => new(date.Month >= 4 ? date.Year : date.Year - 1, 4, 1);

    partial void OnStatementChanged(SupplierStatement? value) => OnPropertyChanged(nameof(HasStatement));
    partial void OnSelectedSupplierChanged(Distributor? value) => _ = LoadStatementIfReadyAsync();

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is SupplierStatementRequest request) _pendingSupplierId = request.SupplierId;
    }

    public override async Task LoadAsync()
    {
        try
        {
            var list = await _distributors.SearchAsync(null, includeInactive: true);
            Suppliers.Clear();
            foreach (var d in list) Suppliers.Add(d);
            _loaded = true;
            SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == _pendingSupplierId) ?? SelectedSupplier;
            _pendingSupplierId = null;
            if (SelectedSupplier == null) OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading distributors failed");
            SetError(PurchaseUi.Message(ex, "load distributors"));
        }
    }

    private Task LoadStatementIfReadyAsync() => _loaded ? LoadStatementAsync() : Task.CompletedTask;

    [RelayCommand]
    private async Task LoadStatementAsync()
    {
        if (SelectedSupplier == null)
        {
            SetError("Choose a distributor.");
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
            Statement = await _distributors.GetStatementAsync(SelectedSupplier.Id, FromDate.Value, ToDate.Value);
            Lines.Clear();
            foreach (var line in Statement.Lines) Lines.Add(line);
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading statement failed");
            SetError(PurchaseUi.Message(ex, "load the statement"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (Statement == null) await LoadStatementAsync();
        if (Statement == null) return;
        try
        {
            await _print.PreviewAsync(_template, Statement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing statement failed");
            SetError(PurchaseUi.Message(ex, "print the statement"));
        }
    }
}
