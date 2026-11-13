using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.ControlCenter;
using Xunit;

namespace MedicalShopManager.UnitTests.ControlCenter;

/// <summary>Prefix &amp; Sequences editing rules (contract §8: never re-issue a number).</summary>
public class ControlCenterSequenceRulesTests
{
    private static readonly DateTime AsOf = new(2026, 11, 4); // FY 2026-27

    private static DocumentSequence Seq(string prefix = "SR", string template = "{PREFIX}/{FY2}/{SEQ}", int padding = 5,
        bool reset = true, long next = 1) => new()
    {
        DocumentType = DocumentType.SaleReturn,
        Prefix = prefix,
        Template = template,
        Padding = padding,
        ResetEachFinancialYear = reset,
        NextNumber = next
    };

    [Fact]
    public void Preview_FormatsWithTheFinancialYear()
    {
        SequenceChangeRules.Preview(Seq(next: 7), 7, AsOf).Should().Be("SR/2627/00007");
        SequenceChangeRules.Preview(Seq(template: "{PREFIX}-{FY}-{SEQ}", padding: 3), 12, AsOf).Should().Be("SR-2026-27-012");
        SequenceChangeRules.Preview(Seq(template: ""), 1, AsOf).Should().BeNull();
    }

    [Fact]
    public void ValidChange_HasNoErrors()
    {
        var counters = new[] { new SequenceCounter(2026, 11) }; // 10 issued this FY
        SequenceChangeRules.Validate(Seq(next: 11), Seq(prefix: "SRN", next: 25), counters, AsOf).Should().BeEmpty();
    }

    [Fact]
    public void NextNumber_CannotGoBackToAnIssuedNumber()
    {
        var counters = new[] { new SequenceCounter(2025, 300), new SequenceCounter(2026, 11) };

        var errors = SequenceChangeRules.Validate(Seq(next: 11), Seq(next: 10), counters, AsOf);

        errors.Should().ContainSingle(e => e.Field == nameof(DocumentSequence.NextNumber))
            .Which.Message.Should().Contain("11 or more").And.Contain("10 has already been issued");
        SequenceChangeRules.MinimumNextNumber(Seq(next: 11), Seq(next: 11), counters, AsOf).Should().Be(11,
            "last year's counter does not matter while the series resets every April");
    }

    [Fact]
    public void SwitchingResetOff_MustContinueAboveEveryYearsNumbers()
    {
        var counters = new[] { new SequenceCounter(2025, 300), new SequenceCounter(2026, 11) };
        var proposed = Seq(template: "{PREFIX}/{SEQ}", reset: false, next: 50);

        SequenceChangeRules.MinimumNextNumber(Seq(next: 11), proposed, counters, AsOf).Should().Be(300);
        SequenceChangeRules.Validate(Seq(next: 11), proposed, counters, AsOf)
            .Should().ContainSingle(e => e.Field == nameof(DocumentSequence.NextNumber));
        SequenceChangeRules.Validate(Seq(next: 11), Seq(template: "{PREFIX}/{SEQ}", reset: false, next: 300), counters, AsOf)
            .Should().BeEmpty();
    }

    [Fact]
    public void SwitchingResetOn_WithAnFyTemplate_ContinuesAboveTheRunningSeries()
    {
        var current = Seq(reset: false, next: 42);
        var counters = new[] { new SequenceCounter(0, 42) };

        SequenceChangeRules.MinimumNextNumber(current, Seq(reset: true, next: 1), counters, AsOf).Should().Be(42);

        // A running series without {FY} produced different text, so a fresh FY series may start at 1.
        var plain = Seq(template: "{PREFIX}{SEQ}", reset: false, next: 42);
        SequenceChangeRules.MinimumNextNumber(plain, Seq(reset: true, next: 1), counters, AsOf).Should().Be(1);
    }

