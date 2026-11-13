using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.SalesReturns;
using Xunit;

namespace MedicalShopManager.UnitTests.SalesReturns;

/// <summary>
/// SaleReturnCalculator: pro-rata credit of bill lines, walk-in pricing, disposition, settlement and validation rules.
/// Golden vector (spec 02 / PricingCalculatorTests): 1 pack + 5 loose of a 15's strip at ₹188.40 = ₹251.20, GST 12 %,
/// taxable ₹224.29, bill rounded to ₹251.00.
/// </summary>
public class SalesReturnsCalculatorTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    private static ReturnableSaleLine GoldenLine(long returnedUnits = 0, long returnedTotal = 0, long returnedTaxable = 0, long free = 0) => new()
    {
        SaleItemId = 11,
        MedicineId = 1,
        MedicineName = "Dolo 650",
        BatchId = 5,
        BatchNumber = "B-DOL",
        ExpiryDate = new DateTime(2027, 12, 31),
        UnitsPerPack = 15,
        SoldUnits = 20 + free,
        FreeUnits = free,
        ReturnedUnits = returnedUnits,
        RatePerPackPaisa = 18840,
        MrpPerPackPaisa = 18840,
        GstPercent = 12m,
        LineTotalPaisa = 25120,
        TaxablePaisa = 22429,
        ReturnedTotalPaisa = returnedTotal,
        ReturnedTaxablePaisa = returnedTaxable
    };

    private static ReturnableSale GoldenSale(long paid = 25100, bool counter = true, long previouslyReturned = 0, long previouslyRefunded = 0,
        ReturnableSaleLine? line = null) => new()
    {
        SaleId = 7,
        InvoiceNumber = "INV-2026-00007",
        InvoiceDate = new DateTime(2026, 11, 1, 10, 0, 0),
        IsCounterSale = counter,
        CustomerId = counter ? null : 3,
        CustomerName = counter ? "Cash / Counter Sale" : "Ramesh",
        GrandTotalPaisa = 25100,
        AmountPaidPaisa = paid,
        BalanceDuePaisa = 25100 - paid,
        PreviouslyReturnedPaisa = previouslyReturned,
        PreviouslyRefundedPaisa = previouslyRefunded,
        Lines = { line ?? GoldenLine() }
    };

    private static SaleReturnRequest BillRequest(long units, SaleRefundMode mode = SaleRefundMode.Cash,
        SaleReturnDisposition disposition = SaleReturnDisposition.Restock) => new()
    {
        SaleId = 7,
        ReturnDate = Today,
        RefundMode = mode,
        Lines = new[] { new SaleReturnLineRequest { SaleItemId = 11, MedicineId = 1, BatchId = 5, Units = units, Disposition = disposition } }
    };

    private static readonly IReadOnlyDictionary<long, SaleReturnBatchInfo> NoBatches = new Dictionary<long, SaleReturnBatchInfo>();

    // ───────────────────────── Bill lines ─────────────────────────

    [Fact]
    public void AgainstBillLine_FullLine_CreditsExactlyWhatWasCharged()
    {
        var a = SaleReturnCalculator.AgainstBillLine(GoldenLine(), 20, isInterState: false);

        a.TotalPaisa.Should().Be(25120);
        a.TaxablePaisa.Should().Be(22429);
        a.TaxPaisa.Should().Be(2691);
        (a.CgstPaisa + a.SgstPaisa).Should().Be(2691);
        a.IgstPaisa.Should().Be(0);
    }

    [Fact]
    public void AgainstBillLine_PartialReturn_IsProRataToUnitsSold()
    {
        // 5 of 20 units: 25120 × 5 / 20 = 6280; taxable round(6280 × 100 / 112) = 5607; CGST round(673 / 2) = 337.
        var a = SaleReturnCalculator.AgainstBillLine(GoldenLine(), 5, isInterState: false);

        a.TotalPaisa.Should().Be(6280);
        a.TaxablePaisa.Should().Be(5607);
        a.TaxPaisa.Should().Be(673);
        a.CgstPaisa.Should().Be(337);
        a.SgstPaisa.Should().Be(336);
        a.GrossPaisa.Should().Be(6280); // 18840 × 5 / 15
    }

    [Fact]
    public void AgainstBillLine_SeveralPartialReturns_NeverCreditMoreThanTheLine()
    {
        var first = SaleReturnCalculator.AgainstBillLine(GoldenLine(), 5, false);
        var second = SaleReturnCalculator.AgainstBillLine(GoldenLine(5, first.TotalPaisa, first.TaxablePaisa), 5, false);
        var last = SaleReturnCalculator.AgainstBillLine(
            GoldenLine(10, first.TotalPaisa + second.TotalPaisa, first.TaxablePaisa + second.TaxablePaisa), 10, false);

        (first.TotalPaisa + second.TotalPaisa + last.TotalPaisa).Should().Be(25120);
        (first.TaxablePaisa + second.TaxablePaisa + last.TaxablePaisa).Should().Be(22429);
        last.TaxablePaisa.Should().Be(11215);
    }

    [Fact]
    public void AgainstBillLine_WithFreeUnits_CreditsTheEffectivePricePaid()
    {
        // 20 paid + 4 free for ₹251.20: 6 units back = 25120 × 6 / 24 = 6280.
        var a = SaleReturnCalculator.AgainstBillLine(GoldenLine(free: 4), 6, false);
        a.TotalPaisa.Should().Be(6280);
    }

    [Fact]
    public void AgainstBillLine_InterState_IsAllIgst()
    {
        var a = SaleReturnCalculator.AgainstBillLine(GoldenLine(), 5, isInterState: true);
        a.IgstPaisa.Should().Be(673);
        (a.CgstPaisa + a.SgstPaisa).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void AgainstBillLine_RejectsUnitsOutsideWhatIsReturnable(long units)
    {
        var act = () => SaleReturnCalculator.AgainstBillLine(GoldenLine(), units, false);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ───────────────────────── Walk-in lines ─────────────────────────

    private static SaleReturnBatchInfo Batch(DateTime? expiry = null) => new()
    {
        BatchId = 5, MedicineId = 1, MedicineName = "Dolo 650", BatchNumber = "B-DOL", ExpiryDate = expiry ?? new DateTime(2027, 12, 31),
        UnitsPerPack = 15, MrpPaisa = 18840, SaleRatePaisa = 18840, GstPercent = 12m, HsnCode = "30049099"
    };

    [Fact]
    public void WalkInLine_IsPricedLikeASaleLine()
    {
        // 1 pack at ₹188.40 less 10 % = round(18840 × 0.9) = 16956.
        var a = SaleReturnCalculator.WalkInLine(Batch(), 15, 18840, 10m);
        a.TotalPaisa.Should().Be(16956);
        (a.TaxablePaisa + a.TaxPaisa).Should().Be(16956);
        a.IgstPaisa.Should().Be(0);
    }

    [Fact]
    public void Build_WalkIn_ComputesAndValidatesRateAgainstMrp()
    {
        var batches = new Dictionary<long, SaleReturnBatchInfo> { [5] = Batch() };
        var request = new SaleReturnRequest
        {
            ReturnDate = Today,
            Lines = new[] { new SaleReturnLineRequest { MedicineId = 1, BatchId = 5, Units = 15, RatePerPackPaisa = 20000 } }
        };

        var (_, errors) = SaleReturnCalculator.Build(request, null, batches, RoundOffMode.NearestRupee, Today);
        errors.Should().ContainSingle(e => e.Contains("more than the MRP"));

        var (ok, okErrors) = SaleReturnCalculator.Build(request with
        {
            Lines = new[] { new SaleReturnLineRequest { MedicineId = 1, BatchId = 5, Units = 15, RatePerPackPaisa = 18840 } }
        }, null, batches, RoundOffMode.NearestRupee, Today);
        okErrors.Should().BeEmpty();
        ok.NetAmountPaisa.Should().Be(18800);
        ok.RoundOffPaisa.Should().Be(-40);
        ok.SaleId.Should().BeNull();
        ok.Lines.Single().SaleItemId.Should().BeNull();
    }

    [Fact]
    public void Build_WalkIn_RejectsUnknownOrMismatchedBatch_MissingRate_AndBadDiscount()
    {
        var batches = new Dictionary<long, SaleReturnBatchInfo> { [5] = Batch() };
        var request = new SaleReturnRequest
        {
            ReturnDate = Today,
            Lines = new[]
            {
                new SaleReturnLineRequest { MedicineId = 2, BatchId = 5, Units = 1, RatePerPackPaisa = 100 },
                new SaleReturnLineRequest { MedicineId = 1, BatchId = 9, Units = 1, RatePerPackPaisa = 100 }
            }
        };
        SaleReturnCalculator.Build(request, null, batches, RoundOffMode.None, Today).Errors
            .Should().HaveCount(2).And.OnlyContain(e => e.Contains("not found"));

        var noRate = request with { Lines = new[] { new SaleReturnLineRequest { MedicineId = 1, BatchId = 5, Units = 1 } } };
        SaleReturnCalculator.Build(noRate, null, batches, RoundOffMode.None, Today).Errors.Should().ContainSingle(e => e.Contains("refund rate"));

        var badDiscount = request with
        {
            Lines = new[] { new SaleReturnLineRequest { MedicineId = 1, BatchId = 5, Units = 1, RatePerPackPaisa = 100, DiscountPercent = 120m } }
        };
        SaleReturnCalculator.Build(badDiscount, null, batches, RoundOffMode.None, Today).Errors.Should().ContainSingle(e => e.Contains("discount"));
    }

    [Fact]
    public void Build_WalkIn_CreditNeedsASavedCustomer()
    {
        var batches = new Dictionary<long, SaleReturnBatchInfo> { [5] = Batch() };
        var request = new SaleReturnRequest
        {
            ReturnDate = Today,
            RefundMode = SaleRefundMode.CreditToCustomer,
            Lines = new[] { new SaleReturnLineRequest { MedicineId = 1, BatchId = 5, Units = 15, RatePerPackPaisa = 18840 } }
        };
        SaleReturnCalculator.Build(request, null, batches, RoundOffMode.None, Today).Errors.Should().ContainSingle(e => e.Contains("saved customer"));
        SaleReturnCalculator.Build(request with { CustomerId = 3, CustomerName = "Ramesh" }, null, batches, RoundOffMode.None, Today).Errors
            .Should().BeEmpty();
    }

    // ───────────────────────── Disposition ─────────────────────────

    [Theory]
    [InlineData(SaleReturnDisposition.Restock, "2027-01-31", SaleReturnDisposition.Restock)]
    [InlineData(SaleReturnDisposition.Restock, "2026-11-05", SaleReturnDisposition.ExpiredStock)]
    [InlineData(SaleReturnDisposition.Restock, "2026-10-31", SaleReturnDisposition.ExpiredStock)]
    [InlineData(SaleReturnDisposition.Damaged, "2026-10-31", SaleReturnDisposition.Damaged)]
    [InlineData(SaleReturnDisposition.Damaged, "2027-01-31", SaleReturnDisposition.Damaged)]
    public void ResolveDisposition_ExpiredBatchGoesToExpiredStock_DamagedIsKept(SaleReturnDisposition requested, string expiry,
        SaleReturnDisposition expected)
    {
        SaleReturnCalculator.ResolveDisposition(requested, DateTime.Parse(expiry), Today).Should().Be(expected);
    }

    [Fact]
    public void Build_ExpiredBatchLine_IsMarkedExpiredStock()
    {
        var line = GoldenLine();
        line.ExpiryDate = new DateTime(2026, 10, 31);
        var (doc, errors) = SaleReturnCalculator.Build(BillRequest(5), GoldenSale(line: line), NoBatches, RoundOffMode.NearestRupee, Today);
        errors.Should().BeEmpty();
        doc.Lines.Single().Disposition.Should().Be(SaleReturnDisposition.ExpiredStock);
        doc.Lines.Single().GoesToStock.Should().BeTrue();
    }

    // ───────────────────────── Bill returns: totals and rules ─────────────────────────

    [Fact]
    public void Build_FullReturn_MatchesTheRoundedBill()
    {
        var (doc, errors) = SaleReturnCalculator.Build(BillRequest(20), GoldenSale(), NoBatches, RoundOffMode.NearestRupee, Today);

        errors.Should().BeEmpty();
        doc.NetAmountPaisa.Should().Be(25100);
        doc.RoundOffPaisa.Should().Be(-20);
        doc.TaxablePaisa.Should().Be(22429);
        doc.TaxPaisa.Should().Be(2691);
        doc.SaleInvoiceNumber.Should().Be("INV-2026-00007");
        doc.Lines.Single().Disposition.Should().Be(SaleReturnDisposition.Restock);
    }

    [Fact]
    public void Build_SecondPartialReturn_IsCappedAtWhatIsLeftOfTheBill()
    {
        // First return of 10 units credited 12560 → 12600 after rounding. The rest (12560 → 12600) would credit 25200
        // in total against a ₹251.00 bill, so the second one is capped at 12500 with the difference in round-off.
        var line = GoldenLine(10, 12560, 11214);
        var sale = GoldenSale(previouslyReturned: 12600, previouslyRefunded: 12600, line: line);

        var (doc, errors) = SaleReturnCalculator.Build(BillRequest(10), sale, NoBatches, RoundOffMode.NearestRupee, Today);

        errors.Should().BeEmpty();
        doc.NetAmountPaisa.Should().Be(12500);
        doc.RoundOffPaisa.Should().Be(-60);
    }

    [Fact]
    public void Build_CustomChargeIsNotRefunded()
    {
        var sale = GoldenSale();
        sale.GrandTotalPaisa = 30100; // ₹50 delivery fee on top of the medicines
        sale.CustomChargePaisa = 5000;
        sale.AmountPaidPaisa = 30100;
        var (doc, _) = SaleReturnCalculator.Build(BillRequest(20), sale, NoBatches, RoundOffMode.NearestRupee, Today);
        doc.NetAmountPaisa.Should().Be(25100);
        SaleReturnCalculator.MaxCreditValue(sale).Should().Be(25100);
    }

    [Fact]
    public void Build_RejectsMoreThanReturnable_WithAClearMessage()
    {
        var sale = GoldenSale(line: GoldenLine(15, 18840, 16821));
        var (_, errors) = SaleReturnCalculator.Build(BillRequest(6), sale, NoBatches, RoundOffMode.NearestRupee, Today);
        errors.Should().ContainSingle().Which.Should().Contain("only 0 + 5 can be returned").And.Contain("already returned 1");
    }

    [Fact]
    public void Build_FullyReturnedLine_SaysSo()
    {
        var sale = GoldenSale(line: GoldenLine(20, 25120, 22429));
        SaleReturnCalculator.Build(BillRequest(1), sale, NoBatches, RoundOffMode.None, Today).Errors
            .Should().ContainSingle(e => e.Contains("already been returned in full"));
        sale.IsFullyReturned.Should().BeTrue();
    }

    [Fact]
    public void Build_NoQuantities_AndNegativeQuantities_AreRejected()
    {
        SaleReturnCalculator.Build(BillRequest(0), GoldenSale(), NoBatches, RoundOffMode.None, Today).Errors
            .Should().ContainSingle(e => e.Contains("at least one item"));
        SaleReturnCalculator.Build(BillRequest(-2), GoldenSale(), NoBatches, RoundOffMode.None, Today).Errors
            .Should().Contain(e => e.Contains("negative"));
    }

    [Fact]
    public void Build_DuplicateAndForeignLines_AreRejected()
    {
        var request = BillRequest(1) with
        {
            Lines = new[]
            {
                new SaleReturnLineRequest { SaleItemId = 11, MedicineId = 1, BatchId = 5, Units = 1 },
                new SaleReturnLineRequest { SaleItemId = 11, MedicineId = 1, BatchId = 5, Units = 1 },
                new SaleReturnLineRequest { SaleItemId = 99, MedicineId = 1, BatchId = 5, Units = 1 }
            }
        };
        var errors = SaleReturnCalculator.Build(request, GoldenSale(), NoBatches, RoundOffMode.None, Today).Errors;
        errors.Should().Contain(e => e.Contains("listed twice")).And.Contain(e => e.Contains("not on invoice"));
    }

    [Fact]
    public void Build_Dates_MustBeBetweenTheInvoiceAndToday()
    {
        SaleReturnCalculator.Build(BillRequest(1) with { ReturnDate = new DateTime(2026, 10, 30) }, GoldenSale(), NoBatches, RoundOffMode.None, Today)
            .Errors.Should().ContainSingle(e => e.Contains("before the invoice date"));
        SaleReturnCalculator.Build(BillRequest(1) with { ReturnDate = Today.AddDays(1) }, GoldenSale(), NoBatches, RoundOffMode.None, Today)
            .Errors.Should().ContainSingle(e => e.Contains("future"));
    }

    [Fact]
    public void Build_CancelledOrMissingBill_IsRejected()
    {
        var sale = GoldenSale();
        sale.IsCancelled = true;
        SaleReturnCalculator.Build(BillRequest(1), sale, NoBatches, RoundOffMode.None, Today).Errors.Should().Contain(e => e.Contains("cancelled"));
        SaleReturnCalculator.Build(BillRequest(1), null, NoBatches, RoundOffMode.None, Today).Errors.Should().ContainSingle(e => e.Contains("not found"));
    }

    [Fact]
    public void Build_CreditOnACounterSale_IsRejected_ButAllowedForACustomerBill()
    {
        SaleReturnCalculator.Build(BillRequest(5, SaleRefundMode.CreditToCustomer), GoldenSale(), NoBatches, RoundOffMode.None, Today).Errors
            .Should().ContainSingle(e => e.Contains("counter sale"));
        var (doc, errors) = SaleReturnCalculator.Build(BillRequest(5, SaleRefundMode.CreditToCustomer), GoldenSale(counter: false),
            NoBatches, RoundOffMode.None, Today);
        errors.Should().BeEmpty();
        doc.CustomerId.Should().Be(3);
    }

    [Fact]
    public void Build_RefundNow_IsCappedAtWhatWasPaid()
    {
        // Credit bill: ₹100 paid of ₹251. Returning everything cannot pay back more than the ₹100 received.
        var sale = GoldenSale(paid: 10000, counter: false);
        var errors = SaleReturnCalculator.Build(BillRequest(20), sale, NoBatches, RoundOffMode.NearestRupee, Today).Errors;
        errors.Should().ContainSingle(e => e.Contains("at most ₹100.00 can be refunded now"));

        SaleReturnCalculator.Build(BillRequest(20, SaleRefundMode.CreditToCustomer), sale, NoBatches, RoundOffMode.NearestRupee, Today).Errors
            .Should().BeEmpty();
        SaleReturnCalculator.MaxRefundNow(GoldenSale(previouslyRefunded: 5000)).Should().Be(20100);
        SaleReturnCalculator.MaxRefundNow(null).Should().Be(long.MaxValue);
    }

    [Fact]
    public void Build_RefundMethodMustBeAPaymentMethod_AndTextsAreBounded()
    {
        var request = BillRequest(1) with { RefundMethod = PaymentMethod.Credit, Notes = new string('x', 301), Reason = new string('r', 121) };
        var errors = SaleReturnCalculator.Build(request, GoldenSale(), NoBatches, RoundOffMode.None, Today).Errors;
        errors.Should().Contain(e => e.Contains("cash, UPI, card or bank"))
            .And.Contain(e => e.Contains("notes"))
            .And.Contain(e => e.Contains("reason"));
    }

    // ───────────────────────── Refund and cancel rules ─────────────────────────

    private static SaleReturnDocument Note(long net = 25100, long refunded = 0, SaleReturnStatus status = SaleReturnStatus.Active) => new()
    {
        Id = 1, CreditNoteNumber = "CN/2627/00001", ReturnNumber = "SR/2627/00001", ReturnDate = new DateTime(2026, 11, 2),
        NetAmountPaisa = net, RefundedPaisa = refunded, Status = status
    };

    [Fact]
    public void ValidateRefund_AllowsUpToTheOpenCredit()
    {
        var refund = new CreditRefundRequest { SaleReturnId = 1, AmountPaisa = 15100, RefundDate = Today };
        SaleReturnCalculator.ValidateRefund(Note(refunded: 10000), refund, Today).Should().BeEmpty();
        SaleReturnCalculator.ValidateRefund(Note(refunded: 10001), refund, Today).Should().ContainSingle(e => e.Contains("Only ₹150.99"));
        SaleReturnCalculator.ValidateRefund(Note(), refund with { AmountPaisa = 0 }, Today).Should().ContainSingle(e => e.Contains("Enter the amount"));
        SaleReturnCalculator.ValidateRefund(Note(status: SaleReturnStatus.Cancelled), refund, Today).Should().Contain(e => e.Contains("cancelled"));
        SaleReturnCalculator.ValidateRefund(Note(), refund with { Method = PaymentMethod.Credit }, Today).Should().ContainSingle();
        SaleReturnCalculator.ValidateRefund(Note(), refund with { RefundDate = Today.AddDays(1) }, Today).Should().ContainSingle(e => e.Contains("future"));
        SaleReturnCalculator.ValidateRefund(Note(), refund with { RefundDate = new DateTime(2026, 11, 1) }, Today)
            .Should().ContainSingle(e => e.Contains("before the return date"));
    }

    [Fact]
    public void ValidateCancel_NeedsAReason_AndAnActiveReturn()
    {
        SaleReturnCalculator.ValidateCancel(Note(), "Entered by mistake").Should().BeEmpty();
        SaleReturnCalculator.ValidateCancel(Note(), " ").Should().ContainSingle();
        SaleReturnCalculator.ValidateCancel(Note(status: SaleReturnStatus.Cancelled), "Duplicate").Should().ContainSingle(e => e.Contains("already"));
    }

    [Fact]
    public void Document_CreditBalance_IsNetLessRefunds_AndZeroWhenCancelled()
    {
        Note(refunded: 10000).CreditBalancePaisa.Should().Be(15100);
        Note(refunded: 30000).CreditBalancePaisa.Should().Be(0);
        Note(status: SaleReturnStatus.Cancelled).CreditBalancePaisa.Should().Be(0);
    }

    [Fact]
    public void ReturnTotals_ExcludeCancelledReturns()
    {
        var rows = new[]
        {
            new SaleReturnRow { Id = 1, TaxablePaisa = 100, TaxPaisa = 12, NetAmountPaisa = 112, RefundedPaisa = 112 },
            new SaleReturnRow { Id = 2, TaxablePaisa = 200, TaxPaisa = 24, NetAmountPaisa = 224, RefundedPaisa = 24, RefundMode = SaleRefundMode.CreditToCustomer },
            new SaleReturnRow { Id = 3, TaxablePaisa = 999, TaxPaisa = 99, NetAmountPaisa = 1098, Status = SaleReturnStatus.Cancelled }
        };
        var totals = SaleReturnTotals.From(rows);
        totals.Count.Should().Be(2);
        totals.NetAmountPaisa.Should().Be(336);
        totals.TaxPaisa.Should().Be(36);
        totals.RefundedPaisa.Should().Be(136);
        totals.OpenCreditPaisa.Should().Be(200);
        rows[1].StatusLabel.Should().Be("Open credit");
        rows[0].StatusLabel.Should().Be("Settled");
        rows[2].StatusLabel.Should().Be("Cancelled");
    }
}
