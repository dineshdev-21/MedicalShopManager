using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Modules.Finance.Printing;
using MedicalShopManager.App.Modules.Finance.ViewModels;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Modules.Finance;
using MedicalShopManager.Infrastructure.Modules.Finance;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Finance (6.2): expenses, cash &amp; bank accounts with statements, receipts from customers / payments to suppliers,
/// cheque management and party ledgers with receivable / payable ageing. Migration number 16 (overflow 160–169).
/// Code: Core/Modules/Finance, Infrastructure/Modules/Finance, App/Modules/Finance. Ledgers reference parties as
/// (PartyType, PartyId). Other modules open the pages with the keys and requests in <see cref="FinancePages"/>, e.g.
/// <c>NavigateTo(FinancePages.ReceivePayment, new OpenPartyPaymentRequest(PartyType.Customer, customerId))</c>.
/// No window-level shortcuts (none are assigned to Finance); every key is page-level.
/// </summary>
public static class FinanceModule
{
    public static IServiceCollection AddFinanceModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        // ─── Pages ───
        services.AddPage<FinanceCashBankViewModel>(navigation, FinancePages.CashBank, "Cash & Bank", NavGroup.Finance, "", 10);
        services.AddPage<FinanceReceivePaymentViewModel>(navigation, FinancePages.ReceivePayment, "Receive Payment", NavGroup.Finance, "", 20);
        services.AddPage<FinancePaySupplierViewModel>(navigation, FinancePages.PaySupplier, "Pay Supplier", NavGroup.Finance, "", 30);
        services.AddPage<FinanceVouchersViewModel>(navigation, FinancePages.Vouchers, "Payments In / Out", NavGroup.Finance, "", 40);
        services.AddPage<FinanceChequesViewModel>(navigation, FinancePages.Cheques, "Cheques", NavGroup.Finance, "", 50);
        services.AddPage<FinancePartyLedgersViewModel>(navigation, FinancePages.PartyLedgers, "Party Ledgers", NavGroup.Finance, "", 60);
        services.AddPage<ExpensesViewModel>(navigation, FinancePages.Expenses, "Expenses", NavGroup.Finance, "", 70);
        services.AddPage<FinanceAccountStatementViewModel>(navigation, FinancePages.AccountStatement, "Account Statement", NavGroup.Finance, "", 80, showInSidebar: false);

        // ─── Data and services ───
        services.AddSingleton<IFinanceAccountRepository, FinanceAccountRepository>();
        services.AddSingleton<IFinancePartyRepository, FinancePartyRepository>();
        services.AddSingleton<IFinanceVoucherRepository, FinanceVoucherRepository>();
        services.AddSingleton<IFinanceChequeRepository, FinanceChequeRepository>();
        services.AddSingleton<IFinanceExpenseRepository, FinanceExpenseRepository>();
        services.AddSingleton<ICashBankService, CashBankService>();
        services.AddSingleton<IPartyPaymentService, PartyPaymentService>();
        services.AddSingleton<IChequeService, ChequeService>();
        services.AddSingleton<IExpenseService, ExpenseService>();
        services.AddSingleton<IFinanceFileDialog, FinanceFileDialog>();

        // ─── Print templates ───
        services.AddSingleton<IDocumentTemplate<FinanceVoucher>, FinanceVoucherTemplate>();
        services.AddSingleton<IDocumentTemplate<AccountStatement>, FinanceAccountStatementTemplate>();
        services.AddSingleton<IDocumentTemplate<PartyLedger>, FinancePartyLedgerTemplate>();
        return services;
    }
}
