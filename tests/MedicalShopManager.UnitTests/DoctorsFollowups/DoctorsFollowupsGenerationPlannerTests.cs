using FluentAssertions;
using MedicalShopManager.Core.Modules.DoctorsFollowups;
using Xunit;

namespace MedicalShopManager.UnitTests.DoctorsFollowups;

/// <summary>
/// Follow-ups from bills of chronic medicines: one per bill and salt, updated when the bill changes, only the latest
/// purchase of a patient stays pending, cancelled with the bill; and the recommended reminders from repeat purchases.
/// </summary>
public class DoctorsFollowupsGenerationPlannerTests
{
    private static readonly DateTime D0 = new(2026, 10, 1);
    private static readonly DateTime ScanFrom = D0.AddDays(-120);
    private const decimal Default = 0.5m;

    private static FollowupSaleLine Line(long saleId, DateTime date, long units = 30, long customerId = 5, string? patient = null,
        long medicineId = 10, string medicine = "Glycomet 500", long? compositionId = 3, decimal? rule = null) =>
        new(saleId, $"INV-{saleId}", date, customerId, "Ravi Kumar", patient, medicineId, medicine, compositionId,
            compositionId is null ? null : "Metformin", units, rule);

    private static ExistingFollowup Existing(long id, long? saleId, DateTime date, long units = 30, decimal perDay = Default, bool manualRate = false,
        FollowupStatus status = FollowupStatus.Pending, FollowupSource source = FollowupSource.Sale, string patientKey = "", string itemKey = "C3",
        long customerId = 5) =>
        new(id, customerId, patientKey, itemKey, saleId, date, units, perDay, manualRate, status, source);

    private static FollowupPlan Plan(IReadOnlyList<FollowupSaleLine> lines, params ExistingFollowup[] existing) =>
        FollowupGenerationPlanner.Plan(lines, existing, Default, ScanFrom);

    [Fact]
    public void NewChronicLine_CreatesAPendingFollowupAtTheDefaultConsumption()
    {
        var plan = Plan(new[] { Line(1, D0.AddHours(18)) });

        plan.Updates.Should().BeEmpty();
        plan.Closures.Should().BeEmpty();
        var insert = plan.Inserts.Should().ContainSingle().Subject;
        insert.CustomerId.Should().Be(5);
        insert.PatientKey.Should().BeEmpty();
        insert.PatientName.Should().BeNull();
        insert.ItemKey.Should().Be("C3");
        insert.SaleId.Should().Be(1);
        insert.InvoiceNumber.Should().Be("INV-1");
        insert.SaleDate.Should().Be(D0);
        insert.QuantityUnits.Should().Be(30);
        insert.UnitsPerDay.Should().Be(0.5m);
        insert.NextRefillDate.Should().Be(D0.AddDays(60)); // 30 ÷ 0.5
        insert.Status.Should().Be(FollowupStatus.Pending);
        insert.Source.Should().Be(FollowupSource.Sale);
        FollowupGenerationResult.From(plan).Should().Be(new FollowupGenerationResult(1, 0, 0, 0));
    }

    [Fact]
    public void ItemRuleConsumption_BeatsTheDefault()
    {
        var insert = Plan(new[] { Line(1, D0, rule: 2m) }).Inserts.Single();

        insert.UnitsPerDay.Should().Be(2m);
        insert.NextRefillDate.Should().Be(D0.AddDays(15));
    }

    [Fact]
    public void InvalidRuleConsumption_FallsBackToTheDefault() =>
        Plan(new[] { Line(1, D0, rule: 500m) }).Inserts.Single().UnitsPerDay.Should().Be(0.5m);

    [Fact]
    public void LinesOfTheSameSaltOnOneBill_AddUp()
    {
        var plan = Plan(new[] { Line(1, D0, 30), Line(1, D0, 15, medicineId: 11, medicine: "Gluconorm 500") });

        var insert = plan.Inserts.Should().ContainSingle().Subject;
        insert.QuantityUnits.Should().Be(45);
        insert.MedicineId.Should().Be(10);
        insert.MedicineName.Should().Be("Glycomet 500 / Gluconorm 500");
        insert.NextRefillDate.Should().Be(D0.AddDays(90));
    }

    [Fact]
    public void ItemsWithoutComposition_KeyOnTheMedicine()
    {
        var plan = Plan(new[] { Line(1, D0, compositionId: null), Line(1, D0, medicineId: 11, medicine: "Thyronorm 50", compositionId: null) });

        plan.Inserts.Select(i => i.ItemKey).Should().BeEquivalentTo("M10", "M11");
    }

