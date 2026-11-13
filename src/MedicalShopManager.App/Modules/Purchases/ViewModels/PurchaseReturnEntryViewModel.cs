using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>
/// Return Purchase (spec 04 §4, Ctrl+Alt+R): pick the distributor (and optionally one of its bills), enter the return
/// quantity (packs + loose) and rate for the batches going back, a reason, and any refund already received. Posting
/// issues a debit note number (DocumentSequences) and takes the stock out in one transaction; the debit note can be
/// printed straight away. <see cref="PurchaseExpiryReturnViewModel"/> reuses this screen for expired / near-expiry stock.
/// </summary>
public partial class PurchaseReturnEntryViewModel : BaseViewModel, INavigationAware
{
    private readonly IPurchaseReturnService _returns;
    private readonly IPurchaseEntryService _entry;
    private readonly IDistributorService _distributors;
    private readonly IAuthService _auth;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<PurchaseReturnDocument> _template;
    private readonly ILogger<PurchaseReturnEntryViewModel> _logger;
    private PurchaseReturnRequest? _pending;
    private PurchaseSettings _settings = PurchaseSettings.Default;
    private bool _loaded;
    private long? _pendingPurchaseId;

    public PurchaseReturnEntryViewModel(IPurchaseReturnService returns, IPurchaseEntryService entry, IDistributorService distributors,
        IAuthService auth, IPrintService print, IDocumentTemplate<PurchaseReturnDocument> template, ILogger<PurchaseReturnEntryViewModel> logger)
    {
        _returns = returns;
        _entry = entry;
        _distributors = distributors;
        _auth = auth;
        _print = print;
        _template = template;
        _logger = logger;
    }

    /// <summary>Regular purchase return, or expiry return (subclass).</summary>
    public virtual PurchaseReturnKind Kind => PurchaseReturnKind.Regular;
    public bool IsExpiryReturn => Kind == PurchaseReturnKind.Expiry;
    public string Title => IsExpiryReturn ? "Return Expired Products" : "Return Purchase";

