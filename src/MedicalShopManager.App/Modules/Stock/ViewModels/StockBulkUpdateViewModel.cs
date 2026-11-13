using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Stock;

namespace MedicalShopManager.App.Modules.Stock.ViewModels;

/// <summary>An item in the bulk-update list with its selection tick.</summary>
public partial class StockBulkItem : ObservableObject
{
    public StockBulkItem(StockListItem item) => Item = item;
    public StockListItem Item { get; }
    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>
/// Bulk Update Settings: filter items (search, stock filter, schedule, company), tick the ones to change, and set GST,
/// HSN, rack, min/max stock, max discount, schedule or category on all of them in one transaction. A blank field is
/// left unchanged. Applying needs a second confirmation. Keys: F5 refresh, Alt+S apply / confirm, Esc cancel.
/// </summary>
public partial class StockBulkUpdateViewModel : BaseViewModel
{
    private readonly IStockService _stock;
    private IReadOnlyList<StockListItem> _all = Array.Empty<StockListItem>();
    private int _nearExpiryDays = StockRules.DefaultNearExpiryDays;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private StockOption<StockFilter> _selectedFilter;
    [ObservableProperty] private StockOption<DrugSchedule?> _selectedScheduleFilter;
    [ObservableProperty] private string _selectedManufacturer = StockViewModel.AllCompanies;

    [ObservableProperty] private string _gstText = string.Empty;
    [ObservableProperty] private string _hsnText = string.Empty;
    [ObservableProperty] private string _rackText = string.Empty;
    [ObservableProperty] private string _minStockText = string.Empty;
    [ObservableProperty] private string _maxStockText = string.Empty;
    [ObservableProperty] private string _maxDiscountText = string.Empty;
    [ObservableProperty] private string _categoryText = string.Empty;
    [ObservableProperty] private StockOption<DrugSchedule?> _newSchedule;
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmText = string.Empty;

    private StockBulkUpdate? _pending;

    public ObservableCollection<StockBulkItem> Items { get; } = new();
    public ObservableCollection<string> Manufacturers { get; } = new() { StockViewModel.AllCompanies };

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
        new[] { new StockOption<DrugSchedule?>(null, "Any schedule") }
            .Concat(Enum.GetValues<DrugSchedule>().Select(s => new StockOption<DrugSchedule?>(s, s == DrugSchedule.None ? "OTC (-)" : s.ToLabel())))
            .ToList();

    public IReadOnlyList<StockOption<DrugSchedule?>> NewScheduleOptions { get; } =
        new[] { new StockOption<DrugSchedule?>(null, "(unchanged)") }
            .Concat(Enum.GetValues<DrugSchedule>().Select(s => new StockOption<DrugSchedule?>(s, s == DrugSchedule.None ? "OTC (-)" : s.ToLabel())))
            .ToList();

