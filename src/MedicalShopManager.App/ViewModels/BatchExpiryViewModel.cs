using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.App.ViewModels;

/// <summary>
/// Batch and expiry tracking ViewModel with status badges, FEFO monitoring, and stock adjustment dialogs.
/// </summary>
public partial class BatchExpiryViewModel : BaseViewModel
{
    private readonly IInventoryService _inventoryService;

    [ObservableProperty] private ObservableCollection<MedicineBatch> _batches = new();
    [ObservableProperty] private MedicineBatch? _selectedBatch;
    [ObservableProperty] private string _selectedFilter = "All"; // "All", "NearExpiry", "Expired"

    [ObservableProperty] private int _totalBatchesCount;
    [ObservableProperty] private int _nearExpiryCount;
    [ObservableProperty] private int _expiredCount;

    // Adjustment Modal
    [ObservableProperty] private bool _isAdjustModalOpen;
    [ObservableProperty] private int _adjustNewQuantity;
    [ObservableProperty] private string _adjustReason = string.Empty;

    // Write-off Modal
    [ObservableProperty] private bool _isWriteOffModalOpen;
    [ObservableProperty] private int _writeOffQuantity = 1;
    [ObservableProperty] private MovementType _writeOffType = MovementType.Expired;
    [ObservableProperty] private string _writeOffReason = "Stock expired";

    public IReadOnlyList<string> FilterOptions { get; } = new[] { "All", "NearExpiry", "Expired" };

    public IReadOnlyList<MovementType> WriteOffTypes { get; } = new[]
    {
        MovementType.Expired,
        MovementType.Damaged,
        MovementType.WriteOff
    };

    public BatchExpiryViewModel(IInventoryService inventoryService)
    {
        _inventoryService = inventoryService;
    }

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            var nearExpiry = await _inventoryService.GetNearExpiryBatchesAsync(90);
            var expired = await _inventoryService.GetExpiredBatchesAsync();

            NearExpiryCount = nearExpiry.Count;
            ExpiredCount = expired.Count;

            await ApplyFilterAsync();
        }
        catch (Exception ex)
        {
            SetError($"Failed to load batches: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ApplyFilterAsync()
    {
        IsLoading = true;
        try
        {
            IReadOnlyList<MedicineBatch> list;
            if (SelectedFilter == "NearExpiry")
            {
                list = await _inventoryService.GetNearExpiryBatchesAsync(90);
            }
            else if (SelectedFilter == "Expired")
            {
                list = await _inventoryService.GetExpiredBatchesAsync();
            }
            else
            {
                // Fetch near expiry + expired or all batches
                var near = await _inventoryService.GetNearExpiryBatchesAsync(90);
                var exp = await _inventoryService.GetExpiredBatchesAsync();
                list = near.Concat(exp).DistinctBy(b => b.Id).OrderBy(b => b.ExpiryDate).ToList();
            }

            Batches.Clear();
            foreach (var b in list)
                Batches.Add(b);

            TotalBatchesCount = Batches.Count;
        }
        catch (Exception ex)
        {
            SetError($"Error applying filter: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenAdjustModal(MedicineBatch? batch)
    {
        if (batch == null) return;
        SelectedBatch = batch;
        AdjustNewQuantity = batch.QuantityRemaining;
        AdjustReason = string.Empty;
        IsAdjustModalOpen = true;
    }

    [RelayCommand]
    private void CloseAdjustModal() => IsAdjustModalOpen = false;

    [RelayCommand]
    private async Task SaveAdjustmentAsync()
    {
        if (SelectedBatch == null) return;
        if (string.IsNullOrWhiteSpace(AdjustReason))
        {
            SetError("Reason is required for stock adjustment.");
            return;
        }

        IsLoading = true;
        try
        {
            await _inventoryService.AdjustStockAsync(SelectedBatch.Id, AdjustNewQuantity, AdjustReason.Trim(), userId: 1);
            SetStatus($"Batch {SelectedBatch.BatchNumber} quantity updated to {AdjustNewQuantity}.");
            IsAdjustModalOpen = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetError($"Failed to adjust stock: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenWriteOffModal(MedicineBatch? batch)
    {
        if (batch == null) return;
        SelectedBatch = batch;
        WriteOffQuantity = Math.Min(batch.QuantityRemaining, 1);
        WriteOffType = batch.IsExpired ? MovementType.Expired : MovementType.Damaged;
        WriteOffReason = batch.IsExpired ? "Expired stock write-off" : "Damaged stock write-off";
        IsWriteOffModalOpen = true;
    }

    [RelayCommand]
    private void CloseWriteOffModal() => IsWriteOffModalOpen = false;

    [RelayCommand]
    private async Task SaveWriteOffAsync()
    {
        if (SelectedBatch == null) return;
        if (WriteOffQuantity <= 0)
        {
            SetError("Write-off quantity must be greater than zero.");
            return;
        }

        IsLoading = true;
        try
        {
            await _inventoryService.WriteOffStockAsync(SelectedBatch.Id, WriteOffQuantity, WriteOffType, WriteOffReason.Trim(), userId: 1);
            SetStatus($"Written off {WriteOffQuantity} units from batch {SelectedBatch.BatchNumber}.");
            IsWriteOffModalOpen = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetError($"Failed to write off stock: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
