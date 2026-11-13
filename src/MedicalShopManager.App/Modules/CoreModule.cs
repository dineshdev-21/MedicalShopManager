using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.Services;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Numbering;
using MedicalShopManager.Infrastructure.Repositories;
using MedicalShopManager.Infrastructure.Services;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Shared kernel: database, every pre-6.2 repository and service, numbering, printing, navigation, and the
/// System pages (Backup, Excel). FROZEN for module agents — a module that needs a different implementation of
/// one of these services calls <c>services.Replace(...)</c> in its own AddXxxModule (modules run after this one).
/// </summary>
public static class CoreModule
{
    public static IServiceCollection AddCoreModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        // ─── Database ───
        services.AddSingleton<DatabaseManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DatabaseManager>>();
            return new DatabaseManager(null, logger);
        });

        // ─── Repositories ───
        services.AddSingleton<IMedicineRepository, MedicineRepository>();
        services.AddSingleton<IBatchRepository, BatchRepository>();
        services.AddSingleton<ISaleRepository, SaleRepository>();
        services.AddSingleton<ISaleItemRepository, SaleItemRepository>();
        services.AddSingleton<IPurchaseRepository, PurchaseRepository>();
        services.AddSingleton<IPurchaseItemRepository, PurchaseItemRepository>();
        services.AddSingleton<ISupplierRepository, SupplierRepository>();
        services.AddSingleton<ICustomerRepository, CustomerRepository>();
        services.AddSingleton<IExpenseRepository, ExpenseRepository>();
        services.AddSingleton<IReturnRepository, ReturnRepository>();
        services.AddSingleton<IStockMovementRepository, StockMovementRepository>();
        services.AddSingleton<IPaymentRepository, PaymentRepository>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IAuditLogRepository, AuditLogRepository>();
        services.AddSingleton<IAppSettingsRepository, AppSettingsRepository>();
        services.AddSingleton<IExcelSyncQueueRepository, ExcelSyncQueueRepository>();

        // ─── Core Services ───
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IAuditService, AuditService>();
        services.AddSingleton<IMedicineService, MedicineService>();
        services.AddSingleton<IInventoryService, InventoryService>();
        services.AddSingleton<IBillingService, BillingService>();
        services.AddSingleton<IPurchaseService, PurchaseService>();
        services.AddSingleton<IReportService, ReportService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IExcelSyncService, ExcelSyncService>();
        services.AddSingleton<IDocumentNumberService, DocumentNumberService>();

        // ─── Shell services ───
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IPrintService, FlowDocumentPrintService>();

        // ─── System pages ───
        services.AddPage<BackupViewModel>(navigation, "Backup", "Backup & Restore", NavGroup.System, "", 10);
        services.AddPage<ExcelManagementViewModel>(navigation, "ExcelManagement", "Excel Data", NavGroup.System, "", 20);

        return services;
    }
}
