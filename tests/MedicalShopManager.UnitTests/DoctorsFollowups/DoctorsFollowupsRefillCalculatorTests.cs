using FluentAssertions;
using MedicalShopManager.Core.Modules.DoctorsFollowups;
using Xunit;

namespace MedicalShopManager.UnitTests.DoctorsFollowups;

/// <summary>
/// Refill maths (01-shell-dashboard §5 / MASTER-SPEC §4.9: next refill = sale date + units ÷ units per day,
/// Localwell default consumption 0.5), buckets, tabs, counts, sorting and input validation.
/// </summary>
public class DoctorsFollowupsRefillCalculatorTests
{
    private static readonly DateTime Today = new(2026, 11, 12);
    private static readonly FollowupSettings Settings = FollowupSettings.Default; // upcoming 7, grace 7

    private static PatientFollowup Followup(long id, DateTime due, FollowupStatus status = FollowupStatus.Pending, string customer = "Ravi",
        DateTime? saleDate = null) => new()
    {
        Id = id, CustomerId = id, CustomerName = customer, MedicineName = "Glycomet 500", SaleDate = saleDate ?? due.AddDays(-60),
        QuantityUnits = 30, UnitsPerDay = 0.5m, NextRefillDate = due, Status = status
    };

    [Theory]
    [InlineData(30, 0.5, 60)]   // Localwell default: a strip of 30 lasts 60 days
    [InlineData(10, 3, 3)]      // floor(3.33)
    [InlineData(1, 2, 1)]       // never less than a day
    [InlineData(1000, 0.5, 365)] // capped at a year
    [InlineData(0, 1, 0)]
    public void DaysSupply_FloorsAndClamps(long units, double perDay, int expected) =>
        RefillCalculator.DaysSupply(units, (decimal)perDay).Should().Be(expected);

