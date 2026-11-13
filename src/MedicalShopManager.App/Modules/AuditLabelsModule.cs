using Microsoft.Extensions.DependencyInjection;
using MedicalShopManager.App.Navigation;

namespace MedicalShopManager.App.Modules;

/// <summary>
/// Stock audit + barcode label printing (6.2). Migration number 14 (overflow 140–149). Nav group: Stock.
/// Not built yet: the owning module agent fills this in (pages via services.AddPage, services, templates).
/// </summary>
public static class AuditLabelsModule
{
    public static IServiceCollection AddAuditLabelsModule(this IServiceCollection services, INavigationRegistry navigation)
    {
        return services;
    }
}
