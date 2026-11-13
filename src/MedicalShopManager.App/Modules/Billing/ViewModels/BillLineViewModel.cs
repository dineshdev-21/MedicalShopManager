using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>
/// One row of the POS grid (spec 02 §H). Holds the immutable <see cref="BillLine"/> plus the editable cells (PACKS,
/// LOOSE, FREE, RATE, DISC %); amounts come from <see cref="BillingCalculator.ComputeBill"/> via <see cref="ApplyComputed"/>,
/// never from arithmetic here. RATE shows the discounted per-pack rate; typing a rate sets DISC % relative to MRP.
/// </summary>
public sealed partial class BillLineViewModel : ObservableObject
{
    private BillLine _line;

    public BillLineViewModel(BillLine line)
    {
        _line = line;
        _packs = line.Packs;
        _loose = line.Loose;
        _freeUnits = line.FreeUnits;
        _discountPercent = line.DiscountPercent;
    }

    /// <summary>Raised when a cell the bill depends on changes (the page recomputes and auto-saves the draft).</summary>
    public event EventHandler? Changed;

    [ObservableProperty] private int _rowNumber;
    [ObservableProperty] private bool _isReadOnly;
    [ObservableProperty] private string? _rowError;

    [ObservableProperty] private long _packs;
    [ObservableProperty] private long _loose;
    [ObservableProperty] private long _freeUnits;
    [ObservableProperty] private decimal _discountPercent;

    [ObservableProperty] private long _amountPaisa;
    [ObservableProperty] private decimal _effectiveDiscountPercent;
    [ObservableProperty] private decimal _marginPercent;
    [ObservableProperty] private long _discountedRatePaisa;

    /// <summary>Expiry state on the bill date (EXPIRED red / EXPIRING orange legend).</summary>
    [ObservableProperty] private BatchExpiryState _expiryState;

    /// <summary>The line as the services see it (current cell values applied).</summary>
    public BillLine Line => _line with { Packs = Packs, Loose = Loose, FreeUnits = FreeUnits, DiscountPercent = DiscountPercent };

    public string MedicineName => _line.MedicineName;
    public string? Manufacturer => _line.Manufacturer;
    public string ScheduleLabel => _line.Schedule.ToLabel();
    public bool IsScheduled => _line.Schedule.RequiresPrescription();
    public bool NeedsRegister => _line.Schedule.RequiresRegisterEntry();
    public string? RackLocation => _line.RackLocation;
    public string BatchNumber => _line.BatchNumber;
    public long BatchId => _line.BatchId;
    public long MedicineId => _line.MedicineId;
    public string? HsnCode => _line.HsnCode;
    public string PackDisplay => $"1 x {Math.Max(_line.UnitsPerPack, 1)}";
    public string ExpiryLabel => _line.ExpiryDate.ToString("MM/yy");
    public decimal Mrp => _line.MrpPerPackPaisa / 100m;
    public decimal GstPercent => _line.GstPercent;
    public bool AllowLoose => _line.AllowLooseSale && _line.UnitsPerPack > 1 && !IsReadOnly;
    public string AvailableDisplay => BillingFormat.Quantity(_line.AvailableUnits, _line.UnitsPerPack);
    public string QuantityDisplay => BillingFormat.Quantity(SafeUnits(), _line.UnitsPerPack) + (FreeUnits > 0 ? $" +{FreeUnits}" : string.Empty);
    public decimal Amount => AmountPaisa / 100m;
    /// <summary>Units leaving stock (paid + free), 0 while a cell holds a negative number.</summary>
    public long StockUnitsSafe => SafeUnits() + Math.Max(FreeUnits, 0);

    /// <summary>Shows a saved line's stored figures (view mode; nothing is recomputed).</summary>
    public void ApplyStored(long amountPaisa, decimal discountPercent)
    {
        AmountPaisa = amountPaisa;
        EffectiveDiscountPercent = discountPercent;
        OnPropertyChanged(nameof(Amount));
    }

    /// <summary>RATE ₹ cell: the discounted per-pack rate. Setting it derives DISC % from MRP (Localwell behaviour).</summary>
    public decimal Rate
    {
        get => PricingCalculator.RateAfterDiscount(BillingCalculator.BaseRate(_line), Math.Clamp(DiscountPercent, 0m, 100m)) / 100m;
        set
        {
            var paisa = PricingCalculator.RoundPaisa(Math.Max(value, 0m) * 100m);
            DiscountPercent = BillingCalculator.DiscountFromEnteredRate(_line, paisa);
            OnPropertyChanged();
        }
    }

    /// <summary>Replaces the underlying line (another batch, a refreshed stock figure), optionally keeping the typed cells.</summary>
    public void ReplaceLine(BillLine line, bool keepQuantities = true)
    {
        _line = line;
        if (!keepQuantities)
        {
            Packs = line.Packs;
            Loose = line.Loose;
            FreeUnits = line.FreeUnits;
            DiscountPercent = line.DiscountPercent;
        }
        OnPropertyChanged(string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies the calculator's figures for this row.</summary>
    public void ApplyComputed(ComputedBillLine computed)
    {
        AmountPaisa = computed.Amounts.TotalPaisa;
        EffectiveDiscountPercent = computed.EffectiveDiscountPercent;
        MarginPercent = computed.MarginPercent;
        DiscountedRatePaisa = computed.Amounts.DiscountedRatePerPackPaisa;
        OnPropertyChanged(nameof(Amount));
        OnPropertyChanged(nameof(QuantityDisplay));
    }

    partial void OnPacksChanged(long value) => Touch();
    partial void OnLooseChanged(long value) => Touch();
    partial void OnFreeUnitsChanged(long value) => Touch();

    partial void OnDiscountPercentChanged(decimal value)
    {
        OnPropertyChanged(nameof(Rate));
        Touch();
    }

    partial void OnIsReadOnlyChanged(bool value) => OnPropertyChanged(nameof(AllowLoose));

    private void Touch()
    {
        OnPropertyChanged(nameof(QuantityDisplay));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private long SafeUnits() => Packs < 0 || Loose < 0 ? 0 : Packs * Math.Max(_line.UnitsPerPack, 1) + Loose;
}
