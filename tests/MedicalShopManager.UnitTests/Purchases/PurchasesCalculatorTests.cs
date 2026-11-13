using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;
using Xunit;

namespace MedicalShopManager.UnitTests.Purchases;

/// <summary>
/// Purchase line / bill maths (spec 04 "Business rules & calculations"): per-pack rates, unit quantities, item +
/// scheme + bill discounts, GST added on top, net purchase rate over paid + free units, round-off, margin.
/// </summary>
public class PurchasesCalculatorTests
{
    private static PurchaseBillLine Line(long ratePaisa = 10_000, int pack = 10, int units = 50, int free = 0, decimal disc = 0,
        decimal scheme = 0, decimal gst = 12, long mrp = 15_000, long sale = 0) => new()
    {
        MedicineId = 1, MedicineName = "Paracetamol 500", HsnCode = "3004", BatchNumber = "B1", ExpiryDate = new DateTime(2028, 3, 31),
        UnitsPerPack = pack, Quantity = units, FreeQuantity = free, EnteredRatePaisa = ratePaisa, DiscountPercent = disc,
        SchemePercent = scheme, GstRatePercent = gst, MrpPaisa = mrp, SaleRatePaisa = sale
    };

    [Fact]
    public void Line_PerPackRate_UnitQuantity_DiscountThenGstOnTop()
    {
        // ₹100 per 10's, 5 packs (50 units), 10% off, 12% GST → taxable ₹450, GST ₹54, total ₹504.
        var bill = new PurchaseBill { Lines = { Line(disc: 10) } };
        var r = PurchaseCalculator.CalculateLine(bill.Lines[0], bill);

        Assert.Equal(50_000, r.ValueBeforeDiscountPaisa);
        Assert.Equal(5_000, r.DiscountAmountPaisa);
        Assert.Equal(45_000, r.TaxablePaisa);
        Assert.Equal(5_400, r.TaxPaisa);
        Assert.Equal(2_700, r.CgstPaisa);
        Assert.Equal(2_700, r.SgstPaisa);
        Assert.Equal(50_400, r.TotalPaisa);
        Assert.Equal(9_000, r.NetRatePaisa);
        Assert.Equal(9_000, r.NetPurchaseRatePaisa);
    }

    [Fact]
    public void FreeUnits_LowerTheNetPurchaseRate_NotTheBillAmount()
    {
        // 5 packs paid + 1 pack free: landed cost per pack = 45000 × 10 / 60 = ₹75.
        var bill = new PurchaseBill { Lines = { Line(disc: 10, free: 10) } };
        var r = PurchaseCalculator.CalculateLine(bill.Lines[0], bill);

        Assert.Equal(45_000, r.TaxablePaisa);
        Assert.Equal(7_500, r.NetPurchaseRatePaisa);
    }

    [Fact]
    public void SchemeAndBillDiscount_CompoundOnTheRate()
    {
        // 100 × (1 − 10%) × (1 − 5%) = 85.50 per pack; bill 2% → 83.79 per pack.
        var bill = new PurchaseBill { BillDiscountPercent = 2, Lines = { Line(units: 10, disc: 10, scheme: 5) } };
        var r = PurchaseCalculator.CalculateLine(bill.Lines[0], bill);

        Assert.Equal(8_550, r.TaxablePaisa + 171); // gross before the bill discount
        Assert.Equal(8_379, r.TaxablePaisa);
        Assert.Equal(8_379, r.NetRatePaisa);
    }

    [Fact]
    public void GstInclusiveRates_AreConvertedToExGst()
    {
        var bill = new PurchaseBill { RatesIncludeGst = true, Lines = { Line(ratePaisa: 11_200, units: 10) } };
        var r = PurchaseCalculator.CalculateLine(bill.Lines[0], bill);

        Assert.Equal(10_000, r.PurchaseRateExGstPaisa);
        Assert.Equal(10_000, r.TaxablePaisa);
        Assert.Equal(1_200, r.TaxPaisa);
    }

