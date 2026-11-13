using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using Xunit;

namespace MedicalShopManager.UnitTests.Finance;

/// <summary>Pure Finance rules: allocation, ageing, running balances, cheque state machine, expense GST, validation, CSV.</summary>
public class FinanceRulesTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    private static DueDocument Due(long id, int daysAgo, long balance, AllocationTarget target = AllocationTarget.Sale) => new()
    {
        Target = target, DocumentId = id, Number = target == AllocationTarget.OpeningBalance ? "Opening balance" : $"INV-{id}",
        Date = Today.AddDays(-daysAgo), TotalPaisa = balance, BalancePaisa = balance
    };

    // ───────────────────────── Allocation ─────────────────────────

    [Fact]
    public void AllocateOldestFirst_SettlesOpeningBalanceThenOldestBills_RemainderStaysOnAccount()
    {
        var dues = new[] { Due(3, 5, 10000), Due(1, 40, 5000), Due(0, 400, 2000, AllocationTarget.OpeningBalance), Due(2, 40, 3000) };

        var result = FinanceRules.AllocateOldestFirst(12000, dues);

        result.Should().Equal(
            new VoucherAllocation(AllocationTarget.OpeningBalance, 0, "Opening balance", 2000),
            new VoucherAllocation(AllocationTarget.Sale, 1, "INV-1", 5000),
            new VoucherAllocation(AllocationTarget.Sale, 2, "INV-2", 3000),
            new VoucherAllocation(AllocationTarget.Sale, 3, "INV-3", 2000));
    }

    [Fact]
    public void AllocateOldestFirst_MoreThanDue_AllocatesOnlyTheDues()
    {
        var result = FinanceRules.AllocateOldestFirst(50000, new[] { Due(1, 3, 25100) });

        result.Should().ContainSingle().Which.AmountPaisa.Should().Be(25100);
        (50000 - result.Sum(a => a.AmountPaisa)).Should().Be(24900, "the rest is an advance");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void AllocateOldestFirst_NonPositiveAmount_AllocatesNothing(long amount) =>
        FinanceRules.AllocateOldestFirst(amount, new[] { Due(1, 3, 25100) }).Should().BeEmpty();

    [Fact]
    public void AllocateOldestFirst_SkipsSettledDocuments() =>
        FinanceRules.AllocateOldestFirst(100, new[] { Due(1, 9, 0), Due(2, 3, 500) }).Should().ContainSingle(a => a.DocumentId == 2);

    [Fact]
    public void ValidateAllocations_RejectsOverpaymentDuplicatesUnknownAndTotalAboveAmount()
    {
        var dues = new[] { Due(1, 5, 1000), Due(2, 3, 2000) };
        var allocations = new[]
        {
            new VoucherAllocation(AllocationTarget.Sale, 1, "INV-1", 1500),
            new VoucherAllocation(AllocationTarget.Sale, 2, "INV-2", 500),
            new VoucherAllocation(AllocationTarget.Sale, 2, "INV-2", 100),
            new VoucherAllocation(AllocationTarget.Sale, 9, "INV-9", 100),
            new VoucherAllocation(AllocationTarget.Sale, 3, "INV-3", 0)
        };

        var errors = FinanceRules.ValidateAllocations(1000, allocations, dues);

        errors.Should().Contain(e => e.Contains("INV-1") && e.Contains("more than its balance due"));
        errors.Should().Contain(e => e.Contains("INV-2 is selected twice"));
        errors.Should().Contain(e => e.Contains("INV-9 has no balance due"));
        errors.Should().Contain(e => e.Contains("INV-3") && e.Contains("more than zero"));
        errors.Should().Contain(e => e.Contains("add up to"));
    }

    [Fact]
    public void ValidateAllocations_ValidPartialAllocation_HasNoErrors() =>
        FinanceRules.ValidateAllocations(1500, new[] { new VoucherAllocation(AllocationTarget.Sale, 1, "INV-1", 1000) },
            new[] { Due(1, 5, 1000) }).Should().BeEmpty();

    [Theory]
    [InlineData(1000, 1000, PaymentStatus.Paid)]
    [InlineData(1000, 1200, PaymentStatus.Paid)]
    [InlineData(1000, 1, PaymentStatus.Partial)]
    [InlineData(1000, 0, PaymentStatus.Unpaid)]
    public void PaymentStatusFor_FollowsPaidAmount(long total, long paid, PaymentStatus expected) =>
        FinanceRules.PaymentStatusFor(total, paid).Should().Be(expected);

    // ───────────────────────── Ageing ─────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 0)]
    [InlineData(31, 1)]
    [InlineData(60, 1)]
    [InlineData(61, 2)]
    [InlineData(90, 2)]
    [InlineData(91, 3)]
    [InlineData(1000, 3)]
    public void AgeBucket_UsesInclusiveUpperBounds(int days, int bucket) => FinanceRules.AgeBucket(days).Should().Be(bucket);

    [Fact]
    public void Ageing_SplitsOpenBalancesByDocumentAge()
    {
        var buckets = FinanceRules.Ageing(new[] { Due(1, 10, 100), Due(2, 45, 200), Due(3, 75, 300), Due(4, 120, 400), Due(5, 5, 0) }, Today);

        buckets.Should().BeEquivalentTo(new { Days0To30Paisa = 100L, Days31To60Paisa = 200L, Days61To90Paisa = 300L, Over90Paisa = 400L });
        buckets.TotalPaisa.Should().Be(1000);
    }

    [Fact]
    public void Ageing_FutureDatedDocumentCountsAsCurrent() =>
        FinanceRules.Ageing(new[] { Due(1, -3, 100) }, Today).Days0To30Paisa.Should().Be(100);

    [Fact]
    public void PartyBalanceRow_NetsCreditsAndSignsSuppliersNegative()
    {
        var customer = new PartyBalanceRow { PartyType = PartyType.Customer, Ageing = new AgeingBuckets { Days0To30Paisa = 1000 }, CreditsPaisa = 300 };
        var supplier = customer with { PartyType = PartyType.Supplier };

        customer.NetDuePaisa.Should().Be(700);
        customer.SignedBalancePaisa.Should().Be(700);
        supplier.SignedBalancePaisa.Should().Be(-700);
        FinanceRules.Totals(new[] { customer, supplier }).Should().BeEquivalentTo(new { CreditsPaisa = 600L, PartyCount = 2, NetDuePaisa = 1400L });
    }

    // ───────────────────────── Running balances ─────────────────────────

    [Fact]
    public void AccountStatement_RunningBalanceOrdersByDateThenSourceOrder()
    {
        var lines = new[]
        {
            new AccountStatementLine { Date = Today, InPaisa = 500, SortKey = 2 },
            new AccountStatementLine { Date = Today.AddDays(-1), OutPaisa = 200, SortKey = 9 },
            new AccountStatementLine { Date = Today, OutPaisa = 100, SortKey = 1 }
        };

        var result = FinanceRules.WithRunningBalance(1000, lines);

        result.Select(l => l.BalancePaisa).Should().Equal(800, 700, 1200);
    }

    [Fact]
    public void PartyLedger_RunningBalanceAndClosing()
    {
        var lines = FinanceRules.WithRunningBalance(100, new[]
        {
            new PartyLedgerLine { Date = Today.AddDays(-2), DebitPaisa = 25100, SortKey = 1 },
            new PartyLedgerLine { Date = Today, CreditPaisa = 20000, SortKey = 2 }
        });
        var ledger = new PartyLedger { OpeningBalancePaisa = 100, Lines = lines };

        lines.Select(l => l.BalancePaisa).Should().Equal(25200, 5200);
        ledger.ClosingBalancePaisa.Should().Be(5200);
        lines[1].BalanceLabel.Should().Be("₹52.00 Dr");
    }

    [Theory]
    [InlineData(125000, "₹1,250.00 Dr")]
    [InlineData(-30000, "₹300.00 Cr")]
    [InlineData(0, "₹0.00")]
    [InlineData(12345678, "₹1,23,456.78 Dr")]
    public void DrCr_FormatsReceivableAndPayable(long paisa, string expected) => FinanceRules.DrCr(paisa).Should().Be(expected);

    // ───────────────────────── Cheques ─────────────────────────

    [Theory]
    [InlineData(ChequeStatus.Pending, ChequeStatus.Deposited, true)]
    [InlineData(ChequeStatus.Pending, ChequeStatus.Cleared, true)]
    [InlineData(ChequeStatus.Pending, ChequeStatus.Bounced, true)]
    [InlineData(ChequeStatus.Pending, ChequeStatus.Cancelled, true)]
    [InlineData(ChequeStatus.Deposited, ChequeStatus.Cleared, true)]
    [InlineData(ChequeStatus.Deposited, ChequeStatus.Bounced, true)]
    [InlineData(ChequeStatus.Deposited, ChequeStatus.Cancelled, false)]
    [InlineData(ChequeStatus.Deposited, ChequeStatus.Pending, false)]
    [InlineData(ChequeStatus.Cleared, ChequeStatus.Bounced, false)]
    [InlineData(ChequeStatus.Bounced, ChequeStatus.Cleared, false)]
    [InlineData(ChequeStatus.Cancelled, ChequeStatus.Deposited, false)]
    public void CanTransition_FollowsChequeLifeCycle(ChequeStatus from, ChequeStatus to, bool allowed) =>
        FinanceRules.CanTransition(from, to).Should().Be(allowed);

    private static FinanceCheque Cheque(ChequeStatus status = ChequeStatus.Pending, long? accountId = null) => new()
    {
        Id = 1, Direction = ChequeDirection.Received, ChequeNumber = "123456", ChequeDate = Today, AmountPaisa = 10000,
        Status = status, AccountId = accountId
    };

    [Fact]
    public void ValidateChequeChange_DepositNeedsAnAccount()
    {
        FinanceRules.ValidateChequeChange(Cheque(), new ChequeStatusChange { NewStatus = ChequeStatus.Deposited, Date = Today })
            .Should().ContainSingle(e => e.Contains("bank account"));
        FinanceRules.ValidateChequeChange(Cheque(), new ChequeStatusChange { NewStatus = ChequeStatus.Deposited, Date = Today, AccountId = 2 })
            .Should().BeEmpty();
    }

    [Fact]
    public void ValidateChequeChange_CannotClearBeforeChequeDate() =>
        FinanceRules.ValidateChequeChange(Cheque(accountId: 2), new ChequeStatusChange { NewStatus = ChequeStatus.Cleared, Date = Today.AddDays(-1) })
            .Should().ContainSingle(e => e.Contains("cannot clear before"));

    [Fact]
    public void ValidateChequeChange_BounceNeedsReason()
    {
        FinanceRules.ValidateChequeChange(Cheque(ChequeStatus.Deposited, 2), new ChequeStatusChange { NewStatus = ChequeStatus.Bounced, Date = Today })
            .Should().ContainSingle(e => e.Contains("bounce reason"));
        FinanceRules.ValidateChequeChange(Cheque(ChequeStatus.Deposited, 2),
            new ChequeStatusChange { NewStatus = ChequeStatus.Bounced, Date = Today, Note = "Insufficient funds" }).Should().BeEmpty();
    }

    [Fact]
    public void ValidateChequeChange_FinalStatusCannotMove() =>
        FinanceRules.ValidateChequeChange(Cheque(ChequeStatus.Cleared, 2), new ChequeStatusChange { NewStatus = ChequeStatus.Bounced, Date = Today, Note = "x" })
            .Should().ContainSingle(e => e.Contains("cannot be marked"));

    [Fact]
    public void FinanceCheque_PostDatedOnlyWhileOpen()
    {
        var cheque = Cheque();
        cheque.ChequeDate = Today.AddDays(10);
        cheque.IsPostDated(Today).Should().BeTrue();
        cheque.Status = ChequeStatus.Cancelled;
        cheque.IsPostDated(Today).Should().BeFalse();
    }

    // ───────────────────────── Expenses ─────────────────────────

    [Fact]
    public void ComputeExpense_InclusiveAmount_ExtractsTaxableAndSplitsCgstSgst()
    {
        // ₹1,180 including 18 % GST = ₹1,000 taxable + ₹90 CGST + ₹90 SGST.
        var amounts = FinanceRules.ComputeExpense(118000, 18m, amountIncludesGst: true, isInterState: false);

        amounts.Should().Be(new ExpenseAmounts(100000, 9000, 9000, 0, 118000));
    }

    [Fact]
    public void ComputeExpense_ExclusiveAmount_AddsGstOnTop() =>
        FinanceRules.ComputeExpense(100000, 18m, amountIncludesGst: false, isInterState: false)
            .Should().Be(new ExpenseAmounts(100000, 9000, 9000, 0, 118000));

    [Fact]
    public void ComputeExpense_InterState_UsesIgst() =>
        FinanceRules.ComputeExpense(105000, 5m, amountIncludesGst: true, isInterState: true)
            .Should().Be(new ExpenseAmounts(100000, 0, 0, 5000, 105000));

    [Fact]
    public void ComputeExpense_OddPaisaTaxGoesToCgst()
    {
        // ₹10.05 at 12 % exclusive: tax = round(120.6) = 121 paisa, an odd figure: CGST 61, SGST 60.
        var amounts = FinanceRules.ComputeExpense(1005, 12m, amountIncludesGst: false, isInterState: false);

        amounts.TaxPaisa.Should().Be(121);
        amounts.CgstPaisa.Should().Be(61);
        amounts.SgstPaisa.Should().Be(60);
        amounts.TotalPaisa.Should().Be(1126);
    }

    [Fact]
    public void ComputeExpense_NoGst_TaxableIsTheAmount() =>
        FinanceRules.ComputeExpense(50000, 0m, true, false).Should().Be(new ExpenseAmounts(50000, 0, 0, 0, 50000));

    private static ExpenseRequest Expense() => new()
    {
        ExpenseDate = Today, CategoryId = 1, AmountPaisa = 1500000, AccountId = 1, GstRatePercent = 0, Mode = FinancePaymentMode.Cash
    };

    [Fact]
    public void ValidateExpense_ValidRequest_HasNoErrors() => FinanceRules.ValidateExpense(Expense(), Today).Should().BeEmpty();

    [Fact]
    public void ValidateExpense_ReportsEveryProblem()
    {
        var errors = FinanceRules.ValidateExpense(Expense() with
        {
            CategoryId = 0, AmountPaisa = 0, ExpenseDate = Today.AddDays(1), AccountId = 0, GstRatePercent = 7, PayeeGstin = "BAD"
        }, Today);

        errors.Should().Contain("Choose an expense category.");
        errors.Should().Contain(e => e.StartsWith("Enter the expense amount"));
        errors.Should().Contain("The expense date cannot be in the future.");
        errors.Should().Contain("Choose the account the expense was paid from.");
        errors.Should().Contain(e => e.StartsWith("GST rate must be"));
        errors.Should().Contain(e => e.StartsWith("Payee GSTIN must be 15 characters"));
    }

    [Fact]
    public void ValidateExpense_GstNeedsPayeeGstinAndBillNumber()
    {
        var errors = FinanceRules.ValidateExpense(Expense() with { GstRatePercent = 18 }, Today);

        errors.Should().Contain(e => e.Contains("payee's GSTIN"));
        errors.Should().Contain(e => e.Contains("payee's bill number"));
        FinanceRules.ValidateExpense(Expense() with { GstRatePercent = 18, PayeeGstin = "27AAACM1234A1Z5", InvoiceNumber = "B-77" }, Today)
            .Should().BeEmpty();
    }

    [Fact]
    public void ValidateExpense_ChequeModeRejected() =>
        FinanceRules.ValidateExpense(Expense() with { Mode = FinancePaymentMode.Cheque }, Today).Should().ContainSingle(e => e.Contains("cheque"));

    // ───────────────────────── Vouchers / accounts / movements ─────────────────────────

    private static VoucherRequest Receipt() => new()
    {
        Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = 4, Date = Today, AmountPaisa = 10000,
        Mode = FinancePaymentMode.Cash, AccountId = 1
    };

    [Fact]
    public void ValidateVoucher_ValidReceipt_HasNoErrors() => FinanceRules.ValidateVoucher(Receipt(), Today).Should().BeEmpty();

    [Fact]
    public void ValidateVoucher_ReceiptMustBeFromCustomer_PaymentToSupplier()
    {
        FinanceRules.ValidateVoucher(Receipt() with { PartyType = PartyType.Supplier }, Today).Should().Contain("Receipts are taken from customers.");
        FinanceRules.ValidateVoucher(Receipt() with { Kind = FinanceVoucherKind.Payment }, Today).Should().Contain("Payments are made to suppliers.");
    }

    [Fact]
    public void ValidateVoucher_NonChequeNeedsAccount_FutureDateAndZeroRejected()
    {
        var errors = FinanceRules.ValidateVoucher(Receipt() with { AccountId = null, AmountPaisa = 0, Date = Today.AddDays(1), PartyId = 0 }, Today);

        errors.Should().Contain("Choose the account the money was received into.");
        errors.Should().Contain("Enter the amount (more than zero).");
        errors.Should().Contain("The payment date cannot be in the future.");
        errors.Should().Contain("Choose the customer who paid.");
    }

    [Fact]
    public void ValidateVoucher_ChequeNeedsSixDigitNumberButNoAccount()
    {
        var cheque = Receipt() with { Mode = FinancePaymentMode.Cheque, AccountId = null };

        FinanceRules.ValidateVoucher(cheque, Today).Should().Contain("Enter the cheque number.");
        FinanceRules.ValidateVoucher(cheque with { Cheque = new ChequeDetails("12AB", Today, "SBI") }, Today)
            .Should().ContainSingle(e => e.Contains("6 digits"));
        FinanceRules.ValidateVoucher(cheque with { Cheque = new ChequeDetails("004512", Today.AddDays(200), "SBI") }, Today)
            .Should().ContainSingle(e => e.Contains("6 months"));
        FinanceRules.ValidateVoucher(cheque with { Cheque = new ChequeDetails("004512", Today.AddDays(15), "SBI") }, Today).Should().BeEmpty();
    }

    [Fact]
    public void ValidateAccount_ChecksBankDetailsUpiAndCash()
    {
        FinanceRules.ValidateAccount(new FinanceAccount { Name = "HDFC Current", AccountType = FinanceAccountType.Bank, AccountNumber = "50100123456789", Ifsc = "HDFC0001234" })
            .Should().BeEmpty();
        var errors = FinanceRules.ValidateAccount(new FinanceAccount { Name = "", AccountType = FinanceAccountType.Bank, AccountNumber = "12AB", Ifsc = "HDFC1234" });
        errors.Should().Contain("Enter the account name.");
        errors.Should().Contain(e => e.StartsWith("Bank account number"));
        errors.Should().Contain(e => e.StartsWith("IFSC"));
        FinanceRules.ValidateAccount(new FinanceAccount { Name = "GPay", AccountType = FinanceAccountType.UpiWallet, UpiId = "shop" })
            .Should().ContainSingle(e => e.StartsWith("UPI ID"));
        FinanceRules.ValidateAccount(new FinanceAccount { Name = "Till", AccountType = FinanceAccountType.Cash, OpeningBalancePaisa = -1 })
            .Should().ContainSingle(e => e.Contains("negative"));
        FinanceRules.ValidateAccount(new FinanceAccount { Name = "SBI OD", AccountType = FinanceAccountType.Bank, OpeningBalancePaisa = -500000 })
            .Should().BeEmpty("a bank account may be overdrawn");
    }

    [Fact]
    public void ValidateMovement_TransferNeedsTwoDifferentAccounts_AdjustmentNeedsNote()
    {
        var transfer = new FinanceMovement { Kind = FinanceMovementKind.Transfer, FromAccountId = 1, ToAccountId = 1, AmountPaisa = 100, MovementDate = Today };
        FinanceRules.ValidateMovement(transfer, Today).Should().Contain("Choose two different accounts to transfer between.");
        transfer.ToAccountId = 2;
        FinanceRules.ValidateMovement(transfer, Today).Should().BeEmpty();

        var add = new FinanceMovement { Kind = FinanceMovementKind.AddMoney, ToAccountId = 1, AmountPaisa = 100, MovementDate = Today };
        FinanceRules.ValidateMovement(add, Today).Should().ContainSingle(e => e.Contains("note"));
        add.Notes = "Owner capital";
        FinanceRules.ValidateMovement(add, Today).Should().BeEmpty();

        var reduce = new FinanceMovement { Kind = FinanceMovementKind.ReduceMoney, AmountPaisa = 0, MovementDate = Today.AddDays(1), Notes = "x" };
        FinanceRules.ValidateMovement(reduce, Today).Should().Contain("Choose the account to take money from.")
            .And.Contain("Enter the amount (more than zero).").And.Contain("The date cannot be in the future.");
    }

    [Fact]
    public void ValidateCashAvailable_OnlyCashCannotGoNegative()
    {
        var cash = new FinanceAccount { Name = "Cash In Hand", AccountType = FinanceAccountType.Cash, BalancePaisa = 5000 };
        FinanceRules.ValidateCashAvailable(cash, 5000).Should().BeEmpty();
        FinanceRules.ValidateCashAvailable(cash, 5001).Should().ContainSingle(e => e.Contains("has only ₹50.00"));
        var bank = new FinanceAccount { Name = "SBI", AccountType = FinanceAccountType.Bank, BalancePaisa = 0 };
        FinanceRules.ValidateCashAvailable(bank, 999999).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData("Wrong entry", true)]
    public void ValidateReason_RequiresText(string? reason, bool ok) =>
        FinanceRules.ValidateReason(reason, "expense").Should().HaveCount(ok ? 0 : 1);

    [Fact]
    public void ValidateCategoryName_RequiredAndShort()
    {
        FinanceRules.ValidateCategoryName("Courier").Should().BeEmpty();
        FinanceRules.ValidateCategoryName(" ").Should().ContainSingle();
        FinanceRules.ValidateCategoryName(new string('x', 41)).Should().ContainSingle();
    }

    [Theory]
    [InlineData(12.345, 1235)]
    [InlineData(0.005, 1)]
    [InlineData(1180, 118000)]
    public void ToPaisa_RoundsOnceHalfAwayFromZero(decimal rupees, long paisa) => FinanceRules.ToPaisa(rupees).Should().Be(paisa);

    [Fact]
    public void Labels_AndLegacyMethodMapping()
    {
        FinancePaymentMode.Cheque.ToPaymentMethod().Should().Be(PaymentMethod.BankTransfer);
        FinancePaymentMode.Upi.ToPaymentMethod().Should().Be(PaymentMethod.UPI);
        FinanceAccountType.UpiWallet.DefaultMode().Should().Be(FinancePaymentMode.Upi);
        FinanceAccountType.Bank.ToPaymentMethod().Should().Be(PaymentMethod.BankTransfer);
        ChequeStatus.Bounced.ToLabel().Should().Be("Bounced");
        new FinanceAccount { Name = "HDFC", AccountType = FinanceAccountType.Bank, AccountNumber = "50100123456789" }.DisplayName.Should().Be("HDFC (…6789)");
    }

    // ───────────────────────── CSV ─────────────────────────

    [Theory]
    [InlineData("=HYPERLINK(1)", "'=HYPERLINK(1)")]
    [InlineData("+91 98765", "'+91 98765")]
    [InlineData("-25.00", "-25.00")]
    [InlineData("A, B", "\"A, B\"")]
    [InlineData("x,\"y\"", "\"x,\"\"y\"\"\"")]
    public void Cell_NeutralisesFormulasAndQuotes(string input, string expected) => FinanceCsv.Cell(input).Should().Be(expected);

    [Fact]
    public void AccountStatementCsv_HasOpeningLinesAndClosing()
    {
        var statement = new AccountStatement
        {
            Account = new FinanceAccount { Name = "Cash In Hand", AccountType = FinanceAccountType.Cash },
            From = Today.AddDays(-1), To = Today, OpeningBalancePaisa = 1000,
            Lines = FinanceRules.WithRunningBalance(1000, new[]
            {
                new AccountStatementLine { Date = Today, Source = AccountEntrySource.SalePayment, Reference = "INV-1", InPaisa = 25100, SortKey = 1 }
            })
        };

        var csv = FinanceCsv.AccountStatement(statement);

        csv.Should().Contain("Opening balance").And.Contain("05-11-2026,Sale,INV-1,,,251.00,,261.00").And.Contain("Closing balance");
    }

    [Fact]
    public void PartyBalancesCsv_ListsAgeingColumns()
    {
        var csv = FinanceCsv.PartyBalances(new[]
        {
            new PartyBalanceRow { Name = "Ramesh", Ageing = new AgeingBuckets { Days31To60Paisa = 5000 } }
        }, Today);

        csv.Should().Contain("31-60 days").And.Contain("Ramesh,,,0.00,50.00,0.00,0.00,0.00,50.00");
    }
}
