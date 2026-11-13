using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.SalesReturns;
using MedicalShopManager.Infrastructure.Modules.SalesReturns;
using Xunit;

namespace MedicalShopManager.IntegrationTests.SalesReturns;

/// <summary>Sales register queries (SalesRegisterRepository / SalesRegisterService) over Billing-shaped sales.</summary>
public class SalesReturnsRegisterTests : SalesReturnsDbTestBase
{
    private SalesRegisterService Register() => new(new SalesRegisterRepository(Db));

    private async Task<(long Cash, long Credit, long Cancelled, long Old, long CustomerId, long BatchId)> SeedAsync()
    {
        var customerId = await SeedCustomerAsync();
        var (medicineId, batchId) = await SeedItemAsync(units: 500);
        var (cash, _) = await SeedSaleAsync(medicineId, batchId, "INV-2026-00010");
        var (credit, _) = await SeedSaleAsync(medicineId, batchId, "INV-2026-00011", customerId: customerId, paidPaisa: 5000, method: PaymentMethod.Credit);
        var (cancelled, _) = await SeedSaleAsync(medicineId, batchId, "INV-2026-00012", method: PaymentMethod.UPI, status: 2);
        var (old, _) = await SeedSaleAsync(medicineId, batchId, "INV-2025-00001", date: Today.AddDays(-90));
        return (cash, credit, cancelled, old, customerId, batchId);
    }

    [Fact]
    public async Task Query_DateRange_ReturnsBillsWithItemsAndTotals_ExcludingCancelledFromTotals()
    {
        await SeedAsync();

        var (rows, totals) = await Register().QueryAsync(new SalesRegisterFilter { From = Today.AddDays(-7), To = Today });

        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.ItemCount == 1);
        totals.BillCount.Should().Be(2);
        totals.CancelledCount.Should().Be(1);
        totals.GrandTotalPaisa.Should().Be(50200);
        totals.PaidPaisa.Should().Be(30100);
        totals.DuePaisa.Should().Be(20100);
        totals.TaxPaisa.Should().Be(5382);
    }

    [Theory]
    [InlineData(SalesRegisterStatus.Paid, "INV-2026-00010")]
    [InlineData(SalesRegisterStatus.Partial, "INV-2026-00011")]
    [InlineData(SalesRegisterStatus.Cancelled, "INV-2026-00012")]
    public async Task Query_StatusFilter(SalesRegisterStatus status, string expected)
    {
        await SeedAsync();
        var (rows, _) = await Register().QueryAsync(new SalesRegisterFilter { From = Today.AddDays(-7), To = Today, Status = status });
        rows.Should().ContainSingle().Which.InvoiceNumber.Should().Be(expected);
    }

    [Fact]
    public async Task Query_ModeCustomerAndSearch()
    {
        var ids = await SeedAsync();
        var range = new SalesRegisterFilter { From = Today.AddDays(-7), To = Today };

        (await Register().QueryAsync(range with { Mode = PaymentMethod.UPI })).Rows.Should().ContainSingle().Which.IsCancelled.Should().BeTrue();
        (await Register().QueryAsync(range with { CustomerId = ids.CustomerId })).Rows.Should().ContainSingle().Which.SaleId.Should().Be(ids.Credit);
        (await Register().QueryAsync(range with { SearchText = "98765" })).Rows.Should().ContainSingle().Which.CustomerDisplay.Should().Be("Ramesh Kumar");
        // Searching by bill number looks across all dates.
        (await Register().QueryAsync(range with { SearchText = "2025-0000" })).Rows.Should().ContainSingle().Which.SaleId.Should().Be(ids.Old);
        // LIKE wildcards typed by the user are literal.
        (await Register().QueryAsync(range with { SearchText = "%" })).Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_ShowsActiveReturnsPerBill()
    {
        var ids = await SeedAsync();
        var sale = (await Service().GetReturnableSaleAsync(ids.Cash))!;
        await Service().PostAsync(new SaleReturnRequest
        {
            SaleId = ids.Cash, ReturnDate = Today,
            Lines = new[] { new SaleReturnLineRequest { SaleItemId = sale.Lines[0].SaleItemId, BatchId = ids.BatchId, Units = 5 } }
        });

        var (rows, totals) = await Register().QueryAsync(new SalesRegisterFilter { From = Today.AddDays(-7), To = Today });

        rows.Single(r => r.SaleId == ids.Cash).ReturnedPaisa.Should().Be(6300);
        totals.ReturnedPaisa.Should().Be(6300);
        totals.NetSalesPaisa.Should().Be(50200 - 6300);
    }

    [Fact]
    public async Task SearchBills_FindsActiveBillsOnly_ExactNumberFirst()
    {
        await SeedAsync();
        var register = Register();

        (await register.SearchBillsAsync("INV-2026-0001")).Select(r => r.InvoiceNumber).Should().BeEquivalentTo("INV-2026-00010", "INV-2026-00011");
        (await register.SearchBillsAsync("INV-2026-00011")).First().InvoiceNumber.Should().Be("INV-2026-00011");
        (await register.SearchBillsAsync("INV-2026-00012")).Should().BeEmpty();
        (await register.SearchBillsAsync("  ")).Should().BeEmpty();
    }

    [Fact]
    public async Task Query_ReversedDates_IsAValidationError()
    {
        await FluentActions.Invoking(() => Register().QueryAsync(new SalesRegisterFilter { From = Today, To = Today.AddDays(-1) }))
            .Should().ThrowAsync<SaleReturnValidationException>();
    }
}
