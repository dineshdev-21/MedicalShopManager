using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Database.Migrations;
using MedicalShopManager.Infrastructure.Modules.ControlCenter;
using MedicalShopManager.Infrastructure.Repositories;
using Xunit;
using K = MedicalShopManager.Core.Modules.ControlCenter.ControlCenterSettingKeys;

namespace MedicalShopManager.IntegrationTests.ControlCenter;

/// <summary>Migration 19, the settings store (one transaction per area) and the typed settings service.</summary>
public class ControlCenterSettingsIntegrationTests : ControlCenterDbTestBase
{
    private static BusinessProfile ValidProfile() => new()
    {
        ShopName = "  Sri Sai Medicals ",
        AddressLine1 = "12 MG Road",
        City = "Pune",
        Pincode = "411001",
        StateCode = "27",
        Phone = "9876543210",
        GstRegistrationType = GstRegistrationType.Regular,
        Gstin = "27aapfu0939f1zv",
        DrugLicense20B = "MH-20B-1",
        DrugLicense21B = "MH-21B-2",
        PharmacistName = "R. Kulkarni",
        UpiId = "srisai@okaxis"
    };

    // ─── migration ───

    [Fact]
    public async Task Migration19_CreatesTablesAndSeedsEveryKeyWithItsDefault()
    {
        (await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ControlCenterUserPermissions','ControlCenterAssets')"))
            .Should().Be(2L);
        (await Db.GetAppliedVersionsAsync()).Should().Contain(19);

        foreach (var key in K.SaleKeys.Concat(K.InvoiceKeys))
            (await SettingAsync(key)).Should().NotBeNull(key);

        (await Settings.GetSaleSettingsAsync()).Should().BeEquivalentTo(new SaleSettings(), "seeded values equal the record defaults");
        (await Settings.GetInvoicePrintSettingsAsync()).Should().BeEquivalentTo(new InvoicePrintSettings());
        (await SettingAsync(K.GstRegistrationType)).Should().Be("3", "a fresh install has no GSTIN yet");
    }

    [Fact]
    public async Task Migration19_CarriesThePre62ProfileIntoStructuredKeys()
    {
        // Upgrade path: a shop that ran the app before Control Center existed.
        var path = Path.Combine(Path.GetTempPath(), $"MSM_CC_Upgrade_{Guid.NewGuid():N}.db");
        try
        {
            var before = MigrationCatalog.Discover().Where(m => m.Version != 19).ToList();
            using (var old = new DatabaseManager(path, NullLogger<DatabaseManager>.Instance, before))
            {
                await old.InitializeAsync();
                using var conn = old.CreateNewConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE AppSettings SET Value = ' 29aagcb7383j1z4 ' WHERE Key = 'ShopGstin';
                    UPDATE AppSettings SET Value = '1 Temple Road, Mysuru' WHERE Key = 'ShopAddress';
                    UPDATE AppSettings SET Value = 'KA-DL-777' WHERE Key = 'LicenseNumber';";
                await cmd.ExecuteNonQueryAsync();
            }

            using var upgraded = new DatabaseManager(path, NullLogger<DatabaseManager>.Instance);
            await upgraded.InitializeAsync();
            var service = new ControlCenterSettingsService(new ControlCenterSettingsStore(upgraded),
                Audit, NullLogger<ControlCenterSettingsService>.Instance);
            var profile = await service.GetBusinessProfileAsync();

            profile.Gstin.Should().Be("29AAGCB7383J1Z4");
            profile.StateCode.Should().Be("29");
            profile.GstRegistrationType.Should().Be(GstRegistrationType.Regular);
            profile.AddressLine1.Should().Be("1 Temple Road, Mysuru");
            profile.DrugLicense20B.Should().Be("KA-DL-777");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
        }
    }

    // ─── store ───

    [Fact]
    public async Task Store_SetMany_IsAllOrNothing()
    {
        await Store.SetManyAsync(new Dictionary<string, string> { ["ControlCenter.Test.A"] = "1" });

        var batch = new Dictionary<string, string> { ["ControlCenter.Test.A"] = "2", ["ControlCenter.Test.B"] = "x", [" "] = "bad" };
        await FluentActions.Awaiting(() => Store.SetManyAsync(batch)).Should().ThrowAsync<ArgumentException>();

        (await SettingAsync("ControlCenter.Test.A")).Should().Be("1", "the failed batch rolled back");
        (await SettingAsync("ControlCenter.Test.B")).Should().BeNull();
    }

    [Fact]
    public async Task Store_GetAsync_ReturnsOnlyExistingKeys()
    {
        var values = await Store.GetAsync(new[] { K.ShopName, "No.Such.Key", K.ShopName });
        values.Should().ContainKey(K.ShopName).And.NotContainKey("No.Such.Key");
        (await Store.GetAsync(Array.Empty<string>())).Should().BeEmpty();
    }

    // ─── business profile ───

    [Fact]
    public async Task BusinessProfile_SaveNormalisesWritesLegacyKeysAudits_AndRaisesChanged()
    {
        var raised = new List<ControlCenterSettingsArea>();
        Settings.SettingsChanged += (_, area) => raised.Add(area);

        await Settings.SaveBusinessProfileAsync(ValidProfile());

        var saved = await Settings.GetBusinessProfileAsync();
        saved.ShopName.Should().Be("Sri Sai Medicals");
        saved.Gstin.Should().Be("27AAPFU0939F1ZV");
        saved.Pan.Should().Be("AAPFU0939F", "derived from the GSTIN");
        saved.State!.Name.Should().Be("Maharashtra");

        // The shared print header (FlowDocumentPrintService) still reads the pre-6.2 keys.
        (await SettingAsync(K.ShopName)).Should().Be("Sri Sai Medicals");
        (await SettingAsync(K.ShopGstin)).Should().Be("27AAPFU0939F1ZV");
        (await SettingAsync(K.ShopAddress)).Should().Be("12 MG Road, Pune - 411001, Maharashtra");
        (await SettingAsync(K.LicenseNumber)).Should().Be("20B: MH-20B-1, 21B: MH-21B-2");
        (await SettingAsync(K.PharmacistName)).Should().Be("R. Kulkarni");

        var audit = (await AuditAsync("ControlCenter.BusinessProfileUpdated")).Should().ContainSingle().Subject;
        audit.NewValues.Should().Contain("27AAPFU0939F1ZV");
        audit.OldValues.Should().Contain("My Medical Shop");
        raised.Should().Equal(ControlCenterSettingsArea.BusinessProfile);
    }

    [Fact]
    public async Task BusinessProfile_InvalidInput_SavesNothing()
    {
        var bad = ValidProfile() with { Gstin = "29AAPFU0939F1ZV", Email = "nope" };

        var ex = await FluentActions.Awaiting(() => Settings.SaveBusinessProfileAsync(bad))
            .Should().ThrowAsync<ControlCenterValidationException>();
        ex.Which.Errors.Select(e => e.Field).Should().Contain(new[] { nameof(BusinessProfile.Gstin), nameof(BusinessProfile.Email) });

        (await SettingAsync(K.ShopName)).Should().Be("My Medical Shop");
        (await AuditAsync("ControlCenter.BusinessProfileUpdated")).Should().BeEmpty();
    }

    [Fact]
    public async Task BusinessProfile_SavingTheSameValuesTwice_AuditsOnlyOnce()
    {
        await Settings.SaveBusinessProfileAsync(ValidProfile());
        await Settings.SaveBusinessProfileAsync(ValidProfile());
        (await AuditAsync("ControlCenter.BusinessProfileUpdated")).Should().HaveCount(1);
    }

    [Fact]
    public async Task Unregistered_ClearsThePrintedGstin()
    {
        await Settings.SaveBusinessProfileAsync(ValidProfile());
        await Settings.SaveBusinessProfileAsync(ValidProfile() with { GstRegistrationType = GstRegistrationType.Unregistered });

        (await SettingAsync(K.ShopGstin)).Should().BeEmpty();
        (await Settings.GetBusinessProfileAsync()).SaleDocumentTitle.Should().Be("Bill of Supply");
    }

    // ─── sale, invoice, general, paper ───

    [Fact]
    public async Task SaleSettings_RoundTrip_AndValidation()
    {
        var s = new SaleSettings
        {
            NewSaleIsCredit = true, DefaultDueDays = 15, DefaultDoctorName = " Dr. Rao ", ExpiryCutoffMonths = 2,
            NearExpiryMonths = 3, RoundOffMode = RoundOffMode.None, MaxDiscountPercent = 15m, DefaultDiscountPercent = 5m
        };
        await Settings.SaveSaleSettingsAsync(s);
        (await Settings.GetSaleSettingsAsync()).Should().BeEquivalentTo(s with { DefaultDoctorName = "Dr. Rao" });
        (await AuditAsync("ControlCenter.SaleSettingsUpdated")).Should().ContainSingle();

        await FluentActions.Awaiting(() => Settings.SaveSaleSettingsAsync(s with { MinDiscountPercent = 20m }))
            .Should().ThrowAsync<ControlCenterValidationException>();
        (await Settings.GetSaleSettingsAsync()).MinDiscountPercent.Should().Be(0m);
    }

    [Fact]
    public async Task InvoicePrintSettings_RoundTrip()
    {
        var s = new InvoicePrintSettings { TermsText = "  No returns after 7 days. ", ContactNumber = "9123456789" }
            .With(InvoiceField.ShowUpiQr, true)
            .With(InvoiceField.ShowSchedule, false);
        await Settings.SaveInvoicePrintSettingsAsync(s);

        var read = await Settings.GetInvoicePrintSettingsAsync();
        read.ShowUpiQr.Should().BeTrue();
        read.IsEnabled(InvoiceField.ShowSchedule).Should().BeFalse();
        read.TermsText.Should().Be("No returns after 7 days.");
        (await SettingAsync("ControlCenter.Invoice.ShowUpiQr")).Should().Be("1");
    }

    [Fact]
    public async Task GeneralSettings_RoundTrip_UsingThePre62Keys()
    {
        await Settings.SaveGeneralSettingsAsync(new GeneralSettings { DefaultGstRatePercent = 5m, NearExpiryDays = 60, BackupRetentionDays = 14, AutoBackupEnabled = false });
        (await SettingAsync("DefaultGstRate")).Should().Be("5");
        (await SettingAsync("AutoBackupEnabled")).Should().Be("0");
        (await Settings.GetGeneralSettingsAsync()).NearExpiryDays.Should().Be(60);

        await FluentActions.Awaiting(() => Settings.SaveGeneralSettingsAsync(new GeneralSettings { DefaultGstRatePercent = 7m }))
            .Should().ThrowAsync<ControlCenterValidationException>();
    }

    [Fact]
    public async Task PaperSizes_AreStoredUnderTheKeyThePrintServiceReads()
    {
        (await Settings.GetPaperSizeAsync("SaleInvoice")).Should().BeNull();
        await Settings.SavePaperSizesAsync(new Dictionary<string, PaperSize> { ["SaleInvoice"] = PaperSize.Thermal80mm, ["CreditNote"] = PaperSize.A4 });

        (await SettingAsync("Print.PaperSize.SaleInvoice")).Should().Be("Thermal80mm");
        (await Settings.GetPaperSizeAsync("CreditNote")).Should().Be(PaperSize.A4);
        (await new AppSettingsRepository(Db).GetValueAsync("Print.PaperSize.SaleInvoice")).Should().Be("Thermal80mm");

        await FluentActions.Awaiting(() => Settings.SavePaperSizesAsync(new Dictionary<string, PaperSize> { ["Bad Name"] = PaperSize.A4 }))
            .Should().ThrowAsync<ControlCenterValidationException>();
    }

    // ─── branding images ───

    [Fact]
    public async Task BrandingImages_SaveReplaceAndRemove()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 9 };

