using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Reports;
using Xunit;

namespace MedicalShopManager.UnitTests.Reports;

/// <summary>
/// Report arithmetic through PricingCalculator: stock values per pack (golden vector: 20 units of a 15's strip at
/// ₹188.40 = ₹251.20, PricingCalculatorTests), returned cost share, margin, ageing, expiry, FSN and the drug register.
/// </summary>
public class ReportsMathTests
{
    private static readonly DateTime AsOf = new(2026, 11, 5);

    [Fact]
    public void StockValue_UsesTheGoldenVector()
    {
        ReportMath.StockValue(18840, 20, 15).Should().Be(25120);
        ReportMath.StockValue(18840, 0, 15).Should().Be(0);
        ReportMath.StockValue(18840, 3, 0).Should().Be(56520, "a pack size below 1 is treated as 1");
    }

    [Theory]
    [InlineData(6000L, 30L, 3L, 0L, 1, 600L)]
    [InlineData(1000L, 3L, 1L, 0L, 1, 333L)]
    [InlineData(1000L, 3L, 2L, 0L, 1, 667L)]
    [InlineData(1000L, 3L, 5L, 0L, 1, 1000L)]
    [InlineData(0L, 0L, 4L, 9000L, 15, 2400L)]
    [InlineData(500L, 10L, 0L, 0L, 1, 0L)]
    public void ReturnedCost_IsProRataToTheBillLine_ElseAtBatchRate(long lineCost, long lineUnits, long returned, long batchRate, int pack, long expected)
    {
        ReportMath.ReturnedCost(lineCost, lineUnits, returned, batchRate, pack).Should().Be(expected);
    }

    [Fact]
    public void MarginAndShare_ArePercentages()
    {
        ReportMath.MarginPercent(3600, 9000).Should().Be(40m);
        ReportMath.MarginPercent(1, 3).Should().Be(33.33m);
        ReportMath.MarginPercent(500, 0).Should().Be(0m);
        ReportMath.MarginPercent(-500, 1000).Should().Be(-50m);
        ReportMath.SharePercent(250, 1000).Should().Be(25m);
        ReportMath.SharePercent(250, 0).Should().Be(0m);
    }

    [Fact]
    public void Ageing_BucketsByDaysSinceTheDocument()
    {
        var ageing = new ReportAgeing();
        ageing.Add(AsOf, 100, AsOf);
        ageing.Add(AsOf.AddDays(-30), 200, AsOf);
        ageing.Add(AsOf.AddDays(-31), 300, AsOf);
        ageing.Add(AsOf.AddDays(-90), 400, AsOf);
        ageing.Add(AsOf.AddDays(-91), 500, AsOf);
        ageing.Add(AsOf.AddDays(10), 50, AsOf);
        ageing.Add(AsOf.AddDays(-400), 0, AsOf);
        ageing.Days0To30.Should().Be(350);
        ageing.Days31To60.Should().Be(300);
        ageing.Days61To90.Should().Be(400);
        ageing.Over90.Should().Be(500);
        ageing.Total.Should().Be(1550);
        ageing.Documents.Should().Be(6);
        ageing.Oldest.Should().Be(AsOf.AddDays(-91));
    }

    [Fact]
    public void Expiry_IsExpiredFromTheExpiryDate()
    {
        ReportExpiry.IsExpired(AsOf, AsOf).Should().BeTrue();
        ReportExpiry.IsExpired(AsOf.AddDays(1), AsOf).Should().BeFalse();
        ReportExpiry.IsNearExpiry(AsOf.AddDays(90), AsOf, 90).Should().BeTrue();
        ReportExpiry.IsNearExpiry(AsOf.AddDays(91), AsOf, 90).Should().BeFalse();
        ReportExpiry.StatusLabel(AsOf.AddDays(-1), AsOf, 90).Should().Be("Expired");
        ReportExpiry.StatusLabel(AsOf.AddDays(5), AsOf, 90).Should().Be("Near expiry");
        ReportExpiry.StatusLabel(AsOf.AddDays(500), AsOf, 90).Should().Be("OK");
        ReportExpiry.DaysLeft(AsOf.AddDays(56), AsOf).Should().Be(56);
    }

    [Theory]
    [InlineData(ReportChoiceKeys.All, -1, true)]
    [InlineData(ReportChoiceKeys.All, 30, true)]
    [InlineData(ReportChoiceKeys.All, 200, false)]
    [InlineData(ReportChoiceKeys.Expired, -1, true)]
    [InlineData(ReportChoiceKeys.Expired, 30, false)]
    [InlineData(ReportChoiceKeys.NearExpiry, -1, false)]
    [InlineData(ReportChoiceKeys.NearExpiry, 30, true)]
    public void Expiry_FilterMatchesTheChoice(string filter, int daysFromAsOf, bool expected)
    {
        ReportExpiry.Matches(filter, AsOf.AddDays(daysFromAsOf), AsOf, 90).Should().Be(expected);
    }

