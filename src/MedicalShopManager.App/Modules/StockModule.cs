using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Modules.Stock.Printing;
using MedicalShopManager.App.Modules.Stock.ViewModels;
using MedicalShopManager.App.Navigation;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Modules.Stock;
using MedicalShopManager.Infrastructure.Modules.Stock;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Stock (6.2): the Stock workspace (stock on hand + item detail tabs), item master, batches &amp; expiry, stock
/// adjustments, opening-stock / item-master CSV import and bulk item settings.
/// Migration number 13 (overflow 130–139). Code: Core/Modules/Stock, Infrastructure/Modules/Stock, App/Modules/Stock.
/// </summary>
public static class StockModule
{
    public static IServiceCollection AddStockModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        // Pages. Alt+I (Items) opens the Stock workspace, which supersedes the old item grid as the primary inventory
        // screen; the item master and batch/expiry pages stay available under their existing keys.
        services.AddPage<StockViewModel>(navigation, "Stock", "Stock / Items", NavGroup.Stock, "", 10, keyGesture: "Alt+I");
        services.AddPage<MedicinesViewModel>(navigation, "Medicines", "Item Master", NavGroup.Stock, "", 20);
        services.AddPage<BatchExpiryViewModel>(navigation, "BatchExpiry", "Batch & Expiry", NavGroup.Stock, "", 30);
        services.AddPage<StockAdjustmentViewModel>(navigation, "StockAdjustments", "Stock Adjustment", NavGroup.Stock, "", 40);
        services.AddPage<StockImportViewModel>(navigation, "StockImport", "Upload Stock (CSV)", NavGroup.Stock, "", 50);
        services.AddPage<StockBulkUpdateViewModel>(navigation, "StockBulkUpdate", "Bulk Update Settings", NavGroup.Stock, "", 60);

        // Dialogs hosted inside the Stock workspace
        services.AddTransient<StockItemEditorViewModel>();
        services.AddTransient<StockNewBatchViewModel>();
        services.AddTransient<StockItemSettingsViewModel>();

        // Data and services
        services.AddSingleton<IStockRepository, StockRepository>();
        services.AddSingleton<IStockAdjustmentRepository, StockAdjustmentRepository>();
        services.AddSingleton<IStockService, StockService>();
        services.AddSingleton<IStockImportService, StockImportService>();

        // Print templates
        services.AddSingleton<IDocumentTemplate<StockAdjustment>, StockAdjustmentTemplate>();
        services.AddSingleton<IDocumentTemplate<StockStatement>, StockStatementTemplate>();
        return services;
    }
}
