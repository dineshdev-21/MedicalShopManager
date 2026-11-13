using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Infrastructure.Services;

/// <summary>
/// Purchase management service handling drafts, confirmation with stock additions, and supplier payments.
/// </summary>
public class PurchaseService : IPurchaseService
{
    private readonly IPurchaseRepository _purchaseRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly IStockMovementRepository _movementRepository;
    private readonly ISupplierRepository _supplierRepository;
    private readonly IPaymentRepository _paymentRepository;
    private readonly IAuditService _auditService;
    private readonly IExcelSyncQueueRepository _excelSyncQueueRepository;
    private readonly ILogger<PurchaseService> _logger;

    public PurchaseService(
        IPurchaseRepository purchaseRepository,
        IBatchRepository batchRepository,
        IStockMovementRepository movementRepository,
        ISupplierRepository supplierRepository,
        IPaymentRepository paymentRepository,
        IAuditService auditService,
        IExcelSyncQueueRepository excelSyncQueueRepository,
        ILogger<PurchaseService> logger)
    {
        _purchaseRepository = purchaseRepository;
        _batchRepository = batchRepository;
        _movementRepository = movementRepository;
        _supplierRepository = supplierRepository;
        _paymentRepository = paymentRepository;
        _auditService = auditService;
        _excelSyncQueueRepository = excelSyncQueueRepository;
        _logger = logger;
    }

    public async Task<long> CreateDraftAsync(Purchase purchase)
    {
        if (purchase.Items == null || purchase.Items.Count == 0)
            throw new ArgumentException("Purchase must contain at least one line item.", nameof(purchase));

        // Calculate totals in paisa
        long subtotal = 0;
        long totalDiscount = 0;
        long totalTax = 0;
        long grandTotal = 0;

        foreach (var item in purchase.Items)
        {
            var lineSubtotal = item.Quantity * item.PurchasePricePaisa;
            var lineDiscount = item.DiscountPercent > 0
                ? (long)Math.Round((lineSubtotal * item.DiscountPercent) / 100m, MidpointRounding.AwayFromZero)
                : 0;
            var taxable = lineSubtotal - lineDiscount;
            var lineTax = item.GstRatePercent > 0
                ? (long)Math.Round((taxable * item.GstRatePercent) / 100m, MidpointRounding.AwayFromZero)
                : 0;
            var lineTotal = taxable + lineTax;

            item.DiscountAmountPaisa = lineDiscount;
            item.GstAmountPaisa = lineTax;
            item.LineTotalPaisa = lineTotal;

            subtotal += lineSubtotal;
            totalDiscount += lineDiscount;
            totalTax += lineTax;
            grandTotal += lineTotal;
        }

        purchase.SubtotalPaisa = subtotal;
        purchase.DiscountAmountPaisa = totalDiscount;
        purchase.TaxAmountPaisa = totalTax;
        purchase.GrandTotalPaisa = grandTotal;
        purchase.BalancePaisa = grandTotal - purchase.AmountPaidPaisa;
        purchase.Status = PurchaseStatus.Draft;
        purchase.PaymentStatus = purchase.AmountPaidPaisa >= grandTotal
            ? PaymentStatus.Paid
            : (purchase.AmountPaidPaisa > 0 ? PaymentStatus.Partial : PaymentStatus.Unpaid);
        purchase.CreatedAt = DateTime.UtcNow;
        purchase.UpdatedAt = DateTime.UtcNow;

        var purchaseId = await _purchaseRepository.InsertWithItemsAsync(purchase);
        purchase.Id = purchaseId;

        await _auditService.LogAsync("CreatePurchaseDraft", "Purchase", purchaseId, null,
            $"{{\"Supplier\":\"{purchase.SupplierName}\",\"Invoice\":\"{purchase.SupplierInvoiceNumber}\",\"Total\":{purchase.GrandTotalPaisa}}}",
            "Purchase draft created");

        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Purchase, purchaseId);
        _logger.LogInformation("Purchase draft created: ID {PurchaseId}, Supplier {Supplier}", purchaseId, purchase.SupplierName);

