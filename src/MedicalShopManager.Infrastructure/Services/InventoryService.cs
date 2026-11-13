using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Services;

/// <summary>
/// Inventory and batch management service with FEFO allocation, stock movements, and adjustments.
/// </summary>
public class InventoryService : IInventoryService
{
    private readonly IBatchRepository _batchRepository;
    private readonly IStockMovementRepository _movementRepository;
    private readonly IAuditService _auditService;
    private readonly IExcelSyncQueueRepository _excelSyncQueueRepository;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(
        IBatchRepository batchRepository,
        IStockMovementRepository movementRepository,
        IAuditService auditService,
        IExcelSyncQueueRepository excelSyncQueueRepository,
        ILogger<InventoryService> logger)
    {
        _batchRepository = batchRepository;
        _movementRepository = movementRepository;
        _auditService = auditService;
        _excelSyncQueueRepository = excelSyncQueueRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetBatchesAsync(long medicineId)
    {
        return await _batchRepository.GetByMedicineIdAsync(medicineId);
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetSellableBatchesFefoAsync(long medicineId)
    {
        return await _batchRepository.GetFefoOrderedBatchesAsync(medicineId);
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetNearExpiryBatchesAsync(int daysThreshold = 90)
    {
        return await _batchRepository.GetNearExpiryAsync(daysThreshold);
    }

    public async Task<IReadOnlyList<MedicineBatch>> GetExpiredBatchesAsync()
    {
        return await _batchRepository.GetExpiredAsync();
    }

    /// <summary>
    /// FEFO (First Expiry, First Out) batch stock allocation.
    /// Allocates from the earliest expiring sellable batches first.
    /// Throws InvalidOperationException if insufficient stock.
    /// </summary>
    public async Task<List<(MedicineBatch Batch, int Quantity)>> AllocateStockFefoAsync(long medicineId, int quantityNeeded)
    {
        if (quantityNeeded <= 0)
            throw new ArgumentException("Quantity needed must be greater than zero", nameof(quantityNeeded));

        var sellableBatches = await _batchRepository.GetFefoOrderedBatchesAsync(medicineId);
        var totalAvailable = sellableBatches.Sum(b => b.QuantityRemaining);

        if (totalAvailable < quantityNeeded)
        {
            throw new InvalidOperationException(
                $"Insufficient sellable stock for medicine ID {medicineId}. Requested: {quantityNeeded}, Available: {totalAvailable}");
        }

        var allocations = new List<(MedicineBatch Batch, int Quantity)>();
        var remainingToAllocate = quantityNeeded;

        foreach (var batch in sellableBatches)
        {
            if (remainingToAllocate <= 0)
                break;

            // Extra safeguard: never allocate expired batches
            if (batch.IsExpired || !batch.IsActive || batch.QuantityRemaining <= 0)
                continue;

            var take = Math.Min(batch.QuantityRemaining, remainingToAllocate);
            allocations.Add((batch, take));
            remainingToAllocate -= take;
        }

        if (remainingToAllocate > 0)
        {
            throw new InvalidOperationException(
                $"Could not fulfill full quantity from active sellable batches. Shortfall: {remainingToAllocate}");
        }

        return allocations;
    }

    public async Task AdjustStockAsync(long batchId, int newQuantity, string reason, long userId)
    {
        if (newQuantity < 0)
            throw new ArgumentException("Stock quantity cannot be negative", nameof(newQuantity));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason is required for manual stock adjustment", nameof(reason));

        var batch = await _batchRepository.GetByIdAsync(batchId)
            ?? throw new KeyNotFoundException($"Batch ID {batchId} not found");

        var oldQty = batch.QuantityRemaining;
        var diff = newQuantity - oldQty;

        if (diff == 0)
            return;

        await _batchRepository.UpdateQuantityAsync(batchId, newQuantity);

        // Record stock movement
        var movement = new StockMovement
        {
            MedicineId = batch.MedicineId,
            BatchId = batchId,
            MovementType = MovementType.StockAdjustment,
            Quantity = diff, // signed: positive adds stock, negative removes it
            BalanceAfter = newQuantity,
            ReferenceType = "ManualAdjustment",
            ReferenceId = null,
            Notes = $"{reason} (Old: {oldQty}, New: {newQuantity}, Diff: {(diff > 0 ? "+" : "")}{diff})",
            CreatedByUserId = userId,
            MovementDate = DateTime.UtcNow
        };
        await _movementRepository.InsertAsync(movement);

        await _auditService.LogAsync("AdjustStock", "MedicineBatch", batchId,
            $"{{\"Quantity\":{oldQty}}}", $"{{\"Quantity\":{newQuantity}}}", reason);

        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Batch, batchId);
        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Medicine, batch.MedicineId);

        _logger.LogInformation("Adjusted stock for batch {BatchId}: {OldQty} -> {NewQty} ({Reason})",
            batchId, oldQty, newQuantity, reason);
    }

    public async Task WriteOffStockAsync(long batchId, int quantity, MovementType type, string reason, long userId)
    {
        if (quantity <= 0)
            throw new ArgumentException("Write-off quantity must be greater than zero", nameof(quantity));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason is required for stock write-off", nameof(reason));

        var batch = await _batchRepository.GetByIdAsync(batchId)
            ?? throw new KeyNotFoundException($"Batch ID {batchId} not found");

        if (quantity > batch.QuantityRemaining)
        {
            throw new InvalidOperationException(
                $"Cannot write off {quantity} units. Batch only has {batch.QuantityRemaining} units remaining.");
        }

        var newQty = batch.QuantityRemaining - quantity;
        await _batchRepository.UpdateQuantityAsync(batchId, newQty);

        var movement = new StockMovement
        {
            MedicineId = batch.MedicineId,
            BatchId = batchId,
            MovementType = type,
            Quantity = -quantity,
            BalanceAfter = newQty,
            ReferenceType = "WriteOff",
            Notes = $"{type}: {reason}",
            CreatedByUserId = userId,
            MovementDate = DateTime.UtcNow
        };
        await _movementRepository.InsertAsync(movement);

        await _auditService.LogAsync($"WriteOff_{type}", "MedicineBatch", batchId,
            $"{{\"QuantityRemaining\":{batch.QuantityRemaining}}}",
            $"{{\"QuantityRemaining\":{newQty},\"WrittenOff\":{quantity}}}", reason);

        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Batch, batchId);
        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Medicine, batch.MedicineId);

        _logger.LogInformation("Written off {Qty} units ({Type}) from batch {BatchId}: {Reason}",
            quantity, type, batchId, reason);
    }

    public async Task RecordMovementAsync(StockMovement movement)
    {
        movement.MovementDate = DateTime.UtcNow;
        await _movementRepository.InsertAsync(movement);
    }

    public async Task<IReadOnlyList<StockMovement>> GetMovementsAsync(long medicineId)
    {
        return await _movementRepository.GetByMedicineAsync(medicineId);
    }
}
