using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Core.Pricing;
using Xunit;

namespace MedicalShopManager.UnitTests.ControlCenter;

/// <summary>Invoice print toggles, the UPI payment QR link, branding image checks and the preview sample bill.</summary>
public class ControlCenterPrintSettingsTests
{
    [Fact]
    public void InvoiceFields_HaveUniqueKeys_AndDefaultsAreValid()
    {
        InvoiceField.All.Select(f => f.Key).Should().OnlyHaveUniqueItems();
        InvoiceField.All.Select(f => f.Group).Distinct().Should().HaveCount(Enum.GetValues<InvoiceFieldGroup>().Length);
        InvoicePrintSettings.Validate(new InvoicePrintSettings()).Should().BeEmpty();

        var d = new InvoicePrintSettings();
        d.ShowGstin.Should().BeTrue();
        d.ShowDrugLicense.Should().BeTrue("drug licence numbers are mandatory on a pharmacy bill");
        d.ShowBatch.Should().BeTrue();
        d.ShowExpiry.Should().BeTrue();
        d.ShowUpiQr.Should().BeFalse("needs a UPI id first");
    }

    [Fact]
    public void With_ReturnsACopy_AndRejectsUnknownKeys()
    {
        var original = new InvoicePrintSettings();
        var changed = original.With(InvoiceField.ShowMrp, false);

        changed.ShowMrp.Should().BeFalse();
        original.ShowMrp.Should().BeTrue("settings records are immutable");
        FluentActions.Invoking(() => original.With("ShowSomething", true)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_FlagsLongTextBadPhoneAndUnknownFields()
    {
        var s = new InvoicePrintSettings
        {
            TermsText = new string('x', InvoicePrintSettings.MaxTermsLength + 1),
            FooterNote = new string('y', InvoicePrintSettings.MaxFooterLength + 1),
            ContactNumber = "12345",
            Fields = new Dictionary<string, bool> { ["Bogus"] = true }
        };

        InvoicePrintSettings.Validate(s).Select(e => e.Field).Should()
            .BeEquivalentTo(new[] { "TermsText", "FooterNote", "ContactNumber", "Bogus" });
    }

    [Fact]
    public void IsEnabled_FallsBackToTheDefaultForMissingKeys()
    {
        var s = new InvoicePrintSettings { Fields = new Dictionary<string, bool>() };
        s.ShowHsn.Should().Be(InvoiceField.Find(InvoiceField.ShowHsn)!.DefaultValue);
        s.IsEnabled("NotAField").Should().BeFalse();
    }

    // ─── UPI QR ───

    [Fact]
    public void UpiLink_FollowsTheNpciFormat()
    {
        UpiPaymentLink.Build("srisai@okaxis", "Sri Sai Medicals", 25120, "INV-2026-00042")
            .Should().Be("upi://pay?pa=srisai%40okaxis&pn=Sri%20Sai%20Medicals&am=251.20&cu=INR&tn=INV-2026-00042");
    }

    [Fact]
    public void UpiLink_WithoutAmount_LetsTheCustomerEnterIt()
    {
        UpiPaymentLink.Build("shop@ybl", "").Should().Be("upi://pay?pa=shop%40ybl&pn=Pharmacy&cu=INR");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-vpa")]
    public void UpiLink_IsNullWithoutAValidUpiId(string? upi) => UpiPaymentLink.Build(upi, "Shop", 100).Should().BeNull();

    [Fact]
    public void UpiLink_RejectsNegativeAmounts() =>
        FluentActions.Invoking(() => UpiPaymentLink.Build("shop@ybl", "Shop", -1)).Should().Throw<ArgumentOutOfRangeException>();

    // ─── Branding images ───

    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 };
    private static readonly byte[] JpegHeader = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 };

    [Fact]
    public void BrandingImages_AcceptPngAndJpegOnly()
    {
        BrandingImageRules.Validate(PngHeader).Should().BeNull();
        BrandingImageRules.DetectContentType(PngHeader).Should().Be(BrandingImageRules.Png);
        BrandingImageRules.Validate(JpegHeader).Should().BeNull();
        BrandingImageRules.DetectContentType(JpegHeader).Should().Be(BrandingImageRules.Jpeg);

        BrandingImageRules.Validate("GIF89a"u8.ToArray()).Should().Contain("PNG or JPEG");
        BrandingImageRules.Validate(Array.Empty<byte>()).Should().Contain("empty");
        BrandingImageRules.Validate(null).Should().Contain("empty");
    }

    [Fact]
    public void BrandingImages_AreLimitedTo512Kb()
    {
        var big = new byte[BrandingImageRules.MaxBytes + 1];
        PngHeader.CopyTo(big, 0);
        BrandingImageRules.Validate(big).Should().Contain("512 KB");
        BrandingImageRules.AssetKey(BrandingImageKind.Logo).Should().Be("Logo");
        BrandingImageRules.AssetKey(BrandingImageKind.Signature).Should().Be("Signature");
    }

    // ─── Preview sample bill ───

    [Fact]
    public void SampleInvoice_UsesTheLocalwellGoldenVector_AndAddsUp()
    {
        // Spec 02 / contract §7: 1 pack + 5 loose of 15's @ ₹188.40 = ₹251.20; at 10% off = ₹226.08.
        var sale = SampleInvoiceFactory.Create(new DateTime(2026, 11, 4), "INV-2026-00042");

        sale.Items.Should().HaveCount(3);
        sale.Items[0].Quantity.Should().Be(20);
        sale.Items[0].LineTotalPaisa.Should().Be(22608);
        sale.InvoiceNumber.Should().Be("INV-2026-00042");

        var linesTotal = sale.Items.Sum(i => i.LineTotalPaisa);
        sale.GrandTotalPaisa.Should().Be(PricingCalculator.RoundOff(linesTotal, RoundOffMode.NearestRupee).RoundedPaisa);
        (sale.TaxableAmountPaisa + sale.CgstAmountPaisa + sale.SgstAmountPaisa + sale.IgstAmountPaisa).Should().Be(linesTotal);
        sale.Items.Should().OnlyContain(i => i.TaxableValuePaisa + i.TaxAmountPaisa == i.LineTotalPaisa);
        sale.IgstAmountPaisa.Should().Be(0, "a walk-in sale is intra-state");
    }

    [Fact]
    public void SampleInvoice_FollowsTheRoundOffSetting()
    {
        var exact = SampleInvoiceFactory.Create(new DateTime(2026, 11, 4), "X", RoundOffMode.None);
        exact.GrandTotalPaisa.Should().Be(exact.Items.Sum(i => i.LineTotalPaisa));
    }
}
