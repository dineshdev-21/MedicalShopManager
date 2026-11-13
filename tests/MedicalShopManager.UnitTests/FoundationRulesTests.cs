using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Numbering;
using MedicalShopManager.Infrastructure.Database.Migrations;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Services;
using Xunit;

namespace MedicalShopManager.UnitTests;

/// <summary>Numbering, drug schedule, item-master validation and migration-number contract rules.</summary>
public class FoundationRulesTests
{
    // ─── Financial year & number format ───

    [Theory]
    [InlineData("2026-03-31", 2025, "2025-26", "2526")]
    [InlineData("2026-04-01", 2026, "2026-27", "2627")]
    [InlineData("2026-12-31", 2026, "2026-27", "2627")]
    [InlineData("2099-04-01", 2099, "2099-00", "9900")]
    public void FinancialYear_RunsAprilToMarch(string date, int start, string label, string shortLabel)
    {
        var fy = FinancialYear.For(DateTime.Parse(date));
        Assert.Equal(start, fy.StartYear);
        Assert.Equal(label, fy.Label);
        Assert.Equal(shortLabel, fy.ShortLabel);
        Assert.Equal(new DateTime(start, 4, 1), fy.Start);
        Assert.Equal(new DateTime(start + 1, 3, 31), fy.End);
    }

    [Theory]
    [InlineData("{PREFIX}/{FY2}/{SEQ}", "SR", 7, 5, "2026-05-01", "SR/2627/00007")]
    [InlineData("{PREFIX}-{YYYY}-{SEQ}", "INV", 42, 5, "2026-11-04", "INV-2026-00042")]
    [InlineData("{PREFIX}/{FY}/{SEQ}", "CN", 1, 3, "2027-01-15", "CN/2026-27/001")]
    [InlineData("{PREFIX}{SEQ}", "PO", 123456, 5, "2026-05-01", "PO123456")]
    public void Formatter_ReplacesTokens(string template, string prefix, long seq, int padding, string date, string expected) =>
        Assert.Equal(expected, DocumentNumberFormatter.Format(template, prefix, seq, padding, DateTime.Parse(date)));

    [Theory]
    [InlineData("{PREFIX}/{FY2}/{SEQ}", "SR", 5, true, true)]
    [InlineData("{PREFIX}-{YYYY}-{SEQ}", "INV", 5, false, true)]
    [InlineData("{PREFIX}/{FY2}", "SR", 5, true, false)]          // no {SEQ}
    [InlineData("{PREFIX}/{SEQ}", "SR", 5, true, false)]          // resets per FY but number has no FY → duplicates
    [InlineData("{PREFIX}/{FY}/{SEQ}", "INVOICE", 5, true, false)] // longer than 16 characters
    [InlineData("{PREFIX}/{SEQ}", "SR", 0, false, false)]         // bad padding
    public void Formatter_Validate(string template, string prefix, int padding, bool reset, bool valid) =>
        Assert.Equal(valid, DocumentNumberFormatter.Validate(template, prefix, padding, reset) == null);

    [Theory]
    [InlineData(true, "2026-03-31", 2025)]
    [InlineData(true, "2026-04-01", 2026)]
    [InlineData(false, "2026-04-01", 0)]
    public void CounterPeriod_IsFinancialYearOnlyWhenResetting(bool reset, string date, int period) =>
        Assert.Equal(period, DocumentSequenceAllocator.PeriodFor(reset, DateTime.Parse(date)));

    [Fact]
    public void Preview_FormatsTheLoadedNextNumber()
    {
        var sequence = new DocumentSequence { Prefix = "SR", Template = "{PREFIX}/{FY2}/{SEQ}", Padding = 3, NextNumber = 57 };
        Assert.Equal("SR/2526/057", DocumentSequenceAllocator.Preview(sequence, new DateTime(2026, 3, 31)));
    }

    // ─── Drug schedule ───

    [Theory]
    [InlineData(DrugSchedule.None, false, false, "-")]
    [InlineData(DrugSchedule.H, true, false, "Sch 'H'")]
    [InlineData(DrugSchedule.H1, true, true, "Sch 'H1'")]
    [InlineData(DrugSchedule.X, true, true, "Sch 'X'")]
    [InlineData(DrugSchedule.NRx, true, true, "NRx")]
    [InlineData(DrugSchedule.Rx, true, false, "Rx")]
    public void DrugSchedule_Rules(DrugSchedule schedule, bool rx, bool register, string label)
    {
        Assert.Equal(rx, schedule.RequiresPrescription());
        Assert.Equal(register, schedule.RequiresRegisterEntry());
        Assert.Equal(label, schedule.ToLabel());
    }

