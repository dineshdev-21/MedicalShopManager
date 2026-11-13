using CommunityToolkit.Mvvm.ComponentModel;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>A batch that can go back to the distributor, with the quantity (packs + loose) and rate the user enters.</summary>
public sealed partial class PurchaseReturnCandidateViewModel : ObservableObject
{
    public PurchaseReturnCandidateViewModel(ReturnableBatch batch, int nearExpiryMonths)
    {
        Batch = batch;
        _rate = batch.RatePaisa / 100m;
        IsNearExpiry = !batch.IsExpired && batch.ExpiryDate.Date <= DateTime.Today.AddMonths(Math.Max(0, nearExpiryMonths));
    }

    public ReturnableBatch Batch { get; }

    public string MedicineName => Batch.MedicineName;
    public string BatchNumber => Batch.BatchNumber;
    public string ExpiryLabel => Batch.ExpiryLabel;
    public string Pack => $"1x{Batch.UnitsPerPack}";
    public string StockDisplay => Batch.StockDisplay;
    public string MaxReturnableDisplay => Batch.MaxReturnableDisplay;
    public string PurchaseReference => Batch.PurchaseReference ?? "-";
    public string SupplierName => Batch.SupplierName ?? "-";
    public decimal GstRatePercent => Batch.GstRatePercent;
    public long MrpPaisa => Batch.MrpPaisa;
    public bool IsExpired => Batch.IsExpired;
    public bool IsNearExpiry { get; }

    [ObservableProperty] private int _returnPacks;
    [ObservableProperty] private int _returnLoose;
    [ObservableProperty] private decimal _rate;

    /// <summary>Units entered (packs × pack size + loose), or -1 when negative values were typed.</summary>
    public int ReturnUnits => ReturnPacks < 0 || ReturnLoose < 0 ? -1 : (int)new PackQuantity(ReturnPacks, ReturnLoose).ToUnits(Math.Max(1, Batch.UnitsPerPack));

    public bool IsSelected => ReturnUnits > 0;

    partial void OnReturnPacksChanged(int value) => RaiseQuantityChanged();
    partial void OnReturnLooseChanged(int value) => RaiseQuantityChanged();

    private void RaiseQuantityChanged()
    {
        OnPropertyChanged(nameof(ReturnUnits));
        OnPropertyChanged(nameof(IsSelected));
    }

    /// <summary>Return everything that can still go back.</summary>
    public void ReturnAll()
    {
        var all = PackQuantity.FromUnits(Batch.MaxReturnableUnits, Math.Max(1, Batch.UnitsPerPack));
        ReturnPacks = (int)all.Packs;
        ReturnLoose = (int)all.Loose;
    }

    public void Clear()
    {
        ReturnPacks = 0;
        ReturnLoose = 0;
    }
}
