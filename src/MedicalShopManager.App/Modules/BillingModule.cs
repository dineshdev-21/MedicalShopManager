using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Modules.Billing.Printing;
using MedicalShopManager.App.Modules.Billing.ViewModels;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Infrastructure.Modules.Billing;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Billing (6.2): POS / new bill (per pack + loose, FEFO batches, H1/X capture, drafts, payment wizard, view / edit /
/// delete / replicate / print), customers, unsaved drafts (F8), add customer (Ctrl+Alt+C), sale invoice printing.
/// Migration number 10 (overflow 100–109). Code: Core/Modules/Billing, Infrastructure/Modules/Billing, App/Modules/Billing.
/// Other modules open sales with <c>NavigateTo(BillingModule.NewBillKey, new OpenSaleRequest(id))</c> (or
/// <c>OpenSaleDraftRequest</c> / <c>ReplicateSaleRequest</c>, Core/Modules/Billing).
/// </summary>
public static class BillingModule
{
    public const string NewBillKey = "NewBill";
    public const string CustomersKey = "Customers";
    public const string DraftsKey = "SaleDrafts";
    public const string AddCustomerKey = "AddCustomer";

    public static IServiceCollection AddBillingModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        // ─── Pages ───
        services.AddPage<BillingViewModel>(navigation, NewBillKey, "New Bill / POS", NavGroup.Sales, "", 10, keyGesture: "Ctrl+Alt+S");
        services.AddPage<CustomersViewModel>(navigation, CustomersKey, "Customers", NavGroup.Sales, "", 30);
        services.AddPage<BillingDraftsViewModel>(navigation, DraftsKey, "Unsaved Drafts", NavGroup.Sales, "", 15, keyGesture: "F8");
        services.AddPage<BillingAddCustomerViewModel>(navigation, AddCustomerKey, "Add Customer", NavGroup.Sales, "", 35,
            keyGesture: "Ctrl+Alt+C", showInSidebar: false);

        // ─── Dialog / child ViewModels ───
        services.AddTransient<BillingCustomerEditorViewModel>();
        services.AddTransient<BillingCustomerPickerViewModel>();

        // ─── Data and services ───
        services.AddSingleton<IBillingSaleRepository, BillingSaleRepository>();
        services.AddSingleton<ISaleCatalogRepository, SaleCatalogRepository>();
        services.AddSingleton<IBillingCustomerRepository, BillingCustomerRepository>();
        services.AddSingleton<ISaleDraftRepository, SaleDraftRepository>();
        services.AddSingleton<ICustomChargeRepository, CustomChargeRepository>();
        services.AddSingleton<ISaleBillingService, SaleBillingService>();
        services.AddSingleton<ISaleDraftService, SaleDraftService>();
        services.AddSingleton<IBillingCustomerService, BillingCustomerService>();
        services.AddSingleton<IPrescriptionFileStore, PrescriptionFileStore>();

        // ─── Print templates (Sale first: Control Center's Print Layout previews the IDocumentTemplate<Sale> entry) ───
        services.AddSingleton<IDocumentTemplate<Sale>, SaleInvoiceTemplate>();
        services.AddSingleton<IDocumentTemplate<SaleInvoicePrintModel>, BillingInvoiceTemplate>();
        return services;
    }
}
