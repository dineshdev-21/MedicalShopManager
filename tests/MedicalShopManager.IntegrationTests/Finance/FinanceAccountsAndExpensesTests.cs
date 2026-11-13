using FluentAssertions;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;
using Xunit;

namespace MedicalShopManager.IntegrationTests.Finance;

/// <summary>Cash &amp; bank accounts, transfers, account statements, expenses and party ledgers against a real SQLite file.</summary>
public class FinanceAccountsAndExpensesTests : FinanceDbTestBase
{
    private async Task<FinanceAccount> BankAsync(string name = "SBI Current", long opening = 0, DateTime? openingDate = null) =>
        await CashBankService().SaveAccountAsync(new FinanceAccount
        {
            Name = name, AccountType = FinanceAccountType.Bank, OpeningBalancePaisa = opening, OpeningDate = openingDate ?? Today.AddDays(-60)
        }, null);

    [Fact]
    public async Task Accounts_BalanceDerivedFromSalesRefundsExpensesAndTransfers()
    {
        await SeedSaleAsync(null, 50000, 50000, Today.AddDays(-2), "INV-1");
        await SeedSaleAsync(null, 30000, 30000, Today.AddDays(-1), "INV-2", PaymentMethod.UPI);
        await SeedSaleAsync(null, 20000, 0, Today.AddDays(-1), "INV-3");   // credit: moves no money
        await ScalarAsync(@"INSERT INTO Payments (TransactionId, PaymentDate, PaymentType, PaymentMethod, AmountPaisa)
                            VALUES (1, @D, @T, 0, 5000)", ("@D", D(Today)), ("@T", (int)PaymentType.CustomerRefund));
        var bank = await BankAsync(opening: 200000);
        var upi = await CashBankService().SaveAccountAsync(new FinanceAccount
        {
            Name = "PhonePe Business", AccountType = FinanceAccountType.UpiWallet, UpiId = "shop@ybl", OpeningDate = Today.AddDays(-60)
        }, null);
        await CashBankService().SetMethodAccountAsync(PaymentMethod.UPI, upi.Id, null);

        (await CashBalanceAsync()).Should().Be(50000 - 5000);
        (await Accounts().ListAsync(Today)).Single(a => a.Id == upi.Id).BalancePaisa.Should().Be(30000, "UPI tenders book to the mapped UPI wallet");

        await CashBankService().RecordMovementAsync(new FinanceMovement
        {
            Kind = FinanceMovementKind.Transfer, FromAccountId = CashAccountId, ToAccountId = bank.Id, AmountPaisa = 40000, MovementDate = Today,
            Reference = "Deposit slip 12"
        }, null);

        var accounts = await CashBankService().GetAccountsAsync();
        accounts.Single(a => a.Id == CashAccountId).BalancePaisa.Should().Be(5000);
        accounts.Single(a => a.Id == bank.Id).BalancePaisa.Should().Be(240000);
        (await Accounts().ListAsync(Today.AddDays(-1))).Single(a => a.Id == CashAccountId).BalancePaisa.Should().Be(50000, "as of yesterday");
    }

    [Fact]
    public async Task Transfer_CancelledTransferNoLongerMovesMoney()
    {
        var bank = await BankAsync(opening: 100000);
        var id = await CashBankService().RecordMovementAsync(new FinanceMovement
        {
            Kind = FinanceMovementKind.Transfer, FromAccountId = bank.Id, ToAccountId = CashAccountId, AmountPaisa = 30000, MovementDate = Today
        }, null);
        (await CashBalanceAsync()).Should().Be(30000);

        await CashBankService().CancelMovementAsync(id, "Wrong amount", null);

        (await CashBalanceAsync()).Should().Be(0);
        (await CashBankService().GetMovementsAsync(Today, Today)).Single().IsCancelled.Should().BeTrue();
        await CashBankService().Invoking(s => s.CancelMovementAsync(id, "again", null)).Should().ThrowAsync<FinanceValidationException>();
    }

    [Fact]
    public async Task Transfer_ToInactiveAccount_RolledBack()
    {
        var bank = await BankAsync();
        await CashBankService().SetAccountActiveAsync(bank.Id, false, null);

        await CashBankService().Invoking(s => s.RecordMovementAsync(new FinanceMovement
        {
            Kind = FinanceMovementKind.AddMoney, ToAccountId = bank.Id, AmountPaisa = 100, MovementDate = Today, Notes = "Capital"
        }, null)).Should().ThrowAsync<FinanceValidationException>().WithMessage("*inactive*");
        (await LongAsync("SELECT COUNT(*) FROM FinanceMovements")).Should().Be(0);
    }

    [Fact]
    public async Task Accounts_MappedAccountCannotBeDeactivated_NamesAreUnique()
    {
        await Accounts().Invoking(a => a.SetActiveAsync(CashAccountId, false)).Should().ThrowAsync<FinanceValidationException>().WithMessage("*Choose another account*");
        await BankAsync("HDFC");
        await CashBankService().Invoking(s => s.SaveAccountAsync(new FinanceAccount { Name = "hdfc", AccountType = FinanceAccountType.Bank, OpeningDate = Today }, null))
            .Should().ThrowAsync<FinanceValidationException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task Statement_OpeningRunningAndClosingBalance()
    {
        var bank = await BankAsync(opening: 100000, openingDate: Today.AddDays(-10));
        var service = CashBankService();
        await service.RecordMovementAsync(new FinanceMovement { Kind = FinanceMovementKind.AddMoney, ToAccountId = bank.Id, AmountPaisa = 50000, MovementDate = Today.AddDays(-5), Notes = "Capital" }, null);
        await service.RecordMovementAsync(new FinanceMovement { Kind = FinanceMovementKind.ReduceMoney, FromAccountId = bank.Id, AmountPaisa = 20000, MovementDate = Today.AddDays(-1), Notes = "Drawings" }, null);
        await ExpenseService().SaveAsync(new ExpenseRequest
        {
            ExpenseDate = Today, CategoryId = 1, AmountPaisa = 10000, AccountId = bank.Id, Mode = FinancePaymentMode.BankTransfer, Description = "Rent"
        });

        var full = await service.GetStatementAsync(bank.Id, Today.AddDays(-30), Today);
        full.OpeningBalancePaisa.Should().Be(0);
        full.Lines.Select(l => l.Source).Should().Equal(AccountEntrySource.OpeningBalance, AccountEntrySource.Adjustment,
            AccountEntrySource.Adjustment, AccountEntrySource.Expense);
        full.Lines.Select(l => l.BalancePaisa).Should().Equal(100000, 150000, 130000, 120000);
        full.ClosingBalancePaisa.Should().Be(120000);

        var recent = await service.GetStatementAsync(bank.Id, Today.AddDays(-2), Today);
        recent.OpeningBalancePaisa.Should().Be(150000);
        recent.ClosingBalancePaisa.Should().Be(120000);
        FinanceCsv.AccountStatement(recent).Should().Contain("Closing balance");
    }

    [Fact]
    public async Task Expense_NumberedInTransaction_GstStored_CancelTakesItOffTheCashBook()
    {
        await SeedSaleAsync(null, 200000, 200000, Today.AddDays(-1), "INV-1");
        var service = ExpenseService();
        var peek = await service.PeekNumberAsync(Today);

        var expense = await service.SaveAsync(new ExpenseRequest
        {
            ExpenseDate = Today, CategoryId = 3, AmountPaisa = 118000, GstRatePercent = 18, AmountIncludesGst = true,
            PayeeName = "MSEDCL", PayeeGstin = "27AAACM1234A1Z5", InvoiceNumber = "EB-0925", AccountId = CashAccountId, Mode = FinancePaymentMode.Cash
        });

        expense.ExpenseNumber.Should().Be(peek);
        expense.CategoryName.Should().Be("Electricity");
        expense.TaxablePaisa.Should().Be(100000);
        (expense.CgstPaisa + expense.SgstPaisa).Should().Be(18000);
        (await LongAsync("SELECT Category FROM Expenses WHERE Id = @Id", ("@Id", expense.Id))).Should().Be((int)ExpenseCategory.Electricity);
        (await CashBalanceAsync()).Should().Be(200000 - 118000);

        var (rows, totals) = await service.GetExpensesAsync(new ExpenseFilter { From = Today, To = Today, WithGst = true });
        rows.Should().ContainSingle();
        totals.TaxPaisa.Should().Be(18000);

        await service.CancelAsync(expense.Id, "Duplicate bill", null);
        (await CashBalanceAsync()).Should().Be(200000);
        (await service.GetExpensesAsync(new ExpenseFilter { From = Today, To = Today })).Rows.Should().BeEmpty();
        (await service.GetExpensesAsync(new ExpenseFilter { From = Today, To = Today, IncludeCancelled = true })).Rows.Single().IsCancelled.Should().BeTrue();
        (await AuditCountAsync("FinanceExpenseCancelled")).Should().Be(1);
    }

    [Fact]
    public async Task Expense_InvalidCategory_RollsBack_NumberNotConsumed()
    {
        var bank = await BankAsync();
        var peek = await Numbers().PeekNextAsync(DocumentType.Expense, Today);

        await Expenses().Invoking(r => r.InsertAsync(new FinanceExpense
        {
            ExpenseDate = Today, CategoryId = 999, AmountPaisa = 100, TaxablePaisa = 100, AccountId = bank.Id, Mode = FinancePaymentMode.BankTransfer
        })).Should().ThrowAsync<FinanceValidationException>();

        (await Numbers().PeekNextAsync(DocumentType.Expense, Today)).Should().Be(peek);
        (await LongAsync("SELECT COUNT(*) FROM Expenses")).Should().Be(0);
    }

    [Fact]
    public async Task Expense_EditUpdatesAmountAndKeepsNumber()
    {
        var bank = await BankAsync();
        var service = ExpenseService();
        var saved = await service.SaveAsync(new ExpenseRequest { ExpenseDate = Today, CategoryId = 1, AmountPaisa = 5000, AccountId = bank.Id, Mode = FinancePaymentMode.BankTransfer });

        var edited = await service.SaveAsync(new ExpenseRequest
        {
            Id = saved.Id, ExpenseDate = Today, CategoryId = 2, AmountPaisa = 7000, AccountId = bank.Id, Mode = FinancePaymentMode.Upi
        });

        edited.ExpenseNumber.Should().Be(saved.ExpenseNumber);
        edited.AmountPaisa.Should().Be(7000);
        edited.CategoryName.Should().Be("Salary");
        (await Accounts().ListAsync(Today)).Single(a => a.Id == bank.Id).BalancePaisa.Should().Be(-7000);
    }

    [Fact]
    public async Task Categories_AddRenameDeactivate()
    {
        var service = ExpenseService();
        var courier = await service.AddCategoryAsync("Courier", null);
        await service.Invoking(s => s.AddCategoryAsync("courier", null)).Should().ThrowAsync<FinanceValidationException>();

        await service.RenameCategoryAsync(courier.Id, "Courier & postage", null);
        await service.SetCategoryActiveAsync(courier.Id, false, null);

        (await service.GetCategoriesAsync()).Should().NotContain(c => c.Id == courier.Id);
        (await service.GetCategoriesAsync(true)).Single(c => c.Id == courier.Id).Name.Should().Be("Courier & postage");
        await service.Invoking(s => s.SaveAsync(new ExpenseRequest { ExpenseDate = Today, CategoryId = courier.Id, AmountPaisa = 1, AccountId = CashAccountId }))
            .Should().ThrowAsync<FinanceValidationException>();
    }

    [Fact]
    public async Task CustomerLedger_OpeningSalesPaidWithBillAndReceipts()
    {
        var customer = await SeedCustomerAsync(openingPaisa: 5000, openingDate: Today.AddDays(-100));
        await SeedSaleAsync(customer, 25100, 10000, Today.AddDays(-20), "INV-1");
        await SeedSaleAsync(customer, 15000, 0, Today.AddDays(-10), "INV-2");
        await PaymentService().PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = customer, Date = Today, AmountPaisa = 20000,
            Mode = FinancePaymentMode.Cash, AccountId = CashAccountId
        });

        var ledger = await PaymentService().GetLedgerAsync(PartyType.Customer, customer, Today.AddDays(-15), Today);

        ledger.OpeningBalancePaisa.Should().Be(5000 + 25100 - 10000);
        ledger.Lines.Select(l => (l.Source, l.DebitPaisa, l.CreditPaisa)).Should().Equal(
            (PartyEntrySource.Sale, 15000L, 0L), (PartyEntrySource.Receipt, 0L, 20000L));
        ledger.ClosingBalancePaisa.Should().Be(15100);
        (await Parties().GetAsync(PartyType.Customer, customer))!.NetDuePaisa.Should().Be(15100, "ledger and dues agree");
        FinanceCsv.PartyLedger(ledger).Should().Contain("₹151.00 Dr");
    }

