using Moq;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.UnitTests;

public class StockAdjustmentTests
{
    private readonly Mock<IBatchRepository> _batchRepo = new();
    private readonly Mock<IStockMovementRepository> _movementRepo = new();

    private InventoryService CreateService() => new(
        _batchRepo.Object,
        _movementRepo.Object,
        new Mock<IAuditService>().Object,
        new Mock<IExcelSyncQueueRepository>().Object,
        new Mock<ILogger<InventoryService>>().Object);

    [Theory]
    [InlineData(50, 42, 0, 8)]   // count found 8 fewer -> stock out
    [InlineData(50, 55, 5, 0)]   // count found 5 more  -> stock in
    public async Task AdjustStockAsync_RecordsSignedMovement(int oldQty, int newQty, int expectedIn, int expectedOut)
    {
        _batchRepo.Setup(r => r.GetByIdAsync(7))
            .ReturnsAsync(new MedicineBatch { Id = 7, MedicineId = 3, QuantityRemaining = oldQty });

        await CreateService().AdjustStockAsync(7, newQty, "Physical count", userId: 1);

        _batchRepo.Verify(r => r.UpdateQuantityAsync(7, newQty), Times.Once);
        _movementRepo.Verify(r => r.InsertAsync(It.Is<StockMovement>(m =>
            m.MovementType == MovementType.StockAdjustment &&
            m.QuantityIn == expectedIn &&
            m.QuantityOut == expectedOut &&
            m.BalanceAfter == newQty)), Times.Once);
    }
}
