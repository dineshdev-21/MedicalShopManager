using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.Core.Modules.Billing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>
/// Batch selection popup (spec 02 §G): batches of the picked product in the configured order (FEFO default), with
/// expiry hint, MRP, margin and stock. The default selection is the first sellable batch; expired or cut-off batches
/// are listed (red) but cannot be picked. Enter picks, Esc closes.
/// </summary>
public sealed partial class BillingBatchPickerViewModel : ObservableObject
{
    public event EventHandler<SaleBatchOption>? Picked;
    public event EventHandler? Closed;

    [ObservableProperty] private SaleCatalogItem? _item;
    [ObservableProperty] private SaleBatchOption? _selectedBatch;
    [ObservableProperty] private string? _message;

    public ObservableCollection<SaleBatchOption> Batches { get; } = new();

    public string Title => Item is null ? "Select Batch" : $"Select Batch: {Item.Name}";
    public bool HasBatches => Batches.Count > 0;

    public void Load(SaleCatalogItem item, IReadOnlyList<SaleBatchOption> batches, long? currentBatchId = null)
    {
        Item = item;
        Batches.Clear();
        foreach (var batch in batches) Batches.Add(batch);
        SelectedBatch = Batches.FirstOrDefault(b => b.BatchId == currentBatchId)
                        ?? Batches.FirstOrDefault(b => b.IsSellable)
                        ?? Batches.FirstOrDefault();
        Message = Batches.Count == 0
            ? "No batches for this item. Add stock through a purchase or opening stock."
            : Batches.Any(b => b.IsSellable) ? null : "No sellable batch: every batch is expired, inside the expiry cut-off or out of stock.";
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasBatches));
    }

    [RelayCommand]
    private void Pick(SaleBatchOption? batch)
    {
        batch ??= SelectedBatch;
        if (batch is null) return;
        if (batch.IsExpired)
        {
            Message = $"Batch {batch.BatchNumber} expired ({batch.ExpiryLabel}) and cannot be sold.";
            return;
        }
        if (batch.BlockedByCutoff)
        {
            Message = $"Batch {batch.BatchNumber} expires {batch.ExpiryLabel}, inside the expiry cut-off set in Sale Settings.";
            return;
        }
        Picked?.Invoke(this, batch);
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);
}
