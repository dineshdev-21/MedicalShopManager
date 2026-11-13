using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Infrastructure.Numbering;
using Xunit;

namespace MedicalShopManager.IntegrationTests.ControlCenter;

/// <summary>Prefix &amp; Sequences over the real DocumentNumberService and DocumentSequenceCounters.</summary>
public class ControlCenterSequenceIntegrationTests : ControlCenterDbTestBase
{
    private static readonly DateTime AsOf = new(2026, 11, 4); // FY 2026-27

    private async Task<DocumentSequence> CurrentAsync(DocumentType type) => (await Numbers.GetSequenceAsync(type, AsOf))!;

    private static DocumentSequence Copy(DocumentSequence s, Action<DocumentSequence> change)
    {
        var copy = new DocumentSequence
        {
            DocumentType = s.DocumentType,
            Prefix = s.Prefix,
            Template = s.Template,
            Padding = s.Padding,
            ResetEachFinancialYear = s.ResetEachFinancialYear,
            NextNumber = s.NextNumber
        };
        change(copy);
        return copy;
    }

    private async Task AllocateAsync(DocumentType type, int count)
    {
        for (var i = 0; i < count; i++) await Numbers.AllocateNextAsync(type, AsOf);
    }

    [Fact]
    public async Task Overview_ListsEveryDocumentType_WithPreviews()
    {
        await AllocateAsync(DocumentType.SaleReturn, 3);

        var overview = await Sequences.GetOverviewAsync(AsOf);

        overview.Select(o => o.Sequence.DocumentType).Should().BeEquivalentTo(Enum.GetValues<DocumentType>());
        var sr = overview.Single(o => o.Sequence.DocumentType == DocumentType.SaleReturn);
        sr.LastIssuedNumber.Should().Be(3);
        sr.LastIssuedPreview.Should().Be(await PreviewAsync(DocumentType.SaleReturn, 3));
        sr.NextNumberPreview.Should().Be(await Numbers.PeekNextAsync(DocumentType.SaleReturn, AsOf));
        sr.MinimumNextNumber.Should().Be(4);
    }

    private async Task<string> PreviewAsync(DocumentType type, long number) =>
        SequenceChangeRules.Preview(await CurrentAsync(type), number, AsOf)!;

    [Fact]
    public async Task Save_ChangesPrefixAndNextNumber_AndTheNextAllocationUsesThem()
    {
        var current = await CurrentAsync(DocumentType.CreditNote);
        await Sequences.SaveAsync(Copy(current, s => { s.Prefix = " CN "; s.NextNumber = 101; s.Padding = 4; }), AsOf);

        (await Numbers.AllocateNextAsync(DocumentType.CreditNote, AsOf)).Should().Be("CN/2627/0101");
        var audit = (await AuditAsync("ControlCenter.SequenceUpdated")).Should().ContainSingle().Subject;
        audit.EntityId.Should().Be((long)DocumentType.CreditNote);
        audit.NewValues.Should().Contain("\"NextNumber\":101");
    }

    [Fact]
    public async Task Save_RefusesANextNumberThatWasAlreadyIssued_AndChangesNothing()
    {
        await AllocateAsync(DocumentType.Expense, 5);
        var current = await CurrentAsync(DocumentType.Expense);

        var ex = await FluentActions.Awaiting(() => Sequences.SaveAsync(Copy(current, s => { s.Prefix = "EXP"; s.NextNumber = 3; }), AsOf))
            .Should().ThrowAsync<ControlCenterValidationException>();
        ex.Which.Errors.Should().ContainSingle(e => e.Field == nameof(DocumentSequence.NextNumber));

        var after = await CurrentAsync(DocumentType.Expense);
        (after.Prefix, after.NextNumber).Should().Be((current.Prefix, 6L));
        (await AuditAsync("ControlCenter.SequenceUpdated")).Should().BeEmpty();
    }

    [Fact]
    public async Task Save_SwitchingFyResetOff_MustContinueAboveLastYearsNumbers()
    {
        await AllocateAsync(DocumentType.Receipt, 1);                                  // FY 2026-27: number 1
        for (var i = 0; i < 7; i++) await Numbers.AllocateNextAsync(DocumentType.Receipt, new DateTime(2026, 2, 1)); // FY 2025-26: 1..7
        var current = await CurrentAsync(DocumentType.Receipt);
        current.ResetEachFinancialYear.Should().BeTrue();

        var noReset = Copy(current, s => { s.ResetEachFinancialYear = false; s.Template = "{PREFIX}/{SEQ}"; s.NextNumber = 2; });
        var ex = await FluentActions.Awaiting(() => Sequences.SaveAsync(noReset, AsOf)).Should().ThrowAsync<ControlCenterValidationException>();
        ex.Which.Message.Should().Contain("8 or more");

        await Sequences.SaveAsync(Copy(noReset, s => s.NextNumber = 8), AsOf);
        var allocated = await Numbers.AllocateNextAsync(DocumentType.Receipt, AsOf);
        allocated.Should().EndWith("/00008");
    }

    [Fact]
    public async Task Save_InvalidTemplate_IsAFieldError()
    {
        var current = await CurrentAsync(DocumentType.Payment);
        var ex = await FluentActions.Awaiting(() => Sequences.SaveAsync(Copy(current, s => s.Template = "{PREFIX}/{FY2}"), AsOf))
            .Should().ThrowAsync<ControlCenterValidationException>();
        ex.Which.Errors.Should().ContainSingle(e => e.Field == nameof(DocumentSequence.Template));
    }

    [Fact]
    public async Task NumberAllocatedInARolledBackTransaction_IsNotConsumed_SoTheMinimumDoesNotMove()
    {
        using (var conn = Db.CreateNewConnection())
        using (var tx = conn.BeginTransaction())
        {
            await DocumentSequenceAllocator.AllocateAsync(conn, tx, DocumentType.DebitNote, AsOf);
            tx.Rollback();
        }

        var overview = (await Sequences.GetOverviewAsync(AsOf)).Single(o => o.Sequence.DocumentType == DocumentType.DebitNote);
        overview.LastIssuedNumber.Should().Be(0);
        overview.MinimumNextNumber.Should().Be(1);
    }
}
