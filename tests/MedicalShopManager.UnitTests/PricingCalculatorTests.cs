using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;
using Xunit;

namespace MedicalShopManager.UnitTests;

/// <summary>
/// Golden vectors for PricingCalculator. The 188.40 / 15's cases are the values verified on the Localwell
/// billing screen (spec 02 §H): 1 pack + 5 loose = 251.20, and 226.08 after a 10% discount.
/// </summary>
public class PricingCalculatorTests
{
    // ─── Pack / loose quantities ───

    [Theory]
    [InlineData(50, 15, 3, 5)]
    [InlineData(45, 15, 3, 0)]
    [InlineData(4, 15, 0, 4)]
    [InlineData(7, 1, 7, 0)]
    public void PackQuantity_FromUnits_SplitsPacksAndLoose(long units, int perPack, long packs, long loose)
    {
        var q = PackQuantity.FromUnits(units, perPack);
        Assert.Equal(new PackQuantity(packs, loose), q);
        Assert.Equal(units, q.ToUnits(perPack));
    }

    [Theory]
    [InlineData(50, 15, "3 + 5")]
    [InlineData(45, 15, "3")]
    [InlineData(-20, 15, "-1 + 5")]
    [InlineData(7, 1, "7")]
    public void PackQuantity_Format(long units, int perPack, string expected) =>
        Assert.Equal(expected, PackQuantity.Format(units, perPack));

