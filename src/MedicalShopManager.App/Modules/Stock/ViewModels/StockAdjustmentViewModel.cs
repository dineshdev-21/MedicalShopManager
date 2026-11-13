using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Stock;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Stock.ViewModels;

/// <summary>One line of an adjustment being entered (not yet posted).</summary>
public sealed record StockAdjustmentDraftLine(long MedicineId, string MedicineName, long BatchId, string BatchNumber, string ExpiryLabel,
    int UnitsPerPack, long BalanceUnits, long QuantityChange, long ValuePaisa)
{
    public string ChangeDisplay => (QuantityChange > 0 ? "+" : string.Empty) + PackQuantity.Format(QuantityChange, Math.Max(UnitsPerPack, 1));
    public string BalanceDisplay => PackQuantity.Format(BalanceUnits, Math.Max(UnitsPerPack, 1));
    public string AfterDisplay => PackQuantity.Format(BalanceUnits + QuantityChange, Math.Max(UnitsPerPack, 1));
    public decimal Value => ValuePaisa / 100m;
}

/// <summary>
/// Stock Adjustment: history of numbered adjustment vouchers and the entry form (reason, date, notes, batch lines with
/// a signed quantity in packs + loose). Posting is transactional and audit-logged; vouchers print on A4/A5.
/// Keys: Alt+N new, Alt+L add line, Alt+S save, Ctrl+P print, Esc cancel entry. Accepts <see cref="StockAdjustmentPrefill"/>.
/// </summary>
public partial class StockAdjustmentViewModel : BaseViewModel, INavigationAware
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    private readonly IStockService _stock;
    private readonly IMedicineService _medicines;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<StockAdjustment> _template;
    private StockAdjustmentPrefill? _prefill;
    private int _nearExpiryDays = StockRules.DefaultNearExpiryDays;

    // ─── History ───
    [ObservableProperty] private DateTime _fromDate = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime _toDate = DateTime.Today;
    [ObservableProperty] private ObservableCollection<StockAdjustment> _adjustments = new();
    [ObservableProperty] private StockAdjustment? _selectedAdjustment;
    [ObservableProperty] private StockAdjustment? _selectedDetail;

    // ─── Entry ───
    [ObservableProperty] private bool _isEntryOpen;
    [ObservableProperty] private StockOption<StockAdjustmentReason> _selectedReason;
    [ObservableProperty] private DateTime _adjustmentDate = DateTime.Today;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _nextNumber = string.Empty;
    [ObservableProperty] private string _itemSearch = string.Empty;
    [ObservableProperty] private Medicine? _selectedSearchItem;
    [ObservableProperty] private StockBatchRow? _selectedBatch;
    [ObservableProperty] private long _packs;
    [ObservableProperty] private long _looseUnits;
    [ObservableProperty] private bool _isAddition;
    [ObservableProperty] private string? _entryError;

    public ObservableCollection<Medicine> SearchResults { get; } = new();
    public ObservableCollection<StockBatchRow> BatchOptions { get; } = new();
    public ObservableCollection<StockAdjustmentDraftLine> DraftLines { get; } = new();

    public IReadOnlyList<StockOption<StockAdjustmentReason>> Reasons { get; } =
        Enum.GetValues<StockAdjustmentReason>().Select(r => new StockOption<StockAdjustmentReason>(r, r.ToLabel())).ToList();

    public StockAdjustmentViewModel(IStockService stock, IMedicineService medicines, IPrintService print, IDocumentTemplate<StockAdjustment> template)
    {
        _stock = stock;
        _medicines = medicines;
        _print = print;
        _template = template;
        _selectedReason = Reasons[0];
    }

    // ─── Derived ───
    public bool HasAdjustments => Adjustments.Count > 0;
    public bool HasDraftLines => DraftLines.Count > 0;
    public bool HasEntryError => !string.IsNullOrEmpty(EntryError);
    public bool HasSelectedDetail => SelectedDetail != null;
    /// <summary>Count correction can go either way; every other reason fixes the direction.</summary>
    public bool ShowDirectionChoice => SelectedReason.Value.RequiredSign() == 0;
    public string DirectionText => SelectedReason.Value.RequiredSign() switch
    {
        > 0 => "Adds stock",
        < 0 => "Removes stock",
        _ => IsAddition ? "Adds stock" : "Removes stock"
    };
    public string DraftTotalText
    {
        get
        {
            var total = DraftLines.Sum(l => l.ValuePaisa) / 100m;
            return $"{DraftLines.Count} line(s) · value {(total < 0 ? "-" : "")}₹{Math.Abs(total).ToString("N2", India)}";
        }
    }

    partial void OnEntryErrorChanged(string? value) => OnPropertyChanged(nameof(HasEntryError));
    partial void OnSelectedDetailChanged(StockAdjustment? value) => OnPropertyChanged(nameof(HasSelectedDetail));
    partial void OnIsAdditionChanged(bool value) => OnPropertyChanged(nameof(DirectionText));

    partial void OnSelectedReasonChanged(StockOption<StockAdjustmentReason> value)
    {
        OnPropertyChanged(nameof(ShowDirectionChoice));
        OnPropertyChanged(nameof(DirectionText));
        // Lines already entered were signed for the previous reason; re-sign them for the new one.
        var sign = Sign();
        var lines = DraftLines.ToList();
        DraftLines.Clear();
        foreach (var l in lines)
        {
            var change = Math.Abs(l.QuantityChange) * sign;
            var cost = l.QuantityChange == 0 ? 0 : Math.Abs(l.ValuePaisa);
            DraftLines.Add(l with { QuantityChange = change, ValuePaisa = sign * cost });
        }
        RaiseDraft();
    }

    partial void OnSelectedAdjustmentChanged(StockAdjustment? value) =>
        _ = SafeAsync(async () => SelectedDetail = value == null ? null : await _stock.GetAdjustmentAsync(value.Id));

    partial void OnItemSearchChanged(string value) => _ = SafeAsync(SearchItemsAsync);

    partial void OnSelectedSearchItemChanged(Medicine? value)
    {
        if (_suppressSearchItemLoad) return;
        _ = SafeAsync(() => LoadBatchesAsync(value?.Id, null));
    }

    /// <summary>Set while a prefill selects the item itself and loads its batches with the requested batch selected.</summary>
    private bool _suppressSearchItemLoad;

    partial void OnAdjustmentDateChanged(DateTime value) => _ = SafeAsync(RefreshNextNumberAsync);

    public void OnNavigatedTo(object? parameter) => _prefill = parameter as StockAdjustmentPrefill;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            _nearExpiryDays = await _stock.GetNearExpiryDaysAsync();
            await LoadHistoryAsync();
            if (_prefill is { } prefill)
            {
                _prefill = null;
                await StartEntryAsync();
                var medicine = await _medicines.GetByIdAsync(prefill.MedicineId);
                if (medicine != null)
                {
                    SearchResults.Clear();
                    SearchResults.Add(medicine);
                    _suppressSearchItemLoad = true;
                    try { SelectedSearchItem = medicine; }
                    finally { _suppressSearchItemLoad = false; }
                    await LoadBatchesAsync(medicine.Id, prefill.BatchId);
                }
            }
        }
        catch (Exception ex)
        {
            SetError($"Could not load stock adjustments: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        if (FromDate.Date > ToDate.Date)
        {
            SetError("'From' date must be on or before the 'To' date.");
            return;
        }
        var keep = SelectedAdjustment?.Id;
        Adjustments = new ObservableCollection<StockAdjustment>(await _stock.GetAdjustmentsAsync(FromDate.Date, ToDate.Date));
        SelectedAdjustment = Adjustments.FirstOrDefault(a => a.Id == keep) ?? Adjustments.FirstOrDefault();
        OnPropertyChanged(nameof(HasAdjustments));
    }

    // ─── Entry ───

    [RelayCommand]
    private async Task StartEntryAsync()
    {
        IsEntryOpen = true;
        EntryError = null;
        SelectedReason = Reasons[0];
        AdjustmentDate = DateTime.Today;
        Notes = string.Empty;
        ItemSearch = string.Empty;
        SearchResults.Clear();
        BatchOptions.Clear();
        SelectedBatch = null;
        DraftLines.Clear();
        Packs = 0;
        LooseUnits = 0;
        IsAddition = false;
        RaiseDraft();
        await RefreshNextNumberAsync();
    }

    [RelayCommand]
    private void CancelEntry()
    {
        IsEntryOpen = false;
        EntryError = null;
    }

    private async Task RefreshNextNumberAsync()
    {
        if (!IsEntryOpen) return;
        NextNumber = await _stock.PeekAdjustmentNumberAsync(AdjustmentDate);
    }

    private async Task SearchItemsAsync()
    {
        var text = ItemSearch.Trim();
        if (text.Length < 2) return;
        var results = await _medicines.SearchAsync(text);
        if (text != ItemSearch.Trim()) return; // a newer search is running
        SearchResults.Clear();
        foreach (var m in results.Where(m => m.IsActive).Take(50)) SearchResults.Add(m);
        if (SearchResults.Count == 1) SelectedSearchItem = SearchResults[0];
    }

    private async Task LoadBatchesAsync(long? medicineId, long? selectBatchId)
    {
        BatchOptions.Clear();
        SelectedBatch = null;
        if (medicineId is not { } id) return;
        var rows = StockRules.BatchRows(await _stock.GetBatchesAsync(id), DateTime.Today, _nearExpiryDays, showZero: true);
        foreach (var row in rows.Where(r => r.Status != BatchStockStatus.Inactive)) BatchOptions.Add(row);
        SelectedBatch = BatchOptions.FirstOrDefault(b => b.BatchId == selectBatchId) ?? BatchOptions.FirstOrDefault();
        if (BatchOptions.Count == 0) EntryError = "This item has no batches. Add a batch from Stock (Alt+B) first.";
    }

    private int Sign()
    {
        var required = SelectedReason.Value.RequiredSign();
        return required != 0 ? required : (IsAddition ? 1 : -1);
    }

    [RelayCommand]
    private void AddLine()
    {
        EntryError = null;
        if (SelectedSearchItem == null || SelectedBatch == null)
        {
            EntryError = "Search an item and choose its batch.";
            return;
        }
        var batch = SelectedBatch;
        if (Packs < 0 || LooseUnits < 0) { EntryError = "Quantity cannot be negative; the reason decides the direction."; return; }
        if (batch.UnitsPerPack > 1 && LooseUnits >= batch.UnitsPerPack) { EntryError = $"Loose units must be less than the pack size ({batch.UnitsPerPack})."; return; }
        if (batch.UnitsPerPack == 1 && LooseUnits > 0) { EntryError = "This batch has 1 unit per pack: enter packs only."; return; }
        var units = new PackQuantity(Packs, LooseUnits).ToUnits(batch.UnitsPerPack);
        if (units == 0) { EntryError = "Enter the quantity to adjust."; return; }
        if (DraftLines.Any(l => l.BatchId == batch.BatchId)) { EntryError = $"Batch {batch.BatchNumber} is already in this adjustment."; return; }

        var change = units * Sign();
        if (StockRules.ValidateAdjustmentBalance(batch.BatchNumber, batch.QuantityUnits, change) is { } error) { EntryError = error; return; }

        DraftLines.Add(new StockAdjustmentDraftLine(SelectedSearchItem.Id, SelectedSearchItem.Name, batch.BatchId, batch.BatchNumber,
            batch.ExpiryLabel, batch.UnitsPerPack, batch.QuantityUnits, change,
            StockRules.AdjustmentValuePaisa(change, batch.CostPaisa, batch.UnitsPerPack)));
        Packs = 0;
        LooseUnits = 0;
        RaiseDraft();
    }

    [RelayCommand]
    private void RemoveLine(StockAdjustmentDraftLine? line)
    {
        if (line == null) return;
        DraftLines.Remove(line);
        RaiseDraft();
    }

    private void RaiseDraft()
    {
        OnPropertyChanged(nameof(HasDraftLines));
        OnPropertyChanged(nameof(DraftTotalText));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!IsEntryOpen || IsLoading) return;
        EntryError = null;
        IsLoading = true;
        try
        {
            var posted = await _stock.PostAdjustmentAsync(new StockAdjustmentRequest
            {
                AdjustmentDate = AdjustmentDate.Date,
                Reason = SelectedReason.Value,
                Notes = Notes,
                Lines = DraftLines.Select(l => new StockAdjustmentLineRequest(l.BatchId, l.QuantityChange)).ToList()
            });
            IsEntryOpen = false;
            if (posted.AdjustmentDate < FromDate) FromDate = posted.AdjustmentDate;
            if (posted.AdjustmentDate > ToDate) ToDate = posted.AdjustmentDate;
            await LoadHistoryAsync();
            SelectedAdjustment = Adjustments.FirstOrDefault(a => a.Id == posted.Id);
            SetStatus($"Adjustment {posted.AdjustmentNumber} saved: {posted.Lines.Count} batch(es), value ₹{posted.TotalValue.ToString("N2", India)}. Press Ctrl+P to print.");
        }
        catch (StockValidationException ex)
        {
            EntryError = ex.Message;
        }
        catch (Exception ex)
        {
            EntryError = $"Could not save the adjustment: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task PrintAsync() => SafeAsync(async () =>
    {
        if (SelectedDetail == null)
        {
            SetError("Select an adjustment to print.");
            return;
        }
        await _print.PrintAsync(_template, SelectedDetail);
    });

    [RelayCommand]
    private Task PreviewAsync() => SafeAsync(async () =>
    {
        if (SelectedDetail != null) await _print.PreviewAsync(_template, SelectedDetail);
    });
}
