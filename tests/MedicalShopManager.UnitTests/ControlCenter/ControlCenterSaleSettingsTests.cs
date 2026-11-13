using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.ControlCenter;
using Xunit;

namespace MedicalShopManager.UnitTests.ControlCenter;

public class ControlCenterSaleSettingsTests
{
    private static readonly DateTime Today = new(2026, 11, 4);

    [Fact]
    public void Defaults_AreValid_AndSafe()
    {
        var s = new SaleSettings();
        SaleSettingsRules.Validate(s).Should().BeEmpty();
        s.AllowNegativeStock.Should().BeFalse();
        s.RoundOffMode.Should().Be(RoundOffMode.NearestRupee);
        s.NearExpiryMonths.Should().Be(6, "spec 02: default near-expiry cutoff 6 months");
    }

    [Theory]
    [InlineData(nameof(SaleSettings.DefaultDueDays))]
    [InlineData(nameof(SaleSettings.ExpiryCutoffMonths))]
    [InlineData(nameof(SaleSettings.NearExpiryMonths))]
    [InlineData(nameof(SaleSettings.MinDiscountPercent))]
    [InlineData(nameof(SaleSettings.DefaultDiscountPercent))]
    public void Validate_FlagsOutOfRangeValues(string field)
    {
        var s = field switch
        {
            nameof(SaleSettings.DefaultDueDays) => new SaleSettings { DefaultDueDays = 400 },
            nameof(SaleSettings.ExpiryCutoffMonths) => new SaleSettings { ExpiryCutoffMonths = -1 },
            nameof(SaleSettings.NearExpiryMonths) => new SaleSettings { NearExpiryMonths = 4 },
            nameof(SaleSettings.MinDiscountPercent) => new SaleSettings { MinDiscountPercent = 20, MaxDiscountPercent = 10 },
            _ => new SaleSettings { DefaultDiscountPercent = 15, MaxDiscountPercent = 10 }
        };
        SaleSettingsRules.Validate(s).Should().Contain(e => e.Field == field);
    }

    [Fact]
    public void Expired_FromTheExpiryDateOnwards()
    {
        SaleSettingsRules.IsExpired(Today, Today).Should().BeTrue();
        SaleSettingsRules.IsExpired(Today.AddDays(1), Today).Should().BeFalse();
    }

    [Fact]
    public void ExpiryCutoff_BlocksBatchesExpiringWithinTheWindow()
    {
        var none = new SaleSettings();
        SaleSettingsRules.IsBatchSaleable(Today.AddDays(1), Today, none).Should().BeTrue();
        SaleSettingsRules.IsBatchSaleable(Today, Today, none).Should().BeFalse();

        var threeMonths = new SaleSettings { ExpiryCutoffMonths = 3 };
        SaleSettingsRules.IsBatchSaleable(new DateTime(2027, 2, 4), Today, threeMonths).Should().BeFalse();
        SaleSettingsRules.IsBatchSaleable(new DateTime(2027, 2, 5), Today, threeMonths).Should().BeTrue();
    }

    [Fact]
    public void NearExpiry_IsTheWindowBeforeExpiry()
    {
        var s = new SaleSettings { NearExpiryMonths = 6 };
        SaleSettingsRules.IsNearExpiry(new DateTime(2027, 5, 4), Today, s).Should().BeTrue();
        SaleSettingsRules.IsNearExpiry(new DateTime(2027, 5, 5), Today, s).Should().BeFalse();
        SaleSettingsRules.IsNearExpiry(Today, Today, s).Should().BeFalse("already expired, not near expiry");
    }

    [Fact]
    public void DueDate_UsesCustomerCreditDaysFirst()
    {
        var s = new SaleSettings { DefaultDueDays = 15 };
        SaleSettingsRules.DueDate(Today, s).Should().Be(Today.AddDays(15));
        SaleSettingsRules.DueDate(Today, s, customerCreditDays: 30).Should().Be(Today.AddDays(30));
        SaleSettingsRules.DueDate(Today.AddHours(15), new SaleSettings()).Should().Be(Today);
    }

    [Fact]
    public void BillDiscount_IsClampedToSettingsAndItemCap()
    {
        var s = new SaleSettings { MaxDiscountPercent = 12 };
        SaleSettingsRules.ApplyBillDiscount(5, 10, s, itemMaxDiscountPercent: null, ignoreLimits: false).Should().Be(12);
        SaleSettingsRules.ApplyBillDiscount(5, 10, s, itemMaxDiscountPercent: 8, ignoreLimits: false).Should().Be(8);
        SaleSettingsRules.ApplyBillDiscount(5, 10, s, itemMaxDiscountPercent: 8, ignoreLimits: true).Should().Be(15);
        SaleSettingsRules.DiscountBounds(new SaleSettings { MinDiscountPercent = 2, MaxDiscountPercent = 20 }, false).Should().Be((2m, 20m));
    }

    [Theory]
    [InlineData(DrugSchedule.None, false, false)]
    [InlineData(DrugSchedule.H, false, false)]
    [InlineData(DrugSchedule.H, true, true)]
    [InlineData(DrugSchedule.H1, false, true)]
    [InlineData(DrugSchedule.X, false, true)]
    [InlineData(DrugSchedule.NRx, false, true)]
    public void Doctor_IsStatutoryForH1XNrx_OptionalForH(DrugSchedule schedule, bool requireForH, bool expected) =>
        SaleSettingsRules.RequiresDoctor(schedule, new SaleSettings { RequireDoctorForScheduleH = requireForH }).Should().Be(expected);

    [Fact]
    public void PrescriptionReference_OnlyWhenEnabledAndRx()
    {
        var on = new SaleSettings { RequirePrescriptionReference = true };
        SaleSettingsRules.RequiresPrescriptionReference(DrugSchedule.H, on).Should().BeTrue();
        SaleSettingsRules.RequiresPrescriptionReference(DrugSchedule.None, on).Should().BeFalse();
        SaleSettingsRules.RequiresPrescriptionReference(DrugSchedule.H, new SaleSettings()).Should().BeFalse();
    }

    [Fact]
    public void NegativeStock_IsBlockedUnlessAllowed()
    {
        SaleSettingsRules.CanSellQuantity(10, 10, new SaleSettings()).Should().BeTrue();
        SaleSettingsRules.CanSellQuantity(11, 10, new SaleSettings()).Should().BeFalse();
        SaleSettingsRules.CanSellQuantity(11, 10, new SaleSettings { AllowNegativeStock = true }).Should().BeTrue();
        SaleSettingsRules.CanSellQuantity(0, 10, new SaleSettings { AllowNegativeStock = true }).Should().BeFalse();
    }

    [Fact]
    public void GeneralSettings_Validation()
    {
        GeneralSettings.Validate(new GeneralSettings()).Should().BeEmpty();
        GeneralSettings.Validate(new GeneralSettings { DefaultGstRatePercent = 10, NearExpiryDays = 0, BackupRetentionDays = 0 })
            .Select(e => e.Field).Should().BeEquivalentTo(new[]
            {
                nameof(GeneralSettings.DefaultGstRatePercent), nameof(GeneralSettings.NearExpiryDays), nameof(GeneralSettings.BackupRetentionDays)
            });
    }
}