    [Fact]
    public void BillWithoutGstColumn_AddsNoTax_ButKeepsTheLineGstRate()
    {
        var bill = new PurchaseBill { WithGst = false, Lines = { Line(units: 10) } };
        PurchaseCalculator.Recalculate(bill);

        Assert.Equal(0, bill.TaxAmountPaisa);
        Assert.Equal(10_000, bill.GrandTotalPaisa);
        Assert.Equal(12m, bill.Lines[0].GstRatePercent);
    }

    [Fact]
    public void InterState_PutsAllTaxInIgst()
    {
        var bill = new PurchaseBill { IsInterState = true, Lines = { Line(units: 10) } };
        PurchaseCalculator.Recalculate(bill);

        Assert.Equal(1_200, bill.IgstPaisa);
        Assert.Equal(0, bill.CgstPaisa + bill.SgstPaisa);
    }

    [Fact]
    public void Totals_AddOtherCharges_AndRoundToTheRupee()
    {
        // ₹33.33 per 1's × 3 = 99.99; 5% → 5.00; + ₹10.40 delivery = 115.39 → 115.00, round-off −0.39.
        var bill = new PurchaseBill
        {
            OtherChargesPaisa = 1_040, OtherChargesLabel = "Delivery",
            Lines = { Line(ratePaisa: 3_333, pack: 1, units: 3, gst: 5, mrp: 5_000) }
        };
        var totals = PurchaseCalculator.Recalculate(bill);

        Assert.Equal(9_999, totals.TaxablePaisa);
        Assert.Equal(500, totals.TaxPaisa);
        Assert.Equal(-39, totals.RoundOffPaisa);
        Assert.Equal(11_500, totals.GrandTotalPaisa);
        Assert.Equal(11_500, bill.BalancePaisa);
        Assert.Equal(PaymentStatus.Unpaid, bill.PaymentStatus);
        Assert.Equal(1, bill.Lines[0].LineNo);
    }

    [Fact]
    public void Recalculate_WithoutRoundOff_KeepsPaisa()
    {
        var bill = new PurchaseBill { Lines = { Line(ratePaisa: 3_333, pack: 1, units: 3, gst: 5, mrp: 5_000) } };
        var totals = PurchaseCalculator.Recalculate(bill, RoundOffMode.None);
        Assert.Equal(10_499, totals.GrandTotalPaisa);
        Assert.Equal(0, totals.RoundOffPaisa);
    }

    [Fact]
    public void Margin_IsOnSaleRateExGst_AgainstNetPurchaseRate()
    {
        // Sale ₹112 incl 12% = ₹100 ex; landed cost ₹75 → 25 %.
        var bill = new PurchaseBill { Lines = { Line(disc: 10, free: 10, sale: 11_200) } };
        var r = PurchaseCalculator.CalculateLine(bill.Lines[0], bill);
        Assert.Equal(25m, r.MarginPercent);
    }

    [Fact]
    public void Recalculate_DefaultsPtrToTheExGstRate()
    {
        var bill = new PurchaseBill { RatesIncludeGst = true, Lines = { Line(ratePaisa: 11_200) } };
        PurchaseCalculator.Recalculate(bill);
        Assert.Equal(10_000, bill.Lines[0].PtrPaisa);
        Assert.Equal(10_000, bill.Lines[0].PurchaseRatePaisa);
    }

    [Theory]
    [InlineData(10_000, 0, PaymentStatus.Unpaid)]
    [InlineData(10_000, 4_000, PaymentStatus.Partial)]
    [InlineData(10_000, 10_000, PaymentStatus.Paid)]
    [InlineData(0, 0, PaymentStatus.Paid)]
    public void PaymentStatus_FollowsAmountPaid(long total, long paid, PaymentStatus expected) =>
        Assert.Equal(expected, PurchaseCalculator.PaymentStatusFor(total, paid));