    [Fact]
    public void DrugSchedule_KeepsLegacyIntegers()
    {
        // Values persisted by the old PrescriptionClassification enum must keep their meaning.
        Assert.Equal(0, (int)DrugSchedule.None);
        Assert.Equal(1, (int)DrugSchedule.H);
        Assert.Equal(2, (int)DrugSchedule.H1);
        Assert.Equal(3, (int)DrugSchedule.X);
        Assert.Equal(4, (int)DrugSchedule.NRx);
        Assert.Equal(5, (int)DrugSchedule.Other);
    }

    // ─── Item master ───

    private static Medicine ValidMedicine() => new()
    {
        Name = "Dolo 650", UnitsPerPack = 15, GstRatePercent = 12, MinStock = 30, MaxStock = 300,
        DefaultMrpPaisa = 3400, DefaultSellingPricePaisa = 3200, HsnCode = "30049099", MaxDiscountPercent = 20
    };

    [Fact]
    public void MedicineValidation_AcceptsValidItem() => Assert.Null(MedicineService.Validate(ValidMedicine()));

    [Theory]
    [InlineData("name")]
    [InlineData("pack")]
    [InlineData("gst")]
    [InlineData("maxstock")]
    [InlineData("discount")]
    [InlineData("rate")]
    [InlineData("hsn")]
    public void MedicineValidation_RejectsBadFields(string field)
    {
        var m = ValidMedicine();
        switch (field)
        {
            case "name": m.Name = " "; break;
            case "pack": m.UnitsPerPack = 0; break;
            case "gst": m.GstRatePercent = 30; break;
            case "maxstock": m.MaxStock = 10; break;
            case "discount": m.MaxDiscountPercent = 120; break;
            case "rate": m.DefaultSellingPricePaisa = 3500; break;
            case "hsn": m.HsnCode = "30A4"; break;
        }
        Assert.NotNull(MedicineService.Validate(m));
    }

    [Fact]
    public void Batch_ExpiryFromMonthYear_IsLastDayOfMonth()
    {
        Assert.Equal(new DateTime(2026, 11, 30), MedicineBatch.ExpiryFromMonthYear(11, 26));
        Assert.Equal(new DateTime(2028, 2, 29), MedicineBatch.ExpiryFromMonthYear(2, 2028));
        var batch = new MedicineBatch { QuantityRemaining = 50, UnitsPerPack = 15, ExpiryDate = new DateTime(2026, 11, 30) };
        Assert.Equal("3 + 5", batch.QuantityRemainingDisplay);
        Assert.Equal("11/26", batch.ExpiryLabel);
    }

    // ─── Migration number contract ───

    [Fact]
    public void Migrations_AreUniqueAndAscending()
    {
        var versions = MigrationCatalog.Discover().Select(m => m.Version).ToList();
        Assert.Equal(versions.OrderBy(v => v).Distinct(), versions);
        Assert.Contains(1, versions);
        Assert.Contains(2, versions);
        Assert.Contains(3, versions);
    }

    [Fact]
    public void Migrations_UseOnlyTheirReservedNumbers()
    {
        foreach (var migration in MigrationCatalog.Discover())
        {
            var ns = migration.GetType().Namespace ?? string.Empty;
            const string moduleMarker = ".Infrastructure.Modules.";
            var at = ns.IndexOf(moduleMarker, StringComparison.Ordinal);
            if (at >= 0)
            {
                var module = ns[(at + moduleMarker.Length)..].Split('.')[0];
                Assert.True(MigrationCatalog.IsVersionAllowedFor(module, migration.Version),
                    $"{migration.GetType().Name} (v{migration.Version}) is outside module {module}'s reserved numbers.");
            }
            else
            {
                Assert.True(migration.Version <= MigrationCatalog.MaxFoundationVersion,
                    $"{migration.GetType().FullName} (v{migration.Version}) must live under Infrastructure/Modules/<Name> to use a module number.");
            }
        }
    }

    [Theory]
    [InlineData("Billing", 10, true)]
    [InlineData("Billing", 105, true)]
    [InlineData("Billing", 110, false)]
    [InlineData("SalesReturns", 110, true)]
    [InlineData("DashboardSearch", 209, true)]
    [InlineData("Stock", 12, false)]
    [InlineData("Unknown", 10, false)]
    public void MigrationNumberRanges(string module, int version, bool allowed) =>
        Assert.Equal(allowed, MigrationCatalog.IsVersionAllowedFor(module, version));
}
