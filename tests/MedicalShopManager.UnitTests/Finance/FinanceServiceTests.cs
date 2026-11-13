using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Infrastructure.Modules.Finance;
using Moq;
using Xunit;

namespace MedicalShopManager.UnitTests.Finance;

/// <summary>Finance service orchestration with mocked repositories: validation before writes, audit after writes.</summary>
public class FinanceServiceTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    private readonly Mock<IFinanceAccountRepository> _accounts = new();
    private readonly Mock<IFinancePartyRepository> _parties = new();
    private readonly Mock<IFinanceVoucherRepository> _vouchers = new();
    private readonly Mock<IFinanceChequeRepository> _cheques = new();
    private readonly Mock<IFinanceExpenseRepository> _expenses = new();
    private readonly Mock<IDocumentNumberService> _numbers = new();
    private readonly Mock<IAppSettingsRepository> _settings = new();
    private readonly Mock<IAuditService> _audit = new();

    private static readonly FinanceAccount Cash = new() { Id = 1, Name = "Cash In Hand", AccountType = FinanceAccountType.Cash, BalancePaisa = 10000 };
    private static readonly FinanceAccount Bank = new() { Id = 2, Name = "SBI Current", AccountType = FinanceAccountType.Bank, BalancePaisa = 0 };

    public FinanceServiceTests()
    {
        _accounts.Setup(a => a.ListAsync(It.IsAny<DateTime>(), It.IsAny<bool>())).ReturnsAsync(new[] { Cash, Bank });
        _expenses.Setup(e => e.ListCategoriesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new[] { new FinanceExpenseCategory { Id = 1, Name = "Rent" } });
    }

    private CashBankService CashBank() => new(_accounts.Object, _audit.Object, () => Today);
    private PartyPaymentService Payments() => new(_parties.Object, _vouchers.Object, _accounts.Object, _numbers.Object, _audit.Object, () => Today);
    private ChequeService Cheques() => new(_cheques.Object, _audit.Object, () => Today);
    private ExpenseService Expenses() => new(_expenses.Object, _accounts.Object, _numbers.Object, _settings.Object, _audit.Object, () => Today);

    private void VerifyAudited(string action) =>
        _audit.Verify(a => a.LogAsync(action, It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);

    // ───────────────────────── Cash & bank ─────────────────────────

    [Fact]
    public async Task SaveAccount_New_InsertsAndAudits()
    {
        var account = new FinanceAccount { Name = "HDFC", AccountType = FinanceAccountType.Bank, OpeningBalancePaisa = 500000, OpeningDate = Today };
        _accounts.Setup(a => a.InsertAsync(account)).ReturnsAsync(5);
        _accounts.Setup(a => a.GetAsync(5)).ReturnsAsync(new FinanceAccount { Id = 5, Name = "HDFC" });

        var saved = await CashBank().SaveAccountAsync(account, 1);

        saved.Id.Should().Be(5);
        VerifyAudited("FinanceAccountCreated");
    }

    [Fact]
    public async Task SaveAccount_DuplicateNameAndFutureOpening_Rejected()
    {
        _accounts.Setup(a => a.NameExistsAsync("SBI Current", 0)).ReturnsAsync(true);

        var act = () => CashBank().SaveAccountAsync(
            new FinanceAccount { Name = "SBI Current", AccountType = FinanceAccountType.Bank, OpeningDate = Today.AddDays(1) }, 1);

        var ex = (await act.Should().ThrowAsync<FinanceValidationException>()).Which;
        ex.Errors.Should().Contain(e => e.Contains("already exists")).And.Contain("The opening balance date cannot be in the future.");
        _accounts.Verify(a => a.InsertAsync(It.IsAny<FinanceAccount>()), Times.Never);
    }

    [Fact]
    public async Task SaveAccount_CashCannotChangeType()
    {
        _accounts.Setup(a => a.GetAsync(1)).ReturnsAsync(new FinanceAccount { Id = 1, Name = "Cash In Hand", AccountType = FinanceAccountType.Cash });

        var act = () => CashBank().SaveAccountAsync(new FinanceAccount { Id = 1, Name = "Cash In Hand", AccountType = FinanceAccountType.Bank, OpeningDate = Today }, 1);

        (await act.Should().ThrowAsync<FinanceValidationException>()).Which.Errors.Should().Contain(e => e.Contains("Cash in hand cannot be changed"));
    }

    [Fact]
    public async Task RecordMovement_CashCannotGoNegative()
    {
        var act = () => CashBank().RecordMovementAsync(new FinanceMovement
        {
            Kind = FinanceMovementKind.Transfer, FromAccountId = 1, ToAccountId = 2, AmountPaisa = 10001, MovementDate = Today
        }, 1);

        (await act.Should().ThrowAsync<FinanceValidationException>()).Which.Errors.Should().ContainSingle(e => e.Contains("has only"));
        _accounts.Verify(a => a.InsertMovementAsync(It.IsAny<FinanceMovement>()), Times.Never);
    }

    [Fact]
    public async Task RecordMovement_Valid_InsertsWithUserAndAudits()
    {
        _accounts.Setup(a => a.InsertMovementAsync(It.IsAny<FinanceMovement>())).ReturnsAsync(9);
        var movement = new FinanceMovement { Kind = FinanceMovementKind.Transfer, FromAccountId = 1, ToAccountId = 2, AmountPaisa = 10000, MovementDate = Today };

        (await CashBank().RecordMovementAsync(movement, 3)).Should().Be(9);

        movement.CreatedByUserId.Should().Be(3);
        VerifyAudited("FinanceMovementRecorded");
    }

    [Fact]
    public async Task CancelMovement_NeedsReason()
    {
        var act = () => CashBank().CancelMovementAsync(9, " ", 1);
        await act.Should().ThrowAsync<FinanceValidationException>();
        _accounts.Verify(a => a.CancelMovementAsync(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Statement_RejectsReversedOrTooLongRange()
    {
        await CashBank().Invoking(s => s.GetStatementAsync(1, Today, Today.AddDays(-1))).Should().ThrowAsync<FinanceValidationException>();
        await CashBank().Invoking(s => s.GetStatementAsync(1, Today.AddYears(-6), Today)).Should().ThrowAsync<FinanceValidationException>();
    }

    [Fact]
    public async Task SetMethodAccount_AuditsOldAndNew()
    {
        _accounts.Setup(a => a.GetMethodAccountsAsync()).ReturnsAsync(new[] { new PaymentMethodAccount(PaymentMethod.UPI, 1) });

        await CashBank().SetMethodAccountAsync(PaymentMethod.UPI, 2, 1);

        _accounts.Verify(a => a.SetMethodAccountAsync(PaymentMethod.UPI, 2), Times.Once);
        VerifyAudited("FinanceMethodAccountChanged");
    }

    // ───────────────────────── Receipts / payments ─────────────────────────

    private static VoucherRequest Payment(long amount = 5000, long account = 1) => new()
    {
        Kind = FinanceVoucherKind.Payment, PartyType = PartyType.Supplier, PartyId = 3, Date = Today, AmountPaisa = amount,
        Mode = FinancePaymentMode.Cash, AccountId = account, Reference = "  NEFT1 "
    };

    [Fact]
    public async Task Post_PaymentFromCashBeyondBalance_Rejected()
    {
        var act = () => Payments().PostAsync(Payment(20000));

        (await act.Should().ThrowAsync<FinanceValidationException>()).Which.Errors.Should().ContainSingle(e => e.Contains("has only"));
        _vouchers.Verify(v => v.PostAsync(It.IsAny<VoucherRequest>()), Times.Never);
    }

    [Fact]
    public async Task Post_PaymentFromBank_MayOverdraw_TrimsTextAndAudits()
    {
        VoucherRequest? posted = null;
        _vouchers.Setup(v => v.PostAsync(It.IsAny<VoucherRequest>()))
            .Callback<VoucherRequest>(r => posted = r)
            .ReturnsAsync(new FinanceVoucher { Id = 4, VoucherNumber = "PAY/2627/00001", Kind = FinanceVoucherKind.Payment });

        var voucher = await Payments().PostAsync(Payment(20000, 2));

        voucher.VoucherNumber.Should().Be("PAY/2627/00001");
        posted!.Reference.Should().Be("NEFT1");
        VerifyAudited("FinancePaymentPosted");
    }

    [Fact]
    public async Task Post_ExplicitAllocationsAboveAmount_Rejected()
    {
        var request = Payment(1000, 2) with
        {
            Allocations = new[] { new VoucherAllocation(AllocationTarget.Purchase, 1, "B-1", 1500) }
        };

        await Payments().Invoking(s => s.PostAsync(request)).Should().ThrowAsync<FinanceValidationException>();
    }

    [Fact]
    public async Task Parties_OnlyCustomersAndSuppliers()
    {
        await Payments().Invoking(s => s.GetDuesAsync(PartyType.Doctor, 1)).Should().ThrowAsync<FinanceValidationException>();
        await Payments().Invoking(s => s.GetBalancesAsync(PartyType.Other, Today)).Should().ThrowAsync<FinanceValidationException>();
    }

    [Fact]
    public async Task CancelVoucher_AuditsWithReason()
    {
        _vouchers.Setup(v => v.GetAsync(4)).ReturnsAsync(new FinanceVoucher { Id = 4, VoucherNumber = "RCT/2627/00001" });

        await Payments().CancelVoucherAsync(4, " Entered twice ", 1);

        _vouchers.Verify(v => v.CancelAsync(4, "Entered twice"), Times.Once);
        VerifyAudited("FinanceVoucherCancelled");
    }

    [Fact]
    public async Task PeekNumber_UsesReceiptOrPaymentSequence()
    {
        _numbers.Setup(n => n.PeekNextAsync(DocumentType.Receipt, Today)).ReturnsAsync("RCT/2627/00003");
        (await Payments().PeekNumberAsync(FinanceVoucherKind.Receipt, Today)).Should().Be("RCT/2627/00003");
    }

    // ───────────────────────── Cheques ─────────────────────────

    [Fact]
    public async Task ChequeChange_FutureDateRejected_ValidChangeAudited()
    {
        _cheques.Setup(c => c.GetAsync(7)).ReturnsAsync(new FinanceCheque
        {
            Id = 7, Direction = ChequeDirection.Received, ChequeNumber = "100200", ChequeDate = Today.AddDays(-2), Status = ChequeStatus.Pending, AccountId = 2
        });

        await Cheques().Invoking(s => s.ChangeStatusAsync(new ChequeStatusChange { ChequeId = 7, NewStatus = ChequeStatus.Deposited, Date = Today.AddDays(1) }))
            .Should().ThrowAsync<FinanceValidationException>().WithMessage("*future*");

        await Cheques().ChangeStatusAsync(new ChequeStatusChange { ChequeId = 7, NewStatus = ChequeStatus.Cleared, Date = Today });

        _cheques.Verify(c => c.ChangeStatusAsync(It.Is<ChequeStatusChange>(x => x.NewStatus == ChequeStatus.Cleared)), Times.Once);
        VerifyAudited("FinanceChequeCleared");
    }

    // ───────────────────────── Expenses ─────────────────────────

    private static ExpenseRequest Expense(long amount = 5000, long account = 1) => new()
    {
        ExpenseDate = Today, CategoryId = 1, AmountPaisa = amount, AccountId = account, Mode = FinancePaymentMode.Cash, Description = " Shop rent "
    };

    [Fact]
    public async Task SaveExpense_FromCashBeyondBalance_Rejected()
    {
        await Expenses().Invoking(s => s.SaveAsync(Expense(10001))).Should().ThrowAsync<FinanceValidationException>().WithMessage("*has only*");
        _expenses.Verify(e => e.InsertAsync(It.IsAny<FinanceExpense>()), Times.Never);
    }

    [Fact]
    public async Task SaveExpense_InterStatePayee_UsesIgst()
    {
        _settings.Setup(s => s.GetValueAsync("ShopGstin")).ReturnsAsync("27AAACM1234A1Z5");
        FinanceExpense? inserted = null;
        _expenses.Setup(e => e.InsertAsync(It.IsAny<FinanceExpense>()))
            .Callback<FinanceExpense>(e => inserted = e)
            .ReturnsAsync((FinanceExpense e) => e);

        await Expenses().SaveAsync(Expense(118000, 2) with
        {
            GstRatePercent = 18, AmountIncludesGst = true, PayeeGstin = "29abcde1234f1z5", InvoiceNumber = "KA-77", Mode = FinancePaymentMode.BankTransfer
        });

        inserted!.IsInterState.Should().BeTrue();
        inserted.IgstPaisa.Should().Be(18000);
        inserted.CgstPaisa.Should().Be(0);
        inserted.TaxablePaisa.Should().Be(100000);
        inserted.PayeeGstin.Should().Be("29ABCDE1234F1Z5");
        inserted.Description.Should().Be("Shop rent");
        inserted.CategoryName.Should().Be("Rent");
        VerifyAudited("FinanceExpenseRecorded");
    }

    [Fact]
    public async Task SaveExpense_SameStatePayee_SplitsCgstSgst_FallsBackToStateCodeSetting()
    {
        _settings.Setup(s => s.GetValueAsync("ControlCenter.Business.StateCode")).ReturnsAsync("27");
        FinanceExpense? inserted = null;
        _expenses.Setup(e => e.InsertAsync(It.IsAny<FinanceExpense>())).Callback<FinanceExpense>(e => inserted = e).ReturnsAsync((FinanceExpense e) => e);

        await Expenses().SaveAsync(Expense(100000, 2) with { GstRatePercent = 18, AmountIncludesGst = false, PayeeGstin = "27AAACM1234A1Z5", InvoiceNumber = "1" });

        inserted!.IsInterState.Should().BeFalse();
        inserted.CgstPaisa.Should().Be(9000);
        inserted.SgstPaisa.Should().Be(9000);
        inserted.AmountPaisa.Should().Be(118000);
    }

    [Fact]
    public async Task SaveExpense_EditGivesOldAmountBackBeforeCashCheck()
    {
        _expenses.Setup(e => e.GetAsync(8)).ReturnsAsync(new FinanceExpense { Id = 8, AccountId = 1, AmountPaisa = 6000 });

        await Expenses().SaveAsync(Expense(15000) with { Id = 8 });

        _expenses.Verify(e => e.UpdateAsync(It.Is<FinanceExpense>(x => x.Id == 8 && x.AmountPaisa == 15000)), Times.Once);
        VerifyAudited("FinanceExpenseUpdated");
    }

    [Fact]
    public async Task SaveExpense_CancelledCannotBeEdited()
    {
        _expenses.Setup(e => e.GetAsync(8)).ReturnsAsync(new FinanceExpense { Id = 8, AccountId = 1, AmountPaisa = 6000, IsCancelled = true });

        await Expenses().Invoking(s => s.SaveAsync(Expense(100) with { Id = 8 })).Should().ThrowAsync<FinanceValidationException>().WithMessage("*cancelled*");
    }

    [Fact]
    public async Task PreviewAmounts_MatchesSaveMaths()
    {
        var preview = await Expenses().PreviewAmountsAsync(118000, 18, true, null);
        preview.Should().Be(new ExpenseAmounts(100000, 9000, 9000, 0, 118000));
    }

    [Fact]
    public void Totals_ExcludeCancelled()
    {
        var totals = ExpenseService.Totals(new[]
        {
            new FinanceExpense { TaxablePaisa = 1000, CgstPaisa = 90, SgstPaisa = 90, AmountPaisa = 1180 },
            new FinanceExpense { TaxablePaisa = 500, AmountPaisa = 500, IsCancelled = true }
        });

        totals.Should().Be(new ExpenseTotals(1, 1000, 180, 1180));
    }

    [Fact]
    public async Task Categories_AddValidatesAndAudits()
    {
        await Expenses().Invoking(s => s.AddCategoryAsync("", 1)).Should().ThrowAsync<FinanceValidationException>();
        _expenses.Setup(e => e.InsertCategoryAsync("Courier")).ReturnsAsync(1);

        await Expenses().AddCategoryAsync(" Courier ", 1);

        VerifyAudited("FinanceExpenseCategoryCreated");
    }
}
