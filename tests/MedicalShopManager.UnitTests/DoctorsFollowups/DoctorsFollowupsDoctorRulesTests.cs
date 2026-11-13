using FluentAssertions;
using MedicalShopManager.Core.Modules.DoctorsFollowups;
using Xunit;

namespace MedicalShopManager.UnitTests.DoctorsFollowups;

/// <summary>Doctor master rules, doctor-wise sales commission (through PricingCalculator) and customer metrics.</summary>
public class DoctorsFollowupsDoctorRulesTests
{
    // ───────────────────────── Names ─────────────────────────

    [Theory]
    [InlineData("Dr. A  Sharma", "A Sharma")]
    [InlineData("dr Sharma", "Sharma")]
    [InlineData("DR.Rao", "Rao")]
    [InlineData("  Meera   Nair ", "Meera Nair")]
    [InlineData("Drishti Rao", "Drishti Rao")] // "Dr" inside a name is not a prefix
    [InlineData(null, "")]
    public void StripPrefix_RemovesTheDoctorPrefixAndExtraSpaces(string? input, string expected) =>
        DoctorRules.StripPrefix(input).Should().Be(expected);

    [Fact]
    public void DisplayName_AddsThePrefixOnce()
    {
        DoctorRules.DisplayName("Dr. A Sharma").Should().Be("Dr. A Sharma");
        DoctorRules.DisplayName("A Sharma").Should().Be("Dr. A Sharma");
        DoctorRules.DisplayName("  ").Should().BeEmpty();
    }

    [Fact]
    public void Normalize_CleansEveryField()
    {
        var doctor = DoctorRules.Normalize(new Doctor
        {
            Name = " Dr.  Anil   Sharma ", RegistrationNo = " kmc  45678 ", Qualification = "  MBBS,  MD ", Specialization = " ",
            ClinicName = " City  Clinic ", Phone = "+91 98765-43210", Email = " a@b.in ", City = " Bengaluru ", Notes = "",
            CommissionPercent = 7.555m
        });

        doctor.Name.Should().Be("Anil Sharma");
        doctor.RegistrationNo.Should().Be("KMC 45678");
        doctor.Qualification.Should().Be("MBBS, MD");
        doctor.Specialization.Should().BeNull();
        doctor.ClinicName.Should().Be("City Clinic");
        doctor.Phone.Should().Be("9876543210");
        doctor.Email.Should().Be("a@b.in");
        doctor.City.Should().Be("Bengaluru");
        doctor.Notes.Should().BeNull();
        doctor.CommissionPercent.Should().Be(7.56m);
    }

    [Fact]
    public void Normalize_KeepsAnInvalidPhoneSoValidationCanReportIt()
    {
        var doctor = DoctorRules.Normalize(new Doctor { Name = "Rao", Phone = "12345" });
        doctor.Phone.Should().Be("12345");
        DoctorRules.Validate(doctor).Should().ContainSingle().Which.Should().Contain("10-digit");
    }

    // ───────────────────────── Validation ─────────────────────────

    [Fact]
    public void Validate_AcceptsAMinimalDoctor() =>
        DoctorRules.Validate(new Doctor { Name = "Rao" }).Should().BeEmpty();

    [Fact]
    public void Validate_AcceptsAFullDoctor() =>
        DoctorRules.Validate(new Doctor
        {
            Name = "Anil Sharma", RegistrationNo = "DMC/R/1234", Phone = "080 2345 6789", Email = "anil@clinic.in",
            CommissionPercent = 10, CommissionOnProfit = true
        }).Should().BeEmpty();

    [Theory]
    [InlineData("", "Enter the doctor's name.")]
    [InlineData("Dr. ", "Enter the doctor's name.")]
    [InlineData("A", "The doctor's name is too short.")]
    public void Validate_Name(string name, string expected) =>
        DoctorRules.Validate(new Doctor { Name = name }).Should().Contain(expected);

    [Fact]
    public void Validate_NameTooLong() =>
        DoctorRules.Validate(new Doctor { Name = new string('a', 101) }).Should().ContainSingle().Which.Should().Contain("at most 100");

    [Theory]
    [InlineData("KMC#1")]
    [InlineData("-123")]
    public void Validate_RegistrationNumberCharacters(string registrationNo) =>
        DoctorRules.Validate(new Doctor { Name = "Rao", RegistrationNo = registrationNo }).Should().ContainSingle()
            .Which.Should().Contain("registration number");

