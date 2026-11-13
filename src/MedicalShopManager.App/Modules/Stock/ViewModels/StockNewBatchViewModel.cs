using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Stock;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Stock.ViewModels;

/// <summary>
/// "+ Add New Batch": opening stock for an existing item (batch, expiry MM/YY, packs + loose, MRP and rates per pack).
/// Posted by <see cref="IStockService.AddBatchAsync"/> with an OpeningStock movement in one transaction.
/// </summary>
public partial class StockNewBatchViewModel : StockDialogViewModel
{
    private readonly IStockService _stock;
    private readonly ISupplierRepository _suppliers;
    private long _medicineId;

    [ObservableProperty] private string _itemName = string.Empty;
    [ObservableProperty] private bool _allowLooseSale = true;
    [ObservableProperty] private string _batchNumber = string.Empty;
    [ObservableProperty] private string _expiryText = string.Empty;
    [ObservableProperty] private int _unitsPerPack = 1;
    [ObservableProperty] private long _packs;
    [ObservableProperty] private long _looseUnits;
    [ObservableProperty] private decimal _mrp;
    [ObservableProperty] private decimal _purchaseRate;
    [ObservableProperty] private decimal _ptr;
    [ObservableProperty] private decimal _saleRate;
    [ObservableProperty] private decimal _gstPercent;
    [ObservableProperty] private decimal _discountPercent;
    [ObservableProperty] private Supplier? _supplier;
    [ObservableProperty] private DateTime _entryDate = DateTime.Today;

    public ObservableCollection<Supplier> Suppliers { get; } = new();
    public IReadOnlyList<decimal> GstSlabs { get; } = new[] { 0m, 5m, 12m, 18m, 28m };

    public StockNewBatchViewModel(IStockService stock, ISupplierRepository suppliers)
    {
        _stock = stock;
        _suppliers = suppliers;
    }

    /// <summary>Live preview: "3 + 5 = 50 units · value ₹… · margin …%".</summary>
    public string PreviewText
    {
        get
        {
            if (UnitsPerPack < 1 || Packs < 0 || LooseUnits < 0) return string.Empty;
            var units = Packs * UnitsPerPack + LooseUnits;
            var batch = new MedicineBatch
            {
                UnitsPerPack = UnitsPerPack,
                QuantityRemaining = (int)Math.Min(units, int.MaxValue),
                PurchaseRatePaisa = ToPaisa(PurchaseRate),
                DiscountPercent = Math.Clamp(DiscountPercent, 0m, 100m),
                SaleRatePaisa = ToPaisa(SaleRate > 0 ? SaleRate : Mrp),
                MrpPaisa = ToPaisa(Mrp),
                GstRatePercent = Math.Clamp(GstPercent, 0m, 100m)
            };
            var value = StockRules.StockValuePaisa(batch) / 100m;
            var margin = batch.SaleRatePaisa > 0 ? $" · margin {StockRules.MarginPercent(batch):0.##}%" : string.Empty;
            return $"{PackQuantity.Format(units, UnitsPerPack)} = {units} unit(s) · value ₹{value:N2}{margin}";
        }
    }

    partial void OnPacksChanged(long value) => OnPropertyChanged(nameof(PreviewText));
    partial void OnLooseUnitsChanged(long value) => OnPropertyChanged(nameof(PreviewText));
    partial void OnUnitsPerPackChanged(int value) => OnPropertyChanged(nameof(PreviewText));
    partial void OnPurchaseRateChanged(decimal value) => OnPropertyChanged(nameof(PreviewText));
    partial void OnDiscountPercentChanged(decimal value) => OnPropertyChanged(nameof(PreviewText));
    partial void OnGstPercentChanged(decimal value) => OnPropertyChanged(nameof(PreviewText));
    partial void OnSaleRateChanged(decimal value) => OnPropertyChanged(nameof(PreviewText));
    partial void OnMrpChanged(decimal value) => OnPropertyChanged(nameof(PreviewText));

    public async Task OpenAsync(long medicineId)
    {
        ErrorMessage = null;
        if (Suppliers.Count == 0)
            foreach (var s in (await _suppliers.GetAllAsync()).Where(s => s.IsActive).OrderBy(s => s.Name))
                Suppliers.Add(s);

        var settings = await _stock.GetSettingsAsync(medicineId);
        var detail = await _stock.GetItemDetailAsync(medicineId, showZeroBatches: false);
        if (detail == null || settings == null)
        {
            ErrorMessage = "The item no longer exists.";
            return;
        }

        var h = detail.Header;
        _medicineId = medicineId;
        Title = "Add New Batch";
        ItemName = $"{h.Name} ({h.PackDisplay})";
        AllowLooseSale = settings.AllowLooseSale;
        BatchNumber = string.Empty;
        ExpiryText = string.Empty;
        UnitsPerPack = h.UnitsPerPack;
        Packs = 0;
        LooseUnits = 0;
        Mrp = h.MrpPaisa / 100m;
        PurchaseRate = h.PurchaseRatePaisa / 100m;
        Ptr = 0;
        SaleRate = h.SaleRatePaisa / 100m;
        GstPercent = h.GstRatePercent;
        DiscountPercent = 0;
        Supplier = null;
        EntryDate = DateTime.Today;
        IsOpen = true;
    }

    protected override async Task<long> SaveCoreAsync()
    {
        if (!StockCsv.TryParseExpiry(ExpiryText, out var month, out var year))
            throw new StockValidationException("Enter the expiry as MM/YY, e.g. 08/27.");

        await _stock.AddBatchAsync(new NewBatchRequest
        {
            MedicineId = _medicineId,
            BatchNumber = BatchNumber,
            ExpiryMonth = month,
            ExpiryYear = year,
            UnitsPerPack = UnitsPerPack,
            Packs = Packs,
            LooseUnits = LooseUnits,
            MrpPaisa = ToPaisa(Mrp),
            PurchaseRatePaisa = ToPaisa(PurchaseRate),
            PtrPaisa = ToPaisa(Ptr),
            SaleRatePaisa = ToPaisa(SaleRate),
            GstPercent = GstPercent,
            DiscountPercent = DiscountPercent,
            SupplierId = Supplier?.Id,
            EntryDate = EntryDate
        });
        return _medicineId;
    }
}
