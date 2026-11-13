using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using Xunit;

namespace MedicalShopManager.UnitTests.Purchases;

/// <summary>Supplier statement running balance, pack ↔ unit conversion, register summary and GST state codes.</summary>
public class PurchasesStatementAndHelpersTests
{
    private static readonly DateTime D = new(2026, 6, 1);

    [Fact]
    public void Statement_OpeningIncludesEarlierMovements_AndRunsTheBalance()
    {
        var supplier = new Distributor { Id = 1, Name = "Alpha", OpeningBalancePaisa = 10000 };
        var entries = new List<SupplierLedgerEntry>
        {
            new(D.AddDays(-10), SupplierStatementEntryType.Purchase, "P1", "bill 1", 0, 50000, 1),
            new(D.AddDays(-5), SupplierStatementEntryType.Payment, "P1", "paid", 20000, 0, 2),
            new(D.AddDays(2), SupplierStatementEntryType.Purchase, "P2", "bill 2", 0, 30000, 1),
            new(D.AddDays(3), SupplierStatementEntryType.DebitNote, "DN1", "return", 5000, 0, 3),
            new(D.AddDays(3), SupplierStatementEntryType.RefundReceived, "DN1", "refund", 0, 2000, 4),
            new(D.AddDays(40), SupplierStatementEntryType.Purchase, "P3", "after period", 0, 99900, 1)
        };

        var statement = SupplierStatementBuilder.Build(supplier, entries, D, D.AddDays(30));

        statement.OpeningBalancePaisa.Should().Be(40000); // 100 + 500 − 200
        statement.Lines.Should().HaveCount(4);
        statement.Lines.Select(l => l.BalancePaisa).Should().Equal(40000, 70000, 65000, 67000);
        statement.ClosingBalancePaisa.Should().Be(67000);
        statement.TotalCreditPaisa.Should().Be(32000);
        statement.TotalDebitPaisa.Should().Be(5000);
    }

    [Fact]
    public void Statement_SameDay_OrdersBillBeforePaymentBeforeDebitNote()
    {
        var supplier = new Distributor { Name = "Alpha" };
        var entries = new List<SupplierLedgerEntry>
        {
            new(D, SupplierStatementEntryType.DebitNote, "DN", "", 100, 0, 3),
            new(D, SupplierStatementEntryType.Payment, "P", "", 200, 0, 2),
            new(D, SupplierStatementEntryType.Purchase, "P", "", 0, 1000, 1)
        };

        var statement = SupplierStatementBuilder.Build(supplier, entries, D, D);

        statement.Lines.Skip(1).Select(l => l.Type).Should().Equal(SupplierStatementEntryType.Purchase, SupplierStatementEntryType.Payment, SupplierStatementEntryType.DebitNote);
    }

    [Fact]
    public void Statement_ReversedDates_AreRejected() =>
        Assert.Throws<PurchaseRuleException>(() => SupplierStatementBuilder.Build(new Distributor(), Array.Empty<SupplierLedgerEntry>(), D, D.AddDays(-1)));

    [Theory]
    [InlineData(2, 15, 30)]
    [InlineData(1.5, 10, 15)]
    [InlineData(0, 10, 0)]
    public void UnitsFromPacks_WholeUnits(double packs, int pack, int expected) =>
        PurchaseCalculator.UnitsFromPacks((decimal)packs, pack).Should().Be(expected);

    [Fact]
    public void UnitsFromPacks_FractionalUnitsOrNegative_AreNull()
    {
        PurchaseCalculator.UnitsFromPacks(0.3m, 10).Should().Be(3);
        PurchaseCalculator.UnitsFromPacks(0.33m, 10).Should().BeNull();
        PurchaseCalculator.UnitsFromPacks(-1m, 10).Should().BeNull();
        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseCalculator.UnitsFromPacks(1m, 0));
    }

    [Fact]
    public void PacksFromUnits_RoundTripsRepeatingDecimals()
    {
        var packs = PurchaseCalculator.PacksFromUnits(7, 15); // 0.4666…
        PurchaseCalculator.UnitsFromPacks(packs, 15).Should().Be(7);
    }

    [Fact]
    public void RegisterReport_TotalsConfirmedBillsOnly()
    {
        var rows = new List<PurchaseListRow>
        {
            new() { Status = PurchaseStatus.Confirmed, GrandTotalPaisa = 1000, TaxablePaisa = 900, TaxAmountPaisa = 100, AmountPaidPaisa = 400, BalancePaisa = 600 },
            new() { Status = PurchaseStatus.Draft, GrandTotalPaisa = 5000 },
            new() { Status = PurchaseStatus.Cancelled, GrandTotalPaisa = 7000 }
        };

        var report = PurchaseRegisterReport.Create(new PurchaseListFilter(D, D.AddDays(30), Status: PurchaseListStatusFilter.All), "Alpha", rows);

        report.Summary.Count.Should().Be(1);
        report.Summary.GrandTotalPaisa.Should().Be(1000);
        report.Summary.BalancePaisa.Should().Be(600);
        report.PeriodLabel.Should().Be("01-06-2026 to 01-07-2026");
        report.StatusLabel.Should().Be("All");
    }

    [Fact]
    public void ListRow_StatusLabel()
    {
        new PurchaseListRow { Status = PurchaseStatus.Draft }.StatusLabel.Should().Be("Draft");
        new PurchaseListRow { Status = PurchaseStatus.Confirmed, PaymentStatus = PaymentStatus.Unpaid, BalancePaisa = 10, DueDate = DateTime.Today.AddDays(-1) }
            .StatusLabel.Should().Be("Overdue");
        new PurchaseListRow { Status = PurchaseStatus.Confirmed, PaymentStatus = PaymentStatus.Unpaid, BalancePaisa = 10, DueDate = DateTime.Today.AddDays(5) }
            .StatusLabel.Should().Be("Credit");
        new PurchaseListRow { Status = PurchaseStatus.Confirmed, PaymentStatus = PaymentStatus.Paid }.StatusLabel.Should().Be("Paid");
    }

    [Fact]
    public void GstStates_CoverEveryCodeOnce_AndResolveNames()
    {
        GstStateCodes.All.Select(s => s.Code).Should().OnlyHaveUniqueItems();
        GstStateCodes.NameOf("29").Should().Be("Karnataka");
        GstStateCodes.NameOf("27").Should().Be("Maharashtra");
        GstStateCodes.Find(null).Should().BeNull();
        new Distributor { Gstin = "33ABCDE1234F1Z5" }.EffectiveStateCode.Should().Be("33");
    }
}