    [Fact]
    public void Validate_RegistrationNumberLength() =>
        DoctorRules.Validate(new Doctor { Name = "Rao", RegistrationNo = new string('1', 31) }).Should().ContainSingle()
            .Which.Should().Contain("at most 30");

    [Fact]
    public void Validate_EmailAndCommission()
    {
        DoctorRules.Validate(new Doctor { Name = "Rao", Email = "not-an-email" }).Should().Contain("Enter a valid e-mail address.");
        DoctorRules.Validate(new Doctor { Name = "Rao", CommissionPercent = 100.01m }).Should().Contain("Commission must be between 0 and 100 %.");
        DoctorRules.Validate(new Doctor { Name = "Rao", CommissionPercent = -1 }).Should().Contain("Commission must be between 0 and 100 %.");
        DoctorRules.Validate(new Doctor { Name = "Rao", CommissionOnProfit = true }).Should().ContainSingle().Which.Should().Contain("on profit");
    }

    [Fact]
    public void Validate_LongTexts() =>
        DoctorRules.Validate(new Doctor { Name = "Rao", ClinicName = new string('c', 201) }).Should().ContainSingle()
            .Which.Should().StartWith("Hospital / clinic");

    [Fact]
    public void FromQuickAdd_IsActiveWithoutCommission()
    {
        var doctor = DoctorRules.FromQuickAdd(new DoctorQuickAdd("Dr. Rao", "KMC 1", "Physician", "9876543210", "City Clinic"));

        doctor.Should().BeEquivalentTo(new Doctor
        {
            Name = "Dr. Rao", RegistrationNo = "KMC 1", Specialization = "Physician", Phone = "9876543210", ClinicName = "City Clinic", IsActive = true
        });
    }

    [Fact]
    public void Labels_ForListsAndLookups()
    {
        var doctor = new Doctor
        {
            Id = 4, Name = "A Sharma", RegistrationNo = "KMC 1234", Qualification = "MBBS, MD", Specialization = "Cardiologist",
            ClinicName = "City Clinic", CommissionPercent = 7.5m, CommissionOnProfit = true, IsActive = false
        };

        doctor.DisplayName.Should().Be("Dr. A Sharma");
        doctor.Subtitle.Should().Be("MBBS, MD · Cardiologist · City Clinic");
        doctor.CommissionLabel.Should().Be("7.5% on profit");
        doctor.StatusLabel.Should().Be("Inactive");
        new Doctor { Name = "X Y" }.CommissionLabel.Should().Be("-");

        var item = doctor.ToLookupItem();
        item.Id.Should().Be(4);
        item.IsActive.Should().BeFalse();
        item.Label.Should().Be("Dr. A Sharma (Reg. KMC 1234) · Cardiologist");
        new DoctorLookupItem(1, "Rao", null, null, null, null, null).Label.Should().Be("Dr. Rao");
    }

    // ───────────────────────── Doctor-wise sales ─────────────────────────

    [Fact]
    public void Commission_OnSales_IsPercentOfTheTaxableValue()
    {
        var row = DoctorSalesCalculator.WithCommission(new DoctorSalesRow
        {
            DoctorId = 1, DoctorName = "Rao", SalesPaisa = 112_000, TaxablePaisa = 100_000, CostPaisa = 60_000, CommissionPercent = 10
        });

        row.ProfitPaisa.Should().Be(40_000);
        row.CommissionBasePaisa.Should().Be(100_000);
        row.CommissionPaisa.Should().Be(10_000);
        row.CommissionLabel.Should().Be("10% on sales");
    }

    [Fact]
    public void Commission_OnProfit_IsPercentOfTaxableMinusCost()
    {
        var row = DoctorSalesCalculator.WithCommission(new DoctorSalesRow
        {
            DoctorId = 1, DoctorName = "Rao", TaxablePaisa = 100_000, CostPaisa = 60_000, CommissionPercent = 10, CommissionOnProfit = true
        });

        row.CommissionBasePaisa.Should().Be(40_000);
        row.CommissionPaisa.Should().Be(4_000);
    }

    [Fact]
    public void Commission_OnProfit_IsNeverNegative()
    {
        var row = DoctorSalesCalculator.WithCommission(new DoctorSalesRow
        {
            DoctorName = "Rao", TaxablePaisa = 50_000, CostPaisa = 60_000, CommissionPercent = 10, CommissionOnProfit = true
        });

        row.ProfitPaisa.Should().Be(-10_000);
        row.CommissionBasePaisa.Should().Be(0);
        row.CommissionPaisa.Should().Be(0);
    }