    [Fact]
    public void WalkInLinesAndZeroQuantities_AreIgnored() =>
        Plan(new[] { Line(1, D0, customerId: 0), Line(2, D0, units: 0) }).IsEmpty.Should().BeTrue();

    [Fact]
    public void RunningAgain_ChangesNothing()
    {
        var plan = Plan(new[] { Line(1, D0) }, Existing(100, 1, D0));

        plan.IsEmpty.Should().BeTrue();
        FollowupGenerationResult.From(plan).HasChanges.Should().BeFalse();
    }

    [Fact]
    public void EditedBill_UpdatesThePendingFollowup()
    {
        var plan = Plan(new[] { Line(1, D0.AddDays(1), 60, patient: "Sita Devi") }, Existing(100, 1, D0, 30));

        plan.Inserts.Should().BeEmpty();
        plan.Closures.Should().BeEmpty();
        var update = plan.Updates.Should().ContainSingle().Subject;
        update.Id.Should().Be(100);
        update.QuantityUnits.Should().Be(60);
        update.PatientName.Should().Be("Sita Devi");
        update.PatientKey.Should().Be("sita devi");
        update.SaleDate.Should().Be(D0.AddDays(1));
        update.NextRefillDate.Should().Be(D0.AddDays(121));
    }

    [Fact]
    public void ConsumptionTypedByTheUser_SurvivesRegeneration()
    {
        Plan(new[] { Line(1, D0, rule: 2m) }, Existing(100, 1, D0, 30, perDay: 1m, manualRate: true)).IsEmpty.Should().BeTrue();

        var update = Plan(new[] { Line(1, D0, 60, rule: 2m) }, Existing(100, 1, D0, 30, perDay: 1m, manualRate: true)).Updates.Single();
        update.UnitsPerDay.Should().Be(1m);
        update.NextRefillDate.Should().Be(D0.AddDays(60));
    }

    [Fact]
    public void ChangedItemRule_UpdatesFollowupsWithoutAManualConsumption()
    {
        var update = Plan(new[] { Line(1, D0, rule: 1m) }, Existing(100, 1, D0)).Updates.Single();

        update.UnitsPerDay.Should().Be(1m);
        update.NextRefillDate.Should().Be(D0.AddDays(30));
    }