    public ObservableCollection<PurchaseOption<long?>> SupplierOptions { get; } = new();
    public ObservableCollection<PurchaseOption<long?>> PurchaseOptions { get; } = new();
    public ObservableCollection<PurchaseReturnCandidateViewModel> Candidates { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();
    public IReadOnlyList<PurchaseOption<PurchaseReturnReason>> Reasons => PurchaseUi.ReturnReasons;

    public IReadOnlyList<PurchaseOption<int>> ExpiryWindows { get; } = new[]
    {
        new PurchaseOption<int>(0, "Expired only"), new PurchaseOption<int>(1, "Expired + < 1 month"),
        new PurchaseOption<int>(3, "Expired + < 3 months"), new PurchaseOption<int>(6, "Expired + < 6 months"),
        new PurchaseOption<int>(9, "Expired + < 9 months"), new PurchaseOption<int>(12, "Expired + < 12 months")
    };

    [ObservableProperty] private PurchaseOption<long?>? _selectedSupplier;
    [ObservableProperty] private PurchaseOption<long?>? _selectedPurchase;
    [ObservableProperty] private PurchaseOption<int>? _selectedExpiryWindow;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DateTime? _returnDate = DateTime.Today;
    [ObservableProperty] private PurchaseOption<PurchaseReturnReason>? _selectedReason;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private decimal _refundReceived;
    [ObservableProperty] private string _nextNumber = string.Empty;
    [ObservableProperty] private PurchaseReturnCandidateViewModel? _selectedCandidate;

    // Totals of the debit note being prepared
    [ObservableProperty] private long _taxablePaisa;
    [ObservableProperty] private long _taxPaisa;
    [ObservableProperty] private long _roundOffPaisa;
    [ObservableProperty] private long _netAmountPaisa;
    [ObservableProperty] private int _selectedCount;

    [ObservableProperty] private PurchaseReturnDocument? _lastPosted;

    public bool IsEmpty => !IsLoading && Candidates.Count == 0;
    public bool HasErrors => Errors.Count > 0;
    public bool HasLastPosted => LastPosted != null;
    public bool ShowPurchaseFilter => !IsExpiryReturn;

    partial void OnLastPostedChanged(PurchaseReturnDocument? value) => OnPropertyChanged(nameof(HasLastPosted));
    partial void OnSelectedSupplierChanged(PurchaseOption<long?>? value) => _ = SupplierChangedAsync();
    partial void OnSelectedPurchaseChanged(PurchaseOption<long?>? value) => _ = ReloadIfReadyAsync();
    partial void OnSelectedExpiryWindowChanged(PurchaseOption<int>? value) => _ = ReloadIfReadyAsync();
    partial void OnRefundReceivedChanged(decimal value) => QueueRecalc();
    partial void OnReturnDateChanged(DateTime? value) => _ = PeekNumberAsync();

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is PurchaseReturnRequest request) _pending = request;
    }

    public override async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _settings = await _entry.GetSettingsAsync();
            SelectedReason = Reasons.First(r => r.Value == (IsExpiryReturn ? PurchaseReturnReason.Expired : PurchaseReturnReason.Other));
            SelectedExpiryWindow = ExpiryWindows.FirstOrDefault(w => w.Value == _settings.NearExpiryMonths) ?? ExpiryWindows[2];

            var distributors = await _distributors.SearchAsync(null, includeInactive: true);
            SupplierOptions.Clear();
            if (IsExpiryReturn) SupplierOptions.Add(new PurchaseOption<long?>(null, "All distributors"));
            foreach (var d in distributors) SupplierOptions.Add(new PurchaseOption<long?>(d.Id, d.Name));

            _loaded = true;
            var pending = _pending;
            _pending = null;
            _pendingPurchaseId = pending?.PurchaseId;
            SelectedSupplier = SupplierOptions.FirstOrDefault(o => o.Value == pending?.SupplierId)
                               ?? (IsExpiryReturn ? SupplierOptions.FirstOrDefault() : null);
            await PeekNumberAsync();
            if (SelectedSupplier == null) OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading return screen failed");
            SetError(PurchaseUi.Message(ex, "open the return screen"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PeekNumberAsync()
    {
        try
        {
            NextNumber = await _returns.PeekNextNumberAsync(ReturnDate ?? DateTime.Today);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Peeking debit note number failed");
            NextNumber = "New";
        }
    }

    private async Task SupplierChangedAsync()
    {
        if (!_loaded) return;
        PurchaseOptions.Clear();
        PurchaseOptions.Add(new PurchaseOption<long?>(null, "Any bill (all stock from this distributor)"));
        if (!IsExpiryReturn && SelectedSupplier?.Value is long supplierId)
        {
            try
            {
                var bills = await _entry.ListAsync(new PurchaseListFilter(DateTime.Today.AddYears(-3), DateTime.Today, supplierId));
                foreach (var bill in bills.Where(b => b.Status == PurchaseStatus.Confirmed))
                    PurchaseOptions.Add(new PurchaseOption<long?>(bill.Id, $"{bill.PurchaseNumber} · bill {bill.SupplierInvoiceNumber} · {bill.InvoiceDate:dd-MM-yy}"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Loading distributor bills failed");
            }
        }
        var pendingPurchase = _pendingPurchaseId;
        _pendingPurchaseId = null;
        _selectedPurchaseSuppressed = true;
        SelectedPurchase = PurchaseOptions.FirstOrDefault(o => o.Value == pendingPurchase) ?? PurchaseOptions[0];
        _selectedPurchaseSuppressed = false;
        await ReloadAsync();
    }

    private bool _selectedPurchaseSuppressed;

    private Task ReloadIfReadyAsync() => _loaded && !_selectedPurchaseSuppressed ? ReloadAsync() : Task.CompletedTask;

    /// <summary>Loads the batches that can go back (stock on hand, capped by what each bill delivered minus earlier returns).</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        foreach (var c in Candidates) c.PropertyChanged -= OnCandidateChanged;
        Candidates.Clear();
        if (!IsExpiryReturn && SelectedSupplier?.Value is null)
        {
            Recalculate();
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }
        IsLoading = true;
        try
        {
            IReadOnlyList<ReturnableBatch> batches = IsExpiryReturn
                ? await _returns.GetExpiryCandidatesAsync(SelectedSupplier?.Value, SelectedExpiryWindow?.Value)
                : await _returns.GetReturnableBatchesAsync(SelectedSupplier!.Value!.Value, SelectedPurchase?.Value,
                    string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim());
            if (IsExpiryReturn && !string.IsNullOrWhiteSpace(SearchText))
                batches = batches.Where(b => b.MedicineName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)
                                             || b.BatchNumber.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var batch in batches)
            {
                var candidate = new PurchaseReturnCandidateViewModel(batch, _settings.NearExpiryMonths);
                candidate.PropertyChanged += OnCandidateChanged;
                Candidates.Add(candidate);
            }
            SelectedCandidate = Candidates.FirstOrDefault();
            ClearError();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading returnable batches failed");
            SetError(PurchaseUi.Message(ex, "load the batches"));
        }
        finally
        {
            IsLoading = false;
            Recalculate();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void OnCandidateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PurchaseReturnCandidateViewModel.ReturnUnits) or nameof(PurchaseReturnCandidateViewModel.Rate)) QueueRecalc();
    }

    private void QueueRecalc()
    {
        if (_loaded) Recalculate();
    }

    /// <summary>The debit note as entered: one line per batch with a quantity.</summary>
    private (PurchaseReturnDocument Doc, List<string> Errors) BuildDocument()
    {
        var errors = new List<string>();
        var selected = Candidates.Where(c => c.ReturnUnits != 0).ToList();
        var supplierIds = selected.Select(c => c.Batch.SupplierId ?? SelectedSupplier?.Value).Distinct().ToList();
        var supplierId = SelectedSupplier?.Value ?? (supplierIds.Count == 1 ? supplierIds[0] : null);
        if (IsExpiryReturn && SelectedSupplier?.Value is null && supplierIds.Count > 1)
            errors.Add("A debit note goes to one distributor. Choose the distributor first (the batches selected come from several).");
        if (IsExpiryReturn && supplierId is null && selected.Count > 0)
            errors.Add("These batches have no distributor on record. Choose the distributor to return them to.");

        var doc = new PurchaseReturnDocument
        {
            Kind = Kind,
            ReturnDate = ReturnDate?.Date ?? default,
            SupplierId = supplierId ?? 0,
            SupplierName = SupplierOptions.FirstOrDefault(o => o.Value == supplierId)?.Label ?? string.Empty,
            PurchaseId = IsExpiryReturn ? null : SelectedPurchase?.Value,
            PurchaseReference = IsExpiryReturn || SelectedPurchase?.Value is null ? null : SelectedPurchase.Label.Split(" · ")[0],
            Reason = SelectedReason?.Value ?? PurchaseReturnReason.Other,
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            RefundReceivedPaisa = PurchaseUi.Paisa(RefundReceived)
        };
        foreach (var c in selected)
        {
            if (c.ReturnUnits < 0)
            {
                errors.Add($"{c.MedicineName} (Batch {c.BatchNumber}): quantity cannot be negative.");
                continue;
            }
            if (c.ReturnUnits > c.Batch.MaxReturnableUnits)
                errors.Add($"{c.MedicineName} (Batch {c.BatchNumber}): only {c.MaxReturnableDisplay} can be returned.");
            if (c.Rate < 0) errors.Add($"{c.MedicineName} (Batch {c.BatchNumber}): rate cannot be negative.");
            var line = _returns.CreateLine(c.Batch, c.ReturnUnits);
            line.Quantity = c.ReturnUnits;
            line.RatePaisa = PurchaseUi.Paisa(Math.Max(0, c.Rate));
            doc.Lines.Add(line);
        }
        return (doc, errors);
    }

    private void Recalculate()
    {
        var (doc, _) = BuildDocument();
        try
        {
            PurchaseCalculator.RecalculateReturn(doc, _settings.RoundOffToRupee ? RoundOffMode.NearestRupee : RoundOffMode.None);
            TaxablePaisa = doc.TaxablePaisa;
            TaxPaisa = doc.TaxAmountPaisa;
            RoundOffPaisa = doc.RoundOffPaisa;
            NetAmountPaisa = doc.NetAmountPaisa;
        }
        catch (ArgumentException)
        {
            TaxablePaisa = TaxPaisa = RoundOffPaisa = NetAmountPaisa = 0;
        }
        SelectedCount = doc.Lines.Count;
    }

    [RelayCommand]
    private void ReturnAll(PurchaseReturnCandidateViewModel? candidate)
    {
        candidate ??= SelectedCandidate;
        candidate?.ReturnAll();
    }

    [RelayCommand]
    private void ReturnAllListed()
    {
        foreach (var c in Candidates) c.ReturnAll();
    }

    [RelayCommand]
    private void ClearQuantities()
    {
        foreach (var c in Candidates) c.Clear();
    }

    /// <summary>Save (Alt+S): validates, posts the debit note (number + stock out in one transaction), offers printing.</summary>
    [RelayCommand]
    private async Task PostAsync()
    {
        if (IsLoading) return;
        var (doc, errors) = BuildDocument();
        if (doc.Lines.Count == 0) errors.Insert(0, "Enter a return quantity for at least one batch.");
        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return;
        }
        IsLoading = true;
        try
        {
            var posted = await _returns.PostAsync(doc, _auth.CurrentUser?.Id);
            LastPosted = posted;
            ShowErrors(Array.Empty<string>());
            Notes = string.Empty;
            RefundReceived = 0;
            await ReloadAsync();
            await PeekNumberAsync();
            SetStatus($"Debit note {posted.ReturnNumber} saved: ₹{posted.NetAmount:N2} to {posted.SupplierName}, stock taken out. Ctrl+P prints it.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Posting debit note failed");
            ShowErrors(PurchaseUi.Messages(ex, "save the debit note"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PrintLastAsync()
    {
        if (LastPosted == null)
        {
            SetError("Save a debit note first; past ones are printed from Purchase Returns (Debit Notes).");
            return;
        }
        try
        {
            var doc = await _returns.GetAsync(LastPosted.Id) ?? LastPosted;
            await _print.PreviewAsync(_template, doc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing debit note failed");
            SetError(PurchaseUi.Message(ex, "print the debit note"));
        }
    }

    private void ShowErrors(IEnumerable<string> messages)
    {
        Errors.Clear();
        foreach (var message in messages) Errors.Add(message);
        OnPropertyChanged(nameof(HasErrors));
        if (Errors.Count > 0) SetError(Errors.Count == 1 ? Errors[0] : $"Please fix {Errors.Count} problems (listed below).");
        else ClearError();
    }
}

/// <summary>
/// Return Expired Products (spec 04 §5): the same screen listing expired and near-expiry batches in stock (window from
/// the near-expiry setting), optionally for one distributor; the debit note goes to the batch's distributor.
/// </summary>
public sealed class PurchaseExpiryReturnViewModel : PurchaseReturnEntryViewModel
{
    public PurchaseExpiryReturnViewModel(IPurchaseReturnService returns, IPurchaseEntryService entry, IDistributorService distributors,
        IAuthService auth, IPrintService print, IDocumentTemplate<PurchaseReturnDocument> template, ILogger<PurchaseReturnEntryViewModel> logger)
        : base(returns, entry, distributors, auth, print, template, logger)
    {
    }

    public override PurchaseReturnKind Kind => PurchaseReturnKind.Expiry;
}
