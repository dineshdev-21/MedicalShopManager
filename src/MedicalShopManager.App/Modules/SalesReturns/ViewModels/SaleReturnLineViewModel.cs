using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Modules.SalesReturns;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.SalesReturns.ViewModels;

/// <summary>
/// One row of the return screen: a bill line (with what can still come back) or a batch returned without a bill
/// (with the refund rate and discount the user enters). The user types packs + loose units and can mark it damaged.
/// </summary>
public sealed partial class SaleReturnLineViewModel : ObservableObject
{
    private SaleReturnLineViewModel(long? saleItemId, long medicineId, string medicineName, long batchId, string batchNumber,
        DateTime expiry, int unitsPerPack, long maxUnits, DateTime returnDate)
    {
        SaleItemId = saleItemId;
        MedicineId = medicineId;
        MedicineName = medicineName;
        BatchId = batchId;
        BatchNumber = batchNumber;
        ExpiryDate = expiry;
        UnitsPerPack = Math.Max(unitsPerPack, 1);
        MaxUnits = maxUnits;
        _returnDate = returnDate;
    }

    /// <summary>A line of the original bill.</summary>
    public static SaleReturnLineViewModel ForBillLine(ReturnableSaleLine line, DateTime returnDate)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new SaleReturnLineViewModel(line.SaleItemId, line.MedicineId, line.MedicineName, line.BatchId, line.BatchNumber,
            line.ExpiryDate, line.UnitsPerPack, line.ReturnableUnits, returnDate)
        {
            SoldDisplay = line.SoldDisplay,
            ReturnedDisplay = line.ReturnedDisplay,
            RateDisplay = line.RatePerPackPaisa / 100m,
            DiscountPercent = line.DiscountPercent,
            GstPercent = line.GstPercent
        };
    }

    /// <summary>A batch returned without a bill, priced at <paramref name="ratePaisa"/> per pack.</summary>
    public static SaleReturnLineViewModel ForBatch(SaleReturnBatchInfo batch, long units, long ratePaisa, decimal discountPercent,
        DateTime returnDate)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var unitsPerPack = Math.Max(batch.UnitsPerPack, 1);
        var q = PackQuantity.FromUnits(Math.Max(units, 0), unitsPerPack);
        return new SaleReturnLineViewModel(null, batch.MedicineId, batch.MedicineName, batch.BatchId, batch.BatchNumber,
            batch.ExpiryDate, unitsPerPack, long.MaxValue, returnDate)
        {
            IsWalkIn = true,
            MrpPaisa = batch.MrpPaisa,
            RateDisplay = ratePaisa / 100m,
            DiscountPercent = discountPercent,
            GstPercent = batch.GstPercent,
            Packs = q.Packs,
            Loose = q.Loose
        };
    }

    public long? SaleItemId { get; }
    public long MedicineId { get; }
    public string MedicineName { get; }
    public long BatchId { get; }
    public string BatchNumber { get; }
    public DateTime ExpiryDate { get; }
    public int UnitsPerPack { get; }
    /// <summary>Most units that can come back (bill lines); unlimited for returns without a bill.</summary>
    public long MaxUnits { get; }
    public bool IsWalkIn { get; private init; }
    public long MrpPaisa { get; private init; }
    public decimal GstPercent { get; private init; }
    public string SoldDisplay { get; private init; } = "";
    public string ReturnedDisplay { get; private init; } = "";

    [ObservableProperty] private long _packs;
    [ObservableProperty] private long _loose;
    [ObservableProperty] private bool _isDamaged;
    /// <summary>Rate per pack in rupees (editable for returns without a bill; the bill rate otherwise).</summary>
    [ObservableProperty] private decimal _rateDisplay;
    [ObservableProperty] private decimal _discountPercent;
    /// <summary>Refund value of the line (set by the screen after each recalculation).</summary>
    [ObservableProperty] private long _amountPaisa;

    private DateTime _returnDate;

    public string Pack => $"1 x {UnitsPerPack}";
    public string ExpiryLabel => ExpiryDate == default ? "" : ExpiryDate.ToString("MM/yy");
    public string ReturnableDisplay => IsWalkIn ? "" : PackQuantity.Format(MaxUnits, UnitsPerPack);
    public bool IsFullyReturned => !IsWalkIn && MaxUnits == 0;
    public bool CanEdit => !IsFullyReturned;
    public bool IsExpired => SaleReturnCalculator.IsExpired(ExpiryDate, _returnDate);

    /// <summary>Units entered (packs × pack size + loose); −1 when a negative number was typed.</summary>
    public long Units => Packs < 0 || Loose < 0 ? -1 : new PackQuantity(Packs, Loose).ToUnits(UnitsPerPack);
    public bool HasQuantity => Units != 0;

    /// <summary>Where the units will go: restock, expired stock or not restocked.</summary>
    public string DispositionLabel => SaleReturnCalculator.ResolveDisposition(
        IsDamaged ? SaleReturnDisposition.Damaged : SaleReturnDisposition.Restock, ExpiryDate, _returnDate).ToLabel();

    partial void OnPacksChanged(long value) => QuantityChanged();
    partial void OnLooseChanged(long value) => QuantityChanged();
    partial void OnIsDamagedChanged(bool value) => OnPropertyChanged(nameof(DispositionLabel));

    private void QuantityChanged()
    {
        OnPropertyChanged(nameof(Units));
        OnPropertyChanged(nameof(HasQuantity));
    }

    /// <summary>Re-evaluates expiry (and so the disposition) for a changed return date.</summary>
    public void SetReturnDate(DateTime returnDate)
    {
        _returnDate = returnDate;
        OnPropertyChanged(nameof(IsExpired));
        OnPropertyChanged(nameof(DispositionLabel));
    }

    public void ReturnAll()
    {
        if (IsWalkIn || MaxUnits <= 0) return;
        var q = PackQuantity.FromUnits(MaxUnits, UnitsPerPack);
        Packs = q.Packs;
        Loose = q.Loose;
    }

    public void Clear()
    {
        Packs = 0;
        Loose = 0;
    }

    public SaleReturnLineRequest ToRequest() => new()
    {
        SaleItemId = SaleItemId,
        MedicineId = MedicineId,
        BatchId = BatchId,
        Units = Units,
        Disposition = IsDamaged ? SaleReturnDisposition.Damaged : SaleReturnDisposition.Restock,
        RatePerPackPaisa = IsWalkIn ? SalesReturnsUi.Paisa(RateDisplay) : 0,
        DiscountPercent = IsWalkIn ? DiscountPercent : 0m
    };
}
