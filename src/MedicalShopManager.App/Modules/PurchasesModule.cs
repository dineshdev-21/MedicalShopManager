using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MedicalShopManager.App.Modules.Purchases.Printing;
using MedicalShopManager.App.Modules.Purchases.ViewModels;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Modules.Purchases;
using MedicalShopManager.Infrastructure.Modules.Purchases;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Purchases (6.2): purchase entry per pack (draft / confirm with transactional batch create-or-merge, edit and delete
/// with stock reversal, payments), distributors with quick-add and statements, purchase returns and expiry returns
/// (debit notes), CSV / Excel bill import with saved column mapping, and the purchase register / voucher / debit note /
/// statement print templates. Migration 12 (overflow 120–129). Code: Core/Modules/Purchases,
/// Infrastructure/Modules/Purchases, App/Modules/Purchases. Other modules open its screens with
/// <c>NavigateTo(PurchasePages.Entry, new OpenPurchaseRequest(id))</c> etc. (Core/Modules/Purchases/PurchaseNavigation.cs).
/// </summary>
public static class PurchasesModule
{
    public static IServiceCollection AddPurchasesModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        // ─── Pages ───
        services.AddPage<PurchaseEntryViewModel>(navigation, PurchasePages.Entry, "Add Purchase", NavGroup.Purchases, "", 10, keyGesture: "Ctrl+Alt+P");
        services.AddPage<PurchasesViewModel>(navigation, PurchasePages.List, "View Purchases", NavGroup.Purchases, "", 20);
        services.AddPage<PurchaseImportViewModel>(navigation, PurchasePages.Import, "Import Purchase (CSV)", NavGroup.Purchases, "", 30);
        services.AddPage<PurchaseReturnEntryViewModel>(navigation, PurchasePages.Return, "Return Purchase", NavGroup.Purchases, "", 40, keyGesture: "Ctrl+Alt+R");
        services.AddPage<PurchaseExpiryReturnViewModel>(navigation, PurchasePages.ExpiryReturn, "Return Expired Products", NavGroup.Purchases, "", 50);
        services.AddPage<SuppliersViewModel>(navigation, PurchasePages.Suppliers, "Distributors", NavGroup.Purchases, "", 60, keyGesture: "Ctrl+Alt+D");
        services.AddPage<PurchaseSupplierStatementViewModel>(navigation, PurchasePages.SupplierStatement, "Distributor Statement", NavGroup.Purchases, "", 70);
        services.AddPage<PurchaseDebitNotesViewModel>(navigation, PurchasePages.DebitNotes, "Purchase Returns", NavGroup.Invoices, "", 40);

        // ─── Data and services ───
        services.AddSingleton<IDistributorRepository, DistributorRepository>();
        services.AddSingleton<IPurchaseBillRepository, PurchaseBillRepository>();
        services.AddSingleton<IPurchaseReturnRepository, PurchaseReturnRepository>();
        services.AddSingleton<IPurchaseEntryService, PurchaseEntryService>();
        services.AddSingleton<IPurchaseReturnService, PurchaseReturnService>();
        services.AddSingleton<IDistributorService, DistributorService>();
        services.AddSingleton<IPurchaseImportService, PurchaseImportService>();

        // The pre-6.2 IPurchaseService (owned by Purchases) now posts per pack through the module's transactional
        // entry service: batches carry the item's real UnitsPerPack instead of the interim pack of 1.
        services.Replace(ServiceDescriptor.Singleton<IPurchaseService, PurchaseServiceAdapter>());

        // ─── Print templates ───
        services.AddSingleton<IDocumentTemplate<PurchaseBill>, PurchaseVoucherTemplate>();
        services.AddSingleton<IDocumentTemplate<PurchaseRegisterReport>, PurchaseRegisterTemplate>();
        services.AddSingleton<IDocumentTemplate<PurchaseReturnDocument>, DebitNoteTemplate>();
        services.AddSingleton<IDocumentTemplate<SupplierStatement>, SupplierStatementTemplate>();
        return services;
    }
}
