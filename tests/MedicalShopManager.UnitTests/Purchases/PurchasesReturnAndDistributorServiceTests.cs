using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Infrastructure.Modules.Purchases;
using Xunit;

namespace MedicalShopManager.UnitTests.Purchases;

/// <summary>Rules of <see cref="PurchaseReturnService"/> and <see cref="DistributorService"/> with mocked repositories.</summary>
public class PurchasesReturnAndDistributorServiceTests
{
    private readonly Mock<IPurchaseReturnRepository> _returns = new();
    private readonly Mock<IDistributorRepository> _distributors = new();
    private readonly Mock<IDocumentNumberService> _numbers = new();
    private readonly Mock<IAppSettingsRepository> _settings = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<IExcelSyncQueueRepository> _excel = new();

    public PurchasesReturnAndDistributorServiceTests()
    {
        _settings.Setup(s => s.GetValueAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        _settings.Setup(s => s.GetValueAsync("ShopGstin")).ReturnsAsync("29AAAAA0000A1Z5");
        _distributors.Setup(d => d.GetByIdAsync(7)).ReturnsAsync(new Distributor { Id = 7, Name = "Alpha Pharma", Gstin = "27ABCDE1234F1Z5" });
        _returns.Setup(r => r.PostAsync(It.IsAny<PurchaseReturnDocument>(), It.IsAny<long?>()))
            .ReturnsAsync((PurchaseReturnDocument d, long? _) => { d.Id = 3; d.ReturnNumber = "DN/2627/00001"; return d; });
    }

    private PurchaseReturnService Returns() => new(_returns.Object, _distributors.Object, _numbers.Object, _settings.Object, _audit.Object,
        _excel.Object, NullLogger<PurchaseReturnService>.Instance);

    private DistributorService Distributors() => new(_distributors.Object, _audit.Object, _excel.Object);

    private static ReturnableBatch Batch(int stock = 30, int purchased = 45, int returned = 0, long? itemId = 11) => new()
    {
        BatchId = 5, MedicineId = 1, MedicineName = "Dolo 650", BatchNumber = "B1", ExpiryDate = DateTime.Today.AddMonths(2),
        UnitsPerPack = 15, QuantityRemaining = stock, PurchaseItemId = itemId, PurchasedUnits = purchased, ReturnedUnits = returned,
        RatePaisa = 12000, GstRatePercent = 12, SupplierId = 7
    };

    [Fact]
    public void MaxReturnable_IsStockCappedByWhatTheBillDeliveredLessEarlierReturns()
    {
        Batch(stock: 30, purchased: 45, returned: 20).MaxReturnableUnits.Should().Be(25);
        Batch(stock: 10, purchased: 45).MaxReturnableUnits.Should().Be(10);
        Batch(stock: 30, itemId: null).MaxReturnableUnits.Should().Be(30, "stock not tied to a bill is capped only by what is on hand");
    }

    [Fact]
    public void CreateLine_ClampsQuantity_AndCopiesRateAndGst()
    {
        var line = Returns().CreateLine(Batch(), 100);

        line.Quantity.Should().Be(30);
        line.RatePaisa.Should().Be(12000);
        line.GstRatePercent.Should().Be(12);
        line.PurchaseItemId.Should().Be(11);
    }

    [Fact]
    public async Task Post_SetsInterStateFromGstin_ComputesTotals_AndAudits()
    {
        var service = Returns();
        var doc = new PurchaseReturnDocument { SupplierId = 7, ReturnDate = DateTime.Today, Reason = PurchaseReturnReason.Damaged };
        doc.Lines.Add(service.CreateLine(Batch(), 15)); // 1 pack × ₹120 + 12 % IGST

        var posted = await service.PostAsync(doc, 2);

        posted.ReturnNumber.Should().Be("DN/2627/00001");
        posted.IsInterState.Should().BeTrue();
        posted.TaxablePaisa.Should().Be(12000);
        posted.IgstPaisa.Should().Be(1440);
        posted.NetAmountPaisa.Should().Be(13400, "₹134.40 rounded to the rupee");
        posted.RoundOffPaisa.Should().Be(-40);
        posted.SupplierName.Should().Be("Alpha Pharma");
        _audit.Verify(a => a.LogAsync("PostDebitNote", "PurchaseReturn", 3L, null, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Post_RefundAboveDebitNote_OrNoLines_IsRejected()
    {
        var service = Returns();
        var doc = new PurchaseReturnDocument { SupplierId = 7, ReturnDate = DateTime.Today, RefundReceivedPaisa = 999999 };
        doc.Lines.Add(service.CreateLine(Batch(), 15));
        (await Assert.ThrowsAsync<PurchaseValidationException>(() => service.PostAsync(doc, 2))).Errors.Should().Contain(e => e.StartsWith("Refund cannot"));

        var empty = new PurchaseReturnDocument { SupplierId = 7, ReturnDate = DateTime.Today };
        (await Assert.ThrowsAsync<PurchaseValidationException>(() => service.PostAsync(empty, 2))).Errors.Should().Contain("Please add Products to return");
        _returns.Verify(r => r.PostAsync(It.IsAny<PurchaseReturnDocument>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact]
    public async Task ExpiryCandidates_UseTheNearExpirySetting()
    {
        _settings.Setup(s => s.GetValueAsync(PurchaseSettings.NearExpiryMonthsKey)).ReturnsAsync("6");
        _returns.Setup(r => r.GetExpiryCandidatesAsync(It.IsAny<DateTime>(), 7)).ReturnsAsync(Array.Empty<ReturnableBatch>());

        await Returns().GetExpiryCandidatesAsync(7);

        _returns.Verify(r => r.GetExpiryCandidatesAsync(DateTime.Today.AddMonths(6), 7), Times.Once);
    }

    [Fact]
    public async Task PeekNumber_UsesTheDebitNoteSequence()
    {
        _numbers.Setup(n => n.PeekNextAsync(DocumentType.DebitNote, It.IsAny<DateTime?>())).ReturnsAsync("DN/2627/00004");

        (await Returns().PeekNextNumberAsync(DateTime.Today)).Should().Be("DN/2627/00004");
    }

    [Fact]
    public async Task Distributor_Save_NormalisesAndFillsStateFromGstin()
    {
        _distributors.Setup(d => d.InsertAsync(It.IsAny<Distributor>())).ReturnsAsync(12);
        var d = new Distributor { Name = "  Beta Distributors ", Gstin = "29abcde1234f1z5", DrugLicenseNumber = "ka-blr-20b-1", Phone = " 98450 12345 " };

        var id = await Distributors().SaveAsync(d);

        id.Should().Be(12);
        d.Name.Should().Be("Beta Distributors");
        d.Gstin.Should().Be("29ABCDE1234F1Z5");
        d.StateCode.Should().Be("29");
        d.DrugLicenseNumber.Should().Be("KA-BLR-20B-1");
        _audit.Verify(a => a.LogAsync("CreateSupplier", "Supplier", 12L, null, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Distributor_DuplicateGstin_AndBadData_AreRejected()
    {
        _distributors.Setup(d => d.GstinExistsAsync("29ABCDE1234F1Z5", 0)).ReturnsAsync(true);
        (await Assert.ThrowsAsync<PurchaseValidationException>(() => Distributors().SaveAsync(new Distributor { Name = "X", Gstin = "29ABCDE1234F1Z5" })))
            .Errors.Should().Contain(e => e.Contains("already uses GSTIN"));

        var bad = new Distributor { Name = "", Phone = "123", CreditDays = 400 };
        var errors = (await Assert.ThrowsAsync<PurchaseValidationException>(() => Distributors().SaveAsync(bad))).Errors;
        errors.Should().Contain("Please add Distributor Name").And.Contain(e => e.Contains("phone")).And.Contain(e => e.Contains("Credit period"));
        _distributors.Verify(r => r.InsertAsync(It.IsAny<Distributor>()), Times.Never);
    }

    [Fact]
    public async Task Distributor_Statement_UnknownSupplier_IsRefused()
    {
        await Assert.ThrowsAsync<PurchaseRuleException>(() => Distributors().GetStatementAsync(99, DateTime.Today.AddMonths(-1), DateTime.Today));
    }
}
