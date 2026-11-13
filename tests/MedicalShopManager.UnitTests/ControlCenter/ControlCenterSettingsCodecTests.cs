using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.ControlCenter;
using Xunit;
using K = MedicalShopManager.Core.Modules.ControlCenter.ControlCenterSettingKeys;

namespace MedicalShopManager.UnitTests.ControlCenter;

/// <summary>Typed settings ⇄ AppSettings text: round trips, legacy fall-backs and forgiving reads.</summary>
public class ControlCenterSettingsCodecTests
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private static BusinessProfile FullProfile() => new()
    {
        ShopName = "Sri Sai Medicals",
        LegalName = "Sri Sai Pharma LLP",
        AddressLine1 = "12 MG Road",
        AddressLine2 = "Near Bus Stand",
        City = "Pune",
        Pincode = "411001",
        StateCode = "27",
        Phone = "9876543210",
        Email = "shop@example.com",
        GstRegistrationType = GstRegistrationType.Composition,
        Gstin = "27AAPFU0939F1ZV",
        Pan = "AAPFU0939F",
        DrugLicense20B = "MH-PZ1-20B-123",
        DrugLicense21B = "MH-PZ1-21B-456",
        DrugLicenseExpiry = new DateTime(2027, 3, 31),
        Fssai = "12345678901234",
        PharmacistName = "R. Kulkarni",
        PharmacistRegistrationNo = "MSPC-55555",
        BankName = "State Bank of India",
        BankAccountName = "Sri Sai Medicals",
        BankAccountNumber = "123456789012",
        BankIfsc = "SBIN0001234",
        BankBranch = "Camp",
        UpiId = "srisai@okaxis"
    };

    [Fact]
    public void BusinessProfile_RoundTrips_AndWritesComposedLegacyKeys()
    {
        var profile = FullProfile();
        var values = ControlCenterSettingsCodec.WriteBusinessProfile(profile);

        ControlCenterSettingsCodec.ReadBusinessProfile(values).Should().BeEquivalentTo(profile);
        values[K.ShopAddress].Should().Be("12 MG Road, Near Bus Stand, Pune - 411001, Maharashtra");
        values[K.LicenseNumber].Should().Be("20B: MH-PZ1-20B-123, 21B: MH-PZ1-21B-456");
        values[K.ShopGstin].Should().Be("27AAPFU0939F1ZV");
        values[K.GstRegistrationType].Should().Be("2");
        values[K.DrugLicenseExpiry].Should().Be("2027-03-31");
        values.Keys.Should().BeSubsetOf(K.BusinessKeys, "every written key is read back by the service");
    }

    [Fact]
    public void BusinessProfile_UnregisteredShop_PrintsNoGstinButKeepsWhatWasTyped()
    {
        var values = ControlCenterSettingsCodec.WriteBusinessProfile(FullProfile() with { GstRegistrationType = GstRegistrationType.Unregistered });
        values[K.ShopGstin].Should().BeEmpty();
        values[K.Gstin].Should().Be("27AAPFU0939F1ZV");
    }

    [Fact]
    public void BusinessProfile_FallsBackToPre62Keys()
    {
        var legacy = new Dictionary<string, string>
        {
            [K.ShopName] = "Old Shop",
            [K.ShopAddress] = "1 Old Street, Mysuru",
            [K.ShopGstin] = "29AAGCB7383J1Z4",
            [K.LicenseNumber] = "KA-DL-1",
            [K.ShopPhone] = "0821-2345678"
        };

        var p = ControlCenterSettingsCodec.ReadBusinessProfile(legacy);

        p.ShopName.Should().Be("Old Shop");
        p.AddressLine1.Should().Be("1 Old Street, Mysuru");
        p.Gstin.Should().Be("29AAGCB7383J1Z4");
        p.DrugLicense20B.Should().Be("KA-DL-1");
        p.Phone.Should().Be("0821-2345678");
        p.GstRegistrationType.Should().Be(GstRegistrationType.Regular);
    }

    [Fact]
    public void BusinessProfile_EmptyStore_GivesDefaults()
    {
        var p = ControlCenterSettingsCodec.ReadBusinessProfile(Empty);
        p.Should().BeEquivalentTo(new BusinessProfile());
    }

    [Fact]
    public void SaleSettings_RoundTrip()
    {
        var s = new SaleSettings
        {
            NewSaleIsCredit = true,
            DefaultDueDays = 30,
            DefaultDoctorId = 42,
            DefaultDoctorName = "Dr. Rao",
            ExpiryCutoffMonths = 3,
            NearExpiryMonths = 9,
            RoundOffMode = RoundOffMode.NearestFiftyPaise,
            AllowNegativeStock = true,
            AddSchemeWrtSaleQty = true,
            BarcodeQtyInPacks = true,
            DefaultDiscountPercent = 2.5m,
            MinDiscountPercent = 1m,
            MaxDiscountPercent = 20m,
            PatientReminders = false,
            RequireDoctorForScheduleH = true,
            RequirePrescriptionReference = true
        };

        var values = ControlCenterSettingsCodec.WriteSaleSettings(s);

        ControlCenterSettingsCodec.ReadSaleSettings(values).Should().BeEquivalentTo(s);
        values[K.RoundOffMode].Should().Be("2");
        values[K.DefaultDiscountPercent].Should().Be("2.5");
        values.Keys.Should().BeEquivalentTo(K.SaleKeys);
    }

    [Fact]
    public void SaleSettings_BadOrMissingValues_FallBackToSafeDefaults()
    {
        var garbage = new Dictionary<string, string>
        {
            [K.AllowNegativeStock] = "maybe",
            [K.DefaultDueDays] = "ten",
            [K.RoundOffMode] = "7",
            [K.DefaultDoctorId] = "0",
            [K.MaxDiscountPercent] = "",
        };

        var s = ControlCenterSettingsCodec.ReadSaleSettings(garbage);

        s.Should().BeEquivalentTo(new SaleSettings());
        s.AllowNegativeStock.Should().BeFalse("negative stock must never be switched on by a corrupt value");
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    public void Booleans_AcceptBothStorageStyles(string stored, bool expected)
    {
        var s = ControlCenterSettingsCodec.ReadSaleSettings(new Dictionary<string, string> { [K.NewSaleIsCredit] = stored });
        s.NewSaleIsCredit.Should().Be(expected);
    }

    [Fact]
    public void InvoicePrintSettings_RoundTrip_AndUnknownStoredKeysAreIgnored()
    {
        var s = new InvoicePrintSettings { TermsText = "No returns.", FooterNote = "Thanks", ContactNumber = "9123456789" }
            .With(InvoiceField.ShowUpiQr, true)
            .With(InvoiceField.ShowHsn, false);

        var values = new Dictionary<string, string>(ControlCenterSettingsCodec.WriteInvoicePrintSettings(s))
        {
            [K.InvoiceFieldKey("SomeFutureToggle")] = "1"
        };
        var read = ControlCenterSettingsCodec.ReadInvoicePrintSettings(values);

        read.ShowUpiQr.Should().BeTrue();
        read.ShowHsn.Should().BeFalse();
        read.TermsText.Should().Be("No returns.");
        read.ContactNumber.Should().Be("9123456789");
        read.Fields.Should().NotContainKey("SomeFutureToggle");
        InvoicePrintSettings.Validate(read).Should().BeEmpty();
    }

    [Fact]
    public void InvoicePrintSettings_MissingKeys_UseEachFieldsDefault()
    {
        var read = ControlCenterSettingsCodec.ReadInvoicePrintSettings(Empty);
        foreach (var field in InvoiceField.All)
            read.IsEnabled(field.Key).Should().Be(field.DefaultValue, field.Key);
        read.TermsText.Should().Be(InvoicePrintSettings.DefaultTerms);
    }

    [Fact]
    public void GeneralSettings_RoundTrip()
    {
        var s = new GeneralSettings { DefaultGstRatePercent = 5m, NearExpiryDays = 60, BackupRetentionDays = 7, AutoBackupEnabled = false };
        ControlCenterSettingsCodec.ReadGeneralSettings(ControlCenterSettingsCodec.WriteGeneralSettings(s)).Should().BeEquivalentTo(s);
        ControlCenterSettingsCodec.WriteGeneralSettings(s)[K.DefaultGstRate].Should().Be("5");
    }

    [Theory]
    [InlineData("A5", PaperSize.A5)]
    [InlineData("thermal80mm", PaperSize.Thermal80mm)]
    [InlineData("Thermal58mm", PaperSize.Thermal58mm)]
    [InlineData("Letter", null)]
    [InlineData("9", null)]
    [InlineData(null, null)]
    public void PaperSize_IsStoredByName(string? stored, PaperSize? expected) =>
        ControlCenterSettingsCodec.ReadPaperSize(stored).Should().Be(expected);

    [Fact]
    public void PaperSize_WritesTheEnumName_ThePrintServiceParses()
    {
        foreach (var size in Enum.GetValues<PaperSize>())
            ControlCenterSettingsCodec.ReadPaperSize(ControlCenterSettingsCodec.WritePaperSize(size)).Should().Be(size);
        K.PaperSizeKey("SaleInvoice").Should().Be("Print.PaperSize.SaleInvoice");
    }

    [Theory]
    [InlineData("SaleInvoice", true)]
    [InlineData("Credit_Note.v2", true)]
    [InlineData("", false)]
    [InlineData("1Invoice", false)]
    [InlineData("Sale Invoice", false)]
    [InlineData("Sale'; DROP TABLE", false)]
    public void TemplateNames_MustBePlainIdentifiers(string name, bool valid) =>
        ControlCenterSettingsCodec.IsValidTemplateName(name).Should().Be(valid);

    [Fact]
    public void SettingKeys_AreUniqueAcrossAreas()
    {
        var all = K.BusinessKeys.Concat(K.SaleKeys).Concat(K.InvoiceKeys).Concat(K.GeneralKeys).ToList();
        all.Should().OnlyHaveUniqueItems();
        K.SaleKeys.Should().OnlyContain(k => k.StartsWith("ControlCenter.Sale.", StringComparison.Ordinal));
        K.InvoiceKeys.Should().OnlyContain(k => k.StartsWith("ControlCenter.Invoice.", StringComparison.Ordinal));
    }
}