    [Fact]
    public void DaysSupply_RejectsZeroConsumption()
    {
        var act = () => RefillCalculator.DaysSupply(10, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NextRefillDate_IsSaleDatePlusDaysSupply_IgnoringTheTime() =>
        RefillCalculator.NextRefillDate(new DateTime(2026, 10, 1, 18, 30, 0), 30, 0.5m).Should().Be(new DateTime(2026, 11, 30));

    [Theory]
    [InlineData(30, 0.5, 10, 25)]
    [InlineData(30, 1, 40, 0)]      // never negative
    [InlineData(10, 0.33, 1, 9.6)]  // 9.67 truncated to one decimal
    [InlineData(30, 0.5, -5, 30)]   // a future sale date counts as day 0
    public void RemainingUnits_DeductsConsumption(long units, double perDay, int daysSinceSale, double expected) =>
        RefillCalculator.RemainingUnits(units, (decimal)perDay, Today.AddDays(-daysSinceSale), Today).Should().Be((decimal)expected);

    [Theory]
    [InlineData(-8, FollowupBucket.Lapsed)]
    [InlineData(-7, FollowupBucket.Overdue)]   // the last day of the grace period
    [InlineData(-1, FollowupBucket.Overdue)]
    [InlineData(0, FollowupBucket.DueToday)]
    [InlineData(1, FollowupBucket.Upcoming)]
    [InlineData(7, FollowupBucket.Upcoming)]
    [InlineData(8, FollowupBucket.Later)]
    public void Classify_PendingByDaysToDue(int daysToDue, FollowupBucket expected) =>
        RefillCalculator.Classify(FollowupStatus.Pending, Today.AddDays(daysToDue), Today, Settings).Should().Be(expected);

    [Theory]
    [InlineData(FollowupStatus.Done)]
    [InlineData(FollowupStatus.Refilled)]
    [InlineData(FollowupStatus.Dismissed)]
    [InlineData(FollowupStatus.Cancelled)]
    public void Classify_NotPending_IsClosed(FollowupStatus status) =>
        RefillCalculator.Classify(status, Today, Today, Settings).Should().Be(FollowupBucket.Closed);

    [Fact]
    public void Classify_UsesTheConfiguredWindows()
    {
        var settings = new FollowupSettings { UpcomingDays = 2, GraceDays = 0 };
        RefillCalculator.Classify(FollowupStatus.Pending, Today.AddDays(3), Today, settings).Should().Be(FollowupBucket.Later);
        RefillCalculator.Classify(FollowupStatus.Pending, Today.AddDays(-1), Today, settings).Should().Be(FollowupBucket.Lapsed);
    }

    [Fact]
    public void IsInTab_MapsBucketsToTabs()
    {
        RefillCalculator.IsInTab(FollowupBucket.Overdue, FollowupStatus.Pending, FollowupTab.Arriving).Should().BeTrue();
        RefillCalculator.IsInTab(FollowupBucket.DueToday, FollowupStatus.Pending, FollowupTab.Arriving).Should().BeTrue();
        RefillCalculator.IsInTab(FollowupBucket.Upcoming, FollowupStatus.Pending, FollowupTab.Arriving).Should().BeTrue();
        RefillCalculator.IsInTab(FollowupBucket.Later, FollowupStatus.Pending, FollowupTab.Arriving).Should().BeFalse();
        RefillCalculator.IsInTab(FollowupBucket.Lapsed, FollowupStatus.Pending, FollowupTab.Arriving).Should().BeFalse();
        RefillCalculator.IsInTab(FollowupBucket.Lapsed, FollowupStatus.Pending, FollowupTab.Expired).Should().BeTrue();
        RefillCalculator.IsInTab(FollowupBucket.Closed, FollowupStatus.Done, FollowupTab.Actioned).Should().BeTrue();
        RefillCalculator.IsInTab(FollowupBucket.Closed, FollowupStatus.Dismissed, FollowupTab.Actioned).Should().BeFalse();
        RefillCalculator.IsInTab(FollowupBucket.Later, FollowupStatus.Pending, FollowupTab.AllPending).Should().BeTrue();
        RefillCalculator.IsInTab(FollowupBucket.Closed, FollowupStatus.Done, FollowupTab.AllPending).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_FillsBucketDaysAndUnitsLeft()
    {
        var followup = Followup(1, Today.AddDays(-3), saleDate: Today.AddDays(-63));

        var evaluated = RefillCalculator.Evaluate(followup, Today, Settings);

        evaluated.Bucket.Should().Be(FollowupBucket.Overdue);
        evaluated.DaysToDue.Should().Be(-3);
        evaluated.RemainingUnits.Should().Be(0m); // 30 − 0.5 × 63 < 0
        evaluated.DueLabel.Should().Be("Overdue by 3 days");
        evaluated.StatusLabel.Should().Be("Overdue");
    }

    [Fact]
    public void Evaluate_SnoozeMovesTheDueDate_OnlyWhenLaterThanTheRefillDate()
    {
        var snoozed = RefillCalculator.Evaluate(Followup(1, Today.AddDays(-3)) with { SnoozedUntil = Today.AddDays(2) }, Today, Settings);
        snoozed.DueDate.Should().Be(Today.AddDays(2));
        snoozed.IsSnoozed.Should().BeTrue();
        snoozed.Bucket.Should().Be(FollowupBucket.Upcoming);
        snoozed.DueLabel.Should().Be("Due in 2 days");

        var stale = RefillCalculator.Evaluate(Followup(2, Today.AddDays(5)) with { SnoozedUntil = Today.AddDays(1) }, Today, Settings);
        stale.DueDate.Should().Be(Today.AddDays(5));
        stale.IsSnoozed.Should().BeFalse();
    }

    [Fact]
    public void DueLabel_CoversEveryState()
    {
        RefillCalculator.Evaluate(Followup(1, Today), Today, Settings).DueLabel.Should().Be("Due today");
        RefillCalculator.Evaluate(Followup(2, Today.AddDays(1)), Today, Settings).DueLabel.Should().Be("Due in 1 day");
        RefillCalculator.Evaluate(Followup(3, Today.AddDays(-20)), Today, Settings).DueLabel.Should().Be("Lapsed 20 days ago");
        RefillCalculator.Evaluate(Followup(4, Today, FollowupStatus.Done), Today, Settings).DueLabel.Should().Be("Followed up");
        RefillCalculator.Evaluate(Followup(5, Today, FollowupStatus.Refilled), Today, Settings).DueLabel.Should().Be("Refilled");
    }

    [Fact]
    public void Count_GroupsByBucket()
    {
        var evaluated = new[]
        {
            Followup(1, Today.AddDays(-20)),                      // lapsed
            Followup(2, Today.AddDays(-2)),                       // overdue
            Followup(3, Today.AddDays(-1)),                       // overdue
            Followup(4, Today),                                   // today
            Followup(5, Today.AddDays(3)),                        // upcoming
            Followup(6, Today.AddDays(30)),                       // later
            Followup(7, Today, FollowupStatus.Done),              // actioned
            Followup(8, Today, FollowupStatus.Dismissed)          // not counted
        }.Select(f => RefillCalculator.Evaluate(f, Today, Settings));

        var counts = RefillCalculator.Count(evaluated);

        counts.Should().Be(new FollowupCounts(Arriving: 4, Expired: 1, Actioned: 1, Overdue: 2, DueToday: 1, Upcoming: 1, Later: 1));
        counts.AllPending.Should().Be(6);
    }

    [Fact]
    public void Sort_OrdersByTheChosenKey()
    {
        var a = Followup(1, Today.AddDays(2), customer: "Zubin", saleDate: Today.AddDays(-10));
        var b = Followup(2, Today.AddDays(1), customer: "anita", saleDate: Today.AddDays(-30));
        var c = Followup(3, Today.AddDays(1), customer: "Mohan", saleDate: Today.AddDays(-20));
        var items = new[] { a, b, c };

        RefillCalculator.Sort(items, FollowupSort.FollowupDateAscending).Select(f => f.Id).Should().Equal(2, 3, 1);
        RefillCalculator.Sort(items, FollowupSort.FollowupDateDescending).Select(f => f.Id).Should().Equal(1, 2, 3);
        RefillCalculator.Sort(items, FollowupSort.LastSaleDescending).Select(f => f.Id).Should().Equal(1, 3, 2);
        RefillCalculator.Sort(items, FollowupSort.PatientNameAscending).Select(f => f.Id).Should().Equal(2, 3, 1);
        RefillCalculator.Sort(items, FollowupSort.PatientNameDescending).Select(f => f.Id).Should().Equal(1, 3, 2);
    }

    [Theory]
    [InlineData(0.05, true)]
    [InlineData(50, true)]
    [InlineData(0.04, false)]
    [InlineData(50.01, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void ValidateUnitsPerDay_Bounds(double perDay, bool valid) =>
        (RefillCalculator.ValidateUnitsPerDay((decimal)perDay) is null).Should().Be(valid);

    [Fact]
    public void ValidateSnooze_MustBeAfterTodayAndWithin90Days()
    {
        RefillCalculator.ValidateSnooze(Today, Today).Should().Contain("after today");
        RefillCalculator.ValidateSnooze(Today.AddDays(-1), Today).Should().NotBeNull();
        RefillCalculator.ValidateSnooze(Today.AddDays(1), Today).Should().BeNull();
        RefillCalculator.ValidateSnooze(Today.AddDays(90), Today).Should().BeNull();
        RefillCalculator.ValidateSnooze(Today.AddDays(91), Today).Should().Contain("90 days");
    }

    [Fact]
    public void ValidateManual_AcceptsAGoodRequest()
    {
        var request = new ManualFollowupRequest { CustomerId = 1, MedicineId = 2, QuantityUnits = 30, UnitsPerDay = 1, StartDate = Today.AddDays(-3) };
        RefillCalculator.ValidateManual(request, Today).Should().BeEmpty();
    }

    [Fact]
    public void ValidateManual_ListsEveryProblem()
    {
        var request = new ManualFollowupRequest
        {
            CustomerId = 0, MedicineId = 0, QuantityUnits = 0, UnitsPerDay = 0, StartDate = Today.AddDays(1), Note = new string('x', 1001)
        };

        var errors = RefillCalculator.ValidateManual(request, Today);

        errors.Should().HaveCount(6);
        errors.Should().Contain("Choose the customer to follow up.");
        errors.Should().Contain("Choose the medicine.");
        errors.Should().Contain("The purchase date cannot be in the future.");
    }

    [Fact]
    public void ValidateManual_RejectsVeryOldDatesAndHugeQuantities()
    {
        var request = new ManualFollowupRequest { CustomerId = 1, MedicineId = 2, QuantityUnits = 100_001, UnitsPerDay = 1, StartDate = Today.AddYears(-1).AddDays(-1) };

        RefillCalculator.ValidateManual(request, Today).Should().BeEquivalentTo(
            "The units bought look too large.", "The purchase date is more than a year ago.");
    }
}
