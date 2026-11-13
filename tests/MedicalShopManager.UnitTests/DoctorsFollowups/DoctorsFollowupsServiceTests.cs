using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Core.Modules.DoctorsFollowups;
using MedicalShopManager.Infrastructure.Modules.DoctorsFollowups;
using Moq;
using Xunit;

namespace MedicalShopManager.UnitTests.DoctorsFollowups;

/// <summary>Doctor and follow-up service rules with mocked repositories: validation before writes, audit after writes.</summary>
public class DoctorsFollowupsServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 10, 30, 0);
    private static readonly DateTime Today = Now.Date;

    private readonly Mock<IDoctorRepository> _doctors = new();
    private readonly Mock<IFollowupRepository> _followups = new();
    private readonly Mock<IAppSettingsRepository> _settings = new();
    private readonly Mock<IControlCenterSettingsService> _controlCenter = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IAuditService> _audit = new();

    public DoctorsFollowupsServiceTests()
    {
        _controlCenter.Setup(c => c.GetSaleSettingsAsync()).ReturnsAsync(new SaleSettings { PatientReminders = true });
        _controlCenter.Setup(c => c.GetBusinessProfileAsync()).ReturnsAsync(new BusinessProfile { ShopName = "Sri Sai Medicals", Phone = "9000000001" });
        _auth.SetupGet(a => a.CurrentUser).Returns(new User { Id = 3, Username = "asha", DisplayName = "Asha", Role = UserRole.BillingOperator });
        _followups.Setup(f => f.GetPendingKeysAsync()).ReturnsAsync(new HashSet<(long, string, string)>());
    }

    private DoctorService Doctors() => new(_doctors.Object, _audit.Object, NullLogger<DoctorService>.Instance);

    private FollowupService Followups() => new(_followups.Object, _settings.Object, _controlCenter.Object, _auth.Object, _audit.Object,
        NullLogger<FollowupService>.Instance, () => Now);

    private void VerifyAudited(string action, Times? times = null) =>
        _audit.Verify(a => a.LogAsync(action, It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            times ?? Times.Once());

    private static PatientFollowup Pending(long id, DateTime due, long customerId = 5) => new()
    {
        Id = id, CustomerId = customerId, CustomerName = "Ravi Kumar", CustomerPhone = "9876543210", MedicineName = "Glycomet 500",
        SaleDate = Today.AddDays(-20), QuantityUnits = 30, UnitsPerDay = 0.5m, NextRefillDate = due
    };

    // ───────────────────────── Doctors ─────────────────────────

    [Fact]
    public async Task SaveDoctor_New_NormalisesInsertsAndAudits()
    {
        Doctor? saved = null;
        _doctors.Setup(d => d.InsertAsync(It.IsAny<Doctor>())).Callback<Doctor>(d => saved = d).ReturnsAsync(9);

        var id = await Doctors().SaveAsync(new Doctor { Name = " Dr.  Anil Sharma", RegistrationNo = "kmc 123", Phone = "+91 98765 43210", CommissionPercent = 5 });

        id.Should().Be(9);
        saved!.Name.Should().Be("Anil Sharma");
        saved.RegistrationNo.Should().Be("KMC 123");
        saved.Phone.Should().Be("9876543210");
        VerifyAudited("CreateDoctor");
    }

    [Fact]
    public async Task SaveDoctor_Invalid_IsRejectedBeforeAnyWrite()
    {
        var act = () => Doctors().SaveAsync(new Doctor { Name = "", CommissionPercent = 120 });

        (await act.Should().ThrowAsync<DoctorsFollowupsValidationException>()).Which.Errors.Should().HaveCount(2);
        _doctors.Verify(d => d.InsertAsync(It.IsAny<Doctor>()), Times.Never);
        VerifyAudited("CreateDoctor", Times.Never());
    }

    [Fact]
    public async Task SaveDoctor_DuplicateRegistrationNumber_IsRejected()
    {
        _doctors.Setup(d => d.FindByRegistrationNoAsync("KMC 123", null)).ReturnsAsync(new Doctor { Id = 4, Name = "Rao" });

        var act = () => Doctors().SaveAsync(new Doctor { Name = "Sharma", RegistrationNo = "KMC 123" });

        (await act.Should().ThrowAsync<DoctorsFollowupsValidationException>()).Which.Message.Should().Be("Registration no. KMC 123 is already used by Dr. Rao.");
        _doctors.Verify(d => d.InsertAsync(It.IsAny<Doctor>()), Times.Never);
    }

    [Fact]
    public async Task SaveDoctor_Update_ExcludesItselfFromTheDuplicateCheckAndAuditsTheCommissionChange()
    {
        _doctors.Setup(d => d.GetAsync(4)).ReturnsAsync(new Doctor { Id = 4, Name = "Rao", RegistrationNo = "KMC 123" });
        _doctors.Setup(d => d.UpdateAsync(It.IsAny<Doctor>())).ReturnsAsync(true);

        await Doctors().SaveAsync(new Doctor { Id = 4, Name = "Rao", RegistrationNo = "KMC 123", CommissionPercent = 10, CommissionOnProfit = true });

        _doctors.Verify(d => d.FindByRegistrationNoAsync("KMC 123", 4), Times.Once);
        _audit.Verify(a => a.LogAsync("UpdateDoctor", "Doctor", 4, It.IsAny<string?>(), It.IsAny<string?>(),
            "Doctor Dr. Rao updated; commission - → 10% on profit"), Times.Once);
    }

    [Fact]
    public async Task SaveDoctor_UpdateOfAMissingDoctor_IsRejected()
    {
        var act = () => Doctors().SaveAsync(new Doctor { Id = 4, Name = "Rao" });

        await act.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("The doctor no longer exists.");
    }

    [Fact]
    public async Task SetActive_ChangesAndAuditsOnlyWhenTheStateDiffers()
    {
        _doctors.Setup(d => d.GetAsync(4)).ReturnsAsync(new Doctor { Id = 4, Name = "Rao", IsActive = true });

        await Doctors().SetActiveAsync(4, true);
        _doctors.Verify(d => d.SetActiveAsync(It.IsAny<long>(), It.IsAny<bool>()), Times.Never);

        await Doctors().SetActiveAsync(4, false);
        _doctors.Verify(d => d.SetActiveAsync(4, false), Times.Once);
        VerifyAudited("DeactivateDoctor");
    }

    [Fact]
    public async Task QuickAdd_ReturnsTheExistingDoctorWithTheSameRegistrationNumber_AndReactivatesIt()
    {
        var existing = new Doctor { Id = 4, Name = "Rao", RegistrationNo = "KMC 123", IsActive = false };
        _doctors.Setup(d => d.FindByRegistrationNoAsync("KMC 123", null)).ReturnsAsync(existing);
        _doctors.Setup(d => d.GetAsync(4)).ReturnsAsync(existing);

        var item = await Doctors().QuickAddDoctorAsync(new DoctorQuickAdd("Dr. Rao", "kmc 123"));

        item.Id.Should().Be(4);
        item.IsActive.Should().BeTrue();
        _doctors.Verify(d => d.SetActiveAsync(4, true), Times.Once);
        _doctors.Verify(d => d.InsertAsync(It.IsAny<Doctor>()), Times.Never);
    }

    [Fact]
    public async Task QuickAdd_WithoutRegistrationNumber_MatchesNameAndPhone_ElseInserts()
    {
        _doctors.Setup(d => d.FindByNameAndPhoneAsync("Rao", "9876543210")).ReturnsAsync(new Doctor { Id = 4, Name = "Rao", Phone = "9876543210" });
        (await Doctors().QuickAddDoctorAsync(new DoctorQuickAdd("Dr. Rao", Phone: "98765 43210"))).Id.Should().Be(4);

        _doctors.Setup(d => d.InsertAsync(It.IsAny<Doctor>())).ReturnsAsync(11);
        var added = await Doctors().QuickAddDoctorAsync(new DoctorQuickAdd("Dr. Meera Nair", Specialization: "Physician"));

        added.Id.Should().Be(11);
        added.Name.Should().Be("Meera Nair");
        added.Label.Should().Be("Dr. Meera Nair · Physician");
        VerifyAudited("CreateDoctor");
    }

    [Fact]
    public async Task QuickAdd_Invalid_IsRejected()
    {
        var act = () => Doctors().QuickAddDoctorAsync(new DoctorQuickAdd(" ", Phone: "123"));
        (await act.Should().ThrowAsync<DoctorsFollowupsValidationException>()).Which.Errors.Should().HaveCount(2);
    }

    [Fact]
    public async Task FindDoctors_ReturnsActiveLookupItems()
    {
        _doctors.Setup(d => d.SearchAsync("rao", false, 25)).ReturnsAsync(new[] { new Doctor { Id = 4, Name = "Rao", RegistrationNo = "KMC 123" } });

        var items = await Doctors().FindDoctorsAsync(" rao ");

        items.Should().ContainSingle().Which.Label.Should().Be("Dr. Rao (Reg. KMC 123)");
    }

    [Fact]
    public async Task SalesSummary_ValidatesThePeriodAndComputesCommission()
    {
        var act = () => Doctors().GetSalesSummaryAsync(Today, Today.AddDays(-1), true);
        await act.Should().ThrowAsync<DoctorsFollowupsValidationException>();

        _doctors.Setup(d => d.GetSalesSummaryAsync(Today.AddDays(-30), Today, true)).ReturnsAsync(new[]
        {
            new DoctorSalesRow { DoctorId = 4, DoctorName = "Rao", BillCount = 2, SalesPaisa = 22_400, TaxablePaisa = 20_000, CostPaisa = 12_000, CommissionPercent = 5 }
        });
        var summary = await Doctors().GetSalesSummaryAsync(Today.AddDays(-30).AddHours(9), Today.AddHours(20), true);

        summary.Totals.CommissionPaisa.Should().Be(1_000);
        summary.Totals.ProfitPaisa.Should().Be(8_000);
    }

    [Fact]
    public async Task Bills_OfAnUnlinkedRow_AreFoundByTheTypedName()
    {
        await Doctors().GetBillsAsync(new DoctorSalesRow { DoctorName = "Iyer" }, Today, Today);
        _doctors.Verify(d => d.GetBillsAsync(null, "Iyer", Today, Today), Times.Once);

        await Doctors().GetBillsAsync(new DoctorSalesRow { DoctorId = 4, DoctorName = "Rao" }, Today, Today);
        _doctors.Verify(d => d.GetBillsAsync(4, null, Today, Today), Times.Once);
    }

    // ───────────────────────── Settings and generation ─────────────────────────

    [Fact]
    public async Task Settings_AreReadFromAppSettings_WithDefaultsForMissingKeys()
    {
        _settings.Setup(s => s.GetValueAsync(FollowupSettings.UpcomingDaysKey)).ReturnsAsync("10");

        var settings = await Followups().GetSettingsAsync();

        settings.Should().Be(new FollowupSettings { UpcomingDays = 10 });
    }

    [Fact]
    public async Task SaveSettings_ValidatesWritesEveryKeyAndAudits()
    {
        var invalid = () => Followups().SaveSettingsAsync(new FollowupSettings { LookbackDays = 1 });
        await invalid.Should().ThrowAsync<DoctorsFollowupsValidationException>();
        _settings.Verify(s => s.SetValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);

        await Followups().SaveSettingsAsync(new FollowupSettings { DefaultUnitsPerDay = 1, GraceDays = 3 });

        _settings.Verify(s => s.SetValueAsync(FollowupSettings.DefaultUnitsPerDayKey, "1", It.IsAny<string?>()), Times.Once);
        _settings.Verify(s => s.SetValueAsync(FollowupSettings.GraceDaysKey, "3", It.IsAny<string?>()), Times.Once);
        _settings.Verify(s => s.SetValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Exactly(4));
        VerifyAudited("SaveFollowupSettings");
    }

    [Fact]
    public async Task Generate_IsSkippedWhilePatientRemindersAreOff()
    {
        _controlCenter.Setup(c => c.GetSaleSettingsAsync()).ReturnsAsync(new SaleSettings { PatientReminders = false });

        var result = await Followups().GenerateFromSalesAsync();

        result.Should().Be(FollowupGenerationResult.None);
        _followups.Verify(f => f.GetChronicSaleLinesAsync(It.IsAny<DateTime>()), Times.Never);
        (await Followups().IsEnabledAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Generate_StaysOnWhenTheSaleSettingsCannotBeRead()
    {
        _controlCenter.Setup(c => c.GetSaleSettingsAsync()).ThrowsAsync(new InvalidOperationException("no settings"));
        (await Followups().IsEnabledAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Generate_ScansTheLookbackWindowAndAppliesThePlanOnce()
    {
        var from = Today.AddDays(-120);
        _followups.Setup(f => f.GetChronicSaleLinesAsync(from)).ReturnsAsync(new[]
        {
            new FollowupSaleLine(1, "INV-1", Today.AddDays(-2), 5, "Ravi Kumar", null, 10, "Glycomet 500", 3, "Metformin", 30, null)
        });
        _followups.Setup(f => f.GetForGenerationAsync(from)).ReturnsAsync(Array.Empty<ExistingFollowup>());
        FollowupPlan? applied = null;
        _followups.Setup(f => f.ApplyPlanAsync(It.IsAny<FollowupPlan>(), 3, "Asha", Now)).Callback<FollowupPlan, long?, string?, DateTime>((p, _, _, _) => applied = p)
            .Returns(Task.CompletedTask);

        var result = await Followups().GenerateFromSalesAsync();

        result.Should().Be(new FollowupGenerationResult(1, 0, 0, 0));
        applied!.Inserts.Should().ContainSingle().Which.NextRefillDate.Should().Be(Today.AddDays(58));
    }

    [Fact]
    public async Task Generate_WithNothingToChange_DoesNotWrite()
    {
        _followups.Setup(f => f.GetChronicSaleLinesAsync(It.IsAny<DateTime>())).ReturnsAsync(Array.Empty<FollowupSaleLine>());
        _followups.Setup(f => f.GetForGenerationAsync(It.IsAny<DateTime>())).ReturnsAsync(Array.Empty<ExistingFollowup>());

        (await Followups().GenerateFromSalesAsync()).HasChanges.Should().BeFalse();
        _followups.Verify(f => f.ApplyPlanAsync(It.IsAny<FollowupPlan>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<DateTime>()), Times.Never);
    }

    // ───────────────────────── List ─────────────────────────

    private void SetupList()
    {
        _followups.Setup(f => f.QueryAsync(It.Is<FollowupQuery>(q => q.Tab == FollowupTab.AllPending))).ReturnsAsync(new[]
        {
            Pending(1, Today.AddDays(-20)), Pending(2, Today.AddDays(-2)), Pending(3, Today), Pending(4, Today.AddDays(5)), Pending(5, Today.AddDays(40))
        });
        _followups.Setup(f => f.QueryAsync(It.Is<FollowupQuery>(q => q.Tab == FollowupTab.Actioned))).ReturnsAsync(new[]
        {
            Pending(6, Today.AddDays(-1)) with { Status = FollowupStatus.Done }
        });
    }

    [Theory]
    [InlineData(FollowupTab.Arriving, new long[] { 2, 3, 4 })]
    [InlineData(FollowupTab.Expired, new long[] { 1 })]
    [InlineData(FollowupTab.Actioned, new long[] { 6 })]
    [InlineData(FollowupTab.AllPending, new long[] { 1, 2, 3, 4, 5 })]
    public async Task GetList_FiltersTheTabAndCountsEveryTab(FollowupTab tab, long[] expected)
    {
        SetupList();

        var result = await Followups().GetListAsync(new FollowupQuery { Tab = tab });

        result.Items.Select(f => f.Id).Should().Equal(expected);
        result.Counts.Should().Be(new FollowupCounts(Arriving: 3, Expired: 1, Actioned: 1, Overdue: 1, DueToday: 1, Upcoming: 1, Later: 1));
    }

    [Fact]
    public async Task DashboardFeed_ReturnsTheMostUrgentDueReminders()
    {
        SetupList();
        _followups.Setup(f => f.GetChronicSaleLinesAsync(It.IsAny<DateTime>())).ThrowsAsync(new InvalidOperationException("db busy"));

        var due = await Followups().GetDueRemindersAsync(2);
        var counts = await Followups().GetCountsAsync();

        due.Select(f => f.Id).Should().Equal(2, 3); // a failing generation does not break the card
        due[0].Bucket.Should().Be(FollowupBucket.Overdue);
        counts.Arriving.Should().Be(3);
    }

    // ───────────────────────── Actions ─────────────────────────

    [Fact]
    public async Task MarkDone_NeedsASelectionAndSomethingPending()
    {
        var none = () => Followups().MarkDoneAsync(Array.Empty<long>(), null);
        await none.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("Please select a follow-up to continue.");

        var closed = () => Followups().MarkDoneAsync(new long[] { 1 }, null);
        await closed.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("The selected follow-ups are no longer pending.");

        var longNote = () => Followups().MarkDoneAsync(new long[] { 1 }, new string('x', 1001));
        await longNote.Should().ThrowAsync<DoctorsFollowupsValidationException>();
        VerifyAudited("FollowupDone", Times.Never());
    }

    [Fact]
    public async Task MarkDone_ClosesWithTheUserAndAudits()
    {
        var ids = new long[] { 1, 2 };
        _followups.Setup(f => f.CloseAsync(ids, FollowupStatus.Done, null, "Called, will come Friday", 3, "Asha", Now)).ReturnsAsync(2);

        (await Followups().MarkDoneAsync(ids, "Called, will come Friday")).Should().Be(2);
        VerifyAudited("FollowupDone");
    }

    [Fact]
    public async Task Dismiss_NeedsAReason()
    {
        var blank = () => Followups().DismissAsync(new long[] { 1 }, "  ");
        await blank.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("Enter why the reminder is no longer needed.");

        var tooLong = () => Followups().DismissAsync(new long[] { 1 }, new string('x', 201));
        await tooLong.Should().ThrowAsync<DoctorsFollowupsValidationException>();

        _followups.Setup(f => f.CloseAsync(It.IsAny<IReadOnlyCollection<long>>(), FollowupStatus.Dismissed, "Therapy stopped", null, 3, "Asha", Now)).ReturnsAsync(1);
        (await Followups().DismissAsync(new long[] { 1 }, " Therapy stopped ")).Should().Be(1);
        VerifyAudited("FollowupDismissed");
    }

    [Fact]
    public async Task Reopen_OnlyClosedByTheUser()
    {
        var act = () => Followups().ReopenAsync(1);
        await act.Should().ThrowAsync<DoctorsFollowupsValidationException>();

        _followups.Setup(f => f.ReopenAsync(1, Now)).ReturnsAsync(true);
        await Followups().ReopenAsync(1);
        VerifyAudited("FollowupReopened");
    }

    [Fact]
    public async Task Snooze_ValidatesTheDate()
    {
        var today = () => Followups().SnoozeAsync(1, Today);
        await today.Should().ThrowAsync<DoctorsFollowupsValidationException>();
        var far = () => Followups().SnoozeAsync(1, Today.AddDays(91));
        await far.Should().ThrowAsync<DoctorsFollowupsValidationException>();
        var notPending = () => Followups().SnoozeAsync(1, Today.AddDays(3));
        await notPending.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("Only pending reminders can be snoozed.");

        _followups.Setup(f => f.SnoozeAsync(1, Today.AddDays(3), Now)).ReturnsAsync(true);
        await Followups().SnoozeAsync(1, Today.AddDays(3).AddHours(15));

        _followups.Setup(f => f.SnoozeAsync(1, null, Now)).ReturnsAsync(true);
        await Followups().ClearSnoozeAsync(1);
        _followups.Verify(f => f.SnoozeAsync(1, null, Now), Times.Once);
    }

    [Fact]
    public async Task UpdateConsumption_RecomputesTheRefillDate()
    {
        var invalid = () => Followups().UpdateConsumptionAsync(1, 0);
        await invalid.Should().ThrowAsync<DoctorsFollowupsValidationException>();
        var missing = () => Followups().UpdateConsumptionAsync(1, 1);
        await missing.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("The follow-up no longer exists.");

        _followups.Setup(f => f.GetAsync(2)).ReturnsAsync(Pending(2, Today) with { Status = FollowupStatus.Done });
        var closed = () => Followups().UpdateConsumptionAsync(2, 1);
        await closed.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("Only pending reminders can be changed.");

        _followups.Setup(f => f.GetAsync(1)).ReturnsAsync(Pending(1, Today.AddDays(40)));
        _followups.Setup(f => f.UpdateConsumptionAsync(1, 2m, Today.AddDays(-5), Now)).ReturnsAsync(true); // 30 ÷ 2 = 15 days after the sale 20 days ago
        await Followups().UpdateConsumptionAsync(1, 2m);

        _followups.Verify(f => f.UpdateConsumptionAsync(1, 2m, Today.AddDays(-5), Now), Times.Once);
        VerifyAudited("FollowupConsumption");
    }

    // ───────────────────────── Manual follow-ups and recommendations ─────────────────────────

    private void SetupCustomerAndItem()
    {
        _followups.Setup(f => f.GetCustomerSummaryAsync(5)).ReturnsAsync(CustomerMetrics.Build(5, "Ravi Kumar", "9876543210", null, 0, 0, null, null));
        _followups.Setup(f => f.GetItemAsync(10)).ReturnsAsync(new FollowupItemOption(10, "Glycomet 500", 3, "Metformin", true, null, 15));
    }

    private static ManualFollowupRequest Manual(string? patient = null) => new()
    {
        CustomerId = 5, MedicineId = 10, PatientName = patient, StartDate = Today.AddDays(-10), QuantityUnits = 30, UnitsPerDay = 1, Note = " asked to remind "
    };

    [Fact]
    public async Task CreateManual_InsertsAPendingFollowupAndAudits()
    {
        SetupCustomerAndItem();
        NewFollowup? inserted = null;
        _followups.Setup(f => f.InsertAsync(It.IsAny<NewFollowup>(), 3, "Asha", Now)).Callback<NewFollowup, long?, string?, DateTime>((n, _, _, _) => inserted = n)
            .ReturnsAsync(77);

        var id = await Followups().CreateManualAsync(Manual("ravi kumar"));

        id.Should().Be(77);
        inserted!.PatientKey.Should().BeEmpty();
        inserted.PatientName.Should().BeNull(); // the customer themselves
        inserted.ItemKey.Should().Be("C3");
        inserted.SaleId.Should().BeNull();
        inserted.NextRefillDate.Should().Be(Today.AddDays(20));
        inserted.UnitsPerDayIsManual.Should().BeTrue();
        inserted.Source.Should().Be(FollowupSource.Manual);
        inserted.Note.Should().Be("asked to remind");
        VerifyAudited("CreateFollowup");
    }

    [Fact]
    public async Task CreateManual_RejectsInvalidUnknownAndDuplicateRequests()
    {
        var invalid = () => Followups().CreateManualAsync(new ManualFollowupRequest());
        (await invalid.Should().ThrowAsync<DoctorsFollowupsValidationException>()).Which.Errors.Count.Should().BeGreaterThan(2);

        var unknownCustomer = () => Followups().CreateManualAsync(Manual());
        await unknownCustomer.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("The customer no longer exists.");

        SetupCustomerAndItem();
        _followups.Setup(f => f.GetPendingKeysAsync()).ReturnsAsync(new HashSet<(long, string, string)> { (5, "", "C3") });
        var duplicate = () => Followups().CreateManualAsync(Manual());
        await duplicate.Should().ThrowAsync<DoctorsFollowupsValidationException>()
            .WithMessage("Ravi Kumar already has a pending reminder for Glycomet 500 (Metformin).");
        _followups.Verify(f => f.InsertAsync(It.IsAny<NewFollowup>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task AcceptRecommendation_CreatesTheFollowup_AndOptionallyMarksTheComposition()
    {
        SetupCustomerAndItem();
        _followups.Setup(f => f.InsertAsync(It.IsAny<NewFollowup>(), 3, "Asha", Now)).ReturnsAsync(78);
        var recommendation = new RefillRecommendation(5, "Ravi Kumar", null, null, "", "C3", 10, "Glycomet 500", 3, "Metformin", 3,
            Today.AddDays(-60), Today.AddDays(-5), 30, 28, 1m, Today.AddDays(23));

        (await Followups().AcceptRecommendationAsync(recommendation, alwaysRemind: false)).Should().Be(78);
        _followups.Verify(f => f.SaveRuleAsync(It.IsAny<RefillRule>(), It.IsAny<DateTime>()), Times.Never);

        await Followups().AcceptRecommendationAsync(recommendation, alwaysRemind: true);
        _followups.Verify(f => f.SaveRuleAsync(It.Is<RefillRule>(r => r.CompositionId == 3 && r.MedicineId == null && r.RuleIsChronic == true), Now), Times.Once);
        _followups.Verify(f => f.InsertAsync(It.Is<NewFollowup>(n => n.Source == FollowupSource.Recommended), 3, "Asha", Now), Times.Exactly(2));
        VerifyAudited("SaveRefillRule");
    }

    [Fact]
    public async Task Recommendations_UseHalfAYearOfBillsAndSkipPendingItems()
    {
        _followups.Setup(f => f.GetPurchaseHistoryAsync(Today.AddDays(-180))).ReturnsAsync(new[]
        {
            new FollowupPurchaseRow(5, "Ravi Kumar", null, null, 1, Today.AddDays(-60), 7, "Shelcal 500", null, null, 10),
            new FollowupPurchaseRow(5, "Ravi Kumar", null, null, 2, Today.AddDays(-30), 7, "Shelcal 500", null, null, 10)
        });

        (await Followups().GetRecommendationsAsync()).Should().ContainSingle().Which.ItemKey.Should().Be("M7");

        _followups.Setup(f => f.GetPendingKeysAsync()).ReturnsAsync(new HashSet<(long, string, string)> { (5, "", "M7") });
        (await Followups().GetRecommendationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveRule_ValidatesAndAudits()
    {
        var invalid = () => Followups().SaveRuleAsync(new RefillRule());
        await invalid.Should().ThrowAsync<DoctorsFollowupsValidationException>();

        await Followups().SaveRuleAsync(new RefillRule { MedicineId = 7, Name = "Shelcal 500", RuleIsChronic = true, UnitsPerDay = 1 });
        _followups.Verify(f => f.SaveRuleAsync(It.Is<RefillRule>(r => r.MedicineId == 7), Now), Times.Once);
        VerifyAudited("SaveRefillRule");
    }

    // ───────────────────────── Remarks and message ─────────────────────────

    [Fact]
    public async Task Notes_NeedTextAndAnExistingFollowup()
    {
        var blank = () => Followups().AddNoteAsync(1, "  ");
        await blank.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("Type a remark first.");
        var missing = () => Followups().AddNoteAsync(1, "called");
        await missing.Should().ThrowAsync<DoctorsFollowupsValidationException>();

        _followups.Setup(f => f.GetAsync(1)).ReturnsAsync(Pending(1, Today));
        await Followups().AddNoteAsync(1, " called ");
        _followups.Verify(f => f.AddNoteAsync(1, "called", 3, "Asha", Now), Times.Once);
    }

    [Fact]
    public async Task Notes_AreChangedOnlyByTheirAuthorOrAnAdministrator()
    {
        _followups.Setup(f => f.GetNoteAsync(1)).ReturnsAsync(new FollowupNote { Id = 1, UserId = 3, Note = "mine" });
        _followups.Setup(f => f.GetNoteAsync(2)).ReturnsAsync(new FollowupNote { Id = 2, UserId = 8, Note = "someone else's" });

        await Followups().UpdateNoteAsync(1, "edited");
        _followups.Verify(f => f.UpdateNoteAsync(1, "edited", Now), Times.Once);

        var edit = () => Followups().UpdateNoteAsync(2, "edited");
        await edit.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("You can change only your own remarks.");
        var delete = () => Followups().DeleteNoteAsync(2);
        await delete.Should().ThrowAsync<DoctorsFollowupsValidationException>();
        var gone = () => Followups().DeleteNoteAsync(99);
        await gone.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("The remark no longer exists.");

        _auth.SetupGet(a => a.CurrentUser).Returns(new User { Id = 1, Username = "admin", Role = UserRole.Administrator });
        await Followups().DeleteNoteAsync(2);
        _followups.Verify(f => f.DeleteNoteAsync(2, Now), Times.Once);
    }

    [Fact]
    public async Task BuildMessage_UsesTheBusinessProfile()
    {
        _followups.Setup(f => f.GetManyAsync(It.IsAny<IReadOnlyCollection<long>>())).ReturnsAsync(new[] { Pending(1, Today.AddDays(2)) });

        var message = await Followups().BuildMessageAsync(new long[] { 1 });

        message.Message.Should().Contain("*Sri Sai Medicals*").And.Contain("Call us on *9000000001*").And.Contain("Due on: *Nov 14, 2026*");
        message.Mobile.Should().Be("9876543210");
    }

    [Fact]
    public async Task BuildMessage_FallsBackToAGenericShop_AndRejectsAnEmptySelection()
    {
        _controlCenter.Setup(c => c.GetBusinessProfileAsync()).ThrowsAsync(new InvalidOperationException("no profile"));
        _followups.Setup(f => f.GetManyAsync(It.IsAny<IReadOnlyCollection<long>>())).ReturnsAsync(new[] { Pending(1, Today) });
        (await Followups().BuildMessageAsync(new long[] { 1 })).Message.Should().Contain("Medicine Refill Reminder");

        _followups.Setup(f => f.GetManyAsync(It.IsAny<IReadOnlyCollection<long>>())).ReturnsAsync(Array.Empty<PatientFollowup>());
        var act = () => Followups().BuildMessageAsync(Array.Empty<long>());
        await act.Should().ThrowAsync<DoctorsFollowupsValidationException>().WithMessage("Please select a follow-up to continue.");
    }
}
