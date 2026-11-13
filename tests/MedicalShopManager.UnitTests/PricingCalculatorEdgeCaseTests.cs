using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;
using Xunit;

namespace MedicalShopManager.UnitTests;

/// <summary>
/// Edge cases for PricingCalculator beyond the golden vectors: GST slab extremes (0% and 28%), odd paisa,
/// inclusive back-calculation, out-of-range discounts, loose units and exact bill-discount distribution.
/// Expected values are worked by hand from the formulas in the PricingCalculator header.
/// </summary>
public class PricingCalculatorEdgeCaseTests
{
    private static readonly decimal[] GstSlabs = { 0m, 5m, 12m, 18m, 28m };

    // ─── GST slabs ───

    [Fact]
    public void ZeroGst_InclusiveLine_IsAllTaxable()
    {
        var line = PricingCalculator.CalculateSaleLine(new SaleLineInput { RatePerPackPaisa = 9999, Units = 1, GstPercent = 0 });

        Assert.Equal(9999, line.TotalPaisa);
        Assert.Equal(9999, line.TaxablePaisa);
        Assert.Equal(0, line.TaxPaisa);
        Assert.Equal(0, line.CgstPaisa + line.SgstPaisa + line.IgstPaisa);
    }

    [Fact]
    public void TwentyEightPercent_Inclusive_OddTaxPaisaGoesToCgst()
    {
        // 1001 × 100 / 128 = 782.03 → taxable 782, tax 219 → CGST round(109.5) = 110, SGST 109.
        var line = PricingCalculator.CalculateSaleLine(new SaleLineInput { RatePerPackPaisa = 1001, Units = 1, GstPercent = 28 });

        Assert.Equal(1001, line.TotalPaisa);
        Assert.Equal(782, line.TaxablePaisa);
        Assert.Equal(219, line.TaxPaisa);
        Assert.Equal(110, line.CgstPaisa);
        Assert.Equal(109, line.SgstPaisa);
    }

    [Fact]
    public void TwentyEightPercent_Exclusive_And_InterState()
    {
        // 999 × 28% = 279.72 → 280.
        var line = PricingCalculator.CalculateSaleLine(new SaleLineInput
        {
            RatePerPackPaisa = 999, Units = 1, GstPercent = 28, TaxMode = TaxMode.Exclusive, IsInterState = true
        });

        Assert.Equal(999, line.TaxablePaisa);
        Assert.Equal(280, line.TaxPaisa);
        Assert.Equal(280, line.IgstPaisa);
        Assert.Equal(0, line.CgstPaisa + line.SgstPaisa);
        Assert.Equal(1279, line.TotalPaisa);
    }

    [Fact]
    public void InclusiveBackCalculation_IsWithinHalfAPaisaOfTheTrueTaxable_ForEverySlab()
    {
        foreach (var gst in GstSlabs)
        {
            for (long total = 0; total <= 5000; total++)
            {
                var taxable = PricingCalculator.ExtractTaxable(total, gst);
                var exact = total * 100m / (100m + gst);
                Assert.True(Math.Abs(taxable - exact) <= 0.5m, $"gst {gst}, total {total}: {taxable} vs {exact}");

                // Re-applying GST to the taxable value lands back on the bill amount (± rounding of one paisa).
                Assert.True(Math.Abs(taxable + PricingCalculator.TaxOnTaxable(taxable, gst) - total) <= 1,
                    $"gst {gst}, total {total}");
            }
        }
    }

