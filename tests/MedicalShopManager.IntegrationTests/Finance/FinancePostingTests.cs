using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Infrastructure.Database.Migrations;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Finance;

/// <summary>Receipts, supplier payments, cheques and their rollbacks against a real SQLite file.</summary>
public class FinancePostingTests : FinanceDbTestBase
{
    [Fact]
    public async Task Migration16_CreatesTablesAndSeeds()
    {
        MigrationCatalog.Discover().Select(m => m.Version).Should().Contain(16);
        foreach (var table in new[] { "FinanceAccounts", "FinanceMethodAccounts", "FinanceMovements", "FinanceCheques", "FinanceVouchers",
                     "FinanceVoucherAllocations", "FinanceExpenseCategories" })
            (await LongAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @N", ("@N", table))).Should().Be(1, table);

        var cash = await Accounts().GetAsync(CashAccountId);
        cash!.AccountType.Should().Be(FinanceAccountType.Cash);
        (await Accounts().GetMethodAccountsAsync()).Select(m => m.Method).Should().NotContain(PaymentMethod.Credit)
            .And.Contain(new[] { PaymentMethod.Cash, PaymentMethod.UPI, PaymentMethod.Card, PaymentMethod.BankTransfer });
        (await Expenses().ListCategoriesAsync()).Should().HaveCount(16);
        (await LongAsync("SELECT COUNT(*) FROM pragma_table_info('Payments') WHERE name IN ('AccountId','VoucherId','ChequeId','PartyType','PartyId')"))
            .Should().Be(5);
    }

    [Fact]
    public async Task Receipt_SettlesOpeningBalanceThenOldestBills_UpdatesBillsPaymentsAndCash()
    {
        var customer = await SeedCustomerAsync(openingPaisa: 10000);
        var older = await SeedSaleAsync(customer, 25100, 0, Today.AddDays(-40), "INV-1");
        var newer = await SeedSaleAsync(customer, 50000, 10000, Today.AddDays(-5), "INV-2");

        var voucher = await PaymentService().PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = customer, Date = Today, AmountPaisa = 50000,
            Mode = FinancePaymentMode.Cash, AccountId = CashAccountId
        });

        voucher.VoucherNumber.Should().StartWith("RCT/");
        voucher.Allocations.Should().Equal(
            new VoucherAllocation(AllocationTarget.OpeningBalance, 0, "Opening balance", 10000),
            new VoucherAllocation(AllocationTarget.Sale, older, "INV-1", 25100),
            new VoucherAllocation(AllocationTarget.Sale, newer, "INV-2", 14900));
        (await SaleBalanceAsync(older)).Should().Be(0);
        (await SaleBalanceAsync(newer)).Should().Be(25100);
        (await LongAsync("SELECT PaymentStatus FROM Sales WHERE Id = @Id", ("@Id", older))).Should().Be((int)PaymentStatus.Paid);
        (await LongAsync("SELECT COUNT(*) FROM Payments WHERE VoucherId = @V", ("@V", voucher.Id))).Should().Be(2);