    public StockBulkUpdateViewModel(IStockService stock)
    {
        _stock = stock;
        _selectedFilter = FilterOptions[0];
        _selectedScheduleFilter = ScheduleOptions[0];
        _newSchedule = NewScheduleOptions[0];
    }

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public string SelectionText => $"{SelectedCount} of {Items.Count} item(s) selected";
    public bool HasItems => Items.Count > 0;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            _nearExpiryDays = await _stock.GetNearExpiryDaysAsync();
            Manufacturers.Clear();
            Manufacturers.Add(StockViewModel.AllCompanies);
            foreach (var m in await _stock.GetManufacturersAsync()) Manufacturers.Add(m);
            _all = (await _stock.GetStockListAsync(new StockListQuery { NearExpiryDays = _nearExpiryDays })).Items;
            ApplyFilters();
        }
        catch (Exception ex)
        {
            SetError($"Could not load items: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilters()
    {
        var result = StockRules.Apply(_all, new StockListQuery
        {
            Search = SearchText,
            Filter = SelectedFilter?.Value ?? StockFilter.All,
            Schedule = SelectedScheduleFilter?.Value,
            Manufacturer = SelectedManufacturer == StockViewModel.AllCompanies ? null : SelectedManufacturer,
            NearExpiryDays = _nearExpiryDays
        });
        foreach (var old in Items) old.PropertyChanged -= OnItemChanged;
        Items.Clear();
        foreach (var item in result.Items)
        {
            var row = new StockBulkItem(item);
            row.PropertyChanged += OnItemChanged;
            Items.Add(row);
        }
        IsConfirming = false;
        RaiseSelection();
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StockBulkItem.IsSelected)) RaiseSelection();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasItems));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedFilterChanged(StockOption<StockFilter> value) => ApplyFilters();
    partial void OnSelectedScheduleFilterChanged(StockOption<DrugSchedule?> value) => ApplyFilters();
    partial void OnSelectedManufacturerChanged(string value) => ApplyFilters();

    [RelayCommand]
    private void SelectAll(object? value)
    {
        var select = value is bool b ? b : !Items.All(i => i.IsSelected);
        foreach (var item in Items) item.IsSelected = select;
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void CancelConfirm() => IsConfirming = false;

    /// <summary>First press builds and validates the update and asks for confirmation; the second applies it.</summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsConfirming && _pending != null)
        {
            await CommitAsync(_pending);
            return;
        }

        ClearStatus();
        if (!TryBuild(out var update, out var error))
        {
            SetError(error);
            return;
        }
        if (StockRules.ValidateBulkUpdate(update) is { } invalid)
        {
            SetError(invalid);
            return;
        }
        _pending = update;
        ConfirmText = $"Update {update.MedicineIds.Count} item(s): {Describe(update)}? Press Alt+S again (or Confirm) to apply.";
        IsConfirming = true;
    }

    private async Task CommitAsync(StockBulkUpdate update)
    {
        IsLoading = true;
        try
        {
            var result = await _stock.BulkUpdateAsync(update);
            IsConfirming = false;
            _pending = null;
            await LoadAsync();
            SetStatus($"{result.Succeeded} item(s) updated." + (result.Failed > 0 ? " " + string.Join(" ", result.Errors) : string.Empty));
        }
        catch (StockValidationException ex)
        {
            IsConfirming = false;
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            IsConfirming = false;
            SetError($"Bulk update failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool TryBuild(out StockBulkUpdate update, out string error)
    {
        update = new StockBulkUpdate();
        error = string.Empty;
        var inv = CultureInfo.InvariantCulture;

        decimal? gst = null, disc = null;
        int? min = null, max = null;
        if (GstText.Trim() is { Length: > 0 } g)
        {
            if (!decimal.TryParse(g.TrimEnd('%'), NumberStyles.Number, inv, out var v)) { error = "GST % must be a number, e.g. 12."; return false; }
            gst = v;
        }
        if (MaxDiscountText.Trim() is { Length: > 0 } d)
        {
            if (!decimal.TryParse(d.TrimEnd('%'), NumberStyles.Number, inv, out var v)) { error = "Max discount % must be a number."; return false; }
            disc = v;
        }
        if (MinStockText.Trim() is { Length: > 0 } mn)
        {
            if (!int.TryParse(mn, NumberStyles.Integer, inv, out var v)) { error = "Min stock must be a whole number of units."; return false; }
            min = v;
        }
        if (MaxStockText.Trim() is { Length: > 0 } mx)
        {
            if (!int.TryParse(mx, NumberStyles.Integer, inv, out var v)) { error = "Max stock must be a whole number of units."; return false; }
            max = v;
        }

        update = new StockBulkUpdate
        {
            MedicineIds = Items.Where(i => i.IsSelected).Select(i => i.Item.MedicineId).ToList(),
            GstRatePercent = gst,
            HsnCode = string.IsNullOrWhiteSpace(HsnText) ? null : HsnText.Trim(),
            RackLocation = string.IsNullOrWhiteSpace(RackText) ? null : RackText.Trim(),
            MinStock = min,
            MaxStock = max,
            MaxDiscountPercent = disc,
            Schedule = NewSchedule?.Value,
            Category = string.IsNullOrWhiteSpace(CategoryText) ? null : CategoryText.Trim()
        };
        return true;
    }

    private static string Describe(StockBulkUpdate u)
    {
        var parts = new List<string>();
        if (u.GstRatePercent is { } gst) parts.Add($"GST {gst:0.##}%");
        if (u.HsnCode != null) parts.Add($"HSN {u.HsnCode}");
        if (u.RackLocation != null) parts.Add($"rack {u.RackLocation}");
        if (u.MinStock is { } min) parts.Add($"min stock {min}");
        if (u.MaxStock is { } max) parts.Add($"max stock {max}");
        if (u.MaxDiscountPercent is { } d) parts.Add($"max discount {d:0.##}%");
        if (u.Schedule is { } s) parts.Add($"schedule {(s == DrugSchedule.None ? "OTC" : s.ToLabel())}");
        if (u.Category != null) parts.Add($"category {u.Category}");
        return string.Join(", ", parts);
    }
}
