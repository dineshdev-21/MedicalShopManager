using FluentAssertions;
using MedicalShopManager.Core.Modules.DoctorsFollowups;
using Xunit;

namespace MedicalShopManager.UnitTests.DoctorsFollowups;

/// <summary>
/// Refill reminder text and links (Localwell template, 01-shell-dashboard §7: WhatsApp deep link
/// https://api.whatsapp.com/send/?phone=91&lt;mobile&gt;&amp;text=…), patient / item keys, settings and refill rules.
/// </summary>
public class DoctorsFollowupsMessageAndSettingsTests
{
    // ───────────────────────── Phone numbers ─────────────────────────

    [Theory]
    [InlineData("9876543210", "9876543210")]
    [InlineData("+91 98765-43210", "9876543210")]
    [InlineData("098765 43210", "9876543210")]
    [InlineData("0919876543210", "9876543210")]
    [InlineData("011 2345 6789", "1123456789")] // landline with STD code
    [InlineData("12345", null)]
    [InlineData("0000000000", null)]
    [InlineData("98765432101", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeIndianPhone(string? input, string? expected) =>
        FollowupMessageBuilder.NormalizeIndianPhone(input).Should().Be(expected);

    [Theory]
    [InlineData("98765 43210", "9876543210")]
    [InlineData("6000000000", "6000000000")]
    [InlineData("011 2345 6789", null)] // a landline cannot receive WhatsApp
    [InlineData("5876543210", null)]
    public void NormalizeIndianMobile(string input, string? expected) =>
        FollowupMessageBuilder.NormalizeIndianMobile(input).Should().Be(expected);

    [Fact]
    public void Links_AreBuiltOnlyForValidNumbers()
    {
        FollowupMessageBuilder.WhatsAppLink("+91 98765 43210", "Hi there & welcome")
            .Should().Be("https://api.whatsapp.com/send/?phone=919876543210&text=Hi%20there%20%26%20welcome");
        FollowupMessageBuilder.WhatsAppLink("011 2345 6789", "Hi").Should().BeNull();
        FollowupMessageBuilder.WhatsAppLink(null, "Hi").Should().BeNull();
        FollowupMessageBuilder.CallLink("011 2345 6789").Should().Be("tel:+911123456789");
        FollowupMessageBuilder.CallLink("abc").Should().BeNull();
    }

    // ───────────────────────── Message ─────────────────────────

    [Fact]
    public void RefillMessage_FollowsTheLocalwellTemplate()
    {
        var message = FollowupMessageBuilder.RefillMessage(" Ravi Kumar ", "Sri Sai Medicals", "080-1234567", new[]
        {
            new RefillMessageItem("Glycomet 500", "Metformin", new DateTime(2026, 12, 5)),
            new RefillMessageItem("Thyronorm 50", null, new DateTime(2026, 12, 9))
        });

        message.Should().Be(
            "Greetings *Ravi Kumar*,\n\n" +
            "This is a friendly *Medicine Refill Reminder* from your Trusted Pharmacy *Sri Sai Medicals*\n" +
            "\nBrand Name: *Glycomet 500*\nGeneric Name: Metformin\nDue on: *Dec 05, 2026*\n" +
            "\nBrand Name: *Thyronorm 50*\nDue on: *Dec 09, 2026*\n" +
            "\nTo *Reorder*, You can either *Reply Back* to this Message or Call us on *080-1234567*");
    }

    [Fact]
    public void RefillMessage_WithoutShopPhoneOrNames_UsesFallbacks()
    {
        var message = FollowupMessageBuilder.RefillMessage("", " ", null, new[] { new RefillMessageItem("Dolo", null, new DateTime(2026, 1, 2)) });

        message.Should().StartWith("Greetings *Customer*,");
        message.Should().Contain("*our pharmacy*");
        message.Should().EndWith("To *Reorder*, You can *Reply Back* to this Message");
    }

    [Fact]
    public void RefillMessage_NeedsAtLeastOneMedicine()
    {
        var act = () => FollowupMessageBuilder.RefillMessage("Ravi", "Shop", null, Array.Empty<RefillMessageItem>());
        act.Should().Throw<ArgumentException>();
    }

    private static PatientFollowup Followup(long id, long customerId, string medicine, DateTime due, string? patient = null, string? phone = "98765 43210") => new()
    {
        Id = id, CustomerId = customerId, CustomerName = "Ravi Kumar", CustomerPhone = phone, PatientName = patient,
        PatientKey = FollowupKeys.PatientKey(patient, "Ravi Kumar"), MedicineName = medicine, NextRefillDate = due, QuantityUnits = 30, UnitsPerDay = 1
    };

    [Fact]
    public void Build_OneMessageForTheSelectedMedicinesOfOnePatient_SortedByDueDate()
    {
        var selected = new[]
        {
            Followup(2, 1, "Thyronorm 50", new DateTime(2026, 12, 9)),
            Followup(1, 1, "Glycomet 500", new DateTime(2026, 12, 5))
        };

        var (message, error) = FollowupMessageBuilder.Build(selected, "Sri Sai Medicals", "9000000001");

        error.Should().BeNull();
        message!.PatientName.Should().Be("Ravi Kumar");
        message.Mobile.Should().Be("9876543210");
        message.HasMobile.Should().BeTrue();
        message.FollowupIds.Should().Equal(2, 1);
        message.Message.IndexOf("Glycomet 500", StringComparison.Ordinal).Should().BeLessThan(message.Message.IndexOf("Thyronorm 50", StringComparison.Ordinal));
        message.WhatsAppLink.Should().StartWith("https://api.whatsapp.com/send/?phone=919876543210&text=Greetings%20%2ARavi%20Kumar%2A");
        message.CallLink.Should().Be("tel:+919876543210");
    }

    [Fact]
    public void Build_WithoutAMobile_StillGivesTheText()
    {
        var (message, error) = FollowupMessageBuilder.Build(new[] { Followup(1, 1, "Glycomet 500", new DateTime(2026, 12, 5), phone: null) }, "Shop", null);

        error.Should().BeNull();
        message!.HasMobile.Should().BeFalse();
        message.WhatsAppLink.Should().BeNull();
        message.CallLink.Should().BeNull();
        message.Message.Should().Contain("Glycomet 500");
    }

    [Fact]
    public void Build_RejectsAnEmptySelectionAndSeveralPatients()
    {
        FollowupMessageBuilder.Build(Array.Empty<PatientFollowup>(), "Shop", null).Error.Should().Be("Please select a follow-up to continue.");

        var twoCustomers = new[] { Followup(1, 1, "A", DateTime.Today), Followup(2, 2, "B", DateTime.Today) };
        FollowupMessageBuilder.Build(twoCustomers, "Shop", null).Error.Should().Contain("one patient");

        var twoPatients = new[] { Followup(1, 1, "A", DateTime.Today), Followup(2, 1, "B", DateTime.Today, patient: "Sita Devi") };
        var (message, error) = FollowupMessageBuilder.Build(twoPatients, "Shop", null);
        message.Should().BeNull();
        error.Should().Contain("one patient");
    }

    // ───────────────────────── Keys ─────────────────────────

    [Fact]
    public void ItemKey_PrefersTheComposition()
    {
        FollowupKeys.ItemKey(12, 34).Should().Be("C12");
        FollowupKeys.ItemKey(null, 34).Should().Be("M34");
        FollowupKeys.ItemKey(0, 34).Should().Be("M34");
    }

    [Theory]
    [InlineData(null, "Ravi Kumar", "")]
    [InlineData("  ", "Ravi Kumar", "")]
    [InlineData(" Self ", "Ravi Kumar", "")]
    [InlineData("RAVI   kumar", "Ravi Kumar", "")]
    [InlineData(" Sita  Devi ", "Ravi Kumar", "sita devi")]
    [InlineData("Sita", null, "sita")]
    public void PatientKey_IsBlankForTheCustomerThemselves(string? patient, string? customer, string expected) =>
        FollowupKeys.PatientKey(patient, customer).Should().Be(expected);

    [Theory]
    [InlineData("Ramesh Kumar", "RK")]
    [InlineData("anita", "AN")]
    [InlineData("A", "A")]
    [InlineData("Ram Kumar Verma", "RV")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Initials(string? name, string expected) => FollowupKeys.Initials(name).Should().Be(expected);

    [Fact]
    public void FollowupDisplayMembers()
    {
        var self = Followup(1, 1, "Glycomet 500", new DateTime(2026, 12, 5)) with { CompositionName = "Metformin", Gender = 2 };
        self.PatientDisplay.Should().Be("Ravi Kumar");
        self.Initials.Should().Be("RK");
        self.ItemDisplay.Should().Be("Glycomet 500 (Metformin)");
        self.QuantityDisplay.Should().Be("30 units");
        self.ConsumptionDisplay.Should().Be("1 unit(s)/day");
        self.GenderLabel.Should().Be("Female");
        self.SaleReference.Should().Be("-");

        var other = Followup(2, 1, "Dolo", DateTime.Today, patient: "Sita Devi", phone: null) with { Source = FollowupSource.Manual, QuantityUnits = 1 };
        other.PatientDisplay.Should().Be("Sita Devi");
        other.PhoneDisplay.Should().Be("-");
        other.QuantityDisplay.Should().Be("1 unit");
        other.SaleReference.Should().Be("Manual");
    }

    // ───────────────────────── Settings and rules ─────────────────────────

    [Fact]
    public void Settings_DefaultsAreLocalwellConsumptionAndAWeekEitherSide()
    {
        var settings = FollowupSettings.Default;

        settings.DefaultUnitsPerDay.Should().Be(0.5m);
        settings.UpcomingDays.Should().Be(7);
        settings.GraceDays.Should().Be(7);
        settings.LookbackDays.Should().Be(120);
        settings.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Settings_RoundTripThroughStoredText()
    {
        var settings = new FollowupSettings { DefaultUnitsPerDay = 1.5m, UpcomingDays = 10, GraceDays = 3, LookbackDays = 200 };
        var values = settings.ToValues();

        values.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["DoctorsFollowups.DefaultUnitsPerDay"] = "1.5", ["DoctorsFollowups.UpcomingDays"] = "10",
            ["DoctorsFollowups.GraceDays"] = "3", ["DoctorsFollowups.LookbackDays"] = "200"
        });
        FollowupSettings.FromValues(key => values[key]).Should().Be(settings);
    }

    [Fact]
    public void Settings_MissingOrBadValues_FallBackFieldByField()
    {
        var stored = new Dictionary<string, string?>
        {
            [FollowupSettings.DefaultUnitsPerDayKey] = "abc", [FollowupSettings.UpcomingDaysKey] = "61",
            [FollowupSettings.GraceDaysKey] = "14", [FollowupSettings.LookbackDaysKey] = null
        };

        var settings = FollowupSettings.FromValues(key => stored.GetValueOrDefault(key));

        settings.Should().Be(new FollowupSettings { GraceDays = 14 });
    }

    [Fact]
    public void Settings_Validate_ListsEveryProblem()
    {
        var errors = new FollowupSettings { DefaultUnitsPerDay = 0, UpcomingDays = -1, GraceDays = 91, LookbackDays = 6 }.Validate();

        errors.Should().HaveCount(4);
        errors[0].Should().Be("Default consumption must be between 0.05 and 50 units a day.");
    }

    [Fact]
    public void RefillRule_IsForExactlyOneCompositionOrMedicine()
    {
        new RefillRule { CompositionId = 1, RuleIsChronic = true, UnitsPerDay = 1 }.Validate().Should().BeEmpty();
        new RefillRule { MedicineId = 1 }.Validate().Should().BeEmpty();
        new RefillRule().Validate().Should().ContainSingle();
        new RefillRule { CompositionId = 1, MedicineId = 2 }.Validate().Should().ContainSingle();
        new RefillRule { CompositionId = 1, UnitsPerDay = 100 }.Validate().Should().ContainSingle().Which.Should().Contain("Consumption");
    }

    [Fact]
    public void RefillRule_OverrideBeatsTheItemMasterDefault()
    {
        new RefillRule { CompositionId = 1, DefaultIsChronic = true }.IsChronic.Should().BeTrue();
        new RefillRule { CompositionId = 1, DefaultIsChronic = true, RuleIsChronic = false }.IsChronic.Should().BeFalse();
        new RefillRule { MedicineId = 1, RuleIsChronic = true, UnitsPerDay = 2 }.IsChronic.Should().BeTrue();
        new RefillRule { MedicineId = 1, UnitsPerDay = 2 }.ConsumptionLabel.Should().Be("2/day");
        new RefillRule { CompositionId = 1 }.ConsumptionLabel.Should().Be("default");
        new RefillRule { CompositionId = 1 }.Kind.Should().Be("Composition");
        new RefillRule { MedicineId = 1 }.Kind.Should().Be("Medicine");
    }
}
