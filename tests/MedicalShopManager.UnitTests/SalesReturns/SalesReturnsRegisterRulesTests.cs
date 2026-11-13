using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.SalesReturns;
using Xunit;

namespace MedicalShopManager.UnitTests.SalesReturns;

/// <summary>Sales register status chips, status filter, footer totals and filter validation.</summary>
public class SalesReturnsRegisterRulesTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    private static SalesRegisterRow Row(long total = 10000, long paid = 10000, bool cancelled = false, DateTime? due = null, long returned = 0,
        long tax = 1071) => new()
    {
        SaleId = 1, InvoiceNumber = "INV-1", GrandTotalPaisa = total, AmountPaidPaisa = paid, BalanceDuePaisa = total - paid,
        IsCancelled = cancelled, DueDate = due, ReturnedPaisa = returned, TaxPaisa = tax, PaymentMethod = PaymentMethod.Cash
    };

    [Fact]
    public void StatusLabel_CoversEveryState()
    {
        SalesRegisterRules.StatusLabel(Row(), Today).Should().Be("Paid");
        SalesRegisterRules.StatusLabel(Row(cancelled: true), Today).Should().Be("Cancelled");
        SalesRegisterRules.StatusLabel(Row(paid: 4000), Today).Should().Be("Partly paid");
        SalesRegisterRules.StatusLabel(Row(paid: 0), Today).Should().Be("Unpaid");
        SalesRegisterRules.StatusLabel(Row(paid: 0, due: Today.AddDays(-1)), Today).Should().Be("Overdue");
        SalesRegisterRules.StatusLabel(Row(paid: 0, due: Today), Today).Should().Be("Unpaid");
    }

    [Theory]
    [InlineData(SalesRegisterStatus.All, 4)]
    [InlineData(SalesRegisterStatus.Paid, 1)]
    [InlineData(SalesRegisterStatus.Partial, 1)]
    [InlineData(SalesRegisterStatus.Unpaid, 1)]
    [InlineData(SalesRegisterStatus.Cancelled, 1)]
    public void Matches_FiltersByPaymentStatus(SalesRegisterStatus status, int expected)
    {
        var rows = new[] { Row(), Row(paid: 5000), Row(paid: 0), Row(cancelled: true) };
        rows.Count(r => SalesRegisterRules.Matches(r, status)).Should().Be(expected);
    }

    [Fact]
    public void Summarize_ExcludesCancelledBills_AndNetsOffReturns()
    {
        var totals = SalesRegisterRules.Summarize(new[]
        {
            Row(total: 25100, paid: 25100, returned: 6300),
            Row(total: 10000, paid: 4000),
            Row(total: 99900, paid: 0, cancelled: true)
        });

        totals.BillCount.Should().Be(2);
        totals.CancelledCount.Should().Be(1);
        totals.GrandTotalPaisa.Should().Be(35100);
        totals.PaidPaisa.Should().Be(29100);
        totals.DuePaisa.Should().Be(6000);
        totals.ReturnedPaisa.Should().Be(6300);
        totals.NetSalesPaisa.Should().Be(28800);
        totals.TaxPaisa.Should().Be(2142);
    }

    [Fact]
    public void Validate_RejectsReversedOrHugeRanges()
    {
        SalesRegisterRules.Validate(new SalesRegisterFilter { From = Today, To = Today }).Should().BeNull();
        SalesRegisterRules.Validate(new SalesRegisterFilter { From = Today, To = Today.AddDays(-1) }).Should().Contain("before");
        SalesRegisterRules.Validate(new SalesRegisterFilter { From = Today.AddYears(-6), To = Today }).Should().Contain("5 years");
    }

    [Fact]
    public void Row_DisplayHelpers()
    {
        var counter = Row() with { CustomerName = null };
        counter.CustomerDisplay.Should().Be("Cash / Counter Sale");
        counter.ModeLabel.Should().Be("Cash");
        counter.CanReturn.Should().BeTrue();
        Row(cancelled: true).CanReturn.Should().BeFalse();
        Row(returned: 1).HasReturns.Should().BeTrue();
        SalesReturnsLabels.ModeLabel(PaymentMethod.BankTransfer).Should().Be("Bank");
        SaleRefundMode.CreditToCustomer.ToLabel().Should().Be("Credit to customer");
        SaleReturnDisposition.ExpiredStock.ToLabel().Should().Be("Expired stock");
    }
}