    [Fact]
    public void FollowedUpOrDismissedFollowups_AreNotTouchedByTheirBill()
    {
        Plan(new[] { Line(1, D0, 60) }, Existing(100, 1, D0, status: FollowupStatus.Done)).IsEmpty.Should().BeTrue();
        Plan(new[] { Line(1, D0, 60) }, Existing(100, 1, D0, status: FollowupStatus.Dismissed)).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void NewerBill_RefillsTheOlderPendingFollowup()
    {
        var plan = Plan(new[] { Line(1, D0), Line(2, D0.AddDays(30)) }, Existing(100, 1, D0));

        plan.Inserts.Should().ContainSingle().Which.SaleId.Should().Be(2);
        plan.Inserts[0].Status.Should().Be(FollowupStatus.Pending);
        var closure = plan.Closures.Should().ContainSingle().Subject;
        closure.Should().Be(new FollowupClosure(100, FollowupStatus.Refilled, 2, "Bought again on 31-10-2026 (bill INV-2)."));
        FollowupGenerationResult.From(plan).Should().Be(new FollowupGenerationResult(1, 0, 1, 0));
    }

    [Fact]
    public void FirstRunOverTwoBills_KeepsOnlyTheLatestPending()
    {
        var plan = Plan(new[] { Line(1, D0), Line(2, D0.AddDays(30)) });

        plan.Inserts.Should().HaveCount(2);
        var older = plan.Inserts.Single(i => i.SaleId == 1);
        older.Status.Should().Be(FollowupStatus.Refilled);
        older.ClosedBySaleId.Should().Be(2);
        older.StatusReason.Should().Contain("INV-2");
        plan.Inserts.Single(i => i.SaleId == 2).Status.Should().Be(FollowupStatus.Pending);
        plan.Closures.Should().BeEmpty();
        FollowupGenerationResult.From(plan).Created.Should().Be(1);
    }

    [Fact]
    public void BrandSwitchWithinTheSameSalt_CountsAsTheRefill()
    {
        var plan = Plan(new[] { Line(1, D0), Line(2, D0.AddDays(30), medicineId: 11, medicine: "Gluconorm 500") }, Existing(100, 1, D0));

        plan.Closures.Should().ContainSingle().Which.Status.Should().Be(FollowupStatus.Refilled);
        plan.Inserts.Single().MedicineName.Should().Be("Gluconorm 500");
    }

    [Fact]
    public void DifferentPatientsOfOneCustomer_KeepTheirOwnFollowups()
    {
        var plan = Plan(new[] { Line(1, D0), Line(2, D0.AddDays(30), patient: "Sita Devi") }, Existing(100, 1, D0));

        plan.Closures.Should().BeEmpty();
        plan.Inserts.Should().ContainSingle().Which.PatientKey.Should().Be("sita devi");
    }

    [Fact]
    public void PatientNamedSelfOrLikeTheCustomer_IsTheCustomer()
    {
        var plan = Plan(new[] { Line(1, D0, patient: " self "), Line(2, D0.AddDays(30), patient: "RAVI  kumar") });

        plan.Inserts.Should().OnlyContain(i => i.PatientKey == string.Empty);
        plan.Inserts.Count(i => i.Status == FollowupStatus.Pending).Should().Be(1);
    }

    [Fact]
    public void BillBeatsAManualFollowup_EvenOnTheSameDay()
    {
        var manual = Existing(200, null, D0, source: FollowupSource.Manual);

        var plan = Plan(new[] { Line(1, D0) }, manual);

        plan.Inserts.Single().Status.Should().Be(FollowupStatus.Pending);
        plan.Closures.Should().ContainSingle().Which.Should().Be(new FollowupClosure(200, FollowupStatus.Refilled, 1, "Bought again on 01-10-2026 (bill INV-1)."));
    }

    [Fact]
    public void NewerManualFollowup_RefillsTheBillFollowup()
    {
        var plan = Plan(new[] { Line(1, D0) }, Existing(100, 1, D0), Existing(200, null, D0.AddDays(10), source: FollowupSource.Manual));

        plan.Inserts.Should().BeEmpty();
        plan.Closures.Should().ContainSingle().Which.Should().Be(new FollowupClosure(100, FollowupStatus.Refilled, null, "Bought again on 11-10-2026."));
    }

    [Fact]
    public void CancelledBillOrRemovedLine_CancelsItsPendingFollowup()
    {
        var plan = Plan(Array.Empty<FollowupSaleLine>(), Existing(100, 9, D0));

        plan.Closures.Should().ContainSingle().Which.Should().Be(
            new FollowupClosure(100, FollowupStatus.Cancelled, null, FollowupGenerationPlanner.CancelReason));
        FollowupGenerationResult.From(plan).Cancelled.Should().Be(1);
    }

    [Fact]
    public void FollowupsOfBillsOutsideTheScannedWindow_AreLeftAlone() =>
        Plan(Array.Empty<FollowupSaleLine>(), Existing(100, 9, ScanFrom.AddDays(-1))).IsEmpty.Should().BeTrue();

    [Fact]
    public void ManualFollowups_AreNeverCancelled() =>
        Plan(Array.Empty<FollowupSaleLine>(), Existing(200, null, D0, source: FollowupSource.Manual)).IsEmpty.Should().BeTrue();

    [Fact]
    public void AnUpdateOfAFollowupClosedInTheSameRun_IsDropped()
    {
        var plan = Plan(new[] { Line(1, D0, 60), Line(2, D0.AddDays(30)) }, Existing(100, 1, D0, 30));

        plan.Updates.Should().BeEmpty();
        plan.Closures.Should().ContainSingle().Which.Id.Should().Be(100);
    }

    [Fact]
    public void InvalidDefaultConsumption_IsRejected()
    {
        var act = () => FollowupGenerationPlanner.Plan(Array.Empty<FollowupSaleLine>(), Array.Empty<ExistingFollowup>(), 0m, ScanFrom);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GenerationResult_Summary()
    {
        FollowupGenerationResult.None.Summary.Should().Be("Follow-ups are up to date with the bills.");
        new FollowupGenerationResult(2, 1, 1, 0).Summary.Should().Be("2 new, 1 updated, 1 refilled, 0 cancelled follow-up(s) from bills.");
    }

    // ───────────────────────── Recommended reminders ─────────────────────────

    private static readonly DateTime Today = new(2026, 11, 12);
    private static readonly IReadOnlySet<(long, string, string)> NoPending = new HashSet<(long, string, string)>();

    private static FollowupPurchaseRow Purchase(long saleId, DateTime date, long units = 10, long customerId = 1, long medicineId = 7,
        long? compositionId = null, string? patient = null) =>
        new(customerId, "Meena Rao", "9876543210", patient, saleId, date, medicineId, "Shelcal 500", compositionId,
            compositionId is null ? null : "Calcium", units);

    [Fact]
    public void Recommend_RepeatPurchasesAtASteadyInterval()
    {
        var rows = new[] { Purchase(1, Today.AddDays(-60)), Purchase(2, Today.AddDays(-30), 15) };

        var recommendation = RefillRecommender.Recommend(rows, NoPending, Today).Should().ContainSingle().Subject;

        recommendation.CustomerId.Should().Be(1);
        recommendation.ItemKey.Should().Be("M7");
        recommendation.PatientKey.Should().BeEmpty();
        recommendation.PurchaseCount.Should().Be(2);
        recommendation.AverageIntervalDays.Should().Be(30);
        recommendation.LastUnits.Should().Be(15);
        recommendation.SuggestedUnitsPerDay.Should().Be(0.33m); // 10 units over 30 days
        recommendation.SuggestedDueDate.Should().Be(Today);
        recommendation.PatternLabel.Should().Be("2 purchases, about every 30 days");
        recommendation.PatientDisplay.Should().Be("Meena Rao");
    }

    [Fact]
    public void Recommend_SkipsPendingSinglePurchasesAndIrregularOrStalePatterns()
    {
        var pending = new HashSet<(long, string, string)> { (1, "", "M7") };
        RefillRecommender.Recommend(new[] { Purchase(1, Today.AddDays(-60)), Purchase(2, Today.AddDays(-30)) }, pending, Today).Should().BeEmpty();

        RefillRecommender.Recommend(new[] { Purchase(1, Today.AddDays(-30)) }, NoPending, Today).Should().BeEmpty();
        // two bills on one day are one purchase
        RefillRecommender.Recommend(new[] { Purchase(1, Today.AddDays(-30)), Purchase(2, Today.AddDays(-30).AddHours(5)) }, NoPending, Today).Should().BeEmpty();
        // every 2 days is not a refill pattern
        RefillRecommender.Recommend(new[] { Purchase(1, Today.AddDays(-3)), Purchase(2, Today.AddDays(-1)) }, NoPending, Today).Should().BeEmpty();
        // more than 120 days apart
        RefillRecommender.Recommend(new[] { Purchase(1, Today.AddDays(-150)), Purchase(2, Today.AddDays(-10)) }, NoPending, Today).Should().BeEmpty();
        // would have been due 140 days ago
        RefillRecommender.Recommend(new[] { Purchase(1, Today.AddDays(-200)), Purchase(2, Today.AddDays(-170)) }, NoPending, Today).Should().BeEmpty();
    }

    [Fact]
    public void Recommend_GroupsByCompositionAndPatient_AndSortsByDueDate()
    {
        var rows = new[]
        {
            Purchase(1, Today.AddDays(-40), compositionId: 4), Purchase(2, Today.AddDays(-20), compositionId: 4, medicineId: 8), // same salt, brand switch
            Purchase(3, Today.AddDays(-50), customerId: 2), Purchase(4, Today.AddDays(-40), customerId: 2),
            Purchase(5, Today.AddDays(-45), patient: "Baby Rao"), Purchase(6, Today.AddDays(-15), patient: "Baby Rao")
        };

        var list = RefillRecommender.Recommend(rows, NoPending, Today);

        list.Select(r => (r.CustomerId, r.ItemKey, r.PatientKey)).Should().Equal((2, "M7", ""), (1, "C4", ""), (1, "M7", "baby rao"));
        list[1].MedicineId.Should().Be(8); // the latest brand bought
        list[2].PatientDisplay.Should().Be("Baby Rao");
        RefillRecommender.Recommend(rows, NoPending, Today, limit: 1).Should().ContainSingle();
    }

    [Fact]
    public void Recommend_ClampsTheSuggestedConsumption()
    {
        var rows = new[] { Purchase(1, Today.AddDays(-20), units: 5000), Purchase(2, Today.AddDays(-10)) };

        RefillRecommender.Recommend(rows, NoPending, Today).Single().SuggestedUnitsPerDay.Should().Be(RefillCalculator.MaxUnitsPerDay);
    }

    [Fact]
    public void ToRequest_BecomesARecommendedFollowupFromTheLastPurchase()
    {
        var recommendation = RefillRecommender.Recommend(new[] { Purchase(1, Today.AddDays(-60)), Purchase(2, Today.AddDays(-30), 15) }, NoPending, Today).Single();

        var request = RefillRecommender.ToRequest(recommendation);

        request.CustomerId.Should().Be(1);
        request.MedicineId.Should().Be(7);
        request.StartDate.Should().Be(Today.AddDays(-30));
        request.QuantityUnits.Should().Be(15);
        request.UnitsPerDay.Should().Be(0.33m);
        request.Source.Should().Be(FollowupSource.Recommended);
        request.Note.Should().Be("Recommended: 2 purchases, about every 30 days.");
    }
}
