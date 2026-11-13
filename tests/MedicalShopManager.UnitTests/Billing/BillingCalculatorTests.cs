using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Pricing;
using Xunit;

namespace MedicalShopManager.UnitTests.Billing;

/// <summary>Bill computation, line/sale validation, batches and scans of the Billing module (spec 02).</summary>
public class BillingCalculatorTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    internal static BillLine Line(long packs = 1, long loose = 0, decimal discount = 0m, int pack = 15, long mrp = 18840,
        decimal gst = 12m, long available = 1000, DateTime? expiry = null, long batchId = 1, long medicineId = 1,
        DrugSchedule schedule = DrugSchedule.None, decimal? maxDiscount = null, bool allowLoose = true, long free = 0) => new()
    {
        MedicineId = medicineId,
        MedicineName = $"Item {medicineId}",
        BatchId = batchId,
        BatchNumber = $"B{batchId}",
        ExpiryDate = expiry ?? Today.AddYears(1),
        UnitsPerPack = pack,
        AllowLooseSale = allowLoose,
        MrpPerPackPaisa = mrp,
        RatePerPackPaisa = mrp,
        DiscountPercent = discount,
        MaxDiscountPercent = maxDiscount,
        GstPercent = gst,
        Packs = packs,
        Loose = loose,
        FreeUnits = free,
        AvailableUnits = available,
        Schedule = schedule
    };

    private static BillComputation Compute(params BillLine[] lines) => BillingCalculator.ComputeBill(new BillRequest { Lines = lines });

    // ───────────── Golden vectors (spec 02 §H, verified on Localwell) ─────────────

    [Fact]
    public void OnePackAndFiveLooseOf15s_AtMrp18840_Is25120()
    {
        var bill = Compute(Line(packs: 1, loose: 5));
        bill.Lines[0].Amounts.TotalPaisa.Should().Be(25120);
        bill.Totals.NetPayablePaisa.Should().Be(25100); // rounded to the nearest rupee
        bill.Totals.RoundOffPaisa.Should().Be(-20);
    }

    [Fact]
    public void TenPercentDiscount_Gives22608()
    {
        var bill = Compute(Line(packs: 1, loose: 5, discount: 10m));
        bill.Lines[0].Amounts.TotalPaisa.Should().Be(22608);
        bill.Lines[0].Amounts.DiscountedRatePerPackPaisa.Should().Be(16956); // 188.40 at 10 % → 169.56 (RATE cell)
    }

    [Fact]
    public void GstInclusive_TaxablePlusTaxEqualsLineTotal_AndSplitsCgstSgst()
    {
        var amounts = Compute(Line(packs: 3, pack: 10, mrp: 12450, gst: 5m, discount: 2m)).Lines[0].Amounts;
        (amounts.TaxablePaisa + amounts.TaxPaisa).Should().Be(amounts.TotalPaisa);
        (amounts.CgstPaisa + amounts.SgstPaisa).Should().Be(amounts.TaxPaisa);
        amounts.IgstPaisa.Should().Be(0);
    }

    [Fact]
    public void InterState_PutsAllTaxInIgst()
    {
        var bill = BillingCalculator.ComputeBill(new BillRequest { Lines = new[] { Line() }, IsInterState = true });
        bill.Totals.IgstPaisa.Should().Be(bill.Totals.TaxPaisa);
        bill.Totals.CgstPaisa.Should().Be(0);
    }

    [Fact]
    public void WithoutGst_ComputesNoTax()
    {
        var bill = BillingCalculator.ComputeBill(new BillRequest { Lines = new[] { Line() }, WithGst = false });
        bill.Totals.TaxPaisa.Should().Be(0);
        bill.Totals.TaxablePaisa.Should().Be(bill.Lines[0].Amounts.TotalPaisa);
    }

    [Fact]
    public void BillOfSupplyShop_IgnoresWithGstToggle()
    {
        var request = new SalePostingRequest { Lines = new[] { Line() }, WithGst = true };
        var settings = new BillingSettings { ShopChargesGst = false };
        BillingCalculator.ComputeBill(BillRequest.From(request, false, settings)).Totals.TaxPaisa.Should().Be(0);
    }

    [Fact]
    public void LineDiscount_IsClampedToItemCap()
    {
        var bill = Compute(Line(discount: 30m, maxDiscount: 10m));
        bill.Lines[0].EffectiveDiscountPercent.Should().Be(10m);
    }

    [Fact]
    public void BillDiscountPercent_IsAddedToEachLine_AndClampedToSettingsMax()
    {
        var bill = BillingCalculator.ComputeBill(new BillRequest
        {
            Lines = new[] { Line(discount: 5m), Line(batchId: 2, medicineId: 2, discount: 0m, maxDiscount: 3m) },
            BillDiscountKind = BillDiscountKind.Percent,
            BillDiscountPercent = 5m,
            BillDiscountMaxPercent = 8m
        });
        bill.Lines[0].EffectiveDiscountPercent.Should().Be(8m);   // 5 + 5 capped by settings max 8
        bill.Lines[1].EffectiveDiscountPercent.Should().Be(3m);   // item cap 3
    }

    [Fact]
    public void BillDiscountAmount_IsDistributedExactly()
    {
        var bill = BillingCalculator.ComputeBill(new BillRequest
        {
            Lines = new[] { Line(packs: 1), Line(batchId: 2, medicineId: 2, packs: 2, mrp: 10001) },
            BillDiscountKind = BillDiscountKind.Amount,
            BillDiscountAmountPaisa = 1001
        });
        bill.BillDiscountPaisa.Should().Be(1001);
        bill.Lines.Sum(l => l.Amounts.BillDiscountPaisa).Should().Be(1001);
    }

    [Fact]
    public void BillDiscountAmount_AboveSubtotal_Throws()
    {
        var act = () => BillingCalculator.ComputeBill(new BillRequest
        {
            Lines = new[] { Line() },
            BillDiscountKind = BillDiscountKind.Amount,
            BillDiscountAmountPaisa = 1_000_000
        });
        act.Should().Throw<BillingValidationException>().Which.Errors.Should().Contain("Discount amount cannot exceed the subtotal");
    }

    [Fact]
    public void CustomCharge_IsAddedUntaxed()
    {
        var plain = Compute(Line(packs: 2));
        var charged = BillingCalculator.ComputeBill(new BillRequest { Lines = new[] { Line(packs: 2) }, CustomChargePaisa = 5000 });
        charged.Totals.TaxPaisa.Should().Be(plain.Totals.TaxPaisa);
        charged.Totals.UnroundedTotalPaisa.Should().Be(plain.Totals.UnroundedTotalPaisa + 5000);
    }

    [Fact]
    public void FreeUnits_AreNotCharged_ButLeaveStock()
    {
        var line = Line(packs: 1, free: 3);
        var bill = Compute(line);
        bill.Lines[0].Amounts.TotalPaisa.Should().Be(18840);
        line.StockUnits.Should().Be(18);
        bill.TotalUnits.Should().Be(18);
    }

    [Fact]
    public void NegativeQuantity_Throws() =>
        FluentActions.Invoking(() => Compute(Line(packs: 0, loose: 0) with { FreeUnits = -1 }))
            .Should().Throw<BillingValidationException>();

    // ───────────── Line validation (spec 02 "Line validation") ─────────────

    [Fact]
    public void ValidateLine_ExpiredBatch_IsBlocked()
    {
        var errors = BillingCalculator.ValidateLine(Line(expiry: Today), 1, Today);
        errors.Should().ContainSingle(e => e.Contains("expired") && e.EndsWith("in Row: 1"));
    }

    [Fact]
    public void ValidateLine_InsideExpiryCutoff_IsBlocked()
    {
        BillingCalculator.ValidateLine(Line(expiry: Today.AddMonths(2)), 2, Today, expiryCutoffMonths: 3)
            .Should().ContainSingle(e => e.Contains("cut-off") && e.EndsWith("in Row: 2"));
        BillingCalculator.ValidateLine(Line(expiry: Today.AddMonths(4)), 2, Today, expiryCutoffMonths: 3).Should().BeEmpty();
    }

    [Fact]
    public void ValidateLine_MoreThanStock_IsBlocked_UnlessNegativeStockAllowed()
    {
        var line = Line(packs: 2, available: 20);
        BillingCalculator.ValidateLine(line, 1, Today).Should().ContainSingle(e => e.StartsWith("Only 1 + 5 available"));
        BillingCalculator.ValidateLine(line, 1, Today, allowNegativeStock: true).Should().BeEmpty();
    }

    [Fact]
    public void ValidateLine_LooseWhenNotAllowed_AndZeroQuantity()
    {
        BillingCalculator.ValidateLine(Line(packs: 0, loose: 3, allowLoose: false), 1, Today)
            .Should().Contain(e => e.StartsWith("Loose sale is not allowed"));
        BillingCalculator.ValidateLine(Line(packs: 0, loose: 0), 1, Today)
            .Should().Contain(e => e.StartsWith("Please fill valid Qty Packs/Loose"));
    }

    [Fact]
    public void ValidateLine_DiscountAboveCap_AndRateAboveMrp()
    {
        BillingCalculator.ValidateLine(Line(discount: 20m, maxDiscount: 10m), 1, Today)
            .Should().Contain("Discount Must be Between 0% and 10% in Row: 1");
        BillingCalculator.ValidateLine(Line() with { RatePerPackPaisa = 20000 }, 1, Today)
            .Should().Contain("Sale Rate cannot exceed MRP in Row: 1");
    }

    // ───────────── Sale validation ─────────────

    [Fact]
    public void ValidateSale_EmptyBill_FutureDate_MissingCustomer_ChargeWithoutName()
    {
        var errors = BillingCalculator.ValidateSale(new SalePostingRequest
        {
            InvoiceDate = Today.AddDays(1),
            IsCounterSale = false,
            CustomChargePaisa = 100
        }, new BillingSettings(), Today);
        errors.Should().Contain(new[]
        {
            "Please add Products", "Sale Invoice Date cannot be in the future", "Please add customer", "Please add custom charge type"
        });
    }

    [Fact]
    public void ValidateSale_SameBatchTwice_OverStock_IsReported()
    {
        var request = new SalePostingRequest { InvoiceDate = Today, Lines = new[] { Line(packs: 1, available: 20), Line(packs: 1, available: 20) } };
        BillingCalculator.ValidateSale(request, new BillingSettings(), Today)
            .Should().Contain(e => e.Contains("added more than once"));
    }

    [Theory]
    [InlineData(DrugSchedule.H1)]
    [InlineData(DrugSchedule.X)]
    [InlineData(DrugSchedule.NRx)]
    public void ScheduleRegisterItems_NeedPatientDoctorAndRx(DrugSchedule schedule)
    {
        var request = new SalePostingRequest { InvoiceDate = Today, Lines = new[] { Line(schedule: schedule) } };
        var errors = BillingCalculator.ValidateScheduleCapture(request, new BillingSettings());
        errors.Should().HaveCount(3);
        BillingCalculator.ValidateScheduleCapture(request with { PatientName = "Asha", DoctorName = "Dr Rao", PrescriptionNumber = "RX-1" },
            new BillingSettings()).Should().BeEmpty();
    }

    [Fact]
    public void ScheduleH_NeedsDoctorOnlyWhenSettingOn_AndRxRefWhenRequired()
    {
        var request = new SalePostingRequest { InvoiceDate = Today, Lines = new[] { Line(schedule: DrugSchedule.H) } };
        BillingCalculator.ValidateScheduleCapture(request, new BillingSettings()).Should().BeEmpty();
        BillingCalculator.ValidateScheduleCapture(request, new BillingSettings { RequireDoctorForScheduleH = true })
            .Should().ContainSingle(e => e.Contains("Doctor name"));
        BillingCalculator.ValidateScheduleCapture(request, new BillingSettings { RequirePrescriptionReference = true })
            .Should().ContainSingle(e => e.Contains("Rx) number"));
    }

    // ───────────── Payment resolution (spec 02 §M) ─────────────

    private static SalePostingRequest Paying(params SaleTender[] tenders) =>
        new() { InvoiceDate = Today, IsCounterSale = false, CustomerId = 7, Tenders = tenders, DueDate = Today.AddDays(7) };

    [Fact]
    public void Payment_CashWithChange()
    {
        var (resolution, errors) = BillingCalculator.ResolvePayment(25100,
            Paying(new SaleTender { Mode = TenderMode.Cash, AmountPaisa = 25100 }) with { IsCounterSale = true, CustomerId = null, CashTenderedPaisa = 30000 });
        errors.Should().BeEmpty();
        resolution!.ChangePaisa.Should().Be(4900);
        resolution.Status.Should().Be(PaymentStatus.Paid);
        resolution.Method.Should().Be(PaymentMethod.Cash);
    }

    [Fact]
    public void Payment_Split_IsMethodSplit_AndPartialLeavesDue()
    {
        var (resolution, _) = BillingCalculator.ResolvePayment(10000,
            Paying(new SaleTender { Mode = TenderMode.Cash, AmountPaisa = 3000 }, new SaleTender { Mode = TenderMode.Upi, AmountPaisa = 2000 }));
        resolution!.Method.Should().Be(PaymentMethod.Split);
        resolution.BalanceDuePaisa.Should().Be(5000);
        resolution.Status.Should().Be(PaymentStatus.Partial);
    }

    [Fact]
    public void Payment_CounterSaleMustBePaidInFull_AndCannotBeCredit()
    {
        var counter = new SalePostingRequest { InvoiceDate = Today, IsCounterSale = true, Tenders = new[] { new SaleTender { AmountPaisa = 100 } } };
        BillingCalculator.ResolvePayment(1000, counter).Errors.Should().Contain(e => e.StartsWith("Cash/Counter sale must be paid in full"));
        BillingCalculator.ResolvePayment(1000, counter with { IsCredit = true, Tenders = Array.Empty<SaleTender>() })
            .Errors.Should().Contain(e => e.StartsWith("A Cash/Counter sale cannot be on credit"));
    }

    [Fact]
    public void Payment_Credit_NeedsDueDate_AndNoTenders()
    {
        var credit = Paying() with { IsCredit = true, DueDate = null };
        BillingCalculator.ResolvePayment(1000, credit).Errors.Should().Contain("Please provide a valid payment due date");
        var (ok, _) = BillingCalculator.ResolvePayment(1000, credit with { DueDate = Today.AddDays(30) });
        ok!.Method.Should().Be(PaymentMethod.Credit);
        ok.Status.Should().Be(PaymentStatus.Unpaid);
        BillingCalculator.ResolvePayment(1000, credit with { DueDate = Today, Tenders = new[] { new SaleTender { AmountPaisa = 10 } } })
            .Errors.Should().Contain(e => e.StartsWith("Remove payments"));
    }

    [Fact]
    public void Payment_OverPayment_ShortCheque_AndCashBelowTender()
    {
        BillingCalculator.ResolvePayment(1000, Paying(new SaleTender { AmountPaisa = 2000 })).Errors.Should().Contain("Payment Amount > Payable Amount");
        BillingCalculator.ResolvePayment(1000, Paying(new SaleTender { Mode = TenderMode.Cheque, AmountPaisa = 1000, Reference = "123" }))
            .Errors.Should().Contain("Cheque number must be at least 6 digits");
        BillingCalculator.ResolvePayment(1000, Paying(new SaleTender { AmountPaisa = 1000 }) with { CashTenderedPaisa = 500 })
            .Errors.Should().Contain("Cash received is less than the cash payment amount");
    }

    // ───────────── Batches, defaults, scans ─────────────

    private static SaleBatchOption Batch(long id, DateTime expiry, long qty = 30, DateTime? created = null) => new()
    {
        BatchId = id, MedicineId = 1, BatchNumber = $"B{id}", ExpiryDate = expiry, UnitsPerPack = 15, MrpPaisa = 18840,
        SaleRatePaisa = 18840, PurchaseRatePaisa = 12000, GstPercent = 12m, QuantityRemaining = qty, CreatedAt = created ?? Today.AddDays(-id)
    };

    [Fact]
    public void Fefo_PicksEarliestSellableExpiry_SkippingExpiredAndEmpty()
    {
        var batches = BillingCalculator.Annotate(new[]
        {
            Batch(1, Today.AddDays(-1)),            // expired
            Batch(2, Today.AddMonths(2), qty: 0),   // empty
            Batch(3, Today.AddMonths(9)),
            Batch(4, Today.AddMonths(4))
        }, Today, nearExpiryMonths: 6);
        BillingCalculator.PickDefaultBatch(batches)!.BatchId.Should().Be(4);
        batches.Single(b => b.BatchId == 4).IsNearExpiry.Should().BeTrue();
        batches.Single(b => b.BatchId == 1).IsExpired.Should().BeTrue();
    }

    [Fact]
    public void Lifo_PicksLatestReceived()
    {
        var batches = BillingCalculator.Annotate(new[]
        {
            Batch(1, Today.AddMonths(12), created: Today.AddDays(-30)),
            Batch(2, Today.AddMonths(24), created: Today.AddDays(-1))
        }, Today, 6);
        BillingCalculator.PickDefaultBatch(batches, BatchPickOrder.Lifo)!.BatchId.Should().Be(2);
        BillingCalculator.PickDefaultBatch(batches, BatchPickOrder.Fefo)!.BatchId.Should().Be(1);
    }

    [Fact]
    public void ExpiryCutoff_MakesBatchUnsellable()
    {
        var batches = BillingCalculator.Annotate(new[] { Batch(1, Today.AddMonths(2)), Batch(2, Today.AddMonths(8)) }, Today, 6, expiryCutoffMonths: 3);
        batches[0].BlockedByCutoff.Should().BeTrue();
        batches[0].IsSellable.Should().BeFalse();
        BillingCalculator.PickDefaultBatch(batches)!.BatchId.Should().Be(2);
    }

    [Fact]
    public void Annotate_ComputesMarginOnExGstSaleRate()
    {
        var batch = BillingCalculator.Annotate(new[] { Batch(1, Today.AddYears(1)) }, Today, 6)[0];
        batch.MarginPercent.Should().Be(PricingCalculator.MarginPercent(18840, 12m, 12000));
    }

    [Fact]
    public void DefaultDiscount_CustomerThenBatchThenSettings_NeverBelowImpliedRate_ClampedToCap()
    {
        var batch = Batch(1, Today.AddYears(1)) with { DiscountPercent = 4m };
        BillingCalculator.DefaultDiscountPercent(6m, batch, null).Should().Be(6m);
        BillingCalculator.DefaultDiscountPercent(0m, batch, null).Should().Be(4m);
        BillingCalculator.DefaultDiscountPercent(0m, batch with { DiscountPercent = 0m }, null, 2m).Should().Be(2m);
        BillingCalculator.DefaultDiscountPercent(0m, batch with { SaleRatePaisa = 16956 }, null).Should().Be(10m);
        BillingCalculator.DefaultDiscountPercent(20m, batch, 5m).Should().Be(5m);
    }

    [Fact]
    public void CreateLine_UsesBatchPackMrpAndCost()
    {
        var item = new SaleCatalogItem { MedicineId = 1, Name = "Pan-D", UnitsPerPack = 10, GstPercent = 12m, Schedule = DrugSchedule.H };
        var line = BillingCalculator.CreateLine(item, Batch(1, Today.AddYears(1)), 0m, 2, 3);
        line.UnitsPerPack.Should().Be(15);
        line.MrpPerPackPaisa.Should().Be(18840);
        line.CostPerPackExGstPaisa.Should().Be(12000);
        line.PaidUnits.Should().Be(33);
        line.Schedule.Should().Be(DrugSchedule.H);
    }

    [Fact]
    public void DiscountFromEnteredRate_DerivesPercentFromMrp_AndClamps()
    {
        BillingCalculator.DiscountFromEnteredRate(Line(), 16956).Should().Be(10m);
        BillingCalculator.DiscountFromEnteredRate(Line(), 20000).Should().Be(0m);
        BillingCalculator.DiscountFromEnteredRate(Line(maxDiscount: 5m), 9420).Should().Be(5m);
    }

    [Fact]
    public void Scan_AddsPackOrUnit_RollingLooseIntoPacks()
    {
        BillingCalculator.AddScan(Line(packs: 1), inPacks: true).Packs.Should().Be(2);
        var rolled = BillingCalculator.AddScan(Line(packs: 1, loose: 14), inPacks: false);
        (rolled.Packs, rolled.Loose).Should().Be((2L, 0L));
        BillingCalculator.AddScan(Line(packs: 1, allowLoose: false), inPacks: false).Packs.Should().Be(2);

        var item = new SaleCatalogItem { AllowLooseSale = true };
        BillingCalculator.FirstScanQuantity(item, Batch(1, Today.AddYears(1)), inPacks: false).Should().Be((0L, 1L));
        BillingCalculator.FirstScanQuantity(item, Batch(1, Today.AddYears(1)), inPacks: true).Should().Be((1L, 0L));
    }

    [Theory]
    [InlineData("8901234567890", true)]
    [InlineData("12345678", true)]
    [InlineData("dolo", false)]
    [InlineData("1234", false)]
    public void LooksLikeBarcode(string text, bool expected) => BillingCalculator.LooksLikeBarcode(text).Should().Be(expected);

    [Fact]
    public void DueDays_AndDraftExpiry()
    {
        BillingCalculator.DueDaysLabel(Today, Today).Should().Be("Due Today");
        BillingCalculator.DueDaysLabel(Today.AddDays(3), Today).Should().Be("Due in 3 days");
        BillingCalculator.DueDaysLabel(Today.AddDays(-1), Today).Should().Be("Overdue by 1 day");
        BillingCalculator.DefaultDueDate(Today, 15).Should().Be(Today.AddDays(15));
        BillingCalculator.IsDraftExpired(Today.AddDays(-8), Today, 7).Should().BeTrue();
        BillingCalculator.IsDraftExpired(Today.AddDays(-6), Today, 7).Should().BeFalse();
    }

    [Fact]
    public void BillDiscountPercentAndAmount_ConvertBothWays()
    {
        BillingCalculator.BillDiscountAmountFromPercent(10m, 25120).Should().Be(2512);
        BillingCalculator.BillDiscountPercentFromAmount(2512, 25120).Should().Be(10m);
    }
}
