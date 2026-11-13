using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Infrastructure.Modules.Billing;
using Moq;
using Xunit;

namespace MedicalShopManager.UnitTests.Billing;

/// <summary>SaleBillingService, SaleDraftService and BillingCustomerService rules with mocked repositories.</summary>
public class BillingServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 10, 30, 0);

    private readonly Mock<IBillingSaleRepository> _sales = new();
    private readonly Mock<ISaleCatalogRepository> _catalog = new();
    private readonly Mock<IBillingCustomerRepository> _customers = new();
    private readonly Mock<IAppSettingsRepository> _settings = new();
    private readonly Mock<IControlCenterSettingsService> _controlCenter = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<IExcelSyncQueueRepository> _sync = new();
    private readonly Mock<IAuthService> _auth = new();

    public BillingServiceTests()
    {
        _controlCenter.Setup(c => c.GetSaleSettingsAsync()).ReturnsAsync(new SaleSettings());
        _controlCenter.Setup(c => c.GetBusinessProfileAsync()).ReturnsAsync(new BusinessProfile { StateCode = "27", GstRegistrationType = GstRegistrationType.Regular });
        _catalog.Setup(c => c.GetBatchAsync(1)).ReturnsAsync(new SaleBatchOption
        {
            BatchId = 1, MedicineId = 1, BatchNumber = "B1", ExpiryDate = Now.AddYears(1), UnitsPerPack = 15, QuantityRemaining = 100, MrpPaisa = 18840
        });
        _sales.Setup(s => s.InsertAsync(It.IsAny<SalePosting>())).ReturnsAsync((42L, "INV-2026-00042"));
    }

    private SaleBillingService Service() => new(_sales.Object, _catalog.Object, _customers.Object, _settings.Object, _controlCenter.Object,
        _audit.Object, _sync.Object, _auth.Object, NullLogger<SaleBillingService>.Instance, () => Now);

    private static SalePostingRequest CashBill(params BillLine[] lines) => new()
    {
        InvoiceDate = Now,
        IsCounterSale = true,
        Lines = lines,
        Tenders = new[] { new SaleTender { Mode = TenderMode.Cash, AmountPaisa = 25100 } },
        CashTenderedPaisa = 30000
    };

    [Fact]
    public async Task Save_PostsCashSale_WithChange_AndAudits()
    {
        var posted = await Service().SaveAsync(CashBill(BillingCalculatorTests.Line(packs: 1, loose: 5)));

        posted.InvoiceNumber.Should().Be("INV-2026-00042");
        posted.NetPayablePaisa.Should().Be(25100);
        posted.ChangePaisa.Should().Be(4900);
        _sales.Verify(s => s.InsertAsync(It.Is<SalePosting>(p => p.Payment.AmountPaidPaisa == 25100 && p.Bill.Lines.Count == 1 && !p.IsInterState)), Times.Once);
        _audit.Verify(a => a.LogAsync("CreateSale", "Sale", 42L, null, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Save_UsesDatabaseStock_NotTheScreenFigure()
    {
        _catalog.Setup(c => c.GetBatchAsync(1)).ReturnsAsync(new SaleBatchOption
        {
            BatchId = 1, MedicineId = 1, BatchNumber = "B1", ExpiryDate = Now.AddYears(1), UnitsPerPack = 15, QuantityRemaining = 10
        });
        var act = () => Service().SaveAsync(CashBill(BillingCalculatorTests.Line(packs: 1, loose: 5, available: 1000)));
        (await act.Should().ThrowAsync<BillingValidationException>()).Which.Errors.Should().Contain(e => e.StartsWith("Only"));
        _sales.Verify(s => s.InsertAsync(It.IsAny<SalePosting>()), Times.Never);
    }

    [Fact]
    public async Task Save_H1ItemWithoutRegisterDetails_IsRejected()
    {
        var act = () => Service().SaveAsync(CashBill(BillingCalculatorTests.Line(packs: 1, loose: 5, schedule: DrugSchedule.H1)));
        (await act.Should().ThrowAsync<BillingValidationException>()).Which.Errors.Should().HaveCount(3);
    }

    [Fact]
    public async Task Save_CounterSaleOnCredit_IsRejected()
    {
        var act = () => Service().SaveAsync(CashBill(BillingCalculatorTests.Line(packs: 1, loose: 5)) with { Tenders = Array.Empty<SaleTender>(), IsCredit = true });
        await act.Should().ThrowAsync<BillingValidationException>();
    }

    [Fact]
    public async Task Save_InterStateCustomer_UsesIgst()
    {
        _customers.Setup(c => c.GetByIdAsync(5)).ReturnsAsync(new BillingCustomer { Id = 5, Name = "Goa Clinic", Gstin = "30ABCDE1234F1Z5", Kind = CustomerKind.Business });
        var request = CashBill(BillingCalculatorTests.Line(packs: 1, loose: 5)) with { IsCounterSale = false, CustomerId = 5 };
        await Service().SaveAsync(request);
        _sales.Verify(s => s.InsertAsync(It.Is<SalePosting>(p => p.IsInterState && p.Bill.Totals.IgstPaisa > 0 && p.Request.CustomerGstin == "30ABCDE1234F1Z5")));
    }

    [Fact]
    public async Task Edit_IsBlocked_WhenTheSaleHasReturns()
    {
        _sales.Setup(s => s.GetInvoiceAsync(9)).ReturnsAsync(new BillingInvoice { Id = 9, InvoiceNumber = "INV-9", Status = SaleStatus.Active });
        _sales.Setup(s => s.HasReturnsAsync(9)).ReturnsAsync(true);
        var act = () => Service().SaveAsync(CashBill(BillingCalculatorTests.Line(packs: 1, loose: 5)) with { EditingSaleId = 9 });
        (await act.Should().ThrowAsync<BillingValidationException>()).Which.Message.Should().Contain("sale returns");
    }

    [Fact]
    public async Task Edit_CountsTheSalesOwnUnitsAsAvailable()
    {
        _catalog.Setup(c => c.GetBatchAsync(1)).ReturnsAsync(new SaleBatchOption
        {
            BatchId = 1, MedicineId = 1, BatchNumber = "B1", ExpiryDate = Now.AddYears(1), UnitsPerPack = 15, QuantityRemaining = 0
        });
        _sales.Setup(s => s.GetInvoiceAsync(9)).ReturnsAsync(new BillingInvoice
        {
            Id = 9, InvoiceNumber = "INV-9", Status = SaleStatus.Active,
            Lines = { new BillingInvoiceLine { MedicineId = 1, BatchId = 1, Quantity = 20, UnitsPerPack = 15 } }
        });
        await Service().SaveAsync(CashBill(BillingCalculatorTests.Line(packs: 1, loose: 5)) with { EditingSaleId = 9 });
        _sales.Verify(s => s.UpdateAsync(9, It.IsAny<SalePosting>()), Times.Once);
        _audit.Verify(a => a.LogAsync("EditSale", "Sale", 9L, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Delete_CancelsActiveSale_AndRefusesCancelledOne()
    {
        _sales.Setup(s => s.GetInvoiceAsync(3)).ReturnsAsync(new BillingInvoice { Id = 3, InvoiceNumber = "INV-3", Status = SaleStatus.Active });
        await Service().DeleteAsync(3, 1, " wrong bill ");
        _sales.Verify(s => s.CancelAsync(3, 1, "wrong bill"), Times.Once);
        _audit.Verify(a => a.LogAsync("DeleteSale", "Sale", 3L, It.IsAny<string?>(), null, "wrong bill"), Times.Once);

        _sales.Setup(s => s.GetInvoiceAsync(4)).ReturnsAsync(new BillingInvoice { Id = 4, InvoiceNumber = "INV-4", Status = SaleStatus.Cancelled });
        await FluentActions.Invoking(() => Service().DeleteAsync(4, 1, "x")).Should().ThrowAsync<BillingValidationException>();
    }

    [Fact]
    public async Task Settings_ReadBillingKeys_AndInvoiceTitleFollowsGstRegistration()
    {
        _settings.Setup(s => s.GetValueAsync(BillingSettings.BatchOrderKey)).ReturnsAsync("1");
        var settings = await Service().GetSettingsAsync();
        settings.BatchOrder.Should().Be(BatchPickOrder.Lifo);
        settings.DraftRetentionDays.Should().Be(7);

        _controlCenter.Setup(c => c.GetBusinessProfileAsync()).ReturnsAsync(new BusinessProfile { GstRegistrationType = GstRegistrationType.Composition });
        _sales.Setup(s => s.GetInvoiceAsync(2)).ReturnsAsync(new BillingInvoice { Id = 2, WithGst = true });
        (await Service().GetInvoiceAsync(2))!.IsTaxInvoice.Should().BeFalse();
    }

    [Fact]
    public async Task Replicate_SwapsUnavailableBatch_AndWarns()
    {
        _sales.Setup(s => s.GetInvoiceAsync(8)).ReturnsAsync(new BillingInvoice
        {
            Id = 8, InvoiceNumber = "INV-8", Status = SaleStatus.Active, IsCounterSale = true,
            Lines = { new BillingInvoiceLine { MedicineId = 1, MedicineName = "Dolo", BatchId = 1, BatchNumber = "OLD", Quantity = 15, Packs = 1, UnitsPerPack = 15 } }
        });
        _catalog.Setup(c => c.GetItemAsync(1)).ReturnsAsync(new SaleCatalogItem { MedicineId = 1, Name = "Dolo", UnitsPerPack = 15 });
        _catalog.Setup(c => c.GetBatchesAsync(1)).ReturnsAsync(new[]
        {
            new SaleBatchOption { BatchId = 1, MedicineId = 1, BatchNumber = "OLD", ExpiryDate = Now.AddDays(-1), UnitsPerPack = 15, QuantityRemaining = 50 },
            new SaleBatchOption { BatchId = 2, MedicineId = 1, BatchNumber = "NEW", ExpiryDate = Now.AddYears(1), UnitsPerPack = 15, QuantityRemaining = 50, MrpPaisa = 3364 }
        });
        var (request, warnings) = await Service().ReplicateAsync(8);
        request.Lines.Should().ContainSingle(l => l.BatchId == 2 && l.Packs == 1);
        warnings.Should().ContainSingle(w => w.Contains("replaced by NEW"));
    }

    // ───────────── Drafts ─────────────

    private readonly Mock<ISaleDraftRepository> _drafts = new();

    private SaleDraftService Drafts() => new(_drafts.Object, _settings.Object, _auth.Object, NullLogger<SaleDraftService>.Instance, () => Now);

    [Fact]
    public async Task Draft_EmptyBillDeletesDraft()
    {
        (await Drafts().SaveAsync(new SalePostingRequest(), 12, 0)).Should().BeNull();
        _drafts.Verify(d => d.DeleteAsync(12), Times.Once);
    }

    [Fact]
    public async Task Draft_NeverStoresPayments()
    {
        string? payload = null;
        _drafts.Setup(d => d.SaveAsync(null, It.IsAny<SaleDraftSummary>(), It.IsAny<string>()))
            .Callback<long?, SaleDraftSummary, string>((_, _, p) => payload = p).ReturnsAsync(5);
        var id = await Drafts().SaveAsync(CashBill(BillingCalculatorTests.Line()), null, 25100);
        id.Should().Be(5);
        JsonSerializer.Deserialize<SalePostingRequest>(payload!)!.Tenders.Should().BeEmpty();
    }

    [Fact]
    public async Task Draft_OlderThanRetention_IsDeletedOnOpen()
    {
        _drafts.Setup(d => d.GetAsync(3)).ReturnsAsync(new SaleDraft(new SaleDraftSummary { Id = 3, UpdatedAt = Now.AddDays(-8) }, "{}"));
        (await Drafts().LoadAsync(3)).Should().BeNull();
        _drafts.Verify(d => d.DeleteAsync(3), Times.Once);
    }

    [Fact]
    public async Task Draft_ListPurgesOldOnes()
    {
        _drafts.Setup(d => d.ListAsync(It.IsAny<DateTime>())).ReturnsAsync(Array.Empty<SaleDraftSummary>());
        await Drafts().ListAsync();
        _drafts.Verify(d => d.PurgeAsync(Now.AddDays(-7)), Times.Once);
    }

    // ───────────── Customers ─────────────

    [Fact]
    public async Task Customer_DuplicateMobile_IsRejected()
    {
        _customers.Setup(c => c.FindByPhoneAsync("9876543210")).ReturnsAsync(new BillingCustomer { Id = 1, Name = "Asha" });
        var service = new BillingCustomerService(_customers.Object, _audit.Object, NullLogger<BillingCustomerService>.Instance);
        var act = () => service.SaveAsync(new BillingCustomer { Name = "Ravi", Phone = "9876543210" });
        (await act.Should().ThrowAsync<BillingValidationException>()).Which.Message.Should().Contain("already belongs to customer 'Asha'");
    }

    [Fact]
    public async Task Customer_WithBalance_CannotBeDeleted()
    {
        _customers.Setup(c => c.GetByIdAsync(2)).ReturnsAsync(new BillingCustomer { Id = 2, Name = "Ravi", OutstandingBalancePaisa = 5000 });
        var service = new BillingCustomerService(_customers.Object, _audit.Object, NullLogger<BillingCustomerService>.Instance);
        await FluentActions.Invoking(() => service.DeactivateAsync(2)).Should().ThrowAsync<BillingValidationException>();
        _customers.Verify(c => c.DeactivateAsync(2), Times.Never);
    }
}