    [Fact]
    public void DueDate_IsInvoiceDatePlusCreditDays()
    {
        Assert.Equal(new DateTime(2026, 12, 4), PurchaseCalculator.DueDate(new DateTime(2026, 11, 4), 30));
        Assert.Null(PurchaseCalculator.DueDate(new DateTime(2026, 11, 4), 0));
    }

    [Theory]
    [InlineData(12_000, 0, 12_000)]
    [InlineData(12_000, 10, 10_800)]
    [InlineData(0, 10, 0)]
    public void DefaultSaleRate_IsMrpLessTheSetting(long mrp, decimal discount, long expected) =>
        Assert.Equal(expected, PurchaseCalculator.DefaultSaleRate(mrp, discount));

    [Theory]
    [InlineData(25, 10, 1, SchemeRounding.Full, 2)]
    [InlineData(9, 10, 1, SchemeRounding.Full, 0)]
    [InlineData(15, 10, 1, SchemeRounding.Half, 1.5)]
    [InlineData(14, 10, 1, SchemeRounding.Half, 1.5)]
    [InlineData(13, 10, 1, SchemeRounding.Half, 1)]
    [InlineData(4, 10, 1, SchemeRounding.Half, 0)]
    [InlineData(13, 10, 1, SchemeRounding.Quarter, 1.25)]
    [InlineData(15, 10, 2, SchemeRounding.Quarter, 3)]
    [InlineData(17, 10, 1, SchemeRounding.Quarter, 1.75)]
    [InlineData(2, 10, 1, SchemeRounding.Quarter, 0)]
    [InlineData(10, 0, 1, SchemeRounding.Full, 0)]
    public void Scheme_FreePacks(double packs, int x, int y, SchemeRounding rounding, double expected) =>
        Assert.Equal((decimal)expected, PurchaseCalculator.FreePacksFromScheme((decimal)packs, x, y, rounding));

    [Fact]
    public void FreePacks_ToWholeUnits()
    {
        Assert.Equal(7, PurchaseCalculator.FreeUnitsFromPacks(0.5m, 15));
        Assert.Equal(20, PurchaseCalculator.FreeUnitsFromPacks(2m, 10));
        Assert.Equal(0, PurchaseCalculator.FreeUnitsFromPacks(0m, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseCalculator.FreeUnitsFromPacks(1m, 0));
    }

    [Fact]
    public void Return_AmountsPerPack_GstOnTop_RoundedTotal()
    {
        var doc = new PurchaseReturnDocument
        {
            Lines =
            {
                new PurchaseReturnLine { BatchId = 1, MedicineId = 1, UnitsPerPack = 15, Quantity = 20, RatePaisa = 18_840, GstRatePercent = 12 },
                new PurchaseReturnLine { BatchId = 2, MedicineId = 2, UnitsPerPack = 1, Quantity = 3, RatePaisa = 999, GstRatePercent = 5 }
            }
        };
        PurchaseCalculator.RecalculateReturn(doc);

        Assert.Equal(25_120, doc.Lines[0].TaxablePaisa);   // 188.40 × 20 / 15
        Assert.Equal(3_014, doc.Lines[0].TaxPaisa);
        Assert.Equal(2_997, doc.Lines[1].TaxablePaisa);
        Assert.Equal(150, doc.Lines[1].TaxPaisa);
        Assert.Equal(28_117, doc.TaxablePaisa);
        Assert.Equal(3_164, doc.TaxAmountPaisa);
        Assert.Equal(31_300, doc.NetAmountPaisa);
        Assert.Equal(19, doc.RoundOffPaisa);
    }

    [Fact]
    public void OtherCharges_CannotBeNegative() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseCalculator.Calculate(new PurchaseBill { OtherChargesPaisa = -1 }));

    [Fact]
    public void ToPaisa_RoundsHalfAwayFromZero()
    {
        Assert.Equal(3_550, PurchaseCalculator.ToPaisa(35.50m));
        Assert.Equal(1, PurchaseCalculator.ToPaisa(0.005m));
    }
}