        // Cash book: ₹100 counter payment + ₹500 receipt (the voucher's Payments rows are allocation records, not counted twice).
        (await CashBalanceAsync()).Should().Be(10000 + 50000);
        var dues = await Parties().GetDuesAsync(PartyType.Customer, customer);
        dues.Should().ContainSingle().Which.BalancePaisa.Should().Be(25100);
        (await Parties().GetAsync(PartyType.Customer, customer))!.NetDuePaisa.Should().Be(25100);
        (await AuditCountAsync("FinanceReceiptPosted")).Should().Be(1);
    }

    [Fact]
    public async Task Receipt_MoreThanDue_LeavesAnAdvanceThatReducesTheNetDue()
    {
        var customer = await SeedCustomerAsync();
        await SeedSaleAsync(customer, 25100, 0, Today.AddDays(-3), "INV-1");

        var voucher = await PaymentService().PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = customer, Date = Today, AmountPaisa = 30000,
            Mode = FinancePaymentMode.Upi, AccountId = CashAccountId
        });

        voucher.OnAccountPaisa.Should().Be(4900);
        var row = (await Parties().GetBalancesAsync(PartyType.Customer, Today)).Single(r => r.PartyId == customer);
        row.Ageing.TotalPaisa.Should().Be(0);
        row.CreditsPaisa.Should().Be(4900);
        row.NetDuePaisa.Should().Be(-4900);
    }

    [Fact]
    public async Task Receipt_StaleExplicitAllocation_RollsBackEverything_NumberNotConsumed()
    {
        var customer = await SeedCustomerAsync();
        var sale = await SeedSaleAsync(customer, 25100, 0, Today.AddDays(-3), "INV-1");
        var before = await Numbers().PeekNextAsync(DocumentType.Receipt, Today);

        var act = () => PaymentService().PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = customer, Date = Today, AmountPaisa = 30000,
            Mode = FinancePaymentMode.Cash, AccountId = CashAccountId,
            Allocations = new[] { new VoucherAllocation(AllocationTarget.Sale, sale, "INV-1", 26000) }
        });

        await act.Should().ThrowAsync<FinanceValidationException>().WithMessage("*more than its balance due*");
        (await Numbers().PeekNextAsync(DocumentType.Receipt, Today)).Should().Be(before);
        (await LongAsync("SELECT COUNT(*) FROM FinanceVouchers")).Should().Be(0);
        (await LongAsync("SELECT COUNT(*) FROM Payments")).Should().Be(0);
        (await SaleBalanceAsync(sale)).Should().Be(25100);
    }

    [Fact]
    public async Task Receipt_BillPaidElsewhereDuringPosting_FailsWholeVoucher()
    {
        var customer = await SeedCustomerAsync();
        var first = await SeedSaleAsync(customer, 10000, 0, Today.AddDays(-9), "INV-1");
        var second = await SeedSaleAsync(customer, 10000, 0, Today.AddDays(-3), "INV-2");
        // Another screen settles INV-2 after the dues were shown: the voucher's allocation to it is now stale.
        await ScalarAsync("UPDATE Sales SET BalanceDuePaisa = 0, AmountPaidPaisa = 10000 WHERE Id = @Id", ("@Id", second));

        var act = () => Vouchers().PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = customer, Date = Today, AmountPaisa = 20000,
            Mode = FinancePaymentMode.Cash, AccountId = CashAccountId,
            Allocations = new[]
            {
                new VoucherAllocation(AllocationTarget.Sale, first, "INV-1", 10000),
                new VoucherAllocation(AllocationTarget.Sale, second, "INV-2", 10000)
            }
        });

        await act.Should().ThrowAsync<FinanceValidationException>();
        (await SaleBalanceAsync(first)).Should().Be(10000, "the first bill's settlement rolled back with the failed voucher");
        (await LongAsync("SELECT COUNT(*) FROM FinanceVoucherAllocations")).Should().Be(0);
    }

    [Fact]
    public async Task CancelReceipt_ReopensBillsAndTakesMoneyOutOfTheAccount()
    {
        var customer = await SeedCustomerAsync();
        var sale = await SeedSaleAsync(customer, 25100, 0, Today.AddDays(-3), "INV-1");
        var service = PaymentService();
        var voucher = await service.PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = customer, Date = Today, AmountPaisa = 25100,
            Mode = FinancePaymentMode.Cash, AccountId = CashAccountId
        });

        await service.CancelVoucherAsync(voucher.Id, "Entered against the wrong customer", null);

        (await SaleBalanceAsync(sale)).Should().Be(25100);
        (await LongAsync("SELECT PaymentStatus FROM Sales WHERE Id = @Id", ("@Id", sale))).Should().Be((int)PaymentStatus.Unpaid);
        (await LongAsync("SELECT COUNT(*) FROM Payments WHERE VoucherId = @V", ("@V", voucher.Id))).Should().Be(0);
        (await CashBalanceAsync()).Should().Be(0);
        (await service.GetVoucherAsync(voucher.Id))!.Status.Should().Be(FinanceVoucherStatus.Cancelled);
        await service.Invoking(s => s.CancelVoucherAsync(voucher.Id, "again", null)).Should().ThrowAsync<FinanceValidationException>();
    }

    [Fact]
    public async Task ChequeReceipt_MovesMoneyOnlyWhenCleared_BounceReversesTheReceipt()
    {
        var customer = await SeedCustomerAsync();
        var sale = await SeedSaleAsync(customer, 25100, 0, Today.AddDays(-10), "INV-1");
        var bank = await CashBankService().SaveAccountAsync(new FinanceAccount
        {
            Name = "SBI Current", AccountType = FinanceAccountType.Bank, AccountNumber = "30012345678", Ifsc = "SBIN0001234", OpeningDate = Today.AddDays(-30)
        }, null);

        var voucher = await PaymentService().PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = customer, Date = Today.AddDays(-2), AmountPaisa = 25100,
            Mode = FinancePaymentMode.Cheque, Cheque = new ChequeDetails("004512", Today.AddDays(-2), "HDFC Bank")
        });

        (await SaleBalanceAsync(sale)).Should().Be(0, "the bill is settled when the cheque is received");
        var cheque = (await Cheques().ListAsync(new ChequeFilter { OpenOnly = true })).Single();
        cheque.Status.Should().Be(ChequeStatus.Pending);
        cheque.VoucherNumber.Should().Be(voucher.VoucherNumber);

        await ChequeService().ChangeStatusAsync(new ChequeStatusChange { ChequeId = cheque.Id, NewStatus = ChequeStatus.Deposited, Date = Today.AddDays(-1), AccountId = bank.Id });
        (await Accounts().ListAsync(Today)).Single(a => a.Id == bank.Id).BalancePaisa.Should().Be(0, "a deposited cheque has not cleared");

        await ChequeService().ChangeStatusAsync(new ChequeStatusChange { ChequeId = cheque.Id, NewStatus = ChequeStatus.Bounced, Date = Today, Note = "Insufficient funds" });

        (await SaleBalanceAsync(sale)).Should().Be(25100);
        (await PaymentService().GetVoucherAsync(voucher.Id))!.Status.Should().Be(FinanceVoucherStatus.Bounced);
        (await Accounts().ListAsync(Today)).Single(a => a.Id == bank.Id).BalancePaisa.Should().Be(0);
        var ledger = await Parties().GetLedgerAsync(PartyType.Customer, customer, Today.AddDays(-30), Today);
        ledger.Lines.Select(l => l.Source).Should().Contain(new[] { PartyEntrySource.Sale, PartyEntrySource.Receipt, PartyEntrySource.ChequeBounced });
        ledger.ClosingBalancePaisa.Should().Be(25100);
        (await AuditCountAsync("FinanceChequeBounced")).Should().Be(1);
    }

    [Fact]
    public async Task ChequeReceipt_ClearedChequeEntersTheBankOnTheClearedDate_AndCannotBeCancelled()
    {
        var customer = await SeedCustomerAsync();
        await SeedSaleAsync(customer, 25100, 0, Today.AddDays(-10), "INV-1");
        var bank = await CashBankService().SaveAccountAsync(new FinanceAccount
        {
            Name = "SBI Current", AccountType = FinanceAccountType.Bank, OpeningBalancePaisa = 100000, OpeningDate = Today.AddDays(-30)
        }, null);
        var voucher = await PaymentService().PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = customer, Date = Today.AddDays(-3), AmountPaisa = 25100,
            Mode = FinancePaymentMode.Cheque, Cheque = new ChequeDetails("004513", Today.AddDays(-3), null)
        });
        var cheque = (await Cheques().ListAsync(new ChequeFilter { OpenOnly = true })).Single();

        await ChequeService().ChangeStatusAsync(new ChequeStatusChange { ChequeId = cheque.Id, NewStatus = ChequeStatus.Cleared, Date = Today.AddDays(-1), AccountId = bank.Id });

        var statement = await CashBankService().GetStatementAsync(bank.Id, Today.AddDays(-2), Today);
        statement.OpeningBalancePaisa.Should().Be(100000);
        statement.Lines.Should().ContainSingle().Which.Should().Match<AccountStatementLine>(l =>
            l.Date == Today.AddDays(-1) && l.InPaisa == 25100 && l.BalancePaisa == 125100 && l.Reference == voucher.VoucherNumber);
        await PaymentService().Invoking(s => s.CancelVoucherAsync(voucher.Id, "x", null))
            .Should().ThrowAsync<FinanceValidationException>().WithMessage("*already cleared*");
        await Accounts().Invoking(a => a.SetActiveAsync(bank.Id, false)).Should().NotThrowAsync("the bank has no open cheques now");
    }

    [Fact]
    public async Task SupplierPayment_SettlesOldestPurchase_PayablesAgeing()
    {
        var supplier = await SeedSupplierAsync(openingPaisa: 0);
        var old = await SeedPurchaseAsync(supplier, 100000, Today.AddDays(-75), "A-100");
        var mid = await SeedPurchaseAsync(supplier, 40000, Today.AddDays(-45), "A-140");
        var fresh = await SeedPurchaseAsync(supplier, 20000, Today.AddDays(-2), "A-190");
        var bank = await CashBankService().SaveAccountAsync(new FinanceAccount { Name = "HDFC", AccountType = FinanceAccountType.Bank, OpeningDate = Today.AddDays(-90) }, null);

        var before = (await Parties().GetBalancesAsync(PartyType.Supplier, Today)).Single();
        before.Ageing.Should().BeEquivalentTo(new { Days0To30Paisa = 20000L, Days31To60Paisa = 40000L, Days61To90Paisa = 100000L, Over90Paisa = 0L });
        before.SignedBalancePaisa.Should().Be(-160000);

        var voucher = await PaymentService().PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Payment, PartyType = PartyType.Supplier, PartyId = supplier, Date = Today, AmountPaisa = 120000,
            Mode = FinancePaymentMode.BankTransfer, AccountId = bank.Id, Reference = "NEFT 8812"
        });

        voucher.VoucherNumber.Should().NotBeNullOrEmpty();
        (await PurchaseBalanceAsync(old)).Should().Be(0);
        (await PurchaseBalanceAsync(mid)).Should().Be(20000);
        (await PurchaseBalanceAsync(fresh)).Should().Be(20000);
        (await Accounts().ListAsync(Today)).Single(a => a.Id == bank.Id).BalancePaisa.Should().Be(-120000, "a bank account may be overdrawn");
        var after = (await Parties().GetBalancesAsync(PartyType.Supplier, Today)).Single();
        after.NetDuePaisa.Should().Be(40000);

        var ledger = await Parties().GetLedgerAsync(PartyType.Supplier, supplier, Today.AddDays(-100), Today);
        ledger.ClosingBalancePaisa.Should().Be(-40000);
        ledger.Lines.Last().Source.Should().Be(PartyEntrySource.PaymentVoucher);
    }

    [Fact]
    public async Task SupplierPaymentFromCash_CannotExceedCashInHand()
    {
        var supplier = await SeedSupplierAsync();
        await SeedPurchaseAsync(supplier, 100000, Today.AddDays(-5), "A-1");

        await PaymentService().Invoking(s => s.PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Payment, PartyType = PartyType.Supplier, PartyId = supplier, Date = Today, AmountPaisa = 100000,
            Mode = FinancePaymentMode.Cash, AccountId = CashAccountId
        })).Should().ThrowAsync<FinanceValidationException>().WithMessage("*has only*");
    }

    [Fact]
    public async Task SaleEditedByBillingAfterReceipt_ReceiptBecomesAnAdvance()
    {
        var customer = await SeedCustomerAsync();
        var sale = await SeedSaleAsync(customer, 25100, 0, Today.AddDays(-3), "INV-1");
        await PaymentService().PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = customer, Date = Today, AmountPaisa = 25100,
            Mode = FinancePaymentMode.Cash, AccountId = CashAccountId
        });

        // Billing's cancel deletes the sale's Payments rows (including the receipt's allocation row) and closes the bill.
        await ScalarAsync("DELETE FROM Payments WHERE TransactionId = @S AND PaymentType = 0; UPDATE Sales SET Status = 2, BalanceDuePaisa = 0 WHERE Id = @S",
            ("@S", sale));

        var row = (await Parties().GetBalancesAsync(PartyType.Customer, Today)).Single(r => r.PartyId == customer);
        row.CreditsPaisa.Should().Be(25100, "the money received is still the customer's");
        (await CashBalanceAsync()).Should().Be(25100);
    }
}
