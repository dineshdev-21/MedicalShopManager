using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Stock;

namespace MedicalShopManager.App.Modules.Stock.ViewModels;

/// <summary>
/// The Stock workspace (Localwell "Stocks"): left "Stock on hand" list with search / filters / sort, right item
/// detail with header and tabs BATCHES | PURCHASES | SALES | TIMELINE | SUBSTITUTE | BRANCH STK | DISTRIBUTOR.
/// Keys: Ctrl+Left / Ctrl+Right previous / next item, F4 item settings, Alt+N new item, Alt+B new batch,
/// Alt+E edit item, Alt+A adjust stock, Ctrl+1…7 tabs, F5 refresh, Ctrl+P print statement, Esc closes a dialog.
/// Accepts a medicine id as navigation parameter to open that item.
/// </summary>
public partial class StockViewModel : BaseViewModel, INavigationAware
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    private readonly IStockService _stock;
    private readonly INavigationService _navigation;
    private readonly IPrintService _print;
    private readonly IDocumentTemplate<StockStatement> _statementTemplate;

    private IReadOnlyList<StockListItem> _all = Array.Empty<StockListItem>();
    private long? _pendingSelectId;
    private int _nearExpiryDays = StockRules.DefaultNearExpiryDays;
    private bool _suppressReload;
    private readonly HashSet<StockDetailTab> _loadedTabs = new();

    // ─── List ───
    [ObservableProperty] private ObservableCollection<StockListItem> _items = new();
    [ObservableProperty] private StockListItem? _selectedItem;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private StockOption<StockFilter> _selectedFilter;
    [ObservableProperty] private StockOption<DrugSchedule?> _selectedSchedule;
    [ObservableProperty] private string _selectedManufacturer = AllCompanies;
    [ObservableProperty] private StockOption<StockSort> _selectedSort;
    [ObservableProperty] private bool _includeInactive;
    [ObservableProperty] private StockSummary _summary = new();

    // ─── Detail ───
    [ObservableProperty] private StockItemHeader? _header;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _showZeroBatches;
    [ObservableProperty] private bool _isDetailLoading;
    public ObservableCollection<StockBatchRow> Batches { get; } = new();
    public ObservableCollection<ItemPurchaseRow> Purchases { get; } = new();
    public ObservableCollection<ItemSaleRow> Sales { get; } = new();
    public ObservableCollection<StockTimelineEntry> Timeline { get; } = new();
    public ObservableCollection<SubstituteItem> Substitutes { get; } = new();
    public ObservableCollection<ItemDistributorRow> Distributors { get; } = new();
    public ObservableCollection<string> Manufacturers { get; } = new() { AllCompanies };

    // ─── Dialogs ───
    public StockItemEditorViewModel ItemEditor { get; }
    public StockNewBatchViewModel BatchEditor { get; }
    public StockItemSettingsViewModel SettingsEditor { get; }

    public const string AllCompanies = "All companies";

    public IReadOnlyList<StockOption<StockFilter>> FilterOptions { get; } = new[]
    {
        new StockOption<StockFilter>(StockFilter.All, "All items"),
        new StockOption<StockFilter>(StockFilter.InStock, "In stock"),
        new StockOption<StockFilter>(StockFilter.OutOfStock, "Out of stock"),
        new StockOption<StockFilter>(StockFilter.LowStock, "Low stock"),
        new StockOption<StockFilter>(StockFilter.NearExpiry, "Near expiry"),
        new StockOption<StockFilter>(StockFilter.Expired, "Has expired stock")
    };

    public IReadOnlyList<StockOption<DrugSchedule?>> ScheduleOptions { get; } =
        new[] { new StockOption<DrugSchedule?>(null, "All schedules") }
            .Concat(Enum.GetValues<DrugSchedule>().Select(s => new StockOption<DrugSchedule?>(s, s == DrugSchedule.None ? "OTC (-)" : s.ToLabel())))
            .ToList();

    public IReadOnlyList<StockOption<StockSort>> SortOptions { get; } = new[]
    {
        new StockOption<StockSort>(StockSort.NameAsc, "Name A-Z"),
        new StockOption<StockSort>(StockSort.NameDesc, "Name Z-A"),
        new StockOption<StockSort>(StockSort.StockHighToLow, "Stock: high to low"),
        new StockOption<StockSort>(StockSort.StockLowToHigh, "Stock: low to high"),
        new StockOption<StockSort>(StockSort.ValueHighToLow, "Value: high to low"),
        new StockOption<StockSort>(StockSort.ExpirySoonest, "Expiry: soonest first")
    };

    public IReadOnlyList<string> TabTitles { get; } = Enum.GetValues<StockDetailTab>().Select(t => t.ToTitle()).ToList();

    public StockViewModel(IStockService stock, INavigationService navigation, IPrintService print,
        IDocumentTemplate<StockStatement> statementTemplate, StockItemEditorViewModel itemEditor,
        StockNewBatchViewModel batchEditor, StockItemSettingsViewModel settingsEditor)
    {
        _stock = stock;
        _navigation = navigation;
        _print = print;
        _statementTemplate = statementTemplate;
        ItemEditor = itemEditor;
        BatchEditor = batchEditor;
        SettingsEditor = settingsEditor;
        _selectedFilter = FilterOptions[0];
        _selectedSchedule = ScheduleOptions[0];
        _selectedSort = SortOptions[0];

        foreach (var dialog in new StockDialogViewModel[] { ItemEditor, BatchEditor, SettingsEditor })
        {
            dialog.Saved += OnDialogSaved;
            dialog.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StockDialogViewModel.IsOpen)) OnPropertyChanged(nameof(IsDialogOpen));
            };
        }
    }

    // ─── Derived display ───
    public bool IsDialogOpen => ItemEditor.IsOpen || BatchEditor.IsOpen || SettingsEditor.IsOpen;
    public bool HasSelection => Header != null;
    public bool HasItems => Items.Count > 0;
    public bool IsListEmpty => !IsLoading && Items.Count == 0;
    public string StockOnHandText => $"₹{Summary.StockValue.ToString("N2", India)}";
    public string ListCountText => Summary.ItemCount == 1 ? "1 item" : $"{Summary.ItemCount} items";
    public string SummaryText => $"{Summary.InStockCount} in stock · {Summary.LowStockCount} low · {Summary.OutOfStockCount} out · MRP value ₹{Summary.MrpValue.ToString("N2", India)}";
    public string PositionText => SelectedItem == null ? string.Empty : $"{Items.IndexOf(SelectedItem) + 1} / {Items.Count}";
    public bool HasBatches => Batches.Count > 0;
    public bool HasPurchases => Purchases.Count > 0;
    public bool HasSales => Sales.Count > 0;
    public bool HasTimeline => Timeline.Count > 0;
    public bool HasSubstitutes => Substitutes.Count > 0;
    public bool HasDistributors => Distributors.Count > 0;
    public string MarginText => Header == null ? "-" : $"{Header.MarginPercent:0.##}%";
    public bool HasComposition => !string.IsNullOrWhiteSpace(Header?.Composition);

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is long medicineId) _pendingSelectId = medicineId;
    }

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            _nearExpiryDays = await _stock.GetNearExpiryDaysAsync();
            _suppressReload = true;
            ShowZeroBatches = await _stock.GetShowZeroBatchesAsync();
            _suppressReload = false;

            var manufacturers = await _stock.GetManufacturersAsync();
            Manufacturers.Clear();
            Manufacturers.Add(AllCompanies);
            foreach (var m in manufacturers) Manufacturers.Add(m);
            if (!Manufacturers.Contains(SelectedManufacturer)) SelectedManufacturer = AllCompanies;

            await ReloadListAsync(_pendingSelectId ?? SelectedItem?.MedicineId);
            _pendingSelectId = null;
        }
        catch (Exception ex)
        {
            SetError($"Could not load stock: {ex.Message}");
        }
        finally
        {
            _suppressReload = false;
            IsLoading = false;
            OnPropertyChanged(nameof(IsListEmpty));
        }
    }

    private StockListQuery BuildQuery() => new()
    {
        Search = SearchText,
        Filter = SelectedFilter?.Value ?? StockFilter.All,
        Schedule = SelectedSchedule?.Value,
        Manufacturer = SelectedManufacturer == AllCompanies ? null : SelectedManufacturer,
        Sort = SelectedSort?.Value ?? StockSort.NameAsc,
        NearExpiryDays = _nearExpiryDays,
        IncludeInactive = IncludeInactive
    };

    /// <summary>Re-reads every item from the database, then re-applies the filters.</summary>
    private async Task ReloadListAsync(long? selectId)
    {
        var result = await _stock.GetStockListAsync(BuildQuery() with { Search = null, Filter = StockFilter.All, Schedule = null, Manufacturer = null });
        _all = result.Items;
        ApplyFilters(selectId);
    }

    /// <summary>Filters the cached list in memory (instant search while typing).</summary>
    private void ApplyFilters(long? selectId = null)
    {
        var keep = selectId ?? SelectedItem?.MedicineId;
        var result = StockRules.Apply(_all, BuildQuery());
        Items = new ObservableCollection<StockListItem>(result.Items);
        Summary = result.Summary;
        SelectedItem = Items.FirstOrDefault(i => i.MedicineId == keep) ?? Items.FirstOrDefault();
        if (SelectedItem == null) ClearDetail();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(PositionText));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedFilterChanged(StockOption<StockFilter> value) => ApplyFilters();
    partial void OnSelectedScheduleChanged(StockOption<DrugSchedule?> value) => ApplyFilters();
    partial void OnSelectedManufacturerChanged(string value) => ApplyFilters();
    partial void OnSelectedSortChanged(StockOption<StockSort> value) => ApplyFilters();
    partial void OnIncludeInactiveChanged(bool value) => _ = SafeAsync(() => ReloadListAsync(null));

    partial void OnSummaryChanged(StockSummary value)
    {
        OnPropertyChanged(nameof(StockOnHandText));
        OnPropertyChanged(nameof(ListCountText));
        OnPropertyChanged(nameof(SummaryText));
    }

    partial void OnSelectedItemChanged(StockListItem? value)
    {
        OnPropertyChanged(nameof(PositionText));
        if (value != null) _ = SafeAsync(() => LoadDetailAsync(value.MedicineId));
    }

    partial void OnSelectedTabIndexChanged(int value) => _ = SafeAsync(LoadCurrentTabAsync);

    partial void OnShowZeroBatchesChanged(bool value)
    {
        if (_suppressReload) return;
        _ = SafeAsync(async () =>
        {
            await _stock.SetShowZeroBatchesAsync(value);
            if (Header != null) await LoadDetailAsync(Header.MedicineId);
        });
    }

    partial void OnHeaderChanged(StockItemHeader? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(MarginText));
        OnPropertyChanged(nameof(HasComposition));
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

    // ─── Detail loading ───

    private void ClearDetail()
    {
        Header = null;
        _loadedTabs.Clear();
        foreach (var list in new System.Collections.IList[] { Batches, Purchases, Sales, Timeline, Substitutes, Distributors })
            list.Clear();
        RaiseTabFlags();
    }

    private async Task LoadDetailAsync(long medicineId)
    {
        IsDetailLoading = true;
        try
        {
            var detail = await _stock.GetItemDetailAsync(medicineId, ShowZeroBatches);
            if (SelectedItem?.MedicineId != medicineId) return; // user moved on
            ClearDetail();
            if (detail == null)
            {
                SetError("This item no longer exists. Press F5 to refresh.");
                return;
            }
            Header = detail.Header;
            foreach (var b in detail.Batches) Batches.Add(b);
            _loadedTabs.Add(StockDetailTab.Batches);
            await LoadCurrentTabAsync();
        }
        finally
        {
            IsDetailLoading = false;
            RaiseTabFlags();
        }
    }

    private async Task LoadCurrentTabAsync()
    {
        if (Header == null) return;
        var tab = (StockDetailTab)Math.Clamp(SelectedTabIndex, 0, TabTitles.Count - 1);
        if (!_loadedTabs.Add(tab)) return;
        var id = Header.MedicineId;
        switch (tab)
        {
            case StockDetailTab.Purchases:
                Fill(Purchases, await _stock.GetPurchasesAsync(id));
                break;
            case StockDetailTab.Sales:
                Fill(Sales, await _stock.GetSalesAsync(id));
                break;
            case StockDetailTab.Timeline:
                Fill(Timeline, await _stock.GetTimelineAsync(id));
                break;
            case StockDetailTab.Substitute:
                Fill(Substitutes, await _stock.GetSubstitutesAsync(id));
                break;
            case StockDetailTab.Distributor:
                Fill(Distributors, await _stock.GetDistributorsAsync(id));
                break;
        }
        RaiseTabFlags();
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    private void RaiseTabFlags()
    {
        foreach (var name in new[] { nameof(HasBatches), nameof(HasPurchases), nameof(HasSales), nameof(HasTimeline), nameof(HasSubstitutes), nameof(HasDistributors) })
            OnPropertyChanged(name);
    }

    // ─── Commands ───

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedFilter = FilterOptions[0];
        SelectedSchedule = ScheduleOptions[0];
        SelectedManufacturer = AllCompanies;
        SelectedSort = SortOptions[0];
    }

    [RelayCommand]
    private void Previous() => Move(-1);

    [RelayCommand]
    private void Next() => Move(1);

    private void Move(int step)
    {
        if (Items.Count == 0 || IsDialogOpen) return;
        var index = SelectedItem == null ? 0 : Items.IndexOf(SelectedItem) + step;
        SelectedItem = Items[Math.Clamp(index, 0, Items.Count - 1)];
    }

    [RelayCommand]
    private void SelectTab(object? index)
    {
        // BRANCH STK is a disabled placeholder (no multi-branch sync offline), so Ctrl+6 does nothing.
        if (int.TryParse(index?.ToString(), out var i) && i >= 0 && i < TabTitles.Count && i != (int)StockDetailTab.BranchStock)
            SelectedTabIndex = i;
    }

    [RelayCommand]
    private Task AddNewItemAsync() => SafeAsync(() => ItemEditor.OpenAsync());

    [RelayCommand]
    private Task EditItemAsync() => Header == null ? Task.CompletedTask : SafeAsync(() => ItemEditor.OpenAsync(Header.MedicineId));

    [RelayCommand]
    private Task AddNewBatchAsync()
    {
        if (Header == null)
        {
            SetError("Select an item first, then add its batch.");
            return Task.CompletedTask;
        }
        return SafeAsync(() => BatchEditor.OpenAsync(Header.MedicineId));
    }

    [RelayCommand]
    private Task OpenSettingsAsync()
    {
        if (Header == null)
        {
            SetError("Select an item to open its settings (F4).");
            return Task.CompletedTask;
        }
        return SafeAsync(() => SettingsEditor.OpenAsync(Header.MedicineId, Header.Name, Header.UnitsPerPack));
    }

    [RelayCommand]
    private void CloseDialog()
    {
        ItemEditor.Close();
        BatchEditor.Close();
        SettingsEditor.Close();
    }

    /// <summary>Opens Stock Adjustment for a batch row (or the selected item when no row is given).</summary>
    [RelayCommand]
    private void AdjustStock(StockBatchRow? row)
    {
        if (Header == null) return;
        _navigation.NavigateTo("StockAdjustments", new StockAdjustmentPrefill(Header.MedicineId, row?.BatchId));
    }

    [RelayCommand]
    private void OpenSubstitute(SubstituteItem? item)
    {
        if (item == null) return;
        var target = Items.FirstOrDefault(i => i.MedicineId == item.MedicineId);
        if (target == null)
        {
            // Hidden by the current filters: clear them so the substitute is visible.
            ClearFilters();
            target = Items.FirstOrDefault(i => i.MedicineId == item.MedicineId);
        }
        if (target != null)
        {
            SelectedItem = target;
            SelectedTabIndex = (int)StockDetailTab.Batches;
        }
    }

    [RelayCommand]
    private void OpenPurchases() => _navigation.NavigateTo("Purchases");

    [RelayCommand]
    private Task PrintStatementAsync() => SafeAsync(async () =>
    {
        if (Items.Count == 0)
        {
            SetError("Nothing to print: the list is empty.");
            return;
        }
        var title = $"Stock Statement — {SelectedFilter.Label}" + (SelectedManufacturer == AllCompanies ? string.Empty : $" — {SelectedManufacturer}");
        await _print.PreviewAsync(_statementTemplate, new StockStatement(title, DateTime.Now, Items.ToList(), Summary));
    });

    private void OnDialogSaved(object? sender, long medicineId)
    {
        var message = sender switch
        {
            StockItemEditorViewModel { IsEdit: true } => "Item updated.",
            StockItemEditorViewModel => "Item created. Add its opening batch with Alt+B.",
            StockNewBatchViewModel => "Batch added to stock.",
            _ => "Item settings saved."
        };
        _ = SafeAsync(async () =>
        {
            _pendingSelectId = medicineId;
            await LoadAsync();
            if (SelectedItem?.MedicineId != medicineId)
            {
                // A new item can be hidden by the filters (e.g. "In stock"); show everything so it can be selected.
                ClearFilters();
                SelectedItem = Items.FirstOrDefault(i => i.MedicineId == medicineId) ?? SelectedItem;
            }
            SetStatus(message);
        });
    }
}