    [Fact]
    public void PackQuantity_RejectsZeroPackSize() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PackQuantity.FromUnits(5, 0));

    // ─── Rates ───

    [Fact]
    public void AmountForUnits_RoundsOnTheLineNotTheUnit() =>
        Assert.Equal(25120, PricingCalculator.AmountForUnits(18840, 20, 15)); // 1 pack + 5 loose of 15's @ 188.40

    [Fact]
    public void RateAfterDiscount_And_BackToPercent()
    {
        Assert.Equal(16956, PricingCalculator.RateAfterDiscount(18840, 10m));
        Assert.Equal(10m, PricingCalculator.DiscountPercentFromRate(18840, 16956));
        Assert.Equal(0m, PricingCalculator.DiscountPercentFromRate(0, 100));
    }

    [Fact]
    public void UnitRate_IsUnrounded() => Assert.Equal(1256m, PricingCalculator.UnitRatePaisa(18840, 15));

    // ─── GST ───

    [Theory]
    [InlineData(11200, 12, 10000)]
    [InlineData(10500, 5, 10000)]
    [InlineData(22608, 12, 20186)]
    [InlineData(5000, 0, 5000)]
    public void ExtractTaxable_BacksGstOutOfInclusiveAmount(long inclusive, decimal gst, long taxable) =>
        Assert.Equal(taxable, PricingCalculator.ExtractTaxable(inclusive, gst));

    [Theory]
    [InlineData(11, false, 6, 5, 0)]
    [InlineData(10, false, 5, 5, 0)]
    [InlineData(11, true, 0, 0, 11)]
    public void SplitTax_PutsOddPaisaOnCgst(long tax, bool inter, long cgst, long sgst, long igst)
    {
        var split = PricingCalculator.SplitTax(tax, inter);
        Assert.Equal(new TaxSplit(cgst, sgst, igst), split);
        Assert.Equal(tax, split.TotalPaisa);
    }

    [Theory]
    [InlineData("27", "29ABCDE1234F1Z5", true)]
    [InlineData("27", "27ABCDE1234F1Z5", false)]
    [InlineData("7", "07ABCDE1234F1Z5", false)]
    [InlineData("27", null, false)]
    [InlineData(null, "29ABCDE1234F1Z5", false)]
    public void IsInterStateSupply_ComparesGstinStateCode(string? business, string? party, bool expected) =>
        Assert.Equal(expected, PricingCalculator.IsInterStateSupply(business, party));

    // ─── Sale line ───

    [Fact]
    public void SaleLine_LocalwellGoldenVector_TenPercentOn15s()
    {
        var line = PricingCalculator.CalculateSaleLine(new SaleLineInput
        {
            RatePerPackPaisa = 18840,
            MrpPerPackPaisa = 18840,
            Units = new PackQuantity(1, 5).ToUnits(15),
            UnitsPerPack = 15,
            DiscountPercent = 10,
            GstPercent = 12,
            CostPerPackExGstPaisa = 9000
        });

        Assert.Equal(25120, line.GrossPaisa);
        Assert.Equal(16956, line.DiscountedRatePerPackPaisa);
        Assert.Equal(2512, line.LineDiscountPaisa);
        Assert.Equal(22608, line.TotalPaisa);       // 226.08 as on screen
        Assert.Equal(20186, line.TaxablePaisa);
        Assert.Equal(2422, line.TaxPaisa);
        Assert.Equal(1211, line.CgstPaisa);
        Assert.Equal(1211, line.SgstPaisa);
        Assert.Equal(0, line.IgstPaisa);
        Assert.Equal(25120, line.MrpValuePaisa);
        Assert.Equal(2512, line.SavingsPaisa);
        Assert.Equal(12000, line.CostPaisa);         // 9000 × 20 / 15
        Assert.Equal(8186, line.ProfitPaisa);        // taxable − cost
    }

    [Fact]
    public void SaleLine_Exclusive_AddsGstOnTop()
    {
        var line = PricingCalculator.CalculateSaleLine(new SaleLineInput
        {
            RatePerPackPaisa = 10000, Units = 1, GstPercent = 18, TaxMode = TaxMode.Exclusive, IsInterState = true
        });

        Assert.Equal(10000, line.TaxablePaisa);
        Assert.Equal(1800, line.TaxPaisa);
        Assert.Equal(1800, line.IgstPaisa);
        Assert.Equal(11800, line.TotalPaisa);
        Assert.Equal(0, line.ProfitPaisa); // cost unknown
    }

    [Fact]
    public void SaleLine_BillDiscountShare_ReducesTaxableBeforeBackCalculation()
    {
        var line = PricingCalculator.CalculateSaleLine(new SaleLineInput
        {
            RatePerPackPaisa = 10000, Units = 1, GstPercent = 12, BillDiscountSharePaisa = 1200
        });

        Assert.Equal(8800, line.TotalPaisa);
        Assert.Equal(7857, line.TaxablePaisa);
        Assert.Equal(943, line.TaxPaisa);
        Assert.Equal(1200, line.BillDiscountPaisa);
    }

    [Fact]
    public void SaleLine_InvariantsHoldForManyInputs()
    {
        var random = new Random(42);
        for (var i = 0; i < 2000; i++)
        {
            var perPack = random.Next(1, 31);
            var line = PricingCalculator.CalculateSaleLine(new SaleLineInput
            {
                RatePerPackPaisa = random.Next(1, 500_000),
                Units = random.Next(0, 200),
                UnitsPerPack = perPack,
                DiscountPercent = random.Next(0, 10001) / 100m,
                GstPercent = new[] { 0m, 5m, 12m, 18m, 28m }[random.Next(5)],
                IsInterState = random.Next(2) == 0,
                TaxMode = random.Next(2) == 0 ? TaxMode.Inclusive : TaxMode.Exclusive
            });

            Assert.Equal(line.TotalPaisa, line.TaxablePaisa + line.TaxPaisa);
            Assert.Equal(line.TaxPaisa, line.CgstPaisa + line.SgstPaisa + line.IgstPaisa);
            Assert.True(line.LineDiscountPaisa >= 0);
            Assert.True(line.TaxablePaisa >= 0);
        }
    }

    [Fact]
    public void SaleLine_Guards()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculateSaleLine(new SaleLineInput { RatePerPackPaisa = 100, Units = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculateSaleLine(new SaleLineInput { RatePerPackPaisa = 100, Units = 1, UnitsPerPack = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculateSaleLine(new SaleLineInput { RatePerPackPaisa = 100, Units = 1, DiscountPercent = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculateSaleLine(new SaleLineInput { RatePerPackPaisa = 100, Units = 1, BillDiscountSharePaisa = 101 }));
    }

    // ─── Discounts ───

    [Theory]
    [InlineData(15, 0, 10.0, 10)]
    [InlineData(-1, 0, null, 0)]
    [InlineData(2, 5, 20.0, 5)]
    [InlineData(150, 0, null, 100)]
    public void ClampDiscount(decimal requested, decimal min, double? max, decimal expected) =>
        Assert.Equal(expected, PricingCalculator.ClampDiscount(requested, min, (decimal?)max));

    [Fact]
    public void CombineBillDiscount_AddsThenClamps()
    {
        Assert.Equal(8m, PricingCalculator.CombineBillDiscount(5, 3));
        Assert.Equal(12m, PricingCalculator.CombineBillDiscount(5, 10, 0, 12));
    }

    [Fact]
    public void DistributeAmount_LargestRemainderSumsExactly()
    {
        Assert.Equal(new long[] { 34, 33, 33 }, PricingCalculator.DistributeAmount(100, new long[] { 300, 300, 300 }));
        Assert.Equal(new long[] { 500, 300, 200 }, PricingCalculator.DistributeAmount(1000, new long[] { 5000, 3000, 2000 }));
        Assert.Equal(new long[] { 0, 0 }, PricingCalculator.DistributeAmount(0, new long[] { 0, 0 }));

        var shares = PricingCalculator.DistributeAmount(997, new long[] { 22608, 11800, 1, 4567 });
        Assert.Equal(997, shares.Sum());
    }

    [Fact]
    public void DistributeAmount_RejectsMoreThanTheLines() =>
        Assert.Throws<InvalidOperationException>(() => PricingCalculator.DistributeAmount(101, new long[] { 50, 50 }));

    // ─── Round off and bill totals ───

    [Theory]
    [InlineData(22608, RoundOffMode.NearestRupee, 22600, -8)]
    [InlineData(22650, RoundOffMode.NearestRupee, 22700, 50)]
    [InlineData(22649, RoundOffMode.NearestRupee, 22600, -49)]
    [InlineData(22626, RoundOffMode.NearestFiftyPaise, 22650, 24)]
    [InlineData(22626, RoundOffMode.None, 22626, 0)]
    public void RoundOff(long amount, RoundOffMode mode, long rounded, long roundOff) =>
        Assert.Equal(new RoundOffResult(rounded, roundOff), PricingCalculator.RoundOff(amount, mode));

    [Fact]
    public void SummarizeBill_SumsLinesChargesAdjustmentAndRoundsOff()
    {
        var lines = new[]
        {
            PricingCalculator.CalculateSaleLine(new SaleLineInput { RatePerPackPaisa = 18840, Units = 20, UnitsPerPack = 15, DiscountPercent = 10, GstPercent = 12 }),
            PricingCalculator.CalculateSaleLine(new SaleLineInput { RatePerPackPaisa = 10000, Units = 1, GstPercent = 18, TaxMode = TaxMode.Exclusive })
        };

        var totals = PricingCalculator.SummarizeBill(lines, RoundOffMode.NearestRupee, otherChargesPaisa: 500, adjustmentPaisa: 1000);

        Assert.Equal(33908, totals.UnroundedTotalPaisa);  // 22608 + 11800 + 500 − 1000
        Assert.Equal(33900, totals.NetPayablePaisa);
        Assert.Equal(-8, totals.RoundOffPaisa);
        Assert.Equal(lines.Sum(l => l.TaxPaisa), totals.CgstPaisa + totals.SgstPaisa + totals.IgstPaisa);
        Assert.Equal(2512, totals.DiscountPaisa);
    }

    // ─── Purchase line ───

    [Fact]
    public void PurchaseLine_NetPurchaseRateSpreadsOverFreeGoods()
    {
        var line = PricingCalculator.CalculatePurchaseLine(new PurchaseLineInput
        {
            RatePerPackPaisa = 10000, Units = 100, FreeUnits = 10, UnitsPerPack = 10, DiscountPercent = 10, GstPercent = 12
        });

        Assert.Equal(90000, line.GrossPaisa);
        Assert.Equal(90000, line.TaxablePaisa);
        Assert.Equal(10800, line.TaxPaisa);
        Assert.Equal(100800, line.TotalPaisa);
        Assert.Equal(9000, line.NetRatePerPackPaisa);
        Assert.Equal(8182, line.NetPurchaseRatePerPackPaisa); // 90000 / 11 packs
    }

    [Fact]
    public void PurchaseLine_BillDiscountAndScheme()
    {
        var line = PricingCalculator.CalculatePurchaseLine(new PurchaseLineInput
        {
            RatePerPackPaisa = 10000, Units = 10, UnitsPerPack = 1, SchemePercent = 10, BillDiscountPercent = 5, GstPercent = 5, IsInterState = true
        });

        Assert.Equal(90000, line.GrossPaisa);
        Assert.Equal(85500, line.TaxablePaisa);
        Assert.Equal(4275, line.IgstPaisa);
        Assert.Equal(8550, line.NetPurchaseRatePerPackPaisa);
    }

    [Fact]
    public void PercentFromAmount() => Assert.Equal(12.5m, PricingCalculator.PercentFromAmount(1250, 10000));

    // ─── Margin ───

    [Theory]
    [InlineData(11200, 12, 7000, 30)]
    [InlineData(11200, 12, 10000, 0)]
    [InlineData(11200, 12, 12000, -20)]
    [InlineData(0, 12, 100, 0)]
    public void MarginPercent_IsOnSellingPriceExGst(long sale, decimal gst, long cost, decimal expected) =>
        Assert.Equal(expected, PricingCalculator.MarginPercent(sale, gst, cost));
}
