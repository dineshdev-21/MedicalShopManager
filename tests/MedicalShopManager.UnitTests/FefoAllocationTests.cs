using Moq;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.UnitTests;

public class FefoAllocationTests
{
    private readonly Mock<IBatchRepository> _batchRepoMock = new();
    private readonly Mock<IStockMovementRepository> _movementRepoMock = new();
    private readonly Mock<IAuditService> _auditMock = new();
    private readonly Mock<IExcelSyncQueueRepository> _excelSyncMock = new();
    private readonly Mock<ILogger<InventoryService>> _loggerMock = new();

    private InventoryService CreateService() => new(
        _batchRepoMock.Object,
        _movementRepoMock.Object,
        _auditMock.Object,
        _excelSyncMock.Object,
        _loggerMock.Object);

    [Fact]
    public async Task AllocateStockFefo_SingleBatch_AllocatesCorrectly()
    {
        // Arrange
        var service = CreateService();
        var batch = new MedicineBatch
        {
            Id = 1,
            MedicineId = 10,
            BatchNumber = "B001",
            QuantityRemaining = 50,
            ExpiryDate = DateTime.Today.AddMonths(6),
            IsActive = true
        };

        _batchRepoMock.Setup(r => r.GetFefoOrderedBatchesAsync(10))
            .ReturnsAsync(new List<MedicineBatch> { batch });

        // Act
        var result = await service.AllocateStockFefoAsync(10, 20);

        // Assert
        Assert.Single(result);
        Assert.Equal(1, result[0].Batch.Id);
        Assert.Equal(20, result[0].Quantity);
    }

    [Fact]
    public async Task AllocateStockFefo_MultipleBatches_PicksEarliestExpiryFirst()
    {
        // Arrange
        var service = CreateService();
        var batchEarly = new MedicineBatch
        {
            Id = 1,
            MedicineId = 10,
            BatchNumber = "B-EARLY",
            QuantityRemaining = 15,
            ExpiryDate = DateTime.Today.AddDays(30),
            IsActive = true
        };
        var batchLate = new MedicineBatch
        {
            Id = 2,
            MedicineId = 10,
            BatchNumber = "B-LATE",
            QuantityRemaining = 50,
            ExpiryDate = DateTime.Today.AddDays(300),
            IsActive = true
        };

        _batchRepoMock.Setup(r => r.GetFefoOrderedBatchesAsync(10))
            .ReturnsAsync(new List<MedicineBatch> { batchEarly, batchLate });

        // Act - Request 25 units: should take 15 from batchEarly, and 10 from batchLate
        var result = await service.AllocateStockFefoAsync(10, 25);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].Batch.Id);
        Assert.Equal(15, result[0].Quantity);

        Assert.Equal(2, result[1].Batch.Id);
        Assert.Equal(10, result[1].Quantity);
    }

    [Fact]
    public async Task AllocateStockFefo_InsufficientStock_ThrowsInvalidOperationException()
    {
        // Arrange
        var service = CreateService();
        var batch = new MedicineBatch
        {
            Id = 1,
            MedicineId = 10,
            QuantityRemaining = 5,
            ExpiryDate = DateTime.Today.AddMonths(3),
            IsActive = true
        };

        _batchRepoMock.Setup(r => r.GetFefoOrderedBatchesAsync(10))
            .ReturnsAsync(new List<MedicineBatch> { batch });

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AllocateStockFefoAsync(10, 10));
    }

    [Fact]
    public async Task AllocateStockFefo_IgnoresExpiredBatches()
    {
        // Arrange
        var service = CreateService();
        var expiredBatch = new MedicineBatch
        {
            Id = 1,
            MedicineId = 10,
            BatchNumber = "EXPIRED",
            QuantityRemaining = 100,
            ExpiryDate = DateTime.Today.AddDays(-5), // Expired!
            IsActive = true
        };
        var validBatch = new MedicineBatch
        {
            Id = 2,
            MedicineId = 10,
            BatchNumber = "VALID",
            QuantityRemaining = 20,
            ExpiryDate = DateTime.Today.AddDays(60),
            IsActive = true
        };

        _batchRepoMock.Setup(r => r.GetFefoOrderedBatchesAsync(10))
            .ReturnsAsync(new List<MedicineBatch> { expiredBatch, validBatch });

        // Act
        var result = await service.AllocateStockFefoAsync(10, 10);

        // Assert
        Assert.Single(result);
        Assert.Equal(2, result[0].Batch.Id);
        Assert.Equal("VALID", result[0].Batch.BatchNumber);
        Assert.Equal(10, result[0].Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task AllocateStockFefo_NonPositiveQuantity_ThrowsArgumentException(int qty)
    {
        var service = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AllocateStockFefoAsync(10, qty));
    }
}