    [Fact]
    public void GstOutsideZeroToHundred_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.ExtractTaxable(100, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.TaxOnTaxable(100, 101));
    }

    // ─── Discounts ───

    [Theory]
    [InlineData(100.01)]
    [InlineData(150)]
    [InlineData(-0.01)]
    public void DiscountOutsideZeroToHundred_IsRejectedOnPostingPaths(double percent)
    {
        var d = (decimal)percent;
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculateSaleLine(
            new SaleLineInput { RatePerPackPaisa = 1000, Units = 1, DiscountPercent = d }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.RateAfterDiscount(1000, d));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CombineBillDiscount(0, d));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculatePurchaseLine(
            new PurchaseLineInput { RatePerPackPaisa = 1000, Units = 1, DiscountPercent = d }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculatePurchaseLine(
            new PurchaseLineInput { RatePerPackPaisa = 1000, Units = 1, SchemePercent = d }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculatePurchaseLine(
            new PurchaseLineInput { RatePerPackPaisa = 1000, Units = 1, BillDiscountPercent = d }));
    }

    [Fact]
    public void HundredPercentDiscount_IsAFreeLine()
    {
        var line = PricingCalculator.CalculateSaleLine(new SaleLineInput { RatePerPackPaisa = 18840, Units = 20, UnitsPerPack = 15, DiscountPercent = 100, GstPercent = 12 });
        Assert.Equal(0, line.TotalPaisa);
        Assert.Equal(25120, line.LineDiscountPaisa);
    }

    [Theory]
    [InlineData(30, 0, -5.0, 0)]   // a corrupt negative item cap never yields a negative discount
    [InlineData(30, 0, 150.0, 30)] // a cap above 100 is treated as 100
    [InlineData(120, 0, 150.0, 100)]
    [InlineData(5, 120, null, 100)] // a minimum above 100 is clamped too
    public void ClampDiscount_KeepsBoundsInsideZeroToHundred(decimal requested, decimal min, double? max, decimal expected) =>
        Assert.Equal(expected, PricingCalculator.ClampDiscount(requested, min, max is null ? null : (decimal)max));

    // ─── Loose units ───

    [Theory]
    [InlineData(3550, 7, 10, 2485)]    // 7 loose of a 10's strip @ 35.50
    [InlineData(18840, 1, 15, 1256)]   // a single tablet
    [InlineData(10001, 1, 3, 3334)]    // 3333.67 → 3334, rounded on the line
    [InlineData(10001, 2, 3, 6667)]    // 6667.33 → 6667 (not 2 × 3334)
    [InlineData(5000, 0, 10, 0)]
    public void LooseUnits_AreRoundedOnTheLineTotal(long ratePerPack, long units, int perPack, long expected) =>
        Assert.Equal(expected, PricingCalculator.AmountForUnits(ratePerPack, units, perPack));

    [Fact]
    public void LooseUnitWithDiscount_UsesTheRoundedDiscountedPackRate()
    {
        // 10% off 188.40 → 169.56 per 15's; one tablet = 16956 / 15 = 1130.4 → 1130.
        var line = PricingCalculator.CalculateSaleLine(new SaleLineInput
        {
            RatePerPackPaisa = 18840, Units = new PackQuantity(0, 1).ToUnits(15), UnitsPerPack = 15, DiscountPercent = 10, GstPercent = 12
        });
        Assert.Equal(1130, line.TotalPaisa);
        Assert.Equal(1256 - 1130, line.LineDiscountPaisa);
    }

    [Fact]
    public void PackQuantity_RejectsNegativeParts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackQuantity(-1, 0).ToUnits(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackQuantity(0, -1).ToUnits(10));
    }

    // ─── Bill discount distribution ───

    [Fact]
    public void DistributeAmount_PropertiesHoldForRandomBills()
    {
        var random = new Random(7);
        for (var i = 0; i < 3000; i++)
        {
            var weights = Enumerable.Range(0, random.Next(1, 12))
                .Select(_ => random.Next(4) == 0 ? 0L : random.Next(1, 300_000)).ToArray();
            var total = weights.Sum();
            if (total == 0) continue;
            var amount = (long)(random.NextDouble() * total);

            var shares = PricingCalculator.DistributeAmount(amount, weights);

            Assert.Equal(amount, shares.Sum());
            for (var j = 0; j < weights.Length; j++)
            {
                Assert.InRange(shares[j], 0, weights[j]);
                if (weights[j] == 0) Assert.Equal(0, shares[j]);
            }
        }
    }

    [Fact]
    public void DistributeAmount_WholeBillMakesEveryLineFree() =>
        Assert.Equal(new long[] { 1001, 0, 37 }, PricingCalculator.DistributeAmount(1038, new long[] { 1001, 0, 37 }));

    [Fact]
    public void DistributeAmount_RejectsNegativeInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.DistributeAmount(-1, new long[] { 10 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.DistributeAmount(1, new long[] { 10, -1 }));
        Assert.Throws<InvalidOperationException>(() => PricingCalculator.DistributeAmount(1, new long[] { 0, 0 }));
    }

    [Fact]
    public void BillDiscountInValue_ReducesTheBillByExactlyThatAmount()
    {
        // Three lines with odd amounts at different slabs; ₹10.01 off the bill, distributed by line total.
        var inputs = new[]
        {
            new SaleLineInput { RatePerPackPaisa = 18840, Units = 20, UnitsPerPack = 15, GstPercent = 12, DiscountPercent = 10 },
            new SaleLineInput { RatePerPackPaisa = 1001, Units = 1, GstPercent = 28 },
            new SaleLineInput { RatePerPackPaisa = 3550, Units = 7, UnitsPerPack = 10, GstPercent = 5 }
        };
        var before = inputs.Select(PricingCalculator.CalculateSaleLine).ToList();
        var shares = PricingCalculator.DistributeAmount(1001, before.Select(l => l.TotalPaisa).ToList());

        var after = inputs.Select((input, k) => PricingCalculator.CalculateSaleLine(input with { BillDiscountSharePaisa = shares[k] })).ToList();
        var totalsBefore = PricingCalculator.SummarizeBill(before, RoundOffMode.None);
        var totalsAfter = PricingCalculator.SummarizeBill(after, RoundOffMode.None);

        Assert.Equal(totalsBefore.NetPayablePaisa - 1001, totalsAfter.NetPayablePaisa);
        Assert.Equal(totalsBefore.DiscountPaisa + 1001, totalsAfter.DiscountPaisa);
        Assert.Equal(totalsAfter.NetPayablePaisa, totalsAfter.TaxablePaisa + totalsAfter.TaxPaisa);
        Assert.All(after, l => Assert.Equal(l.TotalPaisa, l.TaxablePaisa + l.TaxPaisa));
    }

    [Fact]
    public void BillDiscountShare_LargerThanTheLine_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculateSaleLine(
            new SaleLineInput { RatePerPackPaisa = 1000, Units = 1, BillDiscountSharePaisa = 1001 }));
}
