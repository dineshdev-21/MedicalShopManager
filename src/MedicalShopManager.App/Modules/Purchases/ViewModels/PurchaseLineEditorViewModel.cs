using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>
/// The single entry row of the purchase grid (spec 04 §1b): PRODUCT, HSN, BATCH, EXPIRY MM/YY, PACK, QTY, FREE (packs),
/// MRP, RATE (per pack), SCHEME X+Y, SCH %, DISC %, GST %, SALE RATE, with NET RATE, MARGIN and AMT computed live.
/// Rupee and pack values are what the user types; <see cref="TryBuildLine"/> turns them into a paisa/unit line.
/// </summary>
public sealed partial class PurchaseLineEditorViewModel : ObservableObject
{
    private static readonly HashSet<string> PreviewInputs = new()
    {
        nameof(UnitsPerPack), nameof(QuantityPacks), nameof(FreePacks), nameof(Mrp), nameof(Rate), nameof(DiscountPercent),
        nameof(SchemePercent), nameof(GstPercent), nameof(SaleRate)
    };

    private bool _saleRateEdited;
    private bool _freeEdited;
    private bool _applying;

    public PurchaseLineEditorViewModel() => PropertyChanged += OnEdited;

    /// <summary>Header values the preview depends on (GST column, inclusive rates, bill discount, IGST).</summary>
    public PurchaseBill Context { get; set; } = new();

    /// <summary>Sale discount % setting used to default the sale rate from MRP.</summary>
    public decimal DefaultSaleDiscountPercent { get; set; }

    /// <summary>Row index being edited (-1 = adding a new row).</summary>
    [ObservableProperty] private int _editingIndex = -1;

    [ObservableProperty] private long _medicineId;
    [ObservableProperty] private string _medicineName = string.Empty;
    [ObservableProperty] private string? _hsnCode;
    [ObservableProperty] private string _batchNumber = string.Empty;
    [ObservableProperty] private int? _expiryMonth;
    [ObservableProperty] private int? _expiryYear;
    [ObservableProperty] private int _unitsPerPack = 1;
    [ObservableProperty] private decimal _quantityPacks;
    [ObservableProperty] private decimal _freePacks;
    [ObservableProperty] private decimal _mrp;
    [ObservableProperty] private decimal _rate;
    [ObservableProperty] private decimal _ptr;
    [ObservableProperty] private int _schemeQuantity;
    [ObservableProperty] private int _schemeFreeQuantity;
    [ObservableProperty] private decimal _schemePercent;
    [ObservableProperty] private decimal _discountPercent;
    [ObservableProperty] private decimal _gstPercent;
    [ObservableProperty] private decimal _saleRate;

    // ── Preview (computed) ──
    [ObservableProperty] private decimal _netRate;
    [ObservableProperty] private decimal _marginPercent;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private string? _lastPurchaseHint;

    public bool HasProduct => MedicineId > 0;
    public bool IsEditingExisting => EditingIndex >= 0;
    public string AddButtonText => IsEditingExisting ? "Update row" : "Add (Enter)";

    partial void OnMedicineIdChanged(long value) => OnPropertyChanged(nameof(HasProduct));

