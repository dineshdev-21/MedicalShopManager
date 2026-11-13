using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>
/// Purchase Returns register (spec 04 §7 "Transactions › Purchase Returns"): debit notes by date and distributor, with
/// their lines, print and cancel (stock put back).
/// </summary>
public sealed partial class PurchaseDebitNotesViewModel : BaseViewModel
{
    private readonly IPurchaseReturnService _returns;
    private readonly IDistributorService _distributors;
    private readonly INavigationService _navigation;
    private readonly IAuthService _auth;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<PurchaseReturnDocument> _template;
    private readonly ILogger<PurchaseDebitNotesViewModel> _logger;
    private bool _loaded;

    public PurchaseDebitNotesViewModel(IPurchaseReturnService returns, IDistributorService distributors, INavigationService navigation,
        IAuthService auth, IPrintService print, IDocumentTemplate<PurchaseReturnDocument> template, ILogger<PurchaseDebitNotesViewModel> logger)
    {
        _returns = returns;
        _distributors = distributors;
        _navigation = navigation;
        _auth = auth;
        _print = print;
        _template = template;
        _logger = logger;
    }

    public ObservableCollection<PurchaseReturnListRow> Notes { get; } = new();
    public ObservableCollection<PurchaseReturnLine> SelectedLines { get; } = new();
    public ObservableCollection<PurchaseOption<long?>> SupplierOptions { get; } = new();

    [ObservableProperty] private DateTime? _fromDate = DateTime.Today.AddMonths(-3);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;
    [ObservableProperty] private PurchaseOption<long?>? _selectedSupplier;
    [ObservableProperty] private PurchaseReturnListRow? _selectedNote;
    [ObservableProperty] private long _totalPaisa;
    [ObservableProperty] private bool _isCancelDialogOpen;

    public bool IsEmpty => !IsLoading && Notes.Count == 0;

    partial void OnSelectedSupplierChanged(PurchaseOption<long?>? value) => _ = RefreshIfLoadedAsync();
    partial void OnSelectedNoteChanged(PurchaseReturnListRow? value) => _ = LoadLinesAsync(value);

    private Task RefreshIfLoadedAsync() => _loaded ? RefreshAsync() : Task.CompletedTask;

    public override async Task LoadAsync()
    {
        try
        {
            var distributors = await _distributors.SearchAsync(null, includeInactive: true);
            SupplierOptions.Clear();
            foreach (var option in PurchaseUi.SupplierFilter(distributors)) SupplierOptions.Add(option);
            SelectedSupplier = SupplierOptions[0];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading distributors failed");
            SetError(PurchaseUi.Message(ex, "load distributors"));
        }
        _loaded = true;
        await RefreshAsync();
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
            var rows = await _returns.ListAsync(FromDate.Value, ToDate.Value, SelectedSupplier?.Value);
            Notes.Clear();
            foreach (var row in rows) Notes.Add(row);
            TotalPaisa = rows.Where(r => !r.IsCancelled).Sum(r => r.NetAmountPaisa);
            SelectedNote = Notes.FirstOrDefault();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading debit notes failed");
            SetError(PurchaseUi.Message(ex, "load debit notes"));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private async Task LoadLinesAsync(PurchaseReturnListRow? row)
    {
        SelectedLines.Clear();
        if (row == null) return;
        try
        {
            var doc = await _returns.GetAsync(row.Id);
            if (doc == null || SelectedNote?.Id != row.Id) return;
            foreach (var line in doc.Lines) SelectedLines.Add(line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading debit note lines failed");
        }
    }

    [RelayCommand]
    private void New() => _navigation.NavigateTo(PurchasePages.Return);

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (SelectedNote == null) return;
        try
        {
            var doc = await _returns.GetAsync(SelectedNote.Id) ?? throw new PurchaseRuleException("This debit note no longer exists.");
            await _print.PreviewAsync(_template, doc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing debit note failed");
            SetError(PurchaseUi.Message(ex, "print the debit note"));
        }
    }

    [RelayCommand]
    private void AskCancel()
    {
        if (SelectedNote is null or { IsCancelled: true }) return;
        IsCancelDialogOpen = true;
    }

    [RelayCommand]
    private void CloseCancelDialog() => IsCancelDialogOpen = false;

    /// <summary>Cancels the debit note and puts its stock back.</summary>
    [RelayCommand]
    private async Task CancelNoteAsync()
    {
        if (SelectedNote == null) return;
        IsCancelDialogOpen = false;
        try
        {
            var number = SelectedNote.ReturnNumber;
            await _returns.CancelAsync(SelectedNote.Id, _auth.CurrentUser?.Id);
            SetStatus($"Debit note {number} cancelled; its stock was put back.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cancelling debit note failed");
            SetError(PurchaseUi.Message(ex, "cancel the debit note"));
        }
    }
}
