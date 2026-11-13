using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Stock;

namespace MedicalShopManager.App.Modules.Stock.ViewModels;

/// <summary>Item settings (F4): rack, min/max stock, discount limits and lock, schedule, loose sale, shortbook exclusion.</summary>
public partial class StockItemSettingsViewModel : StockDialogViewModel
{
    private readonly IStockService _stock;
    private long _medicineId;

    [ObservableProperty] private string _itemName = string.Empty;
    [ObservableProperty] private string _rackLocation = string.Empty;
    [ObservableProperty] private int _minStock;
    [ObservableProperty] private int _maxStock;
    [ObservableProperty] private decimal? _maxDiscountPercent;
    [ObservableProperty] private decimal _minDiscountPercent;
    [ObservableProperty] private DrugSchedule _schedule;
    [ObservableProperty] private bool _allowLooseSale = true;
    [ObservableProperty] private bool _isDiscountLocked;
    [ObservableProperty] private bool _ignoreForShortage;
    [ObservableProperty] private int _unitsPerPack = 1;

    public IReadOnlyList<StockOption<DrugSchedule>> Schedules { get; } =
        Enum.GetValues<DrugSchedule>().Select(s => new StockOption<DrugSchedule>(s, s == DrugSchedule.None ? "OTC (-)" : s.ToLabel())).ToList();

    public StockItemSettingsViewModel(IStockService stock) => _stock = stock;

    /// <summary>Min/max stock are units; this shows them in packs for the item's pack size.</summary>
    public string StockLimitsHint => UnitsPerPack > 1
        ? $"Min/max are in units ({UnitsPerPack} per pack): min = {Core.Pricing.PackQuantity.Format(MinStock, UnitsPerPack)} pack(s)."
        : "Min/max are in units.";

    partial void OnMinStockChanged(int value) => OnPropertyChanged(nameof(StockLimitsHint));

    public async Task OpenAsync(long medicineId, string itemName, int unitsPerPack)
    {
        ErrorMessage = null;
        var settings = await _stock.GetSettingsAsync(medicineId);
        if (settings == null)
        {
            ErrorMessage = "The item no longer exists.";
            return;
        }
        _medicineId = medicineId;
        Title = "Item Settings (F4)";
        ItemName = itemName;
        UnitsPerPack = Math.Max(unitsPerPack, 1);
        RackLocation = settings.RackLocation ?? string.Empty;
        MinStock = settings.MinStock;
        MaxStock = settings.MaxStock;
        MaxDiscountPercent = settings.MaxDiscountPercent;
        MinDiscountPercent = settings.MinDiscountPercent;
        Schedule = settings.Schedule;
        AllowLooseSale = settings.AllowLooseSale;
        IsDiscountLocked = settings.IsDiscountLocked;
        IgnoreForShortage = settings.IgnoreForShortage;
        OnPropertyChanged(nameof(StockLimitsHint));
        IsOpen = true;
    }

    protected override async Task<long> SaveCoreAsync()
    {
        await _stock.SaveSettingsAsync(new StockItemSettings
        {
            MedicineId = _medicineId,
            RackLocation = Clean(RackLocation),
            MinStock = MinStock,
            MaxStock = MaxStock,
            MaxDiscountPercent = MaxDiscountPercent,
            MinDiscountPercent = MinDiscountPercent,
            Schedule = Schedule,
            AllowLooseSale = AllowLooseSale,
            IsDiscountLocked = IsDiscountLocked,
            IgnoreForShortage = IgnoreForShortage
        });
        return _medicineId;
    }
}
