using FluentAssertions;
using MedicalShopManager.Core.Modules.ControlCenter;
using Xunit;

namespace MedicalShopManager.UnitTests.ControlCenter;

/// <summary>GSTIN / PAN / IFSC / PIN / phone / UPI format rules and the GST state list.</summary>
public class ControlCenterIdentifierTests
{
    // Published sample GSTINs (CBIC / common documentation examples) with correct check characters.
    [Theory]
    [InlineData("27AAPFU0939F1ZV")]
    [InlineData("29AAGCB7383J1Z4")]
    [InlineData("27aapfu0939f1zv")] // case-insensitive input
    public void ValidateGstin_AcceptsValidNumbers(string gstin) =>
        IndianIdentifiers.ValidateGstin(gstin).Should().BeNull();

    [Theory]
    [InlineData("27AAPFU0939F1ZX", "check digit")]
    [InlineData("27AAPFU0939F1Z", "15 characters")]
    [InlineData("27AAPFU0939F1AV", "format")]   // 14th must be Z
    [InlineData("99AAPFU0939F1ZV", "state code")]
    [InlineData("", "15 characters")]
    public void ValidateGstin_RejectsInvalidNumbers(string gstin, string reason) =>
        IndianIdentifiers.ValidateGstin(gstin).Should().Contain(reason);

    [Fact]
    public void GstinCheckCharacter_MatchesKnownSample() =>
        IndianIdentifiers.GstinCheckCharacter("27AAPFU0939F1Z").Should().Be('V');

    [Fact]
    public void PanFromGstin_ReturnsCharacters3To12() =>
        IndianIdentifiers.PanFromGstin("27AAPFU0939F1ZV").Should().Be("AAPFU0939F");

    [Theory]
    [InlineData("AAPFU0939F", true)]
    [InlineData("AAPF0939F", false)]
    [InlineData("1APFU0939F", false)]
    public void Pan(string pan, bool valid) => IndianIdentifiers.IsValidPan(pan).Should().Be(valid);

    [Theory]
    [InlineData("SBIN0001234", true)]
    [InlineData("sbin0001234", true)]
    [InlineData("SBIN1001234", false)] // 5th char must be 0
    [InlineData("SBI0001234", false)]
    public void Ifsc(string ifsc, bool valid) => IndianIdentifiers.IsValidIfsc(ifsc).Should().Be(valid);

    [Theory]
    [InlineData("560001", true)]
    [InlineData("060001", false)]
    [InlineData("56001", false)]
    public void Pincode(string pin, bool valid) => IndianIdentifiers.IsValidPincode(pin).Should().Be(valid);

    [Theory]
    [InlineData("9876543210", true)]
    [InlineData("+91 98765 43210", true)]
    [InlineData("080-22334455", true)]   // landline with STD code
    [InlineData("5876543210", false)]    // mobiles start 6-9
    [InlineData("98765", false)]
    [InlineData("98765abc10", false)]
    public void Phone(string phone, bool valid) => IndianIdentifiers.IsValidPhone(phone).Should().Be(valid);

    [Theory]
    [InlineData("myshop@okaxis", true)]
    [InlineData("98765.43210@ybl", true)]
    [InlineData("myshop", false)]
    [InlineData("@okaxis", false)]
    public void Upi(string vpa, bool valid) => IndianIdentifiers.IsValidUpiId(vpa).Should().Be(valid);

    [Theory]
    [InlineData("12345678901234", true)]
    [InlineData("1234567890123", false)]
    public void Fssai(string number, bool valid) => IndianIdentifiers.IsValidFssai(number).Should().Be(valid);

    [Fact]
    public void States_HaveUniqueTwoDigitCodes_AndFindPadsSingleDigits()
    {
        IndianStates.All.Select(s => s.Code).Should().OnlyHaveUniqueItems().And.OnlyContain(c => c.Length == 2);
        IndianStates.Find("7")!.Name.Should().Be("Delhi");
        IndianStates.Find("29")!.Name.Should().Be("Karnataka");
        IndianStates.IsValidCode("25").Should().BeFalse("Daman & Diu merged into 26 in 2020");
    }
}
