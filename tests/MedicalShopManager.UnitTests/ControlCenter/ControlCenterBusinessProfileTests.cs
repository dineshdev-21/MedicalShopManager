using FluentAssertions;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Core.Pricing;
using Xunit;

namespace MedicalShopManager.UnitTests.ControlCenter;

public class ControlCenterBusinessProfileTests
{
    private static BusinessProfile Valid() => new()
    {
        ShopName = "Sharma Medicals",
        AddressLine1 = "12 MG Road",
        City = "Pune",
        Pincode = "411001",
        StateCode = "27",
        Phone = "9876543210",
        GstRegistrationType = GstRegistrationType.Regular,
        Gstin = "27AAPFU0939F1ZV",
        DrugLicense20B = "MH-PZ1-123",
        DrugLicense21B = "MH-PZ1-124",
        PharmacistName = "R. Sharma"
    };

    private static IReadOnlyList<SettingsError> Check(BusinessProfile p) =>
        BusinessProfileValidator.Validate(BusinessProfileValidator.Normalize(p));

    [Fact]
    public void ValidProfile_HasNoErrors() => Check(Valid()).Should().BeEmpty();

    [Fact]
    public void Normalize_TrimsUpperCasesAndDerivesPan()
    {
        var p = BusinessProfileValidator.Normalize(Valid() with { Gstin = " 27aapfu0939f1zv ", ShopName = "  Sharma  ", BankIfsc = "sbin0001234" });
        p.Gstin.Should().Be("27AAPFU0939F1ZV");
        p.Pan.Should().Be("AAPFU0939F");
        p.ShopName.Should().Be("Sharma");
        p.BankIfsc.Should().Be("SBIN0001234");
    }

    [Fact]
    public void ShopNameAndState_AreRequired()
    {
        var errors = Check(Valid() with { ShopName = " ", StateCode = "" });
        errors.Select(e => e.Field).Should().Contain(new[] { nameof(BusinessProfile.ShopName), nameof(BusinessProfile.StateCode) });
    }

    [Theory]
    [InlineData(GstRegistrationType.Regular)]
    [InlineData(GstRegistrationType.Composition)]
    public void RegisteredDealers_NeedAGstin(GstRegistrationType type) =>
        Check(Valid() with { GstRegistrationType = type, Gstin = "" }).Should().Contain(e => e.Field == nameof(BusinessProfile.Gstin));

    [Fact]
    public void Unregistered_NeedsNoGstin_AndPrintsNone()
    {
        var p = Valid() with { GstRegistrationType = GstRegistrationType.Unregistered, Gstin = "" };
        Check(p).Should().BeEmpty();
        (Valid() with { GstRegistrationType = GstRegistrationType.Unregistered }).EffectiveGstin.Should().BeEmpty();
    }

    [Fact]
    public void GstinStateMustMatchShopState() =>
        Check(Valid() with { StateCode = "29" }).Should().ContainSingle(e => e.Field == nameof(BusinessProfile.Gstin))
            .Which.Message.Should().Contain("27").And.Contain("29");

    [Fact]
    public void PanMustMatchTheGstin() =>
        Check(Valid() with { Pan = "AAAAA1111A" }).Should().Contain(e => e.Field == nameof(BusinessProfile.Pan));

    [Fact]
    public void BankDetails_AreCheckedTogether_WhenAnyIsEntered()
    {
        var errors = Check(Valid() with { BankName = "SBI" });
        errors.Select(e => e.Field).Should().Contain(new[] { nameof(BusinessProfile.BankAccountNumber), nameof(BusinessProfile.BankIfsc) });
        Check(Valid() with { BankName = "SBI", BankAccountNumber = "12345678901", BankIfsc = "SBIN0001234" }).Should().BeEmpty();
    }

    [Fact]
    public void OptionalFields_AreValidatedOnlyWhenFilled()
    {
        var errors = Check(Valid() with { Email = "x@", Fssai = "123", UpiId = "shop", Pincode = "01234", Phone = "123" });
        errors.Select(e => e.Field).Should().Contain(new[]
        {
            nameof(BusinessProfile.Email), nameof(BusinessProfile.Fssai), nameof(BusinessProfile.UpiId),
            nameof(BusinessProfile.Pincode), nameof(BusinessProfile.Phone)
        });
    }

    [Fact]
    public void ComposedLegacyValues_ForThePrintHeader()
    {
        var p = Valid() with { AddressLine2 = "Near Station" };
        p.FullAddress.Should().Be("12 MG Road, Near Station, Pune - 411001, Maharashtra");
        p.DrugLicenseDisplay.Should().Be("20B: MH-PZ1-123, 21B: MH-PZ1-124");
        (p with { DrugLicense21B = "" }).DrugLicenseDisplay.Should().Be("20B: MH-PZ1-123");
    }

    [Theory]
    [InlineData(GstRegistrationType.Regular, "Tax Invoice")]
    [InlineData(GstRegistrationType.Composition, "Bill of Supply")]
    [InlineData(GstRegistrationType.Unregistered, "Bill of Supply")]
    public void DocumentTitle_FollowsRegistration(GstRegistrationType type, string title) =>
        (Valid() with { GstRegistrationType = type }).SaleDocumentTitle.Should().Be(title);

    [Fact]
    public void StateCode_DrivesInterStateThroughPricingCalculator()
    {
        var shop = Valid();
        PricingCalculator.IsInterStateSupply(shop.StateCode, "29AAGCB7383J1Z4").Should().BeTrue("Karnataka party, Maharashtra shop");
        PricingCalculator.IsInterStateSupply(shop.StateCode, "27AAPFU0939F1ZV").Should().BeFalse();
    }

    [Fact]
    public void DrugLicenseExpiry_WarnsWithin30Days()
    {
        var today = new DateTime(2026, 11, 4);
        BusinessProfileValidator.IsDrugLicenseExpiringSoon(Valid() with { DrugLicenseExpiry = today.AddDays(30) }, today).Should().BeTrue();
        BusinessProfileValidator.IsDrugLicenseExpiringSoon(Valid() with { DrugLicenseExpiry = today.AddDays(31) }, today).Should().BeFalse();
        BusinessProfileValidator.IsDrugLicenseExpiringSoon(Valid(), today).Should().BeFalse();
    }
}
