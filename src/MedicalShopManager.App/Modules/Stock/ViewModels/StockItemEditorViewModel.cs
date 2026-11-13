using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.App.Modules.Stock.ViewModels;

/// <summary>
/// "+ Add New Item" / edit item dialog with every item-master v2 field. Saves through <see cref="IMedicineService"/>,
/// which validates (MedicineService.Validate), links the composition and writes the audit log.
/// </summary>
public partial class StockItemEditorViewModel : StockDialogViewModel
{
    private readonly IMedicineService _medicines;
    private readonly ISupplierRepository _suppliers;
    private Medicine? _source;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _composition = string.Empty;
    [ObservableProperty] private string _manufacturer = string.Empty;
    [ObservableProperty] private string _brand = string.Empty;
    [ObservableProperty] private string _category = string.Empty;
    [ObservableProperty] private DosageForm _dosageForm = DosageForm.Tablet;
    [ObservableProperty] private string _strength = string.Empty;
    [ObservableProperty] private string _packLabel = string.Empty;
    [ObservableProperty] private int _unitsPerPack = 1;
    [ObservableProperty] private bool _allowLooseSale = true;
    [ObservableProperty] private string _barcode = string.Empty;
    [ObservableProperty] private string _sku = string.Empty;
    [ObservableProperty] private string _hsnCode = string.Empty;
    [ObservableProperty] private decimal _gstRatePercent = 12m;
    [ObservableProperty] private DrugSchedule _schedule = DrugSchedule.None;
    [ObservableProperty] private int _minStock;
    [ObservableProperty] private int _maxStock;
    [ObservableProperty] private decimal? _maxDiscountPercent;
    [ObservableProperty] private string _unit = string.Empty;
    [ObservableProperty] private string _rackLocation = string.Empty;
    [ObservableProperty] private decimal _defaultMrp;
    [ObservableProperty] private decimal _defaultSalePrice;
    [ObservableProperty] private Supplier? _defaultSupplier;
    [ObservableProperty] private MedicineStatus _status = MedicineStatus.Active;
    [ObservableProperty] private string _notes = string.Empty;

    public ObservableCollection<Supplier> Suppliers { get; } = new();
    public IReadOnlyList<DosageForm> DosageForms { get; } = Enum.GetValues<DosageForm>();
    public IReadOnlyList<MedicineStatus> Statuses { get; } = Enum.GetValues<MedicineStatus>();
    public IReadOnlyList<decimal> GstSlabs { get; } = new[] { 0m, 5m, 12m, 18m, 28m };
    public IReadOnlyList<StockOption<DrugSchedule>> Schedules { get; } =
        Enum.GetValues<DrugSchedule>().Select(s => new StockOption<DrugSchedule>(s, s == DrugSchedule.None ? "OTC (-)" : s.ToLabel())).ToList();

    public bool IsEdit => _source != null;

    public StockItemEditorViewModel(IMedicineService medicines, ISupplierRepository suppliers)
    {
        _medicines = medicines;
        _suppliers = suppliers;
    }

    /// <summary>Opens the dialog for a new item, or for editing <paramref name="medicineId"/>.</summary>
    public async Task OpenAsync(long? medicineId = null)
    {
        ErrorMessage = null;
        await LoadSuppliersAsync();
        _source = medicineId is { } id ? await _medicines.GetByIdAsync(id) : null;
        var m = _source ?? new Medicine { GstRatePercent = 12m, MinStock = 0, Category = "Tablets" };
        Title = _source == null ? "Add New Item" : $"Edit Item — {m.Name}";
        OnPropertyChanged(nameof(IsEdit));

        Name = m.Name;
        Composition = m.Composition ?? string.Empty;
        Manufacturer = m.Manufacturer ?? string.Empty;
        Brand = m.Brand ?? string.Empty;
        Category = m.Category ?? string.Empty;
        DosageForm = m.DosageForm;
        Strength = m.Strength ?? string.Empty;
        PackLabel = m.PackLabel ?? string.Empty;
        UnitsPerPack = Math.Max(m.UnitsPerPack, 1);
        AllowLooseSale = m.AllowLooseSale;
        Barcode = m.Barcode ?? string.Empty;
        Sku = m.Sku ?? string.Empty;
        HsnCode = m.HsnCode ?? string.Empty;
        GstRatePercent = m.GstRatePercent;
        Schedule = m.Schedule;
        MinStock = m.MinStock;
        MaxStock = m.MaxStock;
        MaxDiscountPercent = m.MaxDiscountPercent;
        Unit = m.Unit ?? string.Empty;
        RackLocation = m.RackLocation ?? string.Empty;
        DefaultMrp = m.DefaultMrp;
        DefaultSalePrice = m.DefaultSellingPrice;
        DefaultSupplier = Suppliers.FirstOrDefault(s => s.Id == m.DefaultSupplierId);
        Status = m.Status;
        Notes = m.Notes ?? string.Empty;
        IsOpen = true;
    }

    private async Task LoadSuppliersAsync()
    {
        if (Suppliers.Count > 0) return;
        foreach (var s in (await _suppliers.GetAllAsync()).Where(s => s.IsActive).OrderBy(s => s.Name))
            Suppliers.Add(s);
    }

    /// <summary>Typing "15's" in the pack label fills units per pack when it is still 1.</summary>
    partial void OnPackLabelChanged(string value)
    {
        if (UnitsPerPack <= 1)
            UnitsPerPack = Core.Modules.Stock.StockCsv.UnitsFromPackLabel(value);
    }

    protected override async Task<long> SaveCoreAsync()
    {
        var medicine = new Medicine
        {
            Id = _source?.Id ?? 0,
            Name = Name.Trim(),
            Composition = Clean(Composition),
            Manufacturer = Clean(Manufacturer),
            Brand = Clean(Brand),
            Category = Clean(Category),
            DosageForm = DosageForm,
            Strength = Clean(Strength),
            PackLabel = Clean(PackLabel) ?? $"{UnitsPerPack}'s",
            UnitsPerPack = UnitsPerPack,
            AllowLooseSale = AllowLooseSale,
            Barcode = Clean(Barcode),
            Sku = Clean(Sku),
            HsnCode = Clean(HsnCode),
            GstRatePercent = GstRatePercent,
            Schedule = Schedule,
            MinStock = MinStock,
            MaxStock = MaxStock,
            MaxDiscountPercent = MaxDiscountPercent,
            Unit = Clean(Unit),
            RackLocation = Clean(RackLocation),
            DefaultMrpPaisa = ToPaisa(DefaultMrp),
            DefaultSellingPricePaisa = ToPaisa(DefaultSalePrice),
            DefaultSupplierId = DefaultSupplier?.Id,
            Status = Status,
            Notes = Clean(Notes),
            CreatedAt = _source?.CreatedAt ?? DateTime.UtcNow
        };

        if (_source == null)
            return await _medicines.CreateAsync(medicine);

        if (!await _medicines.UpdateAsync(medicine))
            throw new Core.Modules.Stock.StockValidationException("The item no longer exists.");
        return medicine.Id;
    }
}
