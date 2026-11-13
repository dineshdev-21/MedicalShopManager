using Moq;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.UnitTests;

public class PurchaseStockTests
{
    private readonly Mock<IPurchaseRepository> _purchaseRepo = new();
    private readonly Mock<IBatchRepository> _batchRepo = new();
    private readonly Mock<IStockMovementRepository> _movementRepo = new();
    private readonly Mock<ISupplierRepository> _supplierRepo = new();
    private readonly Mock<IPaymentRepository> _paymentRepo = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<IExcelSyncQueueRepository> _excelSync = new();
    private readonly Mock<ILogger<PurchaseService>> _logger = new();

    private PurchaseService CreateService() => new(
        _purchaseRepo.Object,
        _batchRepo.Object,
        _movementRepo.Object,
        _supplierRepo.Object,
        _paymentRepo.Object,
        _audit.Object,
        _excelSync.Object,
        _logger.Object);

    [Fact]
    public async Task CreateDraftAsync_CalculatesTotals_SetsDraftStatus()
    {
        var service = CreateService();

        var purchase = new Purchase
        {
            SupplierInvoiceNumber = "SUP-INV-100",
            SupplierId = 1,
            SupplierName = "Alpha Pharma",
            Items = new List<PurchaseItem>
            {
                new PurchaseItem
                {
                    MedicineId = 10,
                    BatchNumber = "BATCH-A",
                    ExpiryDate = DateTime.Today.AddYears(2),
                    Quantity = 100,
                    FreeQuantity = 10, // 100 + 10 free
                    PurchasePricePaisa = 1000, // Rs 10.00 each -> Subtotal = Rs 1,000 (100,000 paisa)
                    DiscountPercent = 5,       // 5% discount -> 5,000 paisa -> Taxable = 95,000 paisa
                    GstRatePercent = 12        // 12% GST on 95,000 = 11,400 paisa -> Grand Total = 106,400 paisa
                }
            }
        };

        _purchaseRepo.Setup(r => r.InsertWithItemsAsync(It.IsAny<Purchase>())).ReturnsAsync(50L);

        var id = await service.CreateDraftAsync(purchase);

        Assert.Equal(50L, id);
        Assert.Equal(PurchaseStatus.Draft, purchase.Status);
        Assert.Equal(100000, purchase.SubtotalPaisa);
        Assert.Equal(5000, purchase.DiscountAmountPaisa);
        Assert.Equal(11400, purchase.TaxAmountPaisa);
        Assert.Equal(106400, purchase.GrandTotalPaisa);

        // Verify batch was NOT created during draft
        _batchRepo.Verify(r => r.InsertAsync(It.IsAny<MedicineBatch>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmPurchaseAsync_CreatesBatchWithFreeUnits_IncreasesStock()
    {
        var service = CreateService();

        var purchase = new Purchase
        {
            Id = 50,
            SupplierInvoiceNumber = "SUP-INV-100",
            SupplierId = 1,
            SupplierName = "Alpha Pharma",
            Status = PurchaseStatus.Draft,
            PurchaseDate = DateTime.Today,
            GrandTotalPaisa = 106400,
            BalancePaisa = 106400,
            Items = new List<PurchaseItem>
            {
                new PurchaseItem
                {
                    MedicineId = 10,
                    BatchNumber = "BATCH-A",
                    ExpiryDate = DateTime.Today.AddYears(2),
                    Quantity = 100,
                    FreeQuantity = 10, // Total 110 units into stock
                    PurchasePricePaisa = 1000,
                    SellingPricePaisa = 1500,
                    MrpPaisa = 1600
                }
            }
        };

        _purchaseRepo.Setup(r => r.GetWithItemsAsync(50)).ReturnsAsync(purchase);
        _batchRepo.Setup(r => r.GetByBatchNumberAsync(10, "BATCH-A")).ReturnsAsync((MedicineBatch?)null);
        _batchRepo.Setup(r => r.InsertAsync(It.IsAny<MedicineBatch>())).ReturnsAsync(201L);

        var confirmed = await service.ConfirmPurchaseAsync(50, userId: 1);

        Assert.Equal(PurchaseStatus.Confirmed, confirmed.Status);

        // Verify batch inserted with total units (100 + 10 = 110)
        _batchRepo.Verify(r => r.InsertAsync(It.Is<MedicineBatch>(b =>
            b.MedicineId == 10 &&
            b.BatchNumber == "BATCH-A" &&
            b.QuantityReceived == 110 &&
            b.QuantityRemaining == 110 &&
            b.FreeUnits == 10)), Times.Once);

        // Verify stock movement recorded
        _movementRepo.Verify(r => r.InsertAsync(It.Is<StockMovement>(m =>
            m.MovementType == MovementType.PurchaseReceipt &&
            m.Quantity == 110 &&
            m.ReferenceId == 50)), Times.Once);
    }
}
