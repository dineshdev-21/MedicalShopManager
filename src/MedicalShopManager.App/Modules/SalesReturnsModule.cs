using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Modules.SalesReturns.Printing;
using MedicalShopManager.App.Modules.SalesReturns.ViewModels;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Modules.SalesReturns;
using MedicalShopManager.Infrastructure.Modules.SalesReturns;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Sales register + sale returns + credit notes (6.2). Migration number 11 (overflow 110–119).
/// Code: Core/Modules/SalesReturns, Infrastructure/Modules/SalesReturns, App/Modules/SalesReturns.
/// Pages: Invoices ▸ Sales (register), Sale Returns, Credit Notes; Sales ▸ Sale Return (Ctrl+Alt+T).
/// Other modules open them with the keys and requests in <see cref="SalesReturnsPages"/> (Core), e.g.
/// <c>NavigateTo(SalesReturnsPages.Entry, new StartSaleReturnRequest(saleId))</c>.
/// </summary>
public static class SalesReturnsModule
{
    public static IServiceCollection AddSalesReturnsModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        // ─── Pages ───
        services.AddPage<SalesHistoryViewModel>(navigation, SalesReturnsPages.SalesRegister, "Sales", NavGroup.Invoices, "\ue9f9", 10);
        services.AddPage<SaleReturnsViewModel>(navigation, SalesReturnsPages.Register, "Sale Returns", NavGroup.Invoices, "\ue81c", 20);
        services.AddPage<SaleCreditNotesViewModel>(navigation, SalesReturnsPages.CreditNotes, "Credit Notes", NavGroup.Invoices, "\ue7c3", 25);
        services.AddPage<SaleReturnEntryViewModel>(navigation, SalesReturnsPages.Entry, "Sale Return", NavGroup.Sales, "\ue7a7", 20,
            keyGesture: "Ctrl+Alt+T");

        // ─── Data and services ───
        services.AddSingleton<ISalesRegisterRepository, SalesRegisterRepository>();
        services.AddSingleton<ISaleReturnRepository, SaleReturnRepository>();
        services.AddSingleton<ISalesRegisterService, SalesRegisterService>();
        services.AddSingleton<ISaleReturnService, SaleReturnService>();

        // ─── Print templates ───
        services.AddSingleton<IDocumentTemplate<SaleReturnDocument>, CreditNoteTemplate>();
        return services;
    }
}