    [Fact]
    public void FormatErrors_AreReportedAgainstTheTemplate()
    {
        var current = Seq();
        var none = Array.Empty<SequenceCounter>();

        SequenceChangeRules.Validate(current, Seq(template: "{PREFIX}/{FY2}"), none, AsOf)
            .Should().ContainSingle(e => e.Field == nameof(DocumentSequence.Template)).Which.Message.Should().Contain("{SEQ}");
        SequenceChangeRules.Validate(current, Seq(template: "{PREFIX}/{SEQ}", reset: true), none, AsOf)
            .Should().ContainSingle(e => e.Field == nameof(DocumentSequence.Template)).Which.Message.Should().Contain("{FY}");
        SequenceChangeRules.Validate(current, Seq(prefix: "PHARMACY", template: "{PREFIX}/{FY}/{SEQ}", padding: 6), none, AsOf)
            .Should().ContainSingle(e => e.Field == nameof(DocumentSequence.Template)).Which.Message.Should().Contain("16");
        SequenceChangeRules.Validate(current, Seq(prefix: "S R"), none, AsOf)
            .Should().ContainSingle(e => e.Field == nameof(DocumentSequence.Template));
        SequenceChangeRules.Validate(current, Seq(padding: 0), none, AsOf)
            .Should().ContainSingle(e => e.Field == nameof(DocumentSequence.Template));
    }

    [Fact]
    public void PrefixRules()
    {
        var none = Array.Empty<SequenceCounter>();
        SequenceChangeRules.Validate(Seq(), Seq(prefix: ""), none, AsOf)
            .Should().Contain(e => e.Field == nameof(DocumentSequence.Prefix) && e.Message.Contains("Enter a prefix"));
        SequenceChangeRules.Validate(Seq(), Seq(prefix: "ABCDEFGHIJK", template: "{PREFIX}{SEQ}", reset: false, padding: 3), none, AsOf)
            .Should().Contain(e => e.Field == nameof(DocumentSequence.Prefix));
        SequenceChangeRules.Validate(Seq(), Seq(prefix: "", template: "{FY2}/{SEQ}"), none, AsOf).Should().BeEmpty();
    }

    [Fact]
    public void NextNumber_MustBePositive()
    {
        SequenceChangeRules.Validate(Seq(), Seq(next: 0), Array.Empty<SequenceCounter>(), AsOf)
            .Should().ContainSingle(e => e.Field == nameof(DocumentSequence.NextNumber) && e.Message.Contains("at least 1"));
    }

    [Fact]
    public void Describe_ShowsLastIssuedAndPreview()
    {
        var overview = SequenceChangeRules.Describe(Seq(next: 11), new[] { new SequenceCounter(2026, 11) }, AsOf);

        overview.NextNumberPreview.Should().Be("SR/2627/00011");
        overview.LastIssuedNumber.Should().Be(10);
        overview.LastIssuedPreview.Should().Be("SR/2627/00010");
        overview.MinimumNextNumber.Should().Be(11);

        var fresh = SequenceChangeRules.Describe(Seq(), Array.Empty<SequenceCounter>(), AsOf);
        fresh.LastIssuedNumber.Should().Be(0);
        fresh.LastIssuedPreview.Should().BeNull();
    }

    [Fact]
    public void PeriodFor_FollowsTheIndianFinancialYear()
    {
        SequenceChangeRules.PeriodFor(true, new DateTime(2027, 3, 31)).Should().Be(2026);
        SequenceChangeRules.PeriodFor(true, new DateTime(2027, 4, 1)).Should().Be(2027);
        SequenceChangeRules.PeriodFor(false, new DateTime(2027, 4, 1)).Should().Be(0);
    }

    [Fact]
    public void EveryDocumentTypeHasAName_AndPresetsAreValid()
    {
        foreach (var type in Enum.GetValues<DocumentType>())
            SequenceChangeRules.DisplayName(type).Should().NotBeNullOrWhiteSpace();
        foreach (var preset in SequenceChangeRules.TemplatePresets)
        {
            var resets = preset.Contains("{FY", StringComparison.Ordinal);
            SequenceChangeRules.Validate(Seq(), Seq(template: preset, reset: resets), Array.Empty<SequenceCounter>(), AsOf)
                .Should().BeEmpty(preset);
        }
    }
}