    [Fact]
    public void Fsn_ParetoBySalesValue_TopSellerAlwaysFast()
    {
        var classes = MovementClassifier.Classify(new[]
        {
            new ItemMovementInput { MedicineId = 1, Name = "A", SalesValuePaisa = 5000, UnitsSold = 10, StockUnits = 5 },
            new ItemMovementInput { MedicineId = 2, Name = "B", SalesValuePaisa = 2500, UnitsSold = 10 },
            new ItemMovementInput { MedicineId = 3, Name = "C", SalesValuePaisa = 1500, UnitsSold = 3 },
            new ItemMovementInput { MedicineId = 4, Name = "D", SalesValuePaisa = 1000, UnitsSold = 1 },
            new ItemMovementInput { MedicineId = 5, Name = "E", StockUnits = 40 },
            new ItemMovementInput { MedicineId = 6, Name = "F" }
        });
        classes[1].Should().Be(MovementClass.Fast);
        classes[2].Should().Be(MovementClass.Fast, "the share before it is 50 % (< 80 %)");
        classes[3].Should().Be(MovementClass.Fast, "the share before it is 75 %, so it is the item that crosses 80 %");
        classes[4].Should().Be(MovementClass.Slow, "the share before it is 90 %");
        classes[5].Should().Be(MovementClass.NonMoving);
    }

    [Fact]
    public void Fsn_ClassesFollowTheCumulativeShare()
    {
        var classes = MovementClassifier.Classify(new[]
        {
            new ItemMovementInput { MedicineId = 1, Name = "A", SalesValuePaisa = 9000, UnitsSold = 10 },
            new ItemMovementInput { MedicineId = 2, Name = "B", SalesValuePaisa = 600, UnitsSold = 10 },
            new ItemMovementInput { MedicineId = 3, Name = "C", SalesValuePaisa = 400, UnitsSold = 3 },
            new ItemMovementInput { MedicineId = 5, Name = "E", StockUnits = 40 },
            new ItemMovementInput { MedicineId = 6, Name = "F" }
        });
        classes[1].Should().Be(MovementClass.Fast);
        classes[2].Should().Be(MovementClass.Slow);
        classes[3].Should().Be(MovementClass.Slow);
        classes[5].Should().Be(MovementClass.NonMoving);
        classes.Should().NotContainKey(6, "no stock and no sales is not classified");
        MovementClassifier.Matches(ReportChoiceKeys.NonMoving, MovementClass.NonMoving).Should().BeTrue();
        MovementClassifier.Matches(ReportChoiceKeys.Fast, MovementClass.Slow).Should().BeFalse();
        MovementClassifier.Matches(ReportChoiceKeys.All, MovementClass.Slow).Should().BeTrue();
    }

    [Theory]
    [InlineData(30L, 90L, 90, 30L)]
    [InlineData(10L, 3L, 30, 100L)]
    [InlineData(10L, 0L, 30, null)]
    public void DaysOfCover_IsStockOverAverageDailySale(long stock, long sold, int days, long? expected)
    {
        MovementClassifier.DaysOfCover(stock, sold, days).Should().Be(expected);
    }

    [Fact]
    public void DrugRegister_RunsBalancesPerItemWithOpeningAndClosingRows()
    {
        var from = new DateTime(2026, 10, 1);
        var table = DrugRegisterBuilder.Build("Register", from,
            new[]
            {
                new DrugRegisterOpening(1, "Codeine syrup", DrugSchedule.X, 10),
                new DrugRegisterOpening(2, "Tramadol 50", DrugSchedule.NRx, 0),
                new DrugRegisterOpening(3, "Unused", DrugSchedule.X, 0)
            },
            new[]
            {
                new DrugRegisterEntry { MedicineId = 1, MedicineName = "Codeine syrup", Schedule = DrugSchedule.X, Date = from.AddDays(5), Particulars = "Supplied (sale)", QuantityOut = 2, SortKey = 2 },
                new DrugRegisterEntry { MedicineId = 1, MedicineName = "Codeine syrup", Schedule = DrugSchedule.X, Date = from.AddDays(2), Particulars = "Received (purchase)", QuantityIn = 6, SortKey = 1 },
                new DrugRegisterEntry { MedicineId = 2, MedicineName = "Tramadol 50", Schedule = DrugSchedule.NRx, Date = from.AddDays(3), Particulars = "Received (purchase)", QuantityIn = 10, SortKey = 3 }
            });

        table.Rows.Should().HaveCount(7, "item 3 has no opening and no movement");
        table.Rows[0].Style.Should().Be(ReportRowStyle.Opening);
        table.Value(0, "Balance").Should().Be(10L);
        table.Value(1, "Particulars").Should().Be("Received (purchase)");
        table.Value(1, "Balance").Should().Be(16L);
        table.Value(2, "Balance").Should().Be(14L);
        table.Rows[3].Style.Should().Be(ReportRowStyle.Subtotal);
        table.Value(3, "Qty in").Should().Be(6L);
        table.Value(3, "Qty out").Should().Be(2L);
        table.Value(3, "Balance").Should().Be(14L);
        table.Value(4, "Item").Should().Be("Tramadol 50");
        table.Value(6, "Balance").Should().Be(10L);
        table.Total("Qty in").Should().Be(16L, "closing rows are not added twice");
    }
}
