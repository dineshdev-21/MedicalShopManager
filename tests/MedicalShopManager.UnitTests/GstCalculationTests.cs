using Moq;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.UnitTests;

public class GstCalculationTests
{
    private readonly BillingService _billingService;

    public GstCalculationTests()
    {
        var saleRepo = new Mock<ISaleRepository>();
        var batchRepo = new Mock<IBatchRepository>();
        var paymentRepo = new Mock<IPaymentRepository>();
        var documentNumbers = new Mock<IDocumentNumberService>();
        var audit = new Mock<IAuditService>();
        var excelSync = new Mock<IExcelSyncQueueRepository>();
        var logger = new Mock<ILogger<BillingService>>();

        _billingService = new BillingService(
            saleRepo.Object,
            batchRepo.Object,
            paymentRepo.Object,
            documentNumbers.Object,
            audit.Object,
            excelSync.Object,
            logger.Object);
    }

    [Theory]
    // 100.00 Rupees = 10,000 Paisa
    // 12% GST: CGST = 600, SGST = 600, IGST = 0
    [InlineData(10000, 12, false, 600, 600, 0)]
    // 18% GST: CGST = 900, SGST = 900, IGST = 0
    [InlineData(10000, 18, false, 900, 900, 0)]
    // 5% GST: CGST = 250, SGST = 250, IGST = 0
    [InlineData(10000, 5, false, 250, 250, 0)]
    // 0% GST
    [InlineData(10000, 0, false, 0, 0, 0)]
    public void CalculateGst_Intrastate_SplitsEquallyBetweenCgstAndSgst(
        long taxablePaisa, decimal rate, bool isInterstate, long expectedCgst, long expectedSgst, long expectedIgst)
    {
        var (cgst, sgst, igst) = _billingService.CalculateGst(taxablePaisa, rate, isInterstate);

        Assert.Equal(expectedCgst, cgst);
        Assert.Equal(expectedSgst, sgst);
        Assert.Equal(expectedIgst, igst);
        Assert.Equal(expectedCgst + expectedSgst, cgst + sgst);
    }

    [Theory]
    // 10,000 Paisa with 12% IGST: IGST = 1200, CGST = 0, SGST = 0
    [InlineData(10000, 12, true, 0, 0, 1200)]
    // 10,000 Paisa with 18% IGST: IGST = 1800, CGST = 0, SGST = 0
    [InlineData(10000, 18, true, 0, 0, 1800)]
    // 50,000 Paisa with 5% IGST: IGST = 2500
    [InlineData(50000, 5, true, 0, 0, 2500)]
    public void CalculateGst_Interstate_AssignsAllToIgst(
        long taxablePaisa, decimal rate, bool isInterstate, long expectedCgst, long expectedSgst, long expectedIgst)
    {
        var (cgst, sgst, igst) = _billingService.CalculateGst(taxablePaisa, rate, isInterstate);

        Assert.Equal(expectedCgst, cgst);
        Assert.Equal(expectedSgst, sgst);
        Assert.Equal(expectedIgst, igst);
    }

    [Fact]
    public void CalculateGst_OddAmount_PreventsRoundingDiscrepancy()
    {
        // 99 Rupees 95 Paisa = 9995 Paisa at 5% GST
        // 5% of 9995 = 499.75 -> Total Tax = 500 Paisa
        // CGST (2.5%) = 249.875 -> 250 Paisa
        // SGST = 500 - 250 = 250 Paisa
        var (cgst, sgst, igst) = _billingService.CalculateGst(9995, 5, false);

        Assert.Equal(0, igst);
        Assert.Equal(500, cgst + sgst);
    }

    [Fact]
    public void CalculateGst_ZeroOrNegative_ReturnsZero()
    {
        var (cgst1, sgst1, igst1) = _billingService.CalculateGst(0, 12, false);
        Assert.Equal((0L, 0L, 0L), (cgst1, sgst1, igst1));

        var (cgst2, sgst2, igst2) = _billingService.CalculateGst(-500, 12, false);
        Assert.Equal((0L, 0L, 0L), (cgst2, sgst2, igst2));
    }
}