    partial void OnEditingIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsEditingExisting));
        OnPropertyChanged(nameof(AddButtonText));
    }

    /// <summary>Starts a new row for an item from <see cref="IPurchaseEntryService.CreateLineAsync"/> (pack, HSN, GST, last rates).</summary>
    public void Start(PurchaseBillLine template)
    {
        ArgumentNullException.ThrowIfNull(template);
        Load(template, -1);
        QuantityPacks = 0;
        FreePacks = 0;
        BatchNumber = string.Empty;
        ExpiryMonth = null;
        ExpiryYear = null;
        LastPurchaseHint = template.EnteredRatePaisa > 0
            ? $"Last purchase: rate ₹{PurchaseUi.Rupees(template.EnteredRatePaisa):N2}, MRP ₹{PurchaseUi.Rupees(template.MrpPaisa):N2}, pack 1x{template.UnitsPerPack}"
            : null;
    }

    /// <summary>Loads a grid row back into the editor (Edit).</summary>
    public void Load(PurchaseBillLine line, int index)
    {
        _applying = true;
        try
        {
            EditingIndex = index;
            MedicineId = line.MedicineId;
            MedicineName = line.MedicineName;
            HsnCode = line.HsnCode;
            BatchNumber = line.BatchNumber;
            ExpiryMonth = line.ExpiryDate == default ? null : line.ExpiryDate.Month;
            ExpiryYear = line.ExpiryDate == default ? null : line.ExpiryDate.Year % 100;
            UnitsPerPack = Math.Max(1, line.UnitsPerPack);
            QuantityPacks = PurchaseCalculator.PacksFromUnits(line.Quantity, UnitsPerPack);
            FreePacks = PurchaseCalculator.PacksFromUnits(line.FreeQuantity, UnitsPerPack);
            Mrp = PurchaseUi.Rupees(line.MrpPaisa);
            Rate = PurchaseUi.Rupees(line.EnteredRatePaisa);
            Ptr = PurchaseUi.Rupees(line.PtrPaisa);
            SchemeQuantity = line.SchemeQuantity;
            SchemeFreeQuantity = line.SchemeFreeQuantity;
            SchemePercent = line.SchemePercent;
            DiscountPercent = line.DiscountPercent;
            GstPercent = line.GstRatePercent;
            SaleRate = PurchaseUi.Rupees(line.SaleRatePaisa);
            LastPurchaseHint = null;
            _saleRateEdited = line.SaleRatePaisa > 0 && line.SaleRatePaisa != PurchaseCalculator.DefaultSaleRate(line.MrpPaisa, DefaultSaleDiscountPercent);
            _freeEdited = line.FreeQuantity > 0;
        }
        finally
        {
            _applying = false;
        }
        UpdatePreview();
    }

    public void Clear()
    {
        _applying = true;
        try
        {
            EditingIndex = -1;
            MedicineId = 0;
            MedicineName = string.Empty;
            HsnCode = null;
            BatchNumber = string.Empty;
            ExpiryMonth = null;
            ExpiryYear = null;
            UnitsPerPack = 1;
            QuantityPacks = 0;
            FreePacks = 0;
            Mrp = 0;
            Rate = 0;
            Ptr = 0;
            SchemeQuantity = 0;
            SchemeFreeQuantity = 0;
            SchemePercent = 0;
            DiscountPercent = 0;
            GstPercent = 0;
            SaleRate = 0;
            LastPurchaseHint = null;
            _saleRateEdited = false;
            _freeEdited = false;
        }
        finally
        {
            _applying = false;
        }
        UpdatePreview();
    }

    /// <summary>
    /// Builds the line, or returns the problems that stop it (expiry, whole units). Business rules (MRP, rates,
    /// expired batch …) are checked by <see cref="PurchaseValidator"/> when the bill is saved.
    /// </summary>
    public (PurchaseBillLine? Line, IReadOnlyList<string> Errors) TryBuildLine()
    {
        var errors = new List<string>();
        if (MedicineId <= 0) errors.Add("Select a product first (type its name or scan the barcode).");
        if (string.IsNullOrWhiteSpace(BatchNumber)) errors.Add("Please fill Batch No.");
        DateTime expiry = default;
        if (ExpiryMonth is not (>= 1 and <= 12) || ExpiryYear is not (>= 0 and <= 99 or >= 2000 and <= 2099))
            errors.Add("Please fill Expiry as MM / YY (e.g. 11 / 27).");
        else expiry = MedicineBatch.ExpiryFromMonthYear(ExpiryMonth.Value, ExpiryYear.Value);
        if (UnitsPerPack < 1) errors.Add("Pack must be at least 1 (units per pack).");

        var pack = Math.Max(1, UnitsPerPack);
        var units = PurchaseCalculator.UnitsFromPacks(QuantityPacks, pack);
        var free = PurchaseCalculator.UnitsFromPacks(FreePacks, pack);
        if (units is null) errors.Add($"Qty {QuantityPacks:0.##} packs of 1x{pack} is not a whole number of units.");
        if (free is null) errors.Add($"Free {FreePacks:0.##} packs of 1x{pack} is not a whole number of units.");
        if (errors.Count > 0) return (null, errors);

        return (new PurchaseBillLine
        {
            MedicineId = MedicineId,
            MedicineName = MedicineName,
            HsnCode = string.IsNullOrWhiteSpace(HsnCode) ? null : HsnCode.Trim(),
            BatchNumber = BatchNumber.Trim().ToUpperInvariant(),
            ExpiryDate = expiry,
            UnitsPerPack = pack,
            Quantity = units!.Value,
            FreeQuantity = free!.Value,
            MrpPaisa = PurchaseUi.Paisa(Mrp),
            EnteredRatePaisa = PurchaseUi.Paisa(Rate),
            PtrPaisa = PurchaseUi.Paisa(Ptr),
            SaleRatePaisa = PurchaseUi.Paisa(SaleRate),
            SchemeQuantity = SchemeQuantity,
            SchemeFreeQuantity = SchemeFreeQuantity,
            SchemePercent = SchemePercent,
            DiscountPercent = DiscountPercent,
            GstRatePercent = GstPercent
        }, errors);
    }

    private void OnEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        switch (e.PropertyName)
        {
            case nameof(SaleRate):
                _saleRateEdited = true;
                break;
            case nameof(FreePacks):
                _freeEdited = true;
                break;
            case nameof(Mrp) when !_saleRateEdited:
                SetQuietly(() => SaleRate = PurchaseUi.Rupees(PurchaseCalculator.DefaultSaleRate(PurchaseUi.Paisa(Mrp), DefaultSaleDiscountPercent)));
                break;
            case nameof(SchemeQuantity) or nameof(SchemeFreeQuantity) or nameof(QuantityPacks) when !_freeEdited && SchemeQuantity > 0:
                SetQuietly(() =>
                {
                    // Whole free units only (half a scheme of a 15's strip gives 7 units).
                    var pack = Math.Max(1, UnitsPerPack);
                    var freeUnits = PurchaseCalculator.FreeUnitsFromPacks(
                        PurchaseCalculator.FreePacksFromScheme(QuantityPacks, SchemeQuantity, SchemeFreeQuantity), pack);
                    FreePacks = PurchaseCalculator.PacksFromUnits(freeUnits, pack);
                });
                break;
        }
        if (e.PropertyName != null && PreviewInputs.Contains(e.PropertyName)) UpdatePreview();
    }

    private void SetQuietly(Action set)
    {
        _applying = true;
        try { set(); }
        finally { _applying = false; }
    }

    /// <summary>NET RATE, MARGIN and AMT for the row as typed (zeros while the row is incomplete).</summary>
    public void UpdatePreview()
    {
        var (line, errors) = TryBuildLineForPreview();
        if (line == null || errors > 0)
        {
            NetRate = 0;
            MarginPercent = 0;
            Amount = 0;
            return;
        }
        try
        {
            var result = PurchaseCalculator.CalculateLine(line, Context);
            NetRate = PurchaseUi.Rupees(result.NetRatePaisa);
            MarginPercent = Math.Round(result.MarginPercent, 2);
            Amount = PurchaseUi.Rupees(result.TotalPaisa);
        }
        catch (ArgumentException)
        {
            NetRate = 0;
            MarginPercent = 0;
            Amount = 0;
        }
    }

    private (PurchaseBillLine? Line, int Errors) TryBuildLineForPreview()
    {
        var pack = Math.Max(1, UnitsPerPack);
        var units = PurchaseCalculator.UnitsFromPacks(QuantityPacks, pack);
        var free = PurchaseCalculator.UnitsFromPacks(FreePacks, pack);
        if (units is null || free is null) return (null, 1);
        if (DiscountPercent is < 0 or > 100 || SchemePercent is < 0 or > 100 || GstPercent is < 0 or > 28 || Rate < 0) return (null, 1);
        return (new PurchaseBillLine
        {
            UnitsPerPack = pack,
            Quantity = units.Value,
            FreeQuantity = free.Value,
            EnteredRatePaisa = PurchaseUi.Paisa(Rate),
            MrpPaisa = PurchaseUi.Paisa(Mrp),
            SaleRatePaisa = PurchaseUi.Paisa(SaleRate),
            DiscountPercent = DiscountPercent,
            SchemePercent = SchemePercent,
            GstRatePercent = GstPercent
        }, 0);
    }
}
