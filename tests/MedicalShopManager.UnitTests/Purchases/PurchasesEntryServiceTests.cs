using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Infrastructure.Modules.Purchases;
using Xunit;

namespace MedicalShopManager.UnitTests.Purchases;

/// <summary>Rules of <see cref="PurchaseEntryService"/> with the repositories mocked (no database).</summary>
public class PurchasesEntryServiceTests
{
    private readonly Mock<IPurchaseBillRepository> _purchases = new();
    private readonly Mock<IDistributorRepository> _distributors = new();
    private readonly Mock<IAppSettingsRepository> _settings = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<IExcelSyncQueueRepository> _excel = new();
    private static readonly DateTime Today = DateTime.Today;

    private readonly Distributor _distributor = new()
    {
        Id = 7, Name = "Alpha Pharma", Gstin = "27ABCDE1234F1Z5", StateCode = "27", CreditDays = 30
    };

    public PurchasesEntryServiceTests()
    {
        _distributors.Setup(d => d.GetByIdAsync(7)).ReturnsAsync(_distributor);
        _purchases.Setup(p => p.ConfirmAsync(It.IsAny<PurchaseBill>(), It.IsAny<PurchasePaymentInput?>(), It.IsAny<long?>()))
            .ReturnsAsync((PurchaseBill b, PurchasePaymentInput? _, long? _) => new PurchasePostingResult(42, "PUR/2627/00001", new long[] { 5 }));
        _settings.Setup(s => s.GetValueAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
    }

    private PurchaseEntryService Service() =>
        new(_purchases.Object, _distributors.Object, _settings.Object, _audit.Object, _excel.Object, NullLogger<PurchaseEntryService>.Instance);

    private static PurchaseBill ValidBill() => new()
    {
        SupplierId = 7,
        SupplierInvoiceNumber = "INV-101",
        InvoiceDate = Today,
        Lines =
        {
            new PurchaseBillLine
            {
                MedicineId = 1, MedicineName = "Dolo 650", HsnCode = "30049099", BatchNumber = "B1",
                ExpiryDate = Today.AddYears(1), UnitsPerPack = 15, Quantity = 150, EnteredRatePaisa = 12000,
                MrpPaisa = 18840, SaleRatePaisa = 18840, GstRatePercent = 12
            }
        }
    };

    [Fact]
    public async Task Confirm_ComputesTotals_DefaultsDueDate_AndAudits()
    {
        var bill = ValidBill();

        var result = await Service().ConfirmAsync(bill, null, userId: 3);

        result.PurchaseNumber.Should().Be("PUR/2627/00001");
        bill.DueDate.Should().Be(Today.AddDays(30), "due date = invoice date + the distributor's credit days");
        bill.SupplierName.Should().Be("Alpha Pharma");
        bill.TaxablePaisa.Should().Be(120000); // 10 packs × ₹120
        bill.TaxAmountPaisa.Should().Be(14400);
        bill.GrandTotalPaisa.Should().Be(134400);
        bill.PaymentStatus.Should().Be(PaymentStatus.Unpaid);
        _audit.Verify(a => a.LogAsync("ConfirmPurchase", "Purchase", 42L, null, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_WithInitialPayment_SetsPaidAndPartialStatus()
    {
        var bill = ValidBill();
        var payment = new PurchasePaymentInput(50000, PaymentMethod.Cash, null, Today);

        await Service().ConfirmAsync(bill, payment, 3);

        bill.AmountPaidPaisa.Should().Be(50000);
        bill.BalancePaisa.Should().Be(84400);
        bill.PaymentStatus.Should().Be(PaymentStatus.Partial);
        _purchases.Verify(p => p.ConfirmAsync(bill, payment, 3), Times.Once);
    }

    [Fact]
    public async Task Confirm_PaymentAboveBill_IsRejected_BeforeAnyWrite()
    {
        var bill = ValidBill();

        var act = () => Service().ConfirmAsync(bill, new PurchasePaymentInput(200000, PaymentMethod.Cash, null, Today), 3);

        (await act.Should().ThrowAsync<PurchaseValidationException>()).Which.Errors.Should().Contain(e => e.Contains("Payment cannot be more"));
        _purchases.Verify(p => p.ConfirmAsync(It.IsAny<PurchaseBill>(), It.IsAny<PurchasePaymentInput?>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_InvalidBill_ListsEveryProblem()
    {
        var bill = ValidBill();
        bill.SupplierInvoiceNumber = "";
        bill.Lines[0].BatchNumber = "";
        bill.Lines[0].ExpiryDate = Today.AddDays(-1);

        var ex = await Assert.ThrowsAsync<PurchaseValidationException>(() => Service().ConfirmAsync(bill, null, 3));

        ex.Errors.Should().Contain("Please add Invoice No").And.Contain("Please fill Batch No on Row: 1").And.Contain("Batch is Expired on Row: 1");
    }

    [Fact]
    public async Task Confirm_DuplicateBill_Warns_UnlessOverridden()
    {
        var duplicate = new PurchaseListRow { Id = 9, SupplierInvoiceNumber = "INV-101", SupplierName = "Alpha Pharma", PurchaseNumber = "PUR/2627/00009" };
        _purchases.Setup(p => p.FindDuplicateAsync(7, "INV-101", 0)).ReturnsAsync(duplicate);

        await Assert.ThrowsAsync<DuplicatePurchaseInvoiceException>(() => Service().ConfirmAsync(ValidBill(), null, 3));
        var result = await Service().ConfirmAsync(ValidBill(), null, 3, allowDuplicateInvoice: true);

        result.PurchaseId.Should().Be(42);
    }

    [Fact]
    public async Task Confirm_UnknownDistributor_IsRefused()
    {
        var bill = ValidBill();
        bill.SupplierId = 99;

        await Assert.ThrowsAsync<PurchaseRuleException>(() => Service().ConfirmAsync(bill, null, 3));
    }

    [Fact]
    public async Task Confirm_AlreadyConfirmed_IsRefused()
    {
        var bill = ValidBill();
        bill.Status = PurchaseStatus.Confirmed;

        await Assert.ThrowsAsync<PurchaseRuleException>(() => Service().ConfirmAsync(bill, null, 3));
    }

    [Fact]
    public async Task SaveDraft_NeedsOnlyDistributorAndSaneRows()
    {
        _purchases.Setup(p => p.SaveDraftAsync(It.IsAny<PurchaseBill>())).ReturnsAsync(11);
        var bill = ValidBill();
        bill.SupplierInvoiceNumber = ""; // allowed on a draft
        bill.Lines[0].BatchNumber = "";

        var id = await Service().SaveDraftAsync(bill, 3);

        id.Should().Be(11);
        bill.GrandTotalPaisa.Should().Be(134400);
        bill.CreatedByUserId.Should().Be(3);

        var noDistributor = ValidBill();
        noDistributor.SupplierId = 0;
        (await Assert.ThrowsAsync<PurchaseValidationException>(() => Service().SaveDraftAsync(noDistributor, 3)))
            .Errors.Should().Contain("Please add Distributor");
    }

    [Fact]
    public async Task Delete_Draft_RemovesIt_Confirmed_NeedsReasonAndCancels()
    {
        _purchases.Setup(p => p.GetAsync(1)).ReturnsAsync(new PurchaseBill { Id = 1, Status = PurchaseStatus.Draft, SupplierId = 7 });
        _purchases.Setup(p => p.GetAsync(2)).ReturnsAsync(new PurchaseBill { Id = 2, Status = PurchaseStatus.Confirmed, SupplierId = 7, PurchaseNumber = "P2" });
        _purchases.Setup(p => p.GetAsync(3)).ReturnsAsync(new PurchaseBill { Id = 3, Status = PurchaseStatus.Cancelled });

        await Service().DeleteAsync(1, "", 3);
        _purchases.Verify(p => p.DeleteDraftAsync(1), Times.Once);

        await Assert.ThrowsAsync<PurchaseRuleException>(() => Service().DeleteAsync(2, "  ", 3));
        await Service().DeleteAsync(2, "Wrong bill", 3);
        _purchases.Verify(p => p.CancelConfirmedAsync(2, "Wrong bill", 3), Times.Once);

        await Assert.ThrowsAsync<PurchaseRuleException>(() => Service().DeleteAsync(3, "x", 3));
    }

    [Theory]
    [InlineData(0, PaymentMethod.Cash)]
    [InlineData(-100, PaymentMethod.Cash)]
    [InlineData(100, PaymentMethod.Credit)]
    public async Task RecordPayment_RejectsZeroNegativeAndCredit(long amount, PaymentMethod method)
    {
        await Assert.ThrowsAsync<PurchaseRuleException>(() =>
            Service().RecordPaymentAsync(1, new PurchasePaymentInput(amount, method, null, Today), 3));
        _purchases.Verify(p => p.RecordPaymentAsync(It.IsAny<long>(), It.IsAny<PurchasePaymentInput>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact]
    public async Task RecordPayment_FutureDate_IsRejected()
    {
        await Assert.ThrowsAsync<PurchaseRuleException>(() =>
            Service().RecordPaymentAsync(1, new PurchasePaymentInput(100, PaymentMethod.Cash, null, Today.AddDays(1)), 3));
    }

    [Fact]
    public async Task CreateLine_UsesItemMaster_LastPurchaseRates_AndSaleRateSetting()
    {
        _settings.Setup(s => s.GetValueAsync(PurchaseSettings.SaleDiscountKey)).ReturnsAsync("10");
        _purchases.Setup(p => p.GetLastLineForMedicineAsync(1)).ReturnsAsync(new PurchaseBillLine
        {
            UnitsPerPack = 15, EnteredRatePaisa = 11500, PtrPaisa = 11500, MrpPaisa = 20000, DiscountPercent = 2
        });
        var medicine = new Medicine { Id = 1, Name = "Dolo 650", UnitsPerPack = 15, HsnCode = "30049099", GstRatePercent = 12, DefaultMrpPaisa = 18840 };

        var line = await Service().CreateLineAsync(medicine);

        line.UnitsPerPack.Should().Be(15);
        line.HsnCode.Should().Be("30049099");
        line.GstRatePercent.Should().Be(12);
        line.MrpPaisa.Should().Be(20000, "the last purchase's MRP of the same pack size wins");
        line.EnteredRatePaisa.Should().Be(11500);
        line.DiscountPercent.Should().Be(2);
        line.SaleRatePaisa.Should().Be(18000, "MRP less the 10 % default sale discount");
    }

    [Fact]
    public async Task CreateLine_PackSizeChanged_DoesNotReuseOldRates()
    {
        _purchases.Setup(p => p.GetLastLineForMedicineAsync(1)).ReturnsAsync(new PurchaseBillLine { UnitsPerPack = 10, EnteredRatePaisa = 8000, MrpPaisa = 12000 });
        var medicine = new Medicine { Id = 1, Name = "Dolo 650", UnitsPerPack = 15, DefaultMrpPaisa = 18840 };

        var line = await Service().CreateLineAsync(medicine);

        line.EnteredRatePaisa.Should().Be(0);
        line.MrpPaisa.Should().Be(18840);
    }

    [Fact]
    public async Task Settings_FallBackToDefaults_AndReadShopState()
    {
        _settings.Setup(s => s.GetValueAsync(PurchaseSettings.NearExpiryMonthsKey)).ReturnsAsync("abc");
        _settings.Setup(s => s.GetValueAsync(PurchaseSettings.RoundOffKey)).ReturnsAsync("0");
        _settings.Setup(s => s.GetValueAsync("ShopGstin")).ReturnsAsync("29AAAAA0000A1Z5");

        var settings = await Service().GetSettingsAsync();

        settings.NearExpiryMonths.Should().Be(3);
        settings.RoundOffToRupee.Should().BeFalse();
        settings.BusinessStateCode.Should().Be("29");
        (await Service().IsInterStateAsync(_distributor)).Should().BeTrue("Maharashtra (27) supplier to a Karnataka (29) shop");
    }

    [Fact]
    public async Task UpdateConfirmed_TotalBelowAmountPaid_IsRejected()
    {
        var bill = ValidBill();
        bill.Id = 5;
        _purchases.Setup(p => p.GetAsync(5)).ReturnsAsync(new PurchaseBill { Id = 5, Status = PurchaseStatus.Confirmed, AmountPaidPaisa = 500000 });

        var ex = await Assert.ThrowsAsync<PurchaseValidationException>(() => Service().UpdateConfirmedAsync(bill, 3));

        ex.Errors.Should().Contain(e => e.Contains("already paid"));
        _purchases.Verify(p => p.UpdateConfirmedAsync(It.IsAny<PurchaseBill>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact]
    public async Task List_RejectsReversedDates()
    {
        await Assert.ThrowsAsync<PurchaseRuleException>(() => Service().ListAsync(new PurchaseListFilter(Today, Today.AddDays(-1))));
    }
}
