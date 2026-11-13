using Moq;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.UnitTests;

public class BillingTransactionTests
{
    private readonly Mock<ISaleRepository> _saleRepo = new();
    private readonly Mock<IBatchRepository> _batchRepo = new();
    private readonly Mock<IPaymentRepository> _paymentRepo = new();
    private readonly Mock<IDocumentNumberService> _documentNumbers = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<IExcelSyncQueueRepository> _excelSync = new();
    private readonly Mock<ILogger<BillingService>> _logger = new();

    private BillingService CreateService() => new(
        _saleRepo.Object,
        _batchRepo.Object,
        _paymentRepo.Object,
        _documentNumbers.Object,
        _audit.Object,
        _excelSync.Object,
        _logger.Object);

    [Fact]
    public async Task ProcessSaleAsync_ValidSale_ExecutesTransactionSuccessfully()
    {
        // Arrange
        var service = CreateService();

        var batch = new MedicineBatch
        {
            Id = 101,
            MedicineId = 5,
            BatchNumber = "B-2024",
            QuantityRemaining = 100,
            ExpiryDate = DateTime.Today.AddYears(1),
            IsActive = true
        };

        _batchRepo.Setup(r => r.GetByIdAsync(101)).ReturnsAsync(batch);
        // The repository allocates the invoice number inside its insert transaction.
        _saleRepo.Setup(r => r.InsertWithItemsAsync(It.IsAny<Sale>()))
            .Callback<Sale>(s => s.InvoiceNumber = "INV-2024-00001")
            .ReturnsAsync(1L);

        var sale = new Sale
        {
            CustomerId = 1,
            CustomerName = "John Doe",
            PaymentMethod = PaymentMethod.Cash,
            Items = new List<SaleItem>
            {
                new SaleItem
                {
                    MedicineId = 5,
                    MedicineName = "Paracetamol 500mg",
                    BatchId = 101,
                    BatchNumber = "B-2024",
                    Quantity = 10,
                    UnitPricePaisa = 200, // Rs 2.00 per unit, GST-inclusive -> Subtotal Rs 20.00 (2000 paisa)
                    DiscountPercent = 10, // 10% discount -> rate 180 -> line total Rs 18.00 (1800 paisa)
                    GstRatePercent = 12   // inclusive: taxable = round(1800 × 100 / 112) = 1607, tax = 193
                }
            },
            AmountPaidPaisa = 1800,
            CashReceivedPaisa = 3000 // Rs 30.00 cash given -> Rs 12.00 change returned
        };

        // Act
        var result = await service.ProcessSaleAsync(sale, userId: 1);

        // Assert
        Assert.Equal(1L, result.Id);
        Assert.Equal("INV-2024-00001", result.InvoiceNumber);
        Assert.Equal(2000, result.SubtotalPaisa);
        Assert.Equal(200, result.DiscountAmountPaisa);
        Assert.Equal(1607, result.TaxableAmountPaisa);
        Assert.Equal(97, result.CgstAmountPaisa);  // tax 193: odd paisa to CGST
        Assert.Equal(96, result.SgstAmountPaisa);
        Assert.Equal(1800, result.GrandTotalPaisa); // GST is inside the price, never added on top
        Assert.Equal(result.GrandTotalPaisa, result.TaxableAmountPaisa + result.CgstAmountPaisa + result.SgstAmountPaisa);
        Assert.Equal(PaymentStatus.Paid, result.PaymentStatus);
        Assert.Equal(1200, result.ChangeReturnedPaisa); // 3000 - 1800

        // Stock and the stock ledger are posted inside the repository's sale transaction, not by the service.
        _batchRepo.Verify(r => r.UpdateQuantityAsync(It.IsAny<long>(), It.IsAny<int>()), Times.Never);

        // Verify Excel sync queue was enqueued
        _excelSync.Verify(r => r.EnqueueAsync(SyncEntityType.Sale, 1L), Times.Once);
    }

    [Fact]
    public async Task ProcessSaleAsync_ExpiredBatch_ThrowsInvalidOperationException()
    {
        // Arrange
        var service = CreateService();

        var expiredBatch = new MedicineBatch
        {
            Id = 101,
            MedicineId = 5,
            BatchNumber = "EXP-BATCH",
            QuantityRemaining = 100,
            ExpiryDate = DateTime.Today.AddDays(-1), // EXPIRED!
            IsActive = true
        };

        _batchRepo.Setup(r => r.GetByIdAsync(101)).ReturnsAsync(expiredBatch);

        var sale = new Sale
        {
            Items = new List<SaleItem>
            {
                new SaleItem
                {
                    MedicineId = 5,
                    BatchId = 101,
                    Quantity = 5,
                    UnitPricePaisa = 100
                }
            }
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessSaleAsync(sale, 1));
    }

    [Fact]
    public async Task ProcessSaleAsync_InsufficientStock_ThrowsInvalidOperationException()
    {
        // Arrange
        var service = CreateService();

        var batch = new MedicineBatch
        {
            Id = 101,
            MedicineId = 5,
            BatchNumber = "B101",
            QuantityRemaining = 3, // Only 3 units!
            ExpiryDate = DateTime.Today.AddMonths(6),
            IsActive = true
        };

        _batchRepo.Setup(r => r.GetByIdAsync(101)).ReturnsAsync(batch);

        var sale = new Sale
        {
            Items = new List<SaleItem>
            {
                new SaleItem
                {
                    MedicineId = 5,
                    BatchId = 101,
                    Quantity = 10, // Requesting 10
                    UnitPricePaisa = 100
                }
            }
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessSaleAsync(sale, 1));
    }
}