        return purchaseId;
    }

    public async Task<Purchase> ConfirmPurchaseAsync(long purchaseId, long userId)
    {
        var purchase = await _purchaseRepository.GetWithItemsAsync(purchaseId)
            ?? throw new KeyNotFoundException($"Purchase ID {purchaseId} not found");

        if (purchase.Status == PurchaseStatus.Confirmed)
            throw new InvalidOperationException($"Purchase ID {purchaseId} is already confirmed");

        // Convert purchase items into inventory batches and record stock movements
        foreach (var item in purchase.Items)
        {
            var totalUnits = item.Quantity + item.FreeQuantity;
            var existingBatch = await _batchRepository.GetByBatchNumberAsync(item.MedicineId, item.BatchNumber);

            if (existingBatch != null)
            {
                var newQty = existingBatch.QuantityRemaining + totalUnits;
                await _batchRepository.UpdateQuantityAsync(existingBatch.Id, newQty);

                var movement = new StockMovement
                {
                    MedicineId = item.MedicineId,
                    BatchId = existingBatch.Id,
                    MovementType = MovementType.PurchaseReceipt,
                    Quantity = totalUnits,
                    BalanceAfter = newQty,
                    ReferenceType = "Purchase",
                    ReferenceId = purchaseId,
                    Notes = $"Purchase Confirmed: Inv {purchase.SupplierInvoiceNumber}",
                    CreatedByUserId = userId,
                    MovementDate = DateTime.UtcNow
                };
                await _movementRepository.InsertAsync(movement);

                await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Batch, existingBatch.Id);
            }
            else
            {
                var newBatch = new MedicineBatch
                {
                    MedicineId = item.MedicineId,
                    BatchNumber = item.BatchNumber,
                    ManufacturingDate = item.ManufacturingDate,
                    ExpiryDate = item.ExpiryDate,
                    PurchaseDate = purchase.PurchaseDate,
                    SupplierId = purchase.SupplierId,
                    QuantityReceived = totalUnits,
                    QuantityRemaining = totalUnits,
                    FreeUnits = item.FreeQuantity,
                    // Purchase entry is still per unit (PurchaseItem prices are per unit), so the batch's
                    // rates describe a "pack" of 1 unit. The Purchases module moves entry to packs (spec 04).
                    UnitsPerPack = 1,
                    PurchaseRatePaisa = item.PurchasePricePaisa,
                    SaleRatePaisa = item.SellingPricePaisa,
                    MrpPaisa = item.MrpPaisa,
                    DiscountPercent = item.DiscountPercent,
                    GstRatePercent = item.GstRatePercent,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                var batchId = await _batchRepository.InsertAsync(newBatch);
                newBatch.Id = batchId;

                var movement = new StockMovement
                {
                    MedicineId = item.MedicineId,
                    BatchId = batchId,
                    MovementType = MovementType.PurchaseReceipt,
                    Quantity = totalUnits,
                    BalanceAfter = totalUnits,
                    ReferenceType = "Purchase",
                    ReferenceId = purchaseId,
                    Notes = $"Purchase Confirmed: Inv {purchase.SupplierInvoiceNumber}",
                    CreatedByUserId = userId,
                    MovementDate = DateTime.UtcNow
                };
                await _movementRepository.InsertAsync(movement);

                await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Batch, batchId);
            }

            await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Medicine, item.MedicineId);
        }

        await _purchaseRepository.UpdateStatusAsync(purchaseId, PurchaseStatus.Confirmed);
        purchase.Status = PurchaseStatus.Confirmed;

        // Supplier outstanding is derived from confirmed purchase balances, so only the Excel copy needs refreshing.
        if (purchase.BalancePaisa > 0)
            await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Supplier, purchase.SupplierId);

        await _auditService.LogAsync("ConfirmPurchase", "Purchase", purchaseId,
            "{\"Status\":\"Draft\"}", "{\"Status\":\"Confirmed\"}", "Purchase stock added to inventory");

        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Purchase, purchaseId);
        _logger.LogInformation("Purchase {PurchaseId} confirmed. Inventory updated.", purchaseId);

        return purchase;
    }

    public async Task<Purchase?> GetPurchaseAsync(long id)
    {
        return await _purchaseRepository.GetWithItemsAsync(id);
    }

    public async Task<IReadOnlyList<Purchase>> GetPurchasesBySupplierAsync(long supplierId)
    {
        return await _purchaseRepository.GetBySupplierAsync(supplierId);
    }

    public async Task<IReadOnlyList<Purchase>> GetPurchasesByDateAsync(DateTime from, DateTime to)
    {
        return await _purchaseRepository.GetByDateRangeAsync(from, to);
    }

    public async Task RecordPaymentAsync(long purchaseId, long amountPaisa, PaymentMethod method, string? reference, long userId)
    {
        if (amountPaisa <= 0)
            throw new ArgumentException("Payment amount must be greater than zero", nameof(amountPaisa));

        var purchase = await _purchaseRepository.GetByIdAsync(purchaseId)
            ?? throw new KeyNotFoundException($"Purchase ID {purchaseId} not found");

        var payment = new Payment
        {
            PaymentType = PaymentType.SupplierPayment,
            TransactionId = purchaseId,
            PaymentDate = DateTime.Now,
            AmountPaisa = amountPaisa,
            PaymentMethod = method,
            ReferenceNumber = reference,
            Notes = $"Supplier payment for Purchase #{purchaseId} (Inv {purchase.SupplierInvoiceNumber})",
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        var paymentId = await _paymentRepository.InsertAsync(payment);

        purchase.AmountPaidPaisa += amountPaisa;
        purchase.BalancePaisa = Math.Max(0, purchase.GrandTotalPaisa - purchase.AmountPaidPaisa);
        purchase.PaymentStatus = purchase.BalancePaisa == 0 ? PaymentStatus.Paid : PaymentStatus.Partial;
        purchase.UpdatedAt = DateTime.UtcNow;

        await _purchaseRepository.UpdateAsync(purchase);

        // Supplier outstanding is derived from purchase balances (updated above); refresh the Excel copy.
        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Supplier, purchase.SupplierId);

        await _auditService.LogAsync("RecordSupplierPayment", "Purchase", purchaseId,
            null, $"{{\"AmountPaisa\":{amountPaisa},\"Method\":\"{method}\"}}", "Supplier payment recorded");

        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Payment, paymentId);
        await _excelSyncQueueRepository.EnqueueAsync(SyncEntityType.Purchase, purchaseId);

        _logger.LogInformation("Recorded payment of {Amount} paisa for purchase {PurchaseId}", amountPaisa, purchaseId);
    }
}