    [Fact]
    public void Commission_RoundsToThePaisa_HalfAwayFromZero()
    {
        // 333.33 × 2.5% = 8.33325 → ₹8.33; 2.50 × 5% = 0.125 → ₹0.13
        DoctorSalesCalculator.WithCommission(new DoctorSalesRow { TaxablePaisa = 33_333, CommissionPercent = 2.5m }).CommissionPaisa.Should().Be(833);
        DoctorSalesCalculator.WithCommission(new DoctorSalesRow { TaxablePaisa = 250, CommissionPercent = 5m }).CommissionPaisa.Should().Be(13);
    }

    [Fact]
    public void Commission_ZeroPercent_IsZero() =>
        DoctorSalesCalculator.WithCommission(new DoctorSalesRow { TaxablePaisa = 100_000 }).CommissionPaisa.Should().Be(0);

    [Fact]
    public void Build_SortsBySalesAndTotals()
    {
        var summary = DoctorSalesCalculator.Build(new DateTime(2026, 10, 1, 9, 0, 0), new DateTime(2026, 10, 31), new[]
        {
            new DoctorSalesRow { DoctorId = 1, DoctorName = "Rao", BillCount = 2, SalesPaisa = 22_400, TaxablePaisa = 20_000, CostPaisa = 12_000, CommissionPercent = 5 },
            new DoctorSalesRow { DoctorId = 2, DoctorName = "Iyer", BillCount = 3, SalesPaisa = 56_000, TaxablePaisa = 50_000, CostPaisa = 30_000, CommissionPercent = 10, CommissionOnProfit = true },
            new DoctorSalesRow { DoctorName = "Bose", BillCount = 1, SalesPaisa = 22_400, TaxablePaisa = 20_000, CostPaisa = 15_000 }
        });

        summary.Rows.Select(r => r.DoctorName).Should().Equal("Iyer", "Bose", "Rao");
        summary.From.Should().Be(new DateTime(2026, 10, 1));
        summary.Totals.Should().Be(new DoctorSalesTotals(Doctors: 3, Bills: 6, SalesPaisa: 100_800, TaxablePaisa: 90_000, ProfitPaisa: 33_000, CommissionPaisa: 3_000));
        summary.Title.Should().Be("Doctor-wise sales 01-10-2026 to 31-10-2026");
        summary.Rows[1].IsLinked.Should().BeFalse();
        summary.Rows[1].LinkLabel.Should().Be("not in doctor master");
    }

    [Fact]
    public void ValidateRange_RejectsReversedAndVeryLongPeriods()
    {
        DoctorSalesCalculator.ValidateRange(new DateTime(2026, 10, 2), new DateTime(2026, 10, 1)).Should().Contain("before");
        DoctorSalesCalculator.ValidateRange(new DateTime(2025, 1, 1), new DateTime(2026, 6, 1)).Should().Contain("366");
        DoctorSalesCalculator.ValidateRange(new DateTime(2026, 4, 1), new DateTime(2027, 3, 31)).Should().BeNull();
        DoctorSalesCalculator.ValidateRange(new DateTime(2026, 10, 1), new DateTime(2026, 10, 1)).Should().BeNull();
    }

    // ───────────────────────── Customer card ─────────────────────────

    [Fact]
    public void CustomerMetrics_AverageOrderAndRevisit()
    {
        var summary = CustomerMetrics.Build(7, "Ravi", "9876543210", null, 3, 100_000, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31, 17, 0, 0));

        summary.AverageOrderValuePaisa.Should().Be(33_333);
        summary.AverageRevisitDays.Should().Be(15);
        summary.RevisitLabel.Should().Be("15 days");
    }

    [Fact]
    public void CustomerMetrics_NoRevisitWithFewerThanTwoBills()
    {
        CustomerMetrics.Build(7, "Ravi", null, null, 1, 5_000, new DateTime(2026, 1, 1), new DateTime(2026, 1, 1)).AverageRevisitDays.Should().BeNull();
        var none = CustomerMetrics.Build(7, "Ravi", null, null, 0, 0, null, null);
        none.AverageOrderValuePaisa.Should().Be(0);
        none.RevisitLabel.Should().Be("-");
    }
}