        (await Settings.GetBrandingImageAsync(BrandingImageKind.Logo)).Should().BeNull();
        await Settings.SaveBrandingImageAsync(BrandingImageKind.Logo, png);
        await Settings.SaveBrandingImageAsync(BrandingImageKind.Logo, jpeg);
        await Settings.SaveBrandingImageAsync(BrandingImageKind.Signature, png);

        var logo = (await Settings.GetBrandingImageAsync(BrandingImageKind.Logo))!;
        logo.ContentType.Should().Be(BrandingImageRules.Jpeg);
        logo.Data.Should().Equal(jpeg);
        (await Settings.GetBrandingImageAsync(BrandingImageKind.Signature))!.Data.Should().Equal(png);

        await Settings.RemoveBrandingImageAsync(BrandingImageKind.Logo);
        (await Settings.GetBrandingImageAsync(BrandingImageKind.Logo)).Should().BeNull();
        (await Settings.GetBrandingImageAsync(BrandingImageKind.Signature)).Should().NotBeNull();
        (await AuditAsync("ControlCenter.BrandingImageUpdated")).Should().HaveCount(3);
        (await AuditAsync("ControlCenter.BrandingImageRemoved")).Should().ContainSingle();
    }

    [Fact]
    public async Task BrandingImages_RejectNonImages()
    {
        await FluentActions.Awaiting(() => Settings.SaveBrandingImageAsync(BrandingImageKind.Logo, "GIF89a"u8.ToArray()))
            .Should().ThrowAsync<ControlCenterValidationException>();
        (await ScalarAsync("SELECT COUNT(*) FROM ControlCenterAssets")).Should().Be(0L);
    }
}