    [Fact]
    public async Task Receivables_AgeingAndSearch()
    {
        var a = await SeedCustomerAsync("Anita Sharma");
        var b = await SeedCustomerAsync("Bharat Medical Store");
        await SeedSaleAsync(a, 10000, 0, Today.AddDays(-100), "INV-1");
        await SeedSaleAsync(b, 20000, 0, Today.AddDays(-15), "INV-2");
        await SeedCustomerAsync("Settled Customer");

        var rows = await PaymentService().GetBalancesAsync(PartyType.Customer, Today);

        rows.Select(r => r.Name).Should().Equal("Bharat Medical Store", "Anita Sharma");
        rows.Single(r => r.PartyId == a).Ageing.Over90Paisa.Should().Be(10000);
        FinanceRules.Totals(rows).NetDuePaisa.Should().Be(30000);
        (await PaymentService().GetBalancesAsync(PartyType.Customer, Today, includeSettled: true)).Should().HaveCount(3);
        (await PaymentService().SearchPartiesAsync(PartyType.Customer, "bharat")).Should().ContainSingle().Which.NetDuePaisa.Should().Be(20000);
        (await PaymentService().SearchPartiesAsync(PartyType.Customer, "%")).Should().BeEmpty("LIKE wildcards in the search text are literal");
    }

    [Fact]
    public async Task Vouchers_ListFiltersByKindAndSearch()
    {
        var customer = await SeedCustomerAsync();
        await SeedSaleAsync(customer, 25100, 0, Today.AddDays(-3), "INV-1");
        var receipt = await PaymentService().PostAsync(new VoucherRequest
        {
            Kind = FinanceVoucherKind.Receipt, PartyType = PartyType.Customer, PartyId = customer, Date = Today, AmountPaisa = 25100,
            Mode = FinancePaymentMode.Cheque, Cheque = new ChequeDetails("778899", Today, "ICICI")
        });

        (await PaymentService().GetVouchersAsync(new VoucherFilter { From = Today, To = Today, Kind = FinanceVoucherKind.Payment })).Should().BeEmpty();
        var found = await PaymentService().GetVouchersAsync(new VoucherFilter { SearchText = "778899" });
        found.Should().ContainSingle().Which.Should().Match<FinanceVoucher>(v =>
            v.Id == receipt.Id && v.ChequeNumber == "778899" && v.ChequeStatus == ChequeStatus.Pending && v.Allocations.Count == 1);
    }
}
